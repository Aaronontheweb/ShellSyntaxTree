# ShellSyntaxTree

[![NuGet](https://img.shields.io/nuget/v/ShellSyntaxTree.svg)](https://www.nuget.org/packages/ShellSyntaxTree/)

A focused .NET library that parses **bash and PowerShell** command strings
into a structured AST. Purpose-built for tools that need to **reason about
shell commands without running them** — approval gates for LLM-emitted
commands, CI/CD script auditors, sandbox policy generators, audit-log
analytics.

Hand-rolled, AOT-trim friendly, zero native dependencies. Multi-targets
`netstandard2.0` and `net8.0`.

```bash
dotnet add package ShellSyntaxTree --version 0.2.0
```

The `0.2.0` release adds PowerShell support. The latest stable
`0.2.x` package supports bash and PowerShell together. The public surface
documented below tracks the `dev` branch.

## What you get

For an input like `cd /repo && rm /etc/passwd`, ShellSyntaxTree produces:

```mermaid
flowchart TD
    classDef bad fill:#fee,stroke:#b00,stroke-width:2px
    A[cd /repo<br/>📁 /repo] -- "&&" --> B[rm<br/>📁 /etc/passwd<br/>cwd: /repo]
    class B bad
```

A two-clause AST where the second clause's `Args` includes a synthetic
`/repo` attribution arg (so consumers can see "this `rm` is implicitly
operating in `/repo`") *and* `/etc/passwd` is resolved and marked
`IsPath = true`. Hard-deny rules over `/etc/*` fire immediately; no
substring matching, no shelling out, no false positives.

## Things you can do with it

- **Approval gates for AI agents** — given a command emitted by an LLM,
  decide ALLOW / PROMPT / DENY before invoking the shell.
- **CI/CD pipeline audits** — scan shell steps in GitHub Actions /
  Jenkinsfile / Azure Pipelines for writes outside the workspace,
  `curl | bash` from non-allowlisted hosts, hardcoded credential
  echoes.
- **Sandbox / container policy** — derive the minimum-viable volume
  mount set or AppArmor profile from a build script.
- **Pre-commit linters** — flag dangerous patterns (`rm -rf /`,
  `chmod 777 /etc/*`) in shell scripts at commit time.
- **Shell history / audit-log analytics** — ingest `~/.bash_history` or
  `auditd` records into structured form for SIEM-style insights.
- **Documentation / explainers** — convert complex one-liners into
  readable structure for tutorials and runbooks.

The original consumer is
[Netclaw](https://github.com/netclaw-dev/netclaw)'s approval policy;
the library is built to be reusable beyond that.

## Quick start

```csharp
using ShellSyntaxTree;

var parser = new BashParser();
var parsed = parser.Parse("cd /repo && rm /etc/passwd");

if (parsed.IsUnparseable)
{
    // Safe-fail: prompt the user, deny the command, etc.
    Console.WriteLine($"can't model: {parsed.UnparseableReason}");
    return;
}

foreach (var clause in parsed.Clauses)
{
    Console.WriteLine($"{clause.Operator} {clause.Verb.Joined}");

    foreach (var arg in clause.Args.Where(a => a.IsPath))
    {
        var marker = arg.IsCwdAttribution ? "↳ cwd" : "  path";
        Console.WriteLine($"    {marker}: {arg.Resolved}");
    }

    foreach (var redirect in clause.Redirects.Where(r => !r.IsDynamicSkip))
    {
        Console.WriteLine($"    {redirect.Direction}: {redirect.Target}");
    }
}
```

Run that against the example input and you get:

```
None cd
      path: /repo
AndIf rm
    ↳ cwd: /repo
      path: /etc/passwd
```

## Consumer guide

The [consumer guide](./docs/CONSUMER_GUIDE.md) develops the quick start into
a production-oriented algorithm: parser selection, safe-fail handling, command
identity, paths and cwd attribution, redirects, compounds and pipelines, and
PowerShell-specific alias and dynamic-command behavior. It also links to
immutable examples from Netclaw's live approval-gate integration.

## Public API surface

```csharp
namespace ShellSyntaxTree;

public interface IShellParser { ParsedCommand Parse(string command); }
public sealed class BashParser : IShellParser { /* … */ }
public sealed class PwshParser : IShellParser { /* … */ }   // v0.2.0

public abstract record ShellParserOptions { /* HomeDirectory, WorkingDirectory */ }
public sealed record BashParserOptions : ShellParserOptions; // InitialStateMode
public sealed record PwshParserOptions : ShellParserOptions; // InitialStateMode

public sealed record ParsedCommand { /* Source, Clauses, IsUnparseable, … */ }
public sealed record Clause        { /* Operator, Verb, Args, Redirects, Elements, IsSubshell, IsCommandStringWrapped */ }
public sealed record ClauseElement { /* Raw, Value, Role, source span, verb-relative position, path facts */ }
public sealed record VerbChain     { /* Tokens, Joined, CanonicalVerb, IsDynamic */ }
public sealed record Arg           { /* Raw, Resolved, Kind, IsPath, IsCwdAttribution, IsFlag */ }
public sealed record Redirect      { /* Direction, Target, IsDynamicSkip */ }

public enum ArgKind            { Literal, EnvVar, Glob, Tilde, DynamicSkip }
public enum ClauseElementRole  { Verb, Argument, Redirect }
public enum RedirectDirection  { In, Out, Append, ErrOut, ErrAppend }
public enum CompoundOperator   { None, AndIf, OrIf, Sequence, Pipe }
```

Both parsers emit the **same** `ParsedCommand` AST — a consumer walks a
PowerShell parse exactly as it walks a bash one. A Windows `cmd` parser
remains deferred.

Behavioral contract: [`SPEC.md`](./SPEC.md) (bash + shared surface) and
[`SPEC.POWERSHELL.md`](./SPEC.POWERSHELL.md) (PowerShell).

## Samples

Two runnable samples live under [`samples/`](./samples).

### `ShellSyntaxTree.Cli.Sample` — terminal explainer + audit policy

```bash
dotnet run --project samples/ShellSyntaxTree.Cli.Sample -- explain "cd /repo && rm /etc/passwd"
dotnet run --project samples/ShellSyntaxTree.Cli.Sample -- audit "cd /repo && rm /etc/passwd"

# --shell pwsh routes the same explain / audit logic through PwshParser:
dotnet run --project samples/ShellSyntaxTree.Cli.Sample -- explain --shell pwsh "gci C:\logs | rm"
```

`explain` pretty-prints the AST with `[flag]` / `[path]` / `[cwd-attr]` /
`[dyn-skip]` / `[glob]` markers per arg. `audit` runs a small built-in
policy ("deny writes in `/etc`, `/usr`, `/bin`, `/sbin`, `/lib`",
"warn on `curl | bash`", "warn on dynamic args in path slots") and
exits 0 / 1 / 2 by severity. See
[`samples/ShellSyntaxTree.Cli.Sample/Commands/AuditPolicy.cs`](./samples/ShellSyntaxTree.Cli.Sample/Commands/AuditPolicy.cs)
for the illustrative policy code.

### `ShellSyntaxTree.Web.Sample` — Blazor WebAssembly Mermaid visualizer

Paste a bash or PowerShell script, watch the parsed AST render as a
Mermaid flowchart in your browser. Everything runs client-side — pasted
scripts never leave your machine. Useful for "what does this script
actually do?" moments and for understanding how the library models
constructs like subshells and `bash -c` / `pwsh -Command` recursion.
Static `Invoke-Expression` / `iex` payloads surface the same way, while
computed payloads safe-fail.

```bash
dotnet run --project samples/ShellSyntaxTree.Web.Sample
# → http://localhost:5239
```

![Build script preset](./assets/sample-web-build-script.png)

A shell selector switches between the bash and PowerShell parsers. The presets
demonstrate compound commands, Bash subshell isolation, PowerShell grouping and
alias resolution, command-string recursion, dynamic-cwd attribution, and
unparseable inputs. Each preset shows what the library produces in a single
click.

## Building from source

```bash
dotnet tool restore
dotnet build -c Release
dotnet test  -c Release
dotnet pack  -c Release -o ./bin/nuget
```

`global.json` pins the SDK; you need .NET 10 SDK or later for the
`.slnx` solution format.

## Versioning

Tags are bare SemVer version numbers — no `v` prefix. The release
workflow asserts this and fails fast on misformatted tags.

- **0.1.x** — bash parser. Additive after `0.1.0` (more verb table
  entries, more corpus, bug fixes).
- **0.2.0** — first PowerShell parser (`PwshParser`). Adds the shared
  `ShellParserOptions` base and the additive `VerbChain.CanonicalVerb` /
  `VerbChain.IsDynamic` fields; renames `Clause.IsBashCWrapped` →
  `IsCommandStringWrapped` (breaking — see [`RELEASE_NOTES.md`](./RELEASE_NOTES.md)).
- **1.0.0** — when an external consumer beyond Netclaw ships against
  it without finding API gaps.

## License

[Apache-2.0](./LICENSE). Copyright © 2026 Aaron Stannard.

---

**Repository layout** — for contributors and curious agents:

| Path | What |
|---|---|
| `src/ShellSyntaxTree/` | The library (bash + PowerShell parsers) |
| `tests/ShellSyntaxTree.Tests/` | xUnit unit tests + corpus runner |
| `tests/ShellSyntaxTree.Tests/Corpus/<shell>/*.json` | Corpus entries — the acceptance contract (bash + powershell) |
| `samples/ShellSyntaxTree.Cli.Sample/` | Console explainer + audit policy |
| `samples/ShellSyntaxTree.Web.Sample/` | Blazor WASM Mermaid visualizer |
| `docs/CONSUMER_GUIDE.md` | Production-oriented consumer algorithm + Netclaw case study |
| `tools/PwshCorpusTool/` | PowerShell corpus authoring aid |
| `SPEC.md`, `SPEC.POWERSHELL.md` | The behavioral contract |
| `openspec/` | Change-proposal history (rationale for design decisions) |
| `PROJECT_CONTEXT.md`, `TOOLING.md`, `AGENTS.md` | Repo governance — for autonomous agents |
| `IMPLEMENTATION_PLAN.md` | NOW / NEXT / LATER work tracker |
