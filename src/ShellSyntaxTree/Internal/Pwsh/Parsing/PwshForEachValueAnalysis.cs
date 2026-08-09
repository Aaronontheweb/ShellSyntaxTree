// -----------------------------------------------------------------------
// <copyright file="PwshForEachValueAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using ShellSyntaxTree.Internal.Bash.Verbs;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
using ShellSyntaxTree.Internal.Pwsh.Verbs;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

internal enum PwshIterationCardinality
{
    Never,
    OneOrMore,
    ZeroOrMore,
}

internal sealed record PwshForEachAnalysisPlan(
    string BindingName,
    IReadOnlyList<ShellValueDomain> OrderedCandidates,
    int? AuthoredVisitCount,
    PwshIterationCardinality Cardinality,
    bool RequiresFixedPoint,
    ShellValueDomain Summary);

internal static class PwshForEachValueAnalysis
{
    // PowerShell 7.x fresh-host and contextual built-ins whose assignment can
    // fail, coerce values, or mutate host behavior. The live oracle test keeps
    // the fresh-host portion synchronized as supported 7.x releases evolve.
    private static readonly HashSet<string> IneligibleBindingNames = new(
        new[]
        {
            "_", "args", "ConfirmPreference", "ConsoleFileName", "DebugPreference",
            "EnabledExperimentalFeatures", "Error", "ErrorActionPreference", "ErrorView",
            "Event", "EventArgs", "EventSubscriber", "ExecutionContext", "false",
            "foreach", "FormatEnumerationLimit", "HOME", "Host",
            "InformationPreference", "input", "IsCoreCLR", "IsLinux", "IsMacOS",
            "IsWindows", "LASTEXITCODE", "LogCommandHealthEvent",
            "LogCommandLifecycleEvent", "LogEngineHealthEvent",
            "LogEngineLifecycleEvent", "LogProviderHealthEvent",
            "LogProviderLifecycleEvent", "Matches", "MaximumAliasCount",
            "MaximumDriveCount", "MaximumErrorCount", "MaximumFunctionCount",
            "MaximumHistoryCount", "MaximumVariableCount", "MyInvocation",
            "NestedPromptLevel", "null", "OFS", "OutputEncoding", "PID", "PROFILE",
            "ProgressPreference", "PSBoundParameters", "PSCmdlet", "PSCommandPath",
            "PSCulture", "PSDebugContext", "PSDefaultParameterValues", "PSEdition",
            "PSEmailServer", "PSHOME", "PSItem", "PSModuleAutoLoadingPreference",
            "PSNativeCommandArgumentPassing", "PSNativeCommandUseErrorActionPreference",
            "PSScriptRoot", "PSSenderInfo", "PSSessionApplicationName",
            "PSSessionConfigurationName", "PSSessionOption", "PSStyle", "PSUICulture",
            "PSVersionTable", "PWD", "Sender", "ShellId", "StackTrace", "switch",
            "this", "Transcript", "true", "VerbosePreference", "WarningPreference",
            "WhatIfPreference",
        },
        StringComparer.OrdinalIgnoreCase);

    internal static bool IsEligibleBindingName(string name) =>
        !IneligibleBindingNames.Contains(name);

    internal static PwshForEachAnalysisPlan CapturePlan(
        string bindingName,
        IReadOnlyList<PwshToken> iterableTokens,
        bool isLiteralExpression)
    {
        if (iterableTokens.Count != 1)
        {
            return UnknownPlan(bindingName, PwshIterationCardinality.ZeroOrMore);
        }

        var token = iterableTokens[0];
        if (token.Kind == PwshTokenKind.QuotedString &&
            !token.HasInterpolation &&
            TryGetLiteralValue(token.ResolverValue, out var scalar))
        {
            return new PwshForEachAnalysisPlan(
                bindingName,
                new[] { Exact(scalar) },
                AuthoredVisitCount: 1,
                PwshIterationCardinality.OneOrMore,
                RequiresFixedPoint: false,
                Exact(scalar));
        }

        if (token.Kind == PwshTokenKind.Subexpression &&
            token.Value.StartsWith("@(", StringComparison.Ordinal) &&
            TryCaptureLiteralArray(
                token.Value,
                out var ordered,
                out var authoredVisitCount,
                out var arrayCardinality,
                out var requiresFixedPoint,
                out var summary))
        {
            return new PwshForEachAnalysisPlan(
                bindingName,
                ordered,
                authoredVisitCount,
                arrayCardinality,
                requiresFixedPoint,
                summary);
        }

        if (token.Kind == PwshTokenKind.Word &&
            string.Equals(token.Value, "$null", StringComparison.OrdinalIgnoreCase))
        {
            return new PwshForEachAnalysisPlan(
                bindingName,
                Array.Empty<ShellValueDomain>(),
                AuthoredVisitCount: 0,
                PwshIterationCardinality.Never,
                RequiresFixedPoint: false,
                ShellValueDomain.Unknown);
        }

        return isLiteralExpression
            ? new PwshForEachAnalysisPlan(
                bindingName,
                new[] { ShellValueDomain.Unknown },
                AuthoredVisitCount: 1,
                PwshIterationCardinality.OneOrMore,
                RequiresFixedPoint: false,
                ShellValueDomain.Unknown)
            : UnknownPlan(bindingName, PwshIterationCardinality.ZeroOrMore);
    }

    private static PwshForEachAnalysisPlan UnknownPlan(
        string bindingName,
        PwshIterationCardinality cardinality) =>
        new(
            bindingName,
            Array.Empty<ShellValueDomain>(),
            AuthoredVisitCount: null,
            cardinality,
            RequiresFixedPoint: true,
            ShellValueDomain.Unknown);

    private static bool TryCaptureLiteralArray(
        string raw,
        out IReadOnlyList<ShellValueDomain> orderedCandidates,
        out int authoredVisitCount,
        out PwshIterationCardinality cardinality,
        out bool requiresFixedPoint,
        out ShellValueDomain summary)
    {
        orderedCandidates = Array.Empty<ShellValueDomain>();
        authoredVisitCount = 0;
        cardinality = PwshIterationCardinality.Never;
        requiresFixedPoint = false;
        summary = ShellValueDomain.Unknown;
        var index = 2;
        var end = raw.Length - 1;
        SkipWhitespace(raw, ref index, end);
        if (index == end)
        {
            return true;
        }

        var values = new List<string>();
        var ordered = new List<ShellValueDomain>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var allStrings = true;
        var summaryExceeded = false;
        var count = 0;
        while (index < end)
        {
            var start = index;
            if (!TryReadElement(raw, ref index, end))
            {
                return false;
            }

            count++;
            var element = raw.Substring(start, index - start);
            if (!TryDecodeQuotedElement(element, out var value))
            {
                allStrings = false;
                if (count <= ShellAnalysisLimits.MaxValueCandidates)
                {
                    ordered.Add(ShellValueDomain.Unknown);
                }
            }
            else
            {
                if (count <= ShellAnalysisLimits.MaxValueCandidates)
                {
                    ordered.Add(Exact(value));
                }

                if (!summaryExceeded && distinct.Add(value))
                {
                    if (values.Count == ShellAnalysisLimits.MaxValueCandidates)
                    {
                        summaryExceeded = true;
                    }
                    else
                    {
                        values.Add(value);
                    }
                }
            }

            SkipWhitespace(raw, ref index, end);
            if (index == end)
            {
                break;
            }

            if (raw[index++] != ',')
            {
                return false;
            }

            SkipWhitespace(raw, ref index, end);
        }

        cardinality = count == 0
            ? PwshIterationCardinality.Never
            : PwshIterationCardinality.OneOrMore;
        authoredVisitCount = count;
        requiresFixedPoint = count > ShellAnalysisLimits.MaxValueCandidates;
        orderedCandidates = requiresFixedPoint
            ? Array.Empty<ShellValueDomain>()
            : ordered.ToArray();
        if (allStrings && !summaryExceeded)
        {
            summary = CreateFiniteDomain(values);
        }

        return true;
    }

    private static bool TryReadElement(string raw, ref int index, int end)
    {
        if (index >= end)
        {
            return false;
        }

        if (raw[index] is not ('\'' or '"'))
        {
            while (index < end && raw[index] != ',' && !char.IsWhiteSpace(raw[index]))
            {
                index++;
            }

            return true;
        }

        var quote = raw[index++];
        while (index < end)
        {
            if (quote == '"' && raw[index] == '`' && index + 1 < end)
            {
                index += 2;
                continue;
            }

            if (raw[index] != quote)
            {
                index++;
                continue;
            }

            if (quote == '\'' && index + 1 < end && raw[index + 1] == '\'')
            {
                index += 2;
                continue;
            }

            index++;
            return true;
        }

        return false;
    }

    private static bool TryDecodeQuotedElement(string raw, out string value)
    {
        value = string.Empty;
        if (raw.Length < 2 || raw[0] is not ('\'' or '"'))
        {
            return false;
        }

        var tokens = PwshLexer.Tokenize(raw);
        if (tokens.Count != 1 ||
            tokens[0].Kind != PwshTokenKind.QuotedString ||
            tokens[0].HasInterpolation ||
            !TryGetLiteralValue(tokens[0].ResolverValue, out value))
        {
            value = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryGetLiteralValue(ShellValue? value, out string literal)
    {
        literal = string.Empty;
        if (value is null)
        {
            return false;
        }

        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind != ShellValueFragmentKind.Literal)
            {
                return false;
            }
        }

        literal = value.Decoded;
        return true;
    }

    private static ShellValueDomain Exact(string value) => new()
    {
        Kind = ShellValueDomainKind.Exact,
        Values = new[] { value },
    };

    private static ShellValueDomain CreateFiniteDomain(IReadOnlyList<string> values) =>
        values.Count switch
        {
            0 => ShellValueDomain.Unknown,
            1 => Exact(values[0]),
            _ => new ShellValueDomain
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = Copy(values),
            },
        };

    private static string[] Copy(IReadOnlyList<string> values)
    {
        var copy = new string[values.Count];
        for (var index = 0; index < copy.Length; index++)
        {
            copy[index] = values[index];
        }

        return copy;
    }

    private static void SkipWhitespace(string raw, ref int index, int end)
    {
        while (index < end && char.IsWhiteSpace(raw[index]))
        {
            index++;
        }
    }
}

internal static class PwshPersistentStateMutation
{
    internal static bool TryGetEffect(
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        bool providerLocationUnknown,
        out bool unknownCwd)
    {
        unknownCwd = false;
        var verb = GetCanonicalVerb(clause);
        if (verb is null)
        {
            return false;
        }

        if (HasVariableWritingArgument(verb, clause))
        {
            return true;
        }

        if (IsPowerShellScriptInvocation(clause))
        {
            // An external script executes in this runspace and its body is not
            // available to the parser. It can persist location, command
            // resolution, variable, and process-environment mutations.
            unknownCwd = true;
            return true;
        }

        if (IsProviderStateMutation(
                verb,
                clause,
                effectiveArguments,
                failOnUnproved: true,
                providerLocationUnknown: providerLocationUnknown))
        {
            return true;
        }

        if (!IsUnsupportedForEachStateVerb(verb))
        {
            return false;
        }

        unknownCwd = verb.Equals("Push-Location", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Pop-Location", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("New-PSDrive", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Remove-PSDrive", StringComparison.OrdinalIgnoreCase) ||
            IsInvokeExpression(verb);
        return true;
    }

    internal static bool MayEscapeChildScope(
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments)
    {
        var verb = GetCanonicalVerb(clause);
        if (verb is null)
        {
            return true;
        }

        var hasLocalProviderTarget = false;
        for (var elementIndex = 0; elementIndex < clause.Elements.Count; elementIndex++)
        {
            var element = clause.Elements[elementIndex];
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (IsOpaqueSplat(element))
            {
                return true;
            }

            if (element.Kind is ArgKind.EnvVar or ArgKind.DynamicSkip &&
                DynamicArgumentMayEscapeChildScope(elementIndex, effectiveArguments))
            {
                return true;
            }

            if (TryGetParameterName(element, out var parameter))
            {
                if (parameter.Equals("Global", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (IsAcceptedPrefix(parameter, "Scope", minimumLength: 1) &&
                    ScopeMayEscapeChild(clause.Elements, elementIndex))
                {
                    return true;
                }
            }

            var value = element.Value;
            if (value.IndexOf("global:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("script:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Function:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Environment:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Env:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("$env:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var isAliasTarget =
                value.StartsWith("Alias:", StringComparison.OrdinalIgnoreCase);
            var isVariableTarget =
                value.StartsWith("Variable:", StringComparison.OrdinalIgnoreCase);
            if ((isAliasTarget || isVariableTarget) &&
                !IsProvedChildLocalProviderMutation(
                    verb,
                    isAliasTarget,
                    isVariableTarget))
            {
                return true;
            }

            hasLocalProviderTarget |= isAliasTarget || isVariableTarget;
        }

        if (HasVariableWritingArgument(verb, clause) ||
            verb.Equals("Set-Variable", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("New-Variable", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Remove-Variable", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Clear-Variable", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Set-Alias", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("New-Alias", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Import-Alias", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !hasLocalProviderTarget;
    }

    internal static bool MayEscapeChildRunspaceProcess(
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        bool providerLocationUnknown)
    {
        var verb = GetCanonicalVerb(clause);
        if (verb is null)
        {
            return true;
        }

        if (IsPowerShellScriptInvocation(clause) ||
            IsInvokeExpression(verb) ||
            verb.Equals("Import-Module", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("New-Module", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Import-PSSession", StringComparison.OrdinalIgnoreCase))
        {
            // These commands can execute module or expression code in-process.
            return true;
        }

        return IsProviderStateMutation(
                verb,
                clause,
                effectiveArguments,
                failOnUnproved: true,
                providerLocationUnknown: providerLocationUnknown) &&
            HasMutableOrUnprovedProviderTarget(
                verb,
                clause,
                effectiveArguments,
                failOnUnproved: true,
                selection: StateProviderSelection.Environment,
                providerLocationUnknown: providerLocationUnknown);
    }

    internal static bool MayMutateAutomaticHome(
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        bool providerLocationUnknown)
    {
        var verb = GetCanonicalVerb(clause);
        if (verb is null)
        {
            return true;
        }

        if (IsPowerShellScriptInvocation(clause) ||
            IsVariableCommand(verb) &&
            VariableCommandMayTargetAutomaticHome(
                verb,
                clause,
                effectiveArguments) ||
            IsInvokeExpression(verb) ||
            verb.Equals("Import-Module", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("New-Module", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Import-PSSession", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsProviderStateMutation(
                verb,
                clause,
                effectiveArguments,
                failOnUnproved: true,
                providerLocationUnknown: providerLocationUnknown) &&
            HasMutableOrUnprovedProviderTarget(
                verb,
                clause,
                effectiveArguments,
                failOnUnproved: true,
                selection: StateProviderSelection.Variable,
                providerLocationUnknown: providerLocationUnknown);
    }

    private static bool IsVariableCommand(string verb) =>
        verb.Equals("Set-Variable", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("New-Variable", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Remove-Variable", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Clear-Variable", StringComparison.OrdinalIgnoreCase);

    private static bool VariableCommandMayTargetAutomaticHome(
        string verb,
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments)
    {
        if (!TryGetAliasMutationNames(
                verb,
                clause,
                effectiveArguments,
                out var variableNames))
        {
            return true;
        }

        foreach (var variableName in variableNames)
        {
            var candidate = TrimMatchingQuotes(variableName);
            if (candidate.Length > 0 && candidate[0] == '+')
            {
                candidate = candidate.Substring(1);
            }

            var scopeSeparator = candidate.IndexOf(':');
            if (scopeSeparator > 0 &&
                IsVariableScope(candidate.Substring(0, scopeSeparator)))
            {
                candidate = candidate.Substring(scopeSeparator + 1);
            }

            if (PwshResolver.IsAutomaticHomeVariable(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVariableScope(string value) =>
        value.Equals("Global", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Local", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Private", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Script", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerShellScriptInvocation(Clause clause)
    {
        if (clause.Verb.Tokens.Count != 1)
        {
            return false;
        }

        return clause.Verb.Tokens[0]
            .EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PreservesCommandResolution(Clause clause)
    {
        var verb = GetCanonicalVerb(clause);
        return verb is not null &&
            (verb.Equals("Set-Variable", StringComparison.OrdinalIgnoreCase) ||
             verb.Equals("New-Variable", StringComparison.OrdinalIgnoreCase) ||
             verb.Equals("Remove-Variable", StringComparison.OrdinalIgnoreCase) ||
             verb.Equals("Clear-Variable", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryGetCommandResolutionMutation(
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        bool providerLocationUnknown,
        out bool invalidatesAll,
        out IReadOnlyList<string> commandNames)
    {
        invalidatesAll = false;
        commandNames = Array.Empty<string>();
        var verb = GetCanonicalVerb(clause);
        if (verb is null)
        {
            invalidatesAll = true;
            return true;
        }

        if (IsInvokeExpression(verb) ||
            verb.Equals("Import-Alias", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Import-Module", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("New-Module", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Remove-Module", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Import-PSSession", StringComparison.OrdinalIgnoreCase))
        {
            invalidatesAll = true;
            return true;
        }

        if (verb.Equals("Set-Alias", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("New-Alias", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Remove-Alias", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetAliasMutationNames(
                    verb,
                    clause,
                    effectiveArguments,
                    out commandNames))
            {
                invalidatesAll = true;
            }

            return true;
        }

        if (!IsProviderStateMutation(
                verb,
                clause,
                effectiveArguments,
                failOnUnproved: true,
                providerLocationUnknown: providerLocationUnknown))
        {
            return false;
        }

        if (providerLocationUnknown &&
            HasMutableOrUnprovedProviderTarget(
                verb,
                clause,
                effectiveArguments,
                failOnUnproved: true,
                selection: StateProviderSelection.CommandResolution,
                providerLocationUnknown: true))
        {
            invalidatesAll = true;
            return true;
        }

        if (verb.Equals("Rename-Item", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Move-Item", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("Copy-Item", StringComparison.OrdinalIgnoreCase))
        {
            if (HasMutableOrUnprovedProviderTarget(
                    verb,
                    clause,
                    effectiveArguments,
                    failOnUnproved: true,
                    selection: StateProviderSelection.CommandResolution))
            {
                // Both the source and destination can affect command lookup.
                invalidatesAll = true;
                return true;
            }

            return false;
        }

        return TryGetProviderMutationNames(
            verb,
            clause,
            effectiveArguments,
            out invalidatesAll,
            out commandNames);
    }

    private static bool TryGetAliasMutationNames(
        string verb,
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        out IReadOnlyList<string> commandNames)
    {
        var names = new List<string>();
        var positionalIndex = 0;
        var pendingParameter = AliasParameterRole.None;
        foreach (var elementWithIndex in EnumerateArguments(clause))
        {
            var element = elementWithIndex.Element;
            var elementIndex = elementWithIndex.Index;
            if (IsOpaqueSplat(element))
            {
                commandNames = Array.Empty<string>();
                return false;
            }

            if (element.IsFlag)
            {
                if (!TryGetParameterName(element, out var parameter))
                {
                    commandNames = Array.Empty<string>();
                    return false;
                }

                pendingParameter = ClassifyAliasParameter(parameter);
                var separator = FindParameterValueSeparator(element.Value);
                if (separator >= 0)
                {
                    if (pendingParameter == AliasParameterRole.Name &&
                        !TryAddInlineAliasCommandNames(
                            element,
                            elementIndex,
                            separator,
                            effectiveArguments,
                            names))
                    {
                        commandNames = Array.Empty<string>();
                        return false;
                    }

                    pendingParameter = AliasParameterRole.None;
                }

                continue;
            }

            var selectsName = pendingParameter == AliasParameterRole.Name ||
                pendingParameter == AliasParameterRole.None &&
                (positionalIndex == 0 ||
                 verb.Equals("Remove-Alias", StringComparison.OrdinalIgnoreCase));
            if (pendingParameter == AliasParameterRole.Unknown ||
                selectsName && !TryAddCommandNames(
                    element,
                    elementIndex,
                    effectiveArguments,
                    names))
            {
                commandNames = Array.Empty<string>();
                return false;
            }

            if (pendingParameter == AliasParameterRole.None)
            {
                positionalIndex++;
            }

            pendingParameter = AliasParameterRole.None;
        }

        if (pendingParameter is AliasParameterRole.Name or AliasParameterRole.Unknown ||
            names.Count == 0)
        {
            commandNames = Array.Empty<string>();
            return false;
        }

        commandNames = names.ToArray();
        return true;
    }

    private static bool TryAddInlineAliasCommandNames(
        ClauseElement element,
        int elementIndex,
        int separator,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        List<string> names)
    {
        if (element.Kind is not (ArgKind.DynamicSkip or ArgKind.EnvVar))
        {
            return TryAddCommandName(
                element.Value.Substring(separator + 1),
                names);
        }

        if (!TryGetExactElementValues(
                element,
                elementIndex,
                effectiveArguments,
                out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            var valueSeparator = FindParameterValueSeparator(value);
            var commandName = valueSeparator >= 0
                ? value.Substring(valueSeparator + 1)
                : value;
            if (!TryAddCommandName(commandName, names))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetProviderMutationNames(
        string verb,
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        out bool invalidatesAll,
        out IReadOnlyList<string> commandNames)
    {
        invalidatesAll = false;
        var names = new List<string>();
        var positionalIndex = 0;
        var pendingParameter = ProviderParameterRole.None;
        foreach (var elementWithIndex in EnumerateArguments(clause))
        {
            var element = elementWithIndex.Element;
            var elementIndex = elementWithIndex.Index;
            if (IsOpaqueSplat(element))
            {
                invalidatesAll = true;
                commandNames = Array.Empty<string>();
                return true;
            }

            if (element.IsFlag)
            {
                var separator = element.Value.IndexOf(':');
                var parameter = separator < 0
                    ? element.Value
                    : element.Value.Substring(0, separator);
                pendingParameter = ClassifyProviderParameter(parameter);
                if (separator >= 0)
                {
                    if (CanSelectProvider(pendingParameter) &&
                        !TryAddProviderCommandNames(
                            element,
                            elementIndex,
                            effectiveArguments,
                            names,
                            out _))
                    {
                        invalidatesAll = true;
                        commandNames = Array.Empty<string>();
                        return true;
                    }

                    pendingParameter = ProviderParameterRole.None;
                }

                continue;
            }

            var couldSelectProvider = CanSelectProvider(pendingParameter) ||
                pendingParameter == ProviderParameterRole.None &&
                IsProviderTargetPosition(verb, positionalIndex);
            if (couldSelectProvider &&
                !TryAddProviderCommandNames(
                    element,
                    elementIndex,
                    effectiveArguments,
                    names,
                    out _))
            {
                invalidatesAll = true;
                commandNames = Array.Empty<string>();
                return true;
            }

            if (pendingParameter == ProviderParameterRole.None)
            {
                positionalIndex++;
            }

            pendingParameter = ProviderParameterRole.None;
        }

        if (CanSelectProvider(pendingParameter))
        {
            invalidatesAll = true;
            commandNames = Array.Empty<string>();
            return true;
        }

        commandNames = names.ToArray();
        return names.Count > 0;
    }

    private static bool TryAddProviderCommandNames(
        ClauseElement element,
        int elementIndex,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        List<string> names,
        out bool selectedCommandProvider)
    {
        selectedCommandProvider = false;
        if (!TryGetExactElementValues(
                element,
                elementIndex,
                effectiveArguments,
                out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (!TryGetCommandNameFromProviderPath(value, out var commandName))
            {
                continue;
            }

            selectedCommandProvider = true;
            if (!TryAddCommandName(commandName, names))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAddCommandNames(
        ClauseElement element,
        int elementIndex,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        List<string> names)
    {
        if (!TryGetExactElementValues(
                element,
                elementIndex,
                effectiveArguments,
                out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (!TryAddCommandName(value, names))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetExactElementValues(
        ClauseElement element,
        int elementIndex,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        out IReadOnlyList<string> values)
    {
        foreach (var effective in effectiveArguments)
        {
            if (effective.ClauseElementIndex != elementIndex)
            {
                continue;
            }

            if (effective.Value.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet) ||
                effective.Value.Values.Count == 0)
            {
                values = Array.Empty<string>();
                return false;
            }

            values = effective.Value.Values;
            return true;
        }

        if (element.Kind is ArgKind.DynamicSkip or ArgKind.EnvVar)
        {
            values = Array.Empty<string>();
            return false;
        }

        values = new[] { element.Value };
        return true;
    }

    private static bool TryAddCommandName(string value, List<string> names)
    {
        value = TrimMatchingQuotes(value);
        if (value.Length == 0 ||
            value.IndexOfAny(new[] { '*', '?', '[', ']', ',', '/', '\\' }) >= 0)
        {
            return false;
        }

        foreach (var existing in names)
        {
            if (existing.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (names.Count == ShellAnalysisLimits.MaxValueCandidates)
        {
            return false;
        }

        names.Add(value);
        return true;
    }

    private static bool TryGetCommandNameFromProviderPath(
        string value,
        out string commandName)
    {
        value = TrimMatchingQuotes(value);
        if (value.Length > 1 && value[0] == '-')
        {
            var separator = FindParameterValueSeparator(value);
            if (separator < 0 || separator + 1 == value.Length)
            {
                commandName = string.Empty;
                return false;
            }

            value = TrimMatchingQuotes(value.Substring(separator + 1));
        }

        var providerPath = value;
        if (!HasCommandResolutionStateProviderPrefix(providerPath))
        {
            var providerStart = Math.Max(
                value.LastIndexOf('\\'),
                value.LastIndexOf('/')) + 1;
            providerPath = value.Substring(providerStart);
        }

        var isFunction = providerPath.StartsWith(
            "Function:",
            StringComparison.OrdinalIgnoreCase);
        var prefixLength = providerPath.StartsWith(
            "Alias:",
            StringComparison.OrdinalIgnoreCase)
                ? "Alias:".Length
                : isFunction
                    ? "Function:".Length
                    : 0;
        if (prefixLength == 0 || prefixLength == providerPath.Length)
        {
            commandName = string.Empty;
            return false;
        }

        commandName = providerPath.Substring(prefixLength);
        if (commandName.Length > 0 && commandName[0] == ':')
        {
            // PowerShell accepts Provider::name as the provider-qualified form.
            // Additional colons belong to the authored command name.
            commandName = commandName.Substring(1);
        }

        commandName = commandName.TrimStart('/', '\\');
        if (isFunction)
        {
            commandName = TrimFunctionScope(commandName);
        }

        return true;
    }

    private static string TrimFunctionScope(string commandName)
    {
        var separator = commandName.IndexOf(':');
        if (separator <= 0)
        {
            return commandName;
        }

        var scope = commandName.Substring(0, separator);
        return scope.Equals("Global", StringComparison.OrdinalIgnoreCase) ||
            scope.Equals("Script", StringComparison.OrdinalIgnoreCase) ||
            scope.Equals("Local", StringComparison.OrdinalIgnoreCase) ||
            scope.Equals("Private", StringComparison.OrdinalIgnoreCase)
                ? commandName.Substring(separator + 1)
                : commandName;
    }

    private static int FindParameterValueSeparator(string value)
    {
        var colon = value.IndexOf(':', 1);
        var equals = value.IndexOf('=', 1);
        if (colon < 0)
        {
            return equals;
        }

        return equals < 0 ? colon : Math.Min(colon, equals);
    }

    private static AliasParameterRole ClassifyAliasParameter(string parameter)
    {
        if (IsAcceptedPrefix(parameter, "Name", minimumLength: 1))
        {
            return AliasParameterRole.Name;
        }

        if (IsAcceptedPrefix(parameter, "Value", minimumLength: 1) ||
            IsAcceptedPrefix(parameter, "Description", minimumLength: 1) ||
            IsAcceptedPrefix(parameter, "Option", minimumLength: 1) ||
            IsAcceptedPrefix(parameter, "Scope", minimumLength: 1) ||
            parameter.Equals("ErrorAction", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("ErrorVariable", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("InformationAction", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("InformationVariable", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("OutBuffer", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("OutVariable", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("PipelineVariable", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("ProgressAction", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("WarningAction", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("WarningVariable", StringComparison.OrdinalIgnoreCase))
        {
            return AliasParameterRole.NonName;
        }

        if (parameter.Equals("Force", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("PassThru", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("WhatIf", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("Confirm", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("Verbose", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            return AliasParameterRole.None;
        }

        return AliasParameterRole.Unknown;
    }

    private static IEnumerable<(ClauseElement Element, int Index)> EnumerateArguments(
        Clause clause)
    {
        for (var index = 0; index < clause.Elements.Count; index++)
        {
            if (clause.Elements[index].Role == ClauseElementRole.Argument)
            {
                yield return (clause.Elements[index], index);
            }
        }
    }

    internal static bool HasVariableWritingArgument(Clause clause)
    {
        var verb = GetCanonicalVerb(clause);
        return verb is null || HasVariableWritingArgument(verb, clause);
    }

    private static string? GetCanonicalVerb(Clause clause)
    {
        var verb = clause.Verb.CanonicalVerb ??
            (clause.Verb.Tokens.Count == 0 ? null : clause.Verb.Tokens[0]);
        if (verb is not null &&
            PwshExecutionRegionBindingCatalog.TryResolveStaticCommandName(
                verb,
                out var canonicalName))
        {
            return canonicalName;
        }

        return verb;
    }

    private static bool IsProvedChildLocalProviderMutation(
        string verb,
        bool isAliasTarget,
        bool isVariableTarget)
    {
        if (verb.Equals("Set-Item", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("New-Item", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return isVariableTarget && !isAliasTarget &&
            (verb.Equals("Clear-Item", StringComparison.OrdinalIgnoreCase) ||
             verb.Equals("Set-Content", StringComparison.OrdinalIgnoreCase));
    }

    private static bool DynamicArgumentMayEscapeChildScope(
        int elementIndex,
        IReadOnlyList<EffectiveArgument> effectiveArguments)
    {
        foreach (var effective in effectiveArguments)
        {
            if (effective.ClauseElementIndex != elementIndex)
            {
                continue;
            }

            if (effective.Value.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet) ||
                effective.Value.Values.Count == 0)
            {
                return true;
            }

            foreach (var value in effective.Value.Values)
            {
                if (ValueMayEscapeChildScope(value))
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    private static bool ValueMayEscapeChildScope(string value) =>
        value.IndexOf("global:", StringComparison.OrdinalIgnoreCase) >= 0 ||
        value.IndexOf("script:", StringComparison.OrdinalIgnoreCase) >= 0 ||
        value.IndexOf("Function:", StringComparison.OrdinalIgnoreCase) >= 0 ||
        value.IndexOf("Environment:", StringComparison.OrdinalIgnoreCase) >= 0 ||
        value.IndexOf("Env:", StringComparison.OrdinalIgnoreCase) >= 0 ||
        value.IndexOf("$env:", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool ScopeMayEscapeChild(
        IReadOnlyList<ClauseElement> elements,
        int parameterIndex)
    {
        var parameter = elements[parameterIndex].Value;
        var separator = parameter.IndexOf(':', 1);
        if (separator < 0)
        {
            separator = parameter.IndexOf('=', 1);
        }

        if (separator >= 0)
        {
            return !IsChildLocalScope(parameter.Substring(separator + 1));
        }

        for (var index = parameterIndex + 1; index < elements.Count; index++)
        {
            var value = elements[index];
            if (value.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            return value.IsFlag || !IsChildLocalScope(value.Value);
        }

        return true;
    }

    private static bool IsChildLocalScope(string value)
    {
        value = TrimMatchingQuotes(value);
        return value.Equals("Local", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Private", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.Ordinal);
    }

    private static bool HasVariableWritingArgument(string verb, Clause clause)
    {
        foreach (var element in clause.Elements)
        {
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (IsOpaqueSplat(element))
            {
                return true;
            }

            if (!TryGetParameterName(element, out var parameter))
            {
                continue;
            }

            if (IsCommonVariableWriter(parameter) ||
                IsCommandSpecificVariableWriter(verb, parameter))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOpaqueSplat(ClauseElement element) =>
        element.Kind == ArgKind.DynamicSkip &&
        element.Raw.Length > 1 &&
        element.Raw[0] == '@' &&
        element.Raw[1] is not ('(' or '{' or '\'' or '"');

    private static bool TryGetParameterName(
        ClauseElement element,
        out string parameter)
    {
        parameter = string.Empty;
        if (!element.IsFlag || element.Value.Length < 2 || element.Value[0] != '-')
        {
            return false;
        }

        var end = element.Value.Length;
        var colon = element.Value.IndexOf(':', 1);
        if (colon >= 0)
        {
            end = colon;
        }

        var equals = element.Value.IndexOf('=', 1);
        if (equals >= 0 && equals < end)
        {
            end = equals;
        }

        if (end <= 1)
        {
            return false;
        }

        parameter = element.Value.Substring(1, end - 1);
        return true;
    }

    private static bool IsCommonVariableWriter(string parameter) =>
        parameter.Equals("ov", StringComparison.OrdinalIgnoreCase) ||
        parameter.Equals("pv", StringComparison.OrdinalIgnoreCase) ||
        parameter.Equals("ev", StringComparison.OrdinalIgnoreCase) ||
        parameter.Equals("wv", StringComparison.OrdinalIgnoreCase) ||
        parameter.Equals("iv", StringComparison.OrdinalIgnoreCase) ||
        IsAcceptedPrefix(parameter, "OutVariable", minimumLength: 4) ||
        IsAcceptedPrefix(parameter, "PipelineVariable", minimumLength: 2) ||
        IsAcceptedPrefix(parameter, "ErrorVariable", minimumLength: 6) ||
        IsAcceptedPrefix(parameter, "WarningVariable", minimumLength: 8) ||
        IsAcceptedPrefix(parameter, "InformationVariable", minimumLength: 12);

    private static bool IsCommandSpecificVariableWriter(
        string verb,
        string parameter)
    {
        if (verb.Equals("Tee-Object", StringComparison.OrdinalIgnoreCase))
        {
            return IsAcceptedPrefix(parameter, "Variable", minimumLength: 1);
        }

        if (verb.Equals("Import-LocalizedData", StringComparison.OrdinalIgnoreCase))
        {
            return IsAcceptedPrefix(parameter, "BindingVariable", minimumLength: 2) ||
                IsAcceptedPrefix(parameter, "Variable", minimumLength: 1);
        }

        if (verb.Equals("Invoke-RestMethod", StringComparison.OrdinalIgnoreCase))
        {
            return parameter.Equals("SV", StringComparison.OrdinalIgnoreCase) ||
                parameter.Equals("RHV", StringComparison.OrdinalIgnoreCase) ||
                IsAcceptedPrefix(parameter, "SessionVariable", minimumLength: 2) ||
                IsAcceptedPrefix(parameter, "ResponseHeadersVariable", minimumLength: 4) ||
                IsAcceptedPrefix(parameter, "StatusCodeVariable", minimumLength: 2);
        }

        return verb.Equals("Invoke-WebRequest", StringComparison.OrdinalIgnoreCase) &&
            (parameter.Equals("SV", StringComparison.OrdinalIgnoreCase) ||
             IsAcceptedPrefix(parameter, "SessionVariable", minimumLength: 2));
    }

    private static bool IsAcceptedPrefix(
        string parameter,
        string fullName,
        int minimumLength) =>
        parameter.Length >= minimumLength &&
        parameter.Length <= fullName.Length &&
        fullName.StartsWith(parameter, StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnsupportedForEachStateVerb(string verb) =>
        verb.Equals("Push-Location", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Pop-Location", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Set-Variable", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("New-Variable", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Remove-Variable", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Clear-Variable", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Set-Alias", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("New-Alias", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Remove-Alias", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Import-Alias", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Import-Module", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("New-Module", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Remove-Module", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Import-PSSession", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("New-PSDrive", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Remove-PSDrive", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("Set-StrictMode", StringComparison.OrdinalIgnoreCase) ||
        IsInvokeExpression(verb);

    private static bool IsInvokeExpression(string verb) =>
        verb.Equals("Invoke-Expression", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("iex", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals(
            "Microsoft.PowerShell.Utility\\Invoke-Expression",
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsProviderStateMutation(string verb, Clause clause) =>
        IsProviderStateMutation(
            verb,
            clause,
            Array.Empty<EffectiveArgument>(),
            failOnUnproved: false);

    private static bool IsProviderStateMutation(
        string verb,
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        bool failOnUnproved,
        bool providerLocationUnknown = false)
    {
        if (!verb.Equals("Set-Item", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("New-Item", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Remove-Item", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Rename-Item", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Move-Item", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Copy-Item", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Clear-Item", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Set-Content", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Add-Content", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Clear-Content", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Remove-Content", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return HasMutableOrUnprovedProviderTarget(
            verb,
            clause,
            effectiveArguments,
            failOnUnproved,
            providerLocationUnknown: providerLocationUnknown);
    }

    private static bool HasMutableOrUnprovedProviderTarget(
        string verb,
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        bool failOnUnproved,
        StateProviderSelection selection = StateProviderSelection.AnyMutable,
        bool providerLocationUnknown = false)
    {
        var positionalIndex = 0;
        var pendingParameter = ProviderParameterRole.None;
        for (var elementIndex = 0; elementIndex < clause.Elements.Count; elementIndex++)
        {
            var element = clause.Elements[elementIndex];
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (element.IsFlag)
            {
                var separator = element.Value.IndexOf(':');
                var parameter = separator < 0
                    ? element.Value
                    : element.Value.Substring(0, separator);
                pendingParameter = ClassifyProviderParameter(parameter);
                if (separator >= 0)
                {
                    if (CanSelectProvider(pendingParameter) &&
                        IsMutableOrUnprovedProviderTarget(
                            element,
                            elementIndex,
                            effectiveArguments,
                            failOnUnproved,
                            selection,
                            providerLocationUnknown))
                    {
                        return true;
                    }

                    pendingParameter = ProviderParameterRole.None;
                }

                continue;
            }

            var couldSelectProvider = CanSelectProvider(pendingParameter) ||
                pendingParameter == ProviderParameterRole.None &&
                IsProviderTargetPosition(verb, positionalIndex);
            if (couldSelectProvider &&
                IsMutableOrUnprovedProviderTarget(
                    element,
                    elementIndex,
                    effectiveArguments,
                    failOnUnproved,
                    selection,
                    providerLocationUnknown))
            {
                return true;
            }

            if (pendingParameter == ProviderParameterRole.None)
            {
                positionalIndex++;
            }

            pendingParameter = ProviderParameterRole.None;
        }

        return failOnUnproved && CanSelectProvider(pendingParameter);
    }

    private static bool CanSelectProvider(ProviderParameterRole parameter) =>
        parameter is ProviderParameterRole.Target or ProviderParameterRole.Unknown;

    private static bool IsMutableOrUnprovedProviderTarget(
        ClauseElement element,
        int elementIndex,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        bool failOnUnproved,
        StateProviderSelection selection,
        bool providerLocationUnknown)
    {
        if (IsSelectedStateProviderPath(element.Value, selection) ||
            IsSelectedStateProviderPath(element.Raw, selection) ||
            element.Resolved is not null &&
            IsSelectedStateProviderPath(element.Resolved, selection))
        {
            return true;
        }

        if (providerLocationUnknown &&
            TargetDependsOnProviderLocation(
                element,
                elementIndex,
                effectiveArguments))
        {
            return true;
        }

        if (!failOnUnproved ||
            element.Kind is ArgKind.Literal or ArgKind.Glob or ArgKind.Tilde)
        {
            return false;
        }

        foreach (var effective in effectiveArguments)
        {
            if (effective.ClauseElementIndex != elementIndex)
            {
                continue;
            }

            if (effective.Value.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
            {
                return true;
            }

            foreach (var value in effective.Value.Values)
            {
                if (IsSelectedStateProviderPath(value, selection))
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    private static bool TargetDependsOnProviderLocation(
        ClauseElement element,
        int elementIndex,
        IReadOnlyList<EffectiveArgument> effectiveArguments)
    {
        if (IsProviderLocationIndependent(element.Value) ||
            IsProviderLocationIndependent(element.Raw) ||
            element.Resolved is not null &&
            IsProviderLocationIndependent(element.Resolved))
        {
            return false;
        }

        foreach (var effective in effectiveArguments)
        {
            if (effective.ClauseElementIndex != elementIndex)
            {
                continue;
            }

            if (effective.Value.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet) ||
                effective.Value.Values.Count == 0)
            {
                return true;
            }

            foreach (var value in effective.Value.Values)
            {
                if (!IsProviderLocationIndependent(value))
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    private static bool IsProviderLocationIndependent(string value)
    {
        value = TrimMatchingQuotes(value);
        if (value.Length > 1 && value[0] == '-')
        {
            var separator = FindParameterValueSeparator(value);
            if (separator >= 0 && separator + 1 < value.Length)
            {
                value = TrimMatchingQuotes(value.Substring(separator + 1));
            }
        }

        if (value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith("\\", StringComparison.Ordinal))
        {
            return true;
        }

        return value.IndexOf("::", StringComparison.Ordinal) > 0 ||
            value.IndexOf(':') > 0;
    }

    private static bool IsSelectedStateProviderPath(
        string value,
        StateProviderSelection selection) => selection switch
        {
            StateProviderSelection.Environment =>
                IsEnvironmentStateProviderPath(value),
            StateProviderSelection.Variable =>
                IsVariableStateProviderPath(value),
            StateProviderSelection.CommandResolution =>
                IsCommandResolutionStateProviderPath(value),
            _ => IsMutableStateProviderPath(value),
        };

    private static ProviderParameterRole ClassifyProviderParameter(string parameter)
    {
        if (parameter.Equals("-Path", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-LiteralPath", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-LP", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-PSPath", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Destination", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderParameterRole.Target;
        }

        if (parameter.Equals("-Value", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Target", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-NewName", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Name", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Filter", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Include", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Exclude", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Credential", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Stream", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderParameterRole.NonTarget;
        }

        if (parameter.Equals("-Force", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Recurse", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-PassThru", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-WhatIf", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-Confirm", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderParameterRole.None;
        }

        return ProviderParameterRole.Unknown;
    }

    private static bool IsProviderTargetPosition(string verb, int position) =>
        position == 0 ||
        position == 1 &&
        (verb.Equals("Copy-Item", StringComparison.OrdinalIgnoreCase) ||
         verb.Equals("Move-Item", StringComparison.OrdinalIgnoreCase));

    private static bool IsMutableStateProviderPath(string value)
    {
        value = TrimMatchingQuotes(value);
        if (HasMutableStateProviderPrefix(value))
        {
            return true;
        }

        if (value.Length > 1 && value[0] == '-')
        {
            var parameterSeparator = value.IndexOf(':');
            if (parameterSeparator > 1 && parameterSeparator + 1 < value.Length)
            {
                var inlineValue = TrimMatchingQuotes(
                    value.Substring(parameterSeparator + 1));
                if (HasMutableStateProviderPrefix(inlineValue))
                {
                    return true;
                }
            }
        }

        var providerStart = Math.Max(
            value.LastIndexOf('\\'),
            value.LastIndexOf('/')) + 1;
        return providerStart > 0 &&
            HasMutableStateProviderPrefix(value.Substring(providerStart));
    }

    private static bool HasMutableStateProviderPrefix(string providerPath) =>
        providerPath.StartsWith("Alias:", StringComparison.OrdinalIgnoreCase) ||
            providerPath.StartsWith("Function:", StringComparison.OrdinalIgnoreCase) ||
            providerPath.StartsWith("Variable:", StringComparison.OrdinalIgnoreCase) ||
            providerPath.StartsWith("Environment:", StringComparison.OrdinalIgnoreCase) ||
            providerPath.StartsWith("Env:", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnvironmentStateProviderPath(string value)
    {
        value = TrimMatchingQuotes(value);
        if (HasEnvironmentStateProviderPrefix(value))
        {
            return true;
        }

        if (value.Length > 1 && value[0] == '-')
        {
            var parameterSeparator = value.IndexOf(':');
            if (parameterSeparator > 1 && parameterSeparator + 1 < value.Length)
            {
                var inlineValue = TrimMatchingQuotes(
                    value.Substring(parameterSeparator + 1));
                if (HasEnvironmentStateProviderPrefix(inlineValue))
                {
                    return true;
                }
            }
        }

        var providerStart = Math.Max(
            value.LastIndexOf('\\'),
            value.LastIndexOf('/')) + 1;
        return providerStart > 0 &&
            HasEnvironmentStateProviderPrefix(value.Substring(providerStart));
    }

    private static bool HasEnvironmentStateProviderPrefix(string providerPath) =>
        providerPath.StartsWith("Environment:", StringComparison.OrdinalIgnoreCase) ||
            providerPath.StartsWith("Env:", StringComparison.OrdinalIgnoreCase);

    private static bool IsVariableStateProviderPath(string value)
    {
        value = TrimMatchingQuotes(value);
        if (value.StartsWith("Variable:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Length > 1 && value[0] == '-')
        {
            var parameterSeparator = value.IndexOf(':');
            if (parameterSeparator > 1 && parameterSeparator + 1 < value.Length)
            {
                return TrimMatchingQuotes(value.Substring(parameterSeparator + 1))
                    .StartsWith("Variable:", StringComparison.OrdinalIgnoreCase);
            }
        }

        var providerStart = Math.Max(
            value.LastIndexOf('\\'),
            value.LastIndexOf('/')) + 1;
        return providerStart > 0 &&
            value.Substring(providerStart)
                .StartsWith("Variable:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandResolutionStateProviderPath(string value)
    {
        value = TrimMatchingQuotes(value);
        if (HasCommandResolutionStateProviderPrefix(value))
        {
            return true;
        }

        if (value.Length > 1 && value[0] == '-')
        {
            var parameterSeparator = value.IndexOf(':');
            if (parameterSeparator > 1 && parameterSeparator + 1 < value.Length)
            {
                var inlineValue = TrimMatchingQuotes(
                    value.Substring(parameterSeparator + 1));
                if (HasCommandResolutionStateProviderPrefix(inlineValue))
                {
                    return true;
                }
            }
        }

        var providerStart = Math.Max(
            value.LastIndexOf('\\'),
            value.LastIndexOf('/')) + 1;
        return providerStart > 0 &&
            HasCommandResolutionStateProviderPrefix(value.Substring(providerStart));
    }

    private static bool HasCommandResolutionStateProviderPrefix(string providerPath) =>
        providerPath.StartsWith("Alias:", StringComparison.OrdinalIgnoreCase) ||
        providerPath.StartsWith("Function:", StringComparison.OrdinalIgnoreCase);

    private static string TrimMatchingQuotes(string value) =>
        value.Length >= 2 &&
        value[0] is '\'' or '"' &&
        value[value.Length - 1] == value[0]
            ? value.Substring(1, value.Length - 2)
            : value;

    private enum ProviderParameterRole
    {
        None,
        Target,
        NonTarget,
        Unknown,
    }

    private enum AliasParameterRole
    {
        None,
        Name,
        NonName,
        Unknown,
    }

    private enum StateProviderSelection
    {
        AnyMutable,
        Environment,
        Variable,
        CommandResolution,
    }
}

internal sealed class PwshForEachValueAnalyzer
{
    private const int MaxLoopAnalysisTransitions = 4096;

    private readonly PwshParserOptions _options;
    private readonly Func<SimpleCommandSyntax, CommandOccurrenceFacts> _factsFactory;
    private readonly Func<ForEachSyntax, PwshForEachAnalysisPlan?> _planFactory;
    private readonly IReadOnlyList<Clause> _incompleteClauses;
    private readonly Dictionary<Clause, CommandOccurrenceFacts> _facts =
        new(ClauseReferenceComparer.Instance);
    private readonly Dictionary<Clause, IReadOnlyList<ExecutionRegionSyntax>>
        _executionRegions = new(ClauseReferenceComparer.Instance);
    private readonly List<AnalysisContext> _invocationRedirectContexts = new();
    private bool _isComplete = true;
    private int _remainingLoopAnalysisTransitions = MaxLoopAnalysisTransitions;
    private long _executionRegionEffectCount;
    private long _nonRegionStateMutationCount;
    private long _locationStateMutationCount;
    private long _childScopeEscapeRiskCount;
    private long _childRunspaceProcessEscapeRiskCount;
    private bool _pipelineStageMayReceiveInput;
    private bool _pipelineStageEffectsMayReachRegionBodies;

    private PwshForEachValueAnalyzer(
        PwshParserOptions options,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
        Func<ForEachSyntax, PwshForEachAnalysisPlan?> planFactory,
        IReadOnlyList<Clause> incompleteClauses)
    {
        _options = options;
        _factsFactory = factsFactory;
        _planFactory = planFactory;
        _incompleteClauses = incompleteClauses;
    }

    internal static bool TryAnalyze(
        ShellBlockSyntax syntax,
        PwshParserOptions options,
        string? initialWorkingDirectory,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
        Func<ForEachSyntax, PwshForEachAnalysisPlan?> planFactory,
        IReadOnlyList<Clause> incompleteClauses,
        out ShellBlockSyntax analyzedSyntax,
        out Func<SimpleCommandSyntax, CommandOccurrenceFacts> analyzedFacts)
    {
        var analyzer = new PwshForEachValueAnalyzer(
            options,
            factsFactory,
            planFactory,
            incompleteClauses);
        analyzer.AnalyzeBlock(
            syntax,
            new AnalysisContext(
                initialWorkingDirectory,
                canPromote: options.InitialStateMode ==
                    PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
                hasConstrainedCommandResolutionBaseline:
                    options.InitialStateMode ==
                    PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
                commandResolutionInvalidated: false,
                commandResolutionInvalidatedBeyondTrackedMutations: false,
                allRunspaceCommandResolutionMayReachProcessMutation: false,
                Array.Empty<string>(),
                processWideStateInvalidated: false,
                configuredHomeAvailable: true,
                homeEnvironmentAvailable: options.InitialStateMode ==
                    PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
                automaticHomeValueAvailable: options.InitialStateMode ==
                    PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
                new List<BindingFrame>()));
        if (!analyzer._isComplete)
        {
            analyzedSyntax = syntax;
            analyzedFacts = analyzer.GetFacts;
            return false;
        }

        var rewrittenFacts = new Dictionary<Clause, CommandOccurrenceFacts>(
            ClauseReferenceComparer.Instance);
        analyzedSyntax = analyzer.RewriteBlock(syntax, rewrittenFacts);
        analyzedFacts = simple => rewrittenFacts.TryGetValue(simple.Clause, out var facts)
            ? facts
            : new CommandOccurrenceFacts();
        return true;
    }

    private PwshFlowResult AnalyzeNode(ShellSyntaxNode node, AnalysisContext input) =>
        node switch
        {
            ShellBlockSyntax block => AnalyzeBlock(block, input),
            SimpleCommandSyntax simple => AnalyzeSimple(simple, input),
            CommandListSyntax list => AnalyzeList(list, input),
            PipelineSyntax pipeline => AnalyzePipeline(pipeline, input),
            GroupSyntax group => AnalyzeGroup(group, input),
            ForEachSyntax forEach => AnalyzeForEach(forEach, input),
            CommandSubstitutionSyntax substitution => AnalyzeSubstitution(substitution, input),
            ExecutionRegionSyntax region => AnalyzeExecutionRegion(region, input),
            _ => AnalyzeUnsupportedNode(input),
        };

    private PwshFlowResult AnalyzeUnsupportedNode(AnalysisContext input)
    {
        _nonRegionStateMutationCount++;
        return PwshFlowResult.Both(input.Invalidate(unknownCwd: true));
    }

    private PwshFlowResult AnalyzeBlock(ShellBlockSyntax block, AnalysisContext input)
    {
        var flow = PwshFlowResult.Both(input);
        foreach (var statement in block.Statements)
        {
            if (flow.JoinedState is not AnalysisContext current)
            {
                break;
            }

            flow = AnalyzeNode(statement, current);
        }

        return flow;
    }

    private PwshFlowResult AnalyzeSimple(SimpleCommandSyntax simple, AnalysisContext input)
    {
        var current = input;
        foreach (var substitution in simple.Substitutions)
        {
            var substitutionFlow = AnalyzeBlock(substitution.Body, current);
            if (substitutionFlow.JoinedState is not AnalysisContext substitutionExit)
            {
                return new PwshFlowResult(null, null);
            }

            current = substitutionExit;
        }

        var source = _factsFactory(simple);
        var isForEachIncomplete = ContainsReference(_incompleteClauses, simple.Clause);
        var effective = CreateEffectiveArguments(
            source.ValueProvenance,
            simple.Clause,
            current,
            includeUnresolved: isForEachIncomplete ||
                current.CommandResolutionInvalidated);
        var redirects = AnalyzeRedirects(source, current);
        var mayPromote = current.CanPromote &&
            source.HasCompleteValueProvenance &&
            simple.Substitutions.Count == 0 &&
            !simple.Clause.IsCommandStringWrapped &&
            current.CanResolveEveryExpansion(source.ValueProvenance);
        RecordFacts(
            simple,
            current,
            source,
            effective,
            redirects,
            isForEachIncomplete,
            mayPromote);

        var hasCommandResolutionMutation =
            PwshPersistentStateMutation.TryGetCommandResolutionMutation(
                simple.Clause,
                effective,
                providerLocationUnknown: current.WorkingDirectory is null,
                out var invalidatesAllCommandNames,
                out var mutatedCommandNames);
        var commandIdentityMayMutateState = simple.Clause.Verb.IsDynamic ||
            !IsCommandIdentityProven(simple.Clause, current);
        var mayMutateAutomaticHome =
            commandIdentityMayMutateState ||
            PwshPersistentStateMutation.MayMutateAutomaticHome(
                simple.Clause,
                effective,
                providerLocationUnknown: current.WorkingDirectory is null);
        var preservesCommandResolution =
            !commandIdentityMayMutateState &&
            PwshPersistentStateMutation.PreservesCommandResolution(simple.Clause);
        if (commandIdentityMayMutateState)
        {
            // An unproved invocation can resolve to arbitrary in-process code
            // regardless of its authored verb, including through ambient
            // aliases, functions, modules, or observed mutations.
            _nonRegionStateMutationCount++;
            _childScopeEscapeRiskCount++;
            _childRunspaceProcessEscapeRiskCount++;
            return ApplyExecutionRegionEffect(
                simple,
                current,
                PwshFlowResult.Both(ApplyPersistentStateInvalidation(
                    current,
                    unknownCwd: true,
                    hasCommandResolutionMutation: true,
                    invalidatesAllCommandNames: true,
                    mutatedCommandNames: Array.Empty<string>(),
                    mayEscapeChildRunspaceProcess: true,
                    mayMutateAutomaticHome: true,
                    preservesCommandResolution: false)));
        }

        var location = AnalyzeSetLocation(simple, current);
        if (location is not null)
        {
            _nonRegionStateMutationCount++;
            _locationStateMutationCount++;
            if (PwshPersistentStateMutation.TryGetEffect(
                    simple.Clause,
                    effective,
                    providerLocationUnknown: current.WorkingDirectory is null,
                    out var locationEffectUnknownCwd))
            {
                var mayEscapeChildRunspaceProcess =
                    PwshPersistentStateMutation.MayEscapeChildRunspaceProcess(
                        simple.Clause,
                        effective,
                        providerLocationUnknown: current.WorkingDirectory is null);
                if (PwshPersistentStateMutation.MayEscapeChildScope(
                        simple.Clause,
                        effective))
                {
                    _childScopeEscapeRiskCount++;
                }

                if (mayEscapeChildRunspaceProcess)
                {
                    _childRunspaceProcessEscapeRiskCount++;
                }

                var flow = location.Value;
                return ApplyExecutionRegionEffect(simple, current, new PwshFlowResult(
                    flow.OnSuccess is AnalysisContext success
                        ? ApplyPersistentStateInvalidation(
                            success,
                            locationEffectUnknownCwd,
                            hasCommandResolutionMutation,
                            invalidatesAllCommandNames,
                            mutatedCommandNames,
                            mayEscapeChildRunspaceProcess,
                            mayMutateAutomaticHome,
                            preservesCommandResolution)
                        : null,
                    flow.OnFailure is AnalysisContext failure
                        ? ApplyPersistentStateInvalidation(
                            failure,
                            locationEffectUnknownCwd,
                            hasCommandResolutionMutation,
                            invalidatesAllCommandNames,
                            mutatedCommandNames,
                            mayEscapeChildRunspaceProcess,
                            mayMutateAutomaticHome,
                            preservesCommandResolution)
                        : null));
            }

            return ApplyExecutionRegionEffect(simple, current, location.Value);
        }

        if (PwshPersistentStateMutation.TryGetEffect(
                simple.Clause,
                effective,
                providerLocationUnknown: current.WorkingDirectory is null,
                out var unknownCwd))
        {
            _nonRegionStateMutationCount++;
            var mayEscapeChildRunspaceProcess =
                PwshPersistentStateMutation.MayEscapeChildRunspaceProcess(
                    simple.Clause,
                    effective,
                    providerLocationUnknown: current.WorkingDirectory is null);
            if (PwshPersistentStateMutation.MayEscapeChildScope(
                    simple.Clause,
                    effective))
            {
                _childScopeEscapeRiskCount++;
            }

            if (mayEscapeChildRunspaceProcess)
            {
                _childRunspaceProcessEscapeRiskCount++;
            }

            return ApplyExecutionRegionEffect(
                simple,
                current,
                PwshFlowResult.Both(ApplyPersistentStateInvalidation(
                    current,
                    unknownCwd,
                    hasCommandResolutionMutation,
                    invalidatesAllCommandNames,
                    mutatedCommandNames,
                    mayEscapeChildRunspaceProcess,
                    mayMutateAutomaticHome,
                    preservesCommandResolution)));
        }

        return ApplyExecutionRegionEffect(
            simple,
            current,
            PwshFlowResult.Both(
                mayMutateAutomaticHome
                    ? current.WithoutAutomaticHomeValue()
                    : current));
    }

    private static AnalysisContext ApplyPersistentStateInvalidation(
        AnalysisContext input,
        bool unknownCwd,
        bool hasCommandResolutionMutation,
        bool invalidatesAllCommandNames,
        IReadOnlyList<string> mutatedCommandNames,
        bool mayEscapeChildRunspaceProcess,
        bool mayMutateAutomaticHome,
        bool preservesCommandResolution)
    {
        var invalidated = input.Invalidate(
            unknownCwd,
            invalidateCommandResolution:
                !hasCommandResolutionMutation && !preservesCommandResolution);
        if (hasCommandResolutionMutation)
        {
            invalidated = invalidated.WithRunspaceCommandResolutionProcessRisk(
                invalidatesAllCommandNames,
                mutatedCommandNames);
        }

        if (mayMutateAutomaticHome)
        {
            invalidated = invalidated.WithoutAutomaticHomeValue();
        }

        return mayEscapeChildRunspaceProcess
            ? invalidated.WithProcessWideStateInvalidated()
            : invalidated;
    }

    private PwshFlowResult ApplyExecutionRegionEffect(
        SimpleCommandSyntax simple,
        AnalysisContext receiverInput,
        PwshFlowResult flow)
    {
        if (simple.ExecutionRegions.Count == 0)
        {
            return flow;
        }

        var receiverIdentityProven = IsExecutionRegionReceiverIdentityProven(
            simple.Clause,
            receiverInput);
        var binding = PwshExecutionRegionBindingCatalog.Bind(
            simple.Clause,
            receiverIdentityProven);
        if (binding.Status == PwshExecutionRegionBindingStatus.ProvedData)
        {
            RecordExecutionRegions(
                simple.Clause,
                Array.Empty<ExecutionRegionSyntax>(),
                simple.ExecutionRegions);
            return flow;
        }

        if (!IsSupportedExecutionRegionReceiver(binding) ||
            !TryApplyExecutionRegionBindings(simple, binding, out var regions))
        {
            RecordExecutionRegions(
                simple.Clause,
                simple.ExecutionRegions,
                simple.ExecutionRegions);
            _executionRegionEffectCount++;
            if (receiverIdentityProven &&
                binding.Receiver == PwshExecutionRegionReceiver.StartJob)
            {
                return flow;
            }

            AnalyzeUnreachableRegions(
                simple.ExecutionRegions,
                receiverInput
                    .Invalidate(unknownCwd: true)
                    .WithoutConfiguredHome());
            _childScopeEscapeRiskCount++;
            return new PwshFlowResult(
                flow.OnSuccess is AnalysisContext success
                    ? success
                        .Invalidate(unknownCwd: true)
                        .WithoutAutomaticHomeValue()
                    : null,
                flow.OnFailure is AnalysisContext failure
                    ? failure
                        .Invalidate(unknownCwd: true)
                        .WithoutAutomaticHomeValue()
                    : null);
        }

        RecordExecutionRegions(simple.Clause, regions, simple.ExecutionRegions);
        if (flow.JoinedState is not AnalysisContext regionInput)
        {
            return flow;
        }

        if (binding.ParameterSet is PwshExecutionRegionParameterSet.ForEachScriptBlock or
            PwshExecutionRegionParameterSet.WhereScriptBlock)
        {
            return AnalyzePipelineCallbackRegions(
                binding,
                regions,
                regionInput,
                flow);
        }

        if (binding.ParameterSet == PwshExecutionRegionParameterSet.ForEachParallel)
        {
            return AnalyzeForEachParallel(
                binding,
                regions,
                receiverInput,
                flow);
        }

        if (binding.ParameterSet == PwshExecutionRegionParameterSet.InvokeInProcess)
        {
            return AnalyzeInProcessInvokeCommand(
                binding,
                regions[0],
                regionInput,
                flow);
        }

        if (binding.ParameterSet == PwshExecutionRegionParameterSet.InvokeRemote)
        {
            return AnalyzeRemoteInvokeCommand(
                regions,
                receiverInput,
                flow);
        }

        if (binding.ParameterSet == PwshExecutionRegionParameterSet.NewModuleScriptBlock)
        {
            var bodyInput = PwshPersistentStateMutation.HasVariableWritingArgument(
                simple.Clause)
                ? receiverInput.Invalidate(unknownCwd: false)
                : receiverInput;
            return AnalyzeNewModule(regions[0], bodyInput, flow);
        }

        if (binding.ParameterSet == PwshExecutionRegionParameterSet.StartJobScriptBlock)
        {
            return AnalyzeStartJob(simple, binding, regions, receiverInput, flow);
        }

        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var bodyFlow = AnalyzeExecutionRegionBody(regions[0].Body, regionInput);
        _executionRegionEffectCount = executionRegionEffectCount + 1;
        _nonRegionStateMutationCount = nonRegionStateMutationCount;
        return bodyFlow.JoinedState is AnalysisContext bodyExit
            ? PwshFlowResult.Both(bodyExit)
            : flow;
    }

    private PwshFlowResult AnalyzeNewModule(
        ExecutionRegionSyntax region,
        AnalysisContext input,
        PwshFlowResult hostFlow)
    {
        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var locationStateMutationCount = _locationStateMutationCount;
        var childScopeEscapeRiskCount = _childScopeEscapeRiskCount;
        var body = AnalyzeExecutionRegionBody(region.Body, input);
        var locationMutated = _locationStateMutationCount > locationStateMutationCount;
        var childScopeMayEscape =
            _childScopeEscapeRiskCount > childScopeEscapeRiskCount;
        _executionRegionEffectCount = executionRegionEffectCount + 1;
        _nonRegionStateMutationCount = nonRegionStateMutationCount;
        _childScopeEscapeRiskCount = childScopeEscapeRiskCount +
            (childScopeMayEscape ? 1 : 0);

        var restoredExit = AnalysisContext.JoinNullable(
            RestoreChildScopeExit(
                body.OnSuccess,
                input,
                locationMutated,
                childScopeMayEscape),
            RestoreChildScopeExit(
                body.OnFailure,
                input,
                locationMutated,
                childScopeMayEscape));
        if (restoredExit is not AnalysisContext bodyExit ||
            hostFlow.JoinedState is not AnalysisContext hostExit)
        {
            return hostFlow;
        }

        return PwshFlowResult.Both(hostExit.WithCwd(bodyExit.WorkingDirectory));
    }

    private PwshFlowResult AnalyzeStartJob(
        SimpleCommandSyntax simple,
        PwshExecutionRegionBindingResult binding,
        IReadOnlyList<ExecutionRegionSyntax> regions,
        AnalysisContext receiverInput,
        PwshFlowResult hostFlow)
    {
        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var locationStateMutationCount = _locationStateMutationCount;
        var childScopeEscapeRiskCount = _childScopeEscapeRiskCount;
        var childRunspaceProcessEscapeRiskCount =
            _childRunspaceProcessEscapeRiskCount;
        try
        {
            var child = CreateStartJobInput(simple, binding, receiverInput);
            if (TryAnalyzeRegionPhase(
                    regions,
                    ExecutionRegionPhase.Initialization,
                    child,
                    out var initialized))
            {
                TryAnalyzeRegionPhase(
                    regions,
                    ExecutionRegionPhase.Main,
                    initialized,
                    out _);
            }
        }
        finally
        {
            _executionRegionEffectCount = executionRegionEffectCount + regions.Count;
            _nonRegionStateMutationCount = nonRegionStateMutationCount;
            _locationStateMutationCount = locationStateMutationCount;
            _childScopeEscapeRiskCount = childScopeEscapeRiskCount;
            _childRunspaceProcessEscapeRiskCount =
                childRunspaceProcessEscapeRiskCount;
        }

        return hostFlow;
    }

    private PwshFlowResult AnalyzeForEachParallel(
        PwshExecutionRegionBindingResult binding,
        IReadOnlyList<ExecutionRegionSyntax> regions,
        AnalysisContext receiverInput,
        PwshFlowResult hostFlow)
    {
        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var locationStateMutationCount = _locationStateMutationCount;
        var childScopeEscapeRiskCount = _childScopeEscapeRiskCount;
        var childRunspaceProcessEscapeRiskCount =
            _childRunspaceProcessEscapeRiskCount;
        var childRunspaceProcessMayEscape = false;
        try
        {
            var childInput = receiverInput.CreateChildRunspaceInput();
            if (TryAnalyzeRegionSequence(regions, childInput, out var childExit))
            {
                var firstVisitMayEscape =
                    _childRunspaceProcessEscapeRiskCount >
                    childRunspaceProcessEscapeRiskCount;
                if (firstVisitMayEscape ||
                    !binding.HasUseNewRunspace &&
                    childExit.HasRunspaceCommandResolutionProcessRisk)
                {
                    // Pooled runspaces retain local command-resolution mutation;
                    // fresh runspaces still share process-wide child effects.
                    var repeatedInput = childInput
                        .Invalidate(unknownCwd: firstVisitMayEscape)
                        .WithConditionalProcessWideStateInvalidation(
                            firstVisitMayEscape)
                        .WithoutBindings();
                    if (firstVisitMayEscape)
                    {
                        repeatedInput = repeatedInput.WithoutConfiguredHome();
                    }

                    if (!binding.HasUseNewRunspace)
                    {
                        repeatedInput = repeatedInput
                            .WithRunspaceCommandResolutionProcessRisk(childExit);
                    }

                    TryAnalyzeRegionSequence(
                        regions,
                        repeatedInput,
                        out _);
                }
            }

            childRunspaceProcessMayEscape =
                _childRunspaceProcessEscapeRiskCount >
                childRunspaceProcessEscapeRiskCount;
        }
        finally
        {
            _executionRegionEffectCount = executionRegionEffectCount + regions.Count;
            _nonRegionStateMutationCount = nonRegionStateMutationCount;
            _locationStateMutationCount = locationStateMutationCount;
            _childScopeEscapeRiskCount = childScopeEscapeRiskCount;
            _childRunspaceProcessEscapeRiskCount =
                childRunspaceProcessEscapeRiskCount +
                (childRunspaceProcessMayEscape ? 1 : 0);
        }

        if (!childRunspaceProcessMayEscape)
        {
            return hostFlow;
        }

        return new PwshFlowResult(
            hostFlow.OnSuccess is AnalysisContext success
                ? success
                    .Invalidate(unknownCwd: true)
                    .WithProcessWideStateInvalidated()
                : null,
            hostFlow.OnFailure is AnalysisContext failure
                ? failure
                    .Invalidate(unknownCwd: true)
                    .WithProcessWideStateInvalidated()
                : null);
    }

    private AnalysisContext CreateStartJobInput(
        SimpleCommandSyntax simple,
        PwshExecutionRegionBindingResult binding,
        AnalysisContext receiverInput)
    {
        var child = receiverInput.CreateChildProcessInput();
        if (binding.WorkingDirectoryElementIndex is not int elementIndex)
        {
            return child;
        }

        if (!TryGetElementValue(
                simple,
                elementIndex,
                binding.WorkingDirectoryValueOffset,
                out var targetValue))
        {
            return child.WithCwd(workingDirectory: null);
        }

        if (receiverInput.TryEvaluateValue(targetValue, out var domain) &&
            domain.Kind == ShellValueDomainKind.Exact &&
            domain.Values.Count == 1)
        {
            targetValue = ShellValue.Literal(domain.Values[0]);
        }

        var options = new PwshParserOptions
        {
            HomeDirectory = _options.HomeDirectory,
            WorkingDirectory = receiverInput.WorkingDirectory,
            InitialStateMode = _options.InitialStateMode,
        };
        var resolved = PwshResolver.Resolve(
            targetValue,
            treatAsPath: true,
            options,
            workingDirectoryUnknown: true,
            ShellResolutionConsumer.PowerShellCmdletPath);
        return child.WithCwd(
            resolved.IsPath &&
            resolved.Resolved is not null
                ? resolved.Resolved
                : null);
    }

    private PwshFlowResult AnalyzeInProcessInvokeCommand(
        PwshExecutionRegionBindingResult binding,
        ExecutionRegionSyntax region,
        AnalysisContext input,
        PwshFlowResult fallback)
    {
        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var locationStateMutationCount = _locationStateMutationCount;
        var childScopeEscapeRiskCount = _childScopeEscapeRiskCount;
        var body = AnalyzeExecutionRegionBody(region.Body, input);
        var locationMutated = _locationStateMutationCount > locationStateMutationCount;
        var childScopeMayEscape =
            _childScopeEscapeRiskCount > childScopeEscapeRiskCount;
        _executionRegionEffectCount = executionRegionEffectCount + 1;
        _nonRegionStateMutationCount = nonRegionStateMutationCount;

        if (binding.HasNoNewScope)
        {
            return body.JoinedState is AnalysisContext bodyExit
                ? PwshFlowResult.Both(bodyExit)
                : fallback;
        }

        _childScopeEscapeRiskCount = childScopeEscapeRiskCount +
            (childScopeMayEscape ? 1 : 0);
        var restoredExit = AnalysisContext.JoinNullable(
            RestoreChildScopeExit(
                body.OnSuccess,
                input,
                locationMutated,
                childScopeMayEscape),
            RestoreChildScopeExit(
                body.OnFailure,
                input,
                locationMutated,
                childScopeMayEscape));
        return restoredExit is AnalysisContext joined
            ? PwshFlowResult.Both(joined)
            : fallback;
    }

    private PwshFlowResult AnalyzeRemoteInvokeCommand(
        IReadOnlyList<ExecutionRegionSyntax> regions,
        AnalysisContext receiverInput,
        PwshFlowResult hostFlow)
    {
        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var locationStateMutationCount = _locationStateMutationCount;
        var childScopeEscapeRiskCount = _childScopeEscapeRiskCount;
        var childRunspaceProcessEscapeRiskCount =
            _childRunspaceProcessEscapeRiskCount;
        try
        {
            TryAnalyzeRegionSequence(
                regions,
                receiverInput.CreateRemoteInput(),
                out _);
        }
        finally
        {
            _executionRegionEffectCount = executionRegionEffectCount + regions.Count;
            _nonRegionStateMutationCount = nonRegionStateMutationCount;
            _locationStateMutationCount = locationStateMutationCount;
            _childScopeEscapeRiskCount = childScopeEscapeRiskCount;
            _childRunspaceProcessEscapeRiskCount =
                childRunspaceProcessEscapeRiskCount;
        }

        return hostFlow;
    }

    private PwshFlowResult AnalyzePipelineCallbackRegions(
        PwshExecutionRegionBindingResult binding,
        IReadOnlyList<ExecutionRegionSyntax> regions,
        AnalysisContext input,
        PwshFlowResult fallback)
    {
        var parameterSet = binding.ParameterSet;
        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var current = input;
        if (parameterSet == PwshExecutionRegionParameterSet.ForEachScriptBlock &&
            !TryAnalyzeRegionPhase(regions, ExecutionRegionPhase.Begin, current, out current))
        {
            return fallback;
        }

        var processRegions = RegionsForPhase(
            regions,
            parameterSet == PwshExecutionRegionParameterSet.WhereScriptBlock
                ? ExecutionRegionPhase.Filter
                : ExecutionRegionPhase.Process);
        var mayReceiveInput = _pipelineStageMayReceiveInput ||
            binding.HasExplicitInputObject;
        if (parameterSet == PwshExecutionRegionParameterSet.WhereScriptBlock &&
            !mayReceiveInput)
        {
            AnalyzeUnreachableRegions(processRegions, current);
        }
        else if (mayReceiveInput)
        {
            if (!TryAnalyzeZeroOrMoreRegions(processRegions, current, out current))
            {
                return fallback;
            }
        }
        else if (!TryAnalyzeRegionSequence(processRegions, current, out current))
        {
            return fallback;
        }

        if (parameterSet == PwshExecutionRegionParameterSet.ForEachScriptBlock &&
            !TryAnalyzeRegionPhase(regions, ExecutionRegionPhase.End, current, out current))
        {
            return fallback;
        }

        _executionRegionEffectCount = executionRegionEffectCount + regions.Count;
        _nonRegionStateMutationCount = nonRegionStateMutationCount;
        return PwshFlowResult.Both(current);
    }

    private bool TryAnalyzeRegionPhase(
        IReadOnlyList<ExecutionRegionSyntax> regions,
        ExecutionRegionPhase phase,
        AnalysisContext input,
        out AnalysisContext output) =>
        TryAnalyzeRegionSequence(RegionsForPhase(regions, phase), input, out output);

    private bool TryAnalyzeRegionSequence(
        IReadOnlyList<ExecutionRegionSyntax> regions,
        AnalysisContext input,
        out AnalysisContext output)
    {
        output = input;
        for (var index = 0; index < regions.Count; index++)
        {
            var body = AnalyzeExecutionRegionBody(regions[index].Body, output);
            if (body.JoinedState is not AnalysisContext bodyExit)
            {
                return false;
            }

            output = bodyExit;
        }

        return true;
    }

    private bool TryAnalyzeZeroOrMoreRegions(
        IReadOnlyList<ExecutionRegionSyntax> regions,
        AnalysisContext input,
        out AnalysisContext output)
    {
        var exits = input;
        var head = input;
        for (var iteration = 0;
             iteration <= ShellAnalysisLimits.MaxValueCandidates;
             iteration++)
        {
            if (!TryConsumeLoopAnalysisTransition() ||
                !TryAnalyzeRegionSequence(regions, head, out var bodyExit))
            {
                output = input;
                return false;
            }

            exits = AnalysisContext.Join(exits, bodyExit);
            var nextHead = AnalysisContext.Join(head, bodyExit);
            if (head.StateEquals(nextHead))
            {
                output = exits;
                return true;
            }

            head = nextHead;
        }

        output = AnalysisContext.Widen(input, exits);
        return true;
    }

    private void AnalyzeUnreachableRegions(
        IReadOnlyList<ExecutionRegionSyntax> regions,
        AnalysisContext input)
    {
        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var locationStateMutationCount = _locationStateMutationCount;
        var childScopeEscapeRiskCount = _childScopeEscapeRiskCount;
        var childRunspaceProcessEscapeRiskCount =
            _childRunspaceProcessEscapeRiskCount;
        TryAnalyzeRegionSequence(regions, input, out _);
        _executionRegionEffectCount = executionRegionEffectCount;
        _nonRegionStateMutationCount = nonRegionStateMutationCount;
        _locationStateMutationCount = locationStateMutationCount;
        _childScopeEscapeRiskCount = childScopeEscapeRiskCount;
        _childRunspaceProcessEscapeRiskCount =
            childRunspaceProcessEscapeRiskCount;
    }

    private PwshFlowResult AnalyzeExecutionRegionBody(
        ShellBlockSyntax body,
        AnalysisContext input)
    {
        var enclosingStageMayReceiveInput = _pipelineStageMayReceiveInput;
        _pipelineStageMayReceiveInput = false;
        var bodyInput = _pipelineStageEffectsMayReachRegionBodies
            ? input
                .Invalidate(unknownCwd: true)
                .WithoutAutomaticHomeValue()
            : input;
        var flow = AnalyzeBlock(body, bodyInput);
        _pipelineStageMayReceiveInput = enclosingStageMayReceiveInput;
        return flow;
    }

    private static IReadOnlyList<ExecutionRegionSyntax> RegionsForPhase(
        IReadOnlyList<ExecutionRegionSyntax> regions,
        ExecutionRegionPhase phase)
    {
        var matching = new List<ExecutionRegionSyntax>();
        for (var index = 0; index < regions.Count; index++)
        {
            if (regions[index].Phase == phase)
            {
                matching.Add(regions[index]);
            }
        }

        return matching;
    }

    private static bool IsCommandIdentityProven(
        Clause clause,
        AnalysisContext input)
    {
        if (!input.HasConstrainedCommandResolutionBaseline)
        {
            return false;
        }

        if (!input.CommandResolutionInvalidated)
        {
            return true;
        }

        return !input.CommandResolutionInvalidatedBeyondTrackedMutations &&
            !input.MayResolveCommandToProcessMutation(clause);
    }

    private static bool IsExecutionRegionReceiverIdentityProven(
        Clause clause,
        AnalysisContext input) =>
        input.HasConstrainedCommandResolutionBaseline &&
        !input.CommandResolutionInvalidatedBeyondTrackedMutations &&
        !input.MayResolveCommandToProcessMutation(clause);

    private void RecordExecutionRegions(
        Clause clause,
        IReadOnlyList<ExecutionRegionSyntax> regions,
        IReadOnlyList<ExecutionRegionSyntax> unknownFallback)
    {
        if (!_executionRegions.TryGetValue(clause, out var prior))
        {
            _executionRegions.Add(clause, regions);
            return;
        }

        if (!HaveSameCompleteRegionFacts(prior, regions))
        {
            _executionRegions[clause] = unknownFallback;
        }
    }

    private static bool HaveSameCompleteRegionFacts(
        IReadOnlyList<ExecutionRegionSyntax> left,
        IReadOnlyList<ExecutionRegionSyntax> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].HostClauseElementIndex != right[index].HostClauseElementIndex ||
                left[index].Phase != right[index].Phase ||
                left[index].Timing != right[index].Timing ||
                left[index].Cardinality != right[index].Cardinality)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSupportedExecutionRegionReceiver(
        PwshExecutionRegionBindingResult binding)
    {
        if (binding.Status != PwshExecutionRegionBindingStatus.ProvedExecution)
        {
            return false;
        }

        return binding.ParameterSet switch
        {
            PwshExecutionRegionParameterSet.StartJobScriptBlock =>
                !binding.HasExplicitPSVersion &&
                AllBindingsAreCompleteWithTiming(
                    binding.Bindings,
                    ExecutionRegionTiming.Concurrent),
            PwshExecutionRegionParameterSet.ForEachParallel =>
                AllBindingsAreCompleteWithTiming(
                    binding.Bindings,
                    ExecutionRegionTiming.Concurrent),
            PwshExecutionRegionParameterSet.InvokeRemote =>
                AllBindingsAreComplete(binding.Bindings),
            PwshExecutionRegionParameterSet.MeasureExpression or
                PwshExecutionRegionParameterSet.TraceExpression or
                PwshExecutionRegionParameterSet.InvokeInProcess or
                PwshExecutionRegionParameterSet.NewModuleScriptBlock or
                PwshExecutionRegionParameterSet.ForEachScriptBlock or
                PwshExecutionRegionParameterSet.WhereScriptBlock =>
                AllBindingsAreCompleteWithTiming(
                    binding.Bindings,
                    ExecutionRegionTiming.Synchronous),
            _ => false,
        };
    }

    private static bool AllBindingsAreComplete(
        IReadOnlyList<PwshExecutionRegionBinding> bindings)
    {
        if (bindings.Count == 0)
        {
            return false;
        }

        for (var index = 0; index < bindings.Count; index++)
        {
            if (!bindings[index].IsComplete)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllBindingsAreCompleteWithTiming(
        IReadOnlyList<PwshExecutionRegionBinding> bindings,
        ExecutionRegionTiming timing)
    {
        if (bindings.Count == 0)
        {
            return false;
        }

        for (var index = 0; index < bindings.Count; index++)
        {
            if (!bindings[index].IsComplete ||
                bindings[index].Timing != timing)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyExecutionRegionBindings(
        SimpleCommandSyntax simple,
        PwshExecutionRegionBindingResult binding,
        out IReadOnlyList<ExecutionRegionSyntax> regions)
    {
        var resolved = new ExecutionRegionSyntax[binding.Bindings.Count];
        for (var bindingIndex = 0;
             bindingIndex < binding.Bindings.Count;
             bindingIndex++)
        {
            var current = binding.Bindings[bindingIndex];
            ExecutionRegionSyntax? source = null;
            for (var regionIndex = 0;
                 regionIndex < simple.ExecutionRegions.Count;
                 regionIndex++)
            {
                if (simple.ExecutionRegions[regionIndex].HostClauseElementIndex ==
                    current.HostClauseElementIndex)
                {
                    source = simple.ExecutionRegions[regionIndex];
                    break;
                }
            }

            if (source is null)
            {
                regions = Array.Empty<ExecutionRegionSyntax>();
                return false;
            }

            resolved[bindingIndex] = source with
            {
                Phase = current.Phase,
                Timing = current.Timing,
                Cardinality = current.Cardinality,
            };
        }

        regions = resolved;
        return true;
    }

    private void RecordFacts(
        SimpleCommandSyntax simple,
        AnalysisContext input,
        CommandOccurrenceFacts source,
        IReadOnlyList<EffectiveArgument> effective,
        IReadOnlyList<RedirectAnalysis> redirects,
        bool isForEachIncomplete,
        bool mayPromote)
    {
        var current = new CommandOccurrenceFacts
        {
            EffectiveArguments = effective,
            WorkingDirectory = input.ToWorkingDirectoryDomain(),
            Redirects = redirects,
            RedirectTargetProvenance = source.RedirectTargetProvenance,
            CwdPathDependencies = source.CwdPathDependencies,
            ValueProvenance = source.ValueProvenance,
            HasCompleteValueProvenance = source.HasCompleteValueProvenance,
            IsComplete = source.IsComplete &&
                IsCommandIdentityProven(simple.Clause, input) &&
                (!isForEachIncomplete || mayPromote),
        };
        if (!_facts.TryGetValue(simple.Clause, out var prior))
        {
            _facts.Add(simple.Clause, current);
            return;
        }

        _facts[simple.Clause] = new CommandOccurrenceFacts
        {
            EffectiveArguments = JoinEffectiveArguments(
                prior.EffectiveArguments,
                current.EffectiveArguments),
            WorkingDirectory = JoinWorkingDirectories(
                prior.WorkingDirectory,
                current.WorkingDirectory),
            Redirects = JoinRedirects(prior.Redirects, current.Redirects),
            RedirectTargetProvenance = source.RedirectTargetProvenance,
            CwdPathDependencies = source.CwdPathDependencies,
            ValueProvenance = source.ValueProvenance,
            HasCompleteValueProvenance = source.HasCompleteValueProvenance,
            IsComplete = prior.IsComplete && current.IsComplete,
        };
    }

    private IReadOnlyList<RedirectAnalysis> AnalyzeRedirects(
        CommandOccurrenceFacts source,
        AnalysisContext input)
    {
        if (source.Redirects.Count == 0 ||
            source.RedirectTargetProvenance.Count == 0)
        {
            return source.Redirects;
        }

        var rewritten = new RedirectAnalysis[source.Redirects.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = source.Redirects[index];
        }

        foreach (var provenance in source.RedirectTargetProvenance)
        {
            if (provenance.RedirectIndex < 0 ||
                provenance.RedirectIndex >= rewritten.Length ||
                !rewritten[provenance.RedirectIndex].IsPathRelevant)
            {
                continue;
            }

            // Parser-time redirect compatibility values are only a baseline.
            // Clear them before occurrence-local evaluation so mutations or
            // remote execution cannot leave stale local values behind.
            rewritten[provenance.RedirectIndex] =
                rewritten[provenance.RedirectIndex] with
                {
                    Target = ShellValueDomain.Unknown,
                };
            if (!TryGetRedirectEvaluationContext(
                    provenance.InvocationScopeDepth,
                    input,
                    out var evaluationContext))
            {
                continue;
            }

            if (!(evaluationContext.TryEvaluateValue(
                      provenance.Value,
                      out var domain) ||
                  TryAnalyzeParserKnownValue(
                      provenance.Value,
                      usesNativeBinding: true,
                      evaluationContext,
                      out domain)) ||
                domain.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
            {
                continue;
            }

            var values = new List<string>();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            var resolutionOptions = _options with
            {
                WorkingDirectory = evaluationContext.WorkingDirectory,
            };
            var resolvedAll = true;
            foreach (var candidate in domain.Values)
            {
                if (!evaluationContext.ConfiguredHomeAvailable &&
                    IsRedirectHomePath(candidate))
                {
                    resolvedAll = false;
                    break;
                }

                var resolved = PwshResolver.Resolve(
                    ShellValue.Literal(candidate),
                    treatAsPath: true,
                    resolutionOptions,
                    workingDirectoryUnknown:
                        evaluationContext.WorkingDirectory is null,
                    ShellResolutionConsumer.PowerShellRedirect);
                if (resolved.Resolved is null || !resolved.IsPath)
                {
                    resolvedAll = false;
                    break;
                }

                if (distinct.Add(resolved.Resolved))
                {
                    values.Add(resolved.Resolved);
                }
            }

            rewritten[provenance.RedirectIndex] =
                rewritten[provenance.RedirectIndex] with
                {
                    Target = resolvedAll
                        ? CreateDomain(values)
                        : ShellValueDomain.Unknown,
                };
        }

        return rewritten;
    }

    private bool TryGetRedirectEvaluationContext(
        int invocationScopeDepth,
        AnalysisContext input,
        out AnalysisContext context)
    {
        if (invocationScopeDepth == 0)
        {
            context = input;
            return true;
        }

        if (invocationScopeDepth > 0 &&
            invocationScopeDepth <= _invocationRedirectContexts.Count)
        {
            context = _invocationRedirectContexts[
                _invocationRedirectContexts.Count - invocationScopeDepth];
            return true;
        }

        context = input;
        return false;
    }

    private static bool IsRedirectHomePath(string value) =>
        value.Length > 0 &&
        value[0] == '~' &&
        (value.Length == 1 || value[1] is '/' or '\\');

    private static IReadOnlyList<RedirectAnalysis> JoinRedirects(
        IReadOnlyList<RedirectAnalysis> left,
        IReadOnlyList<RedirectAnalysis> right)
    {
        if (left.Count != right.Count)
        {
            return Array.Empty<RedirectAnalysis>();
        }

        var joined = new RedirectAnalysis[left.Count];
        for (var index = 0; index < joined.Length; index++)
        {
            var leftFact = left[index];
            var rightFact = right[index];
            if (leftFact.RedirectIndex != rightFact.RedirectIndex ||
                leftFact.Source != rightFact.Source ||
                leftFact.Operation != rightFact.Operation ||
                leftFact.TargetDescriptor != rightFact.TargetDescriptor ||
                leftFact.IsPathRelevant != rightFact.IsPathRelevant ||
                leftFact.HereDocument != rightFact.HereDocument)
            {
                joined[index] = new RedirectAnalysis
                {
                    RedirectIndex = leftFact.RedirectIndex,
                };
                continue;
            }

            joined[index] = leftFact with
            {
                Target = JoinDomains(leftFact.Target, rightFact.Target),
                IsComplete = leftFact.IsComplete && rightFact.IsComplete,
            };
        }

        return joined;
    }

    private static ShellValueDomain CreateDomain(IReadOnlyList<string> values) =>
        values.Count switch
        {
            1 => new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { values[0] },
            },
            _ when values.Count > 1 &&
                values.Count <= ShellAnalysisLimits.MaxValueCandidates =>
                new ShellValueDomain
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = values,
            },
            _ => ShellValueDomain.Unknown,
        };

    private PwshFlowResult AnalyzeList(CommandListSyntax list, AnalysisContext input)
    {
        if (list.Items.Count == 0)
        {
            return PwshFlowResult.Both(input);
        }

        var flow = AnalyzeNode(list.Items[0].Command, input);
        for (var index = 1; index < list.Items.Count; index++)
        {
            var item = list.Items[index];
            switch (item.Operator)
            {
                case CompoundOperator.AndIf:
                    if (flow.OnSuccess is AnalysisContext success)
                    {
                        var right = AnalyzeNode(item.Command, success);
                        flow = new PwshFlowResult(
                            right.OnSuccess,
                            AnalysisContext.JoinNullable(flow.OnFailure, right.OnFailure));
                    }

                    break;
                case CompoundOperator.OrIf:
                    if (flow.OnFailure is AnalysisContext failure)
                    {
                        var right = AnalyzeNode(item.Command, failure);
                        flow = new PwshFlowResult(
                            AnalysisContext.JoinNullable(flow.OnSuccess, right.OnSuccess),
                            right.OnFailure);
                    }

                    break;
                case CompoundOperator.Sequence:
                    if (flow.JoinedState is AnalysisContext sequenceInput)
                    {
                        flow = AnalyzeNode(item.Command, sequenceInput);
                    }

                    break;
                default:
                    return flow.JoinedState is AnalysisContext joined
                        ? PwshFlowResult.Both(joined.Invalidate(unknownCwd: true))
                        : flow;
            }
        }

        return flow;
    }

    private PwshFlowResult AnalyzePipeline(PipelineSyntax pipeline, AnalysisContext input)
    {
        var stageInput = input;
        var enclosingStageEffectsMayReachBodies =
            _pipelineStageEffectsMayReachRegionBodies;
        _pipelineStageEffectsMayReachRegionBodies |=
            PipelineStageEffectsMayReachRegionBodies(pipeline);
        try
        {
            for (var stageIndex = 0; stageIndex < pipeline.Stages.Count; stageIndex++)
            {
                var stage = pipeline.Stages[stageIndex];
                var executionRegionEffectsBefore = _executionRegionEffectCount;
                var nonRegionMutationsBefore = _nonRegionStateMutationCount;
                var enclosingStageMayReceiveInput = _pipelineStageMayReceiveInput;
                _pipelineStageMayReceiveInput = stageIndex > 0;
                var stageFlow = AnalyzeNode(stage, stageInput);
                _pipelineStageMayReceiveInput = enclosingStageMayReceiveInput;
                if (stageFlow.JoinedState is not AnalysisContext stageExit)
                {
                    return new PwshFlowResult(null, null);
                }

                if (!stageInput.StateEquals(stageExit))
                {
                    var regionCausedTransition =
                        _executionRegionEffectCount > executionRegionEffectsBefore;
                    var unrelatedMutationCausedTransition =
                        _nonRegionStateMutationCount > nonRegionMutationsBefore;
                    if (!regionCausedTransition || unrelatedMutationCausedTransition)
                    {
                        _isComplete = false;
                        return new PwshFlowResult(null, null);
                    }

                    stageInput = stageInput.Invalidate(unknownCwd: true);
                }
            }

            return PwshFlowResult.Both(stageInput);
        }
        finally
        {
            _pipelineStageEffectsMayReachRegionBodies =
                enclosingStageEffectsMayReachBodies;
        }
    }

    private static bool PipelineStageEffectsMayReachRegionBodies(PipelineSyntax pipeline)
    {
        var hasPipelineSensitiveRegion = false;
        var statefulStageCount = 0;
        for (var index = 0; index < pipeline.Stages.Count; index++)
        {
            var stage = pipeline.Stages[index];
            hasPipelineSensitiveRegion |= ContainsPipelineSensitiveExecutionRegion(stage);
            if (MayMutatePipelineState(stage))
            {
                statefulStageCount++;
            }
        }

        return hasPipelineSensitiveRegion && statefulStageCount > 1;
    }

    private static bool ContainsPipelineSensitiveExecutionRegion(ShellSyntaxNode node) =>
        node switch
        {
            SimpleCommandSyntax simple => IsPipelineSensitiveExecutionRegionHost(simple),
            ShellBlockSyntax block => BlockContains(
                block,
                ContainsPipelineSensitiveExecutionRegion),
            GroupSyntax group => ContainsPipelineSensitiveExecutionRegion(group.Body),
            CommandListSyntax list => ListContains(
                list,
                ContainsPipelineSensitiveExecutionRegion),
            PipelineSyntax pipeline => PipelineContains(
                pipeline,
                ContainsPipelineSensitiveExecutionRegion),
            ExecutionRegionSyntax => true,
            _ => false,
        };

    private static bool MayMutatePipelineState(ShellSyntaxNode node) =>
        node switch
        {
            SimpleCommandSyntax simple => SimpleMayMutatePipelineState(simple),
            ShellBlockSyntax block => BlockContains(block, MayMutatePipelineState),
            GroupSyntax group => MayMutatePipelineState(group.Body),
            CommandListSyntax list => ListContains(list, MayMutatePipelineState),
            PipelineSyntax pipeline => PipelineContains(pipeline, MayMutatePipelineState),
            ForEachSyntax => true,
            CommandSubstitutionSyntax => true,
            ExecutionRegionSyntax => true,
            _ => false,
        };

    private static bool IsPipelineSensitiveExecutionRegionHost(SimpleCommandSyntax simple)
    {
        var binding = PwshExecutionRegionBindingCatalog.Bind(
            simple.Clause,
            commandIdentityProven: true);
        return binding.Status == PwshExecutionRegionBindingStatus.ProvedExecution &&
            binding.ParameterSet is PwshExecutionRegionParameterSet.ForEachScriptBlock or
                PwshExecutionRegionParameterSet.ForEachParallel or
                PwshExecutionRegionParameterSet.WhereScriptBlock or
                PwshExecutionRegionParameterSet.InvokeInProcess or
                PwshExecutionRegionParameterSet.MeasureExpression or
                PwshExecutionRegionParameterSet.TraceExpression or
                PwshExecutionRegionParameterSet.NewModuleScriptBlock;
    }

    private static bool SimpleMayMutatePipelineState(SimpleCommandSyntax simple)
    {
        if (simple.Substitutions.Count > 0 ||
            simple.ExecutionRegions.Count > 0 ||
            simple.Clause.Verb.IsDynamic)
        {
            return true;
        }

        return PwshPersistentStateMutation.TryGetEffect(
            simple.Clause,
            Array.Empty<EffectiveArgument>(),
            providerLocationUnknown: false,
            out _);
    }

    private static bool BlockContains(
        ShellBlockSyntax block,
        Func<ShellSyntaxNode, bool> predicate)
    {
        for (var index = 0; index < block.Statements.Count; index++)
        {
            if (predicate(block.Statements[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ListContains(
        CommandListSyntax list,
        Func<ShellSyntaxNode, bool> predicate)
    {
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (predicate(list.Items[index].Command))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PipelineContains(
        PipelineSyntax pipeline,
        Func<ShellSyntaxNode, bool> predicate)
    {
        for (var index = 0; index < pipeline.Stages.Count; index++)
        {
            if (predicate(pipeline.Stages[index]))
            {
                return true;
            }
        }

        return false;
    }

    private PwshFlowResult AnalyzeGroup(GroupSyntax group, AnalysisContext input)
    {
        if (group.GroupKind == ShellGroupKind.CurrentScope)
        {
            return AnalyzeBlock(group.Body, input);
        }

        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var locationStateMutationCount = _locationStateMutationCount;
        var childScopeEscapeRiskCount = _childScopeEscapeRiskCount;
        var childRunspaceProcessEscapeRiskCount =
            _childRunspaceProcessEscapeRiskCount;
        _invocationRedirectContexts.Add(input);
        try
        {
            var bodyInput = group.GroupKind == ShellGroupKind.IsolatedScope
                ? input.CreateDecodedHostInput()
                : input.WithoutBindings().Invalidate(
                    unknownCwd: false,
                    invalidateCommandResolution: false);
            AnalyzeBlock(
                group.Body,
                bodyInput);
        }
        finally
        {
            _invocationRedirectContexts.RemoveAt(
                _invocationRedirectContexts.Count - 1);
        }
        _executionRegionEffectCount = executionRegionEffectCount;
        _nonRegionStateMutationCount = nonRegionStateMutationCount;
        _locationStateMutationCount = locationStateMutationCount;
        _childScopeEscapeRiskCount = childScopeEscapeRiskCount;
        _childRunspaceProcessEscapeRiskCount =
            childRunspaceProcessEscapeRiskCount;
        return PwshFlowResult.Both(input);
    }

    private PwshFlowResult AnalyzeSubstitution(
        CommandSubstitutionSyntax substitution,
        AnalysisContext input)
    {
        return AnalyzeBlock(substitution.Body, input);
    }

    private PwshFlowResult AnalyzeExecutionRegion(
        ExecutionRegionSyntax region,
        AnalysisContext input)
    {
        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        var locationStateMutationCount = _locationStateMutationCount;
        var childScopeEscapeRiskCount = _childScopeEscapeRiskCount;
        var body = AnalyzeExecutionRegionBody(region.Body, input);
        var locationMutated = _locationStateMutationCount > locationStateMutationCount;
        var childScopeMayEscape =
            _childScopeEscapeRiskCount > childScopeEscapeRiskCount;
        _executionRegionEffectCount = executionRegionEffectCount + 1;
        _nonRegionStateMutationCount = nonRegionStateMutationCount;
        _childScopeEscapeRiskCount = childScopeEscapeRiskCount +
            (childScopeMayEscape ? 1 : 0);

        if (region.Origin == ExecutionRegionOrigin.DotSource)
        {
            return body;
        }

        if (region.Origin != ExecutionRegionOrigin.DirectCall)
        {
            return PwshFlowResult.Both(
                input
                    .Invalidate(unknownCwd: true)
                    .WithoutAutomaticHomeValue());
        }

        return new PwshFlowResult(
            RestoreChildScopeExit(
                body.OnSuccess,
                input,
                locationMutated,
                childScopeMayEscape),
            RestoreChildScopeExit(
                body.OnFailure,
                input,
                locationMutated,
                childScopeMayEscape));
    }

    private static AnalysisContext? RestoreChildScopeExit(
        AnalysisContext? bodyExit,
        AnalysisContext input,
        bool locationMutated,
        bool childScopeMayEscape)
    {
        if (bodyExit is null)
        {
            return null;
        }

        var restored = childScopeMayEscape
            ? input.Invalidate(unknownCwd: false)
            : input;
        if (childScopeMayEscape &&
            bodyExit.Value.HasRunspaceCommandResolutionProcessRisk)
        {
            restored = restored.WithRunspaceCommandResolutionProcessRisk(
                bodyExit.Value);
        }

        if (bodyExit.Value.ProcessWideStateInvalidated)
        {
            restored = restored.WithProcessWideStateInvalidated();
        }

        if (!bodyExit.Value.AutomaticHomeValueAvailable)
        {
            restored = restored.WithoutAutomaticHomeValue();
        }

        if (!locationMutated && string.Equals(
                bodyExit.Value.WorkingDirectory,
                input.WorkingDirectory,
                StringComparison.Ordinal))
        {
            return restored;
        }

        if (bodyExit.Value.WorkingDirectory is string workingDirectory)
        {
            return restored.WithCwd(workingDirectory);
        }

        return locationMutated
            ? restored.Invalidate(unknownCwd: true)
            : restored.WithCwd(workingDirectory: null);
    }

    private PwshFlowResult AnalyzeForEach(ForEachSyntax forEach, AnalysisContext input)
    {
        _nonRegionStateMutationCount++;
        var plan = _planFactory(forEach);
        if (plan is null)
        {
            AnalyzeBlock(
                forEach.IteratorCommands,
                input.Invalidate(
                    unknownCwd: false,
                    invalidateCommandResolution: false));
            AnalyzeBlock(
                forEach.Body,
                input.Invalidate(
                    unknownCwd: false,
                    invalidateCommandResolution: false));
            return PwshFlowResult.Both(input.Invalidate(
                unknownCwd: false,
                invalidateCommandResolution: false));
        }

        var iterator = AnalyzeBlock(forEach.IteratorCommands, input);
        if (iterator.JoinedState is not AnalysisContext loopInput)
        {
            return new PwshFlowResult(null, null);
        }

        if (plan.Cardinality == PwshIterationCardinality.Never)
        {
            RecordUnvisitedBindingArguments(forEach.Body, plan.BindingName);
            return PwshFlowResult.Success(loopInput);
        }

        if (plan.RequiresFixedPoint)
        {
            return AnalyzeForEachFixedPoint(forEach, loopInput, plan);
        }

        var iterationInput = loopInput;
        foreach (var candidate in plan.OrderedCandidates)
        {
            if (!TryConsumeLoopAnalysisTransition())
            {
                return new PwshFlowResult(null, null);
            }

            var body = AnalyzeBlock(
                forEach.Body,
                iterationInput.WithBinding(plan.BindingName, candidate));
            if (body.JoinedState is not AnalysisContext bodyExit)
            {
                return new PwshFlowResult(null, null);
            }

            iterationInput = bodyExit;
        }

        return PwshFlowResult.Both(iterationInput);
    }

    private PwshFlowResult AnalyzeForEachFixedPoint(
        ForEachSyntax forEach,
        AnalysisContext loopInput,
        PwshForEachAnalysisPlan plan)
    {
        AnalysisContext? exits = plan.Cardinality == PwshIterationCardinality.ZeroOrMore
            ? loopInput
            : null;
        var head = loopInput;
        var wideningBase = loopInput;
        var nextHead = loopInput;
        for (var iteration = 0;
             iteration <= ShellAnalysisLimits.MaxValueCandidates;
             iteration++)
        {
            if (!TryConsumeLoopAnalysisTransition())
            {
                return new PwshFlowResult(null, null);
            }

            var body = AnalyzeBlock(
                forEach.Body,
                head.WithBinding(plan.BindingName, plan.Summary));
            if (body.JoinedState is not AnalysisContext bodyExit)
            {
                return new PwshFlowResult(null, null);
            }

            exits = AnalysisContext.JoinNullable(exits, bodyExit);
            wideningBase = head;
            nextHead = AnalysisContext.Join(head, bodyExit);
            if (head.StateEquals(nextHead))
            {
                return exits is AnalysisContext stableExit
                    ? PwshFlowResult.Both(stableExit)
                    : new PwshFlowResult(null, null);
            }

            head = nextHead;
        }

        if (!TryConsumeLoopAnalysisTransition())
        {
            return new PwshFlowResult(null, null);
        }

        var widened = AnalysisContext.Widen(wideningBase, nextHead);
        var widenedBody = AnalyzeBlock(
            forEach.Body,
            widened.WithBinding(plan.BindingName, plan.Summary));
        exits = AnalysisContext.JoinNullable(exits, widenedBody.JoinedState);
        return exits is AnalysisContext widenedExit
            ? PwshFlowResult.Both(widenedExit)
            : new PwshFlowResult(null, null);
    }

    private bool TryConsumeLoopAnalysisTransition()
    {
        if (_remainingLoopAnalysisTransitions == 0)
        {
            _isComplete = false;
            return false;
        }

        _remainingLoopAnalysisTransitions--;
        return true;
    }

    private PwshFlowResult? AnalyzeSetLocation(
        SimpleCommandSyntax simple,
        AnalysisContext input)
    {
        var verb = simple.Clause.Verb.CanonicalVerb ??
            (simple.Clause.Verb.Tokens.Count == 0
                ? null
                : simple.Clause.Verb.Tokens[0]);
        if (!string.Equals(verb, "Set-Location", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = string.Empty;
        var hasTarget = false;
        var expectsPath = false;
        var literalPath = false;
        for (var elementIndex = 0;
             elementIndex < simple.Clause.Elements.Count;
             elementIndex++)
        {
            var element = simple.Clause.Elements[elementIndex];
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (!TryGetElementDomain(simple, elementIndex, input, out var domain) ||
                domain.Kind != ShellValueDomainKind.Exact ||
                domain.Values.Count != 1)
            {
                return new PwshFlowResult(input.Invalidate(unknownCwd: true), input);
            }

            var value = domain.Values[0];
            if (element.IsFlag)
            {
                var colon = value.IndexOf(':');
                var parameter = colon < 0 ? value : value.Substring(0, colon);
                if (parameter.Equals("-PassThru", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsPathParameter(parameter, out var isLiteralPath))
                {
                    return new PwshFlowResult(input.Invalidate(unknownCwd: true), input);
                }

                literalPath = isLiteralPath;
                if (colon >= 0)
                {
                    if (hasTarget || colon + 1 == value.Length)
                    {
                        return new PwshFlowResult(null, input);
                    }

                    target = value.Substring(colon + 1);
                    hasTarget = true;
                }
                else
                {
                    expectsPath = true;
                }

                continue;
            }

            if (hasTarget)
            {
                return new PwshFlowResult(null, input);
            }

            target = value;
            hasTarget = true;
            expectsPath = false;
        }

        if (expectsPath)
        {
            return new PwshFlowResult(null, input);
        }

        if (!hasTarget)
        {
            var home = string.IsNullOrEmpty(_options.HomeDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : _options.HomeDirectory!;
            return new PwshFlowResult(input.WithCwd(NormalizePath(home)), input);
        }

        if (target is "-" or "+")
        {
            return new PwshFlowResult(input.Invalidate(unknownCwd: true), input);
        }

        var resolverOptions = new PwshParserOptions
        {
            HomeDirectory = _options.HomeDirectory,
            WorkingDirectory = input.WorkingDirectory,
            InitialStateMode = _options.InitialStateMode,
        };
        var resolved = PwshResolver.Resolve(
            ShellValue.Literal(target),
            treatAsPath: true,
            resolverOptions,
            workingDirectoryUnknown: input.WorkingDirectory is null,
            literalPath
                ? ShellResolutionConsumer.PowerShellCmdletLiteralPath
                : ShellResolutionConsumer.PowerShellCmdletPath);
        return resolved.IsPath && resolved.Resolved is not null
            ? new PwshFlowResult(input.WithCwd(resolved.Resolved), input)
            : new PwshFlowResult(input.Invalidate(unknownCwd: true), input);
    }

    private static bool IsPathParameter(string parameter, out bool literalPath)
    {
        literalPath = parameter.Equals("-LiteralPath", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-LP", StringComparison.OrdinalIgnoreCase);
        return literalPath ||
            parameter.Equals("-Path", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-PSPath", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetElementDomain(
        SimpleCommandSyntax simple,
        int elementIndex,
        AnalysisContext input,
        out ShellValueDomain domain)
    {
        if (TryGetElementValue(simple, elementIndex, 0, out var value))
        {
            return input.TryEvaluateValue(value, out domain);
        }

        domain = ShellValueDomain.Unknown;
        return false;
    }

    private bool TryGetElementValue(
        SimpleCommandSyntax simple,
        int elementIndex,
        int valueOffset,
        out ShellValue value)
    {
        var source = _factsFactory(simple);
        foreach (var provenance in source.ValueProvenance)
        {
            if (provenance.ClauseElementIndex == elementIndex)
            {
                if (valueOffset < 0 || valueOffset > provenance.Value.Decoded.Length)
                {
                    value = ShellValue.Literal(string.Empty);
                    return false;
                }

                value = valueOffset == 0
                    ? provenance.Value
                    : provenance.Value.Slice(valueOffset);
                return true;
            }
        }

        value = ShellValue.Literal(string.Empty);
        return false;
    }

    private CommandOccurrenceFacts GetFacts(SimpleCommandSyntax simple)
    {
        if (_facts.TryGetValue(simple.Clause, out var facts))
        {
            return facts;
        }

        var source = _factsFactory(simple);
        return ContainsReference(_incompleteClauses, simple.Clause)
            ? new CommandOccurrenceFacts
            {
                EffectiveArguments = source.EffectiveArguments,
                WorkingDirectory = ShellValueDomain.Unknown,
                Redirects = RewriteRedirectsForUnknownState(source),
                RedirectTargetProvenance = source.RedirectTargetProvenance,
                CwdPathDependencies = source.CwdPathDependencies,
                ValueProvenance = source.ValueProvenance,
                HasCompleteValueProvenance = source.HasCompleteValueProvenance,
                IsComplete = false,
            }
            : source;
    }

    private ShellBlockSyntax RewriteBlock(
        ShellBlockSyntax block,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var statements = new ShellSyntaxNode[block.Statements.Count];
        for (var index = 0; index < statements.Length; index++)
        {
            statements[index] = RewriteNode(block.Statements[index], facts);
        }

        return block with { Statements = statements };
    }

    private ShellSyntaxNode RewriteNode(
        ShellSyntaxNode node,
        Dictionary<Clause, CommandOccurrenceFacts> facts) =>
        node switch
        {
            ShellBlockSyntax block => RewriteBlock(block, facts),
            SimpleCommandSyntax simple => RewriteSimple(simple, facts),
            PipelineSyntax pipeline => pipeline with
            {
                Stages = RewriteNodes(pipeline.Stages, facts),
            },
            CommandListSyntax list => list with
            {
                Items = RewriteItems(list.Items, facts),
            },
            GroupSyntax group => group with { Body = RewriteBlock(group.Body, facts) },
            ForEachSyntax forEach => forEach with
            {
                IteratorCommands = RewriteBlock(forEach.IteratorCommands, facts),
                Body = RewriteBlock(forEach.Body, facts),
            },
            ConditionLoopSyntax loop => loop with
            {
                Condition = RewriteBlock(loop.Condition, facts),
                Body = RewriteBlock(loop.Body, facts),
            },
            ConditionalSyntax conditional => conditional with
            {
                Branches = RewriteBranches(conditional.Branches, facts),
                Else = conditional.Else is null
                    ? null
                    : RewriteBlock(conditional.Else, facts),
            },
            ConditionalBranchSyntax branch => branch with
            {
                Condition = RewriteBlock(branch.Condition, facts),
                Body = RewriteBlock(branch.Body, facts),
            },
            CommandSubstitutionSyntax substitution => substitution with
            {
                Body = RewriteBlock(substitution.Body, facts),
            },
            ExecutionRegionSyntax region => region with
            {
                Body = RewriteBlock(region.Body, facts),
            },
            _ => node,
        };

    private SimpleCommandSyntax RewriteSimple(
        SimpleCommandSyntax simple,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var substitutions = new CommandSubstitutionSyntax[simple.Substitutions.Count];
        for (var index = 0; index < substitutions.Length; index++)
        {
            substitutions[index] = (CommandSubstitutionSyntax)RewriteNode(
                simple.Substitutions[index],
                facts);
        }

        var sourceRegions = _executionRegions.TryGetValue(
            simple.Clause,
            out var analyzedRegions)
            ? analyzedRegions
            : simple.ExecutionRegions;
        var executionRegions = new ExecutionRegionSyntax[sourceRegions.Count];
        for (var index = 0; index < executionRegions.Length; index++)
        {
            var region = sourceRegions[index];
            executionRegions[index] = region with
            {
                Body = RewriteBlock(region.Body, facts),
            };
        }

        var source = GetFacts(simple);
        var clause = RewriteCwdCompatibility(
            simple.Clause,
            source,
            out var hasUnresolvedCwdDynamicElement);
        var redirects = RewriteRedirectFacts(
            source.Redirects,
            source.RedirectTargetProvenance,
            clause);
        facts.Add(clause, new CommandOccurrenceFacts
        {
            EffectiveArguments = source.EffectiveArguments,
            WorkingDirectory = source.WorkingDirectory,
            Redirects = redirects,
            RedirectTargetProvenance = source.RedirectTargetProvenance,
            CwdPathDependencies = source.CwdPathDependencies,
            ValueProvenance = source.ValueProvenance,
            HasCompleteValueProvenance = source.HasCompleteValueProvenance,
            IsComplete = source.IsComplete && !hasUnresolvedCwdDynamicElement,
        });
        return simple with
        {
            Clause = clause,
            Substitutions = substitutions,
            ExecutionRegions = executionRegions,
        };
    }

    private static IReadOnlyList<RedirectAnalysis> RewriteRedirectFacts(
        IReadOnlyList<RedirectAnalysis> source,
        IReadOnlyList<RedirectTargetProvenance> provenance,
        Clause clause)
    {
        if (source.Count == 0)
        {
            return source;
        }

        var rewritten = new RedirectAnalysis[source.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            var fact = source[index];
            if (!fact.IsPathRelevant ||
                fact.RedirectIndex < 0 ||
                fact.RedirectIndex >= clause.Redirects.Count)
            {
                rewritten[index] = fact;
                continue;
            }

            var compatibility = clause.Redirects[fact.RedirectIndex];
            var hasProvenance = HasRedirectProvenance(
                provenance,
                fact.RedirectIndex);
            rewritten[index] = fact with
            {
                Target = hasProvenance
                    ? fact.Target
                    : compatibility.IsDynamicSkip
                        ? ShellValueDomain.Unknown
                        : new ShellValueDomain
                        {
                            Kind = ShellValueDomainKind.Exact,
                            Values = new[] { compatibility.Target },
                        },
            };
        }

        return rewritten;
    }

    private static bool HasRedirectProvenance(
        IReadOnlyList<RedirectTargetProvenance> provenance,
        int redirectIndex)
    {
        foreach (var item in provenance)
        {
            if (item.RedirectIndex == redirectIndex)
            {
                return true;
            }
        }

        return false;
    }

    private Clause RewriteCwdCompatibility(
        Clause clause,
        CommandOccurrenceFacts facts,
        out bool hasUnresolvedCwdDynamicElement)
    {
        hasUnresolvedCwdDynamicElement = false;
        var workingDirectory = facts.WorkingDirectory;
        var currentCwd = workingDirectory.Kind == ShellValueDomainKind.Exact &&
            workingDirectory.Values.Count == 1
            ? workingDirectory.Values[0]
            : null;
        var parseCwd = CompatibilityWorkingDirectory(clause, out var hadAttribution);
        if (string.Equals(currentCwd, parseCwd, StringComparison.Ordinal))
        {
            return clause;
        }

        var resolutionChanges = new Dictionary<string, string?>(StringComparer.Ordinal);
        var promotedArguments = new Dictionary<int, string>();
        var elements = new ClauseElement[clause.Elements.Count];
        var changedResolution = false;
        var literalPath = false;
        var cmdletStyle = IsCmdletStyle(clause);
        var argumentIndex = 0;
        for (var index = 0; index < elements.Length; index++)
        {
            var element = clause.Elements[index];
            if (IsCwdDependentResolution(element))
            {
                var elementLiteralPath = literalPath ||
                    IsLiteralPathParameter(element.Value);
                var consumer = element.Role == ClauseElementRole.Redirect
                    ? ShellResolutionConsumer.PowerShellRedirect
                    : elementLiteralPath
                        ? ShellResolutionConsumer.PowerShellCmdletLiteralPath
                        : cmdletStyle
                            ? ShellResolutionConsumer.PowerShellCmdletPath
                            : ShellResolutionConsumer.PowerShellNativeArgument;
                var rewritten = currentCwd is null
                    ? null
                    : ResolveCompatibilityPath(
                        PathCandidate(element.Value, element.IsFlag),
                        currentCwd,
                        consumer);
                resolutionChanges[element.Resolved!] = rewritten;
                elements[index] = element with { Resolved = rewritten };
                changedResolution = true;
            }
            else if (currentCwd is not null &&
                     element.Role is (
                         ClauseElementRole.Argument or ClauseElementRole.Redirect) &&
                     element.Kind == ArgKind.DynamicSkip &&
                     CanPromoteCwdDynamicElement(
                         element,
                         index,
                         facts.ValueProvenance,
                         hadAttribution && parseCwd is null))
            {
                var elementLiteralPath = literalPath ||
                    IsLiteralPathParameter(element.Value);
                var consumer = element.Role == ClauseElementRole.Redirect
                    ? ShellResolutionConsumer.PowerShellRedirect
                    : elementLiteralPath
                        ? ShellResolutionConsumer.PowerShellCmdletLiteralPath
                        : cmdletStyle
                            ? ShellResolutionConsumer.PowerShellCmdletPath
                            : ShellResolutionConsumer.PowerShellNativeArgument;
                var promoted = ResolveCompatibilityPath(
                    PathCandidate(element.Value, element.IsFlag),
                    currentCwd,
                    consumer);
                if (promoted is not null)
                {
                    elements[index] = element with
                    {
                        Kind = ArgKind.Literal,
                        IsPath = true,
                        Resolved = promoted,
                    };
                    if (element.Role == ClauseElementRole.Argument)
                    {
                        promotedArguments.Add(argumentIndex, promoted);
                    }
                    changedResolution = true;
                }
                else
                {
                    elements[index] = element;
                }
            }
            else
            {
                elements[index] = element;
            }

            if (element.Role == ClauseElementRole.Argument)
            {
                argumentIndex++;
            }

            literalPath = IsLiteralPathParameter(element.Value);
        }

        var args = new List<Arg>(clause.Args.Count + 1);
        literalPath = false;
        argumentIndex = 0;
        foreach (var argument in clause.Args)
        {
            if (argument.IsCwdAttribution)
            {
                continue;
            }

            if (promotedArguments.TryGetValue(argumentIndex, out var promoted))
            {
                args.Add(argument with
                {
                    Kind = ArgKind.Literal,
                    IsPath = true,
                    Resolved = promoted,
                });
                changedResolution = true;
            }
            else if (IsCwdDependentResolution(argument))
            {
                var rewritten = argument.Resolved is not null &&
                    resolutionChanges.TryGetValue(argument.Resolved, out var mapped)
                    ? mapped
                    : currentCwd is null
                        ? null
                        : ResolveCompatibilityPath(
                            PathCandidate(argument.Raw, argument.IsFlag),
                            currentCwd,
                            literalPath
                                ? ShellResolutionConsumer.PowerShellCmdletLiteralPath
                                : cmdletStyle
                                    ? ShellResolutionConsumer.PowerShellCmdletPath
                                    : ShellResolutionConsumer.PowerShellNativeArgument);
                args.Add(argument with { Resolved = rewritten });
                changedResolution = true;
            }
            else
            {
                args.Add(argument);
            }

            argumentIndex++;
            literalPath = IsLiteralPathParameter(argument.Raw);
        }

        if (hadAttribution || changedResolution)
        {
            args.Add(currentCwd is null
                ? new Arg
                {
                    Raw = "<dynamic-cwd>",
                    Kind = ArgKind.DynamicSkip,
                    IsCwdAttribution = true,
                }
                : new Arg
                {
                    Raw = currentCwd,
                    Resolved = currentCwd,
                    Kind = ArgKind.Literal,
                    IsPath = true,
                    IsCwdAttribution = true,
                });
        }

        var redirects = RewriteCompatibilityRedirects(
            clause.Redirects,
            clause.Elements,
            elements);
        hasUnresolvedCwdDynamicElement = hadAttribution &&
            parseCwd is null &&
            currentCwd is not null &&
            HasUnresolvedCwdDynamicElement(clause.Elements, elements);
        return clause with
        {
            Args = args.ToArray(),
            Elements = elements,
            Redirects = redirects,
        };
    }

    private static bool HasUnresolvedCwdDynamicElement(
        IReadOnlyList<ClauseElement> original,
        IReadOnlyList<ClauseElement> rewritten)
    {
        for (var index = 0; index < original.Count; index++)
        {
            var element = original[index];
            if (element.Role is not (
                    ClauseElementRole.Argument or ClauseElementRole.Redirect) ||
                element.Resolved is not null ||
                element.Kind is not (ArgKind.DynamicSkip or ArgKind.EnvVar))
            {
                continue;
            }

            if (rewritten[index].Resolved is null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetLiteralProvenance(
        IReadOnlyList<ShellValueElementProvenance> provenance,
        int elementIndex)
    {
        foreach (var value in provenance)
        {
            if (value.ClauseElementIndex != elementIndex)
            {
                continue;
            }

            foreach (var fragment in value.Value.Fragments)
            {
                if (fragment.Kind != ShellValueFragmentKind.Literal ||
                    fragment.Cardinality != ShellValueCardinality.ExactlyOne)
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static bool CanPromoteCwdDynamicElement(
        ClauseElement element,
        int elementIndex,
        IReadOnlyList<ShellValueElementProvenance> provenance,
        bool hadDynamicAttribution)
    {
        if (!IsStaticSingleValueSpelling(element))
        {
            return false;
        }

        if (TryGetLiteralProvenance(provenance, elementIndex))
        {
            return true;
        }

        if (!hadDynamicAttribution && element.Role != ClauseElementRole.Redirect)
        {
            return false;
        }

        return true;
    }

    private static bool IsStaticSingleValueSpelling(ClauseElement element)
    {
        var value = element.Value;
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character == ',' && !IsFullyQuoted(element.Raw) ||
                character is '$' or '`' or '@' or '*' or '?' or '[' or ']' or
                    '{' or '}' or '(' or ')' or ';' or '|' or '&')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFullyQuoted(string value)
    {
        if (value.Length < 2 ||
            value[0] is not ('\'' or '"') ||
            value[value.Length - 1] != value[0])
        {
            return false;
        }

        for (var index = 1; index < value.Length - 1; index++)
        {
            if (value[index] == value[0])
            {
                return false;
            }
        }

        return true;
    }

    private string? CompatibilityWorkingDirectory(
        Clause clause,
        out bool hadAttribution)
    {
        foreach (var argument in clause.Args)
        {
            if (argument.IsCwdAttribution)
            {
                hadAttribution = true;
                return argument.Resolved;
            }
        }

        hadAttribution = false;
        return NormalizePath(
            _options.WorkingDirectory ?? Environment.CurrentDirectory);
    }

    private string? ResolveCompatibilityPath(
        string value,
        string workingDirectory,
        ShellResolutionConsumer consumer)
    {
        var options = new PwshParserOptions
        {
            HomeDirectory = _options.HomeDirectory,
            WorkingDirectory = workingDirectory,
            InitialStateMode = _options.InitialStateMode,
        };
        var resolved = PwshResolver.Resolve(
            ShellValue.Literal(value),
            treatAsPath: true,
            options,
            workingDirectoryUnknown: false,
            consumer);
        return resolved.IsPath ? resolved.Resolved : null;
    }

    private static bool IsCmdletStyle(Clause clause)
    {
        var verb = clause.Verb.CanonicalVerb ??
            (clause.Verb.Tokens.Count == 0 ? null : clause.Verb.Tokens[0]);
        return verb?.IndexOf('-') >= 0;
    }

    private static bool IsCwdDependentResolution(ClauseElement element)
    {
        if (!element.IsPath ||
            element.Resolved is null ||
            element.Kind != ArgKind.Literal)
        {
            return false;
        }

        return !IsRootedPath(PathCandidate(element.Value, element.IsFlag));
    }

    private static bool IsCwdDependentResolution(Arg argument)
    {
        if (!argument.IsPath ||
            argument.Resolved is null ||
            argument.Kind != ArgKind.Literal)
        {
            return false;
        }

        return !IsRootedPath(PathCandidate(argument.Raw, argument.IsFlag));
    }

    private static string PathCandidate(string value, bool isFlag)
    {
        if (value.Length >= 2 &&
            value[0] is '\'' or '"' &&
            value[value.Length - 1] == value[0])
        {
            value = value.Substring(1, value.Length - 2);
        }

        if (isFlag)
        {
            var colon = value.IndexOf(':');
            var equals = value.IndexOf('=');
            var separator = colon < 0
                ? equals
                : equals < 0
                    ? colon
                    : Math.Min(colon, equals);
            if (separator >= 0 && separator + 1 < value.Length)
            {
                value = value.Substring(separator + 1);
            }
        }

        const string fullFileSystemProvider =
            "Microsoft.PowerShell.Core\\FileSystem::";
        const string fileSystemProvider = "FileSystem::";
        if (value.StartsWith(fullFileSystemProvider, StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(fullFileSystemProvider.Length);
        }
        else if (value.StartsWith(fileSystemProvider, StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(fileSystemProvider.Length);
        }

        return value;
    }

    private static bool IsLiteralPathParameter(string value)
    {
        var colon = value.IndexOf(':');
        var equals = value.IndexOf('=');
        var separator = colon < 0
            ? equals
            : equals < 0
                ? colon
                : Math.Min(colon, equals);
        var parameter = separator < 0 ? value : value.Substring(0, separator);
        return parameter.Equals("-LiteralPath", StringComparison.OrdinalIgnoreCase) ||
            parameter.Equals("-LP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootedPath(string value) =>
        value.Length > 0 && value[0] is '/' or '\\' ||
        value.Length >= 3 &&
        IsAsciiLetter(value[0]) &&
        value[1] == ':' &&
        value[2] is '/' or '\\';

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static IReadOnlyList<Redirect> RewriteCompatibilityRedirects(
        IReadOnlyList<Redirect> redirects,
        IReadOnlyList<ClauseElement> originalElements,
        IReadOnlyList<ClauseElement> rewrittenElements)
    {
        if (redirects.Count == 0)
        {
            return redirects;
        }

        var result = new Redirect[redirects.Count];
        var redirectIndex = 0;
        for (var elementIndex = 0;
             elementIndex < originalElements.Count && redirectIndex < result.Length;
             elementIndex++)
        {
            var original = originalElements[elementIndex];
            if (original.Role != ClauseElementRole.Redirect)
            {
                continue;
            }

            var rewritten = rewrittenElements[elementIndex];
            result[redirectIndex] = !string.Equals(
                original.Resolved,
                rewritten.Resolved,
                StringComparison.Ordinal)
                ? rewritten.Resolved is null
                    ? redirects[redirectIndex] with
                    {
                        Target = original.Value,
                        IsDynamicSkip = true,
                    }
                    : redirects[redirectIndex] with
                    {
                        Target = rewritten.Resolved,
                        IsDynamicSkip = false,
                    }
                : redirects[redirectIndex];
            redirectIndex++;
        }

        while (redirectIndex < result.Length)
        {
            result[redirectIndex] = redirects[redirectIndex];
            redirectIndex++;
        }

        return result;
    }

    private IReadOnlyList<ShellSyntaxNode> RewriteNodes(
        IReadOnlyList<ShellSyntaxNode> nodes,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new ShellSyntaxNode[nodes.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = RewriteNode(nodes[index], facts);
        }

        return rewritten;
    }

    private IReadOnlyList<CommandListItemSyntax> RewriteItems(
        IReadOnlyList<CommandListItemSyntax> items,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new CommandListItemSyntax[items.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = items[index] with
            {
                Command = RewriteNode(items[index].Command, facts),
            };
        }

        return rewritten;
    }

    private IReadOnlyList<ConditionalBranchSyntax> RewriteBranches(
        IReadOnlyList<ConditionalBranchSyntax> branches,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new ConditionalBranchSyntax[branches.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = (ConditionalBranchSyntax)RewriteNode(
                branches[index],
                facts);
        }

        return rewritten;
    }

    private void RecordUnvisitedBindingArguments(
        ShellBlockSyntax block,
        string bindingName)
    {
        foreach (var statement in block.Statements)
        {
            RecordUnvisitedBindingArguments(statement, bindingName);
        }
    }

    private void RecordUnvisitedBindingArguments(
        ShellSyntaxNode node,
        string bindingName)
    {
        switch (node)
        {
            case SimpleCommandSyntax simple:
                var source = _factsFactory(simple);
                var effective = new List<EffectiveArgument>();
                foreach (var provenance in source.ValueProvenance)
                {
                    if (ReferencesBinding(provenance.Value, bindingName))
                    {
                        effective.Add(new EffectiveArgument
                        {
                            ClauseElementIndex = provenance.ClauseElementIndex,
                            Value = ShellValueDomain.Unknown,
                        });
                    }
                }

                _facts[simple.Clause] = new CommandOccurrenceFacts
                {
                    EffectiveArguments = effective.ToArray(),
                    WorkingDirectory = ShellValueDomain.Unknown,
                    Redirects = RewriteRedirectsForUnknownState(source),
                    RedirectTargetProvenance = source.RedirectTargetProvenance,
                    CwdPathDependencies = source.CwdPathDependencies,
                    ValueProvenance = source.ValueProvenance,
                    HasCompleteValueProvenance = source.HasCompleteValueProvenance,
                    IsComplete = false,
                };
                foreach (var substitution in simple.Substitutions)
                {
                    RecordUnvisitedBindingArguments(substitution.Body, bindingName);
                }

                break;
            case ShellBlockSyntax nestedBlock:
                RecordUnvisitedBindingArguments(nestedBlock, bindingName);
                break;
            case PipelineSyntax pipeline:
                foreach (var stage in pipeline.Stages)
                {
                    RecordUnvisitedBindingArguments(stage, bindingName);
                }

                break;
            case CommandListSyntax list:
                foreach (var item in list.Items)
                {
                    RecordUnvisitedBindingArguments(item.Command, bindingName);
                }

                break;
            case GroupSyntax group:
                RecordUnvisitedBindingArguments(group.Body, bindingName);
                break;
            case ForEachSyntax forEach:
                RecordUnvisitedBindingArguments(forEach.IteratorCommands, bindingName);
                RecordUnvisitedBindingArguments(forEach.Body, bindingName);
                break;
            case CommandSubstitutionSyntax substitution:
                RecordUnvisitedBindingArguments(substitution.Body, bindingName);
                break;
        }
    }

    private IReadOnlyList<RedirectAnalysis> RewriteRedirectsForUnknownState(
        CommandOccurrenceFacts source)
    {
        if (source.Redirects.Count == 0)
        {
            return source.Redirects;
        }

        var redirects = new RedirectAnalysis[source.Redirects.Count];
        for (var index = 0; index < redirects.Length; index++)
        {
            var fact = source.Redirects[index];
            if (!fact.IsPathRelevant ||
                !TryGetRedirectProvenance(
                    source.RedirectTargetProvenance,
                    fact.RedirectIndex,
                    out var value))
            {
                redirects[index] = fact.IsPathRelevant
                    ? fact with { Target = ShellValueDomain.Unknown }
                    : fact;
                continue;
            }

            var resolved = PwshResolver.Resolve(
                value,
                treatAsPath: true,
                _options with { WorkingDirectory = null },
                workingDirectoryUnknown: true,
                ShellResolutionConsumer.PowerShellRedirect);
            redirects[index] = fact with
            {
                Target = resolved.Resolved is not null && resolved.IsPath
                    ? new ShellValueDomain
                    {
                        Kind = ShellValueDomainKind.Exact,
                        Values = new[] { resolved.Resolved },
                    }
                    : ShellValueDomain.Unknown,
            };
        }

        return redirects;
    }

    private static bool TryGetRedirectProvenance(
        IReadOnlyList<RedirectTargetProvenance> provenance,
        int redirectIndex,
        out ShellValue value)
    {
        foreach (var item in provenance)
        {
            if (item.RedirectIndex == redirectIndex)
            {
                value = item.Value;
                return true;
            }
        }

        value = ShellValue.Literal(string.Empty);
        return false;
    }

    private static bool ReferencesBinding(ShellValue value, string bindingName)
    {
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Expansion is ShellExpansionReference expansion &&
                expansion.Kind == ShellExpansionKind.Variable &&
                string.Equals(
                    expansion.Name,
                    bindingName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<EffectiveArgument> JoinEffectiveArguments(
        IReadOnlyList<EffectiveArgument> left,
        IReadOnlyList<EffectiveArgument> right)
    {
        var joined = new Dictionary<int, ShellValueDomain>();
        foreach (var argument in left)
        {
            joined[argument.ClauseElementIndex] = argument.Value;
        }

        foreach (var argument in right)
        {
            joined[argument.ClauseElementIndex] = joined.TryGetValue(
                argument.ClauseElementIndex,
                out var prior)
                ? JoinDomains(prior, argument.Value)
                : argument.Value;
        }

        var indices = new List<int>(joined.Keys);
        indices.Sort();
        var result = new EffectiveArgument[indices.Count];
        for (var index = 0; index < indices.Count; index++)
        {
            result[index] = new EffectiveArgument
            {
                ClauseElementIndex = indices[index],
                Value = joined[indices[index]],
            };
        }

        return result;
    }

    private static ShellValueDomain JoinWorkingDirectories(
        ShellValueDomain left,
        ShellValueDomain right) =>
        left.Kind == ShellValueDomainKind.Exact &&
        right.Kind == ShellValueDomainKind.Exact &&
        left.Values.Count == 1 &&
        right.Values.Count == 1 &&
        string.Equals(left.Values[0], right.Values[0], StringComparison.Ordinal)
            ? left
            : ShellValueDomain.Unknown;

    private static ShellValueDomain JoinDomains(
        ShellValueDomain left,
        ShellValueDomain right)
    {
        if (left.Kind is not (ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet) ||
            right.Kind is not (ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
        {
            return ShellValueDomain.Unknown;
        }

        var values = new List<string>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in left.Values)
        {
            if (distinct.Add(value))
            {
                values.Add(value);
            }
        }

        foreach (var value in right.Values)
        {
            if (distinct.Add(value))
            {
                if (values.Count == ShellAnalysisLimits.MaxValueCandidates)
                {
                    return ShellValueDomain.Unknown;
                }

                values.Add(value);
            }
        }

        return values.Count switch
        {
            1 => new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = values.ToArray(),
            },
            > 1 => new ShellValueDomain
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = values.ToArray(),
            },
            _ => ShellValueDomain.Unknown,
        };
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private IReadOnlyList<EffectiveArgument> CreateEffectiveArguments(
        IReadOnlyList<ShellValueElementProvenance> provenance,
        Clause clause,
        AnalysisContext context,
        bool includeUnresolved)
    {
        var effective = new List<EffectiveArgument>();
        foreach (var value in provenance)
        {
            if (value.ClauseElementIndex >= 0 &&
                value.ClauseElementIndex < clause.Elements.Count &&
                PwshExecutionRegionBindingCatalog.IsScriptBlock(
                    clause.Elements[value.ClauseElementIndex]))
            {
                continue;
            }

            if (context.TryAnalyzeEffectiveValue(value.Value, out var domain))
            {
                effective.Add(new EffectiveArgument
                {
                    ClauseElementIndex = value.ClauseElementIndex,
                    Value = domain,
                });
            }
            else if (RequiresIndependentEffectiveValue(clause, value))
            {
                if (TryAnalyzeParserKnownValue(
                        value.Value,
                        EffectiveArgumentUsesNativeBinding(
                            value,
                            clause,
                            context),
                        context,
                        out domain) ||
                    IsPolicySensitiveValue(clause, value))
                {
                    effective.Add(new EffectiveArgument
                    {
                        ClauseElementIndex = value.ClauseElementIndex,
                        Value = domain,
                    });
                }
                else if (includeUnresolved && ContainsExpansion(value.Value))
                {
                    effective.Add(new EffectiveArgument
                    {
                        ClauseElementIndex = value.ClauseElementIndex,
                        Value = ShellValueDomain.Unknown,
                    });
                }
            }
            else if (includeUnresolved && ContainsExpansion(value.Value))
            {
                effective.Add(new EffectiveArgument
                {
                    ClauseElementIndex = value.ClauseElementIndex,
                    Value = ShellValueDomain.Unknown,
                });
            }
        }

        return effective.ToArray();
    }

    private static bool RequiresIndependentEffectiveValue(
        Clause clause,
        ShellValueElementProvenance provenance)
    {
        if (provenance.ClauseElementIndex < 0 ||
            provenance.ClauseElementIndex >= clause.Elements.Count)
        {
            return false;
        }

        var nonEmptyLiteralFragments = 0;
        var hasLiteralLexicalTransform = false;
        foreach (var fragment in provenance.Value.Fragments)
        {
            if (fragment.Kind != ShellValueFragmentKind.Literal)
            {
                return true;
            }

            if (fragment.Value.Length == 0)
            {
                continue;
            }

            nonEmptyLiteralFragments++;
            if (fragment.SourceLength != fragment.Value.Length)
            {
                hasLiteralLexicalTransform = true;
            }
        }

        var element = clause.Elements[provenance.ClauseElementIndex];
        if ((element.IsPath || element.IsFlag) &&
            (hasLiteralLexicalTransform || nonEmptyLiteralFragments > 1))
        {
            return true;
        }

        return HasShellSpecificPathSpelling(provenance.Value.Decoded);
    }

    private static bool IsPolicySensitiveValue(
        Clause clause,
        ShellValueElementProvenance provenance)
    {
        if (provenance.ClauseElementIndex < 0 ||
            provenance.ClauseElementIndex >= clause.Elements.Count)
        {
            return false;
        }

        var element = clause.Elements[provenance.ClauseElementIndex];

        return element.IsPath ||
            element.IsFlag ||
            HasShellSpecificPathSpelling(provenance.Value.Decoded) ||
            IsPolicySensitiveBinding(clause, provenance.ClauseElementIndex);
    }

    private static bool IsPolicySensitiveBinding(Clause clause, int targetElementIndex)
    {
        var canonicalVerb = clause.Verb.CanonicalVerb ??
            (clause.Verb.Tokens.Count == 0 ? null : clause.Verb.Tokens[0]);
        var cmdletStyle = IsCmdletStyle(clause);
        var positionalIndex = 0;
        string? pendingValue = null;

        for (var index = 0; index < clause.Elements.Count; index++)
        {
            var element = clause.Elements[index];
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (element.IsFlag)
            {
                if (index == targetElementIndex)
                {
                    return true;
                }

                var parameter = ParameterName(element.Value);
                if (cmdletStyle)
                {
                    var binding = PwshBindingTables.Resolve(canonicalVerb, parameter);
                    pendingValue = binding.Binding == PwshBinding.Value &&
                        !HasInlineParameterValue(element.Value)
                            ? binding.CanonicalName ?? parameter
                            : null;
                }
                else
                {
                    var verb = clause.Verb.Tokens.Count == 0
                        ? string.Empty
                        : clause.Verb.Tokens[0];
                    pendingValue = element.Value.IndexOf('=') < 0 &&
                        BashVerbs.FlagsWithValue.TryGetValue(verb, out var flags) &&
                        flags.Contains(parameter)
                            ? parameter
                            : null;
                }

                continue;
            }

            if (index == targetElementIndex)
            {
                if (pendingValue is not null)
                {
                    return true;
                }

                return cmdletStyle
                    ? PwshPerVerbRules.IsPositionalPathArg(
                        canonicalVerb,
                        !string.IsNullOrEmpty(canonicalVerb) &&
                            PwshVerbs.FileVerbs.Contains(canonicalVerb!),
                        positionalIndex,
                        element.Value)
                    : BashPerVerbRules.IsPositionalPathArg(
                        clause.Verb,
                        positionalIndex,
                        element.Value);
            }

            if (pendingValue is not null)
            {
                pendingValue = null;
            }
            else
            {
                positionalIndex++;
            }
        }

        return false;
    }

    private static string ParameterName(string value)
    {
        var colon = value.IndexOf(':');
        var equals = value.IndexOf('=');
        var separator = colon < 0
            ? equals
            : equals < 0
                ? colon
                : Math.Min(colon, equals);
        return separator < 0 ? value : value.Substring(0, separator);
    }

    private static bool HasInlineParameterValue(string value) =>
        value.IndexOf(':') >= 0 || value.IndexOf('=') >= 0;

    private bool TryAnalyzeParserKnownValue(
        ShellValue value,
        bool? usesNativeBinding,
        AnalysisContext context,
        out ShellValueDomain domain)
    {
        var homeDirectory = PwshResolver.GetHomeDirectory(_options);
        var composed = new StringBuilder(value.Decoded.Length);
        for (var fragmentIndex = 0;
             fragmentIndex < value.Fragments.Count;
             fragmentIndex++)
        {
            var fragment = value.Fragments[fragmentIndex];
            if (fragment.Kind == ShellValueFragmentKind.Literal)
            {
                composed.Append(fragment.Value);
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                fragment.Expansion is not ShellExpansionReference expansion)
            {
                domain = ShellValueDomain.Unknown;
                return false;
            }

            if (expansion.Kind == ShellExpansionKind.Tilde)
            {
                if ((fragment.AllowedTransforms & ShellLexicalTransform.Tilde) != 0)
                {
                    var expandsWholeArgumentTilde = fragmentIndex == 0 &&
                        composed.Length == 0 &&
                        (value.Decoded.Length == 1 ||
                         value.Decoded.Length > 1 &&
                         value.Decoded[1] is '/' or '\\');
                    if (!expandsWholeArgumentTilde)
                    {
                        composed.Append(fragment.Value);
                        continue;
                    }

                    if (usesNativeBinding is null)
                    {
                        domain = ShellValueDomain.Unknown;
                        return false;
                    }

                    if (usesNativeBinding == false)
                    {
                        composed.Append(fragment.Value);
                        continue;
                    }

                    if (!context.ConfiguredHomeAvailable || homeDirectory.Length == 0)
                    {
                        domain = ShellValueDomain.Unknown;
                        return false;
                    }

                    composed.Append(homeDirectory);
                }
                else
                {
                    composed.Append(fragment.Value);
                }

                continue;
            }

            if (expansion.Kind == ShellExpansionKind.Glob)
            {
                if ((fragment.AllowedTransforms & ShellLexicalTransform.Glob) != 0)
                {
                    if (usesNativeBinding is null)
                    {
                        domain = ShellValueDomain.Unknown;
                        return false;
                    }

                    if (usesNativeBinding == true)
                    {
                        domain = ShellValueDomain.Unknown;
                        return false;
                    }
                }

                composed.Append(fragment.Value);
                continue;
            }

            var hasKnownHomeVariable =
                PwshResolver.IsAutomaticHomeVariable(expansion.Name)
                    ? context.AutomaticHomeValueAvailable
                    : PwshResolver.IsUserProfileEnvironmentVariable(expansion.Name) &&
                        context.HomeEnvironmentAvailable &&
                        !context.ProcessWideStateInvalidated;
            if (expansion.Kind != ShellExpansionKind.Variable ||
                !hasKnownHomeVariable ||
                (fragment.AllowedTransforms & ShellLexicalTransform.Variable) == 0 ||
                homeDirectory.Length == 0)
            {
                domain = ShellValueDomain.Unknown;
                return false;
            }

            composed.Append(homeDirectory);
        }

        domain = new ShellValueDomain
        {
            Kind = ShellValueDomainKind.Exact,
            Values = new[] { composed.ToString() },
        };
        return true;
    }

    private static bool? EffectiveArgumentUsesNativeBinding(
        ShellValueElementProvenance provenance,
        Clause clause,
        AnalysisContext context)
    {
        if (clause.Verb.IsDynamic)
        {
            return null;
        }

        return context.HasConstrainedCommandResolutionBaseline &&
            IsCommandIdentityProven(clause, context)
                ? provenance.UsesNativeArgumentBinding
                : null;
    }

    private static bool HasShellSpecificPathSpelling(string value)
    {
        if (value == "~" || value.IndexOfAny(new[] { '*', '?', '[' }) >= 0)
        {
            return true;
        }

        if (value.IndexOf("::", StringComparison.Ordinal) > 0)
        {
            return true;
        }

        var colon = value.IndexOf(':');
        return colon > 1 &&
            colon + 1 < value.Length &&
            value[colon + 1] is '/' or '\\' &&
            !(value[colon + 1] == '/' &&
              colon + 2 < value.Length &&
              value[colon + 2] == '/');
    }

    private static bool ContainsExpansion(ShellValue value)
    {
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind != ShellValueFragmentKind.Literal)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsReference(IReadOnlyList<Clause> clauses, Clause expected)
    {
        foreach (var clause in clauses)
        {
            if (ReferenceEquals(clause, expected))
            {
                return true;
            }
        }

        return false;
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

    private readonly struct AnalysisContext
    {
        private readonly IReadOnlyList<BindingFrame> _bindings;
        private readonly IReadOnlyList<string> _runspaceProcessMutationCommandNames;

        internal AnalysisContext(
            string? workingDirectory,
            bool canPromote,
            bool hasConstrainedCommandResolutionBaseline,
            bool commandResolutionInvalidated,
            bool commandResolutionInvalidatedBeyondTrackedMutations,
            bool allRunspaceCommandResolutionMayReachProcessMutation,
            IReadOnlyList<string> runspaceProcessMutationCommandNames,
            bool processWideStateInvalidated,
            bool configuredHomeAvailable,
            bool homeEnvironmentAvailable,
            bool automaticHomeValueAvailable,
            IReadOnlyList<BindingFrame> bindings)
        {
            WorkingDirectory = workingDirectory;
            CanPromote = canPromote;
            HasConstrainedCommandResolutionBaseline =
                hasConstrainedCommandResolutionBaseline;
            CommandResolutionInvalidated = commandResolutionInvalidated;
            CommandResolutionInvalidatedBeyondTrackedMutations =
                commandResolutionInvalidatedBeyondTrackedMutations;
            AllRunspaceCommandResolutionMayReachProcessMutation =
                allRunspaceCommandResolutionMayReachProcessMutation;
            _runspaceProcessMutationCommandNames =
                runspaceProcessMutationCommandNames;
            ProcessWideStateInvalidated = processWideStateInvalidated;
            ConfiguredHomeAvailable = configuredHomeAvailable;
            HomeEnvironmentAvailable = homeEnvironmentAvailable;
            AutomaticHomeValueAvailable = automaticHomeValueAvailable;
            _bindings = bindings;
        }

        internal string? WorkingDirectory { get; }

        internal bool CanPromote { get; }

        internal bool HasConstrainedCommandResolutionBaseline { get; }

        internal bool CommandResolutionInvalidated { get; }

        internal bool CommandResolutionInvalidatedBeyondTrackedMutations { get; }

        internal bool AllRunspaceCommandResolutionMayReachProcessMutation { get; }

        internal bool ProcessWideStateInvalidated { get; }

        internal bool ConfiguredHomeAvailable { get; }

        internal bool HomeEnvironmentAvailable { get; }

        internal bool AutomaticHomeValueAvailable { get; }

        internal bool HasRunspaceCommandResolutionProcessRisk =>
            AllRunspaceCommandResolutionMayReachProcessMutation ||
            _runspaceProcessMutationCommandNames.Count > 0;

        internal bool MayResolveCommandToProcessMutation(Clause clause)
        {
            if (ProcessWideStateInvalidated ||
                AllRunspaceCommandResolutionMayReachProcessMutation)
            {
                return true;
            }

            if (clause.Verb.Tokens.Count != 1)
            {
                return _runspaceProcessMutationCommandNames.Count > 0;
            }

            var authoredName = clause.Verb.Tokens[0];
            var canonicalName = clause.Verb.CanonicalVerb;
            foreach (var commandName in _runspaceProcessMutationCommandNames)
            {
                if (commandName.Equals(
                        authoredName,
                        StringComparison.OrdinalIgnoreCase) ||
                    canonicalName is not null &&
                    commandName.Equals(
                        canonicalName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal AnalysisContext Invalidate(
            bool unknownCwd,
            bool invalidateCommandResolution = true)
        {
            var commandResolutionInvalidated = CommandResolutionInvalidated ||
                invalidateCommandResolution;
            if (_bindings.Count == 0)
            {
                return new AnalysisContext(
                    unknownCwd ? null : WorkingDirectory,
                    false,
                    HasConstrainedCommandResolutionBaseline,
                    commandResolutionInvalidated,
                    CommandResolutionInvalidatedBeyondTrackedMutations ||
                        invalidateCommandResolution,
                    AllRunspaceCommandResolutionMayReachProcessMutation,
                    _runspaceProcessMutationCommandNames,
                    ProcessWideStateInvalidated,
                    ConfiguredHomeAvailable,
                    HomeEnvironmentAvailable,
                    AutomaticHomeValueAvailable,
                    _bindings);
            }

            var unknown = new BindingFrame[_bindings.Count];
            for (var index = 0; index < unknown.Length; index++)
            {
                unknown[index] = new BindingFrame(
                    _bindings[index].Name,
                    ShellValueDomain.Unknown);
            }

            return new AnalysisContext(
                unknownCwd ? null : WorkingDirectory,
                false,
                HasConstrainedCommandResolutionBaseline,
                commandResolutionInvalidated,
                CommandResolutionInvalidatedBeyondTrackedMutations ||
                    invalidateCommandResolution,
                AllRunspaceCommandResolutionMayReachProcessMutation,
                _runspaceProcessMutationCommandNames,
                ProcessWideStateInvalidated,
                ConfiguredHomeAvailable,
                HomeEnvironmentAvailable,
                AutomaticHomeValueAvailable,
                unknown);
        }

        internal AnalysisContext WithRunspaceCommandResolutionProcessRisk() =>
            new(
                WorkingDirectory,
                CanPromote,
                HasConstrainedCommandResolutionBaseline,
                commandResolutionInvalidated: true,
                commandResolutionInvalidatedBeyondTrackedMutations: true,
                allRunspaceCommandResolutionMayReachProcessMutation: true,
                Array.Empty<string>(),
                ProcessWideStateInvalidated,
                ConfiguredHomeAvailable,
                HomeEnvironmentAvailable,
                AutomaticHomeValueAvailable,
                _bindings);

        internal AnalysisContext WithRunspaceCommandResolutionProcessRisk(
            bool invalidatesAll,
            IReadOnlyList<string> commandNames)
        {
            if (invalidatesAll)
            {
                return WithRunspaceCommandResolutionProcessRisk();
            }

            if (AllRunspaceCommandResolutionMayReachProcessMutation ||
                commandNames.Count == 0)
            {
                return this;
            }

            var names = MergeCommandNames(
                _runspaceProcessMutationCommandNames,
                commandNames,
                out var exceededLimit);
            return exceededLimit
                ? WithRunspaceCommandResolutionProcessRisk()
                : new AnalysisContext(
                    WorkingDirectory,
                    CanPromote,
                    HasConstrainedCommandResolutionBaseline,
                    commandResolutionInvalidated: true,
                    CommandResolutionInvalidatedBeyondTrackedMutations,
                    allRunspaceCommandResolutionMayReachProcessMutation: false,
                    names,
                    ProcessWideStateInvalidated,
                    ConfiguredHomeAvailable,
                    HomeEnvironmentAvailable,
                    AutomaticHomeValueAvailable,
                    _bindings);
        }

        internal AnalysisContext WithRunspaceCommandResolutionProcessRisk(
            AnalysisContext source) =>
            source.AllRunspaceCommandResolutionMayReachProcessMutation
                ? WithRunspaceCommandResolutionProcessRisk()
                : WithRunspaceCommandResolutionProcessRisk(
                    invalidatesAll: false,
                    source._runspaceProcessMutationCommandNames);

        internal AnalysisContext WithProcessWideStateInvalidated() =>
            new(
                WorkingDirectory,
                CanPromote,
                HasConstrainedCommandResolutionBaseline,
                commandResolutionInvalidated: true,
                commandResolutionInvalidatedBeyondTrackedMutations: true,
                AllRunspaceCommandResolutionMayReachProcessMutation,
                _runspaceProcessMutationCommandNames,
                processWideStateInvalidated: true,
                ConfiguredHomeAvailable,
                HomeEnvironmentAvailable,
                AutomaticHomeValueAvailable,
                _bindings);

        internal AnalysisContext WithConditionalProcessWideStateInvalidation(
            bool invalidated) => invalidated
                ? WithProcessWideStateInvalidated()
                : this;

        internal AnalysisContext WithoutConfiguredHome() =>
            new(
                WorkingDirectory,
                CanPromote,
                HasConstrainedCommandResolutionBaseline,
                CommandResolutionInvalidated,
                CommandResolutionInvalidatedBeyondTrackedMutations,
                AllRunspaceCommandResolutionMayReachProcessMutation,
                _runspaceProcessMutationCommandNames,
                ProcessWideStateInvalidated,
                configuredHomeAvailable: false,
                homeEnvironmentAvailable: false,
                automaticHomeValueAvailable: false,
                _bindings);

        internal AnalysisContext WithoutAutomaticHomeValue() =>
            new(
                WorkingDirectory,
                CanPromote,
                HasConstrainedCommandResolutionBaseline,
                CommandResolutionInvalidated,
                CommandResolutionInvalidatedBeyondTrackedMutations,
                AllRunspaceCommandResolutionMayReachProcessMutation,
                _runspaceProcessMutationCommandNames,
                ProcessWideStateInvalidated,
                ConfiguredHomeAvailable,
                HomeEnvironmentAvailable,
                automaticHomeValueAvailable: false,
                _bindings);

        internal AnalysisContext WithoutBindings() =>
            new(
                WorkingDirectory,
                CanPromote,
                HasConstrainedCommandResolutionBaseline,
                CommandResolutionInvalidated,
                CommandResolutionInvalidatedBeyondTrackedMutations,
                AllRunspaceCommandResolutionMayReachProcessMutation,
                _runspaceProcessMutationCommandNames,
                ProcessWideStateInvalidated,
                ConfiguredHomeAvailable,
                HomeEnvironmentAvailable,
                AutomaticHomeValueAvailable,
                Array.Empty<BindingFrame>());

        internal AnalysisContext CreateChildProcessInput() =>
            new(
                WorkingDirectory,
                canPromote: false,
                hasConstrainedCommandResolutionBaseline: false,
                commandResolutionInvalidated: ProcessWideStateInvalidated,
                commandResolutionInvalidatedBeyondTrackedMutations:
                    ProcessWideStateInvalidated,
                allRunspaceCommandResolutionMayReachProcessMutation: false,
                Array.Empty<string>(),
                ProcessWideStateInvalidated,
                HomeEnvironmentAvailable && !ProcessWideStateInvalidated,
                HomeEnvironmentAvailable && !ProcessWideStateInvalidated,
                HomeEnvironmentAvailable && !ProcessWideStateInvalidated,
                Array.Empty<BindingFrame>());

        internal AnalysisContext CreateDecodedHostInput() =>
            new(
                WorkingDirectory,
                canPromote: false,
                hasConstrainedCommandResolutionBaseline: false,
                commandResolutionInvalidated: ProcessWideStateInvalidated,
                commandResolutionInvalidatedBeyondTrackedMutations:
                    ProcessWideStateInvalidated,
                allRunspaceCommandResolutionMayReachProcessMutation: false,
                Array.Empty<string>(),
                ProcessWideStateInvalidated,
                HomeEnvironmentAvailable && !ProcessWideStateInvalidated,
                homeEnvironmentAvailable: false,
                automaticHomeValueAvailable: false,
                Array.Empty<BindingFrame>());

        internal AnalysisContext CreateRemoteInput() =>
            // The target host or persistent session can have arbitrary cwd,
            // variables, aliases, functions, modules, and profiles. Its exit
            // state is isolated from the invoking host.
            new(
                workingDirectory: null,
                canPromote: false,
                hasConstrainedCommandResolutionBaseline: false,
                commandResolutionInvalidated: true,
                commandResolutionInvalidatedBeyondTrackedMutations: true,
                allRunspaceCommandResolutionMayReachProcessMutation: false,
                Array.Empty<string>(),
                processWideStateInvalidated: false,
                configuredHomeAvailable: false,
                homeEnvironmentAvailable: false,
                automaticHomeValueAvailable: false,
                Array.Empty<BindingFrame>());

        internal AnalysisContext CreateChildRunspaceInput() =>
            // Caller aliases and ordinary variables do not initialize a Parallel
            // child runspace; explicit $using: values remain conservatively unknown.
            new(
                WorkingDirectory,
                CanPromote,
                HasConstrainedCommandResolutionBaseline,
                commandResolutionInvalidated: ProcessWideStateInvalidated,
                commandResolutionInvalidatedBeyondTrackedMutations:
                    ProcessWideStateInvalidated,
                allRunspaceCommandResolutionMayReachProcessMutation: false,
                Array.Empty<string>(),
                ProcessWideStateInvalidated,
                HomeEnvironmentAvailable && !ProcessWideStateInvalidated,
                HomeEnvironmentAvailable && !ProcessWideStateInvalidated,
                HomeEnvironmentAvailable && !ProcessWideStateInvalidated,
                Array.Empty<BindingFrame>());

        internal AnalysisContext WithCwd(string? workingDirectory) =>
            new(
                workingDirectory,
                CanPromote,
                HasConstrainedCommandResolutionBaseline,
                CommandResolutionInvalidated,
                CommandResolutionInvalidatedBeyondTrackedMutations,
                AllRunspaceCommandResolutionMayReachProcessMutation,
                _runspaceProcessMutationCommandNames,
                ProcessWideStateInvalidated,
                ConfiguredHomeAvailable,
                HomeEnvironmentAvailable,
                AutomaticHomeValueAvailable,
                _bindings);

        internal AnalysisContext WithBinding(
            string name,
            ShellValueDomain domain)
        {
            var bindings = new List<BindingFrame>(_bindings.Count + 1);
            foreach (var binding in _bindings)
            {
                if (!string.Equals(binding.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    bindings.Add(binding);
                }
            }

            bindings.Add(new BindingFrame(
                name,
                CanPromote ? domain : ShellValueDomain.Unknown));
            return new AnalysisContext(
                WorkingDirectory,
                CanPromote,
                HasConstrainedCommandResolutionBaseline,
                CommandResolutionInvalidated,
                CommandResolutionInvalidatedBeyondTrackedMutations,
                AllRunspaceCommandResolutionMayReachProcessMutation,
                _runspaceProcessMutationCommandNames,
                ProcessWideStateInvalidated,
                ConfiguredHomeAvailable,
                HomeEnvironmentAvailable,
                AutomaticHomeValueAvailable,
                bindings);
        }

        internal ShellValueDomain ToWorkingDirectoryDomain() =>
            WorkingDirectory is null
                ? ShellValueDomain.Unknown
                : new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { WorkingDirectory },
                };

        internal bool TryEvaluateValue(
            ShellValue value,
            out ShellValueDomain domain)
        {
            var literal = true;
            foreach (var fragment in value.Fragments)
            {
                if (fragment.Kind != ShellValueFragmentKind.Literal)
                {
                    literal = false;
                    break;
                }
            }

            if (literal)
            {
                domain = new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { value.Decoded },
                };
                return true;
            }

            return TryAnalyzeEffectiveValue(value, out domain);
        }

        internal bool CanResolveEveryExpansion(
            IReadOnlyList<ShellValueElementProvenance> provenance)
        {
            foreach (var value in provenance)
            {
                foreach (var fragment in value.Value.Fragments)
                {
                    if (fragment.Kind == ShellValueFragmentKind.Literal)
                    {
                        continue;
                    }

                    if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                        fragment.Expansion is not ShellExpansionReference expansion ||
                        expansion.Kind != ShellExpansionKind.Variable ||
                        expansion.Name is null ||
                        FindBinding(expansion.Name) is null)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        internal bool TryAnalyzeEffectiveValue(
            ShellValue value,
            out ShellValueDomain domain)
        {
            var referenced = new List<BindingFrame>();
            var unresolved = false;
            foreach (var fragment in value.Fragments)
            {
                if (fragment.Kind == ShellValueFragmentKind.Literal)
                {
                    continue;
                }

                if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                    fragment.Expansion is not ShellExpansionReference expansion ||
                    expansion.Kind != ShellExpansionKind.Variable ||
                    expansion.Name is null)
                {
                    unresolved = true;
                    continue;
                }

                var binding = FindBinding(expansion.Name);
                if (binding is null)
                {
                    unresolved = true;
                }
                else if (!ContainsReference(referenced, binding))
                {
                    referenced.Add(binding);
                }
            }

            if (referenced.Count == 0)
            {
                domain = ShellValueDomain.Unknown;
                return false;
            }

            if (unresolved)
            {
                domain = ShellValueDomain.Unknown;
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

            var selected = new Dictionary<BindingFrame, string>();
            var candidates = new List<string>();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            if (!TryCompose(
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

            domain = candidates.Count == 1
                ? new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { candidates[0] },
                }
                : new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.FiniteSet,
                    Values = candidates.ToArray(),
                };
            return true;
        }

        private bool TryCompose(
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
                if (!TryCompose(
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
                    fragment.Expansion is not ShellExpansionReference expansion ||
                    expansion.Kind != ShellExpansionKind.Variable ||
                    expansion.Name is null)
                {
                    return null;
                }

                var binding = FindBinding(expansion.Name);
                if (binding is null || !selected.TryGetValue(binding, out var candidate))
                {
                    return null;
                }

                rendered.Append(candidate);
            }

            return rendered.ToString();
        }

        private BindingFrame? FindBinding(string name)
        {
            for (var index = _bindings.Count - 1; index >= 0; index--)
            {
                if (string.Equals(
                        _bindings[index].Name,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return _bindings[index];
                }
            }

            return null;
        }

        internal bool StateEquals(AnalysisContext other)
        {
            if (!string.Equals(
                WorkingDirectory,
                    other.WorkingDirectory,
                    StringComparison.Ordinal) ||
                CanPromote != other.CanPromote ||
                HasConstrainedCommandResolutionBaseline !=
                    other.HasConstrainedCommandResolutionBaseline ||
                CommandResolutionInvalidated != other.CommandResolutionInvalidated ||
                CommandResolutionInvalidatedBeyondTrackedMutations !=
                    other.CommandResolutionInvalidatedBeyondTrackedMutations ||
                AllRunspaceCommandResolutionMayReachProcessMutation !=
                    other.AllRunspaceCommandResolutionMayReachProcessMutation ||
                ProcessWideStateInvalidated != other.ProcessWideStateInvalidated ||
                ConfiguredHomeAvailable != other.ConfiguredHomeAvailable ||
                HomeEnvironmentAvailable != other.HomeEnvironmentAvailable ||
                AutomaticHomeValueAvailable != other.AutomaticHomeValueAvailable ||
                _runspaceProcessMutationCommandNames.Count !=
                    other._runspaceProcessMutationCommandNames.Count ||
                _bindings.Count != other._bindings.Count)
            {
                return false;
            }

            foreach (var commandName in _runspaceProcessMutationCommandNames)
            {
                if (!ContainsCommandName(
                        other._runspaceProcessMutationCommandNames,
                        commandName))
                {
                    return false;
                }
            }

            foreach (var binding in _bindings)
            {
                var otherBinding = other.FindBinding(binding.Name);
                if (otherBinding is null ||
                    !DomainEquals(binding.Domain, otherBinding.Domain))
                {
                    return false;
                }
            }

            return true;
        }

        internal static AnalysisContext Join(
            AnalysisContext left,
            AnalysisContext right)
        {
            var names = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in left._bindings)
            {
                if (seenNames.Add(binding.Name))
                {
                    names.Add(binding.Name);
                }
            }

            foreach (var binding in right._bindings)
            {
                if (seenNames.Add(binding.Name))
                {
                    names.Add(binding.Name);
                }
            }

            var bindings = new List<BindingFrame>(names.Count);
            foreach (var name in names)
            {
                var leftBinding = left.FindBinding(name);
                var rightBinding = right.FindBinding(name);
                bindings.Add(new BindingFrame(
                    name,
                    leftBinding is null || rightBinding is null
                        ? ShellValueDomain.Unknown
                        : JoinDomains(leftBinding.Domain, rightBinding.Domain)));
            }

            var commandNames = MergeCommandNames(
                left._runspaceProcessMutationCommandNames,
                right._runspaceProcessMutationCommandNames,
                out var commandNameLimitExceeded);
            var invalidatesAllCommandNames =
                left.AllRunspaceCommandResolutionMayReachProcessMutation ||
                right.AllRunspaceCommandResolutionMayReachProcessMutation ||
                commandNameLimitExceeded;
            return new AnalysisContext(
                string.Equals(
                    left.WorkingDirectory,
                    right.WorkingDirectory,
                    StringComparison.Ordinal)
                    ? left.WorkingDirectory
                    : null,
                left.CanPromote && right.CanPromote,
                left.HasConstrainedCommandResolutionBaseline &&
                    right.HasConstrainedCommandResolutionBaseline,
                left.CommandResolutionInvalidated ||
                    right.CommandResolutionInvalidated,
                left.CommandResolutionInvalidatedBeyondTrackedMutations ||
                    right.CommandResolutionInvalidatedBeyondTrackedMutations,
                invalidatesAllCommandNames,
                invalidatesAllCommandNames
                    ? Array.Empty<string>()
                    : commandNames,
                left.ProcessWideStateInvalidated ||
                    right.ProcessWideStateInvalidated,
                left.ConfiguredHomeAvailable && right.ConfiguredHomeAvailable,
                left.HomeEnvironmentAvailable && right.HomeEnvironmentAvailable,
                left.AutomaticHomeValueAvailable &&
                    right.AutomaticHomeValueAvailable,
                bindings);
        }

        private static IReadOnlyList<string> MergeCommandNames(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right,
            out bool exceededLimit)
        {
            exceededLimit = false;
            var names = new List<string>(left.Count + right.Count);
            foreach (var commandName in left)
            {
                names.Add(commandName);
            }

            foreach (var commandName in right)
            {
                if (ContainsCommandName(names, commandName))
                {
                    continue;
                }

                if (names.Count == ShellAnalysisLimits.MaxValueCandidates)
                {
                    exceededLimit = true;
                    return Array.Empty<string>();
                }

                names.Add(commandName);
            }

            return names.ToArray();
        }

        private static bool ContainsCommandName(
            IReadOnlyList<string> commandNames,
            string expected)
        {
            foreach (var commandName in commandNames)
            {
                if (commandName.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static AnalysisContext Widen(
            AnalysisContext left,
            AnalysisContext right) => Join(left, right);

        internal static AnalysisContext? JoinNullable(
            AnalysisContext? left,
            AnalysisContext? right)
        {
            if (left is null)
            {
                return right;
            }

            if (right is null)
            {
                return left;
            }

            return Join(left.Value, right.Value);
        }

        private static bool DomainEquals(
            ShellValueDomain left,
            ShellValueDomain right)
        {
            if (left.Kind != right.Kind || left.Values.Count != right.Values.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Values.Count; index++)
            {
                if (!string.Equals(
                        left.Values[index],
                        right.Values[index],
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ContainsReference(
            IReadOnlyList<BindingFrame> bindings,
            BindingFrame expected)
        {
            foreach (var binding in bindings)
            {
                if (ReferenceEquals(binding, expected))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private readonly struct PwshFlowResult
    {
        internal PwshFlowResult(
            AnalysisContext? onSuccess,
            AnalysisContext? onFailure)
        {
            OnSuccess = onSuccess;
            OnFailure = onFailure;
        }

        internal AnalysisContext? OnSuccess { get; }

        internal AnalysisContext? OnFailure { get; }

        internal AnalysisContext? JoinedState =>
            AnalysisContext.JoinNullable(OnSuccess, OnFailure);

        internal static PwshFlowResult Both(AnalysisContext state) => new(state, state);

        internal static PwshFlowResult Success(AnalysisContext state) => new(state, null);
    }

    private sealed class ClauseReferenceComparer : IEqualityComparer<Clause>
    {
        internal static ClauseReferenceComparer Instance { get; } = new();

        public bool Equals(Clause? x, Clause? y) => ReferenceEquals(x, y);

        public int GetHashCode(Clause obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
