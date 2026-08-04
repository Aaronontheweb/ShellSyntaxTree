## 1. Contract

- [x] 1.1 Replace the `Invoke-Expression` exclusion in `SPEC.POWERSHELL.md` section 10 with static recursion and dynamic safe-fail rules.
- [x] 1.2 Specify current-scope location propagation, shared recursion failures, and corpus coverage in `SPEC.POWERSHELL.md` sections 10, 11, and 13.
- [x] 1.3 Update the shared command-string wrapper documentation in `SPEC.md` without changing the public API.

## 2. Lexical Evidence

- [x] 2.1 Record unescaped interpolation on double-quoted strings and expandable here-strings.
- [x] 2.2 Add lexer tests for variable interpolation, subexpression interpolation, escaped dollar signs, and static expandable strings.

## 3. Parser

- [x] 3.1 Recognize `Invoke-Expression` and `iex` through one canonical command identity.
- [x] 3.2 Recurse into one provably static positional or `-Command` payload and surface wrapped inner clauses.
- [x] 3.3 Collapse direct computed payloads into one `DynamicSkip` source argument.
- [x] 3.4 Mark pipeline-only, incoming-pipeline, missing, and ambiguously bound payloads unparseable.
- [x] 3.5 Share the existing input cap and depth-five counter across expression, command, and encoded-command recursion.
- [x] 3.6 Share location context with expression recursion so inner location changes affect following outer clauses while child `pwsh` remains isolated.

## 4. Verification

- [x] 4.1 Add parser unit tests for full-name, alias, literal, parameter, compound, operator, and wrapper cases.
- [x] 4.2 Add parser unit tests for interpolation, variables, concatenation, subexpressions, pipeline input, missing payload, and inner anomalies.
- [x] 4.3 Add parser unit tests for inherited location, exported location changes, child-process isolation, depth limits, mixed wrappers, and input limits.
- [x] 4.4 Replace PowerShell corpus case 157 and add the required static, dynamic, pipeline, location, and recursion cases to `CorpusManifest`.
- [x] 4.5 Regenerate the PowerShell corpus and confirm the real-`pwsh` oracle matrix remains valid.
- [x] 4.6 Cover Unicode and backtick-newline escape decoding found during security review.
- [x] 4.7 Cover exact colon-form `Invoke-Expression -Command:` binding.
- [x] 4.8 Cover decoded whitespace, colon comments, and escaped inline colon payloads found during follow-up review.
- [x] 4.9 Cover decoded NUL safe-fail and inline colon Unicode escapes.

## 5. Completion

- [x] 5.1 Update `RELEASE_NOTES.md` and `IMPLEMENTATION_PLAN.md` for issue #63.
- [x] 5.2 Run OpenSpec validation, Release build, full tests, and copyright-header verification.
