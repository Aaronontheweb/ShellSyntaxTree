# ShellSyntaxTree Agent Constitution

This file is the repository's stable agent constitution. Keep it small. Keep
it durable. Keep it routing-focused. Volatile detail belongs in the
referenced repo-owned artifacts, not here.

## Authority and Scope

- You are authorized to plan, design, implement, test, document, and
  release ShellSyntaxTree v0.x.
- Default to the smallest safe change that advances the v0.1 acceptance
  criteria in `SPEC.md` §17.
- Prefer explicit trade-offs over hidden complexity. Bias toward marking
  inputs `DynamicSkip` / `IsUnparseable` over guessing — this library
  feeds security gates.

## Read First (in this order)

1. `SPEC.md` — the locked v0.1 contract (public API, AST, grammar, verb
   tables, resolver, corpus rules). **The contract.**
2. `PROJECT_CONTEXT.md` — what this is, who consumes it, scope discipline.
3. `TOOLING.md` — what's available and how to access it.
4. `IMPLEMENTATION_PLAN.md` — current NOW / NEXT / LATER work.
5. The relevant `dotnet-skills:*` skill for the .NET concern at hand
   (see `TOOLING.md` routing table).

When `SPEC.md` and any other artifact disagree, **`SPEC.md` wins** — fix
the other artifact in the same change.

## Required Task Routing (MODE)

Pick a MODE before acting. Each MODE has different signals, expected
outputs, and definition-of-done.

### MODE=build

**Use when:** implementing parser logic, lexer, resolver, verb tables,
AST records, options, or any production code under `src/ShellSyntaxTree/`,
plus the unit tests that exercise it.

**Signals:** changes under `src/`, non-corpus changes under `tests/`,
public-API surface changes (rare — requires bumping toward v0.2+).

**Expected outputs:**

- code under `src/ShellSyntaxTree/`
- unit tests under `tests/ShellSyntaxTree.Tests/` (NOT corpus JSON)
- `dotnet build` and `dotnet test` both pass locally
- Public API surface matches `SPEC.md` §2 exactly (any drift is a bug)

**Definition of done:** see Universal DoD below, plus: every behavior
change is covered either by a unit test or a corpus entry.

### MODE=corpus

**Use when:** authoring, sanitizing, or curating JSON entries under
`tests/ShellSyntaxTree.Tests/Corpus/bash/*.json`. The corpus is the
acceptance contract — corpus work is materially different from code work
(JSON shape, sanitization rules, PII audit).

**Signals:** changes under `tests/.../Corpus/`, references to dogfood
logs, requests like "add a case for `chmod`", "seed from logs".

**Expected outputs:**

- new or updated `*.json` corpus entries matching the SPEC §13 schema
- entries cover the SPEC §13 category coverage targets
- if seeded from logs: every sanitization rule in SPEC §14 is applied,
  manually reviewed, and the entry is committed only after PII audit
  would pass on it

**Definition of done:** see Universal DoD, plus:

- the PII audit scan (regex rules from SPEC §14) finds zero hits
- every new entry parses to the declared `expected` AST when run through
  `BashParser` (i.e., implementation must already cover it, OR this entry
  is intentionally a `[Theory]` failure that pins a NOW item in the
  implementation plan — note the link in the entry's `notes` field)

### MODE=release

**Use when:** version bumps, RELEASE_NOTES.md updates, tagging,
NuGet publish flow changes.

**Signals:** changes to `RELEASE_NOTES.md`, `Directory.Build.props`
`VersionPrefix`/`VersionSuffix`, `.github/workflows/publish_nuget.yml`,
or a request to "tag" / "ship" / "cut a release".

**Expected outputs:**

- semantic version that matches SPEC §15 progression rules
  (`v0.1.x` additive, `v0.2.0` for first PowerShell parser, etc.)
- `RELEASE_NOTES.md` updated with the new section in the existing format
- on tag push, `publish_nuget.yml` produces a `.nupkg` matching
  `VersionPrefix`/`VersionSuffix` and pushes to nuget.org

**Definition of done:** see Universal DoD, plus:

- the tag triggers the publish workflow and the package appears on
  nuget.org
- for v0.1.0-alpha specifically: SPEC §17 acceptance #1-#8 all hold

## Discovery Rules

Before adding code or a test, discover in this order:

1. Is there a SPEC clause that already specifies the behavior? Implement
   to that. Do not invent shape that disagrees with the SPEC.
2. Is there an existing corpus entry that pins the expected AST for this
   case? Make the implementation match the corpus.
3. Is there a `dotnet-skills:*` skill for the .NET concern? Open it; apply
   the parts that fit; note conflicts.
4. Is the construct in SPEC §1 / §18 non-goals? If yes, mark
   `IsUnparseable` and stop.

If any of (1)-(4) conflict, **fix the conflict before implementing**.

## Universal Quality Bar

- **API surface is locked.** Public types in `SPEC.md` §2 do not change
  without a deliberate version bump. Internal types are free.
- **No silent fallbacks.** If a token can't be safely classified, mark
  `DynamicSkip`. If a construct can't be parsed, mark `IsUnparseable`.
  Do not guess. Consumers can always relax — they can't un-execute a
  command we falsely classified safe.
- **No native dependencies.** Multi-target `netstandard2.0` and `net8.0`;
  AOT-trim friendly.
- **`TreatWarningsAsErrors=true`** is enforced repo-wide via
  `Directory.Build.props`. Don't suppress warnings to make a build pass —
  fix the cause.
- **No `Thread.Sleep`/`Task.Delay` in tests** to wait for conditions.
  Tests should be deterministic; the parser is synchronous.
- **Comments: skip noise, keep signal.** Don't narrate code that
  identifiers already explain. Do explain *why* a verb is in (or absent
  from) `BashArity` / `FileVerbs`, *why* a token marks `DynamicSkip`,
  *why* a fallback is or isn't safe in this context.
- **No stray sample apps, helper scripts, or "interesting" files.** This
  repo ships one library and its tests. If a helper is genuinely needed,
  put it under `tests/` or a clearly-scoped `tools/` folder and document
  it in `TOOLING.md`.

## Definition of Done (universal)

A change is done when **all** of these hold:

- behavior aligns with `SPEC.md` (or `SPEC.md` was updated in the same
  change with explicit justification)
- `dotnet build -c Release` is clean
- `dotnet test  -c Release` passes (unit + corpus)
- copyright headers present on every new/modified `.cs` file —
  `pwsh ./scripts/Add-FileHeaders.ps1 -Verify` exits 0 (CI enforces this)
- public API surface still matches SPEC §2 exactly
- corpus entries that touch the changed code path still pass
- if MODE=corpus: PII audit regex finds zero hits
- if MODE=release: tag-driven publish workflow succeeds end-to-end
- `IMPLEMENTATION_PLAN.md` is updated (item moved, completed, or
  parked) so the plan reflects reality

## Validation Expectations

Run before declaring done:

```bash
dotnet tool restore
dotnet build  -c Release
dotnet test   -c Release
dotnet pack   -c Release -o ./bin/nuget   # only on release-shaped changes
```

For any code change that touches public API:

- diff `src/ShellSyntaxTree/` headers against `SPEC.md` §2 — they must
  match field-for-field.

For corpus changes:

- the PII audit test (a `[Fact]` that scans `tests/.../Corpus/bash/*.json`
  for the SPEC §14 forbidden patterns) must pass.

## Continuous Improvement Rule

- If a workflow or correction repeats twice → extract or refine a skill or
  add a row to `TOOLING.md`.
- If volatile project knowledge surfaces → put it in `PROJECT_CONTEXT.md`
  or `IMPLEMENTATION_PLAN.md`, not here.
- This file should rarely change. Constitution edits are a deliberate act
  — they ripple to every future agent run.
- Compressing a referenced authority (SPEC, dotnet-skills, etc.) into a
  rule here is a *retrieval* operation, not a *memory* operation. If you
  can't write the rule without losing a distinction the source draws,
  drop the rule and link to the source instead.

## Handy Commands

```bash
# Restore and build everything
dotnet tool restore && dotnet restore && dotnet build -c Release

# Run the full test+corpus suite
dotnet test -c Release

# Pack locally to validate package metadata
dotnet pack -c Release -o ./bin/nuget

# Cut a release (manual, then push the tag)
release_version=$(dotnet msbuild src/ShellSyntaxTree/ShellSyntaxTree.csproj -nologo -getProperty:Version)
git tag "$release_version" && git push origin "$release_version"
```
