// -----------------------------------------------------------------------
// <copyright file="NativeArgumentFragmentClassifierTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Bash.Parsing;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
using ShellSyntaxTree.Internal.Pwsh.Parsing;
using ShellSyntaxTree.Internal.Resolving;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class NativeArgumentFragmentClassifierTests
{
    [Fact]
    public void Bash_adapter_preserves_complete_literal_run()
    {
        const string source = "--data=@request' file'\".json\" tail";
        var tokens = BashLexer.Tokenize(source);
        var prefixIndex = FindToken(tokens, token => token.Value == "--data=@request");
        var prefix = tokens[prefixIndex];
        var equalsOffset = prefix.Value.IndexOf('=');
        var prefixValue = Assert.IsType<ShellValue>(prefix.ResolverValue)
            .Slice(equalsOffset + 1);

        var classified = NativeArgumentFragmentClassifier.TryClassify(
            source,
            prefix.SourceStart,
            prefix.SourceStart + prefix.SourceLength,
            prefix.SourceStart + equalsOffset + 1,
            "--data=",
            prefixValue,
            tokens,
            prefixIndex + 1,
            new BashNativeArgumentFragmentAdapter(),
            out var result);

        Assert.True(classified);
        Assert.Equal("--data=@request' file'\".json\"", result.Raw);
        Assert.Equal("@request' file'\".json\"", result.ValueRaw);
        Assert.Equal("--data=@request file.json", result.DecodedArgument);
        Assert.Equal("@request file.json", result.DecodedValue);
        Assert.Equal(0, result.SourceStart);
        Assert.Equal(result.Raw.Length, result.SourceLength);
        Assert.Equal(BashTokenKind.Whitespace, tokens[result.NextTokenIndex].Kind);
        Assert.True(result.AllValueFragmentsLiteral);
        Assert.False(result.HasExpansionFragment);
        Assert.False(result.HasOpaqueFragment);
        Assert.False(result.HasOpaqueOrComputedFragment);
    }

    [Fact]
    public void PowerShell_adapter_preserves_complete_literal_run()
    {
        const string source = "--data=@request' file'\".json\" tail";
        var tokens = PwshLexer.Tokenize(source);
        var prefixIndex = FindToken(tokens, token => token.Value == "--data=@request");
        var prefix = tokens[prefixIndex];
        var equalsOffset = prefix.Value.IndexOf('=');
        var prefixValue = Assert.IsType<ShellValue>(prefix.ResolverValue)
            .Slice(equalsOffset + 1);

        var classified = NativeArgumentFragmentClassifier.TryClassify(
            source,
            prefix.SourceStart,
            prefix.SourceStart + prefix.SourceLength,
            prefix.SourceStart + equalsOffset + 1,
            "--data=",
            prefixValue,
            tokens,
            prefixIndex + 1,
            new PwshNativeArgumentFragmentAdapter(),
            out var result);

        Assert.True(classified);
        Assert.Equal("--data=@request' file'\".json\"", result.Raw);
        Assert.Equal("@request' file'\".json\"", result.ValueRaw);
        Assert.Equal("--data=@request file.json", result.DecodedArgument);
        Assert.Equal("@request file.json", result.DecodedValue);
        Assert.Equal(0, result.SourceStart);
        Assert.Equal(result.Raw.Length, result.SourceLength);
        Assert.Equal(PwshTokenKind.Whitespace, tokens[result.NextTokenIndex].Kind);
        Assert.True(result.AllValueFragmentsLiteral);
        Assert.False(result.HasExpansionFragment);
        Assert.False(result.HasOpaqueFragment);
        Assert.False(result.HasOpaqueOrComputedFragment);
    }

    [Fact]
    public void Bash_adapter_preserves_typed_expansion_and_opaque_cause()
    {
        const string expansionSource = "--output=$HOME\".json\" tail";
        var expansion = ClassifyBash(expansionSource, "--output=$HOME", "--output=");

        var home = Assert.Single(
            expansion.ResolverValue.Fragments,
            fragment => fragment.Kind == ShellValueFragmentKind.Expansion);
        Assert.Equal(
            new ShellExpansionReference(ShellExpansionKind.Variable, "HOME"),
            home.Expansion);
        Assert.False(expansion.AllValueFragmentsLiteral);
        Assert.True(expansion.HasExpansionFragment);
        Assert.False(expansion.HasOpaqueFragment);
        Assert.True(expansion.HasOpaqueOrComputedFragment);

        const string opaqueSource = "--data=@$(generate) tail";
        var opaque = ClassifyBash(opaqueSource, "--data=@", "--data=");

        Assert.False(opaque.HasExpansionFragment);
        Assert.True(opaque.HasOpaqueFragment);
        Assert.True(opaque.HasOpaqueOrComputedFragment);
        Assert.Contains(
            opaque.ResolverValue.Fragments,
            fragment => fragment.OpaqueCause == ShellOpaqueCause.CommandSubstitution);
    }

    [Fact]
    public void PowerShell_adapter_preserves_typed_expansion_and_opaque_cause()
    {
        const string expansionSource = "--output=$HOME\".json\" tail";
        var expansion = ClassifyPowerShell(
            expansionSource,
            "--output=$HOME",
            "--output=");

        var home = Assert.Single(
            expansion.ResolverValue.Fragments,
            fragment => fragment.Kind == ShellValueFragmentKind.Expansion);
        Assert.Equal(
            new ShellExpansionReference(ShellExpansionKind.Variable, "HOME"),
            home.Expansion);
        Assert.False(expansion.AllValueFragmentsLiteral);
        Assert.True(expansion.HasExpansionFragment);
        Assert.False(expansion.HasOpaqueFragment);
        Assert.True(expansion.HasOpaqueOrComputedFragment);

        const string opaqueSource = "--data=@$(Get-Item x) tail";
        var opaque = ClassifyPowerShell(opaqueSource, "--data=@", "--data=");

        Assert.False(opaque.HasExpansionFragment);
        Assert.True(opaque.HasOpaqueFragment);
        Assert.True(opaque.HasOpaqueOrComputedFragment);
        Assert.Contains(
            opaque.ResolverValue.Fragments,
            fragment => fragment.OpaqueCause == ShellOpaqueCause.PowerShellSubexpression);
    }

    [Fact]
    public void Adapters_fail_closed_when_lexer_provenance_is_missing()
    {
        var bashToken = new BashToken(
            BashTokenKind.Word,
            "literal",
            null,
            0,
            7,
            null);
        Assert.True(new BashNativeArgumentFragmentAdapter().TryAdapt(
            bashToken,
            out var bashFragment));
        var bashValue = Assert.Single(bashFragment.Value.Fragments);
        Assert.Equal(ShellValueFragmentKind.Opaque, bashValue.Kind);
        Assert.Equal(ShellOpaqueCause.Unsupported, bashValue.OpaqueCause);

        var powerShellToken = new PwshToken(
            PwshTokenKind.Word,
            "literal",
            null,
            0,
            7,
            null);
        Assert.True(new PwshNativeArgumentFragmentAdapter().TryAdapt(
            powerShellToken,
            out var powerShellFragment));
        var powerShellValue = Assert.Single(powerShellFragment.Value.Fragments);
        Assert.Equal(ShellValueFragmentKind.Opaque, powerShellValue.Kind);
        Assert.Equal(ShellOpaqueCause.Unsupported, powerShellValue.OpaqueCause);
    }

    private static NativeArgumentFragmentClassification ClassifyBash(
        string source,
        string prefixText,
        string decodedArgumentPrefix)
    {
        var tokens = BashLexer.Tokenize(source);
        var prefixIndex = FindToken(tokens, token => token.Value == prefixText);
        var prefix = tokens[prefixIndex];
        var equalsOffset = prefix.Value.IndexOf('=');
        var prefixValue = Assert.IsType<ShellValue>(prefix.ResolverValue)
            .Slice(equalsOffset + 1);
        Assert.True(NativeArgumentFragmentClassifier.TryClassify(
            source,
            prefix.SourceStart,
            prefix.SourceStart + prefix.SourceLength,
            prefix.SourceStart + equalsOffset + 1,
            decodedArgumentPrefix,
            prefixValue,
            tokens,
            prefixIndex + 1,
            new BashNativeArgumentFragmentAdapter(),
            out var result));
        return result;
    }

    private static NativeArgumentFragmentClassification ClassifyPowerShell(
        string source,
        string prefixText,
        string decodedArgumentPrefix)
    {
        var tokens = PwshLexer.Tokenize(source);
        var prefixIndex = FindToken(tokens, token => token.Value == prefixText);
        var prefix = tokens[prefixIndex];
        var equalsOffset = prefix.Value.IndexOf('=');
        var prefixValue = Assert.IsType<ShellValue>(prefix.ResolverValue)
            .Slice(equalsOffset + 1);
        Assert.True(NativeArgumentFragmentClassifier.TryClassify(
            source,
            prefix.SourceStart,
            prefix.SourceStart + prefix.SourceLength,
            prefix.SourceStart + equalsOffset + 1,
            decodedArgumentPrefix,
            prefixValue,
            tokens,
            prefixIndex + 1,
            new PwshNativeArgumentFragmentAdapter(),
            out var result));
        return result;
    }

    private static int FindToken<TToken>(
        IReadOnlyList<TToken> tokens,
        Func<TToken, bool> predicate)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (predicate(tokens[index]))
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException("Expected token was not emitted.");
    }
}
