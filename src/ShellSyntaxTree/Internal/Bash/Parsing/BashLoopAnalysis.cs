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

internal enum BashIterationCardinality
{
    Never,
    OneOrMore,
    ZeroOrMore,
}

internal enum BashBindingAlternativeResult
{
    Exact,
    Unknown,
    ExceededLimit,
}

internal sealed record BashLoopWord(
    ShellValue? Value,
    bool HasUnmodeledBraceExpansion);

internal sealed record BashForInAnalysisPlan(
    string BindingName,
    IReadOnlyList<BashLoopWord> Words);

internal sealed record BashForInAnalysisPlanReference(
    ForEachSyntax Syntax,
    BashForInAnalysisPlan Plan);

internal sealed record BashIterationPlan(
    IReadOnlyList<ShellValueDomainFacts> OrderedCandidates,
    BashIterationCardinality Cardinality,
    bool RequiresFixedPoint,
    ShellValueDomainFacts Summary);

/// <summary>
/// Preserves bounded loop-variable proofs while the Bash structural parser
/// still owns lexer provenance. Compatibility leaves deliberately retain
/// their authored dynamic values; only occurrence facts receive these
/// effective domains.
/// </summary>
internal sealed class BashLoopBindingContext
{
    private readonly List<BindingFrame> _bindings = new();

    internal BashLoopBindingContext WithBinding(
        string name,
        ShellValueDomainFacts domain)
    {
        var clone = Clone();
        for (var index = clone._bindings.Count - 1; index >= 0; index--)
        {
            if (string.Equals(clone._bindings[index].Name, name, StringComparison.Ordinal))
            {
                clone._bindings.RemoveAt(index);
            }
        }

        clone._bindings.Add(new BindingFrame(name, domain));
        return clone;
    }

    private BashLoopBindingContext Clone()
    {
        var clone = new BashLoopBindingContext();
        clone._bindings.AddRange(_bindings);
        return clone;
    }

    internal BashLoopBindingContext WithoutBindings() => new();

    internal bool HasBindings => _bindings.Count > 0;

    internal BashBindingAlternativeResult EnumerateExactAlternatives(
        int maximumCount,
        IReadOnlyList<string> bindingNames,
        out IReadOnlyList<BashLoopBindingContext> alternatives)
    {
        var current = new List<BashLoopBindingContext> { new() };
        foreach (var binding in _bindings)
        {
            if (!ContainsName(bindingNames, binding.Name))
            {
                continue;
            }

            if (binding.Domain.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet) ||
                binding.Domain.Values.Count == 0)
            {
                alternatives = Array.Empty<BashLoopBindingContext>();
                return BashBindingAlternativeResult.Unknown;
            }

            if (current.Count > maximumCount / binding.Domain.Values.Count)
            {
                alternatives = Array.Empty<BashLoopBindingContext>();
                return BashBindingAlternativeResult.ExceededLimit;
            }

            var next = new List<BashLoopBindingContext>(
                current.Count * binding.Domain.Values.Count);
            foreach (var state in current)
            {
                foreach (var value in binding.Domain.Values)
                {
                    next.Add(state.WithBinding(
                        binding.Name,
                        new ShellValueDomainFacts
                        {
                            Kind = ShellValueDomainKind.Exact,
                            Values = new[] { value },
                        }));
                }
            }

            current = next;
        }

        alternatives = current;
        return BashBindingAlternativeResult.Exact;
    }

    internal IReadOnlyList<string> FindReferencedBindingNames(
        IReadOnlyList<ShellValue> values)
    {
        var names = new List<string>();
        foreach (var binding in _bindings)
        {
            foreach (var value in values)
            {
                if (ReferencesBinding(value, binding.Name))
                {
                    names.Add(binding.Name);
                    break;
                }
            }
        }

        return names;
    }

    internal bool ReferencesTrackedBinding(ShellValue value)
    {
        foreach (var binding in _bindings)
        {
            if (ReferencesBinding(value, binding.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsName(IReadOnlyList<string> names, string expected)
    {
        foreach (var name in names)
        {
            if (string.Equals(name, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal bool StateEquals(BashLoopBindingContext other)
    {
        if (_bindings.Count != other._bindings.Count)
        {
            return false;
        }

        foreach (var binding in _bindings)
        {
            var otherBinding = other.FindExactBinding(binding.Name);
            if (otherBinding is null || !DomainEquals(binding.Domain, otherBinding.Domain))
            {
                return false;
            }
        }

        return true;
    }

    internal static BashLoopBindingContext JoinState(
        BashLoopBindingContext left,
        BashLoopBindingContext right)
    {
        var joined = new BashLoopBindingContext();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in left._bindings)
        {
            names.Add(binding.Name);
        }

        foreach (var binding in right._bindings)
        {
            names.Add(binding.Name);
        }

        foreach (var name in names)
        {
            var leftBinding = left.FindExactBinding(name);
            var rightBinding = right.FindExactBinding(name);
            joined._bindings.Add(new BindingFrame(
                name,
                leftBinding is null || rightBinding is null
                    ? ShellValueDomainFacts.Unknown
                    : JoinDomains(leftBinding.Domain, rightBinding.Domain)));
        }

        return joined;
    }

    internal static BashLoopBindingContext WidenState(
        BashLoopBindingContext left,
        BashLoopBindingContext right)
    {
        var widened = new BashLoopBindingContext();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in left._bindings)
        {
            names.Add(binding.Name);
        }

        foreach (var binding in right._bindings)
        {
            names.Add(binding.Name);
        }

        foreach (var name in names)
        {
            var leftBinding = left.FindExactBinding(name);
            var rightBinding = right.FindExactBinding(name);
            widened._bindings.Add(new BindingFrame(
                name,
                leftBinding is not null &&
                rightBinding is not null &&
                DomainEquals(leftBinding.Domain, rightBinding.Domain)
                    ? leftBinding.Domain
                    : ShellValueDomainFacts.Unknown));
        }

        return widened;
    }

    internal static bool ReferencesBinding(ShellValue value, string bindingName)
    {
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind == ShellValueFragmentKind.Expansion &&
                fragment.Expansion is ShellExpansionReference expansion &&
                expansion.Kind == ShellExpansionKind.Variable &&
                string.Equals(expansion.Name, bindingName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal BashIterationPlan AnalyzeIterationPlan(
        IReadOnlyList<BashLoopWord> words,
        BashParserOptions options,
        bool workingDirectoryUnknown)
    {
        if (words.Count == 0)
        {
            return new BashIterationPlan(
                Array.Empty<ShellValueDomainFacts>(),
                BashIterationCardinality.Never,
                RequiresFixedPoint: false,
                ShellValueDomainFacts.Unknown);
        }

        var ordered = new List<ShellValueDomainFacts>(words.Count);
        var summary = new List<string>(words.Count);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            if (word.Value is null || word.HasUnmodeledBraceExpansion)
            {
                return FixedPointPlan(
                    BashIterationCardinality.ZeroOrMore,
                    ShellValueDomainFacts.Unknown);
            }

            if (TryBuildStaticPattern(
                    word.Value,
                    options,
                    workingDirectoryUnknown,
                    out var pattern))
            {
                return FixedPointPlan(BashIterationCardinality.ZeroOrMore, pattern);
            }

            ShellValueDomainFacts domain;
            if (IsEntirelyLiteral(word.Value))
            {
                domain = new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { word.Value.Decoded },
                };
            }
            else if (!TryAnalyzeEffectiveValue(word.Value, out domain) ||
                     domain.Kind != ShellValueDomainKind.Exact)
            {
                return FixedPointPlan(
                    BashIterationCardinality.ZeroOrMore,
                    domain);
            }

            ordered.Add(domain);
            var candidate = domain.Values[0];
            if (distinct.Add(candidate))
            {
                summary.Add(candidate);
            }

            if (ordered.Count > ShellAnalysisLimits.MaxValueCandidates)
            {
                return FixedPointPlan(
                    BashIterationCardinality.OneOrMore,
                    ShellValueDomainFacts.Unknown);
            }
        }

        return new BashIterationPlan(
            ordered.ToArray(),
            BashIterationCardinality.OneOrMore,
            RequiresFixedPoint: false,
            CreateFiniteDomain(summary));
    }

    internal static BashForInAnalysisPlan CapturePlan(
        string bindingName,
        IReadOnlyList<BashToken> words)
    {
        var captured = new BashLoopWord[words.Count];
        for (var index = 0; index < captured.Length; index++)
        {
            captured[index] = new BashLoopWord(
                words[index].ResolverValue,
                HasUnmodeledBraceExpansion(words[index]));
        }

        return new BashForInAnalysisPlan(bindingName, captured);
    }

    private static BashIterationPlan FixedPointPlan(
        BashIterationCardinality cardinality,
        ShellValueDomainFacts summary) => new(
            Array.Empty<ShellValueDomainFacts>(),
            cardinality,
            RequiresFixedPoint: true,
            summary);

    internal static ShellValueDomainFacts JoinDomains(
        ShellValueDomainFacts left,
        ShellValueDomainFacts right)
    {
        if (DomainEquals(left, right))
        {
            return left;
        }

        if (left.Kind is not (
                ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet) ||
            right.Kind is not (
                ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
        {
            return ShellValueDomainFacts.Unknown;
        }

        var values = new List<string>(left.Values.Count + right.Values.Count);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in left.Values)
        {
            if (distinct.Add(candidate))
            {
                values.Add(candidate);
            }
        }

        foreach (var candidate in right.Values)
        {
            if (distinct.Add(candidate))
            {
                if (distinct.Count > ShellAnalysisLimits.MaxValueCandidates)
                {
                    return ShellValueDomainFacts.Unknown;
                }

                values.Add(candidate);
            }
        }

        return CreateFiniteDomain(values);
    }

    private static bool DomainEquals(
        ShellValueDomainFacts left,
        ShellValueDomainFacts right) => ShellValueDomainFacts.AreEqual(left, right);

    internal bool TryAnalyzeEffectiveValue(
        ShellValue value,
        out ShellValueDomainFacts domain) =>
        TryAnalyzeValue(value, ignoreFieldSplitting: false, out domain);

    internal bool TryAnalyzeAuthoredValue(
        ShellValue value,
        out ShellValueDomainFacts domain) =>
        TryAnalyzeValue(value, ignoreFieldSplitting: true, out domain);

    private bool TryAnalyzeValue(
        ShellValue value,
        bool ignoreFieldSplitting,
        out ShellValueDomainFacts domain)
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
                    !ignoreFieldSplitting &&
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
            domain = ShellValueDomainFacts.Unknown;
            return false;
        }

        if (dependentButUnsupported || ContainsUnresolvedFragment(value, referenced))
        {
            domain = ShellValueDomainFacts.Unknown;
            return true;
        }

        if (referenced.Count == 1 &&
            referenced[0].Domain.Kind == ShellValueDomainKind.Pattern)
        {
            domain = IsOneBindingExpansion(value, referenced[0])
                ? referenced[0].Domain
                : ShellValueDomainFacts.Unknown;
            return true;
        }

        foreach (var binding in referenced)
        {
            if (binding.Domain.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
            {
                domain = ShellValueDomainFacts.Unknown;
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
            domain = ShellValueDomainFacts.Unknown;
            return true;
        }

        domain = CreateFiniteDomain(candidates);
        return true;
    }

    internal ShellValueDomainFacts AnalyzeWordForTransfer(ShellValue value)
    {
        if (IsEntirelyLiteral(value))
        {
            return new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { value.Decoded },
            };
        }

        return TryAnalyzeEffectiveValue(value, out var domain)
            ? domain
            : ShellValueDomainFacts.Unknown;
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
        out ShellValueDomainFacts pattern)
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
                pattern = ShellValueDomainFacts.Unknown;
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
            pattern = ShellValueDomainFacts.Unknown;
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
            pattern = ShellValueDomainFacts.Unknown;
            return false;
        }

        pattern = new ShellValueDomainFacts
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

    private static ShellValueDomainFacts CreateFiniteDomain(IReadOnlyList<string> values) =>
        values.Count switch
        {
            1 => new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { values[0] },
            },
            >= 2 and <= 32 => new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = Copy(values),
            },
            _ => ShellValueDomainFacts.Unknown,
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
        internal BindingFrame(string name, ShellValueDomainFacts domain)
        {
            Name = name;
            Domain = domain;
        }

        internal string Name { get; }

        internal ShellValueDomainFacts Domain { get; }
    }
}
