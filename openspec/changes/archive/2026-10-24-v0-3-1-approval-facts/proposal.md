## Why

Netclaw v0.26.0-beta.3 produced 18 distinct shell approval prompts after its
2026-08-11 15:13:55 UTC daemon start. Two prompt classes expose facts that
ShellSyntaxTree 0.3.0 cannot communicate without a consumer reparsing shell
source:

- a quoted argument can concatenate literals with a bounded special parameter,
  for example `echo "---EXIT $?---"`; and
- a static loop can have a finite authored path expression even when ambient
  Bash attributes prevent an effective runtime-value proof.

The second case is not a missing loop-composition algorithm. Version 0.3.0
already composes finite fragments in `IsolatedNonInteractive` mode. The missing
contract is a truthful separation between effective runtime values and values
proved from authored source alone, plus parser-owned lexical path shape.

## What Changes

- Add bounded integer and concatenation alternatives to `ShellValueDomain`.
- Add `AnalyzedArgument.AuthoredValue` as a pre-field-splitting word fact
  without changing the existing effective `Value` property.
- Add shell-general `AuthoredPathShape` that reports lexical shape, not operand
  semantics or authority.
- Add opt-in `BashParserOptions.PublishAuthoredSourceFacts`. The default keeps
  the released unparseable-loop behavior.
- Preserve the existing isolated-mode finite composition implementation and
  crop duplicate work from the plan.
- Add an exact, sanitized D01-D18 evidence catalog and executable corpus cases
  for the three ShellSyntaxTree-owned facts.
- Update the contract and consumer guide with input/output examples and strict
  counterexamples.

The paired Netclaw change is `structure-shell-approval-policy`. It decides
whether its approval threat model may consume `AuthoredValue`; ShellSyntaxTree
does not make that policy decision.

## Capabilities

### New Capabilities

- `bounded-shell-values`: typed bounded scalar and concatenated string facts,
  authored-source word values, and lexical path shape.

### Modified Capabilities

- None.

## Impact

- Public API: additive nested `ShellValueDomain` records, one additive enum,
  one Bash option, and two additive `AnalyzedArgument` properties.
- Bash analysis: quoted `$?` becomes bounded. An opt-in projection exposes a
  static loop word before ambient attributes, `IFS`, and field splitting.
- PowerShell: no language behavior change. `AuthoredValue` equals `Value`, and
  `AuthoredPathShape` remains `Unknown` in 0.3.1.
- Consumers: existing use of `Value` is unchanged and remains conservative.
  Use of `AuthoredValue` is an explicit product-policy choice.
- Release: additive v0.x release `0.3.1`, with package and consumer-guide work
  required before publication.
