# ShellSyntaxTree

A focused .NET library that parses shell command strings into a structured AST.
Purpose-built for **security gate evaluators** — tools that inspect agent-emitted
shell commands and decide whether to allow, prompt for, or deny execution.

ShellSyntaxTree is **not** a shell interpreter. It does not execute, expand,
or evaluate commands. It returns an AST (verb chain, args with path
classification, redirects, compound operators, `cd`-in-compound propagation,
`bash -c` recursion) that consumers walk to make policy decisions.

The original consumer is [Netclaw](https://github.com/netclaw-dev/netclaw)'s
approval policy. Any tool that needs to reason about the shape of an
agent-emitted shell command — without running it — is welcome to consume it.

## Status

**v0.1 — pre-alpha.** Public API surface and behavior are specified in
[`SPEC.md`](./SPEC.md). Implementation is in progress.

## Why not `tree-sitter-bash`?

Native dependencies, AOT trim concerns, and IDE-grade fidelity we don't need.
We want unsupported constructs to mark `IsUnparseable = true` so consumers
route to safe-fail. See [`SPEC.md` Appendix B](./SPEC.md) for the full
trade-off analysis.

## Public API surface (locked for v0.1)

```csharp
namespace ShellSyntaxTree;

public interface IShellParser { ParsedCommand Parse(string command); }
public sealed class BashParser : IShellParser { /* ... */ }
public sealed record BashParserOptions { /* HomeDirectory, WorkingDirectory */ }

public sealed record ParsedCommand { /* Source, Clauses, IsUnparseable, ... */ }
public sealed record Clause       { /* Operator, Verb, Args, Redirects, ... */ }
public sealed record VerbChain    { /* Tokens */ }
public sealed record Arg          { /* Raw, Resolved, Kind, IsPath, ... */ }
public sealed record Redirect     { /* Direction, Target */ }

public enum ArgKind            { Literal, EnvVar, Glob, Tilde, DynamicSkip }
public enum RedirectDirection  { In, Out, Append, ErrOut, ErrAppend }
public enum CompoundOperator   { None, AndIf, OrIf, Sequence, Pipe }
```

PowerShell and Windows `cmd` parsers are deferred to later versions; the
`IShellParser` seam is already in place so consumers don't have to refactor
when they ship.

## Multi-targeting

`netstandard2.0` for broad consumer compatibility, `net8.0` for modern
runtimes. Tests target `net10.0`.

## Repository layout

```
src/ShellSyntaxTree/                 # library (TBD)
tests/ShellSyntaxTree.Tests/         # xunit tests + corpus runner (TBD)
tests/ShellSyntaxTree.Tests/Corpus/  # JSON test corpus (acceptance contract)
SPEC.md                              # the implementation specification
PROJECT_CONTEXT.md                   # what this is, who it serves
TOOLING.md                           # available tooling and how to access it
AGENTS.md / CLAUDE.md                # agent operating constitution
IMPLEMENTATION_PLAN.md               # NOW / NEXT / LATER work tracker
```

## Building

```bash
dotnet tool restore
dotnet build -c Release
dotnet test  -c Release
dotnet pack  -c Release -o ./bin/nuget
```

`global.json` pins the SDK version. Targeting requires .NET 10 SDK or later
(`.slnx` solution format).

## License

[Apache-2.0](./LICENSE). Copyright © 2026 Aaron Stannard.
