// -----------------------------------------------------------------------
// <copyright file="BashLoopAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Preserves bounded loop-variable proofs while the Bash structural parser
/// still owns lexer provenance. Compatibility leaves deliberately retain
/// their authored dynamic values; only occurrence facts receive these
/// effective domains.
/// </summary>
internal sealed class BashLoopBindingContext
{
    private readonly List<BindingFrame> _bindings = new();

    internal int Count => _bindings.Count;

    internal bool Contains(string name) => FindExactBinding(name) is not null;

    internal void Push(string name, ShellValueDomain domain) =>
        _bindings.Add(new BindingFrame(name, domain));

    internal void Pop()
    {
        if (_bindings.Count > 0)
        {
            _bindings.RemoveAt(_bindings.Count - 1);
        }
    }

    internal BashLoopBindingContext Clone()
    {
        var clone = new BashLoopBindingContext();
        clone._bindings.AddRange(_bindings);
        return clone;
    }

    internal ShellValueDomain AnalyzeIterable(
        IReadOnlyList<BashToken> words,
        BashParserOptions options,
        bool workingDirectoryUnknown)
    {
        if (words.Count == 0)
        {
            return ShellValueDomain.Unknown;
        }

        if (words.Count == 1 &&
            words[0].ResolverValue is not null &&
            TryBuildStaticPattern(
                words[0].ResolverValue!,
                options,
                workingDirectoryUnknown,
                out var pattern))
        {
            return pattern;
        }

        var values = new List<string>(words.Count);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            if (word.ResolverValue is null || HasUnmodeledBraceExpansion(word))
            {
                return ShellValueDomain.Unknown;
            }

            ShellValueDomain wordDomain;
            if (IsEntirelyLiteral(word.ResolverValue))
            {
                wordDomain = new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { word.ResolverValue.Decoded },
                };
            }
            else if (!TryAnalyzeEffectiveValue(word.ResolverValue, out wordDomain) ||
                     wordDomain.Kind is not (
                         ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
            {
                return ShellValueDomain.Unknown;
            }

            foreach (var candidate in wordDomain.Values)
            {
                if (!distinct.Add(candidate))
                {
                    continue;
                }

                if (distinct.Count > ShellAnalysisLimits.MaxValueCandidates)
                {
                    return ShellValueDomain.Unknown;
                }

                values.Add(candidate);
            }
        }

        return CreateFiniteDomain(values);
    }

    internal bool TryAnalyzeEffectiveValue(
        ShellValue value,
        out ShellValueDomain domain)
    {
        var referenced = new List<BindingFrame>();
        var dependentButUnsupported = false;
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                fragment.Expansion is null)
            {
                continue;
            }

            var binding = FindExactBinding(fragment.Expansion.Value);
            if (binding is not null)
            {
                if (!referenced.Contains(binding))
                {
                    referenced.Add(binding);
                }

                if (fragment.Cardinality != ShellValueCardinality.ExactlyOne ||
                    (fragment.AllowedTransforms & ShellLexicalTransform.FieldSplit) != 0)
                {
                    dependentButUnsupported = true;
                }

                continue;
            }

            if (ReferencesBindingThroughUnsupportedExpansion(fragment.Expansion.Value))
            {
                dependentButUnsupported = true;
            }
        }

        if (referenced.Count == 0 && !dependentButUnsupported)
        {
            domain = ShellValueDomain.Unknown;
            return false;
        }

        if (dependentButUnsupported || ContainsUnresolvedFragment(value, referenced))
        {
            domain = ShellValueDomain.Unknown;
            return true;
        }

        if (referenced.Count == 1 &&
            referenced[0].Domain.Kind == ShellValueDomainKind.Pattern)
        {
            domain = IsOneBindingExpansion(value, referenced[0])
                ? referenced[0].Domain
                : ShellValueDomain.Unknown;
            return true;
        }

        foreach (var binding in referenced)
        {
            if (binding.Domain.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
            {
                domain = ShellValueDomain.Unknown;
                return true;
            }
        }

        var candidates = new List<string>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var selected = new Dictionary<BindingFrame, string>();
        if (!TryComposeCandidates(
                value,
                referenced,
                bindingIndex: 0,
                selected,
                candidates,
                distinct))
        {
            domain = ShellValueDomain.Unknown;
            return true;
        }

        domain = CreateFiniteDomain(candidates);
        return true;
    }

    private bool TryComposeCandidates(
        ShellValue value,
        IReadOnlyList<BindingFrame> bindings,
        int bindingIndex,
        Dictionary<BindingFrame, string> selected,
        List<string> candidates,
        HashSet<string> distinct)
    {
        if (bindingIndex == bindings.Count)
        {
            var rendered = Render(value, selected);
            if (rendered is null)
            {
                return false;
            }

            if (distinct.Add(rendered))
            {
                if (distinct.Count > ShellAnalysisLimits.MaxValueCandidates)
                {
                    return false;
                }

                candidates.Add(rendered);
            }

            return true;
        }

        var binding = bindings[bindingIndex];
        foreach (var candidate in binding.Domain.Values)
        {
            selected[binding] = candidate;
            if (!TryComposeCandidates(
                    value,
                    bindings,
                    bindingIndex + 1,
                    selected,
                    candidates,
                    distinct))
            {
                return false;
            }
        }

        selected.Remove(binding);
        return true;
    }

    private string? Render(
        ShellValue value,
        IReadOnlyDictionary<BindingFrame, string> selected)
    {
        var rendered = new StringBuilder(value.Decoded.Length);
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind == ShellValueFragmentKind.Literal)
            {
                rendered.Append(fragment.Value);
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                fragment.Expansion is null)
            {
                return null;
            }

            var binding = FindExactBinding(fragment.Expansion.Value);
            if (binding is null || !selected.TryGetValue(binding, out var candidate))
            {
                return null;
            }

            rendered.Append(candidate);
        }

        return rendered.ToString();
    }

    private bool ContainsUnresolvedFragment(
        ShellValue value,
        IReadOnlyList<BindingFrame> referenced)
    {
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind == ShellValueFragmentKind.Literal)
            {
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                fragment.Expansion is null)
            {
                return true;
            }

            var binding = FindExactBinding(fragment.Expansion.Value);
            if (binding is null || !ContainsReference(referenced, binding))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsReference(
        IReadOnlyList<BindingFrame> bindings,
        BindingFrame expected)
    {
        foreach (var binding in bindings)
        {
            if (object.ReferenceEquals(binding, expected))
            {
                return true;
            }
        }

        return false;
    }

    private BindingFrame? FindExactBinding(ShellExpansionReference expansion)
    {
        if (expansion.Kind != ShellExpansionKind.Variable || expansion.Name is null)
        {
            return null;
        }

        for (var index = _bindings.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_bindings[index].Name, expansion.Name, StringComparison.Ordinal))
            {
                return _bindings[index];
            }
        }

        return null;
    }

    private BindingFrame? FindExactBinding(string name)
    {
        for (var index = _bindings.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_bindings[index].Name, name, StringComparison.Ordinal))
            {
                return _bindings[index];
            }
        }

        return null;
    }

    private bool ReferencesBindingThroughUnsupportedExpansion(
        ShellExpansionReference expansion)
    {
        if (expansion.Kind != ShellExpansionKind.Variable || expansion.Name is null)
        {
            return false;
        }

        foreach (var binding in _bindings)
        {
            var name = expansion.Name;
            if (name.Length > binding.Name.Length &&
                (name[0] == '!' &&
                 string.Equals(name.Substring(1), binding.Name, StringComparison.Ordinal) ||
                 name.StartsWith(binding.Name, StringComparison.Ordinal) &&
                 IsParameterOperator(name[binding.Name.Length])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsParameterOperator(char value) =>
        value is '[' or ':' or '-' or '+' or '=' or '?' or '%' or '#' or '/' or '^' or ',';

    private BindingFrame? BindingFor(ShellValueFragment fragment) =>
        fragment.Expansion is null ? null : FindExactBinding(fragment.Expansion.Value);

    private bool IsOneBindingExpansion(ShellValue value, BindingFrame binding)
    {
        var expansionCount = 0;
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind == ShellValueFragmentKind.Literal && fragment.Value.Length == 0)
            {
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                !object.ReferenceEquals(BindingFor(fragment), binding))
            {
                return false;
            }

            expansionCount++;
        }

        return expansionCount == 1;
    }

    private static bool TryBuildStaticPattern(
        ShellValue value,
        BashParserOptions options,
        bool workingDirectoryUnknown,
        out ShellValueDomain pattern)
    {
        var containsGlob = false;
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind == ShellValueFragmentKind.Literal)
            {
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                fragment.Expansion is null ||
                fragment.Expansion.Value.Kind != ShellExpansionKind.Glob)
            {
                pattern = ShellValueDomain.Unknown;
                return false;
            }

            containsGlob = true;
        }

        var authored = value.Decoded;
        if (!containsGlob ||
            !BashResolver.LooksLikePath(authored) ||
            authored.IndexOf("://", StringComparison.Ordinal) >= 0 ||
            ContainsParentTraversal(authored) ||
            HasGlobBearingDotSegment(authored))
        {
            pattern = ShellValueDomain.Unknown;
            return false;
        }

        var firstGlob = FirstGlobIndex(authored);
        var slash = authored.LastIndexOf('/', firstGlob);
        var directory = slash switch
        {
            < 0 => ".",
            0 => "/",
            _ => authored.Substring(0, slash),
        };
        var resolved = BashResolver.Resolve(
            ShellValue.Literal(directory),
            treatAsPath: true,
            options,
            workingDirectoryUnknown,
            ShellResolutionConsumer.BashArgument);
        if (resolved.Resolved is null)
        {
            pattern = ShellValueDomain.Unknown;
            return false;
        }

        pattern = new ShellValueDomain
        {
            Kind = ShellValueDomainKind.Pattern,
            Pattern = authored,
            CoveringDirectory = resolved.Resolved,
        };
        return true;
    }

    private static int FirstGlobIndex(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is '*' or '?' or '[')
            {
                return index;
            }
        }

        return value.Length;
    }

    private static bool ContainsParentTraversal(string path)
    {
        var segments = path.Replace('\\', '/').Split('/');
        foreach (var segment in segments)
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasGlobBearingDotSegment(string path)
    {
        var segments = path.Replace('\\', '/').Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length > 0 &&
                segment[0] == '.' &&
                FirstGlobIndex(segment) < segment.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEntirelyLiteral(ShellValue value)
    {
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind != ShellValueFragmentKind.Literal)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasUnmodeledBraceExpansion(BashToken token) =>
        token.Kind == BashTokenKind.Word &&
        (token.Value.IndexOf('{') >= 0 || token.Value.IndexOf('}') >= 0);

    private static ShellValueDomain CreateFiniteDomain(IReadOnlyList<string> values) =>
        values.Count switch
        {
            1 => new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { values[0] },
            },
            >= 2 and <= 32 => new ShellValueDomain
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = Copy(values),
            },
            _ => ShellValueDomain.Unknown,
        };

    private static string[] Copy(IReadOnlyList<string> values)
    {
        var copy = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = values[index];
        }

        return copy;
    }

    private sealed class BindingFrame
    {
        internal BindingFrame(string name, ShellValueDomain domain)
        {
            Name = name;
            Domain = domain;
        }

        internal string Name { get; }

        internal ShellValueDomain Domain { get; }
    }
}
