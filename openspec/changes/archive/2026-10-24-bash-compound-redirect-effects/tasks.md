## 1. Contract and compatibility proof

- [x] 1.1 Review the public API names and default-off option with the maintainer before production code changes; record the approved contract in `SPEC.md`.
- [x] 1.2 Add tests that prove the default option keeps compound redirects unparseable and an opted-in parser exposes each redirect owner.
- [x] 1.3 Add a native Bash `-n` oracle test for positive syntax fixtures; verify that the oracle never executes command text and skips on unsupported hosts.

## 2. Compound redirect facts

- [x] 2.1 Add the wrapper and redirect fact records; verify their source spans, order, and analysis fields in public API tests.
- [x] 2.2 Add the `ParseCompoundRedirects` option with a false default; verify all existing constructors keep their previous parse behavior.
- [x] 2.3 Parse redirects after `for`, `while`, `until`, `if`, and subshell forms; verify each redirect stays on the compound owner.
- [x] 2.4 Keep child `CommandOccurrence.Redirects` unchanged; verify command occurrences retain truthful completeness and source order.
- [x] 2.5 Keep brace-group redirects and unsupported redirect syntax unparseable; verify no partial command projection escapes.

## 3. Parser regression coverage

- [x] 3.1 Add input, output, repeated, descriptor, and unknown-target cases where the existing redirect analyzer supports the source form; verify every analysis type and target domain.
- [x] 3.2 Add nested compound redirects; verify each effect stays with its exact wrapper and source span.
- [x] 3.3 Add launch-environment cases for supplied and unsupplied `$HOME`; verify supplied facts use existing rules and unknown values stay unknown.
- [x] 3.4 Add redirect-open failure flow cases for sequence, `||`, and `&&`; verify the analyzer keeps the pre-body state on failure.
- [x] 3.5 Keep decoded `bash -c` compound redirects unparseable; verify the parser emits no partial occurrences.
- [x] 3.6 Keep outer here-docs and here-strings unparseable; verify no partial command occurrences escape.
- [x] 3.7 Run focused parser tests, Bash corpus tests, and native Bash syntax-only controls; verify all positive source strings parse under Bash.

## 4. Specification and release metadata

- [x] 4.1 Update `SPEC.md`, `docs/CONSUMER_GUIDE.md`, and `IMPLEMENTATION_PLAN.md`; verify API shapes and fail-closed rules match the tests.
- [x] 4.2 Set the local package version to `0.5.0-beta.1` only after the API diff and focused tests pass; verify `dotnet msbuild` reports that prerelease version.
- [x] 4.3 Compare the public API diff with `SPEC.md` and the previous stable package; verify every change is additive.
- [x] 4.4 Run Release build, tests, corpus suite, headers, Slopwatch, and diff checks; verify they pass before a Netclaw local-package test.

## 5. Downstream safety and release gate

- [x] 5.1 Keep default parsing unparseable in a Netclaw 0.28.0-beta.2 consumer fixture; verify the prior consumer cannot auto-allow the source.
- [x] 5.2 Update a separate Netclaw consumer change to opt in and inspect every compound redirect; verify protected-path and file-read checks remain active.
- [x] 5.3 Validate Netclaw against the local package feed; verify the attended and unattended policy tests pass before package publication is considered. The selected policy tests passed. The broader Actor run had 41 skips and one known baseline glob-in-directory-segment failure.
- [x] 5.4 Prepare a local package and source handoff before a separately authorized release; verify that the initial handoff did not tag or publish the package. The private handoff completed first. A later, separate authorization led to tag `0.5.0-beta.1` and public release.
