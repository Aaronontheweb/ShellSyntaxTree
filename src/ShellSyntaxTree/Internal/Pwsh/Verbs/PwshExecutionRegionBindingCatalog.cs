// -----------------------------------------------------------------------
// <copyright file="PwshExecutionRegionBindingCatalog.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShellSyntaxTree.Internal.Pwsh.Verbs;

internal enum PwshExecutionRegionBindingStatus
{
    NotApplicable,
    ProvedData,
    ProvedExecution,
    InvalidParameterSet,
    Ambiguous,
}

internal enum PwshExecutionRegionReceiver
{
    Unknown,
    ForEachObject,
    WhereObject,
    InvokeCommand,
    MeasureCommand,
    TraceCommand,
    StartJob,
    NewModule,
    SetPSBreakpoint,
    RegisterObjectEvent,
    RegisterEngineEvent,
    RegisterArgumentCompleter,
    StartThreadJob,
    WriteOutput,
}

internal enum PwshExecutionRegionParameterSet
{
    Unknown,
    ForEachScriptBlock,
    ForEachParallel,
    WhereScriptBlock,
    InvokeInProcess,
    InvokeRemote,
    MeasureExpression,
    TraceExpression,
    StartJobScriptBlock,
    StartJobFilePath,
    NewModuleScriptBlock,
    Breakpoint,
    ObjectEvent,
    EngineEvent,
    ArgumentCompleter,
    ThreadJobScriptBlock,
    ThreadJobFilePath,
}

internal enum PwshParameterValueKind
{
    String,
    Object,
    ScriptBlock,
    Switch,
    Int32,
    Enum,
    Uri,
    Guid,
    Version,
    Hashtable,
    RuntimeObject,
}

internal readonly record struct PwshExecutionRegionBinding(
    int HostClauseElementIndex,
    string? CanonicalParameterName,
    ExecutionRegionPhase Phase,
    ExecutionRegionTiming Timing,
    ExecutionRegionCardinality Cardinality,
    bool IsComplete);

internal sealed record PwshExecutionRegionBindingResult
{
    internal PwshExecutionRegionBindingStatus Status { get; init; }

    internal PwshExecutionRegionReceiver Receiver { get; init; }

    internal PwshExecutionRegionParameterSet ParameterSet { get; init; }

    internal string? CanonicalCommandName { get; init; }

    internal bool HasExplicitInputObject { get; init; }

    internal bool HasNoNewScope { get; init; }

    internal bool HasUseNewRunspace { get; init; }

    internal int? WorkingDirectoryElementIndex { get; init; }

    internal int WorkingDirectoryValueOffset { get; init; }

    internal bool HasExplicitPSVersion { get; init; }

    internal IReadOnlyList<PwshExecutionRegionBinding> Bindings { get; init; } =
        Array.Empty<PwshExecutionRegionBinding>();
}

/// <summary>
/// Version-pinned PowerShell 7.6.4 command and parameter metadata used to
/// identify script-block arguments. This is intentionally separate from the
/// parser's broad v0.2 path-binding table: only this closed catalog is allowed
/// to prove that a script block executes or is data.
/// </summary>
internal static class PwshExecutionRegionBindingCatalog
{
    internal const string PinnedPowerShellVersion = "7.6.4";

    internal const string PinnedThreadJobModuleVersion = "2.2.0";

    private static readonly HashSet<string> CommonParameters = Names(
        "Debug,ErrorAction,ErrorVariable,InformationAction,InformationVariable," +
        "OutBuffer,OutVariable,PipelineVariable,ProgressAction,Verbose," +
        "WarningAction,WarningVariable");

    private static readonly HashSet<string> CommonSwitchParameters = Names("Debug,Verbose");

    private static readonly HashSet<string> ScriptBlockParameters = Names(
        "Action,Begin,End,Expression,FilterScript,InitializationScript,Parallel," +
        "Process,RemainingScripts,ScriptBlock");

    private static readonly HashSet<string> SwitchParameters = Names(
        "AllowRedirection,AsCustomObject,AsJob,CContains,CEQ,CGE,CGT,CIn,CLE,CLT," +
        "CLike,CMatch,CNE,CNotContains,CNotIn,CNotLike,CNotMatch,Confirm,Contains," +
        "Debug,Debugger,EnableNetworkAccess,EQ,Force,Forward,GE,GT,HideComputerName," +
        "In,InDisconnectedSession,Is,IsNot,LE,Like,LT,Match,Native,NativeFallback," +
        "NE,NoEnumerate,NoNewScope,Not,NotContains,NotIn,NotLike,NotMatch,PSHost," +
        "RemoteDebug,ReturnResult,RunAs32,RunAsAdministrator,SSHTransport,SupportEvent," +
        "UseNewRunspace,UseSSL,Verbose,WhatIf");

    private static readonly HashSet<string> Int32Parameters = Names(
        "Column,ConnectingTimeout,Line,MaxTriggerCount,OutBuffer,Port,ThrottleLimit," +
        "TimeoutSeconds");

    private static readonly HashSet<string> EnumParameters = Names(
        "Authentication,ErrorAction,InformationAction,ListenerOption,Mode,Option," +
        "ProgressAction,WarningAction");

    private static readonly HashSet<string> ObjectParameters = Names(
        "ArgumentList,InputObject,MessageData,Value");

    private static readonly HashSet<string> RuntimeObjectParameters = Names(
        "Credential,Runspace,Session,SessionOption,SSHConnection,StreamingHost");

    private static readonly IReadOnlyDictionary<string, string> CommonParameterAliases = Aliases(
        "db=Debug,ea=ErrorAction,ev=ErrorVariable,infa=InformationAction," +
        "iv=InformationVariable,ob=OutBuffer,ov=OutVariable,pv=PipelineVariable," +
        "proga=ProgressAction,vb=Verbose,wa=WarningAction,wv=WarningVariable");

    private static readonly IReadOnlyDictionary<string, CommandEntry> Commands =
        new Dictionary<string, CommandEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["ForEach-Object"] = Entry(
                PwshExecutionRegionReceiver.ForEachObject,
                "Microsoft.PowerShell.Core",
                "ArgumentList,AsJob,Begin,Confirm,End,InputObject,MemberName,Parallel," +
                "Process,RemainingScripts,ThrottleLimit,TimeoutSeconds,UseNewRunspace,WhatIf",
                "AsJob,Confirm,UseNewRunspace,WhatIf",
                aliases: "Args=ArgumentList,cf=Confirm,wi=WhatIf"),
            ["Where-Object"] = Entry(
                PwshExecutionRegionReceiver.WhereObject,
                "Microsoft.PowerShell.Core",
                "CContains,CEQ,CGE,CGT,CIn,CLE,CLT,CLike,CMatch,CNE,CNotContains," +
                "CNotIn,CNotLike,CNotMatch,Contains,EQ,FilterScript,GE,GT,In,InputObject," +
                "Is,IsNot,LE,LT,Like,Match,NE,Not,NotContains,NotIn,NotLike,NotMatch," +
                "Property,Value",
                "CContains,CEQ,CGE,CGT,CIn,CLE,CLT,CLike,CMatch,CNE,CNotContains," +
                "CNotIn,CNotLike,CNotMatch,Contains,EQ,GE,GT,In,Is,IsNot,LE,LT,Like," +
                "Match,NE,Not,NotContains,NotIn,NotLike,NotMatch",
                aliases: "IContains=Contains,IEQ=EQ,IGE=GE,IGT=GT,IIn=In,ILE=LE," +
                "ILike=Like,ILT=LT,IMatch=Match,INE=NE,INotContains=NotContains," +
                "INotIn=NotIn,INotLike=NotLike,INotMatch=NotMatch"),
            ["Invoke-Command"] = Entry(
                PwshExecutionRegionReceiver.InvokeCommand,
                "Microsoft.PowerShell.Core",
                "AllowRedirection,ApplicationName,ArgumentList,AsJob,Authentication," +
                "CertificateThumbprint,ComputerName,ConfigurationName,ConnectingTimeout," +
                "ConnectionUri,ContainerId,Credential,EnableNetworkAccess,FilePath," +
                "HideComputerName,HostName,InDisconnectedSession,InputObject,JobName," +
                "KeyFilePath,NoNewScope,Options,Port,RemoteDebug,RunAsAdministrator," +
                "ScriptBlock,Session,SessionName,SessionOption,SSHConnection,SSHTransport," +
                "Subsystem,ThrottleLimit,UseSSL,UserName,VMId,VMName",
                "AllowRedirection,AsJob,EnableNetworkAccess,HideComputerName," +
                "InDisconnectedSession,NoNewScope,RemoteDebug,RunAsAdministrator," +
                "SSHTransport,UseSSL",
                aliases: "Args=ArgumentList,Cn=ComputerName,URI=ConnectionUri," +
                "CU=ConnectionUri,PSPath=FilePath,HCN=HideComputerName," +
                "Disconnected=InDisconnectedSession,IdentityFilePath=KeyFilePath," +
                "Command=ScriptBlock,VMGuid=VMId"),
            ["Measure-Command"] = Entry(
                PwshExecutionRegionReceiver.MeasureCommand,
                "Microsoft.PowerShell.Utility",
                "Expression,InputObject",
                ""),
            ["Trace-Command"] = Entry(
                PwshExecutionRegionReceiver.TraceCommand,
                "Microsoft.PowerShell.Utility",
                "ArgumentList,Command,Debugger,Expression,FilePath,Force,InputObject," +
                "ListenerOption,Name,Option,PSHost",
                "Debugger,Force,PSHost",
                aliases: "Args=ArgumentList,PSPath=FilePath,Path=FilePath"),
            ["Start-Job"] = Entry(
                PwshExecutionRegionReceiver.StartJob,
                "Microsoft.PowerShell.Core",
                "ArgumentList,Authentication,ConnectingTimeout,Credential,DefinitionName," +
                "DefinitionPath,FilePath,InitializationScript,InputObject,LiteralPath,Name," +
                "Options,PSVersion,RunAs32,ScriptBlock,Type,WorkingDirectory",
                "RunAs32",
                aliases: "Args=ArgumentList,PSPath=LiteralPath,LP=LiteralPath," +
                "Command=ScriptBlock"),
            ["New-Module"] = Entry(
                PwshExecutionRegionReceiver.NewModule,
                "Microsoft.PowerShell.Core",
                "ArgumentList,AsCustomObject,Cmdlet,Function,Name,ReturnResult,ScriptBlock",
                "AsCustomObject,ReturnResult",
                aliases: "Args=ArgumentList"),
            ["Set-PSBreakpoint"] = Entry(
                PwshExecutionRegionReceiver.SetPSBreakpoint,
                "Microsoft.PowerShell.Utility",
                "Action,Column,Command,Line,Mode,Runspace,Script,Variable",
                "",
                aliases: "C=Command,V=Variable"),
            ["Register-ObjectEvent"] = Entry(
                PwshExecutionRegionReceiver.RegisterObjectEvent,
                "Microsoft.PowerShell.Utility",
                "Action,EventName,Forward,InputObject,MaxTriggerCount,MessageData," +
                "SourceIdentifier,SupportEvent",
                "Forward,SupportEvent"),
            ["Register-EngineEvent"] = Entry(
                PwshExecutionRegionReceiver.RegisterEngineEvent,
                "Microsoft.PowerShell.Utility",
                "Action,Forward,MaxTriggerCount,MessageData,SourceIdentifier,SupportEvent",
                "Forward,SupportEvent"),
            ["Register-ArgumentCompleter"] = Entry(
                PwshExecutionRegionReceiver.RegisterArgumentCompleter,
                "Microsoft.PowerShell.Core",
                "CommandName,Native,NativeFallback,ParameterName,ScriptBlock",
                "Native,NativeFallback"),
            ["Start-ThreadJob"] = Entry(
                PwshExecutionRegionReceiver.StartThreadJob,
                "Microsoft.PowerShell.ThreadJob",
                "ArgumentList,FilePath,InitializationScript,InputObject,Name,ScriptBlock," +
                "StreamingHost,ThrottleLimit",
                ""),
            ["Write-Output"] = Entry(
                PwshExecutionRegionReceiver.WriteOutput,
                "Microsoft.PowerShell.Utility",
                "InputObject,NoEnumerate",
                "NoEnumerate"),
        };

    internal static bool IsSupportedModuleQualifiedCommand(string command)
    {
        var separator = command.LastIndexOf('\\');
        if (separator <= 0 || separator + 1 >= command.Length)
        {
            return false;
        }

        var module = command.Substring(0, separator);
        var name = command.Substring(separator + 1);
        return Commands.TryGetValue(name, out var entry)
            && string.Equals(module, entry.ModuleName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryResolveStaticCommandName(
        string command,
        out string? canonicalName,
        PwshDialect dialect)
    {
        canonicalName = command;
        var separator = command.LastIndexOf('\\');
        if (separator > 0)
        {
            if (!IsSupportedModuleQualifiedCommand(command))
            {
                canonicalName = null;
                return false;
            }

            canonicalName = command.Substring(separator + 1);
            return true;
        }

        var alias = PwshAliases.Resolve(command, dialect);
        if (alias is not null)
        {
            canonicalName = alias;
        }

        return Commands.ContainsKey(canonicalName);
    }

    internal static PwshExecutionRegionBindingResult Bind(
        Clause clause,
        bool commandIdentityProven,
        PwshDialect dialect,
        bool threadJobModuleProven = false)
    {
        var scriptBlocks = FindScriptBlocks(clause.Elements);
        if (scriptBlocks.Count == 0)
        {
            return new PwshExecutionRegionBindingResult
            {
                Status = PwshExecutionRegionBindingStatus.NotApplicable,
            };
        }

        if (!TryResolveCommand(clause.Verb, dialect, out var canonicalName, out var entry))
        {
            return Unknown(canonicalName, scriptBlocks);
        }

        if (!commandIdentityProven)
        {
            return Ambiguous(canonicalName, entry.Receiver, scriptBlocks);
        }

        if (entry.Receiver == PwshExecutionRegionReceiver.StartThreadJob
            && !threadJobModuleProven)
        {
            return Ambiguous(canonicalName, entry.Receiver, scriptBlocks);
        }

        if (entry.Receiver == PwshExecutionRegionReceiver.WriteOutput)
        {
            return new PwshExecutionRegionBindingResult
            {
                Status = PwshExecutionRegionBindingStatus.ProvedData,
                Receiver = entry.Receiver,
                CanonicalCommandName = canonicalName,
            };
        }

        if (dialect == PwshDialect.WindowsPowerShell51)
        {
            // The 5.1 callback/job parameter catalogs are intentionally not
            // inferred from the PowerShell 7 metadata table. Keep completely
            // delimited bodies visible, but incomplete, until each receiver
            // table is proved against the Windows PowerShell oracle.
            return Ambiguous(canonicalName, entry.Receiver, scriptBlocks);
        }

        var arguments = BindArguments(clause.Elements, entry);
        return BindReceiver(canonicalName!, entry, arguments, scriptBlocks);
    }

    private static PwshExecutionRegionBindingResult BindReceiver(
        string canonicalName,
        CommandEntry command,
        BoundArguments arguments,
        IReadOnlyList<int> scriptBlocks)
    {
        var receiver = command.Receiver;
        var compatibleSets = command.GetCompatibleParameterSets(arguments);
        if (arguments.HasAmbiguousScriptBlockBinding
            || arguments.HasDuplicateParameter
            || arguments.HasInvalidScalarScriptBlockArray
            || HasReceiverValidationConflict(receiver, arguments))
        {
            return Ambiguous(canonicalName, receiver, scriptBlocks);
        }

        if (compatibleSets.Count == 0)
        {
            var result = Ambiguous(canonicalName, receiver, scriptBlocks);
            return receiver == PwshExecutionRegionReceiver.InvokeCommand &&
                arguments.HasNamed("AsJob")
                    ? result with
                    {
                        Status = PwshExecutionRegionBindingStatus.InvalidParameterSet,
                    }
                    : result;
        }

        var bindings = new List<PwshExecutionRegionBinding>();
        var parameterSet = PwshExecutionRegionParameterSet.Unknown;
        switch (receiver)
        {
            case PwshExecutionRegionReceiver.ForEachObject:
                parameterSet = BindForEach(arguments, bindings);
                break;
            case PwshExecutionRegionReceiver.WhereObject:
                parameterSet = BindSingle(
                    arguments,
                    bindings,
                    "FilterScript",
                    PwshExecutionRegionParameterSet.WhereScriptBlock,
                    ExecutionRegionPhase.Filter,
                    ExecutionRegionTiming.Synchronous,
                    ExecutionRegionCardinality.OncePerInputObject,
                    positionalSlot: 0);
                break;
            case PwshExecutionRegionReceiver.InvokeCommand:
                parameterSet = BindInvokeCommand(arguments, bindings, compatibleSets);
                break;
            case PwshExecutionRegionReceiver.MeasureCommand:
                parameterSet = BindSingle(
                    arguments,
                    bindings,
                    "Expression",
                    PwshExecutionRegionParameterSet.MeasureExpression,
                    ExecutionRegionPhase.Main,
                    ExecutionRegionTiming.Synchronous,
                    ExecutionRegionCardinality.Once,
                    positionalSlot: 0);
                break;
            case PwshExecutionRegionReceiver.TraceCommand:
                parameterSet = BindTraceCommand(arguments, bindings);
                break;
            case PwshExecutionRegionReceiver.StartJob:
                parameterSet = BindStartJob(arguments, bindings);
                break;
            case PwshExecutionRegionReceiver.NewModule:
                parameterSet = BindNewModule(arguments, bindings);
                break;
            case PwshExecutionRegionReceiver.SetPSBreakpoint:
                parameterSet = BindNamed(
                    arguments,
                    bindings,
                    "Action",
                    PwshExecutionRegionParameterSet.Breakpoint,
                    ExecutionRegionPhase.Action,
                    ExecutionRegionTiming.Deferred,
                    ExecutionRegionCardinality.ZeroOrMore);
                break;
            case PwshExecutionRegionReceiver.RegisterObjectEvent:
                parameterSet = BindEvent(
                    arguments,
                    bindings,
                    PwshExecutionRegionParameterSet.ObjectEvent,
                    "InputObject", "EventName", "SourceIdentifier");
                break;
            case PwshExecutionRegionReceiver.RegisterEngineEvent:
                parameterSet = BindEvent(
                    arguments,
                    bindings,
                    PwshExecutionRegionParameterSet.EngineEvent,
                    "SourceIdentifier");
                break;
            case PwshExecutionRegionReceiver.RegisterArgumentCompleter:
                parameterSet = BindNamed(
                    arguments,
                    bindings,
                    "ScriptBlock",
                    PwshExecutionRegionParameterSet.ArgumentCompleter,
                    ExecutionRegionPhase.Completion,
                    ExecutionRegionTiming.Deferred,
                    ExecutionRegionCardinality.ZeroOrMore);
                break;
            case PwshExecutionRegionReceiver.StartThreadJob:
                parameterSet = BindThreadJob(arguments, bindings);
                break;
        }

        if (bindings.Count != scriptBlocks.Count)
        {
            return Ambiguous(canonicalName, receiver, scriptBlocks);
        }

        return new PwshExecutionRegionBindingResult
        {
            Status = PwshExecutionRegionBindingStatus.ProvedExecution,
            Receiver = receiver,
            ParameterSet = parameterSet,
            CanonicalCommandName = canonicalName,
            HasExplicitInputObject = arguments.HasNamed("InputObject"),
            HasNoNewScope = receiver == PwshExecutionRegionReceiver.InvokeCommand &&
                arguments.IsSwitchEnabled("NoNewScope"),
            HasUseNewRunspace = receiver == PwshExecutionRegionReceiver.ForEachObject &&
                arguments.IsSwitchEnabled("UseNewRunspace"),
            WorkingDirectoryElementIndex = receiver == PwshExecutionRegionReceiver.StartJob
                ? arguments.FirstNamedArgumentElementIndex("WorkingDirectory")
                : null,
            WorkingDirectoryValueOffset = receiver == PwshExecutionRegionReceiver.StartJob
                ? arguments.FirstNamedArgumentValueOffset("WorkingDirectory")
                : 0,
            HasExplicitPSVersion = receiver == PwshExecutionRegionReceiver.StartJob &&
                arguments.HasNamed("PSVersion"),
            Bindings = bindings.OrderBy(binding => binding.HostClauseElementIndex).ToArray(),
        };
    }

    private static PwshExecutionRegionParameterSet BindForEach(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings)
    {
        if (arguments.HasNamed("Parallel"))
        {
            AddNamed(
                arguments,
                bindings,
                "Parallel",
                ExecutionRegionPhase.Process,
                ExecutionRegionTiming.Concurrent,
                ExecutionRegionCardinality.OncePerInputObject);
            return PwshExecutionRegionParameterSet.ForEachParallel;
        }

        var hasBegin = AddNamed(arguments, bindings, "Begin", ExecutionRegionPhase.Begin,
            ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.Once);
        var hasEnd = AddNamed(arguments, bindings, "End", ExecutionRegionPhase.End,
            ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.Once);

        var positionalBlocks = arguments.PositionalScriptBlocks().ToArray();
        if (positionalBlocks.Length != arguments.PositionalArguments.Count
            || !positionalBlocks.Select((block, index) => block.Position == index)
                .All(value => value))
        {
            return PwshExecutionRegionParameterSet.Unknown;
        }

        var processBlocks = arguments.NamedScriptBlocks("Process")
            .Concat(arguments.NamedScriptBlocks("RemainingScripts"))
            .Concat(positionalBlocks)
            .GroupBy(block => block.ElementIndex)
            .Select(group => group.First())
            .OrderBy(block => block.ElementIndex)
            .ToArray();
        if (processBlocks.Length == 0)
        {
            return PwshExecutionRegionParameterSet.Unknown;
        }

        var firstProcessIndex = 0;
        var lastProcessIndex = processBlocks.Length - 1;
        if (hasBegin == 0 && processBlocks.Length > 1)
        {
            bindings.Add(Binding(processBlocks[0].ElementIndex, "Begin",
                ExecutionRegionPhase.Begin, ExecutionRegionTiming.Synchronous,
                ExecutionRegionCardinality.Once));
            firstProcessIndex++;
        }

        if (hasEnd == 0 && lastProcessIndex - firstProcessIndex + 1 > 1)
        {
            bindings.Add(Binding(processBlocks[lastProcessIndex].ElementIndex, "End",
                ExecutionRegionPhase.End, ExecutionRegionTiming.Synchronous,
                ExecutionRegionCardinality.Once));
            lastProcessIndex--;
        }

        for (var index = firstProcessIndex; index <= lastProcessIndex; index++)
        {
            bindings.Add(Binding(processBlocks[index].ElementIndex, "Process",
                ExecutionRegionPhase.Process, ExecutionRegionTiming.Synchronous,
                ExecutionRegionCardinality.OncePerInputObject));
        }

        return PwshExecutionRegionParameterSet.ForEachScriptBlock;
    }

    private static PwshExecutionRegionParameterSet BindInvokeCommand(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings,
        IReadOnlyList<ParameterSetDefinition> compatibleSets)
    {
        var remote = compatibleSets.Any(set => set.IsRemote);
        var inProcess = compatibleSets.Any(set => !set.IsRemote);
        if (remote == inProcess)
        {
            return PwshExecutionRegionParameterSet.Unknown;
        }

        var set = remote
            ? PwshExecutionRegionParameterSet.InvokeRemote
            : PwshExecutionRegionParameterSet.InvokeInProcess;
        var remoteTargetCardinality = remote
            ? GetRemoteTargetCardinality(arguments, compatibleSets)
            : RemoteTargetCardinality.Unknown;
        var timing = remote
            ? GetRemoteTiming(arguments, remoteTargetCardinality)
            : ExecutionRegionTiming.Synchronous;
        var cardinality = remoteTargetCardinality == RemoteTargetCardinality.Single
            ? ExecutionRegionCardinality.Once
            : remote
                ? ExecutionRegionCardinality.Unknown
                : ExecutionRegionCardinality.Once;
        var named = AddNamed(
            arguments,
            bindings,
            "ScriptBlock",
            ExecutionRegionPhase.Main,
            timing,
            cardinality);
        if (named > 0)
        {
            return set;
        }

        var block = arguments.FirstPositionalScriptBlockBoundTo(
            "ScriptBlock",
            compatibleSets[0]);
        if (block is BoundArgument boundBlock)
        {
            bindings.Add(Binding(
                boundBlock.ElementIndex,
                "ScriptBlock",
                ExecutionRegionPhase.Main,
                timing,
                cardinality));
        }

        return set;
    }

    private static ExecutionRegionTiming GetRemoteTiming(
        BoundArguments arguments,
        RemoteTargetCardinality targetCardinality)
    {
        if (arguments.IsSwitchEnabled("AsJob") ||
            arguments.IsSwitchEnabled("InDisconnectedSession") ||
            targetCardinality == RemoteTargetCardinality.Multiple)
        {
            return ExecutionRegionTiming.Concurrent;
        }

        return targetCardinality == RemoteTargetCardinality.Single
            ? ExecutionRegionTiming.Synchronous
            : ExecutionRegionTiming.Unknown;
    }

    private static RemoteTargetCardinality GetRemoteTargetCardinality(
        BoundArguments arguments,
        IReadOnlyList<ParameterSetDefinition> compatibleSets)
    {
        string? targetParameter = null;
        ParameterSetDefinition? selectedSet = null;
        foreach (var compatibleSet in compatibleSets)
        {
            var candidate = RemoteTargetParameterFor(compatibleSet.Name);
            if (candidate is null ||
                targetParameter is not null && !targetParameter.Equals(
                    candidate,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RemoteTargetCardinality.Unknown;
            }

            targetParameter = candidate;
            selectedSet = compatibleSet;
        }

        if (targetParameter is null || selectedSet is null)
        {
            return RemoteTargetCardinality.Unknown;
        }

        var targets = arguments.ArgumentsBoundTo(targetParameter, selectedSet);
        if (targets.Count == 0)
        {
            return RemoteTargetCardinality.Unknown;
        }

        if (targets.Count > 1 || targets.Any(TargetHasMultipleValues))
        {
            return RemoteTargetCardinality.Multiple;
        }

        return IsProvedScalarTarget(targets[0], targetParameter)
            ? RemoteTargetCardinality.Single
            : RemoteTargetCardinality.Unknown;
    }

    private static bool TargetHasMultipleValues(BoundArgument target)
    {
        if (target.HasTrailingComma)
        {
            return true;
        }

        var raw = target.RawValue.Trim();
        if (raw.Length == 0 || IsQuotedScalar(raw))
        {
            return false;
        }

        if (raw.StartsWith("@(", StringComparison.Ordinal) &&
            raw.EndsWith(")", StringComparison.Ordinal))
        {
            raw = raw.Substring(2, raw.Length - 3);
        }

        return ContainsTopLevelComma(raw);
    }

    private static bool ContainsTopLevelComma(string raw)
    {
        var parenthesisDepth = 0;
        var braceDepth = 0;
        var bracketDepth = 0;
        var quote = '\0';
        for (var index = 0; index < raw.Length; index++)
        {
            var current = raw[index];
            if (quote != '\0')
            {
                if (quote == '\'' && current == '\'' &&
                    index + 1 < raw.Length && raw[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                if (quote == '"' && current == '`' && index + 1 < raw.Length)
                {
                    index++;
                    continue;
                }

                if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current == '`' && index + 1 < raw.Length)
            {
                index++;
                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }

            switch (current)
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    if (parenthesisDepth > 0)
                    {
                        parenthesisDepth--;
                    }

                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                    }

                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                    {
                        bracketDepth--;
                    }

                    break;
                case ',' when parenthesisDepth == 0 &&
                    braceDepth == 0 && bracketDepth == 0:
                    return true;
            }
        }

        return false;
    }

    private static bool IsProvedScalarTarget(
        BoundArgument target,
        string targetParameter)
    {
        if (target.HasTrailingComma || TargetHasMultipleValues(target))
        {
            return false;
        }

        var raw = target.RawValue.Trim();
        if (IsQuotedScalar(raw))
        {
            return true;
        }

        if (targetParameter.Equals("SSHConnection", StringComparison.OrdinalIgnoreCase))
        {
            return raw.StartsWith("@{", StringComparison.Ordinal) &&
                raw.EndsWith("}", StringComparison.Ordinal);
        }

        if (targetParameter.Equals("ConnectionUri", StringComparison.OrdinalIgnoreCase) ||
            targetParameter.Equals("VMId", StringComparison.OrdinalIgnoreCase))
        {
            // Compatible parameter-set selection already proved the scalar
            // URI or GUID conversion; their punctuation can be DynamicSkip
            // in the general shell argument classifier.
            return true;
        }

        return target.Kind is ArgKind.Literal or ArgKind.Glob or ArgKind.Tilde;
    }

    private static bool IsQuotedScalar(string raw) =>
        raw.Length >= 2 &&
        raw[0] is '\'' or '"' &&
        raw[raw.Length - 1] == raw[0];

    private static string? RemoteTargetParameterFor(string parameterSetName) =>
        parameterSetName switch
        {
            "Session" => "Session",
            "ComputerName" => "ComputerName",
            "Uri" => "ConnectionUri",
            "VMId" => "VMId",
            "VMName" => "VMName",
            "SSHHost" => "HostName",
            "ContainerId" => "ContainerId",
            "SSHHostHashParam" => "SSHConnection",
            _ => null,
        };

    private static PwshExecutionRegionParameterSet BindTraceCommand(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings)
    {
        if (arguments.HasNamed("Command"))
        {
            return PwshExecutionRegionParameterSet.Unknown;
        }

        var named = AddNamed(arguments, bindings, "Expression", ExecutionRegionPhase.Main,
            ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.Once);
        if (named > 0)
        {
            return PwshExecutionRegionParameterSet.TraceExpression;
        }

        var expressionPosition = arguments.HasNamed("Name") ? 0 : 1;
        var block = arguments.FirstPositionalScriptBlockAt(expressionPosition);
        if (block is BoundArgument boundBlock)
        {
            bindings.Add(Binding(boundBlock.ElementIndex, "Expression", ExecutionRegionPhase.Main,
                ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.Once));
        }

        return PwshExecutionRegionParameterSet.TraceExpression;
    }

    private static PwshExecutionRegionParameterSet BindStartJob(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings)
    {
        var filePath = arguments.HasAnyNamed("FilePath", "LiteralPath");
        var definition = arguments.HasNamed("DefinitionName");
        var nonScriptSet = filePath || definition;
        if (!nonScriptSet)
        {
            AddNamed(arguments, bindings, "ScriptBlock", ExecutionRegionPhase.Main,
                ExecutionRegionTiming.Concurrent, ExecutionRegionCardinality.Once);
        }

        AddNamed(arguments, bindings, "InitializationScript", ExecutionRegionPhase.Initialization,
            ExecutionRegionTiming.Concurrent, ExecutionRegionCardinality.Once);

        var initializationPosition = arguments.HasNamed("ScriptBlock") || filePath ? 0 : 1;
        foreach (var block in arguments.PositionalScriptBlocks())
        {
            if (!nonScriptSet && block.Position == 0 && !arguments.HasNamed("ScriptBlock"))
            {
                bindings.Add(Binding(block.ElementIndex, "ScriptBlock", ExecutionRegionPhase.Main,
                    ExecutionRegionTiming.Concurrent, ExecutionRegionCardinality.Once));
            }
            else if (!definition
                && block.Position == initializationPosition
                && !arguments.HasNamed("InitializationScript"))
            {
                bindings.Add(Binding(block.ElementIndex, "InitializationScript",
                    ExecutionRegionPhase.Initialization, ExecutionRegionTiming.Concurrent,
                    ExecutionRegionCardinality.Once));
            }
        }

        return filePath
            ? PwshExecutionRegionParameterSet.StartJobFilePath
            : PwshExecutionRegionParameterSet.StartJobScriptBlock;
    }

    private static PwshExecutionRegionParameterSet BindNewModule(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings)
    {
        var named = AddNamed(arguments, bindings, "ScriptBlock",
            ExecutionRegionPhase.Initialization, ExecutionRegionTiming.Synchronous,
            ExecutionRegionCardinality.Once);
        if (named == 0)
        {
            var expectedPosition = arguments.HasNamed("Name") ? 0 :
                arguments.PositionalArguments.Any(value => value.Position == 0 && !value.IsScriptBlock)
                    ? 1
                    : 0;
            var block = arguments.FirstPositionalScriptBlockAt(expectedPosition);
            if (block is BoundArgument boundBlock)
            {
                bindings.Add(Binding(boundBlock.ElementIndex, "ScriptBlock",
                    ExecutionRegionPhase.Initialization, ExecutionRegionTiming.Synchronous,
                    ExecutionRegionCardinality.Once));
            }
        }

        return PwshExecutionRegionParameterSet.NewModuleScriptBlock;
    }

    private static PwshExecutionRegionParameterSet BindThreadJob(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings)
    {
        var filePath = arguments.HasNamed("FilePath");
        if (!filePath)
        {
            var named = AddNamed(arguments, bindings, "ScriptBlock", ExecutionRegionPhase.Main,
                ExecutionRegionTiming.Concurrent, ExecutionRegionCardinality.Once);
            if (named == 0)
            {
                var block = arguments.FirstPositionalScriptBlockAt(0);
                if (block is BoundArgument boundBlock)
                {
                    bindings.Add(Binding(boundBlock.ElementIndex, "ScriptBlock",
                        ExecutionRegionPhase.Main, ExecutionRegionTiming.Concurrent,
                        ExecutionRegionCardinality.Once));
                }
            }
        }

        AddNamed(arguments, bindings, "InitializationScript",
            ExecutionRegionPhase.Initialization, ExecutionRegionTiming.Concurrent,
            ExecutionRegionCardinality.Once);
        return filePath
            ? PwshExecutionRegionParameterSet.ThreadJobFilePath
            : PwshExecutionRegionParameterSet.ThreadJobScriptBlock;
    }

    private static PwshExecutionRegionParameterSet BindEvent(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings,
        PwshExecutionRegionParameterSet parameterSet,
        params string[] parametersBeforeAction)
    {
        var named = AddNamed(arguments, bindings, "Action", ExecutionRegionPhase.Action,
            ExecutionRegionTiming.Deferred, ExecutionRegionCardinality.ZeroOrMore);
        if (named == 0)
        {
            var expected = parametersBeforeAction.Count(parameter =>
                !arguments.HasNamed(parameter));
            var block = arguments.FirstPositionalScriptBlockAt(expected);
            if (block is BoundArgument boundBlock)
            {
                bindings.Add(Binding(boundBlock.ElementIndex, "Action", ExecutionRegionPhase.Action,
                    ExecutionRegionTiming.Deferred, ExecutionRegionCardinality.ZeroOrMore));
            }
        }

        return parameterSet;
    }

    private static bool HasReceiverValidationConflict(
        PwshExecutionRegionReceiver receiver,
        BoundArguments arguments)
    {
        if (receiver == PwshExecutionRegionReceiver.StartJob
            && arguments.HasNamed("RunAs32"))
        {
            // RunAs32 availability depends on the installed PowerShell host.
            return true;
        }

        if (receiver == PwshExecutionRegionReceiver.ForEachObject
            && arguments.HasNamed("AsJob")
            && arguments.HasNamed("TimeoutSeconds"))
        {
            return true;
        }

        return (receiver == PwshExecutionRegionReceiver.RegisterObjectEvent
                || receiver == PwshExecutionRegionReceiver.RegisterEngineEvent)
            && arguments.HasNamed("Forward");
    }

    private static PwshExecutionRegionParameterSet BindSingle(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings,
        string parameterName,
        PwshExecutionRegionParameterSet parameterSet,
        ExecutionRegionPhase phase,
        ExecutionRegionTiming timing,
        ExecutionRegionCardinality cardinality,
        int positionalSlot)
    {
        var named = AddNamed(arguments, bindings, parameterName, phase, timing, cardinality);
        if (named == 0)
        {
            var block = arguments.FirstPositionalScriptBlockAt(positionalSlot);
            if (block is BoundArgument boundBlock)
            {
                bindings.Add(Binding(
                    boundBlock.ElementIndex, parameterName, phase, timing, cardinality));
            }
        }

        return parameterSet;
    }

    private static PwshExecutionRegionParameterSet BindNamed(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings,
        string parameterName,
        PwshExecutionRegionParameterSet parameterSet,
        ExecutionRegionPhase phase,
        ExecutionRegionTiming timing,
        ExecutionRegionCardinality cardinality)
    {
        AddNamed(arguments, bindings, parameterName, phase, timing, cardinality);
        return parameterSet;
    }

    private static int AddNamed(
        BoundArguments arguments,
        List<PwshExecutionRegionBinding> bindings,
        string parameterName,
        ExecutionRegionPhase phase,
        ExecutionRegionTiming timing,
        ExecutionRegionCardinality cardinality,
        bool isComplete = true)
    {
        var count = 0;
        foreach (var argument in arguments.NamedScriptBlocks(parameterName))
        {
            bindings.Add(Binding(
                argument.ElementIndex,
                parameterName,
                phase,
                timing,
                cardinality,
                isComplete));
            count++;
        }

        return count;
    }

    private static PwshExecutionRegionBinding Binding(
        int elementIndex,
        string parameterName,
        ExecutionRegionPhase phase,
        ExecutionRegionTiming timing,
        ExecutionRegionCardinality cardinality,
        bool isComplete = true) =>
        new(elementIndex, parameterName, phase, timing, cardinality, isComplete);

    private static BoundArguments BindArguments(
        IReadOnlyList<ClauseElement> elements,
        CommandEntry command)
    {
        var result = new BoundArguments();
        var consumed = new HashSet<int>();
        var positionalIndex = 0;
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            if (element.Role != ClauseElementRole.Argument || consumed.Contains(index))
            {
                continue;
            }

            if (element.IsFlag)
            {
                var parameter = ParseParameter(element.Value);
                if (!parameter.IsSupportedSpelling)
                {
                    result.HasAmbiguousScriptBlockBinding = true;
                    continue;
                }

                var resolution = command.Resolve(parameter.Name);
                if (!resolution.IsKnown)
                {
                    // An unknown or ambiguous parameter can select a different
                    // parameter set or consume a later value. Once this clause
                    // contains a script block, no local adjacency heuristic is
                    // strong enough to retain proved receiver semantics.
                    result.HasAmbiguousScriptBlockBinding = true;
                    continue;
                }

                if (resolution.IsSwitch
                    && parameter.HasInlineSeparator
                    && !parameter.HasInlineValue)
                {
                    result.HasAmbiguousScriptBlockBinding = true;
                    continue;
                }

                result.AddNamedParameter(resolution.CanonicalName!);
                if (parameter.InlineScriptBlock)
                {
                    result.NamedArguments.Add(new BoundArgument(
                        index, -1, true, resolution.CanonicalName,
                        HasTrailingComma(element), parameter.InlineValue!,
                        InlineRawValue(element.Raw),
                        element.Kind,
                        element.Value.Length - parameter.InlineValue!.Length));
                    if (HasTrailingComma(element)
                        && !AcceptsScriptBlockArray(resolution.CanonicalName!))
                    {
                        result.HasInvalidScalarScriptBlockArray = true;
                    }

                    continue;
                }

                if (parameter.HasInlineValue)
                {
                    var hasTrailingComma = HasTrailingComma(element);
                    result.NamedArguments.Add(new BoundArgument(
                        index, -1, false, resolution.CanonicalName, hasTrailingComma,
                        parameter.InlineValue!,
                        InlineRawValue(element.Raw),
                        element.Kind,
                        element.Value.Length - parameter.InlineValue!.Length));
                    if (hasTrailingComma &&
                        AcceptsArgumentArray(resolution.CanonicalName!))
                    {
                        ConsumeNamedArrayContinuation(
                            elements,
                            index,
                            resolution.CanonicalName!,
                            consumed,
                            result);
                    }
                    else if (hasTrailingComma)
                    {
                        result.HasInvalidScalarScriptBlockArray = true;
                    }

                    continue;
                }

                if (resolution.IsSwitch ||
                    !TryFindNextArgument(elements, index + 1, out var valueIndex))
                {
                    if (!resolution.IsSwitch)
                    {
                        result.HasAmbiguousScriptBlockBinding = true;
                    }

                    continue;
                }

                if (elements[valueIndex].IsFlag)
                {
                    result.HasAmbiguousScriptBlockBinding = true;
                    continue;
                }

                consumed.Add(valueIndex);
                result.NamedArguments.Add(new BoundArgument(
                    valueIndex, -1, IsScriptBlock(elements[valueIndex]),
                    resolution.CanonicalName, HasTrailingComma(elements[valueIndex]),
                    elements[valueIndex].Value,
                    elements[valueIndex].Raw,
                    elements[valueIndex].Kind));
                if (HasTrailingComma(elements[valueIndex]) &&
                    AcceptsArgumentArray(resolution.CanonicalName!))
                {
                    ConsumeNamedArrayContinuation(
                        elements,
                        valueIndex,
                        resolution.CanonicalName!,
                        consumed,
                        result);
                }
                else if (HasTrailingComma(elements[valueIndex]) &&
                    !AcceptsScriptBlockArray(resolution.CanonicalName!))
                {
                    result.HasInvalidScalarScriptBlockArray = true;
                }

                continue;
            }

            result.PositionalArguments.Add(new BoundArgument(
                index, positionalIndex, IsScriptBlock(element), null,
                HasTrailingComma(element), element.Value, element.Raw, element.Kind));
            positionalIndex++;
        }

        return result;
    }

    private static void ConsumeNamedArrayContinuation(
        IReadOnlyList<ClauseElement> elements,
        int firstValueIndex,
        string parameterName,
        HashSet<int> consumed,
        BoundArguments result)
    {
        var valueIndex = firstValueIndex;
        while (HasTrailingComma(elements[valueIndex]))
        {
            if (!TryFindNextArgument(elements, valueIndex + 1, out var nextIndex) ||
                elements[nextIndex].IsFlag)
            {
                result.HasAmbiguousScriptBlockBinding = true;
                return;
            }

            consumed.Add(nextIndex);
            var next = elements[nextIndex];
            result.NamedArguments.Add(new BoundArgument(
                nextIndex,
                -1,
                IsScriptBlock(next),
                parameterName,
                HasTrailingComma(next),
                next.Value,
                next.Raw,
                next.Kind));
            valueIndex = nextIndex;
        }
    }

    private static bool TryFindNextArgument(
        IReadOnlyList<ClauseElement> elements,
        int start,
        out int index)
    {
        for (index = start; index < elements.Count; index++)
        {
            if (elements[index].Role == ClauseElementRole.Argument)
            {
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static ParsedParameter ParseParameter(string value)
    {
        if (value.Length < 2 || value[0] != '-' || value[1] == '-')
        {
            return new ParsedParameter(string.Empty, false, false, false, false, null);
        }

        var colon = value.IndexOf(':');
        if (colon < 0)
        {
            return new ParsedParameter(value.Substring(1), false, false, true, false, null);
        }

        var inlineValue = value.Substring(colon + 1);
        return new ParsedParameter(
            value.Substring(1, colon - 1),
            inlineValue.Length > 0,
            LooksLikeScriptBlock(inlineValue.Trim()),
            true,
            true,
            inlineValue);
    }

    private static bool AcceptsScriptBlockArray(string parameterName) =>
        string.Equals(parameterName, "Process", StringComparison.OrdinalIgnoreCase)
        || string.Equals(parameterName, "RemainingScripts", StringComparison.OrdinalIgnoreCase);

    private static bool AcceptsArgumentArray(string parameterName) =>
        parameterName.Equals("ComputerName", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("ConnectionUri", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("Session", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("VMId", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("VMName", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("HostName", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("ContainerId", StringComparison.OrdinalIgnoreCase) ||
        parameterName.Equals("SSHConnection", StringComparison.OrdinalIgnoreCase);

    private static string InlineRawValue(string raw)
    {
        var separator = raw.IndexOf(':');
        return separator < 0 ? raw : raw.Substring(separator + 1);
    }

    private static bool HasTrailingComma(ClauseElement element) =>
        element.Raw.TrimEnd().EndsWith(",", StringComparison.Ordinal);

    private static PwshParameterValueKind ValueKindFor(string parameterName)
    {
        if (ScriptBlockParameters.Contains(parameterName))
        {
            return PwshParameterValueKind.ScriptBlock;
        }

        if (SwitchParameters.Contains(parameterName))
        {
            return PwshParameterValueKind.Switch;
        }

        if (Int32Parameters.Contains(parameterName))
        {
            return PwshParameterValueKind.Int32;
        }

        if (EnumParameters.Contains(parameterName))
        {
            return PwshParameterValueKind.Enum;
        }

        if (ObjectParameters.Contains(parameterName))
        {
            return PwshParameterValueKind.Object;
        }

        if (RuntimeObjectParameters.Contains(parameterName))
        {
            return PwshParameterValueKind.RuntimeObject;
        }

        switch (parameterName)
        {
            case "ConnectionUri":
                return PwshParameterValueKind.Uri;
            case "VMId":
                return PwshParameterValueKind.Guid;
            case "PSVersion":
                return PwshParameterValueKind.Version;
            case "Options":
                return PwshParameterValueKind.Hashtable;
            default:
                return PwshParameterValueKind.String;
        }
    }

    private static bool CanConvert(
        BoundArgument argument,
        string parameterName)
    {
        var valueKind = ValueKindFor(parameterName);
        var value = argument.HasTrailingComma
            ? argument.Value.TrimEnd().TrimEnd(',')
            : argument.Value;
        switch (valueKind)
        {
            case PwshParameterValueKind.String:
                return !argument.IsScriptBlock
                    && IsStringLiteral(parameterName, value);
            case PwshParameterValueKind.Object:
                return !string.Equals(
                    value,
                    "$null",
                    StringComparison.OrdinalIgnoreCase);
            case PwshParameterValueKind.ScriptBlock:
                return argument.IsScriptBlock;
            case PwshParameterValueKind.Switch:
                return IsBooleanLiteral(parameterName, value);
            case PwshParameterValueKind.Int32:
                return IsInt32Literal(parameterName, value);
            case PwshParameterValueKind.Enum:
                return IsEnumLiteral(parameterName, value);
            case PwshParameterValueKind.Uri:
                return Uri.TryCreate(value, UriKind.Absolute, out _);
            case PwshParameterValueKind.Guid:
                return System.Guid.TryParse(value, out _);
            case PwshParameterValueKind.Version:
                return string.Equals(value, "5.1", StringComparison.Ordinal);
            case PwshParameterValueKind.Hashtable:
                return value.StartsWith("@{", StringComparison.Ordinal)
                    && value.EndsWith("}", StringComparison.Ordinal)
                    && value.Substring(2, value.Length - 3)
                        .Trim().Length > 0;
            case PwshParameterValueKind.RuntimeObject:
                return !argument.IsScriptBlock &&
                    argument.Kind is ArgKind.EnvVar or ArgKind.DynamicSkip &&
                    !string.Equals(value, "$null", StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static bool IsStringLiteral(string parameterName, string value)
    {
        if (value.Length == 0
            || string.Equals(value, "$null", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(parameterName, "WorkingDirectory",
                StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        if (parameterName.EndsWith("Variable", StringComparison.OrdinalIgnoreCase))
        {
            return IsSimpleVariableName(value);
        }

        return true;
    }

    private static bool IsSimpleVariableName(string value)
    {
        if (value.Length == 0
            || !(value[0] == '_' || char.IsLetter(value[0])))
        {
            return false;
        }

        return value.Skip(1).All(character =>
            character == '_' || char.IsLetterOrDigit(character));
    }

    private static bool IsBooleanLiteral(string parameterName, string value)
    {
        var trueValue = string.Equals(value, "$true", StringComparison.OrdinalIgnoreCase)
            || value == "1";
        if (string.Equals(parameterName, "SSHTransport", StringComparison.OrdinalIgnoreCase))
        {
            return trueValue;
        }

        return trueValue
            || string.Equals(value, "$false", StringComparison.OrdinalIgnoreCase)
            || value == "0";
    }

    private static bool IsInt32Literal(string parameterName, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        switch (parameterName)
        {
            case "ThrottleLimit":
                return parsed >= 1 && parsed <= 1_000_000;
            case "TimeoutSeconds":
                return parsed >= 0 && parsed <= 2_147_483;
            case "OutBuffer":
                return parsed >= 0;
            case "Port":
                return parsed >= 1 && parsed <= 65_535;
            case "Column":
            case "Line":
                return parsed >= 1;
            default:
                return true;
        }
    }

    private static bool IsEnumLiteral(string parameterName, string value)
    {
        string names;
        switch (parameterName)
        {
            case "Authentication":
                names = "Default";
                break;
            case "ListenerOption":
                names = "None,LogicalOperationStack,DateTime,Timestamp,ProcessId,ThreadId," +
                    "Callstack";
                break;
            case "Mode":
                names = "Read,Write,ReadWrite";
                break;
            case "Option":
                names = "None,Constructor,Dispose,Finalizer,Method,Property,Delegates,Events," +
                    "Exception,Lock,Error,Errors,Warning,Verbose,WriteLine,Data,Scope," +
                    "ExecutionFlow,Assert,All";
                break;
            default:
                names = "SilentlyContinue,Stop,Continue,Inquire,Ignore,Break";
                break;
        }

        var allowed = Names(names);
        var parts = value.Split(',').Select(part => part.Trim()).ToArray();
        return parts.Length > 0
            && parts.All(part => part.Length > 0 && allowed.Contains(part));
    }

    private static IReadOnlyList<int> FindScriptBlocks(IReadOnlyList<ClauseElement> elements)
    {
        var result = new List<int>();
        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index].Role == ClauseElementRole.Argument && IsScriptBlock(elements[index]))
            {
                result.Add(index);
            }
        }

        return result;
    }

    internal static bool IsScriptBlock(ClauseElement element)
    {
        if (element.Kind != ArgKind.DynamicSkip)
        {
            return false;
        }

        if (LooksLikeScriptBlock(element.Raw.Trim()))
        {
            return true;
        }

        var colon = element.Raw.IndexOf(':');
        return colon >= 0 && LooksLikeScriptBlock(element.Raw.Substring(colon + 1).Trim());
    }

    private static bool LooksLikeScriptBlock(string value) =>
        IsDelimitedScriptBlock(value)
        || value.Length >= 3 && value[value.Length - 1] == ','
        && IsDelimitedScriptBlock(value.Substring(0, value.Length - 1).TrimEnd());

    private static bool IsDelimitedScriptBlock(string value) =>
        value.Length >= 2 && value[0] == '{' && value[value.Length - 1] == '}';

    private static bool TryResolveCommand(
        VerbChain verb,
        PwshDialect dialect,
        out string? canonicalName,
        out CommandEntry entry)
    {
        canonicalName = verb.CanonicalVerb ?? verb.Tokens.FirstOrDefault();
        entry = default!;
        if (verb.IsDynamic || canonicalName is null)
        {
            return false;
        }

        return TryResolveStaticCommandName(canonicalName, out canonicalName, dialect)
            && Commands.TryGetValue(canonicalName!, out entry!);
    }

    private static PwshExecutionRegionBindingResult Unknown(
        string? canonicalName,
        IReadOnlyList<int> scriptBlocks) =>
        Ambiguous(canonicalName, PwshExecutionRegionReceiver.Unknown, scriptBlocks);

    private static PwshExecutionRegionBindingResult Ambiguous(
        string? canonicalName,
        PwshExecutionRegionReceiver receiver,
        IReadOnlyList<int> scriptBlocks) =>
        new()
        {
            Status = PwshExecutionRegionBindingStatus.Ambiguous,
            Receiver = receiver,
            ParameterSet = PwshExecutionRegionParameterSet.Unknown,
            CanonicalCommandName = canonicalName,
            Bindings = scriptBlocks.Select(index => new PwshExecutionRegionBinding(
                index,
                null,
                ExecutionRegionPhase.Unknown,
                ExecutionRegionTiming.Unknown,
                ExecutionRegionCardinality.Unknown,
                false)).ToArray(),
        };

    private static CommandEntry Entry(
        PwshExecutionRegionReceiver receiver,
        string moduleName,
        string parameters,
        string switches,
        string aliases = "") =>
        new(receiver, moduleName, Names(parameters), Names(switches), Aliases(aliases));

    private static HashSet<string> Names(string names)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(name.Trim());
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> Aliases(string aliases)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in aliases.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && separator + 1 < pair.Length)
            {
                result[pair.Substring(0, separator).Trim()] =
                    pair.Substring(separator + 1).Trim();
            }
        }

        return result;
    }

    private static IReadOnlyList<ParameterSetDefinition> ParameterSetsFor(
        PwshExecutionRegionReceiver receiver)
    {
        switch (receiver)
        {
            case PwshExecutionRegionReceiver.ForEachObject:
                return new[]
                {
                    Set("ScriptBlockSet",
                        "InputObject,Begin,Process,End,RemainingScripts,WhatIf,Confirm",
                        "Process", new[] { Pos("Process", acceptsScriptBlock: true, multiple: true) },
                        remaining: "RemainingScripts", remainingAcceptsScriptBlock: true),
                    Set("PropertyAndMethodSet",
                        "InputObject,MemberName,ArgumentList,WhatIf,Confirm",
                        "MemberName", new[] { Pos("MemberName") }, remaining: "ArgumentList"),
                    Set("ParallelParameterSet",
                        "InputObject,Parallel,ThrottleLimit,TimeoutSeconds,AsJob," +
                        "UseNewRunspace,WhatIf,Confirm",
                        "Parallel"),
                };
            case PwshExecutionRegionReceiver.WhereObject:
                return WhereParameterSets();
            case PwshExecutionRegionReceiver.InvokeCommand:
                return InvokeParameterSets();
            case PwshExecutionRegionReceiver.MeasureCommand:
                return new[]
                {
                    Set("__AllParameterSets", "InputObject,Expression", "Expression",
                        new[] { Pos("Expression", acceptsScriptBlock: true) }),
                };
            case PwshExecutionRegionReceiver.TraceCommand:
                return new[]
                {
                    Set("expressionSet",
                        "InputObject,Name,Option,Expression,ListenerOption,FilePath,Force," +
                        "Debugger,PSHost",
                        "Name,Expression",
                        new[] { Pos("Name"), Pos("Expression", true), Pos("Option") }),
                    Set("commandSet",
                        "InputObject,Name,Option,Command,ArgumentList,ListenerOption,FilePath," +
                        "Force,Debugger,PSHost",
                        "Name,Command",
                        new[] { Pos("Name"), Pos("Command"), Pos("Option") },
                        remaining: "ArgumentList"),
                };
            case PwshExecutionRegionReceiver.StartJob:
                return new[]
                {
                    Set("ComputerName",
                        "Name,ScriptBlock,Credential,Authentication,InitializationScript," +
                        "WorkingDirectory,RunAs32,PSVersion,InputObject,ArgumentList",
                        "ScriptBlock",
                        new[] { Pos("ScriptBlock", true), Pos("InitializationScript", true) }),
                    Set("DefinitionName",
                        "DefinitionName,DefinitionPath,Type,WorkingDirectory",
                        "DefinitionName",
                        new[] { Pos("DefinitionName"), Pos("DefinitionPath"), Pos("Type") }),
                    Set("FilePathComputerName",
                        "Name,Credential,FilePath,Authentication,InitializationScript," +
                        "WorkingDirectory,RunAs32,PSVersion,InputObject,ArgumentList",
                        "FilePath",
                        new[] { Pos("FilePath"), Pos("InitializationScript", true) }),
                    Set("LiteralFilePathComputerName",
                        "Name,Credential,LiteralPath,Authentication,InitializationScript," +
                        "WorkingDirectory,RunAs32,PSVersion,InputObject,ArgumentList",
                        "LiteralPath",
                        new[] { Pos("InitializationScript", true) }),
                    Set("SSHHost", "WorkingDirectory,ConnectingTimeout,Options", string.Empty),
                };
            case PwshExecutionRegionReceiver.NewModule:
                return new[]
                {
                    Set("ScriptBlock",
                        "ScriptBlock,Function,Cmdlet,ReturnResult,AsCustomObject,ArgumentList",
                        "ScriptBlock", new[] { Pos("ScriptBlock", true) },
                        remaining: "ArgumentList"),
                    Set("Name",
                        "Name,ScriptBlock,Function,Cmdlet,ReturnResult,AsCustomObject,ArgumentList",
                        "Name,ScriptBlock", new[] { Pos("Name"), Pos("ScriptBlock", true) },
                        remaining: "ArgumentList"),
                };
            case PwshExecutionRegionReceiver.SetPSBreakpoint:
                return new[]
                {
                    Set("Line", "Action,Column,Line,Script,Runspace", "Line,Script",
                        new[] { Pos("Script"), Pos("Line"), Pos("Column") }),
                    Set("Command", "Action,Command,Script,Runspace", "Command",
                        new[] { Pos("Script") }),
                    Set("Variable", "Action,Script,Variable,Mode,Runspace", "Variable",
                        new[] { Pos("Script") }),
                };
            case PwshExecutionRegionReceiver.RegisterObjectEvent:
                return new[]
                {
                    Set("__AllParameterSets",
                        "InputObject,EventName,SourceIdentifier,Action,MessageData,SupportEvent," +
                        "Forward,MaxTriggerCount",
                        "InputObject,EventName",
                        new[]
                        {
                            Pos("InputObject"), Pos("EventName"), Pos("SourceIdentifier"),
                            Pos("Action", true),
                        }),
                };
            case PwshExecutionRegionReceiver.RegisterEngineEvent:
                return new[]
                {
                    Set("__AllParameterSets",
                        "SourceIdentifier,Action,MessageData,SupportEvent,Forward,MaxTriggerCount",
                        "SourceIdentifier",
                        new[] { Pos("SourceIdentifier"), Pos("Action", true) }),
                };
            case PwshExecutionRegionReceiver.RegisterArgumentCompleter:
                return new[]
                {
                    Set("NativeCommandSet", "CommandName,ScriptBlock,Native",
                        "CommandName,ScriptBlock,Native"),
                    Set("PowerShellSet", "CommandName,ParameterName,ScriptBlock",
                        "ParameterName,ScriptBlock"),
                    Set("NativeFallbackSet", "ScriptBlock,NativeFallback",
                        "ScriptBlock,NativeFallback"),
                };
            case PwshExecutionRegionReceiver.StartThreadJob:
                return new[]
                {
                    Set("ScriptBlock",
                        "ScriptBlock,Name,InitializationScript,InputObject,ArgumentList," +
                        "ThrottleLimit,StreamingHost",
                        "ScriptBlock", new[] { Pos("ScriptBlock", true) }),
                    Set("FilePath",
                        "FilePath,Name,InitializationScript,InputObject,ArgumentList," +
                        "ThrottleLimit,StreamingHost",
                        "FilePath", new[] { Pos("FilePath") }),
                };
            case PwshExecutionRegionReceiver.WriteOutput:
                return new[]
                {
                    Set("__AllParameterSets", "InputObject,NoEnumerate", "InputObject",
                        new[] { Pos("InputObject") }, remaining: "InputObject"),
                };
            default:
                return Array.Empty<ParameterSetDefinition>();
        }
    }

    private static IReadOnlyList<ParameterSetDefinition> WhereParameterSets()
    {
        var result = new List<ParameterSetDefinition>
        {
            Set("ScriptBlockSet", "InputObject,FilterScript", "FilterScript",
                new[] { Pos("FilterScript", true) }),
            Set("EqualSet", "InputObject,Property,Value,EQ", "Property",
                new[] { Pos("Property"), Pos("Value") }),
        };
        var operators = new[]
        {
            "CEQ", "NE", "CNE", "GT", "CGT", "LT", "CLT", "GE", "CGE", "LE",
            "CLE", "Like", "CLike", "NotLike", "CNotLike", "Match", "CMatch",
            "NotMatch", "CNotMatch", "Contains", "CContains", "NotContains",
            "CNotContains", "In", "CIn", "NotIn", "CNotIn", "Is", "IsNot",
        };
        foreach (var operation in operators)
        {
            result.Add(Set(operation + "Set", "InputObject,Property,Value," + operation,
                "Property," + operation, new[] { Pos("Property"), Pos("Value") }));
        }

        result.Add(Set("Not", "InputObject,Property,Not", "Property,Not",
            new[] { Pos("Property") }));
        return result;
    }

    private static IReadOnlyList<ParameterSetDefinition> InvokeParameterSets() => new[]
    {
        Set("InProcess", "ScriptBlock,NoNewScope,InputObject,ArgumentList", "ScriptBlock",
            new[] { Pos("ScriptBlock", true) }),
        Set("Session",
            "Session,ThrottleLimit,AsJob,HideComputerName,JobName,ScriptBlock,RemoteDebug," +
            "InputObject,ArgumentList",
            "ScriptBlock", new[]
            {
                Pos("Session", acceptsCommaContinuation: true),
                Pos("ScriptBlock", true),
            },
            selectionRequiredAny: "Session", remote: true),
        Set("FilePathRunspace",
            "Session,ThrottleLimit,AsJob,HideComputerName,JobName,FilePath,RemoteDebug," +
            "InputObject,ArgumentList",
            "FilePath", new[]
            {
                Pos("Session", acceptsCommaContinuation: true), Pos("FilePath"),
            },
            selectionRequiredAny: "Session", remote: true),
        Set("ComputerName",
            "ComputerName,Credential,Port,UseSSL,ConfigurationName,ApplicationName," +
            "ThrottleLimit,AsJob,InDisconnectedSession,SessionName,HideComputerName," +
            "JobName,ScriptBlock,SessionOption,Authentication,EnableNetworkAccess," +
            "RemoteDebug,InputObject,ArgumentList,CertificateThumbprint",
            "ScriptBlock", new[]
            {
                Pos("ComputerName", acceptsCommaContinuation: true),
                Pos("ScriptBlock", true),
            },
            selectionRequiredAny: "ComputerName", remote: true),
        Set("FilePathComputerName",
            "ComputerName,Credential,Port,UseSSL,ConfigurationName,ApplicationName," +
            "ThrottleLimit,AsJob,InDisconnectedSession,SessionName,HideComputerName," +
            "JobName,FilePath,SessionOption,Authentication,EnableNetworkAccess,RemoteDebug," +
            "InputObject,ArgumentList",
            "FilePath", new[]
            {
                Pos("ComputerName", acceptsCommaContinuation: true), Pos("FilePath"),
            },
            selectionRequiredAny: "ComputerName", remote: true),
        Set("Uri",
            "Credential,ConfigurationName,ThrottleLimit,ConnectionUri,AsJob," +
            "InDisconnectedSession,HideComputerName,JobName,ScriptBlock,AllowRedirection," +
            "SessionOption,Authentication,EnableNetworkAccess,RemoteDebug,InputObject," +
            "ArgumentList,CertificateThumbprint",
            "ScriptBlock", new[]
            {
                Pos("ConnectionUri", acceptsCommaContinuation: true),
                Pos("ScriptBlock", true),
            },
            selectionRequiredAny: "ConnectionUri", remote: true),
        Set("FilePathUri",
            "Credential,ConfigurationName,ThrottleLimit,ConnectionUri,AsJob," +
            "InDisconnectedSession,HideComputerName,JobName,FilePath,AllowRedirection," +
            "SessionOption,Authentication,EnableNetworkAccess,RemoteDebug,InputObject," +
            "ArgumentList",
            "FilePath", new[]
            {
                Pos("ConnectionUri", acceptsCommaContinuation: true), Pos("FilePath"),
            },
            selectionRequiredAny: "ConnectionUri", remote: true),
        Set("VMId",
            "Credential,ConfigurationName,ThrottleLimit,AsJob,HideComputerName,ScriptBlock," +
            "RemoteDebug,InputObject,ArgumentList,VMId",
            "Credential,ScriptBlock,VMId", new[]
            {
                Pos("VMId", acceptsCommaContinuation: true), Pos("ScriptBlock", true),
            },
            remote: true),
        Set("VMName",
            "Credential,ConfigurationName,ThrottleLimit,AsJob,HideComputerName,ScriptBlock," +
            "RemoteDebug,InputObject,ArgumentList,VMName",
            "Credential,ScriptBlock,VMName", new[] { Pos("ScriptBlock", true) }, remote: true),
        Set("SSHHost",
            "Port,AsJob,HideComputerName,JobName,ScriptBlock,HostName,UserName,KeyFilePath," +
            "Subsystem,ConnectingTimeout,SSHTransport,Options,RemoteDebug,InputObject," +
            "ArgumentList",
            "ScriptBlock,HostName", new[] { Pos("ScriptBlock", true) }, remote: true),
        Set("ContainerId",
            "ConfigurationName,ThrottleLimit,AsJob,HideComputerName,JobName,ScriptBlock," +
            "RunAsAdministrator,RemoteDebug,InputObject,ArgumentList,ContainerId",
            "ScriptBlock,ContainerId", new[] { Pos("ScriptBlock", true) }, remote: true),
        Set("SSHHostHashParam",
            "AsJob,HideComputerName,JobName,ScriptBlock,SSHConnection,RemoteDebug," +
            "InputObject,ArgumentList",
            "ScriptBlock,SSHConnection", new[] { Pos("ScriptBlock", true) }, remote: true),
        Set("FilePathVMId",
            "Credential,ConfigurationName,ThrottleLimit,AsJob,HideComputerName,FilePath," +
            "RemoteDebug,InputObject,ArgumentList,VMId",
            "Credential,FilePath,VMId", new[]
            {
                Pos("VMId", acceptsCommaContinuation: true), Pos("FilePath"),
            }, remote: true),
        Set("FilePathVMName",
            "Credential,ConfigurationName,ThrottleLimit,AsJob,HideComputerName,FilePath," +
            "RemoteDebug,InputObject,ArgumentList,VMName",
            "Credential,FilePath,VMName", new[] { Pos("FilePath") }, remote: true),
        Set("FilePathContainerId",
            "ConfigurationName,ThrottleLimit,AsJob,HideComputerName,JobName,FilePath," +
            "RunAsAdministrator,RemoteDebug,InputObject,ArgumentList,ContainerId",
            "FilePath,ContainerId", remote: true),
        Set("FilePathSSHHost",
            "AsJob,HideComputerName,FilePath,HostName,UserName,KeyFilePath,Subsystem," +
            "ConnectingTimeout,SSHTransport,Options,RemoteDebug,InputObject,ArgumentList",
            "FilePath,HostName", remote: true),
        Set("FilePathSSHHostHash",
            "AsJob,HideComputerName,FilePath,SSHConnection,RemoteDebug,InputObject,ArgumentList",
            "FilePath,SSHConnection", remote: true),
    };

    private static ParameterSetDefinition Set(
        string name,
        string allowed,
        string mandatory,
        IReadOnlyList<PositionalParameter>? positionals = null,
        string? remaining = null,
        bool remainingAcceptsScriptBlock = false,
        string selectionRequiredAny = "",
        bool remote = false) =>
        new(name, Names(allowed), Names(mandatory),
            positionals ?? Array.Empty<PositionalParameter>(), remaining,
            remainingAcceptsScriptBlock, Names(selectionRequiredAny), remote);

    private static PositionalParameter Pos(
        string name,
        bool acceptsScriptBlock = false,
        bool multiple = false,
        bool acceptsCommaContinuation = false) =>
        new(name, acceptsScriptBlock, multiple, acceptsCommaContinuation);

    private readonly record struct ParsedParameter(
        string Name,
        bool HasInlineValue,
        bool InlineScriptBlock,
        bool IsSupportedSpelling,
        bool HasInlineSeparator,
        string? InlineValue);

    private readonly record struct BoundArgument(
        int ElementIndex,
        int Position,
        bool IsScriptBlock,
        string? ParameterName,
        bool HasTrailingComma,
        string Value,
        string RawValue,
        ArgKind Kind,
        int ValueOffset = 0);

    private sealed class BoundArguments
    {
        internal List<BoundArgument> NamedArguments { get; } = new();

        internal List<BoundArgument> PositionalArguments { get; } = new();

        internal HashSet<string> NamedParameters { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        internal bool HasAmbiguousScriptBlockBinding { get; set; }

        internal bool HasDuplicateParameter { get; private set; }

        internal bool HasInvalidScalarScriptBlockArray { get; set; }

        internal void AddNamedParameter(string name)
        {
            if (!NamedParameters.Add(name))
            {
                HasDuplicateParameter = true;
            }
        }

        internal bool HasNamed(string name) => NamedParameters.Contains(name);

        internal bool IsSwitchEnabled(string name)
        {
            if (!HasNamed(name))
            {
                return false;
            }

            foreach (var argument in NamedArguments)
            {
                if (!string.Equals(
                        argument.ParameterName,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return string.Equals(
                        argument.Value,
                        "$true",
                        StringComparison.OrdinalIgnoreCase) ||
                    argument.Value == "1";
            }

            return true;
        }

        internal bool HasAnyNamed(params string[] names) => names.Any(HasNamed);

        internal int? FirstNamedArgumentElementIndex(string parameterName)
        {
            foreach (var argument in NamedArguments)
            {
                if (string.Equals(
                        argument.ParameterName,
                        parameterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return argument.ElementIndex;
                }
            }

            return null;
        }

        internal int FirstNamedArgumentValueOffset(string parameterName)
        {
            foreach (var argument in NamedArguments)
            {
                if (string.Equals(
                        argument.ParameterName,
                        parameterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return argument.ValueOffset;
                }
            }

            return 0;
        }

        internal int CountNamed(params string[] names) => names.Count(HasNamed);

        internal IEnumerable<BoundArgument> NamedScriptBlocks(string parameterName) =>
            NamedArguments.Where(argument =>
                argument.IsScriptBlock && string.Equals(
                    argument.ParameterName,
                    parameterName,
                    StringComparison.OrdinalIgnoreCase));

        internal IEnumerable<BoundArgument> PositionalScriptBlocks() =>
            PositionalArguments.Where(argument => argument.IsScriptBlock);

        internal BoundArgument? FirstPositionalScriptBlockAt(int position)
        {
            foreach (var block in PositionalScriptBlocks())
            {
                if (block.Position == position)
                {
                    return block;
                }
            }

            return null;
        }

        internal BoundArgument? FirstPositionalScriptBlockBoundTo(
            string parameterName,
            ParameterSetDefinition parameterSet)
        {
            return parameterSet.BindPositionals(this)
                .Where(binding => string.Equals(
                    binding.ParameterName,
                    parameterName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(binding => (BoundArgument?)binding.Argument)
                .FirstOrDefault();
        }

        internal IReadOnlyList<BoundArgument> ArgumentsBoundTo(
            string parameterName,
            ParameterSetDefinition parameterSet)
        {
            var result = NamedArguments
                .Where(argument => string.Equals(
                    argument.ParameterName,
                    parameterName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            result.AddRange(parameterSet.BindPositionals(this)
                .Where(binding => string.Equals(
                    binding.ParameterName,
                    parameterName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(binding => binding.Argument));
            return result;
        }
    }

    private sealed class CommandEntry
    {
        private readonly HashSet<string> _parameters;
        private readonly HashSet<string> _switches;
        private readonly Dictionary<string, string> _aliases;
        private readonly IReadOnlyList<ParameterSetDefinition> _parameterSets;

        internal CommandEntry(
            PwshExecutionRegionReceiver receiver,
            string moduleName,
            HashSet<string> parameters,
            HashSet<string> switches,
            IReadOnlyDictionary<string, string> aliases)
        {
            Receiver = receiver;
            ModuleName = moduleName;
            _parameterSets = ParameterSetsFor(receiver);
            _parameters = new HashSet<string>(CommonParameters, StringComparer.OrdinalIgnoreCase);
            _parameters.UnionWith(parameters);
            _switches = new HashSet<string>(CommonSwitchParameters, StringComparer.OrdinalIgnoreCase);
            _switches.UnionWith(switches);
            _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in CommonParameterAliases)
            {
                _aliases[alias.Key] = alias.Value;
            }

            foreach (var alias in aliases)
            {
                _aliases[alias.Key] = alias.Value;
            }
        }

        internal PwshExecutionRegionReceiver Receiver { get; }

        internal string ModuleName { get; }

        internal IReadOnlyList<ParameterSetDefinition> GetCompatibleParameterSets(
            BoundArguments arguments) =>
            _parameterSets.Where(set => set.IsCompatible(arguments)).ToArray();

        internal ParameterResolution Resolve(string prefix)
        {
            var exact = _parameters.FirstOrDefault(name =>
                string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return new ParameterResolution(exact, _switches.Contains(exact), true);
            }

            if (_aliases.TryGetValue(prefix, out var exactAlias))
            {
                return new ParameterResolution(
                    exactAlias,
                    _switches.Contains(exactAlias),
                    true);
            }

            var matches = _parameters.Where(name =>
                    name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Concat(_aliases.Where(alias =>
                    alias.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(alias => alias.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return matches.Length == 1
                ? new ParameterResolution(matches[0], _switches.Contains(matches[0]), true)
                : new ParameterResolution(null, false, false);
        }
    }

    private sealed class ParameterSetDefinition
    {
        private readonly HashSet<string> _allowed;
        private readonly HashSet<string> _mandatory;
        private readonly IReadOnlyList<PositionalParameter> _positionals;
        private readonly string? _remaining;
        private readonly bool _remainingAcceptsScriptBlock;
        private readonly HashSet<string> _selectionRequiredAny;

        internal ParameterSetDefinition(
            string name,
            HashSet<string> allowed,
            HashSet<string> mandatory,
            IReadOnlyList<PositionalParameter> positionals,
            string? remaining,
            bool remainingAcceptsScriptBlock,
            HashSet<string> selectionRequiredAny,
            bool isRemote)
        {
            Name = name;
            _allowed = allowed;
            _mandatory = mandatory;
            _positionals = positionals;
            _remaining = remaining;
            _remainingAcceptsScriptBlock = remainingAcceptsScriptBlock;
            _selectionRequiredAny = selectionRequiredAny;
            IsRemote = isRemote;
        }

        internal string Name { get; }

        internal bool IsRemote { get; }

        internal bool IsCompatible(BoundArguments arguments)
        {
            if (arguments.NamedParameters.Any(parameter =>
                    !CommonParameters.Contains(parameter) && !_allowed.Contains(parameter)))
            {
                return false;
            }

            if (arguments.NamedArguments.Any(argument =>
                    !CanConvert(argument, argument.ParameterName!)))
            {
                return false;
            }

            if (!TryBindPositionals(arguments, out var positionalBindings))
            {
                return false;
            }

            var bound = new HashSet<string>(arguments.NamedParameters,
                StringComparer.OrdinalIgnoreCase);
            bound.UnionWith(positionalBindings.Select(binding => binding.ParameterName));
            return _mandatory.All(bound.Contains)
                && (_selectionRequiredAny.Count == 0
                    || _selectionRequiredAny.Any(bound.Contains));
        }

        internal IReadOnlyList<PositionalBinding> BindPositionals(BoundArguments arguments)
        {
            return TryBindPositionals(arguments, out var bindings)
                ? bindings
                : Array.Empty<PositionalBinding>();
        }

        private bool TryBindPositionals(
            BoundArguments arguments,
            out IReadOnlyList<PositionalBinding> bindings)
        {
            var result = new List<PositionalBinding>();
            var slot = 0;
            foreach (var argument in arguments.PositionalArguments)
            {
                while (slot < _positionals.Count
                    && arguments.HasNamed(_positionals[slot].Name))
                {
                    slot++;
                }

                if (slot < _positionals.Count)
                {
                    var positional = _positionals[slot];
                    if (argument.IsScriptBlock != positional.AcceptsScriptBlock
                        || !CanConvert(argument, positional.Name)
                        || (argument.HasTrailingComma &&
                            !positional.AcceptsMultiple &&
                            !positional.AcceptsCommaContinuation))
                    {
                        bindings = Array.Empty<PositionalBinding>();
                        return false;
                    }

                    result.Add(new PositionalBinding(argument, positional.Name));
                    if (!positional.AcceptsMultiple &&
                        !(positional.AcceptsCommaContinuation &&
                            argument.HasTrailingComma))
                    {
                        slot++;
                    }

                    continue;
                }

                if (_remaining is null
                    || argument.IsScriptBlock && !_remainingAcceptsScriptBlock)
                {
                    bindings = Array.Empty<PositionalBinding>();
                    return false;
                }

                result.Add(new PositionalBinding(argument, _remaining));
            }

            bindings = result;
            return true;
        }
    }

    private readonly record struct PositionalParameter(
        string Name,
        bool AcceptsScriptBlock,
        bool AcceptsMultiple,
        bool AcceptsCommaContinuation);

    private readonly record struct PositionalBinding(
        BoundArgument Argument,
        string ParameterName);

    private readonly record struct ParameterResolution(
        string? CanonicalName,
        bool IsSwitch,
        bool IsKnown);

    private enum RemoteTargetCardinality
    {
        Unknown,
        Single,
        Multiple,
    }
}
