// -----------------------------------------------------------------------
// <copyright file="PwshForEachValueAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
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
        out bool unknownCwd)
    {
        unknownCwd = false;
        var verb = clause.Verb.CanonicalVerb ??
            (clause.Verb.Tokens.Count == 0 ? null : clause.Verb.Tokens[0]);
        if (verb is null)
        {
            return false;
        }

        if (HasVariableWritingArgument(verb, clause))
        {
            return true;
        }

        if (IsProviderStateMutation(
                verb,
                clause,
                effectiveArguments,
                failOnUnproved: true))
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
        bool failOnUnproved)
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
            failOnUnproved);
    }

    private static bool HasMutableOrUnprovedProviderTarget(
        string verb,
        Clause clause,
        IReadOnlyList<EffectiveArgument> effectiveArguments,
        bool failOnUnproved)
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
                            failOnUnproved))
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
                    failOnUnproved))
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
        bool failOnUnproved)
    {
        if (IsMutableStateProviderPath(element.Value) ||
            IsMutableStateProviderPath(element.Raw) ||
            element.Resolved is not null &&
            IsMutableStateProviderPath(element.Resolved))
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
                if (IsMutableStateProviderPath(value))
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

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
    private bool _isComplete = true;
    private int _remainingLoopAnalysisTransitions = MaxLoopAnalysisTransitions;
    private long _executionRegionEffectCount;
    private long _nonRegionStateMutationCount;

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
                commandResolutionInvalidated: false,
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
            current,
            includeUnresolved: isForEachIncomplete ||
                current.CommandResolutionInvalidated);
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
            isForEachIncomplete,
            mayPromote);

        var location = AnalyzeSetLocation(simple, current);
        if (location is not null)
        {
            _nonRegionStateMutationCount++;
            if (PwshPersistentStateMutation.TryGetEffect(
                    simple.Clause,
                    effective,
                    out var locationEffectUnknownCwd))
            {
                var flow = location.Value;
                return ApplyExecutionRegionEffect(simple, new PwshFlowResult(
                    flow.OnSuccess is AnalysisContext success
                        ? success.Invalidate(locationEffectUnknownCwd)
                        : null,
                    flow.OnFailure is AnalysisContext failure
                        ? failure.Invalidate(locationEffectUnknownCwd)
                        : null));
            }

            return ApplyExecutionRegionEffect(simple, location.Value);
        }

        if (simple.Clause.Verb.IsDynamic)
        {
            _nonRegionStateMutationCount++;
            return ApplyExecutionRegionEffect(
                simple,
                PwshFlowResult.Both(current.Invalidate(unknownCwd: true)));
        }

        if (PwshPersistentStateMutation.TryGetEffect(
                simple.Clause,
                effective,
                out var unknownCwd))
        {
            _nonRegionStateMutationCount++;
            return ApplyExecutionRegionEffect(
                simple,
                PwshFlowResult.Both(current.Invalidate(unknownCwd)));
        }

        return ApplyExecutionRegionEffect(simple, PwshFlowResult.Both(current));
    }

    private PwshFlowResult ApplyExecutionRegionEffect(
        SimpleCommandSyntax simple,
        PwshFlowResult flow)
    {
        if (simple.ExecutionRegions.Count == 0)
        {
            return flow;
        }

        _executionRegionEffectCount++;
        return new PwshFlowResult(
            flow.OnSuccess is AnalysisContext success
                ? success.Invalidate(unknownCwd: true)
                : null,
            flow.OnFailure is AnalysisContext failure
                ? failure.Invalidate(unknownCwd: true)
                : null);
    }

    private void RecordFacts(
        SimpleCommandSyntax simple,
        AnalysisContext input,
        CommandOccurrenceFacts source,
        IReadOnlyList<EffectiveArgument> effective,
        bool isForEachIncomplete,
        bool mayPromote)
    {
        var current = new CommandOccurrenceFacts
        {
            EffectiveArguments = effective,
            WorkingDirectory = input.ToWorkingDirectoryDomain(),
            Redirects = source.Redirects,
            CwdPathDependencies = source.CwdPathDependencies,
            ValueProvenance = source.ValueProvenance,
            HasCompleteValueProvenance = source.HasCompleteValueProvenance,
            IsComplete = source.IsComplete &&
                !input.CommandResolutionInvalidated &&
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
            Redirects = source.Redirects,
            CwdPathDependencies = source.CwdPathDependencies,
            ValueProvenance = source.ValueProvenance,
            HasCompleteValueProvenance = source.HasCompleteValueProvenance,
            IsComplete = prior.IsComplete && current.IsComplete,
        };
    }

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
        foreach (var stage in pipeline.Stages)
        {
            var executionRegionEffectsBefore = _executionRegionEffectCount;
            var nonRegionMutationsBefore = _nonRegionStateMutationCount;
            var stageFlow = AnalyzeNode(stage, stageInput);
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

    private PwshFlowResult AnalyzeGroup(GroupSyntax group, AnalysisContext input)
    {
        if (group.GroupKind == ShellGroupKind.CurrentScope)
        {
            return AnalyzeBlock(group.Body, input);
        }

        var executionRegionEffectCount = _executionRegionEffectCount;
        var nonRegionStateMutationCount = _nonRegionStateMutationCount;
        AnalyzeBlock(
            group.Body,
            input.WithoutBindings().Invalidate(
                unknownCwd: false,
                invalidateCommandResolution: false));
        _executionRegionEffectCount = executionRegionEffectCount;
        _nonRegionStateMutationCount = nonRegionStateMutationCount;
        return PwshFlowResult.Both(input);
    }

    private PwshFlowResult AnalyzeSubstitution(
        CommandSubstitutionSyntax substitution,
        AnalysisContext input)
    {
        return AnalyzeBlock(substitution.Body, input);
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
        var source = _factsFactory(simple);
        foreach (var provenance in source.ValueProvenance)
        {
            if (provenance.ClauseElementIndex == elementIndex)
            {
                return input.TryEvaluateValue(provenance.Value, out domain);
            }
        }

        domain = ShellValueDomain.Unknown;
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
                Redirects = source.Redirects,
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

        var executionRegions = new ExecutionRegionSyntax[simple.ExecutionRegions.Count];
        for (var index = 0; index < executionRegions.Length; index++)
        {
            var region = simple.ExecutionRegions[index];
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
        facts.Add(clause, new CommandOccurrenceFacts
        {
            EffectiveArguments = source.EffectiveArguments,
            WorkingDirectory = source.WorkingDirectory,
            Redirects = source.Redirects,
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
                    Redirects = source.Redirects,
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

    private static IReadOnlyList<EffectiveArgument> CreateEffectiveArguments(
        IReadOnlyList<ShellValueElementProvenance> provenance,
        AnalysisContext context,
        bool includeUnresolved)
    {
        var effective = new List<EffectiveArgument>();
        foreach (var value in provenance)
        {
            if (context.TryAnalyzeEffectiveValue(value.Value, out var domain))
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

        return effective.ToArray();
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

        internal AnalysisContext(
            string? workingDirectory,
            bool canPromote,
            bool commandResolutionInvalidated,
            IReadOnlyList<BindingFrame> bindings)
        {
            WorkingDirectory = workingDirectory;
            CanPromote = canPromote;
            CommandResolutionInvalidated = commandResolutionInvalidated;
            _bindings = bindings;
        }

        internal string? WorkingDirectory { get; }

        internal bool CanPromote { get; }

        internal bool CommandResolutionInvalidated { get; }

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
                    commandResolutionInvalidated,
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
                commandResolutionInvalidated,
                unknown);
        }

        internal AnalysisContext WithoutBindings() =>
            new(
                WorkingDirectory,
                CanPromote,
                CommandResolutionInvalidated,
                Array.Empty<BindingFrame>());

        internal AnalysisContext WithCwd(string? workingDirectory) =>
            new(
                workingDirectory,
                CanPromote,
                CommandResolutionInvalidated,
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
                CommandResolutionInvalidated,
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
                CommandResolutionInvalidated != other.CommandResolutionInvalidated ||
                _bindings.Count != other._bindings.Count)
            {
                return false;
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

            return new AnalysisContext(
                string.Equals(
                    left.WorkingDirectory,
                    right.WorkingDirectory,
                    StringComparison.Ordinal)
                    ? left.WorkingDirectory
                    : null,
                left.CanPromote && right.CanPromote,
                left.CommandResolutionInvalidated ||
                    right.CommandResolutionInvalidated,
                bindings);
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
