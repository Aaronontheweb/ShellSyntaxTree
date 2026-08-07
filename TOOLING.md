# Tooling Inventory — ShellSyntaxTree

What's available to agents working in this repo, how to access it, and what
it enables. Update this file when tooling actually changes; keep it concise.

## Runtime and Build

| Tool | Access | Purpose |
|---|---|---|
| .NET SDK | pinned by `global.json` (10.x line, `rollForward: major`) | build, test, pack, restore |
| `dotnet` CLI | shell | every build action |
| Local .NET tools | `.config/dotnet-tools.json` (run `dotnet tool restore`) | currently `incrementalist` (selective rebuilds) |

DocFX and `build.ps1` (Akka template release-notes injector) were intentionally
removed — this repo does not need them.

## Test Stack

| Tool | Access | Purpose |
|---|---|---|
| xUnit 2.9.x | NuGet via Central Package Management | unit + corpus tests |
| `Microsoft.NET.Test.Sdk` 17.11+ | NuGet | test discovery |
| `coverlet.collector` | NuGet | code coverage |

The corpus runner (SPEC §13) is a single `[Theory] [MemberData]` test that
enumerates every `tests/ShellSyntaxTree.Tests/Corpus/<shell>/*.json`
directory, routes each entry to the matching parser (`bash/` → `BashParser`,
`powershell/` → `PwshParser`), and asserts it parses to its declared
`expected` AST. `PwshOracleTests` is the SPEC.POWERSHELL.md §13 validation
gate — it feeds every PowerShell corpus input to real `pwsh` and enforces
the oracle matrix.

`tests/ShellSyntaxTree.Tests/DesignCorpus/v0.3/` is a separate,
pre-implementation contract corpus. Its focused validator rejects unknown JSON
members, checks syntax/occurrence references and security invariants, and
compares each recorded `current` result with the real v0.2 parser. Design cases
move into the executable `Corpus/<shell>/` only when the corresponding v0.3 API
and parser slice exists. The PII audit scans both corpus trees.

### PwshCorpusTool

`tools/PwshCorpusTool` is the PowerShell corpus authoring aid
(SPEC.POWERSHELL.md §13). It is a dev-only console app (not packed).

| Command | Purpose |
|---|---|
| `dotnet run --project tools/PwshCorpusTool -- generate` | Regenerate every `Corpus/powershell/NNN_slug.json` from the curated `CorpusManifest`. Run after any parser change that shifts PowerShell AST output. |
| `dotnet run --project tools/PwshCorpusTool -- check "<command>"` | Print the parser's expected-AST JSON block for a command beside the real-`pwsh` oracle verdict — the fastest way to author or debug a binding-category entry. |

The curated inputs live in `tools/PwshCorpusTool/CorpusManifest.cs`; the
`expected` AST is generated from `PwshParser`, and `PwshOracleTests`
independently validates every input against real `pwsh`. The manifest owns
every checked-in PowerShell entry. `IncludeStructure` opts selected entries
into exact `syntax` / `commands` expectations, while
`IncludeOptionalAssertions` preserves explicit false/null security assertions
for cases that require them. A safe generator change MUST regenerate to a
temporary directory and produce an exact directory diff before rewriting the
checked-in corpus.

## Source Control and CI

| Tool | Access | Purpose |
|---|---|---|
| `git` | shell | everything |
| `gh` CLI | shell | issues, PRs, releases, tag pushes |
| GitHub Actions | `.github/workflows/` | `pr_validation.yml` (build + test + pack on PR/push), `publish_nuget.yml` (pack + push + release on `v*.*.*` tags) |
| GitHub Dependabot | `.github/dependabot.yml` | NuGet bumps |

## NuGet

| Tool | Access | Purpose |
|---|---|---|
| `NuGet.Config` | repo root | feed configuration (currently `nuget.org`) |
| `dotnet pack` | shell | produces `.nupkg` and `.snupkg` (symbol package) |
| `dotnet nuget push` | CI only (`NuGet/login@v1` OIDC) | publishes to `nuget.org` on tag |
| Central Package Management | `Directory.Packages.props` | single source of truth for package versions |

### NuGet trusted publishing

`publish_nuget.yml` uses
[trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
— no long-lived API key in repo secrets. The workflow exchanges its
OIDC token for a short-lived API key via the `NuGet/login@v1` action.

Required configuration (one-time, on `nuget.org`):

1. Sign in to nuget.org → **Account → Trusted Publisher Policies** →
   **Add new policy**.
2. Publisher: GitHub Actions. Repository owner: `Aaronontheweb`.
   Repository: `ShellSyntaxTree`. Workflow file: `publish_nuget.yml`.
   Environment: `nuget`. Optional package-name pattern:
   `ShellSyntaxTree*`.

Required configuration (one-time, in repo settings):

1. **Settings → Environments → New environment** → name `nuget`.
   Optional protection: restrict deployment to tags matching
   `v*.*.*`.
2. **Settings → Secrets and variables → Actions → New repository
   secret** — `NUGET_USER` set to the nuget.org account username
   that owns the package. The legacy `NUGET_KEY` secret can be
   deleted once trusted publishing is verified.

The workflow's `permissions: id-token: write` and `environment: nuget`
declarations are required for the OIDC exchange to succeed.

## Source-Level Conventions

- `Directory.Build.props` enforces: `Nullable=enable`, `LangVersion=latest`,
  `TreatWarningsAsErrors=true`. Any new project inherits these.
- Solution format is `.slnx` (`ShellSyntaxTree.slnx`).
- Every `.cs` file under `src/` and `tests/` carries a copyright header.
  Use `scripts/Add-FileHeaders.ps1` to add them; CI runs the same script
  with `-Verify` and fails the build on missing headers.

### Copyright header script

| Command | Purpose |
|---|---|
| `pwsh ./scripts/Add-FileHeaders.ps1` | Add Aaron Stannard copyright headers to all `.cs` files missing them under `src/` and `tests/` |
| `pwsh ./scripts/Add-FileHeaders.ps1 -WhatIf` | Preview which files would be modified |
| `pwsh ./scripts/Add-FileHeaders.ps1 -Verify` | CI mode: exit 1 if any file is missing a header |

The script auto-detects file creation year via `git log --diff-filter=A`
and falls back to the current year for new files.

## External Resources

| Resource | Access | Purpose |
|---|---|---|
| Netclaw repo | local at `/home/petabridge/repositories/stannardlabs/netclaw` (also `github.com/netclaw-dev/netclaw`) | reference for the consumer contract; read `src/Netclaw.Security/ShellApprovalSemantics.cs` and `ShellTokenizer.cs` to see the surface ShellSyntaxTree replaces |
| Daemon dogfood logs (Netclaw) | `~/.netclaw/logs/daemon-*.log` (operator's machine; not in CI) | corpus seed source — strict sanitization required (SPEC §14) before any entry lands in the public corpus |

## dotnet-skills Marketplace

Install-once skill bundle that surfaces curated .NET guidance. Prefer
retrieval-led reasoning (open the skill) over pretraining for any non-trivial
.NET work. Routing for this repo:

| Topic | Skill |
|---|---|
| Project layout / `.slnx` / `Directory.Build.props` / SourceLink | `dotnet-skills:project-structure` |
| Central Package Management, `dotnet add/remove` | `dotnet-skills:package-management` |
| Modern C# (records, pattern matching, value objects, Span/Memory) | `dotnet-skills:csharp-coding-standards` |
| Public API stability and extend-only design | `dotnet-skills:csharp-api-design` |
| Type/perf design (sealed classes, readonly structs, etc.) | `dotnet-skills:csharp-type-design-performance` |
| xUnit + corpus discipline | (no dedicated skill; corpus contract is in SPEC §13) |
| Snapshot/golden testing | `dotnet-skills:snapshot-testing` |
| Reward-hacking detector after code changes | `dotnet-skills:slopwatch` |
| Coverage / risk hot-spots | `dotnet-skills:crap-analysis` |
| Decompile a NuGet to confirm behavior | `dotnet-skills:ilspy-decompile` |
| Marketplace publishing workflow (skills, not this lib) | `dotnet-skills:marketplace-publishing` |

Skills are advisory. Open the skill, apply the parts that fit ShellSyntaxTree,
note conflicts back here.

## Specialist Agents

Available via the `Agent` tool:

- `dotnet-skills:dotnet-concurrency-specialist` — only relevant if the
  parser ever grows mutable state (currently it shouldn't).
- `dotnet-skills:dotnet-performance-analyst` — for the "fast enough"
  budget (~1 ms typical) if it's ever in doubt.
- `dotnet-skills:dotnet-benchmark-designer` — if benchmarks become
  needed (SPEC explicitly defers performance tuning).
- `pr-review-specialist` — coordinated PR review, especially for the
  initial public-API-locking PR.

## Helper Skills (Claude Code)

| Skill | When to use |
|---|---|
| `/init` | (already done — generated CLAUDE.md companion) |
| `/commit` | Branch-and-commit hygiene (creates feature branch off `dev` if needed) |
| `/pr` | Open PR against `dev` |
| `/review-pr` | Pre-flight review before requesting human review |
| `/security-review` | Targeted security review — relevant for this library |
| `/generate-image` | HCTI-backed image generation; used to mint the NuGet package icon |

### OpenSpec authoring skills

Light Path-C OpenSpec adoption: change proposals capture decision
rationale; specs decompose to per-capability files in v0.2 work. Skills
copied from `petabridge/sdkbin` and refreshed by `openspec init`.

| Skill | When to use |
|---|---|
| `/opsx:propose` | Create a new change with proposal + design + tasks + specs deltas in one pass |
| `/opsx:explore` | Think-partner mode for investigating a problem before drafting a change |
| `/opsx:apply` | Implement the tasks of an active change |
| `/opsx:archive` | Archive a completed change (move to `openspec/changes/archive/<date>-<name>/`) |
| `openspec-new-change`, `openspec-continue-change`, `openspec-ff-change`, `openspec-verify-change`, `openspec-bulk-archive-change`, `openspec-sync-specs`, `openspec-onboard` | Granular flows for the same workflow; `propose`/`apply`/`archive` are usually enough |

OpenSpec CLI (`openspec init`, `openspec list`, `openspec validate
<change>`, `openspec archive <change>`) is installed at the user level;
verify with `openspec --version`. Skill drift between this repo and
upstream sdkbin: acceptable in v0.1; tracked as a NEXT-bucket task
(periodic sync or marketplace publication).

## Image Generation (HCTI)

Used to mint the package icon. Output goes to `assets/icon.png` (or similar)
and is wired into `Directory.Build.props` via `PackageIcon`. The icon is the
only graphical artifact this repo needs.

## Working Assumptions

- Single maintainer (Aaron Stannard). No org-wide review SLA.
- CI is the only place that publishes packages; local `dotnet nuget push` is
  not used.
- Dogfood log sanitization happens on the operator's machine before any
  corpus entry is committed. CI enforces a regex audit; it does not
  sanitize.
- No external API/secret credentials are required to build or test.
  Publishing to nuget.org requires the `NUGET_KEY` repo secret.
