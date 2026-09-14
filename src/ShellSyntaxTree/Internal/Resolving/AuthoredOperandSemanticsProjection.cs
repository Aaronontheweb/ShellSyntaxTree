// -----------------------------------------------------------------------
// <copyright file="AuthoredOperandSemanticsProjection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Pwsh.Verbs;

namespace ShellSyntaxTree.Internal.Resolving;

internal enum AuditedOperandBindingCategory
{
    Unknown,
    AllNonOptionOperands,
    ExactNamedParameterValue,
    ExactPositionalValue,
    AllArguments,
}

internal enum AuditedOperandSemantic
{
    Unknown,
    LocalFileSystem,
    NonFileSystem,
}

internal readonly record struct AuditedOperandBinding(
    AuditedOperandBindingCategory Category,
    AuditedOperandSemantic Semantic,
    ShellResolutionConsumer? PowerShellPathConsumer = null,
    bool AcceptsAuthoredValueGroup = false);

internal sealed class AuditedOperandBindingCatalogEntry
{
    internal AuditedOperandBindingCatalogEntry(
        ShellProjectionLanguage language,
        string canonicalVerb,
        StringComparison verbComparison,
        AuditedOperandBindingCategory category,
        AuditedOperandSemantic semantic,
        string? parameterName = null,
        int? positionalIndex = null,
        ShellResolutionConsumer? powerShellPathConsumer = null,
        bool acceptsAuthoredValueGroup = false,
        IReadOnlyDictionary<string, PwshBinding>? exactPowerShellParameters = null,
        bool stopsVerbChain = false)
    {
        if (category == AuditedOperandBindingCategory.ExactNamedParameterValue &&
            string.IsNullOrEmpty(parameterName))
        {
            throw new ArgumentException(
                "An exact named-parameter binding requires a parameter name.",
                nameof(parameterName));
        }

        if (category != AuditedOperandBindingCategory.ExactNamedParameterValue &&
            parameterName != null)
        {
            throw new ArgumentException(
                "Only an exact named-parameter binding accepts a parameter name.",
                nameof(parameterName));
        }

        if (category == AuditedOperandBindingCategory.ExactPositionalValue !=
            positionalIndex.HasValue || positionalIndex < 0)
        {
            throw new ArgumentException(
                "An exact positional binding requires one non-negative position.",
                nameof(positionalIndex));
        }

        if (powerShellPathConsumer is not null &&
            (language != ShellProjectionLanguage.PowerShell ||
             semantic != AuditedOperandSemantic.LocalFileSystem ||
             powerShellPathConsumer is not (
                 ShellResolutionConsumer.PowerShellCmdletPath or
                 ShellResolutionConsumer.PowerShellCmdletLiteralPath)))
        {
            throw new ArgumentException(
                "A PowerShell filesystem binding requires an audited cmdlet path consumer.",
                nameof(powerShellPathConsumer));
        }

        if (acceptsAuthoredValueGroup &&
            (language != ShellProjectionLanguage.PowerShell ||
             semantic != AuditedOperandSemantic.NonFileSystem))
        {
            throw new ArgumentException(
                "Only audited PowerShell non-filesystem bindings accept value groups.",
                nameof(acceptsAuthoredValueGroup));
        }

        if (exactPowerShellParameters is not null &&
            (language != ShellProjectionLanguage.PowerShell ||
             category != AuditedOperandBindingCategory.ExactPositionalValue))
        {
            throw new ArgumentException(
                "An exact PowerShell parameter inventory applies only to positional bindings.",
                nameof(exactPowerShellParameters));
        }

        if (semantic == AuditedOperandSemantic.Unknown ||
            category == AuditedOperandBindingCategory.Unknown)
        {
            throw new ArgumentException(
                "An audited binding requires a known category and semantic.",
                nameof(semantic));
        }

        Language = language;
        CanonicalVerb = canonicalVerb;
        VerbComparison = verbComparison;
        Category = category;
        Semantic = semantic;
        ParameterName = parameterName;
        PositionalIndex = positionalIndex;
        PowerShellPathConsumer = powerShellPathConsumer;
        AcceptsAuthoredValueGroup = acceptsAuthoredValueGroup;
        ExactPowerShellParameters = exactPowerShellParameters;
        StopsVerbChain = stopsVerbChain;
    }

    internal ShellProjectionLanguage Language { get; }

    internal string CanonicalVerb { get; }

    internal StringComparison VerbComparison { get; }

    internal AuditedOperandBindingCategory Category { get; }

    internal AuditedOperandSemantic Semantic { get; }

    internal string? ParameterName { get; }

    internal int? PositionalIndex { get; }

    internal ShellResolutionConsumer? PowerShellPathConsumer { get; }

    internal bool AcceptsAuthoredValueGroup { get; }

    internal IReadOnlyDictionary<string, PwshBinding>? ExactPowerShellParameters { get; }

    internal bool StopsVerbChain { get; }
}

/// <summary>
/// Owns the deliberately small catalog whose entries prove audited operand
/// semantics. Compatibility path tables do not feed this catalog.
/// </summary>
internal static class AuditedOperandBindingCatalog
{
    // The initial positional-property proof intentionally admits no flags.
    // A generic parameter table cannot establish Select-Object's parameter set.
    private static readonly IReadOnlyDictionary<string, PwshBinding>
        SelectObjectPositionalParameters = CreateParameterInventory(
            Array.Empty<string>(),
            Array.Empty<string>());

    private static readonly IReadOnlyDictionary<string, PwshBinding>
        GetChildItemPositionalParameters = CreateParameterInventory(
            new[]
            {
                "-Attributes", "-Depth", "-ErrorAction", "-ErrorVariable",
                "-Exclude", "-Filter", "-Include", "-InformationAction",
                "-InformationVariable", "-LiteralPath", "-OutBuffer",
                "-OutVariable", "-Path", "-PipelineVariable", "-WarningAction",
                "-WarningVariable",
            },
            new[]
            {
                "-Debug", "-Directory", "-File", "-Force", "-Hidden", "-Name",
                "-ReadOnly", "-Recurse", "-System", "-Verbose",
            });

    private static readonly IReadOnlyDictionary<string, PwshBinding>
        GetContentPositionalParameters = CreateParameterInventory(
            new[]
            {
                "-Delimiter", "-Encoding", "-ErrorAction", "-ErrorVariable",
                "-Exclude", "-Filter", "-Include", "-InformationAction",
                "-InformationVariable", "-LiteralPath", "-OutBuffer",
                "-OutVariable", "-Path", "-PipelineVariable", "-ReadCount",
                "-Stream", "-Tail", "-TotalCount", "-WarningAction",
                "-WarningVariable",
            },
            new[] { "-Debug", "-Force", "-Raw", "-Verbose", "-Wait" });

    private static readonly IReadOnlyList<AuditedOperandBindingCatalogEntry> Entries =
        new[]
        {
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.Bash,
                "cat",
                StringComparison.Ordinal,
                AuditedOperandBindingCategory.AllNonOptionOperands,
                AuditedOperandSemantic.LocalFileSystem),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Get-Content",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactNamedParameterValue,
                AuditedOperandSemantic.LocalFileSystem,
                "-LiteralPath",
                powerShellPathConsumer:
                    ShellResolutionConsumer.PowerShellCmdletLiteralPath),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Get-Content",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactNamedParameterValue,
                AuditedOperandSemantic.LocalFileSystem,
                "-Path",
                powerShellPathConsumer: ShellResolutionConsumer.PowerShellCmdletPath),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Get-Content",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactPositionalValue,
                AuditedOperandSemantic.LocalFileSystem,
                positionalIndex: 0,
                powerShellPathConsumer: ShellResolutionConsumer.PowerShellCmdletPath,
                exactPowerShellParameters: GetContentPositionalParameters),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Get-ChildItem",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactNamedParameterValue,
                AuditedOperandSemantic.LocalFileSystem,
                "-LiteralPath",
                powerShellPathConsumer:
                    ShellResolutionConsumer.PowerShellCmdletLiteralPath),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Get-ChildItem",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactNamedParameterValue,
                AuditedOperandSemantic.LocalFileSystem,
                "-Path",
                powerShellPathConsumer: ShellResolutionConsumer.PowerShellCmdletPath),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Get-ChildItem",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactPositionalValue,
                AuditedOperandSemantic.LocalFileSystem,
                positionalIndex: 0,
                powerShellPathConsumer: ShellResolutionConsumer.PowerShellCmdletPath,
                exactPowerShellParameters: GetChildItemPositionalParameters),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Select-String",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactNamedParameterValue,
                AuditedOperandSemantic.LocalFileSystem,
                "-Path",
                powerShellPathConsumer: ShellResolutionConsumer.PowerShellCmdletPath),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Select-Object",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactNamedParameterValue,
                AuditedOperandSemantic.NonFileSystem,
                "-Property",
                acceptsAuthoredValueGroup: true),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Select-Object",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactPositionalValue,
                AuditedOperandSemantic.NonFileSystem,
                positionalIndex: 0,
                acceptsAuthoredValueGroup: true,
                exactPowerShellParameters: SelectObjectPositionalParameters),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Select-Object",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactNamedParameterValue,
                AuditedOperandSemantic.NonFileSystem,
                "-ExpandProperty"),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Get-ChildItem",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactNamedParameterValue,
                AuditedOperandSemantic.NonFileSystem,
                "-Include",
                acceptsAuthoredValueGroup: true),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.PowerShell,
                "Get-Process",
                StringComparison.OrdinalIgnoreCase,
                AuditedOperandBindingCategory.ExactNamedParameterValue,
                AuditedOperandSemantic.NonFileSystem,
                "-Name",
                acceptsAuthoredValueGroup: true),
            new AuditedOperandBindingCatalogEntry(
                ShellProjectionLanguage.Bash,
                "tr",
                StringComparison.Ordinal,
                AuditedOperandBindingCategory.AllArguments,
                AuditedOperandSemantic.NonFileSystem,
                stopsVerbChain: true),
        };

    internal static IReadOnlyList<AuditedOperandBinding> Bind(
        ShellProjectionLanguage language,
        Clause clause,
        IReadOnlyList<AnalyzedArgument> arguments)
    {
        var bindings = new AuditedOperandBinding[arguments.Count];
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

            var candidate = Bind(entry, arguments);
            for (var argumentIndex = 0;
                 argumentIndex < bindings.Length;
                 argumentIndex++)
            {
                if (candidate[argumentIndex].Category ==
                    AuditedOperandBindingCategory.Unknown)
                {
                    continue;
                }

                bindings[argumentIndex] = bindings[argumentIndex].Category ==
                    AuditedOperandBindingCategory.Unknown ||
                    bindings[argumentIndex] == candidate[argumentIndex]
                        ? candidate[argumentIndex]
                        : default;
            }
        }

        return bindings;
    }

    internal static IReadOnlyList<AuditedOperandBinding> Bind(
        AuditedOperandBindingCatalogEntry entry,
        IReadOnlyList<AnalyzedArgument> arguments)
    {
        var bindings = new AuditedOperandBinding[arguments.Count];
        return entry.Category switch
        {
            AuditedOperandBindingCategory.AllNonOptionOperands =>
                BindAllNonOptionOperands(bindings, entry),
            AuditedOperandBindingCategory.ExactNamedParameterValue =>
                BindExactNamedParameterValue(
                    arguments,
                    bindings,
                    entry),
            AuditedOperandBindingCategory.ExactPositionalValue =>
                BindExactPositionalValue(arguments, bindings, entry),
            AuditedOperandBindingCategory.AllArguments =>
                BindAllArguments(bindings, entry),
            _ => bindings,
        };
    }

    internal static bool StopsVerbChain(
        ShellProjectionLanguage language,
        string canonicalVerb)
    {
        var entry = Find(language, canonicalVerb);
        return entry is not null && entry.StopsVerbChain;
    }

    internal static bool ClassifiesAllArgumentsAsNonFileSystem(
        ShellProjectionLanguage language,
        string canonicalVerb)
    {
        var entry = Find(language, canonicalVerb);
        return entry is
        {
            Category: AuditedOperandBindingCategory.AllArguments,
            Semantic: AuditedOperandSemantic.NonFileSystem,
        };
    }

    private static IReadOnlyList<AuditedOperandBinding> BindAllNonOptionOperands(
        AuditedOperandBinding[] bindings,
        AuditedOperandBindingCatalogEntry entry)
    {
        for (var index = 0; index < bindings.Length; index++)
        {
            bindings[index] = CreateBinding(entry);
        }

        return bindings;
    }

    private static IReadOnlyList<AuditedOperandBinding> BindExactNamedParameterValue(
        IReadOnlyList<AnalyzedArgument> arguments,
        AuditedOperandBinding[] bindings,
        AuditedOperandBindingCatalogEntry entry)
    {
        var expectsValue = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (expectsValue)
            {
                if (!argument.Argument.IsFlag)
                {
                    bindings[index] = CreateBinding(entry);
                }

                expectsValue = false;
            }

            if (argument.Argument.IsFlag && string.Equals(
                    argument.Argument.Raw,
                    entry.ParameterName!,
                    StringComparison.OrdinalIgnoreCase))
            {
                expectsValue = true;
            }
        }

        return bindings;
    }

    private static IReadOnlyList<AuditedOperandBinding> BindExactPositionalValue(
        IReadOnlyList<AnalyzedArgument> arguments,
        AuditedOperandBinding[] bindings,
        AuditedOperandBindingCatalogEntry entry)
    {
        var positionalIndex = 0;
        var expectsNamedValue = false;
        var exactInventory = entry.ExactPowerShellParameters;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index].Argument;
            if (expectsNamedValue)
            {
                if (argument.IsFlag)
                {
                    return new AuditedOperandBinding[arguments.Count];
                }

                expectsNamedValue = false;
                continue;
            }

            if (argument.IsFlag)
            {
                PwshBinding binding;
                if (exactInventory is not null)
                {
                    if (!exactInventory.TryGetValue(argument.Raw, out binding) ||
                        string.Equals(argument.Raw, "-Path", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            argument.Raw,
                            "-LiteralPath",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return new AuditedOperandBinding[arguments.Count];
                    }
                }
                else
                {
                    var parameter = PwshBindingTables.Resolve(
                        entry.CanonicalVerb,
                        argument.Raw);
                    if (!parameter.IsKnown || parameter.IsAmbiguous ||
                        !string.Equals(
                            argument.Raw,
                            parameter.CanonicalName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return new AuditedOperandBinding[arguments.Count];
                    }

                    binding = parameter.Binding;
                }

                expectsNamedValue = binding == PwshBinding.Value;
                continue;
            }

            if (positionalIndex == entry.PositionalIndex)
            {
                bindings[index] = CreateBinding(entry);
            }

            positionalIndex++;
        }

        return expectsNamedValue || exactInventory is not null && positionalIndex != 1
            ? new AuditedOperandBinding[arguments.Count]
            : bindings;
    }

    private static IReadOnlyDictionary<string, PwshBinding> CreateParameterInventory(
        IReadOnlyList<string> valueParameters,
        IReadOnlyList<string> switchParameters)
    {
        var result = new Dictionary<string, PwshBinding>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < valueParameters.Count; index++)
        {
            result.Add(valueParameters[index], PwshBinding.Value);
        }

        for (var index = 0; index < switchParameters.Count; index++)
        {
            result.Add(switchParameters[index], PwshBinding.Switch);
        }

        return result;
    }

    private static IReadOnlyList<AuditedOperandBinding> BindAllArguments(
        AuditedOperandBinding[] bindings,
        AuditedOperandBindingCatalogEntry entry)
    {
        for (var index = 0; index < bindings.Length; index++)
        {
            bindings[index] = CreateBinding(entry);
        }

        return bindings;
    }

    private static AuditedOperandBinding CreateBinding(
        AuditedOperandBindingCatalogEntry entry) => new(
            entry.Category,
            entry.Semantic,
            entry.PowerShellPathConsumer,
            entry.AcceptsAuthoredValueGroup);

    private static AuditedOperandBindingCatalogEntry? Find(
        ShellProjectionLanguage language,
        string canonicalVerb)
    {
        for (var index = 0; index < Entries.Count; index++)
        {
            var entry = Entries[index];
            if (entry.Language == language &&
                string.Equals(canonicalVerb, entry.CanonicalVerb, entry.VerbComparison))
            {
                return entry;
            }
        }

        return null;
    }
}

/// <summary>
/// Combines audited bindings with transform provenance and path resolution.
/// </summary>
internal static class AuthoredOperandSemanticsProjection
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

        var bindings = AuditedOperandBindingCatalog.Bind(language, clause, arguments);
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
            var fileSystemDomain = argument.AuthoredFileSystemValue;
            var nonFileSystemDomain = argument.AuthoredNonFileSystemValue;
            var candidates = Values(argument.AuthoredValue);
            var binding = bindings[index];
            switch (binding.Category, binding.Semantic)
            {
                case (AuditedOperandBindingCategory.AllNonOptionOperands,
                    AuditedOperandSemantic.LocalFileSystem):
                    var allCandidatesArePaths = ClassifyNonOptionOperand(
                        candidates,
                        bashOptionsEnded,
                        out var nextStates);
                    bashOptionsEnded = nextStates;
                    if (allCandidatesArePaths &&
                        TryGetProvenance(argument, clause, provenanceByElement, out var bashValue) &&
                        IsBashTransformSafe(bashValue.Value, candidates) &&
                        TryResolve(
                            ShellProjectionLanguage.Bash,
                            candidates,
                            workingDirectory,
                            powerShellConsumer: null,
                            out var bashDomain))
                    {
                        fileSystemDomain = bashDomain;
                    }

                    break;
                case (AuditedOperandBindingCategory.ExactNamedParameterValue or
                    AuditedOperandBindingCategory.ExactPositionalValue,
                    AuditedOperandSemantic.LocalFileSystem):
                    if (candidates.Count > 0 &&
                        TryGetProvenance(argument, clause, provenanceByElement, out var pwshValue) &&
                        pwshValue.UsesNativeArgumentBinding == false &&
                        IsSingleField(pwshValue.Value) &&
                        binding.PowerShellPathConsumer is not null &&
                        TryResolve(
                            ShellProjectionLanguage.PowerShell,
                            candidates,
                            workingDirectory,
                            binding.PowerShellPathConsumer,
                            out var pwshDomain))
                    {
                        fileSystemDomain = pwshDomain;
                    }

                    break;
                case (AuditedOperandBindingCategory.AllArguments,
                    AuditedOperandSemantic.NonFileSystem):
                    if (candidates.Count > 0 &&
                        TryGetProvenance(argument, clause, provenanceByElement, out var dataValue) &&
                        IsBashTransformSafe(dataValue.Value, candidates))
                    {
                        nonFileSystemDomain = argument.AuthoredValue;
                    }

                    break;
                case (AuditedOperandBindingCategory.ExactNamedParameterValue or
                    AuditedOperandBindingCategory.ExactPositionalValue,
                    AuditedOperandSemantic.NonFileSystem):
                    if (!TryGetProvenance(
                            argument,
                            clause,
                            provenanceByElement,
                            out var pwshData) ||
                        pwshData.UsesNativeArgumentBinding != false)
                    {
                        break;
                    }

                    if (binding.AcceptsAuthoredValueGroup &&
                        pwshData.AuthoredValueGroup is { Count: >= 2 } group &&
                        group.Count <= ShellAnalysisLimits.MaxValueCandidates)
                    {
                        nonFileSystemDomain = new ShellValueDomain.OrderedList(group);
                    }
                    else if (pwshData.AuthoredValueGroup is null &&
                             candidates.Count > 0 &&
                             IsStaticPowerShellData(pwshData.Value))
                    {
                        nonFileSystemDomain = argument.AuthoredValue;
                    }

                    break;
            }

            projected[index] = argument with
            {
                AuthoredFileSystemValue = fileSystemDomain,
                AuthoredNonFileSystemValue = nonFileSystemDomain,
            };
        }

        return HasValidDomains(projected) ? projected : Array.Empty<AnalyzedArgument>();
    }

    internal static bool HasValidDomains(IReadOnlyList<AnalyzedArgument> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var hasFileSystemValue = IsPositiveScalarDomain(
                argument.AuthoredFileSystemValue);
            var hasNonFileSystemValue = IsPositiveNonFileSystemDomain(
                argument.AuthoredNonFileSystemValue);
            if (hasFileSystemValue && hasNonFileSystemValue ||
                argument.Value is ShellValueDomain.OrderedList ||
                argument.AuthoredValue is ShellValueDomain.OrderedList ||
                !IsAuditedScalarDomain(argument.AuthoredFileSystemValue) ||
                !IsAuditedNonFileSystemDomain(argument.AuthoredNonFileSystemValue))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasAuditedBinding(
        IReadOnlyList<AuditedOperandBinding> bindings)
    {
        for (var index = 0; index < bindings.Count; index++)
        {
            if (bindings[index].Category != AuditedOperandBindingCategory.Unknown)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAuditedScalarDomain(ShellValueDomain domain) => domain is
        ShellValueDomain.Unknown or
        ShellValueDomain.Exact or
        ShellValueDomain.FiniteSet;

    private static bool IsAuditedNonFileSystemDomain(ShellValueDomain domain) => domain switch
    {
        ShellValueDomain.Unknown or
        ShellValueDomain.Exact or
        ShellValueDomain.FiniteSet => true,
        ShellValueDomain.OrderedList list =>
            list.Values.Count >= 2 &&
            list.Values.Count <= ShellAnalysisLimits.MaxValueCandidates &&
            !ContainsNull(list.Values),
        _ => false,
    };

    private static bool IsPositiveScalarDomain(ShellValueDomain domain) => domain is
        ShellValueDomain.Exact or
        ShellValueDomain.FiniteSet;

    private static bool IsPositiveNonFileSystemDomain(ShellValueDomain domain) => domain is
        ShellValueDomain.Exact or
        ShellValueDomain.FiniteSet or
        ShellValueDomain.OrderedList;

    private static Dictionary<int, ShellValueElementProvenance> IndexProvenance(
        IReadOnlyList<ShellValueElementProvenance> provenance)
    {
        var indexed = new Dictionary<int, ShellValueElementProvenance>();
        for (var index = 0; index < provenance.Count; index++)
        {
            var current = provenance[index];
            if (current.ClauseElementIndex >= 0 &&
                !indexed.ContainsKey(current.ClauseElementIndex))
            {
                indexed.Add(current.ClauseElementIndex, current);
            }
        }

        return indexed;
    }

    private static bool TryGetProvenance(
        AnalyzedArgument argument,
        Clause clause,
        IReadOnlyDictionary<int, ShellValueElementProvenance> provenance,
        out ShellValueElementProvenance value)
    {
        for (var index = 0; index < clause.Elements.Count; index++)
        {
            if (ReferenceEquals(clause.Elements[index], argument.Element) &&
                provenance.TryGetValue(index, out value!))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool ContainsNull(IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is null)
            {
                return true;
            }
        }

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

    private static bool IsStaticPowerShellData(ShellValue value)
    {
        for (var index = 0; index < value.Fragments.Count; index++)
        {
            var fragment = value.Fragments[index];
            if (fragment.Kind == ShellValueFragmentKind.Literal)
            {
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                fragment.Expansion is not ShellExpansionReference expansion ||
                expansion.Kind != ShellExpansionKind.Glob)
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
        ShellResolutionConsumer? powerShellConsumer,
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
                workingDirectory.Values[0],
                powerShellConsumer);
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
        string workingDirectory,
        ShellResolutionConsumer? powerShellConsumer)
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
                powerShellConsumer ?? ShellResolutionConsumer.PowerShellCmdletLiteralPath),
            _ => (ArgKind.DynamicSkip, null, false),
        };

        return resolution.Kind == ArgKind.Literal &&
               resolution.IsPath &&
               resolution.Resolved is not null
            ? resolution.Resolved
            : null;
    }
}
