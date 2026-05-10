# Tasks — v0.1 locked interpretations

The eight interpretations land across PRs 1–6 of the v0.1.0-alpha shipping
plan (`/home/petabridge/.claude/plans/okay-are-you-ready-wise-emerson.md`).
This task list maps each interpretation to its implementing PR(s) and to
the SPEC.md sections that get updated alongside the implementation.

## 1. PR 1 — Public API surface and OpenSpec scaffolding

- [x] 1.1 Bootstrap `src/ShellSyntaxTree/ShellSyntaxTree.csproj`
      (multi-target `netstandard2.0;net8.0`, `IsAotCompatible=true` for
      net8.0)
- [x] 1.2 Implement public types per SPEC §2/§3 verbatim
- [x] 1.3 Add `Arg.IsCwdAttribution: bool` (default `false`) per
      interpretation #1
- [x] 1.4 Bootstrap `tests/ShellSyntaxTree.Tests/ShellSyntaxTree.Tests.csproj`
- [x] 1.5 Author `PublicApiSnapshotTests.cs` — reflection-based snapshot
      of the locked surface (18 facts asserting type kinds, properties,
      enum members, ctor behavior, default values)
- [x] 1.6 Add both projects to `ShellSyntaxTree.slnx`
- [x] 1.7 Bootstrap OpenSpec scaffolding (`openspec/`, change directories,
      this proposal/design/tasks/specs delta)
- [ ] 1.8 Update `SPEC.md` §2/§3 to enumerate `Arg.IsCwdAttribution`;
      annotate `VerbChain.Joined` to use `string.Join(" ", Tokens)` for
      cross-tfm compatibility (was `string.Join(' ', …)`, char overload
      missing on netstandard2.0)
- [ ] 1.9 Update `IMPLEMENTATION_PLAN.md` — mark PR 1 in-progress;
      reference this OpenSpec change
- [ ] 1.10 Update `TOOLING.md` to list installed OpenSpec skills under
       Helper Skills
- [ ] 1.11 Run `pwsh ./scripts/Add-FileHeaders.ps1`; verify with `-Verify`
- [ ] 1.12 `dotnet build -c Release` clean; `dotnet test -c Release`
       all-green
- [ ] 1.13 Commit (signed) and push `pr1-bootstrap`; open PR with
       `gh pr merge --auto --squash` against `dev`
- [ ] 1.14 On merge: archive this OpenSpec change to
       `openspec/changes/archive/2026-05-10-v0.1-locked-interpretations/`

## 2. PR 2 — Lexer + opaque-region scanner (interpretation #2)

- [ ] 2.1 Implement `Internal/Lexing/OpaqueRegionScanner.cs` — finds
      balanced delimiters with quote-aware nesting; configurable open/close
- [ ] 2.2 Implement `Internal/Bash/Lexing/BashLexer.cs` per SPEC §5
- [ ] 2.3 Recognize `$(...)` and `` ` `` regions via the scanner; emit
      `OpaqueSubstitution` token
- [ ] 2.4 Recognize `$((` and `${var//` openers; emit `IsUnparseable`
      sentinel
- [ ] 2.5 Update `SPEC.md` §1 (note that command substitution is marked
      DynamicSkip), §5 (lex rules for opaque regions), §11 (add arithmetic
      and complex-param-expansion to IsUnparseable triggers)
- [ ] 2.6 Open OpenSpec change `bash-lexer-opaque-regions` capturing the
      §1/§5/§11 deltas in their final form

## 3. PR 3 — Verb tables + parser core

- [ ] 3.1 `Internal/Bash/Verbs/BashVerbs.cs` — `BashArity`, `CwdVerbs`,
      `FileVerbs`, `FlagsWithValue` from SPEC §6
- [ ] 3.2 `Internal/Bash/Parsing/BashCommandParser.cs` per SPEC §4
- [ ] 3.3 Compound splitting; verb chain longest-prefix probe; flag/positional
- [ ] 3.4 Heredoc skip (`<<DELIM` / `<<-DELIM`)
- [ ] 3.5 Anomaly safe-fail per SPEC §11
- [ ] 3.6 Wire opaque-substitution tokens into `Arg{ Kind=DynamicSkip }`
      per interpretation #2
- [ ] 3.7 Wire arithmetic/complex-param sentinels into
      `ParsedCommand.IsUnparseable` per interpretation #2

## 4. PR 4 — Resolver + per-verb rules (interpretations #3 + #8)

- [ ] 4.1 `Internal/Resolving/BashResolver.cs` per SPEC §8
- [ ] 4.2 Tilde + `$HOME`; other env vars → `DynamicSkip, IsPath=false`
- [ ] 4.3 `filesystem::/path` strip; glob detection per interpretation #3
      (`Kind=Glob, IsPath=true (in path slot), Resolved=null`)
- [ ] 4.4 Relative-path joining against `WorkingDirectory`; `LooksLikePath`
      heuristic
- [ ] 4.5 `Internal/Bash/Verbs/BashPerVerbRules.cs` per SPEC §7 with
      interpretation #8 fallback for tar (default rule) and docker -v
      (single literal arg, IsPath=false)
- [ ] 4.6 Update `SPEC.md` §7 (note v0.1 limitations + reference issues),
      §8 (rewrite steps 4 & 6 to remove the overlap; explicit IsPath
      asymmetry per interpretation #3)
- [ ] 4.7 File 2 GitHub issues: tar action-flag awareness; docker -v
      colon-split + Windows drive-letter handling
- [ ] 4.8 Open OpenSpec change `path-resolver-rules` for the §7/§8 deltas

## 5. PR 5 — cd attribution + subshells + bash -c (interpretations #4, #5, #6)

- [ ] 5.1 `Internal/Bash/Parsing/CdAttributionContext.cs` (parser-internal
      mutable; output AST stays immutable)
- [ ] 5.2 Only `cd`/`chdir` propagate (interpretation #5); pushd/popd
      parse but don't propagate
- [ ] 5.3 Synthetic `Arg{ IsCwdAttribution=true, … }` appended to
      subsequent clauses; `Kind=DynamicSkip` when cd target is dynamic
      (interpretation #6)
- [ ] 5.4 Subshell `(...)` parsing with attribution stack push/pop
- [ ] 5.5 `bash -c "..."` / `sh -c "..."` recursion (cap at 5)
- [ ] 5.6 Recursion overflow → outer `ParsedCommand.IsUnparseable=true`
      (interpretation #4)
- [ ] 5.7 Update `SPEC.md` §9 (clarify which CwdVerbs propagate; add
      dynamic-cd attribution subsection); §10 (replace "mark the deepest
      clause" wording with "set ParsedCommand.IsUnparseable=true")
- [ ] 5.8 File GitHub issue: "Model pushd/popd directory stack semantics"
- [ ] 5.9 Open OpenSpec change `cd-attribution-clarifications` for the
      §9/§10 deltas

## 6. PR 6 — Corpus completeness + PII audit (interpretation #7)

- [ ] 6.1 Audit corpus categories from SPEC §13; fill to ≥105 entries
- [ ] 6.2 `tests/.../Corpus/AstAssert.cs` — structural equality with
      diffable failures
- [ ] 6.3 `tests/.../Corpus/PiiAuditTests.cs` — `[Fact]` regex scan over
      corpus JSON for SPEC §14 forbidden patterns
- [ ] 6.4 Verify pr_validation runs both via `dotnet test`
- [ ] 6.5 Confirm green on Linux + Windows (path separators)
- [ ] 6.6 Update `SPEC.md` §13 (replace abbreviated path with canonical
      `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json`)
- [ ] 6.7 Open OpenSpec change `corpus-location` for the §13 delta

## 7. Verify

- [ ] 7.1 After all PRs land: `dotnet build -c Release` and `dotnet test
      -c Release` clean on Linux + Windows
- [ ] 7.2 `PublicApiSnapshotTests` still green — no public surface drift
      beyond the locked `IsCwdAttribution` addition
- [ ] 7.3 PII audit passes
- [ ] 7.4 SPEC.md edits consistent with implementations across all PRs

## Status conventions

- `- [ ]` pending
- `- [x]` done
- Subtasks left as `[ ]` while the parent PR is in flight; flipped to
  `[x]` only when the PR merges.
