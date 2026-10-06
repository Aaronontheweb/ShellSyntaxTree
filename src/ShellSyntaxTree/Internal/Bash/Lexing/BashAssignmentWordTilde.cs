// -----------------------------------------------------------------------
// <copyright file="BashAssignmentWordTilde.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;

namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>What Bash does with one unquoted <c>~</c> inside a word.</summary>
internal enum BashAssignmentTildeKind
{
    /// <summary>Bash keeps the <c>~</c> as text.</summary>
    Literal,

    /// <summary>Bash replaces the <c>~</c> with HOME.</summary>
    Home,

    /// <summary>
    /// Bash can expand it (<c>~user</c>, <c>~+</c>), or the parser cannot see
    /// whether the word is assignment-shaped. The value is not proved.
    /// </summary>
    Unknown,
}

/// <summary>
/// Finds the <c>~</c> that Bash expands inside one unquoted word token (#243).
/// Outside POSIX mode, Bash treats a word whose text before the first
/// unquoted <c>=</c> (or <c>+=</c>) is a variable name like an assignment.
/// It expands a <c>~</c> directly after that <c>=</c> and after each later
/// unquoted <c>:</c>: <c>make PREFIX=~/x</c> passes <c>PREFIX=$HOME/x</c>. This
/// applies to arguments, <c>for</c> and <c>case</c> words, and redirect
/// targets; a here-string keeps the text. The tilde prefix ends at <c>/</c>,
/// <c>:</c>, or the end of the word. A quoted or escaped prefix stays text.
/// </summary>
/// <remarks>
/// Owner: the lexer, call-local. The parser does not see the words before
/// this token, so after a quoted part (<c>a="b":~/x</c>) a <c>~</c> after
/// <c>=</c> or <c>:</c> is <see cref="BashAssignmentTildeKind.Unknown"/>.
/// </remarks>
internal struct BashAssignmentWordTilde
{
    /// <summary>
    /// The expansion name that marks a <c>~</c> after <c>=</c> or <c>:</c>.
    /// A word-start <c>~</c> has no name.
    /// </summary>
    internal const string ExpansionName = "=";

    private readonly bool _isWordStart;
    private State _state;
    private int _nameLength;
    private bool _plus;
    private bool _tildeMayFollow;

    internal BashAssignmentWordTilde(bool isWordStart)
    {
        _isWordStart = isWordStart;
        _state = isWordStart ? State.Name : State.Other;
        _nameLength = 0;
        _plus = false;
        _tildeMayFollow = false;
    }

    private enum State
    {
        Name,
        Value,
        Other,
    }

    /// <summary>Classifies a <c>~</c> that is not the first character of the token.</summary>
    internal readonly BashAssignmentTildeKind Classify(ReadOnlySpan<char> source, int tilde)
    {
        if (!_tildeMayFollow)
        {
            return BashAssignmentTildeKind.Literal;
        }

        if (!_isWordStart)
        {
            return BashAssignmentTildeKind.Unknown;
        }

        var next = BashLineContinuation.Skip(source, tilde + 1, BashContinuationContext.Unquoted);
        if (next >= source.Length)
        {
            return BashAssignmentTildeKind.Home;
        }

        return source[next] switch
        {
            '/' or ':' or ' ' or '\t' or '\n' or
                ';' or '|' or '&' or '<' or '>' or '(' or ')' => BashAssignmentTildeKind.Home,
            '\'' or '"' or '\\' => BashAssignmentTildeKind.Literal,
            _ => BashAssignmentTildeKind.Unknown,
        };
    }

    /// <summary>Records an unquoted, unescaped character of the word.</summary>
    internal void OnPlain(char character)
    {
        _tildeMayFollow = false;
        switch (_state)
        {
            case State.Name:
                if (character == '=' && _nameLength > 0)
                {
                    _state = State.Value;
                    _tildeMayFollow = true;
                }
                else if (character == '+' && _nameLength > 0 && !_plus)
                {
                    _plus = true;
                }
                else if (!_plus && IsNameCharacter(character, _nameLength == 0))
                {
                    _nameLength++;
                }
                else
                {
                    _state = State.Other;
                }

                return;
            case State.Value:
                _tildeMayFollow = character == ':';
                return;
            default:
                // Not at the word start: the word may be assignment-shaped.
                _tildeMayFollow = !_isWordStart && character is '=' or ':';
                return;
        }
    }

    /// <summary>Records an escaped character or an expansion.</summary>
    internal void OnOther()
    {
        _tildeMayFollow = false;
        if (_state == State.Name)
        {
            _state = State.Other;
        }
    }

    private static bool IsNameCharacter(char character, bool first) =>
        character == '_' || character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ||
        !first && character is >= '0' and <= '9';
}
