// -----------------------------------------------------------------------
// <copyright file="PwshNativeArgumentFragmentAdapter.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

internal readonly struct PwshNativeArgumentFragmentAdapter
    : INativeArgumentFragmentAdapter<PwshToken>
{
    public bool CanStart(PwshToken token) => token.Kind is
        PwshTokenKind.Word
        or PwshTokenKind.QuotedString
        or PwshTokenKind.ScriptBlock
        or PwshTokenKind.Subexpression
        or PwshTokenKind.Splat;

    public bool TryAdapt(PwshToken token, out NativeArgumentFragment fragment)
    {
        if (token.Kind is not (PwshTokenKind.Word
            or PwshTokenKind.QuotedString
            or PwshTokenKind.ScriptBlock
            or PwshTokenKind.Subexpression
            or PwshTokenKind.Splat))
        {
            fragment = default;
            return false;
        }

        var value = token.ResolverValue
            ?? ShellValue.Opaque(
                token.Value,
                ShellOpaqueCause.Unsupported,
                token.SourceStart,
                token.SourceLength);
        fragment = new NativeArgumentFragment(
            value,
            token.SourceStart,
            token.SourceLength);
        return true;
    }
}
