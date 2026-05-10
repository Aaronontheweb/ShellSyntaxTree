// -----------------------------------------------------------------------
// <copyright file="CdAttributionContext.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Parser-internal mutable state that tracks the "attributed cwd" for the
/// current compound while building clauses. The output AST stays immutable;
/// this object just drives:
/// <list type="bullet">
///   <item>which subsequent clauses receive a synthetic
///         <see cref="Arg.IsCwdAttribution"/> arg (SPEC §9);</item>
///   <item>which <see cref="BashParserOptions.WorkingDirectory"/> the
///         resolver runs against for clauses that follow a literal
///         <c>cd</c>/<c>chdir</c>;</item>
///   <item>subshell isolation — push/pop via <see cref="PushForSubshell"/>
///         and <see cref="PopForSubshell"/> so a <c>cd</c> inside a
///         subshell doesn't leak out (SPEC §9 rule 4 / §10).</item>
/// </list>
/// Only <c>cd</c> and <c>chdir</c> propagate attribution per locked
/// interpretation #5; <c>pushd</c>/<c>popd</c>/<c>push-location</c>/
/// <c>set-location</c> are parsed as CwdVerbs but explicitly skipped here.
/// </summary>
internal sealed class CdAttributionContext
{
    /// <summary>
    /// Snapshot saved during <see cref="PushForSubshell"/>; restored on
    /// <see cref="PopForSubshell"/>. Inner state is mutated freely while
    /// the subshell parses; on exit, the outer state is reinstated.
    /// </summary>
    private readonly Stack<Frame> _frames = new();

    /// <summary>
    /// The resolved working directory inherited from the most recent
    /// literal <c>cd</c>/<c>chdir</c> in the current compound. Null when
    /// no attribution is active or when the cd target was dynamic.
    /// </summary>
    public string? ResolvedCwd { get; private set; }

    /// <summary>
    /// True when the most recent <c>cd</c>/<c>chdir</c> target resolved to
    /// <see cref="ArgKind.DynamicSkip"/> (locked interpretation #6). The
    /// synthetic attribution arg appended to subsequent clauses uses
    /// <c>Kind=DynamicSkip, IsPath=false, Resolved=null</c>; relative path
    /// args in those clauses are not re-resolved.
    /// </summary>
    public bool IsDynamic { get; private set; }

    /// <summary>
    /// True when a previous <c>cd</c>/<c>chdir</c> set attribution (either
    /// literal or dynamic). Drives whether subsequent clauses receive the
    /// synthetic IsCwdAttribution arg.
    /// </summary>
    public bool HasAttribution => ResolvedCwd is not null || IsDynamic;

    /// <summary>
    /// Record a literal cd attribution. <paramref name="resolvedTarget"/>
    /// must be the resolver's normalized absolute path for the cd's first
    /// non-flag positional. Replaces any previous attribution per SPEC §9
    /// rule 3.
    /// </summary>
    public void SetLiteralAttribution(string resolvedTarget)
    {
        ResolvedCwd = resolvedTarget;
        IsDynamic = false;
    }

    /// <summary>
    /// Record that the most recent cd target was dynamic (locked
    /// interpretation #6). Subsequent clauses get a synthetic DynamicSkip
    /// attribution arg; their relative paths are not re-resolved.
    /// </summary>
    public void SetDynamicAttribution()
    {
        ResolvedCwd = null;
        IsDynamic = true;
    }

    /// <summary>
    /// Push the current attribution state onto an internal stack so the
    /// inner subshell can mutate freely. The pushed state mirrors current
    /// values — a subshell <em>inherits</em> outer attribution (so e.g.
    /// <c>cd /a &amp;&amp; (cmd1)</c> still attributes cmd1 to /a) but
    /// changes inside the subshell stay isolated.
    /// </summary>
    public void PushForSubshell()
    {
        _frames.Push(new Frame(ResolvedCwd, IsDynamic));
    }

    /// <summary>
    /// Restore the most recently pushed attribution state. Called when the
    /// parser leaves a subshell. Any cd attribution that the subshell
    /// applied is discarded — only the outer state survives (SPEC §9
    /// rule 4 / §10 "subshell isolation").
    /// </summary>
    public void PopForSubshell()
    {
        if (_frames.Count == 0)
        {
            // Defensive — caller bug. Reset to a no-attribution state
            // rather than throw; the parser is in safe-fail territory if
            // depth tracking diverges, but the output AST stays well-formed.
            ResolvedCwd = null;
            IsDynamic = false;
            return;
        }

        var frame = _frames.Pop();
        ResolvedCwd = frame.ResolvedCwd;
        IsDynamic = frame.IsDynamic;
    }

    private readonly struct Frame
    {
        public Frame(string? resolvedCwd, bool isDynamic)
        {
            ResolvedCwd = resolvedCwd;
            IsDynamic = isDynamic;
        }

        public string? ResolvedCwd { get; }

        public bool IsDynamic { get; }
    }
}
