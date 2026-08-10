// -----------------------------------------------------------------------
// <copyright file="PublicApiSnapshotTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ShellSyntaxTree.Tests;

/// <summary>
/// Locks the public API surface for v0.2.0. Any drift here means either
/// SPEC.md §2/§3 / SPEC.POWERSHELL.md §2/§3 was bumped (deliberate version
/// change) or a regression — both are blockers until reconciled.
/// </summary>
public class PublicApiSnapshotTests
{
    private static readonly Assembly LibAssembly = typeof(BashParser).Assembly;

    // -------- IShellParser --------

    [Fact]
    public void IShellParser_has_expected_shape()
    {
        var t = typeof(IShellParser);
        Assert.True(t.IsInterface);
        Assert.True(t.IsPublic);
        Assert.Equal("ShellSyntaxTree", t.Namespace);

        var methods = t.GetMethods();
        var parse = Assert.Single(methods, m => m.Name == "Parse");
        Assert.Equal(typeof(ParsedCommand), parse.ReturnType);
        var parameters = parse.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        AssertReferenceNullability(parse.ReturnParameter, NullabilityState.NotNull);
        AssertReferenceNullability(parameters[0], NullabilityState.NotNull);
    }

    // -------- BashParser --------

    [Fact]
    public void BashParser_has_expected_shape()
    {
        var t = typeof(BashParser);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        Assert.True(t.IsClass);
        Assert.Contains(typeof(IShellParser), t.GetInterfaces());

        var ctors = t.GetConstructors().OrderBy(c => c.GetParameters().Length).ToArray();
        Assert.Equal(2, ctors.Length);

        Assert.Empty(ctors[0].GetParameters());

        var withOptions = ctors[1].GetParameters();
        Assert.Single(withOptions);
        Assert.Equal(typeof(BashParserOptions), withOptions[0].ParameterType);

        var parse = t.GetMethod(nameof(BashParser.Parse), new[] { typeof(string) });
        Assert.NotNull(parse);
        Assert.Equal(typeof(ParsedCommand), parse!.ReturnType);
        Assert.Equal(
            new[] { nameof(BashParser.Parse) },
            DeclaredPublicMethodNames(t));
        AssertReferenceNullability(parse.ReturnParameter, NullabilityState.NotNull);
        AssertReferenceNullability(
            Assert.Single(parse.GetParameters()),
            NullabilityState.NotNull);
        AssertReferenceNullability(withOptions[0], NullabilityState.NotNull);
    }

    [Fact]
    public void BashParser_Parse_throws_ArgumentNullException_on_null()
    {
        var parser = new BashParser();
        Assert.Throws<ArgumentNullException>(() => parser.Parse(null!));
    }

    [Fact]
    public void BashParser_Parse_returns_ParsedCommand_for_simple_input()
    {
        var parser = new BashParser();
        var result = parser.Parse("ls");
        Assert.NotNull(result);
        Assert.Equal("ls", result.Source);
        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);
    }

    [Fact]
    public void BashParser_default_ctor_constructs_with_default_options()
    {
        // Smoke test: parameterless ctor doesn't throw.
        var parser = new BashParser();
        Assert.NotNull(parser);
    }

    [Fact]
    public void BashParser_options_ctor_constructs_with_supplied_options()
    {
        var parser = new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/tmp",
        });
        Assert.NotNull(parser);
    }

    // -------- PwshParser --------

    [Fact]
    public void PwshParser_has_expected_shape()
    {
        var t = typeof(PwshParser);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        Assert.True(t.IsClass);
        Assert.Contains(typeof(IShellParser), t.GetInterfaces());

        var ctors = t.GetConstructors().OrderBy(c => c.GetParameters().Length).ToArray();
        Assert.Equal(2, ctors.Length);

        Assert.Empty(ctors[0].GetParameters());

        var withOptions = ctors[1].GetParameters();
        Assert.Single(withOptions);
        Assert.Equal(typeof(PwshParserOptions), withOptions[0].ParameterType);

        var parse = t.GetMethod(nameof(PwshParser.Parse), new[] { typeof(string) });
        Assert.NotNull(parse);
        Assert.Equal(typeof(ParsedCommand), parse!.ReturnType);
        Assert.Equal(
            new[] { nameof(PwshParser.Parse) },
            DeclaredPublicMethodNames(t));
        AssertReferenceNullability(parse.ReturnParameter, NullabilityState.NotNull);
        AssertReferenceNullability(
            Assert.Single(parse.GetParameters()),
            NullabilityState.NotNull);
        AssertReferenceNullability(withOptions[0], NullabilityState.NotNull);
    }

    [Fact]
    public void PwshParser_Parse_throws_ArgumentNullException_on_null()
    {
        var parser = new PwshParser();
        Assert.Throws<ArgumentNullException>(() => parser.Parse(null!));
    }

    [Fact]
    public void PwshParser_Parse_returns_ParsedCommand_for_simple_input()
    {
        var parser = new PwshParser();
        var result = parser.Parse("Get-Date");
        Assert.NotNull(result);
        Assert.Equal("Get-Date", result.Source);
        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);
    }

    [Fact]
    public void PwshParser_options_ctor_constructs_with_supplied_options()
    {
        var parser = new PwshParser(new PwshParserOptions
        {
            HomeDirectory = "C:/Users/user",
            WorkingDirectory = "C:/work",
        });
        Assert.NotNull(parser);
    }

    // -------- ShellParserOptions --------

    [Fact]
    public void ShellParserOptions_has_expected_shape()
    {
        var t = typeof(ShellParserOptions);
        Assert.True(t.IsPublic);
        Assert.True(t.IsAbstract);
        AssertIsRecord(t);

        AssertInitProperty(t, "HomeDirectory", typeof(string), nullable: true);
        AssertInitProperty(t, "WorkingDirectory", typeof(string), nullable: true);

        var declaredProps = DeclaredInstanceProps(t)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(new[] { "HomeDirectory", "WorkingDirectory" }, declaredProps);
    }

    // -------- BashParserOptions --------

    [Fact]
    public void BashParserOptions_has_expected_shape()
    {
        var t = typeof(BashParserOptions);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        AssertIsRecord(t);
        Assert.Equal(typeof(ShellParserOptions), t.BaseType);

        // The resolver knobs are inherited from ShellParserOptions; the
        // object-initializer shape stays source-compatible with v0.1.
        AssertInitProperty(t, "HomeDirectory", typeof(string), nullable: true);
        AssertInitProperty(t, "WorkingDirectory", typeof(string), nullable: true);
        AssertInitProperty(t, "InitialStateMode", typeof(BashInitialStateMode));

        var declaredProps = DeclaredInstanceProps(t)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .ToArray();
        Assert.Equal(new[] { "InitialStateMode" }, declaredProps);
    }

    [Fact]
    public void BashInitialStateMode_has_expected_values()
    {
        Assert.Equal(0, (int)BashInitialStateMode.Unknown);
        Assert.Equal(1, (int)BashInitialStateMode.IsolatedNonInteractive);
    }

    // -------- PwshParserOptions --------

    [Fact]
    public void PwshParserOptions_has_expected_shape()
    {
        var t = typeof(PwshParserOptions);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        AssertIsRecord(t);
        Assert.Equal(typeof(ShellParserOptions), t.BaseType);

        AssertInitProperty(t, "HomeDirectory", typeof(string), nullable: true);
        AssertInitProperty(t, "WorkingDirectory", typeof(string), nullable: true);
        AssertInitProperty(t, "InitialStateMode", typeof(PwshInitialStateMode));
        AssertInitProperty(t, "Dialect", typeof(PwshDialect));

        var declaredProps = DeclaredInstanceProps(t)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .ToArray();
        Assert.Equal(new[] { "InitialStateMode", "Dialect" }, declaredProps);
    }

    [Fact]
    public void PwshInitialStateMode_has_expected_values_and_safe_default()
    {
        Assert.Equal(0, (int)PwshInitialStateMode.Unknown);
        Assert.Equal(1, (int)PwshInitialStateMode.IsolatedNonInteractiveNoProfile);
        Assert.Equal(PwshInitialStateMode.Unknown, new PwshParserOptions().InitialStateMode);
        Assert.Equal(
            PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
            new PwshParserOptions
            {
                InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
            }.InitialStateMode);
    }

    [Fact]
    public void PwshDialect_has_expected_values_and_compatible_default()
    {
        Assert.Equal(0, (int)PwshDialect.Unknown);
        Assert.Equal(1, (int)PwshDialect.PowerShell7);
        Assert.Equal(2, (int)PwshDialect.WindowsPowerShell51);
        Assert.Equal(PwshDialect.PowerShell7, new PwshParserOptions().Dialect);
    }

    [Fact]
    public void PwshDialect_participates_in_record_value_behavior()
    {
        var powerShell7 = new PwshParserOptions();
        var equivalent = new PwshParserOptions { Dialect = PwshDialect.PowerShell7 };
        var windowsPowerShell = new PwshParserOptions
        {
            Dialect = PwshDialect.WindowsPowerShell51,
        };

        Assert.Equal(powerShell7, equivalent);
        Assert.Equal(powerShell7.GetHashCode(), equivalent.GetHashCode());
        Assert.NotEqual(powerShell7, windowsPowerShell);
        Assert.Contains("Dialect = PowerShell7", powerShell7.ToString());
    }

    // -------- ParsedCommand --------

    [Fact]
    public void ParsedCommand_has_expected_shape()
    {
        var t = typeof(ParsedCommand);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        AssertIsRecord(t);

        AssertInitProperty(t, "Source", typeof(string));
        AssertInitProperty(t, "Syntax", typeof(ShellBlockSyntax));
        AssertInitProperty(t, "Commands", typeof(IReadOnlyList<CommandOccurrence>));
        AssertInitProperty(t, "Clauses", typeof(IReadOnlyList<Clause>));
        AssertInitProperty(t, "IsUnparseable", typeof(bool));
        AssertInitProperty(t, "UnparseableReason", typeof(string), nullable: true);

        var declaredProps = DeclaredInstanceProps(t)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "Clauses",
                "Commands",
                "IsUnparseable",
                "Source",
                "Syntax",
                "UnparseableReason",
            },
            declaredProps);

        var instance = new ParsedCommand();
        Assert.Equal("", instance.Source);
        Assert.NotNull(instance.Syntax);
        Assert.Equal(ShellSyntaxKind.Block, instance.Syntax.Kind);
        Assert.Empty(instance.Syntax.Statements);
        Assert.Empty(instance.Commands);
        Assert.Empty(instance.Clauses);
        Assert.False(instance.IsUnparseable);
        Assert.Null(instance.UnparseableReason);
    }

    // -------- Clause --------

    [Fact]
    public void Clause_has_expected_shape()
    {
        var t = typeof(Clause);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        AssertIsRecord(t);

        AssertInitProperty(t, "Operator", typeof(CompoundOperator));
        AssertInitProperty(t, "Verb", typeof(VerbChain));
        AssertInitProperty(t, "Args", typeof(IReadOnlyList<Arg>));
        AssertInitProperty(t, "Redirects", typeof(IReadOnlyList<Redirect>));
        AssertInitProperty(t, "Elements", typeof(IReadOnlyList<ClauseElement>));
        AssertInitProperty(t, "IsSubshell", typeof(bool));
        AssertInitProperty(t, "IsCommandStringWrapped", typeof(bool));
        AssertDeclaredPropertyNames(
            t,
            "Args",
            "Elements",
            "IsCommandStringWrapped",
            "IsSubshell",
            "Operator",
            "Redirects",
            "Verb");

        var instance = new Clause();
        Assert.Equal(CompoundOperator.None, instance.Operator);
        Assert.NotNull(instance.Verb);
        Assert.Empty(instance.Verb.Tokens);
        Assert.Empty(instance.Args);
        Assert.Empty(instance.Redirects);
        Assert.Empty(instance.Elements);
        Assert.False(instance.IsSubshell);
        Assert.False(instance.IsCommandStringWrapped);
    }

    // -------- ClauseElement --------

    [Fact]
    public void ClauseElement_has_expected_shape()
    {
        var t = typeof(ClauseElement);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        AssertIsRecord(t);

        AssertInitProperty(t, "Raw", typeof(string));
        AssertInitProperty(t, "Value", typeof(string));
        AssertInitProperty(t, "Role", typeof(ClauseElementRole));
        AssertInitProperty(t, "SourceStart", typeof(int?), nullable: true);
        AssertInitProperty(t, "SourceLength", typeof(int?), nullable: true);
        AssertInitProperty(t, "PrecedingVerbElementCount", typeof(int));
        AssertInitProperty(t, "Kind", typeof(ArgKind));
        AssertInitProperty(t, "IsFlag", typeof(bool));
        AssertInitProperty(t, "IsPath", typeof(bool));
        AssertInitProperty(t, "Resolved", typeof(string), nullable: true);
        AssertDeclaredPropertyNames(
            t,
            "IsFlag",
            "IsPath",
            "Kind",
            "PrecedingVerbElementCount",
            "Raw",
            "Resolved",
            "Role",
            "SourceLength",
            "SourceStart",
            "Value");

        var instance = new ClauseElement();
        Assert.Equal("", instance.Raw);
        Assert.Equal("", instance.Value);
        Assert.Equal(ClauseElementRole.Verb, instance.Role);
        Assert.Null(instance.SourceStart);
        Assert.Null(instance.SourceLength);
        Assert.Equal(0, instance.PrecedingVerbElementCount);
        Assert.Equal(ArgKind.Literal, instance.Kind);
        Assert.False(instance.IsFlag);
        Assert.False(instance.IsPath);
        Assert.Null(instance.Resolved);
    }

    // -------- VerbChain --------

    [Fact]
    public void VerbChain_has_expected_shape()
    {
        var t = typeof(VerbChain);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        AssertIsRecord(t);

        AssertInitProperty(t, "Tokens", typeof(IReadOnlyList<string>));
        AssertInitProperty(t, "CanonicalVerb", typeof(string), nullable: true);
        AssertInitProperty(t, "IsDynamic", typeof(bool));

        // Joined is a computed (get-only) property, no setter.
        var joined = t.GetProperty("Joined");
        Assert.NotNull(joined);
        Assert.Equal(typeof(string), joined!.PropertyType);
        Assert.True(joined.CanRead);
        Assert.False(joined.CanWrite);
        AssertReferenceNullability(joined, NullabilityState.NotNull);
        AssertDeclaredPropertyNames(t, "CanonicalVerb", "IsDynamic", "Joined", "Tokens");

        var instance = new VerbChain { Tokens = new[] { "git", "push" } };
        Assert.Equal("git push", instance.Joined);

        var empty = new VerbChain();
        Assert.Empty(empty.Tokens);
        Assert.Equal("", empty.Joined);
        Assert.Null(empty.CanonicalVerb);
        Assert.False(empty.IsDynamic);
    }

    // -------- Arg --------

    [Fact]
    public void Arg_has_expected_shape()
    {
        var t = typeof(Arg);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        AssertIsRecord(t);

        AssertInitProperty(t, "Raw", typeof(string));
        AssertInitProperty(t, "Resolved", typeof(string), nullable: true);
        AssertInitProperty(t, "Kind", typeof(ArgKind));
        AssertInitProperty(t, "IsPath", typeof(bool));
        AssertInitProperty(t, "IsCwdAttribution", typeof(bool));

        // IsFlag is a computed property with no setter.
        var isFlag = t.GetProperty("IsFlag");
        Assert.NotNull(isFlag);
        Assert.Equal(typeof(bool), isFlag!.PropertyType);
        Assert.True(isFlag.CanRead);
        Assert.False(isFlag.CanWrite);
        AssertDeclaredPropertyNames(
            t,
            "IsCwdAttribution",
            "IsFlag",
            "IsPath",
            "Kind",
            "Raw",
            "Resolved");

        Assert.True(new Arg { Raw = "-f" }.IsFlag);
        Assert.True(new Arg { Raw = "--force" }.IsFlag);
        Assert.False(new Arg { Raw = "foo" }.IsFlag);
        Assert.False(new Arg { Raw = "" }.IsFlag);
    }

    [Fact]
    public void Arg_IsCwdAttribution_defaults_to_false()
    {
        var instance = new Arg();
        Assert.False(instance.IsCwdAttribution);
        Assert.Equal("", instance.Raw);
        Assert.Null(instance.Resolved);
        Assert.Equal(ArgKind.Literal, instance.Kind);
        Assert.False(instance.IsPath);
    }

    // -------- Redirect --------

    [Fact]
    public void Redirect_has_expected_shape()
    {
        var t = typeof(Redirect);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        AssertIsRecord(t);

        AssertInitProperty(t, "Direction", typeof(RedirectDirection));
        AssertInitProperty(t, "Target", typeof(string));
        AssertInitProperty(t, "IsDynamicSkip", typeof(bool));
        AssertDeclaredPropertyNames(t, "Direction", "IsDynamicSkip", "Target");

        var instance = new Redirect();
        Assert.Equal(RedirectDirection.In, instance.Direction);
        Assert.Equal("", instance.Target);
        Assert.False(instance.IsDynamicSkip);
    }

    // -------- enums --------

    [Fact]
    public void ArgKind_has_expected_members()
    {
        Assert.Equal(
            new[] { "Literal", "EnvVar", "Glob", "Tilde", "DynamicSkip" },
            Enum.GetNames(typeof(ArgKind)));

        // Underlying values follow declaration order (0..4).
        Assert.Equal(0, (int)ArgKind.Literal);
        Assert.Equal(1, (int)ArgKind.EnvVar);
        Assert.Equal(2, (int)ArgKind.Glob);
        Assert.Equal(3, (int)ArgKind.Tilde);
        Assert.Equal(4, (int)ArgKind.DynamicSkip);
    }

    [Fact]
    public void ClauseElementRole_has_expected_members()
    {
        Assert.Equal(
            new[] { "Verb", "Argument", "Redirect" },
            Enum.GetNames(typeof(ClauseElementRole)));

        Assert.Equal(0, (int)ClauseElementRole.Verb);
        Assert.Equal(1, (int)ClauseElementRole.Argument);
        Assert.Equal(2, (int)ClauseElementRole.Redirect);
    }

    [Fact]
    public void RedirectDirection_has_expected_members()
    {
        Assert.Equal(
            new[] { "In", "Out", "Append", "ErrOut", "ErrAppend" },
            Enum.GetNames(typeof(RedirectDirection)));

        Assert.Equal(0, (int)RedirectDirection.In);
        Assert.Equal(1, (int)RedirectDirection.Out);
        Assert.Equal(2, (int)RedirectDirection.Append);
        Assert.Equal(3, (int)RedirectDirection.ErrOut);
        Assert.Equal(4, (int)RedirectDirection.ErrAppend);
    }

    [Fact]
    public void CompoundOperator_has_expected_members()
    {
        Assert.Equal(
            new[] { "None", "AndIf", "OrIf", "Sequence", "Pipe" },
            Enum.GetNames(typeof(CompoundOperator)));

        Assert.Equal(0, (int)CompoundOperator.None);
        Assert.Equal(1, (int)CompoundOperator.AndIf);
        Assert.Equal(2, (int)CompoundOperator.OrIf);
        Assert.Equal(3, (int)CompoundOperator.Sequence);
        Assert.Equal(4, (int)CompoundOperator.Pipe);
    }

    // -------- closure --------

    [Fact]
    public void Public_namespace_contains_only_expected_types()
    {
        var actual = LibAssembly
            .GetExportedTypes()
            .Where(t => t.Namespace == "ShellSyntaxTree" && t.DeclaringType is null)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        var expected = new[]
        {
            nameof(Arg),
            nameof(AnalyzedArgument),
            nameof(ArgKind),
            nameof(BashParser),
            nameof(BashInitialStateMode),
            nameof(BashParserOptions),
            nameof(Clause),
            nameof(ClauseElement),
            nameof(ClauseElementRole),
            nameof(CommandAncestryFrame),
            nameof(CommandAncestryRegion),
            nameof(CommandListItemSyntax),
            nameof(CommandListSyntax),
            nameof(CommandOccurrence),
            nameof(CommandOccurrenceRole),
            nameof(CommandSubstitutionSyntax),
            nameof(CompoundOperator),
            nameof(DescriptorCloseRedirectAnalysis),
            nameof(DescriptorDuplicateRedirectAnalysis),
            nameof(DescriptorMoveRedirectAnalysis),
            nameof(ExecutionRegionCardinality),
            nameof(ExecutionRegionOrigin),
            nameof(ExecutionRegionPhase),
            nameof(ExecutionRegionSyntax),
            nameof(ExecutionRegionTiming),
            nameof(ForEachSyntax),
            nameof(FileRedirectAnalysis),
            nameof(FileRedirectMode),
            nameof(GroupSyntax),
            nameof(HereDocumentAnalysis),
            nameof(HereDocumentExpansionMode),
            nameof(IShellParser),
            nameof(ParsedCommand),
            nameof(PipelineSyntax),
            nameof(PwshParser),
            nameof(PwshDialect),
            nameof(PwshInitialStateMode),
            nameof(PwshParserOptions),
            nameof(Redirect),
            nameof(RedirectAnalysis),
            nameof(RedirectDirection),
            nameof(RedirectSource),
            nameof(ShellBlockSyntax),
            nameof(ShellGroupKind),
            nameof(ShellParserOptions),
            nameof(ShellSourceFragment),
            nameof(ShellSyntaxNode),
            nameof(ShellValueDomain),
            nameof(SimpleCommandSyntax),
            nameof(HereDocumentRedirectAnalysis),
            nameof(HereStringRedirectAnalysis),
            nameof(UnresolvedRedirectAnalysis),
            nameof(VerbChain),
        }.OrderBy(n => n).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Library_assembly_exports_no_other_namespaces()
    {
        var namespaces = LibAssembly
            .GetExportedTypes()
            .Select(t => t.Namespace)
            .Where(n => n is not null)
            .Distinct()
            .ToArray();

        Assert.Equal(new[] { "ShellSyntaxTree" }, namespaces);
    }

    // -------- helpers --------

    private static IEnumerable<PropertyInfo> DeclaredInstanceProps(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    private static string[] DeclaredPublicMethodNames(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

    private static void AssertDeclaredPropertyNames(Type type, params string[] expected)
    {
        Assert.Equal(
            expected.OrderBy(name => name),
            DeclaredInstanceProps(type)
                .Where(property => property.Name != "EqualityContract")
                .Select(property => property.Name)
                .OrderBy(name => name));
    }

    private static void AssertIsRecord(Type t)
    {
        // Records emit a compiler-generated <Clone>$ method and an
        // EqualityContract property. DeclaredOnly avoids the
        // AmbiguousMatchException a record hierarchy would otherwise raise —
        // a derived record re-emits both members with a covariant return.
        var clone = t.GetMethod(
            "<Clone>$",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotNull(clone);

        var equalityContract = t.GetProperty(
            "EqualityContract",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.NotNull(equalityContract);
    }

    private static void AssertInitProperty(Type t, string name, Type expectedType, bool nullable = false)
    {
        var prop = t.GetProperty(name);
        Assert.NotNull(prop);
        Assert.True(prop!.CanRead, $"{t.Name}.{name} must be readable");
        Assert.True(prop.CanWrite, $"{t.Name}.{name} must be writable (init-only)");
        Assert.Equal(expectedType, prop.PropertyType);

        // init-only setter has the IsExternalInit modreq baked into its return type.
        var setter = prop.SetMethod;
        Assert.NotNull(setter);
        var modreqs = setter!.ReturnParameter.GetRequiredCustomModifiers();
        Assert.Contains(modreqs, m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

        if (!prop.PropertyType.IsValueType)
        {
            var expectedNullability = nullable
                ? NullabilityState.Nullable
                : NullabilityState.NotNull;
            AssertReferenceNullability(prop, expectedNullability);
        }
    }

    private static void AssertReferenceNullability(
        PropertyInfo property,
        NullabilityState expected)
    {
        var info = new NullabilityInfoContext().Create(property);
        Assert.Equal(expected, info.ReadState);
        if (property.CanWrite)
        {
            Assert.Equal(expected, info.WriteState);
        }

        AssertGenericArgumentsNotNull(property.Name, info);
    }

    private static void AssertReferenceNullability(
        ParameterInfo parameter,
        NullabilityState expected)
    {
        var info = new NullabilityInfoContext().Create(parameter);
        Assert.Equal(expected, info.ReadState);
        AssertGenericArgumentsNotNull(parameter.Name ?? "return", info);
    }

    private static void AssertGenericArgumentsNotNull(
        string member,
        NullabilityInfo info)
    {
        foreach (var argument in info.GenericTypeArguments)
        {
            Assert.Equal(NullabilityState.NotNull, argument.ReadState);
            AssertGenericArgumentsNotNull(member, argument);
        }
    }
}
