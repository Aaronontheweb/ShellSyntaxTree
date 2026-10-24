## ADDED Requirements

### Requirement: Invoke-Expression recurses into provably static command strings

The PowerShell parser SHALL recurse into the payload of `Invoke-Expression` and its canonical `iex` alias only when binding produces exactly one scalar string token whose value is statically knowable.
The parser SHALL consume the outer expression clause, surface the inner clauses,
set `IsCommandStringWrapped = true` on every surfaced clause, and preserve
the outer clause's preceding operator on the first surfaced clause.

#### Scenario: Full cmdlet name with a literal payload
- **WHEN** PowerShell parses `Invoke-Expression 'Get-Date'`
- **THEN** the result contains one `Get-Date` clause
- **THEN** the clause has `IsCommandStringWrapped` set to `true`
- **THEN** no outer `Invoke-Expression` clause is returned

#### Scenario: Alias with a static destructive payload
- **WHEN** PowerShell parses `iex 'Remove-Item C:\x'`
- **THEN** the result contains a canonical `Remove-Item` clause
- **THEN** `C:\x` is surfaced as its path argument

#### Scenario: Expandable string without interpolation is static
- **WHEN** PowerShell parses `iex "Get-Date"`
- **THEN** the result contains one wrapped `Get-Date` clause

#### Scenario: Bare scalar word is static
- **WHEN** PowerShell parses `Invoke-Expression Get-Date`
- **THEN** the result contains one wrapped `Get-Date` clause

#### Scenario: Command parameter binds a static payload
- **WHEN** PowerShell parses `Invoke-Expression -Command 'Get-Date'`
- **THEN** the result contains one wrapped `Get-Date` clause

#### Scenario: Colon command parameter binds a static payload
- **WHEN** PowerShell parses `Invoke-Expression -Command:Get-Date`
- **THEN** the result contains one wrapped `Get-Date` clause

#### Scenario: Inner compound clauses are surfaced
- **WHEN** PowerShell parses `iex 'Get-Date; Get-Process'`
- **THEN** the result contains wrapped `Get-Date` and `Get-Process` clauses
- **THEN** the second clause has the `Sequence` operator

#### Scenario: Outer operator is preserved
- **WHEN** PowerShell parses `Get-Process; iex 'Get-Date'`
- **THEN** the surfaced `Get-Date` clause has the `Sequence` operator

### Requirement: Computed expression payloads never produce a clean approval shape

When an `Invoke-Expression` payload depends on runtime computation, the parser MUST NOT recurse into it.
When the complete source expression is observable, the parser SHALL retain
the outer canonical command identity and
SHALL surface the complete payload source slice as one
`Arg { Kind = DynamicSkip, IsPath = false, Resolved = null }`. When the
payload is missing, comes from an incoming pipeline, or cannot be bound
unambiguously, the parser SHALL set `ParsedCommand.IsUnparseable = true`.

#### Scenario: Variable payload is dynamic
- **WHEN** PowerShell parses `Invoke-Expression $code`
- **THEN** the result retains an `Invoke-Expression` clause
- **THEN** `$code` is one `DynamicSkip` argument
- **THEN** the authored verb and payload remain source-aligned clause elements

#### Scenario: Interpolated payload is dynamic
- **WHEN** PowerShell parses `iex "Remove-$noun C:\x"`
- **THEN** the result retains the canonical `Invoke-Expression` identity
- **THEN** the complete quoted payload is one `DynamicSkip` argument

#### Scenario: Subexpression payload is dynamic
- **WHEN** PowerShell parses `iex $(Get-Content script.ps1)`
- **THEN** the complete payload expression is one `DynamicSkip` argument

#### Scenario: Concatenated literals are dynamic
- **WHEN** PowerShell parses `iex ('Get-' + 'Date')`
- **THEN** the parser does not concatenate or recurse into the expression
- **THEN** the complete payload expression is one `DynamicSkip` argument

#### Scenario: Pipeline-only payload is unparseable
- **WHEN** PowerShell parses `Get-Content script.ps1 | Invoke-Expression`
- **THEN** `ParsedCommand.IsUnparseable` is `true`
- **THEN** the diagnostic identifies dynamic pipeline input

#### Scenario: Incoming pipeline dominates an explicit payload
- **WHEN** PowerShell parses `'Get-Date' | Invoke-Expression 'Get-Process'`
- **THEN** `ParsedCommand.IsUnparseable` is `true`
- **THEN** the parser does not return a clean static expression expansion

#### Scenario: Missing payload is unparseable
- **WHEN** PowerShell parses `Invoke-Expression`
- **THEN** `ParsedCommand.IsUnparseable` is `true`

### Requirement: Expandable-string staticness uses lexical interpolation evidence

The PowerShell lexer SHALL distinguish unescaped variable or subexpression interpolation from escaped literal dollar signs in double-quoted strings and expandable here-strings.
The parser SHALL use that lexical evidence when proving an
`Invoke-Expression` payload static.

#### Scenario: Variable interpolation is detected
- **WHEN** PowerShell lexes `"Get-$noun"`
- **THEN** the quoted-string token records interpolation

#### Scenario: Subexpression interpolation is detected
- **WHEN** PowerShell lexes `"Get-$(Get-Variable noun)"`
- **THEN** the quoted-string token records interpolation

#### Scenario: Escaped dollar is literal
- **WHEN** PowerShell parses ``iex "Write-Host `$name"``
- **THEN** the quoted payload is treated as static
- **THEN** the inner `Write-Host` clause is surfaced

#### Scenario: Unicode escape in an invoked alias is decoded
- **WHEN** PowerShell parses ``& "i`u{65}x" $code``
- **THEN** the command is recognized as `iex`
- **THEN** `$code` is surfaced as `DynamicSkip`

#### Scenario: Escaped newline in a static payload is decoded
- **WHEN** a static expression payload contains `` `n`` between two commands
- **THEN** both inner command clauses are surfaced

#### Scenario: Decoded PowerShell whitespace separates inner tokens
- **WHEN** a static expression payload separates a verb and path with
  vertical tab, form feed, or Unicode whitespace
- **THEN** the path is surfaced as a separate inner argument

#### Scenario: Colon payload comment leaves the argument missing
- **WHEN** PowerShell parses `Invoke-Expression -Command:#comment`
- **THEN** `ParsedCommand.IsUnparseable` is `true`

#### Scenario: Decoded NUL safe-fails
- **WHEN** a static expression payload contains a decoded NUL
- **THEN** `ParsedCommand.IsUnparseable` is `true`

#### Scenario: Scoped interpolation is dynamic
- **WHEN** a static-looking payload contains `$:name`
- **THEN** the payload is surfaced as `DynamicSkip`

#### Scenario: Unsupported invocation identity safe-fails
- **WHEN** an expression payload invokes a quoted command without `&` or an unsupported module-qualified cmdlet
- **THEN** `ParsedCommand.IsUnparseable` is `true`

#### Scenario: Dynamic command invalidates location
- **WHEN** a dynamic command identity executes before a relative path clause
- **THEN** the following path and cwd attribution are dynamic

#### Scenario: Malformed Unicode escape safe-fails
- **WHEN** a command string contains an empty or out-of-range `` `u{...}`` escape
- **THEN** `ParsedCommand.IsUnparseable` is `true`

### Requirement: Static expression recursion shares the caller's location context

The parser SHALL parse a static `Invoke-Expression` payload using the caller's effective PowerShell location.
Location changes made by the static payload SHALL update attribution for
clauses that follow in the containing
scope. Child `pwsh` recursion SHALL retain its existing isolated location
context.

#### Scenario: Static payload inherits caller location
- **WHEN** PowerShell parses `Set-Location C:\a; iex 'Remove-Item child.txt'`
- **THEN** the surfaced `Remove-Item` path resolves under `C:\a`

#### Scenario: Payload location change affects following outer clause
- **WHEN** PowerShell parses `iex 'Set-Location C:\b'; Remove-Item child.txt`
- **THEN** the outer `Remove-Item` path resolves under `C:\b`

#### Scenario: Payload compound uses and exports its final location
- **WHEN** PowerShell parses `Set-Location C:\a; iex 'Set-Location C:\b; Remove-Item child.txt'; Get-ChildItem child.txt`
- **THEN** the inner `Remove-Item` path resolves under `C:\b`
- **THEN** the following outer `Get-ChildItem` path resolves under `C:\b`

#### Scenario: Child pwsh location remains isolated
- **WHEN** PowerShell parses `pwsh -Command 'Set-Location C:\b'; Remove-Item child.txt` with a different outer working directory
- **THEN** the outer `Remove-Item` does not inherit `C:\b`

#### Scenario: Dynamic expression invalidates following location
- **WHEN** PowerShell parses `Set-Location C:\safe; iex $code; Remove-Item child.txt`
- **THEN** the following `child.txt` path is `DynamicSkip`
- **THEN** the following clause carries dynamic cwd attribution

### Requirement: PowerShell command-string constructs share security limits

`Invoke-Expression`, `pwsh -Command`, and `pwsh -EncodedCommand` SHALL share the existing depth-five PowerShell command-string recursion counter.
Every static expression payload SHALL be subject to the existing 64 KiB input
cap.
An over-limit payload or an inner parse that is unparseable SHALL mark the
outer `ParsedCommand` unparseable.

#### Scenario: Five nested expression strings parse
- **WHEN** PowerShell parses five nested static `Invoke-Expression` payloads
- **THEN** the innermost clauses are surfaced
- **THEN** `ParsedCommand.IsUnparseable` is `false`

#### Scenario: Six nested expression strings exceed the cap
- **WHEN** PowerShell parses six nested static `Invoke-Expression` payloads
- **THEN** `ParsedCommand.IsUnparseable` is `true`
- **THEN** the diagnostic identifies the depth-five recursion cap

#### Scenario: Mixed wrappers share one depth budget
- **WHEN** a command alternates `Invoke-Expression`, `pwsh -Command`, and `pwsh -EncodedCommand` beyond five total levels
- **THEN** `ParsedCommand.IsUnparseable` is `true`

#### Scenario: Oversized static payload is rejected
- **WHEN** a static `Invoke-Expression` payload exceeds 64 KiB of UTF-16 characters
- **THEN** `ParsedCommand.IsUnparseable` is `true`

#### Scenario: Inner anomaly propagates outward
- **WHEN** a static `Invoke-Expression` payload parses as an unsupported control-flow script
- **THEN** `ParsedCommand.IsUnparseable` is `true`
