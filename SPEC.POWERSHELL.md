# ShellSyntaxTree — PowerShell Specification (v0.2.0)

**Status:** Draft for v0.2.0. Approved decisions; implementation pending.
**Audience:** Whoever (human or agent) implements the ShellSyntaxTree
PowerShell parser.
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

/// <summary>Configuration knobs for PwshParser.</summary>
public sealed record PwshParserOptions : ShellParserOptions
{
    /// <summary>When true (default), built-in PowerShell aliases (ls, gci,
    /// rm, ...) are resolved to their canonical cmdlet on
    /// VerbChain.CanonicalVerb. The raw token always stays verbatim in
    /// VerbChain.Tokens.</summary>
    public bool ResolveAliases { get; init; } = true;
}

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
- `Clause.IsBashCWrapped` is renamed `Clause.IsCommandStringWrapped`.

**Versioning.** `PwshParser`, `PwshParserOptions`, `ShellParserOptions`, and
`VerbChain.CanonicalVerb` are additive. The `Clause` field rename and the
`BashParserOptions` reparenting are **breaking** — appropriate for the
v0.2.0 minor bump per `SPEC.md` §15. `PublicApiSnapshotTests` is updated in
the same change. `PwshParser.Parse` throws `ArgumentNullException` on null
input and never throws on a well-formed string, exactly like `BashParser`.

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
- A parenthesized **pipeline** `( ... )` parses as a sub-pipeline; its
  clauses carry `IsSubshell = true` (the bash subshell analog, `SPEC.md`
  §10). A group containing control flow marks `IsUnparseable`.
- `--%` is the stop-parsing token: the remainder of the pipeline element
  becomes one opaque `DynamicSkip` arg.
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
  keeps the value; the parser splits on the first `:`.
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
all ASCII letters; the segment after `-` begins with an ASCII letter and is
ASCII letters/digits only. `Get-ChildItem`, `get-childitem`, and
`GET-CHILDITEM` all match.

A cmdlet-shaped first token (or one resolved through the alias table, §6.3)
makes the verb chain exactly **one token**. PowerShell cmdlets take explicit
parameters; there is no `git push origin` style nested-subcommand idiom for
cmdlets, so the bash greedy walk does not apply to them.

### 6.2 Native-command verb chains

When the first token is neither cmdlet-shaped nor a known alias (`git`,
`dotnet`, `npm`, `kubectl`, `python`, ...) it is a **native command**.
Native commands reuse the bash greedy verb-chain walk (`SPEC.md` §6.1):
append the first token, then walk consecutive verb-like Word tokens,
transparently consuming flag-with-value pairs, stopping at the first
non-verb-like token, flag, operator, quoted string, or opaque token. The
verb-like predicate is the bash predicate made **case-insensitive** (first
char an ASCII letter of any case; remaining chars `[A-Za-z0-9._-]`), because
native subcommands invoked from PowerShell are mixed-case in the wild
(`dotnet ef migrations add`). `Clause.Verb` remains a convenience hint, not
a security contract (`SPEC.md` §6.1.1); consumers pattern-prefix match.

### 6.3 Built-in alias table

`PwshAliases` is a static, case-insensitive map from a typed alias to its
canonical cmdlet. When the first token is a known alias the parser:

- keeps the verbatim typed token in `VerbChain.Tokens` (source fidelity;
  pattern-matching sees what was typed), and
- sets `VerbChain.CanonicalVerb` to the canonical cmdlet, which drives the
  per-cmdlet path rules (§7) and the Cwd/File verb classification (§6.4).

Alias resolution is controlled by `PwshParserOptions.ResolveAliases`
(default true). The built-in table (seed — extended as the corpus surfaces
real usage):

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

---

## 7. Per-Verb / Per-Parameter Path-Arg Extraction Rules

PowerShell classifies path arguments in two layers — a **parameter-value**
layer (dominant, because PowerShell names paths explicitly) and a
**positional** layer. Per-cmdlet rules are keyed by the *canonical* verb
(§6.3), so `rm x` and `Remove-Item x` classify identically.

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
path-parameter, positional 0 (and beyond) classifies as a path for canonical
FileVerbs. Per-cmdlet overrides:

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
the first positional is a URL; the `-o` / `-O` value is a path).

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

---

## 9. Set-Location-in-Compound Propagation

PowerShell honors the same cwd-attribution propagation bash applies to `cd`
(`SPEC.md` §9): `Set-Location <dir>; cmd` runs `cmd` with cwd `<dir>`.

**Rules** (parallel to `SPEC.md` §9):

1. A clause whose canonical verb is `Set-Location` (raw aliases `cd`,
   `chdir`, `sl`) sets the **attributed cwd** for subsequent clauses in the
   same compound. The cwd target is the value of `-Path` / `-LiteralPath`
   when present, else positional 0.
2. Subsequent clauses receive a synthetic `Arg` with `IsCwdAttribution=true`
   — `Kind=Literal, IsPath=true` when the target resolved, `Kind=DynamicSkip,
   IsPath=false` when the target was dynamic (`cd $repo`).
3. A later `Set-Location` replaces the attributed cwd.
4. Sub-pipeline (`( ... )`) boundaries isolate attribution, exactly as bash
   subshells do (`SPEC.md` §9 rule 4 / §10).
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
`-Command` parameter (or the short `-c`, or an unambiguous `-Comm*` prefix)
whose value is a quoted string. It parses the quoted argument as a fresh
`ParsedCommand` and surfaces the inner clauses inline, each with
`IsCommandStringWrapped = true`. The depth-5 recursion cap applies — deeper
nesting sets the outer `ParsedCommand.IsUnparseable = true` (`SPEC.md` §10).

`pwsh -File script.ps1` is **not** recursion — the file content is not
available to the parser. It parses as an ordinary clause with `script.ps1`
as a path arg.

### `pwsh -EncodedCommand` recursion

`pwsh -EncodedCommand <base64>` (also `-e` / `-enc`) runs a Base64-encoded,
UTF-16LE command string — the prime way to hide a command from a security
gate. The parser **decodes and recurses**: it Base64-decodes the single
base64-shaped token, UTF-16LE-decodes the result, parses it as a fresh
`ParsedCommand`, and surfaces the inner clauses inline with
`IsCommandStringWrapped = true`, under the same depth-5 cap. Seeing through
this obfuscation is core value for a security-gating parser. On any failure
— the token is not well-formed base64, the bytes are not valid UTF-16LE, or
the depth cap is exceeded — the parser falls back to
`ParsedCommand.IsUnparseable = true` with a reason naming the failure.

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
   recursion depth exceeds 5, or an `-EncodedCommand` payload fails to
   decode (§10).

**Diagnostic precedence** (most-informative first, mirroring `SPEC.md` §11):
(1) lexer `UnparseableSentinel` tokens; (2) a control-flow / definition /
block keyword at statement position; (3) a trailing `&` background job;
(4) an assignment or bare type-literal statement; (5) grouping `( )` balance
errors or an unexpected operator; (6) the `pwsh` recursion cap or an
`-EncodedCommand` decode failure.

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
  to `SyntaxError`.

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
| Parameter binding (named, positional, switch, colon-form, splat) | 10 |
| Redirect (including streams and `2>&1`) | 10 |
| `pwsh -Command` / `-EncodedCommand` recursion | 10 |
| Dynamic skip (`$var`, `$( )`, glob, script block) | 10 |
| Per-verb / per-parameter path rules | 10 |
| Unparseable (control flow, definitions, `param()`, recursion overflow) | 12 |

**Total minimum: 142 entries.** Alias-resolution and parameter-binding are
PowerShell-specific net-new categories. Strive for 180+ once seeded from
sanitized real-world commands.

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
differential comparison of our AST against PowerShell's AST. It catches
hand-authored entries with invented or subtly-invalid syntax. A developer
without `pwsh` on `PATH` sees the gate skipped; CI installs `pwsh` and an
explicit step fails loudly if it is absent. A `tools/PwshCorpusTool` aid
prints, for a given command, the parser's `expected` JSON block beside the
real `pwsh` verdict — the corpus-authoring companion to the gate.

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
   reparent `BashParserOptions`, add `PwshParserOptions`, add a `PwshParser`
   skeleton (`Parse` throws `NotImplementedException`), add
   `VerbChain.CanonicalVerb`, rename `Clause.IsBashCWrapped` →
   `IsCommandStringWrapped`. Update `PublicApiSnapshotTests`, the corpus
   DTOs, and the `isBashCWrapped` → `isCommandStringWrapped` key in the
   existing bash corpus JSON. All existing bash tests stay green.
2. **PowerShell verb tables** — `Internal/Pwsh/Verbs/`: `PwshAliases`,
   `PwshVerbs` (Cwd / File / control-flow), `PwshPerVerbRules`.
3. **`PwshLexer`** (`Internal/Pwsh/Lexing/`) — quoting, backtick escape,
   `$var` / `$env:` / `${name}`, parameters, stream redirects, statement
   separators, comments; opaque regions via the shared `OpaqueRegionScanner`
   (with the backtick-escape mode). Heavy unit tests.
4. **`PwshCommandParser` core** — pipeline / statement splitting, verb-chain
   extraction, args & parameters, redirects.
5. **Alias resolution** — populate `VerbChain.CanonicalVerb`, honoring
   `PwshParserOptions.ResolveAliases`.
6. **`PwshResolver`** — §8.
7. **Per-verb / per-parameter path rules** — §7.
8. **`Set-Location`-in-compound propagation** — §9.
9. **`pwsh -Command` / `-EncodedCommand` recursion** — §10.
10. **Anomaly safe-fail** — §11.
11. **Hand-author the PowerShell corpus** — §13 (≈142 entries).
12. **`pwsh` validation gate + `tools/PwshCorpusTool`** — §13.
13. **Multi-shell refactor** of the corpus runner and PII audit — §13.
14. **`SPEC.md` edits** — update §1 / §2 / §3 / §6.4 / §15 to reflect the
    shipped v0.2.0 surface, and wire CI; tag `0.2.0-alpha` when green.

Steps 2–10 are PowerShell-only files under `Internal/Pwsh/`; no bash
internals are refactored. Extracting a shared lexer/parser core is deferred
past v0.2.0 (§18).

---

## 17. Acceptance Criteria

v0.2.0 ships when **all** of these hold:

1. The public API matches §2 — `PwshParser`, `PwshParserOptions`,
   `ShellParserOptions`, `VerbChain.CanonicalVerb`, and the `Clause` rename.
   `dotnet pack` produces `ShellSyntaxTree.0.2.0.nupkg`.
2. Every existing bash corpus entry still parses to its expected AST —
   v0.2.0 is a non-regression for bash.
3. Every PowerShell corpus entry parses to its expected AST.
4. The PowerShell corpus has ≥142 entries spanning the §13 categories,
   including alias-resolution entries that assert both `Tokens` and
   `CanonicalVerb`.
5. The `pwsh` validation gate passes — every PowerShell entry is consistent
   with real `pwsh` per the §13 matrix.
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
- Extracting a shared lexer/parser core from the bash and PowerShell
  implementations — deliberately deferred until two parsers exist so the
  seam is designed from real duplication, not guessed.
- Windows `cmd` parsing — still deferred (`SPEC.md` §18).

---

## Appendix A: Consumer Contract

The consumer contract is defined in **`SPEC.md` Appendix A** and is
shell-neutral — a consumer walks a PowerShell `ParsedCommand` exactly as it
walks a bash one. One addition: when gating on verb identity, a consumer
SHOULD use `VerbChain.CanonicalVerb ?? VerbChain.Tokens[0]` as the gate key,
so an aliased PowerShell verb (`rm`, `gci`) gates as its canonical cmdlet
(`Remove-Item`, `Get-ChildItem`).

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
