// -----------------------------------------------------------------------
// <copyright file="BashNativeArgumentFragmentAdapter.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

internal readonly struct BashNativeArgumentFragmentAdapter
    : INativeArgumentFragmentAdapter<BashToken>
{
    public bool CanStart(BashToken token) => token.Kind is
        BashTokenKind.Word
        or BashTokenKind.QuotedString
        or BashTokenKind.OpaqueSubstitution;

    public bool TryAdapt(BashToken token, out NativeArgumentFragment fragment)
    {
        if (token.Kind is not (BashTokenKind.Word
            or BashTokenKind.QuotedString
            or BashTokenKind.OpaqueSubstitution))
        {
            fragment = default;
            return false;
        }

        var value = token.ResolverValue
            ?? ShellValue.Opaque(
                token.Value,
                token.Kind == BashTokenKind.OpaqueSubstitution
                    ? ShellOpaqueCause.CommandSubstitution
                    : ShellOpaqueCause.Unsupported,
                token.SourceStart,
                token.SourceLength);
        fragment = new NativeArgumentFragment(
            value,
            token.SourceStart,
            token.SourceLength);
        return true;
    }
}
