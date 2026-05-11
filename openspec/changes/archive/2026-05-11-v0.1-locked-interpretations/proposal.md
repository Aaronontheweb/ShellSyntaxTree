# v0.1 locked interpretations

## Why

`SPEC.md` is the locked v0.1 contract for ShellSyntaxTree, but a 30-minute
planning interview surfaced eight clauses where the SPEC was either silent,
abbreviated, or internally contradictory. Resolving them in code without
capturing the rationale would create unprovenanced "magic" that future
agents (or this one, in v0.2) would have to re-derive.

This change records the eight resolutions, the trade-offs considered, and
the alternatives rejected. It also bootstraps the OpenSpec change-proposal
workflow for ShellSyntaxTree at the lightest level (Path C — light adoption
per the planning notes), aligning governance with netclaw's process without
forcing a full capability-spec decomposition until v0.2 PowerShell drives
that refactor.

## What Changes

### 1. `Arg.IsCwdAttribution` becomes part of the public API

SPEC §9 mandates a flag on synthetic cwd-attribution args but §2's record
enumeration didn't list it. Without the flag, consumers can't distinguish
"a path the user typed" from "a path we inferred from a preceding `cd`."

**Decision:** Add `public bool IsCwdAttribution { get; init; }` to `Arg`
(default `false`). Additive to §2's locked surface; consumers ignore it by
default.

**Rejected alternatives:**

- **Side-channel `Clause.AttributedCwd: string?`.** Cleaner separation, but
  the safety failure mode is *under-restriction* (a careless consumer
  forgets the side-channel and misses the cwd path). Choosing a default
  that prompts-too-often beats one that silently allows.
- **Both fields.** Doubles the source of truth without a clear winner.

### 2. Command substitution + arithmetic + complex param expansion

SPEC §1 lists arithmetic and `${var//pat/repl}` as non-goals. SPEC §11's
`IsUnparseable` triggers do **not** include `$()` or backticks — that
silence is either a deliberate gap or an oversight.

**Decision:** Three rules, applied uniformly:

- `$(cmd)` and backtick `` `cmd` `` → the substitution is collapsed into a
  single `Arg{ Kind = DynamicSkip, IsPath = false }` token. The rest of the
  clause parses normally so hard-deny rules can still fire on visible parts.
- `$((expr))` → `ParsedCommand.IsUnparseable = true` (matches §1 non-goals).
- `${var//pat/repl}` → `ParsedCommand.IsUnparseable = true` (matches §1).

Boundary tracking for `$()` lives in a shared `OpaqueRegionScanner` so the
same machinery serves PowerShell's `$( … )` in v0.2.

**Rejected alternatives:**

- **All four → `IsUnparseable`.** Simpler but routes routine LLM commands
  (`cd $(git rev-parse --show-toplevel)`, `kill $(pgrep nginx)`) to user
  prompt instead of letting hard-deny rules auto-decide on the surrounding
  structure. Loses the `rm /etc/important && cmd $(echo extra)` case where
  the dangerous clause is auto-denyable.
- **Token-level skip for all four.** Pretends arithmetic and complex
  param expansion are parseable when SPEC §1 explicitly says they aren't.

### 3. Glob vs `DynamicSkip` precedence in path-arg slots

SPEC §8 step 4 says glob tokens get `Kind = Glob, Resolved = null`. Step 6
says tokens get `DynamicSkip` "when … contains glob metachars AND the
resolver was asked for an absolute resolution." These read as overlapping.

**Decision:** Different signals to the consumer.

- Glob in a path-arg slot → `Kind = Glob, IsPath = true, Resolved = null`.
  Consumer can apply the SPEC §8 covering-directory heuristic
  (`Path.GetDirectoryName(arg.Raw)`) to extract `/tmp` from `/tmp/*.bak`.
- Unresolved env var (other than `$HOME`) or resolver-throws →
  `Kind = DynamicSkip, IsPath = false, Resolved = null`. No useful directory
  signal; consumer routes to safe-fail prompt.

Matches the `rm $UNRESOLVED/foo` example in SPEC §12 verbatim, and adds the
symmetric rule for globs that the SPEC didn't write out.

**Rejected alternatives:**

- **Uniform `DynamicSkip`.** Simpler but loses the covering-directory
  signal — the consumer can't auto-allow `rm /tmp/*.bak` even when `/tmp`
  is in their allowlist.
- **Glob with `IsPath = false`.** Hides globs from the default
  iterate-paths loop; failure mode is "miss a `rm /etc/*` because the
  iteration never saw the token." Unsafe by SPEC's safety bias.

### 4. `bash -c` recursion overflow site

SPEC §10 says "mark the deepest clause as `IsUnparseable = true`" but
`Clause` has no `IsUnparseable` field — only `ParsedCommand` does.

**Decision:** When `bash -c` nesting exceeds depth 5, set
`ParsedCommand.IsUnparseable = true` with reason `"bash -c recursion depth
exceeded (>5)"`. Clauses parsed up to depth 5 may still appear in `Clauses`
for diagnostic value, but consumers safe-fail per §11. No
`Clause.IsUnparseable` field added in v0.1.

**Rejected alternatives:**

- **Add `Clause.IsUnparseable`.** Would let parser surface partial
  failures, but expands the public surface for one (rare, hostile-input)
  use case. Defer until a real second use case asks for it.
- **Silent truncation.** Drops information on hostile input — exactly what
  safe-fail discipline is designed to prevent.

### 5. `pushd` / `popd` attribution

SPEC §6.2 lists `pushd` / `popd` as `CwdVerbs`; SPEC §9's attribution rules
are written in terms of `cd`. The `pushd` / `popd` semantics genuinely need
a directory stack to model correctly.

**Decision:** Only `cd` and `chdir` propagate attribution in v0.1. `pushd`
and `popd` parse as CwdVerbs (their first non-flag positional is
path-classified, so a hard-deny on `/etc/*` still fires for `pushd /etc`)
but they don't propagate. A subsequent clause after `pushd /target` gets
no synthetic IsCwdAttribution arg.

A GitHub issue is filed for the option-C upgrade ("model directory stack
properly") — likely v0.1.x or v0.2.0 with PowerShell, where Push-Location
is used heavily.

**Rejected alternatives:**

- **Treat `pushd` like `cd`; clear attribution on `popd`.** Handles the
  single-level case but breaks on stacked use — `cmd2` after a balancing
  `popd` should inherit the prior `pushd`'s target, but option-B clears
  attribution. Worse than honest "no attribution" because option-B *claims*
  to track state and gets it wrong.
- **Model the stack now.** Correct but ~50–80 LOC of state tracking +
  subshell-aware push/pop + ~10 corpus entries. Speculative work for a
  rare-in-LLM-output construct. Deferred to v0.1.x once dogfood corpus
  surfaces real `pushd` / `popd` usage, or to v0.2 when PowerShell's
  Push-Location forces the issue.

### 6. `cd $VAR && cmd` propagation

SPEC §9 covers `cd /literal && cmd` cleanly but is silent on what happens
when the cd target is `DynamicSkip`.

**Decision:** Each subsequent clause gets a synthetic
`Arg{ IsCwdAttribution = true, Kind = DynamicSkip, IsPath = false,
Resolved = null }` appended. Naive consumers (iterating `IsPath = true`)
don't see it; consumers that specifically check `IsCwdAttribution` can
detect "this clause's cwd context is unknown" and elevate to
user-prompt rather than treating it like a default-cwd command.

This preserves the cwd-uncertainty signal that `cd $REPO_DIR &&
rm -rf node_modules` is legitimately ambiguous about — `$REPO_DIR` could
be `/etc` for all we statically know.

**Rejected alternatives:**

- **No attribution arg.** Subsequent clauses look identical to "no `cd`
  in the compound" — the consumer can't distinguish them. Silent loss of
  signal.
- **Whole command `IsUnparseable`.** Too coarse; this idiom is
  pervasive in agent output. Routes everything to user prompt and defeats
  the gate's auto-decide purpose.

### 7. Corpus location

SPEC §13 prose says `tests/Corpus/bash/*.json`; the inline runner code
example uses `AppContext.BaseDirectory` (which resolves to the test bin
output). `CLAUDE.md` and `TOOLING.md` say `tests/ShellSyntaxTree.Tests/Corpus/bash/`.

**Decision:** Canonical corpus location is
`tests/ShellSyntaxTree.Tests/Corpus/bash/*.json`, with `<None Update
CopyToOutputDirectory="PreserveNewest" />` so the runner reads from
`AppContext.BaseDirectory` per the §13 example. Aligns with the
constitution and existing runner code; SPEC §13 prose updated in PR 6 to
match.

**Rejected alternatives:**

- **Top-level `tests/Corpus/bash/`** — would only matter if a second test
  project ever consumes the same corpus. Overkill for v0.1.
- **Embedded resources.** Harder to author and diff in PRs.

### 8. `tar` and `docker -v` handling for v0.1

SPEC §7 says tar's path roles depend on the action flag, and lists
`docker -v` in `FlagsWithValue` without specifying how to treat the
colon-joined volume mount.

**Decision:** Default rule for both in v0.1.

- `tar`: all non-flag positionals are paths (no action-flag awareness).
  Most security gates check "is the archive going somewhere sensitive?"
  and that question stays answerable.
- `docker -v "/host:/container"`: a single literal arg with `IsPath = false`.
  The colon-joined form is structurally different from a plain path and we
  don't try to split it.

GitHub issues filed for both (action-flag-aware `tar` paths, and `docker
-v` colon-split with Windows drive-letter handling). Targets: v0.1.x.

**Rejected alternatives:**

- **Implement properly now.** Adds ~half a PR of work to PR 4. Not
  speculative (real consumer pain), but waits for a corpus entry that
  surfaces it.

## Capabilities

### New Capabilities

- `shellsyntaxtree-v0.1`: bootstrap capability spec for the v0.1 contract.
  Initially a stub deferring to root `SPEC.md`; full decomposition into
  per-section capability specs (`bash-lexer`, `path-resolver`,
  `verb-tables`, etc.) deferred until v0.2 PowerShell work drives it.

### Modified Capabilities

None. v0.1.0-alpha is the first release.

## Impact

- `src/ShellSyntaxTree/Arg.cs`: new public field `IsCwdAttribution`.
- `src/ShellSyntaxTree/`: full public API surface bootstrapped to lock
  SPEC §2/§3 in PR 1 of the v0.1.0-alpha implementation plan.
- `tests/ShellSyntaxTree.Tests/PublicApiSnapshotTests.cs`: reflection-based
  snapshot of the locked surface; fails loud on any drift.
- `SPEC.md` §2/§3 (PR 1), §10 (PR 5), §13 (PR 6), §1/§5/§8/§11 (PRs 2–4):
  edits applied in same PR as the matching implementation.
- `openspec/`: scaffolded; this change records v0.1 decisions; subsequent
  spec edits go through new change proposals.
- `.claude/skills/openspec-*`: copied from sdkbin so the change-proposal
  workflow has tooling support.

## Security and operational impact

- **Security defaults bias preserved.** Every interpretation favors the
  failure mode of "prompt the user when we could allow" over "silently
  allow." The asymmetry between option A and B in interpretation 3 (Glob
  vs DynamicSkip) was decided on this exact basis.
- **No public-API surface mutation beyond the `IsCwdAttribution`
  addition.** That field is additive and defaults to `false`; consumers
  ignore it by default.
- **No native dependencies introduced.** AOT/trim friendliness preserved;
  `IsAotCompatible=true` set on the library project.
- **No execution semantics changes** — the parser remains pure analysis.
  No new code paths touch the filesystem, run subprocesses, or call out to
  external services.
- **PII audit unchanged.** Any future corpus entries seeded from dogfood
  logs still go through the SPEC §14 sanitization workflow, gated by the
  `[Fact]`-level audit (added in PR 6).
