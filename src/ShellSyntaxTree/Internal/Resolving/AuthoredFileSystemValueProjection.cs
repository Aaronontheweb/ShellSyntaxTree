// -----------------------------------------------------------------------
// <copyright file="AuthoredFileSystemValueProjection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Resolving;

internal enum AuditedFileSystemBindingCategory
{
    Unknown,
    AllNonOptionOperands,
    ExactNamedParameterValue,
}

internal sealed class AuthoredFileSystemBindingCatalogEntry
{
    internal AuthoredFileSystemBindingCatalogEntry(
        ShellProjectionLanguage language,
        string canonicalVerb,
        StringComparison verbComparison,
        AuditedFileSystemBindingCategory category,
        string? parameterName = null)
    {
        if (category == AuditedFileSystemBindingCategory.ExactNamedParameterValue &&
            string.IsNullOrEmpty(parameterName))
        {
            throw new ArgumentException(
                "An exact named-parameter binding requires a parameter name.",
                nameof(parameterName));
        }

        if (category != AuditedFileSystemBindingCategory.ExactNamedParameterValue &&
            parameterName != null)
        {
            throw new ArgumentException(
                "Only an exact named-parameter binding accepts a parameter name.",
                nameof(parameterName));
        }

        Language = language;
        CanonicalVerb = canonicalVerb;
        VerbComparison = verbComparison;
        Category = category;
        ParameterName = parameterName;
    }

    internal ShellProjectionLanguage Language { get; }

    internal string CanonicalVerb { get; }

    internal StringComparison VerbComparison { get; }

    internal AuditedFileSystemBindingCategory Category { get; }

    internal string? ParameterName { get; }
}

/// <summary>
/// Owns the deliberately small catalog whose entries prove local-filesystem
/// argument semantics. Compatibility path tables do not feed this catalog.
/// </summary>
internal static class AuthoredFileSystemBindingCatalog
{
    private static readonly IReadOnlyList<AuthoredFileSystemBindingCatalogEntry> Entries =
        new[]
        {
            new AuthoredFileSystemBindingCatalogEntry(
                ShellProjectionLanguage.Bash,
                "cat",
                StringComparison.Ordinal,
                AuditedFileSystemBindingCategory.AllNonOptionOperands),
            new AuthoredFileSystemBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Get-Content",
                StringComparison.OrdinalIgnoreCase,
                AuditedFileSystemBindingCategory.ExactNamedParameterValue,
                "-LiteralPath"),
        };

    internal static IReadOnlyList<AuditedFileSystemBindingCategory> Bind(
        ShellProjectionLanguage language,
        Clause clause,
        IReadOnlyList<AnalyzedArgument> arguments)
    {
        var bindings = new AuditedFileSystemBindingCategory[arguments.Count];
        if (clause.Verb.IsDynamic || clause.Verb.Tokens.Count != 1)
        {
            return bindings;
        }

        var canonicalVerb = language == ShellProjectionLanguage.PowerShell
            ? clause.Verb.CanonicalVerb ?? clause.Verb.Tokens[0]
            : clause.Verb.Tokens[0];
        for (var index = 0; index < Entries.Count; index++)
        {
            var entry = Entries[index];
            if (entry.Language != language ||
                !string.Equals(canonicalVerb, entry.CanonicalVerb, entry.VerbComparison))
            {
                continue;
            }

            return Bind(entry, arguments);
        }

        return bindings;
    }

    internal static IReadOnlyList<AuditedFileSystemBindingCategory> Bind(
        AuthoredFileSystemBindingCatalogEntry entry,
        IReadOnlyList<AnalyzedArgument> arguments)
    {
        var bindings = new AuditedFileSystemBindingCategory[arguments.Count];
        return entry.Category switch
        {
            AuditedFileSystemBindingCategory.AllNonOptionOperands =>
                BindAllNonOptionOperands(bindings),
            AuditedFileSystemBindingCategory.ExactNamedParameterValue =>
                BindExactNamedParameterValue(
                    arguments,
                    bindings,
                    entry.ParameterName!),
            _ => bindings,
        };
    }

    private static IReadOnlyList<AuditedFileSystemBindingCategory> BindAllNonOptionOperands(
        AuditedFileSystemBindingCategory[] bindings)
    {
        for (var index = 0; index < bindings.Length; index++)
        {
            bindings[index] = AuditedFileSystemBindingCategory.AllNonOptionOperands;
        }

        return bindings;
    }

    private static IReadOnlyList<AuditedFileSystemBindingCategory> BindExactNamedParameterValue(
        IReadOnlyList<AnalyzedArgument> arguments,
        AuditedFileSystemBindingCategory[] bindings,
        string parameterName)
    {
        var expectsValue = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (expectsValue)
            {
                if (!argument.Argument.IsFlag)
                {
                    bindings[index] = AuditedFileSystemBindingCategory.ExactNamedParameterValue;
                }

                expectsValue = false;
            }

            if (argument.Argument.IsFlag && string.Equals(
                    argument.Argument.Raw,
                    parameterName,
                    StringComparison.OrdinalIgnoreCase))
            {
                expectsValue = true;
            }
        }

        return bindings;
    }
}

/// <summary>
/// Combines an audited binding, transform provenance, and the existing path
/// resolver into one fail-closed public value domain.
/// </summary>
internal static class AuthoredFileSystemValueProjection
{
    internal static IReadOnlyList<AnalyzedArgument> Apply(
        ShellProjectionLanguage language,
        Clause clause,
        IReadOnlyList<ShellValueElementProvenance> provenance,
        ShellValueDomainFacts workingDirectory,
        IReadOnlyList<AnalyzedArgument> arguments)
    {
        if (language == ShellProjectionLanguage.Unknown || arguments.Count == 0)
        {
            return arguments;
        }

        var bindings = AuthoredFileSystemBindingCatalog.Bind(language, clause, arguments);
        if (!HasAuditedBinding(bindings))
        {
            return arguments;
        }

        var provenanceByElement = IndexProvenance(provenance);
        var projected = new AnalyzedArgument[arguments.Count];
        var bashOptionsEnded = new HashSet<bool> { false };
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            ShellValueDomain domain = new ShellValueDomain.Unknown();
            var candidates = Values(argument.AuthoredValue);
            switch (bindings[index])
            {
                case AuditedFileSystemBindingCategory.AllNonOptionOperands:
                    var allCandidatesArePaths = ClassifyNonOptionOperand(
                        candidates,
                        bashOptionsEnded,
                        out var nextStates);
                    bashOptionsEnded = nextStates;
                    if (allCandidatesArePaths &&
                        TryGetProvenance(argument, clause, provenanceByElement, out var bashValue) &&
                        IsBashTransformSafe(bashValue, candidates) &&
                        TryResolve(
                            ShellProjectionLanguage.Bash,
                            candidates,
                            workingDirectory,
                            out var bashDomain))
                    {
                        domain = bashDomain;
                    }

                    break;
                case AuditedFileSystemBindingCategory.ExactNamedParameterValue:
                    if (candidates.Count > 0 &&
                        TryGetProvenance(argument, clause, provenanceByElement, out var pwshValue) &&
                        IsSingleField(pwshValue) &&
                        TryResolve(
                            ShellProjectionLanguage.PowerShell,
                            candidates,
                            workingDirectory,
                            out var pwshDomain))
                    {
                        domain = pwshDomain;
                    }

                    break;
            }

            projected[index] = argument with { AuthoredFileSystemValue = domain };
        }

        return projected;
    }

    private static bool HasAuditedBinding(
        IReadOnlyList<AuditedFileSystemBindingCategory> bindings)
    {
        for (var index = 0; index < bindings.Count; index++)
        {
            if (bindings[index] != AuditedFileSystemBindingCategory.Unknown)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<int, ShellValue> IndexProvenance(
        IReadOnlyList<ShellValueElementProvenance> provenance)
    {
        var indexed = new Dictionary<int, ShellValue>();
        for (var index = 0; index < provenance.Count; index++)
        {
            var current = provenance[index];
            if (current.ClauseElementIndex >= 0 &&
                !indexed.ContainsKey(current.ClauseElementIndex))
            {
                indexed.Add(current.ClauseElementIndex, current.Value);
            }
        }

        return indexed;
    }

    private static bool TryGetProvenance(
        AnalyzedArgument argument,
        Clause clause,
        IReadOnlyDictionary<int, ShellValue> provenance,
        out ShellValue value)
    {
        for (var index = 0; index < clause.Elements.Count; index++)
        {
            if (ReferenceEquals(clause.Elements[index], argument.Element) &&
                provenance.TryGetValue(index, out value!))
            {
                return true;
            }
        }

        value = ShellValue.Literal(string.Empty);
        return false;
    }

    private static IReadOnlyList<string> Values(ShellValueDomain domain) => domain switch
    {
        ShellValueDomain.Exact exact => new[] { exact.Value },
        ShellValueDomain.FiniteSet finite => finite.Values,
        _ => Array.Empty<string>(),
    };

    private static bool ClassifyNonOptionOperand(
        IReadOnlyList<string> candidates,
        IReadOnlyCollection<bool> currentStates,
        out HashSet<bool> nextStates)
    {
        nextStates = new HashSet<bool>();
        if (candidates.Count == 0)
        {
            nextStates.Add(false);
            nextStates.Add(true);
            return false;
        }

        var allArePaths = true;
        foreach (var optionsEnded in currentStates)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (!optionsEnded && string.Equals(candidate, "--", StringComparison.Ordinal))
                {
                    nextStates.Add(true);
                    allArePaths = false;
                }
                else
                {
                    nextStates.Add(optionsEnded);
                    if (string.Equals(candidate, "-", StringComparison.Ordinal) ||
                        !optionsEnded && candidate.StartsWith("-", StringComparison.Ordinal))
                    {
                        allArePaths = false;
                    }
                }
            }
        }

        return allArePaths;
    }

    private static bool IsBashTransformSafe(
        ShellValue value,
        IReadOnlyList<string> candidates)
    {
        if (!IsSingleField(value))
        {
            return false;
        }

        for (var fragmentIndex = 0; fragmentIndex < value.Fragments.Count; fragmentIndex++)
        {
            var fragment = value.Fragments[fragmentIndex];
            if ((fragment.AllowedTransforms & ShellLexicalTransform.Glob) != 0)
            {
                return false;
            }

            if ((fragment.AllowedTransforms & ShellLexicalTransform.FieldSplit) == 0)
            {
                continue;
            }

            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                if (ContainsFieldSplitOrGlobCharacter(candidates[candidateIndex]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsSingleField(ShellValue value)
    {
        for (var index = 0; index < value.Fragments.Count; index++)
        {
            var fragment = value.Fragments[index];
            if (fragment.Kind == ShellValueFragmentKind.Opaque ||
                fragment.Cardinality != ShellValueCardinality.ExactlyOne)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsFieldSplitOrGlobCharacter(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is ' ' or '\t' or '\n' or '*' or '?' or '[')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolve(
        ShellProjectionLanguage language,
        IReadOnlyList<string> candidates,
        ShellValueDomainFacts workingDirectory,
        out ShellValueDomain domain)
    {
        domain = new ShellValueDomain.Unknown();
        if (workingDirectory.Kind != ShellValueDomainKind.Exact ||
            workingDirectory.Values.Count != 1 ||
            candidates.Count == 0 ||
            candidates.Count > ShellAnalysisLimits.MaxValueCandidates)
        {
            return false;
        }

        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var resolved = ResolveCandidate(
                language,
                candidates[index],
                workingDirectory.Values[0]);
            if (resolved is null)
            {
                return false;
            }

            if (distinct.Add(resolved))
            {
                normalized.Add(resolved);
            }
        }

        domain = normalized.Count == 1
            ? new ShellValueDomain.Exact(normalized[0])
            : new ShellValueDomain.FiniteSet(normalized);
        return true;
    }

    private static string? ResolveCandidate(
        ShellProjectionLanguage language,
        string candidate,
        string workingDirectory)
    {
        var value = ShellValue.Literal(candidate);
        var resolution = language switch
        {
            ShellProjectionLanguage.Bash => BashResolver.Resolve(
                value,
                treatAsPath: true,
                new BashParserOptions { WorkingDirectory = workingDirectory },
                workingDirectoryUnknown: false,
                ShellResolutionConsumer.BashArgument),
            ShellProjectionLanguage.PowerShell => PwshResolver.Resolve(
                value,
                treatAsPath: true,
                new PwshParserOptions { WorkingDirectory = workingDirectory },
                workingDirectoryUnknown: false,
                ShellResolutionConsumer.PowerShellCmdletLiteralPath),
            _ => (ArgKind.DynamicSkip, null, false),
        };

        return resolution.Kind == ArgKind.Literal &&
               resolution.IsPath &&
               resolution.Resolved is not null
            ? resolution.Resolved
            : null;
    }
}
