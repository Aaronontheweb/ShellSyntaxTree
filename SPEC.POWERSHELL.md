# ShellSyntaxTree — PowerShell Specification (v0.2.0)

**Status:** Shipped in the v0.2.0 prerelease line; stable promotion pending
downstream Netclaw validation.
**Audience:** Whoever (human or agent) implements, consumes, or maintains the
ShellSyntaxTree PowerShell parser.
**Read `SPEC.md` (the bash and shared-contract specification) end-to-end
first — this document specifies only what differs for PowerShell.**

This document specifies the PowerShell parser shipping in ShellSyntaxTree
v0.2.0: its grammar, tokenization, cmdlet/verb tables, alias resolution,
resolver semantics, and corpus contract. PowerShell support reuses the
shared `IShellParser` interface and the shared AST (`ParsedCommand` /
`Clause` / `VerbChain` / `Arg` / `Redirect`) defined in `SPEC.md` §2–§3.

It is **not** a PowerShell interpreter. It does not execute, expand, or
evaluate commands. It returns the same structured AST a consumer already
walks for bash. The parsing scope is **Pipeline-aware** (§4): linear
command pipelines parse; control flow, definitions, and other script-level
constructs mark `IsUnparseable`.

`SPEC.md` is the canonical home of the shared public API, AST, sanitization
workflow, and consumer contract. Where this spec says "see `SPEC.md` §N" the
referenced section applies unchanged; only the PowerShell-specific delta is
written here.

**Reference dialect.** This spec targets **PowerShell 7.x** (7.4 LTS) as the
reference dialect — the cross-platform `pwsh` executable, not Windows
PowerShell 5.1. The `&&` / `||` pipeline-chain operators (§4, added in
PowerShell 7.0), the default alias set (§6.3), and the redirect stream
syntax (§5) are all PowerShell 7 semantics. The `pwsh` validation oracle
(§13) MUST run a 7.x build.

---

## 1. Goals & Non-Goals

### Goals (v0.2.0)

1. Parse PowerShell command pipelines into the shared `ParsedCommand` AST —
   per-clause verbs, args, parameters, redirects, compound operators —
   exactly the shape bash already produces.
2. Recognize PowerShell cmdlets (`Verb-Noun`), native commands, and built-in
   aliases; resolve aliases to their canonical cmdlet while preserving the
   verbatim typed token.
3. Extract paths with per-cmdlet and per-parameter knowledge (`-Path`,
   `-LiteralPath`, `-Destination`, positional rules).
4. Honor `Set-Location <dir>; cmd` propagation — subsequent clauses see
   `<dir>` as cwd, mirroring bash `cd` (`SPEC.md` §9).
5. Recurse into `pwsh -Command "<inner>"`, `pwsh -c`, and
   `pwsh -EncodedCommand <base64>` so inner command clauses surface to the
   consumer.
6. Mark dynamic-content tokens (`$var`, subexpressions, script blocks,
   splatting) with explicit `DynamicSkip` / `IsPath=false`.
7. Implement `PwshParser : IShellParser` alongside `BashParser` — the
   multi-shell seam from `SPEC.md` §1 is exercised for the first time.

### Non-Goals (v0.2.0)

- Parsing PowerShell *scripts* — control flow (`if`/`foreach`/`while`/
  `switch`), `function`/`filter`/`class`/`enum` definitions, `param()`/
  `begin`/`process`/`end` blocks, `trap`, `DATA` sections. These mark
  `IsUnparseable` (§11).
- Parsing `.ps1` script files. `pwsh -File script.ps1` parses as an ordinary
  clause; the file content is not read.
- Evaluating PowerShell expressions, the pipeline variable `$_` / `$PSItem`,
  or .NET method calls.
- Desired State Configuration (DSC).
- Windows `cmd` parsing (still deferred — see `SPEC.md` §18).
- Command execution and variable expansion — the same non-goals as bash
  (`SPEC.md` §1). The library marks dynamic tokens; it never resolves them.

---

## 2. Public API Surface

The shared interface, AST records, and enums are defined in **`SPEC.md` §2**
and are unchanged. PowerShell adds the following to namespace
`ShellSyntaxTree`; everything else is internal.

```csharp
namespace ShellSyntaxTree;

/// <summary>
/// Shell-neutral configuration shared by every IShellParser implementation.
/// Carries the resolver knobs used to expand and normalize path tokens.
/// </summary>
public abstract record ShellParserOptions
{
    /// <summary>User home directory for ~ / $HOME / $env:USERPROFILE
    /// expansion. Defaults to Environment.SpecialFolder.UserProfile.</summary>
    public string? HomeDirectory { get; init; }

    /// <summary>Working directory for relative-path resolution. Defaults to
    /// the daemon-process cwd.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>Bash configuration. The v0.1 properties move to the base record;
/// the shape stays source-compatible — `new BashParserOptions { HomeDirectory
/// = ... }` still compiles.</summary>
public sealed record BashParserOptions : ShellParserOptions;

/// <summary>Configuration knobs for PwshParser. Empty in v0.2.0 — alias
/// resolution is unconditional (§6.3) and the resolver knobs live on the
/// shared ShellParserOptions base. Kept as a distinct type so a future
/// PowerShell-only knob is an additive change, not a new type.</summary>
public sealed record PwshParserOptions : ShellParserOptions;

/// <summary>PowerShell implementation of IShellParser.</summary>
public sealed class PwshParser : IShellParser
{
    public PwshParser();
    public PwshParser(PwshParserOptions options);
    public ParsedCommand Parse(string command);
}
```

Two shared types gain a change (see §3):

- `VerbChain` gains an additive `string? CanonicalVerb` field.
- `VerbChain` gains an additive `bool IsDynamic` field.
- `Clause.IsBashCWrapped` is renamed `Clause.IsCommandStringWrapped`.

**Versioning.** `PwshParser`, `PwshParserOptions`, `ShellParserOptions`,
`VerbChain.CanonicalVerb`, and `VerbChain.IsDynamic` are additive. The
`Clause` field rename and the `BashParserOptions` reparenting are
**breaking**; `SPEC.md` Appendix A permits a breaking AST change on a `0.x`
minor bump when `RELEASE_NOTES.md` carries the old→new mapping and Netclaw is
updated in lockstep (§15). `PublicApiSnapshotTests` is updated in the same
change. `PwshParser.Parse` throws `ArgumentNullException` on null input and
never throws on a well-formed string, exactly like `BashParser`.

---

## 3. AST Reference

The AST records and enums are defined in **`SPEC.md` §3** and are emitted
unchanged by `PwshParser` — a consumer walks a PowerShell `ParsedCommand`
exactly as it walks a bash one. Two deltas:

### `VerbChain.CanonicalVerb` (new, additive)

```csharp
/// <summary>
/// The canonical, alias-resolved verb identity, set when the parser resolved
/// the first token of Tokens from a shell built-in alias — e.g. `ls` / `gci`
/// / `dir` resolve to `Get-ChildItem`; `rm` / `del` resolve to
/// `Remove-Item`. Tokens always keeps the verbatim token the user typed;
/// this field carries the resolved name so a consumer can gate on canonical
/// identity without re-implementing the alias table.
///
/// Null when no alias resolution applied: every bash clause, and every
/// PowerShell clause whose verb is already a canonical cmdlet or an unknown
/// command. Consumers SHOULD use `CanonicalVerb ?? Tokens[0]` as the gate key.
/// </summary>
public string? CanonicalVerb { get; init; }
```

`CanonicalVerb` is non-null **only when an alias was rewritten**. A user who
types the canonical cmdlet (`Get-ChildItem`) leaves it null — a non-null
value unambiguously signals "an alias was expanded."

### `VerbChain.IsDynamic` (new, additive)

```csharp
/// <summary>
/// True when the clause's command name is a dynamic token the parser
/// cannot statically identify — a variable (`& $exe`), a subexpression
/// (`& (Get-Thing)`), or a script block (`& { ... }`) at verb position.
/// Tokens still carries the verbatim token; CanonicalVerb is null.
///
/// A consumer MUST treat a clause with IsDynamic=true as "the command being
/// run is unknown" and route to safe-fail — the verb identity, and
/// therefore every verb-keyed gate rule, is unresolvable. Always false for
/// bash clauses and for PowerShell clauses with a literal command name.
/// </summary>
public bool IsDynamic { get; init; }
```

`IsDynamic` exists because PowerShell's call operator (`&`) makes invoking a
dynamically-named command a first-class, common idiom. A clause whose verb is
`$exe` is otherwise indistinguishable from one whose verb is a literal — and
"we do not know what is being executed" is the most security-relevant state
the AST can carry, so it gets a field rather than being silently flattened
into `Tokens`. The clause's args and redirects still parse normally so a
consumer sees the rest of the shape.

### `Clause.IsBashCWrapped` → `Clause.IsCommandStringWrapped`

The v0.1 field `IsBashCWrapped` is renamed `IsCommandStringWrapped`. The
meaning is unchanged and now shell-neutral: *true when this clause is the
result of recursing into a command-string wrapper* — bash `bash -c "..."` /
`sh -c "..."`, or PowerShell `pwsh -Command "..."` / `pwsh -c "..."` /
`pwsh -EncodedCommand ...` (§10).

---

## 4. Grammar

Approximate BNF for what the PowerShell parser accepts. Anything outside this
grammar marks `ParsedCommand.IsUnparseable = true` (§11). PowerShell is
**case-insensitive** — keywords, cmdlet names, aliases, parameter names, and
drive qualifiers are all matched case-insensitively.

```
command          := statement (statement_sep statement)*
statement_sep    := ";" | "&&" | "||" | NEWLINE
statement        := pipeline
pipeline         := pipeline_element ("|" pipeline_element)*
pipeline_element := call_op? command_name arg* redirect*
call_op          := "&"                        // call operator at verb position
command_name     := cmdlet | native_word | quoted_string | grouped_pipeline
arg              := parameter | value
parameter        := "-" param_name (":" value)?     // -Name value | -Name:value
                  | "-" param_name                   // switch parameter
                  | "--"                              // end-of-parameters marker
value            := word | quoted_string | here_string
                  | script_block         // { ... }   -> DynamicSkip Arg
                  | subexpression        // $( ... )  -> DynamicSkip Arg
                  | array_expression     // @( ... )  -> DynamicSkip Arg
                  | hash_literal         // @{ ... }  -> DynamicSkip Arg
                  | splat                // @var      -> DynamicSkip Arg
redirect         := redirect_op target
redirect_op      := ">" | ">>" | "<"
                  | STREAM ">" | STREAM ">>"          // STREAM in {1..6, *}
                  | STREAM ">&" STREAM                 // stream merge (2>&1)
target           := word | quoted_string | "$null"
grouped_pipeline := "(" pipeline ")"                 // parenthesized sub-pipeline
word             := run of non-whitespace, non-operator, non-quote chars,
                    honoring backtick escape; absorbs $var / ${name} /
                    $env:NAME / drive-qualified path prefixes
quoted_string    := single_quoted | double_quoted
```

**Notes:**

- Whitespace between tokens is one or more spaces or tabs.
- A bare newline outside quotes, here-strings, `{ }`, `$( )`, `@( )`,
  `@{ }`, grouping `( )`, and line continuations is a **statement
  separator** equivalent to `;` → `CompoundOperator.Sequence`. Consecutive,
  leading, and trailing newlines collapse; a newline immediately after `|`,
  `&&`, or `||` collapses (a pipeline may continue on the next line).
- Backtick `` ` `` followed by a newline is a line continuation (treated as
  whitespace) — the PowerShell analog of bash `\` + newline.
- `&` at verb position is the **call operator** (`& git status`, `& $exe`,
  `& { ... }`). A *trailing* `&` (a PowerShell background job) marks
  `IsUnparseable` (§11).
- A clause whose command name (after an optional `&`) is a dynamic token —
  a variable `$var`, a subexpression `$( ... )`, or a script block
  `{ ... }` — still parses, but its `VerbChain.IsDynamic` is set true (§3).
  Its args and redirects parse normally.
- A parenthesized **pipeline** `( ... )` parses as a grouped sub-pipeline;
  its clauses carry `IsSubshell = true` as a *structural* marker only.
  Unlike a bash subshell, PowerShell's `( ... )` is a grouping operator — it
  creates **no scope and no working-directory boundary** (`$PWD` is runspace
  state, not a scoped variable). `Set-Location` attribution therefore
  **propagates through** `( ... )` rather than being isolated by it (§9). A
  group containing control flow marks `IsUnparseable`.
- `--%` is the stop-parsing token: the remainder of the **line** — to the
  next newline or end of input, including any `|`, `;`, `&&`, or `||`, which
  become literal text rather than operators — becomes one opaque
  `DynamicSkip` arg. `--%` does **not** stop at a pipeline-element boundary;
  treating `| cmd` after `--%` as a new clause would invent a clause that
  does not exist.
- Script blocks `{ ... }`, subexpressions `$( ... )`, array `@( ... )`, and
  hash `@{ ... }` literals are recognized as **opaque tokens** — the
  interior is not parsed; each becomes one `DynamicSkip` arg.
- Control-flow keywords, definition keywords, block keywords, `param()`,
  assignment statements, bare `[type]::member` calls, and bare arithmetic at
  statement position fall outside the grammar → `IsUnparseable` (§11).

---

## 5. Tokenization Rules

The `PwshLexer` produces tokens consumed by `PwshCommandParser`. Token kinds
(`PwshTokenKind`):

- **Word** — a bare token: command name, native arg, path, number, `$var`,
  `${name}`, `$env:PATH`, drive-qualified `C:\x`. Backtick escapes are
  processed; simple `$x` / `${x}` is absorbed into the Word.
- **Parameter** — a `-Name` parameter token. A `-Name:value` colon form
  keeps the value; the parser splits on the first `:` for cmdlet-style
  commands. Parameter names may contain internal hyphens and `?`, so
  `-Name-Part`, `-?`, and native `--work-tree` each remain one token. An
  unquoted native
  `--flag=value` likewise remains one source token; the native-command
  parser splits it into flag and value args using the bash rules. `=` is
  not cmdlet parameter binding — `-Name=value` stays one parameter token
  for a cmdlet.
- **QuotedString** — single-quoted, double-quoted, or here-string.
  Delimiters stripped from the value. Carries `IsSingleQuoted` and
  `IsHereString` flags.
- **Operator** — `;`, `&&`, `||`, `|`, `&`, `(`, `)`, and the redirect
  operators.
- **Whitespace** — spaces/tabs, or a newline run. A newline-bearing run
  carries `IsStatementSeparator = true` (the mechanism added in v0.1.5;
  `SPEC.md` §5).
- **Continuation** — backtick + newline. Treated as whitespace.
- **Comment** — `#` line comment to end-of-line, or `<# ... #>` block
  comment. Dropped by the significant-token filter.
- **ScriptBlock** — a balanced `{ ... }` region, emitted whole. Parser →
  `DynamicSkip` arg.
- **Subexpression** — a balanced `$( ... )`, `@( ... )`, or `@{ ... }`
  region, emitted whole. Parser → `DynamicSkip` arg.
- **Splat** — `@identifier` (splatting). Parser → `DynamicSkip` arg.
- **StopParsing** — the `--%` token; the pipeline-element remainder is
  opaque.
- **UnparseableSentinel** — an unbalanced region or a lex-time-detected
  unsupported construct. Carries the reason; the parser lifts it to
  `ParsedCommand.IsUnparseable` (§11).

### Quote handling

- **Single quotes** `'...'` preserve bytes literally — no escape processing,
  no expansion. A doubled `''` inside is an escaped single quote.
- **Double quotes** `"..."` allow backtick escape sequences and recognize
  `$var` / `${name}` / `$env:X` / `$( ... )` interpolation — but the parser
  **does not expand**; `$var` stays literal in the token value and the
  resolver (§8) classifies it. A `$( ... )` inside a double-quoted string
  does not split the token.
- **Here-strings** — `@"` + newline ... newline + `"@` (expandable) and
  `@'` + newline ... newline + `'@` (literal). The closing delimiter must
  start a line. Lexes to one `QuotedString` token with `IsHereString=true`.
- Unbalanced quotes or here-strings → `IsUnparseable` with a reason.

### Escape handling

- The backtick `` ` `` is PowerShell's escape character — **not** a
  command-substitution delimiter. There is no backtick command substitution
  in PowerShell. Outside quotes, `` `X `` takes `X` literally into the
  current Word. Inside double quotes, `` `n ``, `` `t ``, `` `" ``, `` `$ ``,
  and an escaped backtick are recognized.
- Backtick + newline is a line continuation.

### Operator boundaries

Operators terminate the current token without surrounding whitespace —
`gci|rm` lexes as `[gci, |, rm]`, exactly like bash (`SPEC.md` §5).

### Comment handling

- `#` at a token boundary starts a line comment to (but not including) the
  next newline — the same boundary rules as bash (`SPEC.md` §5 "Comment
  handling"). `#` in the interior of an unquoted word (`abc#def`) is a
  literal character.
- `<#` ... `#>` is a block comment. An unterminated block comment →
  `UnparseableSentinel`.
- Comment tokens are dropped by the significant-token filter. Comment-only
  input parses to `Clauses = []`, `IsUnparseable = false`.

### Redirect tokenization

Recognized redirect operators, longest-match first: `>`, `>>`, `<`; `N>` and
`N>>` for stream `N` in `{1,2,3,4,5,6}`; `*>` and `*>>` (all streams); and
the stream-merge form `N>&N` (e.g. `2>&1`). A redirect target of `$null` is
recognized as the discard sink. §8 covers how stream numbers map onto the
`RedirectDirection` enum.

---

## 6. Verb / Cmdlet Tables

PowerShell command names come in two shapes; the parser recognizes both.

### 6.1 Cmdlet recognition

A token is **cmdlet-shaped** when, case-insensitively: `Kind == Word`;
length in `[3, 64]`; it contains exactly one `-`; the segment before `-` is
an **approved PowerShell verb** (the closed set returned by `Get-Verb` —
`Get`, `Set`, `New`, `Remove`, `Add`, `Clear`, `Invoke`, `Test`, `Start`,
`Stop`, `Import`, `Export`, `Select`, `Out`, etc., held in the static
`PwshApprovedVerbs` table); and the segment after `-` begins with an ASCII
letter and is ASCII letters/digits only. `Get-ChildItem`, `get-childitem`,
and `GET-CHILDITEM` all match.

Gating on the approved-verb table — rather than "any letters before the
dash" — is deliberate. Hyphenated **native** commands (`docker-compose`,
`apt-get`, `git-lfs`, `dotnet-counters`) are not cmdlets, and their first
segment (`docker`, `apt`, `git`, `dotnet`) is not an approved verb; they
therefore fall through to the native-command path (§6.2) and keep their
multi-token subcommand chains. A pure-shape rule would misclassify them as
one-token cmdlets, truncating the chain and dropping their args from
FileVerb path classification. A hyphenated native tool whose prefix happens
to be an approved verb is a rare, accepted false positive.

A cmdlet-shaped first token (or one resolved through the alias table, §6.3)
makes the verb chain exactly **one token**. PowerShell cmdlets take explicit
parameters; there is no `git push origin` style nested-subcommand idiom for
cmdlets, so the bash greedy walk does not apply to them.

### 6.2 Native-command verb chains

When the first token is neither cmdlet-shaped nor a known alias (`git`,
`dotnet`, `npm`, `kubectl`, `python`, ...) it is a **native command**.
Native commands reuse the bash greedy verb-chain walk (`SPEC.md` §6.1).
The parser appends the first token and then walks consecutive verb-like Word
tokens. The walk transparently consumes flag-with-value pairs. It stops at a
path-shaped token, non-verb-like token, flag, operator, quoted string, or
opaque token.

The path-shape test uses `BashResolver.LooksLikePath`. Both native parsers
therefore share one boundary. The verb-like predicate is the bash predicate
**unchanged** (`SPEC.md` §6.1:
`Kind == Word`, length `[1, 64]`, first char ASCII lowercase `[a-z]`,
remaining chars `[a-z0-9._-]`).

The path-shape boundary does not apply to the first native command token.
For example, `deploy.sh status` has verb tokens `deploy.sh` and `status`.

Keeping the predicate **case-sensitive** — not relaxing it to accept an
uppercase first char — is deliberate. The leading-lowercase rule is the only
signal that stops the greedy walk at a capitalized identifier
(`dotnet ef migrations add InitialCreate` stops at `InitialCreate`); relaxing
it would absorb `InitialCreate` into the verb chain and make the PowerShell
verb chain *diverge* from the bash parser's for the identical command. Real
native subcommands are lowercase in the wild (`dotnet ef migrations add`), so
case-sensitivity costs nothing. Cmdlet names, aliases, and parameter names
are still matched case-insensitively against their tables (§4); only the
native greedy-walk predicate stays case-sensitive. `Clause.Verb` remains a
convenience hint, not a security contract (`SPEC.md` §6.1.1); consumers
pattern-prefix match.

### 6.3 Built-in alias table

`PwshAliases` is a static, case-insensitive map from a typed alias to its
canonical cmdlet. When the first token is a known alias the parser:

- keeps the verbatim typed token in `VerbChain.Tokens` (source fidelity;
  pattern-matching sees what was typed), and
- sets `VerbChain.CanonicalVerb` to the canonical cmdlet, which drives the
  per-cmdlet path rules (§7) and the Cwd/File verb classification (§6.4).

Alias resolution is **unconditional** — it is the single most
security-relevant normalization the PowerShell parser performs, and the v0.1
doctrine ("consumers can relax, they can't un-execute") means it is not a
knob. `VerbChain.Tokens` already preserves the verbatim token, so resolution
costs no source fidelity; a switch to disable it would only weaken
alias-keyed gate rules.

`PwshAliases` MUST contain the **complete default alias set** of the
reference PowerShell 7.x build — not a hand-picked subset. An alias absent
from the table degrades to a native command (§6.2); a file cmdlet so
degraded silently loses its per-verb path classification (§7) — a
false-negative-shaped failure in a security parser. The full set is finite
and enumerable (`Get-Alias`), so a `[Fact]` (the §13 `pwsh` oracle already
spawns `pwsh`) diffs `PwshAliases` against live `Get-Alias` output and fails
on any gap. The table below is the **security-relevant excerpt** (file,
cwd, and code-execution verbs), not the whole table:

| Alias(es) | Canonical cmdlet |
|---|---|
| `rm`, `del`, `erase`, `rd`, `rmdir`, `ri` | `Remove-Item` |
| `ls`, `dir`, `gci` | `Get-ChildItem` |
| `cd`, `chdir`, `sl` | `Set-Location` |
| `pushd` / `popd` | `Push-Location` / `Pop-Location` |
| `cat`, `gc`, `type` | `Get-Content` |
| `cp`, `copy`, `cpi` | `Copy-Item` |
| `mv`, `move`, `mi` | `Move-Item` |
| `ni`, `mkdir`, `md` | `New-Item` |
| `ren`, `rni` | `Rename-Item` |
| `ac` | `Add-Content` |
| `sls` | `Select-String` |
| `ipcsv` / `epcsv` | `Import-Csv` / `Export-Csv` |
| `gp` / `sp` / `rp` | `Get-ItemProperty` / `Set-ItemProperty` / `Remove-ItemProperty` |
| `clc` / `cli` | `Clear-Content` / `Clear-Item` |
| `rvpa` | `Resolve-Path` |
| `gi` / `ii` | `Get-Item` / `Invoke-Item` |
| `%` / `?` | `ForEach-Object` / `Where-Object` |
| `iex` | `Invoke-Expression` |
| `echo`, `write` | `Write-Output` |
| `pwd`, `gl` | `Get-Location` |
| `select` / `sort` / `measure` / `group` | `Select-Object` / `Sort-Object` / `Measure-Object` / `Group-Object` |
| `kill` / `ps` / `sleep` | `Stop-Process` / `Get-Process` / `Start-Sleep` |

**Keyword / alias collisions.** Resolution order:

1. A token at statement position that exactly equals a control-flow keyword
   **and** is immediately followed by `(` is the keyword → `IsUnparseable`.
   (`foreach ($x in $y)` is a loop; in `gci | foreach { ... }` the `foreach`
   follows `|` and precedes `{`, so it is the `ForEach-Object` alias.)
2. Otherwise the alias table wins for known aliases.
3. `curl`, `wget`, `sc`, `set`, `start`, and `where` are treated as **native
   commands**, never aliased — their cmdlet vs. native-tool meaning is
   version-dependent and aliasing would mis-apply cmdlet semantics.

### 6.4 Cwd / File / control-flow tables

`PwshVerbs` mirrors `BashVerbs`, keyed by canonical cmdlet plus raw aliases
(case-insensitive):

- **CwdVerbs** — `Set-Location`, `Push-Location`, `Pop-Location`, plus raw
  `cd`, `chdir`, `sl`, `pushd`, `popd`. (`SPEC.md` §6.2 already pre-lists
  `set-location` / `push-location`.)
- **FileVerbs** — the file cmdlets: `Get-ChildItem`, `Get-Content`,
  `Set-Content`, `Add-Content`, `Remove-Item`, `Copy-Item`, `Move-Item`,
  `Rename-Item`, `New-Item`, `Get-Item`, `Invoke-Item`, `Test-Path`,
  `Resolve-Path`, `Out-File`, `Import-Csv`, `Export-Csv`, `Get-FileHash`,
  `Compress-Archive`, `Expand-Archive`, `Select-String` — plus the Windows
  native file utilities reserved in `SPEC.md` §6.4 (`type`, `copy`, `move`,
  `del`, `xcopy`, `robocopy`, `findstr`).
- **ControlFlowKeywords** — `if`, `elseif`, `else`, `switch`, `foreach`,
  `for`, `while`, `do`, `until`, `function`, `filter`, `workflow`,
  `configuration`, `class`, `enum`, `param`, `begin`, `process`, `end`,
  `dynamicparam`, `trap`, `data`, `try`, `catch`, `finally`, `return`,
  `throw`, `break`, `continue`, `exit`, `using`, `hidden`.

### 6.5 Parameter Binding Model

PowerShell interleaves switch parameters, value-bearing parameters, and
positional values in a clause's arg list. Whether a `-Name` token consumes
the next space-separated token as its value depends, in real PowerShell, on
the cmdlet's *compiled parameter metadata* (`[switch]` vs. a typed
parameter). The parser has no metadata; it MUST decide binding from static
tables. **§7's path classification, §9's `Set-Location` target rule, and
§10's `pwsh -Command` detection all depend on this model — it is not
optional.** Without it the parser cannot even compute token boundaries:
`Get-ChildItem -Recurse C:\logs` is "switch `-Recurse`, positional path
`C:\logs`", but `Get-ChildItem -Depth 3 C:\logs` is "value-binding `-Depth`
with value `3`, positional path `C:\logs`" — and only a table tells them
apart.

#### 6.5.1 Token roles and positional index

After the verb chain, each token has exactly one role:

- **Switch** — a `-Name` parameter consuming no following token.
- **Value-binding parameter** — a `-Name` parameter consuming exactly the
  next significant token as its value.
- **Positional value** — any non-parameter token.

A positional value's **positional index** is its running count among
positional values *only*. Switch tokens, value-binding-parameter tokens, and
the values those parameters consume do **not** advance the index. So in
`Copy-Item -Force a -Verbose b`, `a` is positional 0 and `b` is positional 1.

The colon form `-Name:value` (§5) always binds — `-Name` is value-binding,
`value` its value — regardless of the tables below.

#### 6.5.2 The binding tables

Two case-insensitive static tables drive the decision, keyed by
`(canonicalVerb, parameterName)` with a verb-agnostic fallback — the same
keying §7.1 uses for the path-parameter table:

- **`PwshValueParameters`** — parameters that consume the next token.
  Seeded with: the value-bearing **common parameters** (`-ErrorAction`,
  `-WarningAction`, `-InformationAction`, `-ProgressAction`,
  `-ErrorVariable`, `-WarningVariable`, `-InformationVariable`,
  `-OutVariable`, `-OutBuffer`, `-PipelineVariable`); every parameter in the
  §7.1 path-parameter table (`-Path`, `-LiteralPath`, `-PSPath`,
  `-FilePath`, `-OutFile`, `-InFile`, `-Destination`, `-Source`, `-Filter`,
  `-Include`, `-Exclude`, `-Value`, `-ItemType`); and `-Name`, `-Encoding`,
  `-Depth`, `-Stream`, `-Delimiter`, `-Command`, `-EncodedCommand`, `-File`,
  `-ArgumentList`.
- **`PwshSwitchParameters`** — parameters known to consume nothing: the
  switch **common parameters** (`-Verbose`, `-Debug`, `-WhatIf`,
  `-Confirm`) plus frequent cmdlet switches (`-Recurse`, `-Force`,
  `-Append`, `-NoNewline`, `-PassThru`, `-Wait`, `-Quiet`,
  `-CaseSensitive`, `-SimpleMatch`, `-NoClobber`, `-AsByteStream`,
  `-Hidden`, `-Directory`).

PowerShell parameter-name **prefix matching** applies: a `-Name` token
matches a table entry when it case-insensitively prefixes exactly one entry
(`-Rec` → `-Recurse`). A prefix matching two or more entries is **ambiguous**
and treated as unknown.

#### 6.5.3 The binding decision

For each `-Name` token, in order:

1. Colon form `-Name:value` → value-binding; value is the colon tail. When
   the name half carries an `=` the colon tail is **`DynamicSkip`** instead:
   PowerShell reads `-Path=C:\Windows` as parameter `-Path=C:` plus argument
   `\Windows`, a name no cmdlet can bind, so the value's role is unknowable
   and the parser must not classify it.
2. `-Name` (prefix-)matches `PwshValueParameters` → value-binding; consume
   the next significant token as its value. If there is no next token, or
   the next token is itself a parameter or an operator, `-Name` bound
   nothing and is recorded as a switch.
3. `-Name` (prefix-)matches `PwshSwitchParameters` → switch.
4. **Unknown `-Name`** → **switch** (consume nothing).

Rule 4's default — unknown means switch — is the security-conservative
choice. If an unknown `-Name` is *actually* value-binding, treating it as a
switch leaves its value as a positional, where §7's positional rules can
still catch a real path (`IsPath=true`). The opposite default (assume
value-binding) would *consume* that token and hide a real path from the
gate — and a missed path is the unrecoverable failure (`SPEC.md` §1).
Over-classifying a stray literal as a positional path is at worst a
recoverable extra prompt. This is why §7's positional rules are the floor,
not the parameter layer.

#### 6.5.4 The `-File` collision

`-File` is a `Get-ChildItem` switch but `pwsh -File` is value-binding.
Because the tables are keyed by `(canonicalVerb, parameterName)`, `-File`
resolves to value-binding when the canonical verb is `pwsh` / `powershell`
(§10) and to a switch otherwise; the verb-agnostic entries are the fallback
only when no `(verb, name)` row exists.

---

## 7. Per-Verb / Per-Parameter Path-Arg Extraction Rules

PowerShell classifies path arguments in two layers — a **parameter-value**
layer (dominant, because PowerShell names paths explicitly) and a
**positional** layer. Per-cmdlet rules are keyed by the *canonical* verb
(§6.3), so `rm x` and `Remove-Item x` classify identically.

Both layers operate on the token roles the §6.5 binding model has already
assigned: §6.5 decides parameter *consumption* and *positional index*; §7
decides path-*ness*. A §7 rule never re-derives whether a `-Name` consumed
the following token — it trusts §6.5.

### 7.1 Path-typed parameters

A parameter value is a path when the parameter name (case-insensitive) is in
the path-parameter set. The unambiguous, verb-agnostic names:

| Parameter | Value classification |
|---|---|
| `-Path`, `-LiteralPath`, `-PSPath` | path |
| `-FilePath`, `-OutFile`, `-InFile` | path |
| `-Destination`, `-Source` | path |
| `-Filter`, `-Include`, `-Exclude` | glob — `Kind=Glob` if metachars present, else `Literal`; `IsPath=false` |
| `-Value` (for `Set-Content` / `Add-Content`) | not a path (file content) |
| `-ItemType` (for `New-Item`) | not a path (literal `File` / `Directory`) |

`-Name` is context-dependent (a filesystem leaf for `New-Item` /
`Rename-Item`; not a path for `Get-Process` / `Get-Service`). Key the
path-parameter table by `(canonicalVerb, parameterName)` with the
verb-agnostic set above as the fallback — the PowerShell analog of bash's
`FlagValueIsPath` table (`SPEC.md` §7).

### 7.2 Positional path rules

Most file cmdlets take `-Path` as positional 0; with no preceding
path-parameter, positional 0 (and beyond — positional index per §6.5.1)
classifies as a path for canonical FileVerbs. Per-cmdlet overrides:

| Canonical cmdlet | Positional rule |
|---|---|
| `Get-ChildItem`, `Get-Content`, `Get-Item`, `Remove-Item`, `Set-Location`, `Push-Location`, `Invoke-Item`, `Test-Path`, `Out-File`, `Import-Csv` | all non-flag positionals are paths |
| `Copy-Item`, `Move-Item` | positional 0 = source path, positional 1 = destination path |
| `Rename-Item` | positional 0 = path, positional 1 = new name (path fragment) |
| `New-Item` | positional 0 = path |
| `Set-Content`, `Add-Content` | positional 0 = path; `-Value` is content |
| `Select-String` | positional 0 = **pattern**, rest = paths (mirrors bash `grep`) |
| `ForEach-Object`, `Where-Object` | positional 0 is typically a script block → `DynamicSkip`; no path positionals |

The default for a canonical FileVerb with no override is "all non-flag
positionals are paths," exactly as `SPEC.md` §7.

### 7.3 Native commands

Native commands reuse the bash per-verb rules table verbatim — `git`,
`curl`, `tar`, etc. behave identically to `SPEC.md` §7 (`curl` / `wget`:
the first positional is a URL; the `-o` / `-O` value is a path). This
includes hyphenated option names and the bash `--flag=value` split: the
flag and value surface as separate args, and a curated flag's value receives
the same path classification in both parsers. Native `--flag:value` has no
cmdlet-binding semantics and remains verbatim.

---

## 8. Resolver

The resolution doctrine — single-quote bypass, `DynamicSkip` for anything not
statically knowable, the `(ArgKind, Resolved, IsPath)` result — is defined in
**`SPEC.md` §8** and is unchanged. `PwshResolver` parallels `BashResolver`
with the PowerShell-specific steps below. Resolution order:

0. **Single-quote / literal here-string bypass.** A token from a
   single-quoted string or an `@'...'@` here-string is literal bytes — skip
   steps 1–7. (`SPEC.md` §8 step 0.)
1. **`~` expansion.** `~`, `~\path`, `~/path` → `HomeDirectory`. `~user` is
   not supported → `DynamicSkip`.
2. **Home-variable substitution.** `$HOME`, `${HOME}`, `$env:USERPROFILE`,
   `${env:USERPROFILE}` expand to `HomeDirectory`. `$PSScriptRoot` →
   `DynamicSkip` (the script's own directory is not knowable at gate time).
   Every other `$var` / `$env:NAME` / `${name}` reference → `DynamicSkip` in
   a path slot, `ArgKind.EnvVar` in a non-path slot. (Mirrors bash: only the
   home variables are privileged.)
3. **Provider-qualifier stripping.** Strip a leading `FileSystem::` or
   `Microsoft.PowerShell.Core\FileSystem::` prefix (case-insensitive) and
   resolve the remainder. This generalizes the lowercase `filesystem::`
   strip the bash resolver already performs (`SPEC.md` §8).
4. **Drive-qualified paths.** A single-ASCII-letter drive (`C:\`, `d:/foo`)
   is a rooted filesystem path; normalize `\` to `/` for output consistency
   with bash. A **non-FileSystem PSDrive** — `HKLM:`, `HKCU:`, `Env:`,
   `Cert:`, `Variable:`, `Function:`, `Alias:`, or any qualifier longer than
   one letter — is **not a filesystem path**: classify `Kind=Literal`,
   `IsPath=false`. A registry or certificate "path" must not be treated as a
   file by a zone gate.
5. **UNC paths.** `\\server\share\...` is a rooted path, normalized to
   `//server/share/...` (the bash resolver already performs this collapse).
6. **Glob detection.** Wildcard metacharacters `*`, `?`, and `[ ]` →
   `ArgKind.Glob` in a path slot (`IsPath=true`), `IsPath=false` otherwise.
   The parser does not expand globs (`SPEC.md` §8).
7. **Relative-path resolution.** A token with no drive qualifier, no leading
   `\\`, and no leading `/` or `\` is joined to
   `PwshParserOptions.WorkingDirectory`. A dynamic `Set-Location` target
   makes subsequent relative paths `DynamicSkip` (the working-directory-
   unknown mechanism, `SPEC.md` §9).

A redirect target of `$null` sets `Redirect.IsDynamicSkip = true` — it is
the discard sink, not a file; do not resolve it. The `LooksLikePath`
heuristic (`SPEC.md` §8) additionally recognizes a leading `FileSystem::` /
`Microsoft.PowerShell.Core\FileSystem::` qualifier.

**Comma-separated arrays.** PowerShell's `,` is the array operator —
`-Path a.txt,b.txt` binds an array of two paths. The lexer keeps a
comma-joined run as one Word (`,` is not an operator, §5). In a path slot, a
token with an unquoted top-level `,` is marked `Kind=DynamicSkip,
IsPath=false`: the v0.2.0 parser neither splits it into per-element path args
nor resolves the mangled join (a single bogus joined path would mislead a
zone gate). Safe-fail — the consumer prompts on the raw command — is the
correct v0.2.0 behavior; per-element path extraction is a candidate v0.2.x
item (§18).

**Environment-variable prompt rate.** PowerShell commands reference
path-bearing variables (`$env:TEMP`, `$env:APPDATA`, `$env:windir`,
`$env:ProgramFiles`) far more often than bash commands reference `$VAR`
paths. With only the home variables privileged (step 2), a materially higher
share of PowerShell path args resolves to `DynamicSkip` than in the bash
parser. That is the intended safe failure mode — the consumer prompts — but
implementers and consumers should expect a higher prompt rate. Privileging
more `$env:` names is a deliberate non-goal: the value at parse time need not
match the value when the command runs.

### Redirect stream → `RedirectDirection` mapping

`RedirectDirection` (`SPEC.md` §3) has five members; PowerShell has more
output streams than that. The mapping is intentionally **lossy** — a zone
gate cares about the redirect *target path*, which is preserved exactly, not
about which stream produced it:

| PowerShell redirect | `RedirectDirection` |
|---|---|
| `<` | `In` |
| `>`, `1>` | `Out` |
| `>>`, `1>>` | `Append` |
| `2>` | `ErrOut` |
| `2>>` | `ErrAppend` |
| `3>`–`6>`, `*>` | `Out` (lossy — warning/verbose/debug/information/all) |
| `3>>`–`6>>`, `*>>` | `Append` (lossy) |
| stream merge `N>&M` (`2>&1`, `3>&1`, ...) | `ErrOut` when `N` is `2`, else `Out`; `Target` carries `&M` verbatim with `IsDynamicSkip=true` |

Lossless stream identity is deferred (§18); v0.2.0 consumers gate on the
target path, not the originating stream.

---

## 9. Set-Location-in-Compound Propagation

PowerShell honors the same cwd-attribution propagation bash applies to `cd`
(`SPEC.md` §9): `Set-Location <dir>; cmd` runs `cmd` with cwd `<dir>`.

**Rules** (parallel to `SPEC.md` §9):

1. A clause whose canonical verb is `Set-Location` (raw aliases `cd`,
   `chdir`, `sl`) sets the **attributed cwd** for subsequent clauses in the
   same compound. The cwd target is the value of `-Path` / `-LiteralPath`
   when present (per the §6.5 binding model), else positional 0 (positional
   index per §6.5.1). `Set-Location` with no positional and no `-Path`
   targets `HomeDirectory`.
2. Subsequent clauses receive a synthetic `Arg` with `IsCwdAttribution=true`.
   Its kind tracks the `Set-Location` target:
   - target resolves to a filesystem path — a literal path, `~`, `$HOME`,
     `$env:USERPROFILE`, a drive-qualified or UNC path, or the
     no-positional home case → `Kind=Literal, IsPath=true`, `Resolved` set;
     subsequent relative paths in the compound resolve against it.
   - target is dynamic (`cd $repo`), a non-FileSystem PSDrive (`cd HKLM:`,
     `cd Env:` — §8 step 4), or `Set-Location -` / `Set-Location +`
     (previous/next location, not statically knowable) → `Kind=DynamicSkip,
     IsPath=false`, `Resolved=null`; subsequent relative paths in the
     compound also become `Kind=DynamicSkip, IsPath=false` — the working
     directory is no longer statically known (the bash dynamic-cd
     mechanism, `SPEC.md` §9).
3. A later `Set-Location` replaces the attributed cwd.
4. Sub-pipeline `( ... )` boundaries do **not** isolate attribution.
   PowerShell `( ... )` is a grouping operator, not a subshell — it creates
   no working-directory scope (`$PWD` is runspace state, not a scoped
   variable; §4). A `Set-Location` inside `( ... )` changes the cwd for
   everything after the group, and attribution propagates across the group
   boundary. This is the one place PowerShell `Set-Location` propagation
   deliberately diverges from bash `cd`, where a subshell *does* isolate
   (`SPEC.md` §9 rule 4 / §10). Importing the bash isolation here would
   under-attribute — `(cd C:\sensitive); Remove-Item *` would lose the
   sensitive cwd — and produce a security false-negative.
5. Attribution is purely additive — the `Set-Location` clause and every
   subsequent clause keep everything the user typed, plus the synthetic arg.

`Push-Location` / `Pop-Location` (`pushd` / `popd`) are `CwdVerbs` — their
first non-flag positional is path-classified — but they do **not** add a
synthetic attribution arg in v0.2.0, mirroring bash locked interpretation #5
(`SPEC.md` §9). A real directory-stack model is deferred (§18).

Attribution propagates across the statement separators `;`, `&&`, `||`, and
newline. Within a single pipeline the elements share one cwd; `|` joins
pipeline elements within a statement, not separate statements.

---

## 10. Subexpression & `pwsh -Command` Recursion

### Opaque regions

Script blocks `{ ... }`, subexpressions `$( ... )`, array subexpressions
`@( ... )`, and hash literals `@{ ... }` are bounded by the shared
`OpaqueRegionScanner` and emitted as single tokens; the parser consumes each
as one `Arg { Kind=DynamicSkip, IsPath=false, Resolved=null }`, `Raw` being
the verbatim region slice. Splatting `@var` is likewise `DynamicSkip`. The
`--%` stop-parsing token makes the pipeline-element remainder one
`DynamicSkip` arg. The interior of these regions is **not** parsed — this is
the heart of the Pipeline-aware scope (§4): `gci | ? { ... } | rm` is a
clean three-clause pipeline whose middle clause carries an opaque arg.

`OpaqueRegionScanner` is grammar-agnostic but escapes on backslash; for
PowerShell it is given a backtick-escape mode so `` { `} } `` scans
correctly.

### `pwsh -Command` recursion

`pwsh -Command "<inner>"` is the PowerShell analog of bash `bash -c`
(`SPEC.md` §10). The parser recognizes a clause whose verb is `pwsh`,
`powershell`, `pwsh.exe`, or `powershell.exe` (case-insensitive) carrying a
`-Command` parameter (the canonical `-Command`, the short `-c`, or any
unambiguous `-Comm*` prefix).

PowerShell's `-Command` consumes **everything after it** on the invocation,
not just one quoted token. The parser handles all three real forms:

- **Quoted string** — `pwsh -Command "Remove-Item C:\tmp\x"`. Parse the
  string value as a fresh `ParsedCommand`.
- **Script block** — `pwsh -Command { Remove-Item C:\tmp\x }`. Parse the
  script-block *interior* (braces stripped) as a fresh `ParsedCommand`.
- **Bare / multi-token** — `pwsh -Command Remove-Item C:\tmp\x`. Take the
  verbatim source slice from the first token after `-Command` to the end of
  the statement and parse *that* as a fresh `ParsedCommand`.

In every form the inner clauses surface inline, each with
`IsCommandStringWrapped = true`. **Not** recognizing the bare/multi-token
form would let `pwsh -Command Remove-Item C:\x` leak through as opaque args
of `pwsh` — a verb-keyed gate would never see `Remove-Item`. The depth-5
recursion cap applies; deeper nesting, **or an inner parse that itself
yields `IsUnparseable = true`** (e.g. a `-Command` payload that decodes to a
control-flow script), sets the outer `ParsedCommand.IsUnparseable = true` so
the whole command routes to safe-fail (`SPEC.md` §10).

`pwsh -File script.ps1` is **not** recursion — the file content is not
available to the parser. It parses as an ordinary clause with `script.ps1`
as a path arg.

### `pwsh -EncodedCommand` recursion

`pwsh -EncodedCommand <base64>` (also the unambiguous `-e` / `-enc` /
`-encodedc*` prefixes) runs a Base64-encoded, UTF-16LE command string — the
prime way to hide a command from a security gate. The parser **decodes and
recurses**: it takes the single token following `-EncodedCommand` as the
payload (the parameter name *defines* it as base64 — the parser does not
heuristically sniff for "base64-shaped" tokens), Base64-decodes it,
UTF-16LE-decodes the result, **strips a leading UTF-16 BOM (`U+FEFF`) if
present**, parses the remainder as a fresh `ParsedCommand`, and surfaces the
inner clauses inline with `IsCommandStringWrapped = true`, under the same
depth-5 cap. Seeing through this obfuscation is core value for a
security-gating parser.

The BOM strip is not cosmetic: a `U+FEFF` left on the front of the decoded
string corrupts the first token — the verb — and the verb is the gate key.

On any failure — the token is not well-formed base64, the bytes are not
valid UTF-16LE, the decoded payload exceeds the input cap (§11), the inner
parse itself yields `IsUnparseable = true`, or the depth cap is exceeded —
the parser sets `ParsedCommand.IsUnparseable = true` with a reason naming the
failure.

### `Invoke-Expression`

`Invoke-Expression` / `iex` is **not** recursed into — evaluating its string
argument requires PowerShell expression semantics, which is out of scope. It
parses as an ordinary clause; its argument is a normal `Arg` (`DynamicSkip`
when it is `$var` or `$( ... )`). A consumer that wants to gate dynamic code
execution can hard-deny the verb `Invoke-Expression` / `iex` itself.

---

## 11. Parser Anomaly Behavior

The safe-fail contract — set `IsUnparseable=true`, set `UnparseableReason`,
return whatever clauses parsed, never throw on a well-formed string — is
defined in **`SPEC.md` §11** and is unchanged.

`PwshCommandParser` sets `ParsedCommand.IsUnparseable = true` for:

1. **Lexer sentinels** — unbalanced single/double quote, unterminated
   here-string, unterminated `<# ... #>` block comment, unbalanced `{ }` /
   `$( )` / `@( )` / `@{ }`, unbalanced grouping `( )`.
2. **Control-flow keywords at statement/verb position** — `if`, `elseif`,
   `else`, `switch`, `foreach` (when followed by `(`), `for`, `while`, `do`,
   `until`.
3. **Definition keywords** — `function`, `filter`, `workflow`,
   `configuration`, `class`, `enum`.
4. **Block / trap / data keywords** — `param`, `begin`, `process`, `end`,
   `dynamicparam`, `trap`, `data`, `try`, `catch`, `finally`.
5. **Statement keywords leading a statement** — `return`, `throw`, `break`,
   `continue`, `exit` (including `exit 0`), `using`, `hidden`, and the
   dot-source operator `.` used as a statement.
6. **Trailing `&` background-job operator** — a `&` at the end of a pipeline
   (not at verb position). `& git status` is the call operator and parses;
   `git status &` is a background job and does not.
7. **Assignment statement** — a statement that begins `$var = ...`.
8. **Bare type-literal / .NET method call** — a statement that is just
   `[type]::Member(...)`, which has no verb.
9. **`pwsh` recursion failure** — `pwsh -Command` / `-EncodedCommand`
   recursion depth exceeds 5, an `-EncodedCommand` payload fails to decode,
   or an inner parse itself yields `IsUnparseable` (§10).
10. **Oversized input** — the command string, or a decoded `-EncodedCommand`
    payload, exceeds the parser's input cap. The cap guards the
    per-shell-call hot path against a pathological or malicious input (a
    multi-megabyte base64 blob would otherwise decode and recurse up to five
    deep). The cap is a fixed internal constant — **64 KiB of UTF-16
    characters**, applied to the top-level input and to each decoded
    payload — **not** a `PwshParserOptions` knob: a security limit a
    consumer can raise is not a limit. Over-cap input sets
    `IsUnparseable = true` with a reason naming the cap. (This guard is
    introduced by the PowerShell parser; the bash parser may adopt the same
    cap in a later v0.1.x.)

**Diagnostic precedence** (most-informative first, mirroring `SPEC.md` §11):
(1) input over the size cap, checked before lexing; (2) lexer
`UnparseableSentinel` tokens; (3) a control-flow / definition / block keyword
at statement position; (4) a trailing `&` background job; (5) an assignment
or bare type-literal statement; (6) grouping `( )` balance errors or an
unexpected operator; (7) the `pwsh` recursion cap, an inner-parse
`IsUnparseable`, or an `-EncodedCommand` decode failure.

Consumers route an unparseable command to safe-fail exactly as for bash
(`SPEC.md` §11, Appendix A).

---

## 12. Public Examples

Hand-authored input / expected-AST pairs anchoring understanding. Each shows
the salient AST fields; omitted fields take their documented defaults.

### Simple cmdlet with a path parameter

Input: `Get-ChildItem -Path C:\logs -Recurse`

```
Clause 0: Operator=None, Verb=[Get-ChildItem]
          Args=[ {Raw="-Path", IsFlag=true},
                 {Raw="C:\logs", Kind=Literal, IsPath=true, Resolved="C:/logs"},
                 {Raw="-Recurse", IsFlag=true} ]
```

### Alias resolution

Input: `gci C:\logs`

```
Clause 0: Operator=None, Verb=[gci], CanonicalVerb="Get-ChildItem"
          Args=[ {Raw="C:\logs", Kind=Literal, IsPath=true, Resolved="C:/logs"} ]
```

`Tokens` keeps the verbatim `gci`; `CanonicalVerb` carries `Get-ChildItem`.

### Pipeline with an opaque script block

Input: `gci | ? { $_.Length -gt 1mb } | rm`

```
Clause 0: Operator=None, Verb=[gci], CanonicalVerb="Get-ChildItem"
Clause 1: Operator=Pipe, Verb=[?],   CanonicalVerb="Where-Object",
          Args=[ {Raw="{ $_.Length -gt 1mb }", Kind=DynamicSkip, IsPath=false} ]
Clause 2: Operator=Pipe, Verb=[rm],  CanonicalVerb="Remove-Item"
```

### Set-Location propagation

Input: `cd C:\repo; git status`

```
Clause 0: Operator=None, Verb=[cd], CanonicalVerb="Set-Location",
          Args=[ {Raw="C:\repo", IsPath=true, Resolved="C:/repo"} ]
Clause 1: Operator=Sequence, Verb=[git, status],
          Args=[ {Raw="C:\repo", IsPath=true, Resolved="C:/repo",
                  IsCwdAttribution=true} ]
```

### Recursing into `pwsh -Command`

Input: `pwsh -Command "Remove-Item C:\tmp\x"`

```
Clause 0: Operator=None, Verb=[Remove-Item], IsCommandStringWrapped=true
          Args=[ {Raw="C:\tmp\x", IsPath=true, Resolved="C:/tmp/x"} ]
```

### Recursing into `pwsh -EncodedCommand`

Input: `pwsh -EncodedCommand RwBlAHQALQBEAGEAdABlAA==`

```
(payload decodes to `Get-Date`)
Clause 0: Operator=None, Verb=[Get-Date], IsCommandStringWrapped=true
```

### Registry-provider path is not a filesystem path

Input: `Remove-Item HKLM:\Software\X`

```
Clause 0: Operator=None, Verb=[Remove-Item],
          Args=[ {Raw="HKLM:\Software\X", Kind=Literal, IsPath=false} ]
```

### Unparseable — a control-flow construct

Input: `foreach ($f in $list) { Remove-Item $f }`

```
IsUnparseable=true
UnparseableReason="control-flow keyword 'foreach' is not supported in v0.2"
```

---

## 13. Test Corpus Contract

The corpus is the acceptance contract for the parser, exactly as in
**`SPEC.md` §13**. The JSON entry schema, the file-name convention
(`NN_descriptive_slug.json`), and the `[Theory]`-driven runner are unchanged.
PowerShell-specific deltas:

### Location

PowerShell corpus entries live in
`tests/ShellSyntaxTree.Tests/Corpus/powershell/*.json`. Corpus files are
**directory-routed by shell**: an entry under `Corpus/bash/` is parsed with
`BashParser`, an entry under `Corpus/powershell/` with `PwshParser`. The
corpus runner and the PII audit are refactored to enumerate every
`Corpus/<shell>/` directory rather than a hard-coded `bash` path.

### Schema additions

The shared corpus DTO gains two optional fields:

- **`canonicalVerb`** (per clause) — the expected `VerbChain.CanonicalVerb`.
  Omit to assert `null` (every bash entry, and PowerShell canonical/unknown
  verbs); provide the canonical cmdlet to assert an alias was resolved.
- **`oracleExpectation`** (per entry, meaningful only when
  `isUnparseable: true`) — `SyntaxError` (genuinely malformed PowerShell —
  real `pwsh` must also reject it) or `OutOfScope` (valid PowerShell the
  parser deliberately does not model — real `pwsh` must accept it). Defaults
  to `SyntaxError`. `OutOfScope` also covers an input that is valid
  PowerShell but that the parser declines for a non-grammar reason — an
  `-EncodedCommand` decode failure, an over-cap input (§11), or a
  recursion-depth overflow — because real `pwsh` parses the *outer*
  invocation without error.

### Coverage targets for v0.2.0

| Category | Min |
|---|---|
| Simple cmdlet | 10 |
| Alias resolution (assert raw `Tokens` + `CanonicalVerb`) | 15 |
| Native command / multi-token chain | 10 |
| Pipeline | 15 |
| Compound / statement separator (`;`, `&&`, `||`, newline) | 10 |
| `Set-Location` propagation | 10 |
| Quote handling (single, double, here-string, backtick escape) | 10 |
| Parameter binding — named, positional, switch vs. value-binding (§6.5), colon-form, splat | 25 |
| Redirect (including streams 1–6 / `*` and `2>&1`) | 10 |
| `pwsh -Command` / `-EncodedCommand` recursion — incl. bare/script-block `-Command` forms and adversarial `-EncodedCommand` payloads (bad base64, BOM, decodes-to-control-flow, nested) | 15 |
| Dynamic skip (`$var`, `$( )`, glob, script block, dynamic verb `& $exe`) | 10 |
| Per-verb / per-parameter path rules | 10 |
| Unparseable (control flow, definitions, `param()`, blocks, assignment, type-literal, trailing `&`, recursion overflow, over-cap input) | 20 |

**Total minimum: 170 entries.** Alias-resolution and parameter-binding are
PowerShell-specific net-new categories; parameter binding (§6.5) is the
hardest part of the parser and is budgeted accordingly. Strive for 200+ once
seeded from sanitized real-world commands.

### The `pwsh` validation gate

A CI test feeds every PowerShell corpus `input` to the real PowerShell parser
(`[System.Management.Automation.Language.Parser]::ParseInput`) via a batched
child-process `pwsh` invocation and enforces:

| `isUnparseable` | `oracleExpectation` | real `pwsh` must report |
|---|---|---|
| `false` | (n/a) | zero parse errors — the input is valid PowerShell |
| `true` | `SyntaxError` | at least one parse error |
| `true` | `OutOfScope` | zero parse errors — valid PowerShell we decline to model |

This validates corpus *inputs* against ground truth; it is **not** a
differential comparison of our AST against PowerShell's AST — a hand-authored
`expected` AST with a wrong parameter binding (§6.5) still passes the gate.
Author binding-category entries with extra care, and cross-check them with
`tools/PwshCorpusTool`, which prints, for a given command, the parser's
`expected` JSON block beside the real `pwsh` verdict; the tool is registered
in `TOOLING.md`. A developer without `pwsh` on `PATH` sees the gate skipped;
CI installs `pwsh` and an explicit step fails loudly if it is absent.

---

## 14. Sanitization Process

The sanitization workflow, the PII rule table, and the audit gate are defined
in **`SPEC.md` §14** and apply to the PowerShell corpus unchanged — the audit
scans every `Corpus/<shell>/` directory. PowerShell corpus seeded from real
logs carries Windows-shaped paths, so the §14 rule table and the PII-audit
regex set gain:

| Pattern | Replacement |
|---|---|
| `C:\Users\<username>\` (and the mixed-slash `C:\Users\<username>/`) | `C:\Users\user\` |
| UNC `\\<hostname>\share\` | `\\internal-host.example\share\` |
| Windows domain `DOMAIN\username` | `DOMAIN\user` |

A literal `$env:USERNAME` / `$env:USERPROFILE` reference is **not** PII (the
variable is not a value) and is left as-is; only an *expanded* concrete user
path is sanitized.

---

## 15. CI & Release Flow

The CI workflows and the bare-SemVer tag convention are defined in
**`SPEC.md` §15** and are unchanged. PowerShell-specific deltas:

- **Versioning.** `Directory.Build.props` `VersionPrefix` → `0.2.0`. Per
  `SPEC.md` §15, v0.2.0 is the first PowerShell parser implementation. Ship a
  `0.2.0-alpha` → `0.2.0-beta` prerelease so Netclaw validates the PowerShell
  parser and the breaking `Clause` rename before promotion to stable
  `0.2.0` — the same beta-then-promote flow used for v0.1.5.
- **Test job.** `dotnet test` runs the bash corpus, the PowerShell corpus,
  the `pwsh` validation gate, and the multi-shell PII audit.
- **`pwsh` availability.** CI installs `pwsh` (already used for the
  copyright-header script); an explicit step verifies it so the validation
  gate never silently skips on CI.
- **Release notes.** The `RELEASE_NOTES.md` v0.2.0 section must list the
  breaking `Clause.IsBashCWrapped` → `IsCommandStringWrapped` rename with the
  old→new mapping, and the new `PwshParser` / `PwshParserOptions` /
  `ShellParserOptions` / `VerbChain.CanonicalVerb` surface.

---

## 16. Implementation Sequencing

A natural order for the implementer. Each numbered item is a self-contained,
testable step; most are a single PR.

1. **Public-API surface change (lock first).** Add `ShellParserOptions`,
   reparent `BashParserOptions`, add `PwshParserOptions` (empty record), add
   a `PwshParser` skeleton (`Parse` throws `NotImplementedException`), add
   `VerbChain.CanonicalVerb` and `VerbChain.IsDynamic`, rename
   `Clause.IsBashCWrapped` → `IsCommandStringWrapped`. Update
   `PublicApiSnapshotTests`, the corpus DTOs, and the `isBashCWrapped` →
   `isCommandStringWrapped` key in the existing bash corpus JSON. All
   existing bash tests stay green.
2. **PowerShell verb & binding tables** — `Internal/Pwsh/Verbs/`:
   `PwshApprovedVerbs` (the `Get-Verb` set, §6.1), `PwshAliases` (the
   complete default alias set, §6.3), `PwshVerbs` (Cwd / File /
   control-flow, §6.4), `PwshValueParameters` / `PwshSwitchParameters` (the
   binding tables, §6.5), `PwshPerVerbRules` (§7).
3. **`PwshLexer`** (`Internal/Pwsh/Lexing/`) — quoting, backtick escape,
   `$var` / `$env:` / `${name}`, parameters, stream redirects, statement
   separators, comments; opaque regions via the shared `OpaqueRegionScanner`
   (with the backtick-escape mode). Heavy unit tests.
4. **`PwshCommandParser` core** — pipeline / statement splitting, verb-chain
   extraction, the §6.5 parameter-binding decision, args & parameters,
   redirects, `VerbChain.IsDynamic` for dynamic command names.
5. **Alias resolution** — populate `VerbChain.CanonicalVerb` unconditionally
   (§6.3); add the `[Fact]` diffing `PwshAliases` against live `Get-Alias`.
6. **`PwshResolver`** — §8.
7. **Per-verb / per-parameter path rules** — §7.
8. **`Set-Location`-in-compound propagation** — §9.
9. **`pwsh -Command` / `-EncodedCommand` recursion** — §10.
10. **Anomaly safe-fail** — §11.
11. **Multi-shell refactor** of the corpus runner and PII audit — §13 — so
    both enumerate every `Corpus/<shell>/` directory. This MUST precede
    corpus authoring: the runner cannot execute a `Corpus/powershell/` entry
    while it is hard-coded to the `bash` path.
12. **Hand-author the PowerShell corpus** — §13 (≥170 entries).
13. **`pwsh` validation gate + `tools/PwshCorpusTool`** — §13; register the
    tool in `TOOLING.md`.
14. **`SPEC.md` edits** — update §1 / §2 / §3 / §6.4 / §15 to reflect the
    shipped v0.2.0 surface, and wire CI; tag `0.2.0-alpha` when green.

Most new code lives in PowerShell-only files under `Internal/Pwsh/`, but
some shared surface is touched and must be treated as shared — not as "bash
internals":

- `Internal/Lexing/OpaqueRegionScanner` gains a backtick-escape mode (§10) —
  a shared file the bash lexer also uses.
- The native-command greedy walk (§6.2) and the per-verb path rules (§7.3)
  are **reused** from the bash implementation. Reuse means a bash-side change
  can regress PowerShell. Promote the reused pieces into `Internal/Shared/`
  (or reference them deliberately) and ensure the PowerShell corpus exercises
  every shared path, so a bash PR cannot silently break PowerShell without a
  red test.

Extracting a *full* shared lexer/parser core is still deferred past v0.2.0
(§18) — but the incidental shared surface above is real, and "no bash
internals refactored" is not an accurate description of the work.

---

## 17. Acceptance Criteria

v0.2.0 ships when **all** of these hold:

1. The public API matches §2 — `PwshParser`, `PwshParserOptions`,
   `ShellParserOptions`, `VerbChain.CanonicalVerb`, `VerbChain.IsDynamic`,
   and the `Clause` rename. `dotnet pack` produces
   `ShellSyntaxTree.0.2.0.nupkg`.
2. Every existing bash corpus entry still parses to its expected AST —
   v0.2.0 is a non-regression for bash.
3. Every PowerShell corpus entry parses to its expected AST.
4. The PowerShell corpus has ≥170 entries spanning the §13 categories,
   including alias-resolution entries that assert both `Tokens` and
   `CanonicalVerb`, and parameter-binding entries that pin switch-vs-value
   decisions (§6.5).
5. The `pwsh` validation gate passes — every PowerShell entry is consistent
   with real `pwsh` per the §13 matrix — and the `PwshAliases` completeness
   `[Fact]` (§6.3) confirms the table matches live `Get-Alias` output.
6. The PII audit scans both `Corpus/bash/` and `Corpus/powershell/` and finds
   zero hits.
7. `dotnet test` runs on PR via GitHub Actions and passes on Linux and
   Windows.
8. Tagging `0.2.0-alpha` triggers `publish_nuget.yml` and the package appears
   on nuget.org.
9. Netclaw consumes the v0.2.0 package: `IShellParser` resolves, the `Clause`
   rename is absorbed, and at least one Netclaw integration test exercises a
   real PowerShell corpus entry through the live matcher and gets the
   expected gate decision.

---

## 18. Out of Scope (deferred from v0.2.0)

- PowerShell script-level constructs — control flow, `function`/`class`/
  `enum` definitions, `param()`/`begin`/`process`/`end` blocks, `trap`,
  `DATA` (all `IsUnparseable`).
- `.ps1` script-file parsing.
- PowerShell expression evaluation, `$_` / `$PSItem` semantics, .NET method
  calls.
- Desired State Configuration (DSC).
- A real `Push-Location` / `Pop-Location` directory-stack model (§9).
- Lossless redirect-stream identity — PowerShell streams 3–6 / `*` map
  lossily onto `RedirectDirection` (§8); growing the public enum is a
  candidate v0.2.x item.
- Per-element path extraction from a comma-separated array
  (`-Path a,b,c`) — v0.2.0 marks the whole token `DynamicSkip` (§8);
  splitting it into per-element path args is a candidate v0.2.x item.
- Extracting a shared lexer/parser core from the bash and PowerShell
  implementations — deliberately deferred until two parsers exist so the
  seam is designed from real duplication, not guessed.
- Windows `cmd` parsing — still deferred (`SPEC.md` §18).

---

## Appendix A: Consumer Contract

The consumer contract is defined in **`SPEC.md` Appendix A** and is
shell-neutral — a consumer walks a PowerShell `ParsedCommand` exactly as it
walks a bash one. Two additions:

- When gating on verb identity, use the gate key
  `CanonicalVerb ?? (Tokens.Count > 0 ? Tokens[0] : null)` — the index is
  guarded because a redirect-only clause has an empty `Tokens` (`SPEC.md`
  §3). An aliased PowerShell verb (`rm`, `gci`) then gates as its canonical
  cmdlet (`Remove-Item`, `Get-ChildItem`).
- A clause with `VerbChain.IsDynamic = true` (§3) has no
  statically-knowable verb identity — the command name is `$var` or a
  subexpression. Route it to safe-fail regardless of the gate key; no
  verb-pattern grant should match it.

---

## Appendix B: Why a hand-rolled PowerShell parser?

`SPEC.md` Appendix B explains why ShellSyntaxTree hand-rolls its bash parser
rather than binding a native one. The same reasoning holds for PowerShell,
and one PowerShell-specific option is explicitly rejected:

PowerShell ships a full parser in `System.Management.Automation`
(`[System.Management.Automation.Language.Parser]`). Binding it is tempting,
but it pulls the entire PowerShell SDK as a dependency — large, far from
AOT-trim-friendly, and contrary to the "single managed package, zero native
deps" constraint (`SPEC.md` §1). It also produces a full script AST: it would
happily parse `function`, `class`, and control flow that this library
deliberately marks `IsUnparseable` so consumers route to safe-fail.

The hand-rolled, Pipeline-aware parser keeps the v0.2.0 scope deliberate and
the package dependency-free. The real PowerShell parser still earns its keep
— as the **test-time `pwsh` validation oracle** (§13) that confirms every
corpus input is genuine PowerShell — without becoming a runtime dependency.
