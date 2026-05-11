# shellsyntaxtree-v0.1 — spec delta

## ADDED Requirements

### Requirement: Public API surface mirrors SPEC.md §2 / §3

The library SHALL expose only the public types enumerated in `SPEC.md` §2
and §3, plus the additive `Arg.IsCwdAttribution: bool` field per locked
interpretation #1. All other types in the `ShellSyntaxTree` assembly SHALL
be `internal`. The library SHALL multi-target `netstandard2.0;net8.0` and
SHALL set `<IsAotCompatible>true</IsAotCompatible>` on the net8.0
configuration. SourceLink and SymbolPackageFormat=snupkg SHALL be enabled.

#### Scenario: Public API snapshot matches SPEC

- **GIVEN** a freshly built `ShellSyntaxTree.dll`
- **WHEN** reflection enumerates all `public` types in the assembly
- **THEN** the set is exactly: `IShellParser`, `BashParser`,
  `BashParserOptions`, `ParsedCommand`, `Clause`, `VerbChain`, `Arg`,
  `Redirect`, `ArgKind`, `RedirectDirection`, `CompoundOperator`
- **AND** every record in that set is `sealed`
- **AND** every record property uses `init`-only setters
- **AND** the `Arg` record has the additional `IsCwdAttribution: bool`
  property (default `false`) beyond the SPEC §2 enumeration

#### Scenario: BashParser.Parse(null) throws ArgumentNullException

- **GIVEN** a `BashParser` instance
- **WHEN** `Parse(null)` is called
- **THEN** the call throws `ArgumentNullException`

#### Scenario: BashParser.Parse stub throws NotImplementedException in v0.1.0-alpha

- **GIVEN** a `BashParser` instance and any non-null command string
- **WHEN** `Parse(command)` is called before PRs 2–5 wire up the lexer,
  parser, and resolver
- **THEN** the call throws `NotImplementedException`
- **AND** the message references the v0.1 implementation plan

### Requirement: Synthetic cwd attribution arg distinguishes inferred paths

The parser SHALL append a synthetic `Arg` with `IsCwdAttribution = true`
to each clause that follows a `cd` or `chdir` clause within the same
compound. When the cd target is statically known, the synthetic arg SHALL
have `Kind = Literal`, `IsPath = true`, and `Resolved` equal to the
resolved cwd. When the cd target is `Kind = DynamicSkip` (an unresolved env
var, command substitution, or other dynamic content), the synthetic arg
SHALL have `Kind = DynamicSkip`, `IsPath = false`, and `Resolved = null`.

#### Scenario: Literal cd propagates resolvable attribution

- **GIVEN** the input `cd /target && cmd`
- **WHEN** the parser produces the AST
- **THEN** the second clause's `Args` ends with `Arg{ Raw = "/target",
  Resolved = "/target", Kind = Literal, IsPath = true,
  IsCwdAttribution = true }`

#### Scenario: Dynamic cd propagates DynamicSkip attribution

- **GIVEN** the input `cd $REPO_DIR && rm -rf node_modules`
- **WHEN** the parser produces the AST
- **THEN** the rm clause's `Args` includes `Arg{ IsCwdAttribution = true,
  Kind = DynamicSkip, IsPath = false, Resolved = null }`
- **AND** a naive consumer iterating `arg.IsPath` does not see this synth
  arg
- **AND** a consumer specifically checking `arg.IsCwdAttribution` can
  detect "this clause's cwd context is unknown"

#### Scenario: pushd does not propagate attribution in v0.1

- **GIVEN** the input `pushd /target && cmd`
- **WHEN** the parser produces the AST
- **THEN** the cmd clause has no synthetic `IsCwdAttribution` arg
- **AND** the pushd clause's first non-flag arg is still classified as a
  path (`/target` has `IsPath = true`) so hard-deny rules can fire on it

### Requirement: Command substitution becomes opaque token; arithmetic and complex param expansion become unparseable

Command substitution `$(cmd)` and backtick `` `cmd` `` SHALL be collapsed
into a single `Arg` token with `Kind = DynamicSkip`, `IsPath = false`,
`Resolved = null`. The surrounding clause SHALL parse normally. Boundary
detection SHALL handle nested quotes, nested substitutions, and escape
sequences via a shared opaque-region scanner.

Arithmetic expansion `$((expr))` and complex parameter expansion
`${var//pattern/replacement}` SHALL cause `ParsedCommand.IsUnparseable =
true` with a reason naming the construct (per SPEC §1 non-goals).

#### Scenario: $() collapses to DynamicSkip arg

- **GIVEN** the input `rm $(find /tmp -name "*.bak")`
- **WHEN** the parser produces the AST
- **THEN** the rm clause's args include `Arg{ Raw = "$(find /tmp -name
  \"*.bak\")", Kind = DynamicSkip, IsPath = false, Resolved = null }`
- **AND** the rest of the clause (verb, redirects, other args) parses
  normally

#### Scenario: Backticks behave like $()

- **GIVEN** the input `kill `pgrep nginx``
- **WHEN** the parser produces the AST
- **THEN** the kill clause has one `Arg` with `Kind = DynamicSkip`
  containing the backtick substitution

#### Scenario: $((expr)) marks unparseable

- **GIVEN** the input `echo $((1 + 2))`
- **WHEN** the parser produces the AST
- **THEN** `ParsedCommand.IsUnparseable = true`
- **AND** `ParsedCommand.UnparseableReason` references "arithmetic
  expansion"

#### Scenario: ${var//pat/repl} marks unparseable

- **GIVEN** the input `echo ${PATH//:/\n}`
- **WHEN** the parser produces the AST
- **THEN** `ParsedCommand.IsUnparseable = true`
- **AND** `ParsedCommand.UnparseableReason` references "complex parameter
  expansion"

#### Scenario: Compound with a substitution still routes hard-deny on visible clauses

- **GIVEN** the input `cd /repo && rm /etc/important && cmd $(echo extra)`
- **WHEN** the parser produces the AST
- **THEN** all three clauses are parseable
- **AND** the rm clause's `Args` includes `Arg{ Raw = "/etc/important",
  Resolved = "/etc/important", Kind = Literal, IsPath = true }`
- **AND** the cmd clause's `Args` includes `Arg{ Kind = DynamicSkip,
  IsPath = false }` for the `$(echo extra)` portion

### Requirement: Glob and DynamicSkip carry distinct directory-extraction signals

The parser SHALL classify tokens with glob metacharacters (`*`, `?`, `[`)
in a path-arg slot as `Kind = Glob`, `IsPath = true`, `Resolved = null`.
The parser SHALL classify tokens with unresolved env-var references
(other than `$HOME`) or resolver throws in a path-arg slot as
`Kind = DynamicSkip`, `IsPath = false`, `Resolved = null`.

#### Scenario: Glob carries IsPath=true so consumers see the covering directory

- **GIVEN** the input `rm /tmp/*.bak`
- **WHEN** the parser produces the AST
- **THEN** the rm clause has `Arg{ Raw = "/tmp/*.bak", Kind = Glob,
  IsPath = true, Resolved = null }`
- **AND** consumers can apply `Path.GetDirectoryName(arg.Raw)` to obtain
  `/tmp`

#### Scenario: Unresolved env var carries IsPath=false so consumers prompt

- **GIVEN** the input `rm $UNRESOLVED/foo`
- **WHEN** the parser produces the AST
- **THEN** the rm clause has `Arg{ Raw = "$UNRESOLVED/foo", Kind =
  DynamicSkip, IsPath = false, Resolved = null }` (matching SPEC §12 example)
- **AND** consumers iterating `arg.IsPath` see no path
- **AND** route to safe-fail prompt

### Requirement: bash -c recursion overflow signals via outer ParsedCommand.IsUnparseable

When `bash -c` (or `sh -c`) chains nest deeper than 5, the parser SHALL
set the outer `ParsedCommand.IsUnparseable = true` with a reason naming
"bash -c recursion depth exceeded (>5)". Clauses parsed up to depth 5 MAY
appear in `Clauses` for diagnostic value. The `Clause` record SHALL NOT
carry an `IsUnparseable` field in v0.1.

#### Scenario: 6-level bash -c nesting marks outer unparseable

- **GIVEN** an input with `bash -c` chained 6 levels deep
- **WHEN** the parser produces the AST
- **THEN** `ParsedCommand.IsUnparseable = true`
- **AND** `ParsedCommand.UnparseableReason` matches `"bash -c recursion
  depth exceeded (>5)"`
- **AND** consumers route safe-fail per SPEC §11

### Requirement: Corpus location is the test-project subdirectory

The canonical corpus location SHALL be
`tests/ShellSyntaxTree.Tests/Corpus/bash/*.json`, with `<None Update
CopyToOutputDirectory="PreserveNewest" />` so the corpus runner reads
from `AppContext.BaseDirectory` per SPEC §13's runner code example. The
PII audit SHALL operate on this same directory.

#### Scenario: CorpusRunnerTests reads from the test bin output

- **GIVEN** corpus JSONs under `tests/ShellSyntaxTree.Tests/Corpus/bash/`
- **WHEN** the test project is built
- **THEN** the JSONs appear under `tests/ShellSyntaxTree.Tests/bin/Release/net10.0/Corpus/bash/`
- **AND** `CorpusRunnerTests` enumerates them via
  `Path.Combine(AppContext.BaseDirectory, "Corpus", "bash")`

### Requirement: tar and docker -v fall through to default rule in v0.1

In v0.1.0-alpha, `tar` SHALL use the default per-verb rule (all non-flag
positionals classified as paths; no action-flag awareness). The
`docker -v "/host:/container"` argument SHALL be treated as a single
literal arg with `IsPath = false`. v0.1.x or later MAY upgrade these via
new OpenSpec changes; the limitation is documented in `SPEC.md` §7 and
tracked in GitHub issues.

#### Scenario: tar defaults to all-positionals-are-paths

- **GIVEN** the input `tar -xf archive.tar.gz /target`
- **WHEN** the parser produces the AST
- **THEN** both `archive.tar.gz` and `/target` have `IsPath = true`
- **AND** the SPEC §7 entry for `tar` documents this v0.1 limitation
  and references the GitHub issue for action-flag awareness

#### Scenario: docker -v keeps the colon-joined value as a single literal

- **GIVEN** the input `docker run -v /host/data:/container/data nginx`
- **WHEN** the parser produces the AST
- **THEN** the value `/host/data:/container/data` appears as one `Arg`
- **AND** that `Arg` has `IsPath = false`
- **AND** the SPEC §7 entry for `docker` documents this v0.1 limitation
  and references the GitHub issue for colon-split handling

## Notes

- **Spec stub.** This change populates the initial `shellsyntaxtree-v0.1`
  capability spec. Path C light adoption: the spec deliberately stays at
  one root document until v0.2 PowerShell decomposition. Root `SPEC.md`
  remains the canonical human-readable contract.
- **Future decompositions.** `bash-lexer`, `path-resolver`, `verb-tables`,
  `cd-attribution`, `subshells-and-bash-c`, `parser-anomalies`, and
  `corpus-contract` are reserved capability names for v0.2 split.
