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
/// Locks the public API surface for v0.1.0-alpha. Any drift here means
/// either SPEC.md §2/§3 was bumped (deliberate version change) or a
/// regression — both are blockers until reconciled.
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

    // -------- BashParserOptions --------

    [Fact]
    public void BashParserOptions_has_expected_shape()
    {
        var t = typeof(BashParserOptions);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
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

    // -------- ParsedCommand --------

    [Fact]
    public void ParsedCommand_has_expected_shape()
    {
        var t = typeof(ParsedCommand);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
        AssertIsRecord(t);

        AssertInitProperty(t, "Source", typeof(string));
        AssertInitProperty(t, "Clauses", typeof(IReadOnlyList<Clause>));
        AssertInitProperty(t, "IsUnparseable", typeof(bool));
        AssertInitProperty(t, "UnparseableReason", typeof(string), nullable: true);

        var instance = new ParsedCommand();
        Assert.Equal("", instance.Source);
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
        AssertInitProperty(t, "IsSubshell", typeof(bool));
        AssertInitProperty(t, "IsBashCWrapped", typeof(bool));

        var instance = new Clause();
        Assert.Equal(CompoundOperator.None, instance.Operator);
        Assert.NotNull(instance.Verb);
        Assert.Empty(instance.Verb.Tokens);
        Assert.Empty(instance.Args);
        Assert.Empty(instance.Redirects);
        Assert.False(instance.IsSubshell);
        Assert.False(instance.IsBashCWrapped);
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

        // Joined is a computed (get-only) property, no setter.
        var joined = t.GetProperty("Joined");
        Assert.NotNull(joined);
        Assert.Equal(typeof(string), joined!.PropertyType);
        Assert.True(joined.CanRead);
        Assert.False(joined.CanWrite);

        var instance = new VerbChain { Tokens = new[] { "git", "push" } };
        Assert.Equal("git push", instance.Joined);

        var empty = new VerbChain();
        Assert.Empty(empty.Tokens);
        Assert.Equal("", empty.Joined);
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
            .Where(t => t.Namespace == "ShellSyntaxTree")
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        var expected = new[]
        {
            nameof(Arg),
            nameof(ArgKind),
            nameof(BashParser),
            nameof(BashParserOptions),
            nameof(Clause),
            nameof(CompoundOperator),
            nameof(IShellParser),
            nameof(ParsedCommand),
            nameof(Redirect),
            nameof(RedirectDirection),
            nameof(VerbChain),
        };

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

    private static void AssertIsRecord(Type t)
    {
        // Records emit a compiler-generated <Clone>$ method and an EqualityContract property.
        var clone = t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(clone);

        var equalityContract = t.GetProperty(
            "EqualityContract",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
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

        // Suppress unused-parameter warning; nullability metadata isn't queried at runtime
        // without NullabilityInfoContext (net6+) and our purpose here is shape, not nullability.
        _ = nullable;
    }
}
