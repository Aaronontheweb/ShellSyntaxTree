# ShellSyntaxTree — bash & shared-contract Specification

**Status:** Shipped. The bash parser is implemented (v0.1.x); v0.2.0 adds the
PowerShell parser and the shared multi-shell surface.
**Audience:** Whoever (human or agent) works on ShellSyntaxTree.
**Read this end-to-end before writing any code.**
**PowerShell support is specified separately in `SPEC.POWERSHELL.md` (v0.2.0);
this document is the canonical home of the public API, AST, sanitization
workflow, and consumer contract that PowerShell reuses.**

This document specifies the public API, AST, grammar, verb tables, resolver
semantics, and corpus contract for ShellSyntaxTree v0.1. The library is a
focused bash command parser designed for **security gate evaluators** —
tools that inspect agent-emitted shell commands to decide whether to allow,
prompt for, or deny execution.

It is **not** a general-purpose shell interpreter. It does not execute,
expand, or evaluate commands. It returns a structured AST that consumers
walk to make decisions.

The original consumer is [Netclaw](https://github.com/Aaronontheweb/netclaw)'s
approval policy. The library is designed to be reusable beyond Netclaw —
any tool that needs to reason about the shape of an agent-emitted bash
command can consume it.

---

## 1. Goals & Non-Goals

### Goals (v0.1)

1. **Parse bash commands into a structured AST** with per-clause verbs,
   args, redirects, and compound operators.
2. **Extract paths a command operates on** with per-verb knowledge of which
   positional args are paths vs flags vs literal values (`chmod 755 file`
   knows `755` is a mode).
3. **Honor `cd <dir> && cmd` propagation** within a compound — `<dir>`
   counts as a path each subsequent command operates on.
4. **Recurse into `bash -c "<inner>"`** so the inner command is parsed and
   its clauses surface to the consumer.
5. **Mark dynamic-content tokens** (unresolved `$VAR`, unexpanded globs)
   so consumers don't misextract literal `$VAR/foo` as a path.
6. **Multi-shell-ready** via `IShellParser` interface — bash is the only
   v0.1 implementation; PowerShell and cmd are deferred to later versions
   without breaking the seam.

### Non-Goals (v0.1)

- PowerShell parsing (deferred; interface seam is present).
- Windows cmd parsing (deferred).
- Command execution. The library never runs anything.
- Variable expansion. We mark dynamic tokens, never resolve them.
- Function definitions, here-docs body extraction, complex parameter
  expansion (`${var//pattern/replacement}`), arithmetic expansion.
- Command-substitution evaluation. `$(cmd)` and backtick `` `cmd` `` are
  recognized at the lex level and collapsed into a single
  `Kind=DynamicSkip, IsPath=false` arg per locked interpretation #2 (see
  `openspec/changes/archive/.../v0.1-locked-interpretations`). The
  surrounding clause stays parseable so hard-deny rules still fire on
  visible parts.
- Performance tuning beyond "fast enough to invoke per shell call without
  noticeable latency" (~1ms per typical input).

---

## 2. Public API Surface

The package exposes a small surface from a single namespace
`ShellSyntaxTree`. **Public types only**:

```csharp
namespace ShellSyntaxTree;

/// <summary>
/// Parses shell command strings into structured ASTs.
/// </summary>
public interface IShellParser
{
    /// <summary>
    /// Parse the command. Always returns a ParsedCommand; sets
    /// <see cref="ParsedCommand.IsUnparseable"/> when the input cannot
    /// be tokenized (unbalanced quotes, etc.). Never throws on
    /// well-formed strings; throws ArgumentNullException on null input.
    /// </summary>
    ParsedCommand Parse(string command);
}

/// <summary>Bash implementation of IShellParser.</summary>
public sealed class BashParser : IShellParser
{
    public BashParser();
    public BashParser(BashParserOptions options);
    public ParsedCommand Parse(string command);
}

/// <summary>PowerShell implementation of IShellParser (v0.2.0). The
/// PowerShell grammar, tables, and resolver are specified in
/// SPEC.POWERSHELL.md.</summary>
public sealed class PwshParser : IShellParser
{
    public PwshParser();
    public PwshParser(PwshParserOptions options);
    public ParsedCommand Parse(string command);
}

/// <summary>Shell-neutral resolver configuration shared by every parser
/// (added v0.2.0). HomeDirectory / WorkingDirectory live here.</summary>
public abstract record ShellParserOptions { ... }

/// <summary>Configuration knobs for BashParser. As of v0.2.0 a sealed
/// record deriving from ShellParserOptions; the v0.1 object-initializer
/// shape is unchanged.</summary>
public sealed record BashParserOptions : ShellParserOptions;

/// <summary>Configuration knobs for PwshParser (v0.2.0). Empty — the
/// resolver knobs live on ShellParserOptions.</summary>
public sealed record PwshParserOptions : ShellParserOptions;

// The pre-v0.2.0 BashParserOptions body, now hoisted onto ShellParserOptions:
public abstract record ShellParserOptions
{
    /// <summary>
    /// User home directory used to expand `~` and `$HOME` tokens during
    /// resolution. Defaults to <see cref="Environment.SpecialFolder.UserProfile"/>.
    /// </summary>
    public string? HomeDirectory { get; init; }

    /// <summary>
    /// Working directory used to resolve relative path tokens during
    /// resolution. Defaults to the daemon-process cwd.
    /// </summary>
    public string? WorkingDirectory { get; init; }
}

// AST records — see §3.
public sealed record ParsedCommand { ... }
public sealed record Clause { ... }
public sealed record ClauseElement { ... }
public sealed record VerbChain { ... }
public sealed record Arg { ... }
public sealed record Redirect { ... }
public enum ClauseElementRole { Verb, Argument, Redirect }
public enum ArgKind { Literal, EnvVar, Glob, Tilde, DynamicSkip }
public enum RedirectDirection { In, Out, Append, ErrOut, ErrAppend }
public enum CompoundOperator { None, AndIf, OrIf, Sequence, Pipe }
```

That's the entire public API. **Everything else is internal.** The lexer,
parser internals, verb tables, resolver — all implementation detail.

---

## 3. AST Reference

### `ParsedCommand`

The top-level result of parsing. Always returned (never null).

```csharp
public sealed record ParsedCommand
{
    /// <summary>The original input string, verbatim.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Top-level clauses, split on compound operators (&&, ||, ;, |).
    /// For a simple command, exactly one clause with Operator=None.
    /// </summary>
    public IReadOnlyList<Clause> Clauses { get; init; } = [];

    /// <summary>
    /// True when the parser could not produce a clean AST (unbalanced
    /// quotes, unparseable construct). When true, Clauses MAY be partial
    /// or empty. Consumers should route to safe-fail.
    /// </summary>
    public bool IsUnparseable { get; init; }

    /// <summary>
    /// Human-readable diagnostic when IsUnparseable=true; null otherwise.
    /// </summary>
    public string? UnparseableReason { get; init; }
}
```

### `Clause`

One logical command within a compound. Each clause has its own verb chain,
args, redirects, and the operator that joined it to the previous clause.

```csharp
public sealed record Clause
{
    /// <summary>
    /// The operator joining this clause to the previous one. The first
    /// clause in a ParsedCommand has Operator=None. Subsequent clauses
    /// carry the operator that preceded them in the source
    /// (e.g. `a && b` produces clauses [{None,a}, {AndIf,b}]).
    /// </summary>
    public CompoundOperator Operator { get; init; }

    /// <summary>The verb chain (see §3.3 and §6).</summary>
    public VerbChain Verb { get; init; } = new();

    /// <summary>
    /// All argument tokens after the verb chain, in source order. Includes
    /// flags and positional args. See <see cref="Arg.Kind"/> for token kind.
    /// </summary>
    public IReadOnlyList<Arg> Args { get; init; } = [];

    /// <summary>
    /// Redirect operators on this clause (>, >>, <, 2>, 2>>). Each entry
    /// includes direction and target path.
    /// </summary>
    public IReadOnlyList<Redirect> Redirects { get; init; } = [];

    /// <summary>
    /// Significant source-authored verbs, arguments, and redirects in source
    /// order. This is the provenance view; Verb, Args, and Redirects remain
    /// compatibility projections. Synthetic cwd attribution is excluded.
    /// </summary>
    public IReadOnlyList<ClauseElement> Elements { get; init; } = [];

    /// <summary>
    /// True when this clause is wrapped in a subshell (parens). Subshells
    /// isolate cd state — see §9.
    /// </summary>
    public bool IsSubshell { get; init; }

    /// <summary>
    /// True when this clause is the result of recursing into a
    /// command-string wrapper — `bash -c "..."` / `sh -c "..."`, or (v0.2.0)
    /// PowerShell `pwsh -Command "..."` / `pwsh -EncodedCommand ...` /
    /// static `Invoke-Expression '...'`. Useful
    /// for consumers that want to surface "this came from a wrapped
    /// invocation" in UI.
    /// </summary>
    /// <remarks>Renamed from `IsCommandStringWrapped` in v0.2.0 — see RELEASE_NOTES.md
    /// and SPEC.POWERSHELL.md §3 for the old→new mapping.</remarks>
    public bool IsCommandStringWrapped { get; init; }
}
```

### `ClauseElement`

One significant source-authored element of a clause. `Elements` preserves the
cross-projection order that `Verb`, `Args`, and `Redirects` cannot represent on
their own.

```csharp
public sealed record ClauseElement
{
    /// <summary>Exact authored source slice, including quote delimiters.</summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// Lexer-decoded logical value. For a redirect this is the decoded target;
    /// for an inline binding it remains the complete decoded source token.
    /// </summary>
    public string Value { get; init; } = "";

    public ClauseElementRole Role { get; init; }

    /// <summary>
    /// Span in ParsedCommand.Source. Null for elements surfaced through a
    /// decoded command-string wrapper when no exact outer mapping exists.
    /// </summary>
    public int? SourceStart { get; init; }
    public int? SourceLength { get; init; }

    /// <summary>
    /// Number of parser-classified verb elements authored before this element
    /// in the clause. For a verb element, this is its zero-based Verb.Tokens
    /// index. This is an AST coordinate, not an executable-specific semantic
    /// boundary.
    /// </summary>
    public int PrecedingVerbElementCount { get; init; }

    /// <summary>
    /// Argument classification for this token, inline bound value, or redirect
    /// target. Verb elements use Literal, except dynamic command names use
    /// DynamicSkip.
    /// </summary>
    public ArgKind Kind { get; init; }
    public bool IsFlag { get; init; }
    public bool IsPath { get; init; }
    public string? Resolved { get; init; }
}

public enum ClauseElementRole
{
    Verb,
    Argument,
    Redirect
}
```

The collection contains significant leaves only: whitespace, comments,
compound operators, grouping delimiters, and shell call operators are excluded.
Each verb token appears exactly once with `Role=Verb`. Each authored argument
token appears once with `Role=Argument`; inline forms such as
`--work-tree=../repo` stay one element even when `Args` exposes separate flag
and value projections. Each redirect appears once with `Role=Redirect`; its
ordinal among redirect elements matches its ordinal in `Redirects`, and `Raw`
spans the operator through its target.

`PrecedingVerbElementCount` is clause-local and resets to zero at every clause.
For `git -C /repo commit`, `-C` and `/repo` carry `1`; for
`git commit -C HEAD~1`, `-C` and `HEAD~1` carry `2`. ShellSyntaxTree reports
that parser-relative coordinate but does not assign Git-specific meaning to
it. `Role=Verb` mirrors the greedy `Clause.Verb` heuristic. Therefore an
unrecognized option can stop verb extraction and cause a later semantic
subcommand to appear with `Role=Argument`; consumers SHALL use the complete
authored element order rather than treating this count as an executable's
semantic command boundary.

Synthetic cwd-attribution args are deliberately absent from `Elements`: they
remain available through `Args` with `IsCwdAttribution=true`. Clauses expanded
from command-string wrappers preserve each element's inner `Raw` and `Value`,
but set `SourceStart` and `SourceLength` to null rather than guessing how a
decoded or escaped inner character maps into the outer `ParsedCommand.Source`.

Because `Clause` is a record, `Elements` participates in its generated value
equality and hashing. Generated `ToString()` and default JSON serialization
also include the projection. The API addition is source- and binary-additive,
but these generated behaviors are observably different.

### `VerbChain`

The verb of a clause. Multi-token to handle commands like `git push`,
`docker compose up`, `dotnet ef migrations add`. Length determined by the
greedy verb-chain heuristic in §6.1 — consecutive verb-like Word tokens
from the start of the clause, transparently consuming flag-with-value
pairs, with a 1-token carveout for FILE verbs.

```csharp
public sealed record VerbChain
{
    /// <summary>
    /// Verb tokens in source order. Empty when the clause has no verb
    /// (e.g. clause is just a redirect or an empty fragment).
    /// </summary>
    public IReadOnlyList<string> Tokens { get; init; } = [];

    /// <summary>
    /// The canonical, alias-resolved verb identity (added v0.2.0). Non-null
    /// only when the parser rewrote a built-in alias — `ls` → `Get-ChildItem`.
    /// Null for every bash clause. See SPEC.POWERSHELL.md §3.
    /// </summary>
    public string? CanonicalVerb { get; init; }

    /// <summary>
    /// True when the clause's command name is a dynamic token the parser
    /// cannot statically identify — `& $exe`, `& "tool-$name"`,
    /// `& { ... }` (added v0.2.0).
    /// Always false for bash clauses. See SPEC.POWERSHELL.md §3.
    /// </summary>
    public bool IsDynamic { get; init; }

    /// <summary>Convenience: tokens joined with spaces.</summary>
    public string Joined => string.Join(" ", Tokens);
}
```

> **Note:** The single-space form `string.Join(" ", …)` is used (not the
> `char` overload `string.Join(' ', …)`) so the implementation compiles
> on both `netstandard2.0` and `net8.0`. The `char` overload is net5+
> only.

### `Arg`

One argument token after the verb chain. Includes resolution state.

```csharp
public sealed record Arg
{
    /// <summary>Verbatim token from the source.</summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// Resolved value for path tokens — tilde expanded, env vars
    /// substituted, normalized to absolute path against
    /// BashParserOptions.WorkingDirectory. Null when Kind is not a path
    /// (Literal non-path / Glob / DynamicSkip).
    /// </summary>
    public string? Resolved { get; init; }

    /// <summary>Token kind. See <see cref="ArgKind"/>.</summary>
    public ArgKind Kind { get; init; }

    /// <summary>
    /// True when this token starts with '-' or '--' (a flag, not a
    /// positional arg).
    /// </summary>
    public bool IsFlag => Raw.StartsWith('-');

    /// <summary>
    /// True when this token is a path the clause operates on (per the
    /// per-verb pathArgs table; see §7). Set during parsing so consumers
    /// don't reapply per-verb rules.
    /// </summary>
    public bool IsPath { get; init; }

    /// <summary>
    /// True when this Arg is a synthetic attribution arg representing
    /// the working directory inherited from a preceding `cd`/`chdir`
    /// clause in the same compound. Default false. See §9 for
    /// propagation semantics.
    /// </summary>
    public bool IsCwdAttribution { get; init; }
}

public enum ArgKind
{
    /// <summary>Literal value (string, number, flag).</summary>
    Literal,
    /// <summary>Token containing an unresolved env var reference.</summary>
    EnvVar,
    /// <summary>Token containing glob metachars (* ? [).</summary>
    Glob,
    /// <summary>Token starting with ~ (tilde).</summary>
    Tilde,
    /// <summary>
    /// Token whose value cannot be safely resolved (unresolved env var,
    /// unexpandable glob). Consumers SHALL treat as "no value extracted"
    /// rather than using Raw as a literal path.
    /// </summary>
    DynamicSkip
}
```

### `Redirect`

```csharp
public sealed record Redirect
{
    public RedirectDirection Direction { get; init; }
    /// <summary>
    /// Redirect target. Normally a path resolved per Arg conventions
    /// (§8); for fd-dup / fd-close shorthand (`&N`, `&N-`, `&-`)
    /// the raw token is carried verbatim and IsDynamicSkip is true.
    /// </summary>
    public string Target { get; init; } = "";
    /// <summary>
    /// True when the target is opaque to path resolution — a dynamic
    /// token (env var, command substitution) or an fd-dup / fd-close
    /// form. Consumers MUST NOT treat Target as a path when this is true.
    /// </summary>
    public bool IsDynamicSkip { get; init; }
}

public enum RedirectDirection
{
    In,         // <
    Out,        // >
    Append,     // >>
    ErrOut,     // 2>
    ErrAppend   // 2>>
}
```

### `CompoundOperator`

```csharp
public enum CompoundOperator
{
    None,      // first clause; no prior operator
    AndIf,     // &&
    OrIf,      // ||
    Sequence,  // ;
    Pipe       // |
}
```

---

## 4. Grammar

Approximate BNF for what the parser accepts. Anything outside this grammar
is unparseable (`ParsedCommand.IsUnparseable = true`).

```
command         := clause (compound_op clause)*
compound_op     := "&&" | "||" | ";" | "|" | NEWLINE
clause          := subshell | bash_c_wrapper | simple_clause
subshell        := "(" command ")"
bash_c_wrapper  := ("bash" | "sh") "-c" QUOTED_STRING
simple_clause   := verb_chain arg* redirect*
verb_chain      := verb_like_word (FW_pair? verb_like_word)*
                                     // greedy walk per §6.1; FW_pair is a
                                     // flag-with-value pair owned by word_0
                                     // (transparent to the walk); stops at
                                     // the first path-shaped or non-verb-like
                                     // token. For
                                     // word_0 ∈ FileVerbs, exactly 1 token.
arg             := word | flag | quoted_string
flag            := "-" letter+ | "--" word
redirect        := redirect_op target
redirect_op     := ">" | ">>" | "<" | "2>" | "2>>"
target          := word | quoted_string
word            := non-whitespace, non-operator characters
quoted_string   := single-quoted | double-quoted
```

**Notes:**

- Whitespace between tokens is one or more spaces or tabs.
- A bare newline outside quotes, heredoc bodies, line continuations, and
  `$(...)` / backtick substitutions is a **statement separator** —
  semantically equivalent to `;`, producing `CompoundOperator.Sequence`.
  Consecutive newlines, leading and trailing newlines, and a newline
  immediately following a compound operator all collapse: they never
  yield an empty clause. The newline after a heredoc terminator likewise
  separates the heredoc's clause from what follows.
- `\` followed by a newline is a line continuation (treat as whitespace).
- Bash line comments (`#` at a word boundary through end-of-line) are
  whitespace-equivalent at the lexer level — they emit a Comment token
  for source fidelity but are filtered alongside Whitespace by the
  parser, so they do not appear in the grammar. See §5 "Comment
  handling" for boundary rules.
- `\` before a metachar inside a double-quoted string escapes the metachar.
- Single-quoted strings preserve all bytes literally — no escape processing.
- Heredocs (`<<EOF ... EOF`) are recognized as a redirect operator but the
  body is **skipped** (not extracted). The clause containing the heredoc
  parses normally with the heredoc body removed.
- Redirect targets matching the POSIX fd-dup / fd-close shorthand —
  `&N`, `&N-`, or `&-` (where `N` is one or more decimal digits) — are
  NOT path-resolved. The parser carries the raw token (e.g. `&1`) on
  `Redirect.Target` and sets `Redirect.IsDynamicSkip = true`. This
  prevents `2>&1` from being incorrectly resolved to `<cwd>/&1`.
- Function definitions, `for`/`while`/`do`/`done`/`then`/`fi`/`case`/`esac`
  control-flow keywords, and arithmetic expansion `$(( ... ))` cause
  `IsUnparseable = true`. We don't support these in v0.1.

---

## 5. Tokenization Rules

The lexer produces tokens consumed by the parser. Token kinds:

- **WORD** — sequence of non-whitespace, non-operator, non-quote chars.
  Example: `git`, `/etc/foo`, `--force`, `~/path`, `$VAR`. Simple
  parameter expansion `${VAR}` (no `//` slash) is absorbed into a Word
  token; the resolver in §8 decides `Kind`.
- **QUOTED_STRING** — single- or double-quoted string. The lexer strips
  the quote delimiters from the token value. Example: `"hello world"`
  becomes the token value `hello world`.
- **OPERATOR** — `&&`, `||`, `;`, `|`, `>`, `>>`, `<`, `2>`, `2>>`,
  `(`, `)`, `<<`, `<<-`.
- **WHITESPACE** — one or more spaces, tabs, or newlines (newlines inside
  a skipped heredoc body are not tokenized). A whitespace run that
  contains a newline — including the newline after a heredoc terminator —
  is flagged as a **statement separator**; the parser retains those
  tokens past `FilterSignificant` and splits clauses on them per §4. A
  pure space/tab run carries no flag and is discarded after splitting.
- **CONTINUATION** — `\` + `\n`. Treated as whitespace.
- **OPAQUE_SUBSTITUTION** — `$(cmd)` or backtick `` `cmd` ``. The full
  substitution slice (including delimiters) becomes a single token.
  Boundary tracking handles nested same-kind regions, nested quotes,
  and `\X` escapes via a shared opaque-region scanner. The parser
  consumes this token as `Arg{ Kind=DynamicSkip, IsPath=false,
  Resolved=null }` per locked interpretation #2.
- **UNPARSEABLE_SENTINEL** — `$((expr))` arithmetic expansion or
  `${var//pat/repl}` complex parameter expansion. The lexer skips past
  the matching close (`))` or `}` respectively) and emits a sentinel
  whose reason names the rejected construct. The parser consumes this
  token by setting outer `ParsedCommand.IsUnparseable = true` (see §11).
- **COMMENT** — `#` at a word boundary (start of input, or preceded by
  whitespace, a newline, an operator, or any other lexer-recognized
  boundary) starts a line comment running to (but not including) the
  next newline. The lexer emits a single Comment token covering the
  `#` and the comment text, for source fidelity. The parser drops
  Comment tokens in `FilterSignificant` alongside Whitespace and
  Continuation — comments produce no clauses, args, redirects, or
  flags. See "Comment handling" below for boundary rules.

### Quote handling

- **Single quotes** `'...'` preserve bytes literally. No escape processing,
  no variable expansion. Anything inside is one token.
- **Double quotes** `"..."` preserve whitespace but allow:
  - `\"` escapes the closing quote.
  - `\\` escapes a backslash.
  - `\$` escapes a dollar sign.
  - `$VAR` and `${VAR}` are recognized as env var references but **not
    expanded** — the token is marked `ArgKind.EnvVar` (or `DynamicSkip`
    if resolution would be required for path classification).
- Unbalanced quotes → `IsUnparseable = true` with reason
  `"unbalanced quote at position N"`.

### Escape handling

- `\X` outside quotes: removes the backslash, takes X literally. Example:
  `echo \$HOME` produces token `$HOME` with `ArgKind.Literal`.
- `\X` inside double quotes: only `\"`, `\\`, `\$`, `\\`, and `\\`+newline
  are recognized escape sequences. Other backslashes preserved literally.

### Operator boundaries

Operators terminate the current token. `cd /tmp&&ls` lexes as
`[cd, /tmp, &&, ls]` — no whitespace required around operators. The lexer
must handle this.

### Comment handling

- An unquoted `#` that appears at a **word boundary** starts a comment
  that runs to (but does not include) the next newline. A word boundary
  is: start of input, or the position immediately after a whitespace
  run, a newline, an operator (`&&`, `||`, `;`, `|`, `>`, `>>`, `<`,
  `2>`, `2>>`, `(`, `)`, `<<`, `<<-`), a quoted string, or an opaque
  substitution. Equivalently: `#` is comment-start everywhere the
  outer lexer dispatch loop sits, because every other lexer rule has
  already consumed its territory before `#` is considered.
- `#` **inside** single or double quotes is a literal character (no
  comment).
- `#` in the **interior** of an unquoted word (e.g. `abc#def`) is a
  literal character. `ReadWord` consumes the whole word before the
  outer loop can see the embedded `#`; there is no re-scanning.
- `\#` (backslash-escaped `#` outside quotes) is consumed by the
  normal escape rule — the backslash is dropped and `#` becomes a
  regular word character. Equivalent example: `cmd \#abc` produces
  one Word token `#abc`.
- The terminating newline is **not** consumed by the Comment token.
  It survives as a Whitespace token, preserving statement-boundary
  semantics for the parser (see §4).
- A Comment token's `Value` is empty (matching `Whitespace` /
  `Continuation`); `SourceStart` / `SourceLength` identify the slice
  including the leading `#` so callers that need the literal text can
  recover it from the original input span.
- **Effect on parsing**: comment-only input parses to
  `Clauses = []`, `IsUnparseable = false` — mirroring empty-input
  behavior. A comment leading, trailing, or interleaved with a clause
  contributes no tokens to the verb chain, args, or redirects of any
  clause.

---

## 6. Verb Tables

These are **data**, not logic. Implement as `static readonly` collections.

### 6.1 Verb-chain extraction (greedy heuristic)

Per issue #27 (locked in v0.1.4-alpha), the parser does not consult a
static arity table. It walks consecutive verb-like Word tokens from the
clause start. The walk stops before a path-shaped or non-verb-like token.
This rule naturally scales to unknown CLIs
(`freshdesk ticket list`, `kubectl get pods`, `dotnet ef migrations add`)
without curated table entries.

#### IsVerbLikeToken predicate

A token is "verb-like" when **all** of these hold:

- `Kind == BashTokenKind.Word` (quoted strings are values, never verbs at
  index ≥ 1).
- Length is in `[1, 64]` characters.
- First character is an ASCII lowercase letter `[a-z]`.
- Remaining characters are drawn from `[a-z0-9._-]` only.

The predicate is implemented in `BashVerbs.IsVerbLikeToken`. The leading
lowercase requirement mirrors real CLI subcommand convention; the
character allow-list naturally excludes flags (`-x` starts with `-`),
paths (`/`, `\`, `~`), env-var refs (`$VAR`), URLs (`://`), globs
(`* ? [`), and user-named identifiers (uppercase first char like
`InitialCreate`).

The walk also rejects a token that matches the §8 path-shape heuristic.
This rule applies even when the lexical predicate accepts the token.

#### Walk algorithm

For a clause whose first token is a Word `firstVerb`:

1. Append `firstVerb` to the verb chain (it does not need to satisfy
   `IsVerbLikeToken` — bare commands like `Curl` or `_init` are still
   commands). Do not apply the path-shape boundary at command position.
   A command such as `deploy.sh` or `./deploy.sh` remains the first verb.
2. Iterate the remaining tokens in order. For each token `t`:
   - If `t.Kind != Word`: **stop**.
   - If `t` is a flag (`IsFlagWord`):
     - If `firstVerb` has a `FlagsWithValue` entry containing
       `StripEqualsValue(t.Value)` AND the next token is `Word` or
       `QuotedString` AND `t.Value` has no inline `=`: consume both as a
       flag-value pair, mark their indices for `consumedFlagValueIndices`,
       and continue walking.
     - Otherwise: **stop**.
   - If `firstVerb ∈ FileVerbs`: **stop** (1-token carveout — see below).
   - If `BashResolver.LooksLikePath(t.Value)`: **stop**. The argument pass
     uses the same classifier and preserves the token as a path argument.
   - If `!IsVerbLikeToken(t)`: **stop**.
   - Otherwise: append `t.Value` to the verb chain and continue.

If the first token is a `QuotedString` (e.g. `"git" push origin main`),
emit a 1-token verb chain `[firstVerb]` and skip the walk entirely. Bash
treats the quoted form as a verb-identity carrier; remaining tokens are
arg-list material.

#### FileVerb 1-token carveout

For verbs in §6.3 `FileVerbs` (file-mutation, file-read, editors,
compression, shell loaders, etc.), the verb chain stops at exactly one
token. The flag-with-value consumption still runs so the value of
`curl -o file`, `tar -C /path`, `git -C /repo` style flags picks up
`IsPath=true` via the `FlagValueIsPath` mechanism.

The carveout exists because FileVerbs use SPEC §7 per-verb positional
rules to classify args as paths. Without it, a bare-name target like
`cat README` would over-extract — `README` is shape-wise verb-like —
and lose the `IsPath=true` classification downstream consumers depend
on for zone-gate evaluation.

#### Examples

| Input | Verb chain | Args |
|---|---|---|
| `git push origin main` | `[git, push, origin, main]` | `[]` (over-extracts; see §6.1.1) |
| `git -C /repo worktree list --porcelain` | `[git, worktree, list]` | `[-C, /repo, --porcelain]` |
| `freshdesk ticket list --status open` | `[freshdesk, ticket, list]` | `[--status, open]` |
| `kubectl get pods my-pod` | `[kubectl, get, pods, my-pod]` | `[]` |
| `aws s3 cp src dst` | `[aws, s3, cp, src, dst]` | `[]` (bare-word path args over-extract) |
| `dotnet ef migrations add InitialCreate` | `[dotnet, ef, migrations, add]` | `[InitialCreate]` (stops at uppercase) |
| `deploy.sh status` | `[deploy.sh, status]` | `[]` (command position wins) |
| `git diff install-skills.sh` | `[git, diff]` | `[install-skills.sh]` (path-shaped operand) |
| `kubectl apply deployment.yaml` | `[kubectl, apply]` | `[deployment.yaml]` (path-shaped operand) |
| `tool plugin.sh list` | `[tool]` | `[plugin.sh, list]` (path evidence wins) |
| `cat /etc/passwd` | `[cat]` | `[/etc/passwd]` (FileVerb carveout) |
| `cat README` | `[cat]` | `[README]` (FileVerb carveout preserves IsPath) |
| `ls -la /tmp` | `[ls]` | `[-la, /tmp]` (FileVerb carveout) |
| `chmod 755 file` | `[chmod]` | `[755, file]` (digit-start kills walk; FileVerb anyway) |
| `echo hello` | `[echo, hello]` | `[]` (echo is not a FileVerb; over-extracts) |

### 6.1.1 Consumer pattern-matching guidance

`Clause.Verb` is a **convenience hint, not a security contract**.
The parser deliberately over-extracts on bare-word args because no
syntactic rule disambiguates `origin` (a branch name) from `worktree`
(a subcommand verb) without per-CLI semantic knowledge — and we will
not bake per-CLI knowledge into the parser.

Consumers needing security-grade verb identification should pattern-prefix
match against the source-ordered `Clause.Elements` view:

> A command matches an approval pattern `P` if and only if the first
> `len(P.verb_prefix)` verb elements of the command equal `P.verb_prefix`.

This punts depth choice to the consumer (via the pattern they author)
and accommodates the parser's over-extraction transparently:

- Pattern `git push *` (verb-prefix length 2) matches `git push origin
  main` because the first two command tokens are `[git, push]`.
- Pattern `kubectl get pods *` (verb-prefix length 3) matches
  `kubectl get pods my-pod` because the first three tokens are
  `[kubectl, get, pods]`.
- Auto-proposed patterns for unknown commands should default to
  the **full** extracted verb chain (greedy match), which is the
  security-correct default: a subsequent variation re-prompts rather
  than silently auto-grants. Operators wanting broader grants opt in
  explicitly.

False-negative (re-prompt) is recoverable. False-positive (silent
destructive grant) is not. Narrow-by-default favors the recoverable
failure mode.

The path-shape boundary requires no command dictionary. It uses the same
curated evidence as argument classification. A rare extension-shaped
subcommand becomes a path argument because the stronger path evidence wins.

### 6.2 CWD verbs

Verbs whose first non-flag positional arg becomes the cwd for subsequent
clauses in the same compound (see §9).

```csharp
internal static readonly HashSet<string> CwdVerbs =
    new(StringComparer.OrdinalIgnoreCase)
{
    "cd", "chdir", "popd", "pushd",
    "push-location", "set-location"  // PowerShell idioms (forward-compat)
};
```

### 6.3 FILE verbs

Verbs whose positional args are paths. The default extraction rule is "all
non-flag positional args after the verb chain are paths." Per-verb overrides
in §7.

```csharp
internal static readonly HashSet<string> FileVerbs =
    new(StringComparer.OrdinalIgnoreCase)
{
    // CWD verbs are also FILE verbs (their target is a path)
    "cd", "chdir", "popd", "pushd", "push-location", "set-location",
    // File mutation
    "rm", "cp", "mv", "mkdir", "rmdir", "touch", "ln",
    "chmod", "chown", "chgrp", "stat", "test",
    // Read
    "cat", "less", "more", "head", "tail", "grep", "rg",
    "find", "fd", "locate", "wc", "file",
    // Editors / text tools
    "sed", "awk", "vi", "vim", "nano", "emacs", "ed",
    // Compression
    "tar", "zip", "unzip", "gzip", "gunzip", "bzip2", "xz",
    // Network with file targets
    "curl", "wget", "scp", "rsync", "sftp",
    // Shell / interpreter loaders
    "bash", "sh", "zsh", "fish",
    "python", "python3", "node", "ruby", "perl", "php",
    // Diff / patch
    "diff", "patch", "cmp",
    // Listing
    "ls", "dir", "tree",
};
```

### 6.4 CMD_FILE verbs (Windows cmd / PowerShell file utilities)

The Windows native file utilities. As of v0.2.0 the PowerShell parser's
`PwshVerbs.FileVerbs` table consumes this reserved set
(`type`, `copy`, `move`, `del`, `xcopy`, `robocopy`, `findstr`) so a
native Windows file tool in a PowerShell command still gets path
classification. PowerShell *cmdlet* file verbs (`Get-Content`,
`Remove-Item`, `Copy-Item`, ...) are owned by `SPEC.POWERSHELL.md` §6.4 —
they are recognized by cmdlet shape and alias resolution, not by this
table. A Windows `cmd` parser remains deferred (§18).

```csharp
internal static readonly HashSet<string> CmdFileVerbs =
    new(StringComparer.OrdinalIgnoreCase)
{
    "type", "copy", "move", "del", "erase", "ren",
    "xcopy", "robocopy", "findstr",
};
```

---

## 7. Per-Verb Path-Arg Extraction Rules

The default rule for FILE verbs: every non-flag positional arg after the
verb chain is a path. Per-verb overrides:

| Verb | Rule |
|---|---|
| `chmod` | First non-flag positional is **mode** (e.g. `755`, `+x`); rest are paths. |
| `chown` | First non-flag positional is **user[:group]**; rest are paths. |
| `chgrp` | First non-flag positional is **group**; rest are paths. |
| `ln` | All positionals are paths (source then target). |
| `find` | First positional is a path; rest are predicate args (skip). |
| `grep` | First positional is **pattern**; rest are paths. |
| `rg` | First positional is **pattern**; rest are paths. |
| `sed` | First positional is **script**; rest are paths. |
| `awk` | First positional is **program**; rest are paths. |
| `tar` | Action flag determines path roles; default to extracting all non-flag positionals as paths. |
| `curl`, `wget` | First positional is **URL**, not a path. `-o file` flag arg is a path. |
| `scp`, `rsync`, `sftp` | All positionals are paths (some remote). |
| `cd`, `chdir`, `pushd`, `popd` | First non-flag positional is the cwd target (a path). |
| Others (in FileVerbs, no override) | All non-flag positionals are paths. |

### Flag-with-value handling

Some flags take values (`-o file`, `-C /repo`, `--output=file`). The parser
must know which flags consume the next token as a value. Curated table:

```csharp
internal static readonly IReadOnlyDictionary<string, HashSet<string>>
    FlagsWithValue = new Dictionary<string, HashSet<string>>(
        StringComparer.OrdinalIgnoreCase)
{
    ["git"]   = new HashSet<string>(StringComparer.Ordinal) { "-c", "-C", "--git-dir", "--work-tree" },
    ["curl"]  = new HashSet<string>(StringComparer.Ordinal) { "-o", "--output", "-d", "--data" },
    ["wget"]  = new HashSet<string>(StringComparer.Ordinal) { "-O", "--output-document" },
    ["docker"]= new HashSet<string>(StringComparer.Ordinal) { "-v", "--volume", "-f", "--file" },
    ["tar"]   = new HashSet<string>(StringComparer.Ordinal) { "-f", "--file", "-C", "--directory" },
    // Add as corpus surfaces real cases.
};
```

> **Note:** the value type is `HashSet<string>` (not `IReadOnlySet<string>`)
> because `IReadOnlySet<string>` is .NET 5+ only and the library
> multi-targets `netstandard2.0`. Internal-only — no public-API impact.

> **Native option case.** The outer verb dictionary retains its existing
> case-insensitive lookup, but each native option set uses `Ordinal`. Native
> executables receive option spelling unchanged in Bash and PowerShell and may
> assign different meanings by case. Git lists both `-c` and `-C`: both consume
> a value. The generic table classifies uppercase `-C` values as paths and
> lowercase `-c` values as non-paths. Executable-aware consumers still
> reinterpret command-scoped forms such as `git commit -c/-C`, where Git uses
> the operand as a revision rather than the generic table's global meaning.

> **Note:** the verb-chain walk consumes flag-with-value pairs
> transparently. For `git -C /repo log`, the walk consumes `-C /repo`
> before evaluating the next token; `log` is then verb-like and extends
> the chain, producing `Verb.Tokens = ["git", "log"]` per §12's example.
> The same mechanic lets `git -C /repo worktree list` extract the full
> 3-token chain per §6.1.

When a flag-with-value consumes the next token, the consumed token's
`IsPath` flag is set if the value is path-shaped (per the resolver in §8).
For `git -C /repo log`: the `-C` flag consumes `/repo`, marks it as a
path, then the verb chain continues with `log`.

`--output=file` (equals form) is parsed as one token; the path value after
`=` is extracted into a synthetic Arg with `IsPath=true`.

---

## 8. Resolver

For each Arg with potential path content, the resolver attempts to produce
a normalized absolute path. Resolution order:

0. **Single-quoted bypass.** If the source token came from a single-quoted
   string (per §5: bytes are preserved literally — no escape processing,
   no variable expansion), the resolver skips steps 1–5 entirely. Kind is
   `Literal`; `IsPath` is `true` and `Resolved` is set only when the slot
   is a path AND `TryResolveAbsolutePath` on the raw bytes succeeds. So
   `cat '/etc/passwd'` still produces a resolved path, but `echo '$HOME'`
   stays literal — `$HOME` is not expanded inside single quotes.

1. **Tilde expansion.** `~` → `BashParserOptions.HomeDirectory`.
   `~/foo` → `<home>/foo`. `~user` not supported → `DynamicSkip`.

2. **Env-var substitution.** `$VAR` and `${VAR}` are **not expanded**
   even if the value is in `Environment`. We treat any env var reference
   as `DynamicSkip` because the env var available at parse time may
   differ from what's available when the agent's command actually runs.
   `$HOME` is the **only** exception — we treat it as equivalent to `~`
   and expand it from `BashParserOptions.HomeDirectory`.

3. **`filesystem::/path` prefix stripping.** Some tools emit
   `filesystem::/path/to/file`; strip the prefix. Become `/path/to/file`.

4. **Glob detection.** Tokens containing `*`, `?`, or `[` are marked
   `ArgKind.Glob`. The resolver does **not** expand globs. The token
   stays as-is in `Raw`; `Resolved` is null.

   **In a path-arg slot:** `IsPath = true`. Consumers can apply the
   "covering directory" heuristic (`Path.GetDirectoryName(Raw)`) to
   reason about the directory the glob resolves under (e.g.
   `/tmp/*.bak` → `/tmp`).

   **In a non-path slot:** `IsPath = false`.

   Per locked interpretation #3, glob and DynamicSkip carry **distinct**
   signals — globs preserve a useful covering-dir hint that DynamicSkip
   tokens lack.

5. **Relative path resolution.** Tokens not starting with `/` (or `\\` on
   Windows, or a Windows drive letter `X:`) are joined to
   `BashParserOptions.WorkingDirectory` (lazy fallback to
   `Environment.CurrentDirectory` when null). On
   `IOException` / path-format exceptions during resolution, fall through
   to `Kind = DynamicSkip, IsPath = false, Resolved = null`.

6. **DynamicSkip predicates.** A token is `Kind = DynamicSkip,
   IsPath = false, Resolved = null` when:
   - It contains an unresolved env-var reference (other than `$HOME`)
     in a slot the verb's rule classifies as a path.
   - Resolution throws an `IOException` or path-format exception.

   Globs do NOT downgrade to DynamicSkip — they carry their own Kind so
   consumers can still apply the covering-dir heuristic. Consumers must
   not use `Raw` as a literal path for `DynamicSkip` tokens.

### Path-shape heuristic

When deciding whether a token "looks like a path" (used to decide whether
to apply the resolver):

```
LooksLikePath(token) =
   token starts with '/' (Unix absolute)
|| token starts with '\\' or '<letter>:' (Windows absolute)
|| token starts with './' or '../' (Unix relative)
|| token starts with '~' (Tilde)
|| token contains '/' anywhere
|| token contains '\\' at a NON-TRAILING position
|| token ends with a known file extension (.json, .md, .txt, .conf, ...)
|| token is in the args of a FileVerb at a position the per-verb rule
   marks as a path
```

A lone trailing `\\` is excluded because it commonly appears as a
double-quote escape-collapse artifact (`"foo\\"` lexes to Value `foo\\`)
and is not a meaningful path signal on its own.

The per-verb rule wins when present; the heuristic is the fallback.

---

## 9. cd-in-Compound Propagation

The agent's natural idiom is `cd /target && cmd1 && cmd2`. Bash semantics:
`cmd1` and `cmd2` execute with cwd `/target`. The parser honors this for
**path attribution within the same compound**.

### Rules

1. **First clause is a `cd` or `chdir` verb**: the cd target becomes the
   **attributed cwd** for subsequent clauses in the same compound. **Only
   `cd` and `chdir`** propagate attribution per locked interpretation #5.
   `pushd`, `popd`, `push-location`, and `set-location` are still listed
   in `CwdVerbs` so their first non-flag positional is path-classified
   (the target shows up as `IsPath=true`), but they do **not** add a
   synthetic attribution arg to subsequent clauses. A future v0.1.x or
   v0.2 with PowerShell support may model `pushd`/`popd` as a proper
   directory stack.
2. **Subsequent clauses inherit the attributed cwd** as if it were
   prepended with `-C` semantics. Specifically: a synthetic `Arg` with
   `IsPath=true`, `Resolved=<cd target>`, and `Kind=Literal` is added to
   each subsequent clause's `Args` list at the **end**, marked with a flag
   `IsCwdAttribution=true` so consumers can distinguish it from
   user-emitted args.

   *(Add `IsCwdAttribution: bool` to the `Arg` record. Default false.)*

3. **A subsequent `cd`** in the same compound **replaces** the attributed
   cwd for clauses after it. (`cd /a && cmd1 && cd /b && cmd2` → cmd1
   inherits `/a`, cmd2 inherits `/b`.) The replacing `cd /b` itself still
   *receives* `/a` as a synthetic attribution arg (rule 2) before becoming
   the new source — additive semantics per rule 5.

4. **Subshell boundaries reset attribution.** `cd /a && (cd /b && cmd1) && cmd2`:
   cmd1 (inside subshell) inherits `/b`; cmd2 (outside subshell) inherits
   `/a` (the subshell's `cd /b` does not leak out). A subshell *inherits*
   outer attribution on entry (so `cd /a && (cmd)` still attributes cmd
   to /a) but its own cd changes stay isolated.

5. **Attribution does not change the clause's verb or original args.**
   The attribution is purely additive — the `cd` clause itself is still
   parsed normally, and subsequent clauses retain everything the user
   typed, plus the synthetic Arg.

### Dynamic-cd attribution (locked interpretation #6)

When the cd target itself is `Kind=DynamicSkip` (e.g. `cd $REPO`), we
statically don't know the resolved cwd. To preserve the cwd-uncertainty
signal for subsequent clauses:

- A synthetic `Arg { Raw="<dynamic-cwd>", Resolved=null, Kind=DynamicSkip,
  IsPath=false, IsCwdAttribution=true }` is appended to each subsequent
  clause (instead of the literal-cd flavor).
- Relative path args in subsequent clauses are not re-resolved against a
  fall-back cwd; they surface as `Kind=DynamicSkip, IsPath=false,
  Resolved=null` so consumers route to safe-fail rather than trust a
  guessed working directory.

Consumers that iterate `IsPath=true` args won't see the synthetic
attribution arg; consumers that specifically check `IsCwdAttribution`
can detect "this clause's cwd context is unknown" and elevate to
user-prompt instead of treating it like a default-cwd command.

### Example

Input: `cd /target && git -C /other log && cat file.txt`

Parsed clauses:

```
Clause 0: Operator=None, Verb=[cd], Args=[/target]
Clause 1: Operator=AndIf, Verb=[git, log],
          Args=[
            Arg{Raw="-C",IsFlag=true},
            Arg{Raw="/other",IsPath=true,Resolved="/other"},
            Arg{Raw="/target",IsPath=true,Resolved="/target",IsCwdAttribution=true}
          ]
Clause 2: Operator=AndIf, Verb=[cat],
          Args=[
            Arg{Raw="file.txt",IsPath=true,Resolved="/target/file.txt"},
            Arg{Raw="/target",IsPath=true,Resolved="/target",IsCwdAttribution=true}
          ]
```

Note: `file.txt` in clause 2 resolves against the attributed cwd
`/target` to produce `/target/file.txt`. The attributed-cwd Arg is also
appended for completeness, even though the resolver already used it.

Consumers can choose to ignore `IsCwdAttribution=true` args if they
already see the resolved path in another arg.

---

## 10. Subshell & `bash -c` Recursion

### Subshells

Subshells are clauses wrapped in parens: `(cd /a && cmd)`. The parser
recognizes the parens and **flattens** the subshell's inner clauses into
the parent's `Clauses` list, marking each with `IsSubshell=true` so
consumers can distinguish them from outer-compound clauses. A subshell
*inherits* the outer compound's cd attribution on entry but its own cd
changes stay isolated to the subshell (rule 4 above).

Specifically: `(cd /b && cmd) && cmd2` produces three clauses:

```
Clause 0: Op=None, Verb=cd, Args=[/b], IsSubshell=true
Clause 1: Op=AndIf, Verb=cmd, Args=[/b attribution], IsSubshell=true
Clause 2: Op=AndIf, Verb=cmd2, Args=[]   // no /b attribution — subshell isolated
```

### `bash -c` Recursion

`bash -c "inner command"` and `sh -c "inner command"` are common wrappers
the agent emits. The parser:

1. Recognizes the `bash -c` or `sh -c` prefix.
2. Parses the quoted argument as a fresh `ParsedCommand`.
3. Surfaces the inner command's clauses inline in the outer's `Clauses`
   list, each with `IsCommandStringWrapped=true`.

Example: `bash -c "cd /a && cmd"` produces:

```
Clause 0: Op=None, Verb=cd, Args=[/a], IsCommandStringWrapped=true
Clause 1: Op=AndIf, Verb=cmd, Args=[/a attribution], IsCommandStringWrapped=true
```

The outer `bash -c` itself does not appear as a clause — it's "consumed"
by the recursion. Consumers that care that this came from a wrapper can
inspect `IsCommandStringWrapped` on the surfaced clauses.

**Recursion limit:** parse `bash -c "bash -c ..."` chains up to depth 5.
Deeper nesting → set the outer `ParsedCommand.IsUnparseable = true` with
reason `"bash -c recursion depth exceeded (>5)"` per locked interpretation
#4. (`Clause` has no `IsUnparseable` field; we surface the overflow on the
top-level ParsedCommand so consumers safe-fail per §11.)

---

## 11. Parser Anomaly Behavior

When the parser cannot produce a clean AST:

1. **Set `ParsedCommand.IsUnparseable = true`.**
2. **Set `UnparseableReason`** to a human-readable diagnostic.
3. **Return whatever clauses were successfully parsed** in `Clauses`. May
   be empty.
4. **Never throw** on well-formed input strings (only throw on null).

Conditions that produce `IsUnparseable = true`:

- Unbalanced quotes (`"foo` with no closing `"`).
- Unbalanced parens (`(cmd && cmd2`).
- Unrecognized control-flow keywords (`for`, `while`, `do`, `done`,
  `then`, `fi`, `case`, `esac`).
- Function definitions (`name() { ... }`).
- Process substitution (`<(cmd)`, `>(cmd)`).
- Arithmetic expansion `$((expr))` (per §1 non-goal; lexer emits an
  UNPARSEABLE_SENTINEL token; parser sets the outer flag).
- Complex parameter expansion `${var//pat/repl}` (per §1 non-goal; same
  mechanism).
- Recursion depth exceeded on `bash -c` chains (>5 levels).

**Diagnostic precedence.** When multiple conditions could fire on a
single input (e.g. `case x in a) ;; esac` is both a control-flow
keyword AND has unbalanced parens), the parser checks them in this
order so the most informative reason wins:

1. Lexer-emitted `UnparseableSentinel` tokens (unbalanced quote /
   unterminated heredoc / arithmetic / complex parameter expansion).
2. Control-flow keyword at verb position (start of input or
   immediately after a clause separator `&&`, `||`, `;`, `|`, or
   `(`). Catches `case x in a) ;; esac` before the `)` triggers a
   paren-balance error.
3. Function definition pattern (`Word` immediately followed by `(`,
   `)`).
4. Process substitution (`<(` or `>(` adjacent).
5. Segment-split errors (unbalanced parens, unexpected operator).
6. `bash -c` recursion depth cap.

Consumers (e.g. Netclaw's gate evaluator) route unparseable commands to a
safe-fail path (prompt the user; offer only Once and Deny — no persistent
grants on shapes the parser can't model).

---

## 12. Public Examples

A handful of input/expected-AST pairs to anchor understanding. These belong
in the corpus (§13) verbatim.

### Simple verb

Input: `ls -la /tmp`

```
ParsedCommand {
  Source = "ls -la /tmp",
  IsUnparseable = false,
  Clauses = [
    Clause {
      Operator = None,
      Verb = VerbChain { Tokens = ["ls"] },
      Args = [
        Arg { Raw = "-la", IsFlag = true, Kind = Literal },
        Arg { Raw = "/tmp", IsPath = true, Resolved = "/tmp", Kind = Literal }
      ],
      Redirects = [],
      IsSubshell = false,
      IsCommandStringWrapped = false
    }
  ]
}
```

### Multi-token verb (greedy over-extraction)

Input: `git push origin main`

```
Clauses = [
  Clause {
    Verb = VerbChain { Tokens = ["git", "push", "origin", "main"] },
    Args = []
  }
]
```

The greedy heuristic absorbs `origin` and `main` because they're
syntactically indistinguishable from subcommand verbs (lowercase
identifiers, no path-shape). Consumers gating on `git push *` use
pattern-prefix length 2 — see §6.1.1.

Input: `freshdesk ticket list --status open`

```
Clauses = [
  Clause {
    Verb = VerbChain { Tokens = ["freshdesk", "ticket", "list"] },
    Args = [
      Arg { Raw = "--status", Kind = Literal, IsFlag = true },
      Arg { Raw = "open", Kind = Literal, IsPath = false }
    ]
  }
]
```

The walk stops at `--status` (a flag with no `FlagsWithValue` entry for
`freshdesk`). The full subcommand stack is captured without requiring a
curated table entry — the canonical benefit motivating the change.

### Compound with cd attribution

Input: `cd /target && cmd1 && cmd2 file.txt`

```
Clauses = [
  Clause { Verb = [cd], Args = [/target attributed-as-path], Op = None },
  Clause {
    Verb = [cmd1], Op = AndIf,
    Args = [Arg { Raw = "/target", Resolved = "/target",
                  IsPath = true, IsCwdAttribution = true }]
  },
  Clause {
    Verb = [cmd2], Op = AndIf,
    Args = [
      Arg { Raw = "file.txt", Resolved = "/target/file.txt", IsPath = true },
      Arg { Raw = "/target", Resolved = "/target",
            IsPath = true, IsCwdAttribution = true }
    ]
  }
]
```

### `git -C` flag-with-value

Input: `git -C /repo log`

```
Clauses = [
  Clause {
    Verb = VerbChain { Tokens = ["git", "log"] },
    Args = [
      Arg { Raw = "-C", IsFlag = true },
      Arg { Raw = "/repo", IsPath = true, Resolved = "/repo" }
    ],
    Elements = [
      ClauseElement { Value = "git", Role = Verb,
                      PrecedingVerbElementCount = 0 },
      ClauseElement { Value = "-C", Role = Argument,
                      PrecedingVerbElementCount = 1 },
      ClauseElement { Value = "/repo", Role = Argument,
                      PrecedingVerbElementCount = 1 },
      ClauseElement { Value = "log", Role = Verb,
                      PrecedingVerbElementCount = 1 }
    ]
  }
]
```

### Redirect

Input: `cmd > /tmp/out.txt`

```
Clauses = [
  Clause {
    Verb = [cmd],
    Args = [],
    Redirects = [Redirect { Direction = Out, Target = "/tmp/out.txt" }]
  }
]
```

### Subshell isolation

Input: `cd /a && (cd /b && cmd1) && cmd2`

```
Clauses = [
  Clause { Verb = [cd], Args = [/a], Op = None },
  Clause { Verb = [cd], Args = [/b], Op = AndIf, IsSubshell = true,
           Args = [/a attribution from outer compound] },
  Clause { Verb = [cmd1], Op = AndIf, IsSubshell = true,
           Args = [/b attribution — local to subshell] },
  Clause { Verb = [cmd2], Op = AndIf,
           Args = [/a attribution — inherited from outer cd, NOT /b] }
]
```

### Dynamic skip

Input: `rm $UNRESOLVED/foo`

```
Clauses = [
  Clause {
    Verb = [rm],
    Args = [
      Arg { Raw = "$UNRESOLVED/foo", Kind = DynamicSkip, IsPath = false,
            Resolved = null }
    ]
  }
]
```

Consumer impact: zone-gate sees zero paths to evaluate; routes to the
fallback "treat as one untrusted path = the raw token" prompt.

### Unparseable

Input: `for i in 1 2; do echo $i; done`

```
ParsedCommand {
  Source = "for i in 1 2; do echo $i; done",
  IsUnparseable = true,
  UnparseableReason = "control-flow keyword 'for' is not supported in v0.1",
  Clauses = []   // or partial; consumer should not rely on contents
}
```

---

## 13. Test Corpus Contract

The corpus is the **acceptance contract** for the parser. Implementation is
"done" when every corpus entry parses to its expected AST.

### Location

`tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` — one file per corpus
entry. File name pattern: `NN_descriptive_slug.json` where NN is a
zero-padded sequence number.

### Format

Each file:

```json
{
  "name": "Multi-token verb: git push",
  "input": "git push origin main",
  "expected": {
    "isUnparseable": false,
    "clauses": [
      {
        "operator": "None",
        "verb": ["git", "push"],
        "args": [
          { "raw": "origin", "kind": "Literal", "isPath": false },
          { "raw": "main", "kind": "Literal", "isPath": false }
        ],
        "redirects": [],
        "isSubshell": false,
        "isCommandStringWrapped": false
      }
    ]
  },
  "notes": "Optional explanation of edge case being captured."
}
```

An entry may add an `elements` list to a clause to pin the complete
`Clause.Elements` projection (`raw`, `value`, `role`, `sourceStart`,
`sourceLength`, `precedingVerbElementCount`, `kind`, `isFlag`, `isPath`, and
`resolved`). The field is opt-in so older corpus entries remain readable;
issue-specific provenance entries SHALL include it.

### Coverage targets for v0.1

Author at least:

- 10 simple-verb cases (ls, pwd, echo, cat, grep, etc.)
- 10 multi-token-verb cases (git push, dotnet test, docker compose up, etc.)
- 15 compound cases (`&&`, `||`, `;`, `|` combinations)
- 10 `cd`-in-compound propagation cases (single, sequential, with subshell)
- 10 quote-handling cases (single, double, escaped, mixed)
- 10 redirect cases (`>`, `>>`, `<`, `2>`, `2>>`, multiple redirects)
- 10 subshell cases (with and without isolation effects)
- 10 `bash -c` recursion cases (depth 1, 2, with inner compounds)
- 10 dynamic-skip cases (`$VAR`, `${VAR}`, `~user`, glob args)
- 10 per-verb path-rule cases (chmod, chown, find, grep, curl, git -C, etc.)
- 10 unparseable cases (unbalanced quotes, control-flow keywords, function
  definitions)

**Total minimum: 105 entries.** Strive for 150+ once seeded from sanitized
real-world commands (see §14).

### Test runner

A single xunit test method enumerates `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json`, parses
each `input`, and asserts the result matches `expected` field-by-field.
The runner emits a per-corpus-entry test name so failures point at the
specific case.

```csharp
[Theory]
[MemberData(nameof(CorpusEntries))]
public void Corpus_entry_parses_to_expected_ast(CorpusEntry entry)
{
    var parser = new BashParser();
    var actual = parser.Parse(entry.Input);
    AstAssert.Equal(entry.Expected, actual);   // structural equality
}

public static IEnumerable<object[]> CorpusEntries()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "Corpus", "bash");
    foreach (var file in Directory.GetFiles(dir, "*.json"))
    {
        var entry = JsonSerializer.Deserialize<CorpusEntry>(File.ReadAllText(file));
        yield return [entry];
    }
}
```

`AstAssert.Equal` is a helper that does structural equality with helpful
diff messages on mismatch. Implement to taste.

---

## 14. Sanitization Process

A portion of the corpus seeds from real shell commands captured from
agent dogfood logs. The seed source is a daemon log file at
`~/.netclaw/logs/daemon-2026-05-09.log` (and similar). These logs contain
PII (usernames, repo paths, channel/thread IDs) that **must not** appear
in the public corpus.

### Sanitization rules

Apply these transformations to every seeded entry **before** committing:

| Pattern | Replacement |
|---|---|
| `/home/<username>/` (any specific username) | `/home/user/` |
| `/Users/<username>/` (macOS) | `/Users/user/` |
| `~/<username>/` | `~/` |
| Specific repo paths like `/home/user/repositories/stannardlabs/<repo>` | `/home/user/repos/sample-repo` |
| Specific repo names (not in the org's public list) | `sample-repo` or `project` |
| Slack channel IDs (`D[A-Z0-9]{10}`) | `<channel>` (only if appears in command) |
| Slack thread IDs (`\d{10}\.\d{6}`) | `<thread>` |
| Internal hostnames | `internal-host.example` |
| Email addresses | `user@example.com` |
| API keys, tokens, secrets (any `[A-Za-z0-9]{20,}` that looks key-shaped) | `<redacted>` (but prefer to drop the entry entirely) |

### Workflow

1. Pull candidate commands from logs:
   ```bash
   grep -oP "command \K\{[^}]+\}" ~/.netclaw/logs/daemon-*.log \
     | jq -r .Command | sort -u > /tmp/raw-corpus.txt
   ```
2. Apply sanitization (script TBD) — for each line, walk the table above.
3. **Manual review** of each sanitized entry before committing. The script
   can miss patterns; a human (or careful agent) reviews for residual PII.
4. Drop any entry that can't be cleanly sanitized (too many specific
   identifiers; rewrite as a fully-synthetic entry instead).
5. Commit with a clear message: `chore(corpus): seed from sanitized agent
   logs (NN entries)`.

### Audit gate

Before any corpus PR merges, CI runs a regex check against the corpus
files for residual PII patterns. The check fails the build if any
sanitization-rule pattern appears in any committed corpus file. Implement
as a small `dotnet test` that scans `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` for the
forbidden patterns.

---

## 15. CI & Release Flow

The repo template already has:

- `.github/workflows/pr_validation.yml` — runs `dotnet test` on PR.
- `.github/workflows/publish_nuget.yml` — publishes to NuGet on release tag.

Adapt for ShellSyntaxTree:

- **Trigger NuGet publish on tag pattern `v*.*.*`** (e.g. `v0.1.0-alpha`).
- **Test job** runs the corpus runner plus all unit tests.
- **PII audit job** runs the sanitization-pattern scan over `tests/ShellSyntaxTree.Tests/Corpus/`.

### Versioning

- **v0.1.x-alpha** — pre-release alpha cycle. Public API surface per §2 is
  locked; internal data and behavior are subject to course-correction
  while real-world feedback lands (e.g. v0.1.4-alpha replaces the
  `BashArity` static table with the greedy verb-chain heuristic per
  issue #27).
- **v0.1.0** — first publishable non-alpha cut. Bash-only.
- **v0.1.x** (post-0.1.0) — additive changes and SPEC-conformance fixes
  (more verb table entries, more corpus, bug fixes). A fix may shift the
  parsed-AST shape when the prior shape violated this SPEC — e.g. v0.1.5
  makes a bare newline a statement separator per §4. The §2 public API
  surface stays locked.
- **v0.2.0** — first PowerShell parser implementation (`PwshParser`). Adds
  the shared `ShellParserOptions` base, the additive `VerbChain.CanonicalVerb`
  / `VerbChain.IsDynamic` fields, and the breaking `Clause.IsBashCWrapped` →
  `IsCommandStringWrapped` rename. A breaking AST change on a `0.x` minor is
  permitted by Appendix A when `RELEASE_NOTES.md` carries the old→new mapping
  and Netclaw is updated in lockstep. See `SPEC.POWERSHELL.md`.
- **v1.0.0** — ready when at least one external consumer beyond Netclaw
  ships against it without finding API gaps.

### Release notes

Update `RELEASE_NOTES.md` for each tagged release. Format:

```
0.1.0-alpha YYYY-MM-DD

* First publishable cut.
* Bash parser per SPEC.md v0.1.
* Corpus: N entries.
* Public API: IShellParser, BashParser, ParsedCommand, Clause, VerbChain,
  Arg, Redirect, ArgKind, RedirectDirection, CompoundOperator.
```

---

## 16. Implementation Sequencing

A natural order for the implementer:

1. **Bootstrap projects.** Create `src/ShellSyntaxTree/ShellSyntaxTree.csproj`
   (library) and `tests/ShellSyntaxTree.Tests/ShellSyntaxTree.Tests.csproj`
   (xunit). Update `SampleSln.slnx` (rename to `ShellSyntaxTree.slnx`) and
   delete the `Akka.Console` sample.
2. **Update template defaults.** `Directory.Build.props`: replace Akka
   metadata with ShellSyntaxTree. `README.md`: real intro. `LICENSE`: keep
   Apache-2.0 (already correct). `Directory.Packages.props`: add xunit, drop
   Akka.Hosting. `Tags`: bash, shell, parser, ast.
3. **Write public API skeleton** (§2): interface + record stubs that compile
   but throw `NotImplementedException` on `Parse()`. Lock the surface
   first.
4. **Implement BashLexer** (§5). Heavy unit tests on tokenization.
5. **Implement FILE / CWD verb tables and IsVerbLikeToken predicate** (§6) as static data + helper.
6. **Implement BashParser** (§4). One production at a time; unit-test
   each.
7. **Implement Resolver** (§8). Unit-test each resolution rule.
8. **Implement per-verb path-arg rules** (§7). Unit-test per verb.
9. **Implement cd-in-compound propagation** (§9). Unit-test.
10. **Implement subshell + bash -c recursion** (§10). Unit-test.
11. **Implement parser anomaly safe-fail** (§11). Unit-test.
12. **Author corpus** (§13) — start with 105 hand-authored entries
    covering each section. Iterate parser to make all pass.
13. **Sanitize and seed from real logs** (§14) — script + manual review.
    Add 50-100 more corpus entries.
14. **Wire CI** (§15). Tag v0.1.0-alpha when corpus is green and PII audit
    passes.

Estimated implementation effort: 600-800 LOC of source + 400-600 LOC of
test infrastructure + 100-150 corpus entries (~50 KB JSON).

Post-v0.1.0 increments (e.g. v0.1.5 newline-as-statement-separator) are
sequenced through `IMPLEMENTATION_PLAN.md` — §16 records the one-time
v0.1.0 build order, not the ongoing changelog.

---

## 17. Acceptance Criteria

v0.1.0-alpha ships when **all** of the following hold:

1. ✅ Public API matches §2 exactly. `dotnet pack` produces a
   ShellSyntaxTree.0.1.0-alpha.nupkg.
2. ✅ Every corpus entry in `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` parses to its
   expected AST. `dotnet test` runs them all and passes.
3. ✅ Corpus has at least 105 entries spanning the categories in §13.
4. ✅ PII audit scan over `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` finds zero hits.
5. ✅ `dotnet test` runs on PR via GitHub Actions and passes.
6. ✅ Tagging `v0.1.0-alpha` triggers `publish_nuget.yml` and the package
   appears on nuget.org.
7. ✅ Netclaw can consume the package via `<PackageReference>` and the
   `IShellParser` resolves at runtime in Netclaw's DI container.
8. ✅ At least one Netclaw integration test exercises a real corpus entry
   through the live Netclaw matcher and gets the expected gate decision.

---

## 18. Out of Scope (deferred from v0.1)

- PowerShell and cmd parsers.
- Variable expansion (any kind).
- Heredoc body extraction.
- Process substitution `<(cmd)`, `>(cmd)`.
- Function definitions.
- Arithmetic expansion `$((...))`.
- `for`/`while`/`case` control flow.
- Performance optimization beyond "fast enough" (~1ms typical).
- Source-mapping (line/column for AST nodes — useful for IDEs, irrelevant
  for security gates).
- Extensible verb table loading from config (v0.1 ships static tables;
  consumers can layer their own knowledge on top via `BashParserOptions`
  in a future version).

---

## Appendix A: Consumer Contract (Netclaw)

What Netclaw expects from this library:

1. `IShellParser` is registered in DI and `Parse(string)` returns a
   `ParsedCommand` that Netclaw walks.
2. For each `Clause`, Netclaw extracts:
   - `Verb.Tokens` for the verb-pattern gate evaluation.
   - All `Args` where `IsPath = true` (excluding `IsCwdAttribution = true`
     when the resolved path already appears in another arg) for the zone
     gate evaluation.
   - All `Redirects` where the target is path-shaped — the target is a
     path the clause "operates on" for zone-gate purposes.
3. When `IsUnparseable = true`, Netclaw routes to safe-fail (prompt user;
   offer Once / Deny only).
4. When any `Arg.Kind = DynamicSkip`, Netclaw treats that token as
   "path unknown" — falls back to prompting on the raw command for the
   zone gate.
5. Hard-deny rules in Netclaw evaluate against parsed `Clause` records,
   not raw text (except the `rawText` escape-hatch rules — those operate
   on the **rendered clause string**, recoverable via
   `Clause.ToCommandString()` if we add it, or `string.Join(" ", verb +
   args + redirects)` if we don't).

The contract is stable — additive changes to AST records (new fields with
default values) are compatible; renaming or removing fields is breaking.
Before v1.0.0, while the library is in its `0.x` line, a breaking AST change
MAY ship in a minor bump (e.g. the `Clause.IsCommandStringWrapped` →
`IsCommandStringWrapped` rename in v0.2.0) provided `RELEASE_NOTES.md`
documents the old→new mapping and the consumer (Netclaw) is updated in
lockstep. From v1.0.0 onward, renaming or removing a field requires a major
version bump.

---

## Appendix B: Why not tree-sitter-bash?

OpenCode (Node) uses tree-sitter-bash. We considered porting that approach
to .NET. The packaging cost is real:

- No first-class .NET tree-sitter binding. Community bindings exist but
  vary in maintenance.
- Native dependency: ship `libtree-sitter` + `libtree-sitter-bash` per
  platform (Linux x64, Linux arm64, macOS x64, macOS arm64, Windows x64).
  Five binaries to ship and maintain, plus PowerShell would need a
  separate native lib.
- AOT-trimming compatibility is uncertain.
- We don't need IDE-grade fidelity. Fork bombs and function definitions
  legitimately confuse our parser; we want them to mark `IsUnparseable`
  so the consumer routes to safe-fail. tree-sitter would parse them and
  we'd have to teach the consumer to ignore the result anyway.

The hand-rolled approach trades a higher ceiling for control over scope,
zero native deps, and a clean upgrade path to PowerShell via the same
`IShellParser` seam. For our use case, that trade is correct.
