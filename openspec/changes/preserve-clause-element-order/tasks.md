## 1. Contract

- [x] 1.1 Add `ClauseElement`, `ClauseElementRole`, and `Clause.Elements` to
      `SPEC.md` and the public API snapshot.
- [x] 1.2 Add the PowerShell-specific inline-binding and wrapper-span rules to
      `SPEC.POWERSHELL.md`.

## 2. Parser

- [x] 2.1 Emit ordered elements from the Bash classified token walk.
- [x] 2.2 Emit equivalent elements from the PowerShell classified token walk.
- [x] 2.3 Clear source spans when inner command-string clauses are lifted into
      an outer `ParsedCommand`.
- [x] 2.4 Keep synthetic cwd-attribution arguments out of `Elements`.

## 3. Verification

- [x] 3.1 Add public API and default-value assertions.
- [x] 3.2 Cover Git global/subcommand placement, quoted values, repeated text,
      pipeline reset, redirects, inline bindings, wrappers, and cwd attribution
      in Bash tests.
- [x] 3.3 Add equivalent PowerShell native-command and wrapper coverage.
- [x] 3.4 Verify existing `Verb`, `Args`, and `Redirects` regression coverage
      remains green.
- [x] 3.5 Add paired Bash and PowerShell corpus entries for lowercase `-c`,
      uppercase `-C`, mixed occurrences, and an intervening valueless option.
- [x] 3.6 Pin native-option case sensitivity and the heuristic-role boundary.
- [x] 3.7 Audit case-colliding native short options; explicitly model Wget
      log output and curl header output, with paired cross-shell corpus cases.
- [x] 3.8 Harden the audit with tar helper-script bindings, curl `@file` /
      `@-` operand semantics, and scoped Docker context claims.
- [x] 3.9 Reconcile command-string provenance with static and dynamic
      `Invoke-Expression`; audit nested and dynamic `bash -c` behavior.
- [x] 3.10 Apply adversarial-review fixes: safe-fail tar command hooks,
      coalesce adjacent inline native fragments, decode PowerShell colon
      values, preserve PowerShell wrapper redirects, and enforce direct-token
      element coverage across the corpus.
- [x] 3.11 Re-run adversarial review over the fixes; consume maximal adjacent
      fragment runs, safe-fail resolver-sensitive mixed quoting, preserve
      redirects for empty wrapped payloads, and require outer redirect-target
      provenance.
- [x] 3.12 Safe-fail resolver-sensitive literal syntax exposed only after a
      native operand marker such as curl's leading `@` is removed.

## 4. Consumer documentation

- [x] 4.1 Replace the issue #62 limitation in `docs/CONSUMER_GUIDE.md` with a
      complete command-aware-policy example.
- [x] 4.2 Update `PROJECT_CONTEXT.md` and `IMPLEMENTATION_PLAN.md`; explicitly
      schedule migration guidance with the next prerelease rather than making
      this implementation change release-shaped.
- [x] 4.3 Rewrite the Git consumer example to enumerate every occurrence,
      bind operands, and derive Git semantics from all authored elements.
- [x] 4.4 Document strict authored-stream and general executable-aware
      matching; forbid generic verb-role filtering and identify general
      matching as Netclaw's approval-fatigue mitigation.

## 5. Completion

- [x] 5.1 Validate the OpenSpec change.
- [x] 5.2 Run restore, Release build, full tests/corpus, header verification,
      and a public API vs. SPEC diff.
