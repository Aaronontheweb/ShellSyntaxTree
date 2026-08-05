## ADDED Requirements

### Requirement: Clauses expose significant elements in source order
Each parsed clause SHALL expose its source-authored verbs, arguments, and
redirects through `Clause.Elements` in ascending source order.

Whitespace, comments, compound operators, grouping delimiters, shell call
operators, and synthetic cwd-attribution arguments SHALL NOT appear in the
collection.

#### Scenario: Global Git option precedes the subcommand
- **WHEN** either parser parses `git -C /repo commit`
- **THEN** the element values are `git`, `-C`, `/repo`, and `commit`
- **THEN** `-C` and `/repo` each have `PrecedingVerbElementCount` equal to `1`
- **THEN** `commit` is a verb element with `PrecedingVerbElementCount` equal to `1`

#### Scenario: Git option follows the subcommand
- **WHEN** either parser parses `git commit -C HEAD~1`
- **THEN** the element values are `git`, `commit`, `-C`, and `HEAD~1`
- **THEN** `-C` and `HEAD~1` each have `PrecedingVerbElementCount` equal to `2`

#### Scenario: Multiple Git option occurrences remain distinct
- **WHEN** either parser parses `git -C /repo commit -C HEAD~1`
- **THEN** both `-C` occurrences appear at distinct source spans
- **THEN** the first has `PrecedingVerbElementCount` equal to `1`
- **THEN** the second has `PrecedingVerbElementCount` equal to `2`

#### Scenario: Parser roles remain heuristic
- **WHEN** either parser parses `git --no-pager commit -C HEAD~1`
- **THEN** the element values preserve `git`, `--no-pager`, `commit`, `-C`, and `HEAD~1` in that order
- **THEN** `commit` may have `Role=Argument` because `--no-pager` stopped the greedy verb walk
- **THEN** `-C` has `PrecedingVerbElementCount` equal to `1`
- **THEN** consumers SHALL NOT interpret that count alone as Git's semantic option scope

#### Scenario: Intervening arguments are not a generic verb prefix
- **WHEN** either parser parses `git -C /repo commit`
- **THEN** a strict consumer sees `git`, `-C`, `/repo`, and `commit` in that order
- **THEN** it does not discard the two argument elements and generically match `git commit`
- **THEN** a consumer may derive `git commit` only through a Git-aware interpretation that retains `/repo` as policy-relevant scope

#### Scenario: Verb-relative counts reset per clause
- **WHEN** either parser parses `git -C /repo status | git commit -C HEAD~1`
- **THEN** the first clause reports `-C` after one verb token
- **THEN** the second clause reports `-C` after two verb tokens

### Requirement: Elements preserve spelling, logical values, and occurrences
Each direct-source element SHALL expose its exact source slice as `Raw`, its
lexer-decoded logical value as `Value`, and a source span into
`ParsedCommand.Source`.

#### Scenario: Quoted argument value
- **WHEN** Bash parses `git -c "user.name=Jane Doe" commit`
- **THEN** the quoted element has `Raw` equal to `"user.name=Jane Doe"`
- **THEN** its `Value` is `user.name=Jane Doe`
- **THEN** its span starts at the opening quote and covers the closing quote

#### Scenario: Repeated text
- **WHEN** either parser parses `tool item --name item`
- **THEN** the two `item` elements have distinct source starts
- **THEN** the first is a verb and the second is an argument

### Requirement: Element argument metadata matches parser classification
Argument elements SHALL carry the parser's `Kind`, `IsFlag`, `IsPath`, and
`Resolved` result for their source token or inline bound value.

#### Scenario: Inline native path option
- **WHEN** either parser parses `git --work-tree=../repo status`
- **THEN** `--work-tree=../repo` is one argument element
- **THEN** the element is a flag and carries the bound value's path metadata

#### Scenario: Adjacent quoted inline native value
- **WHEN** either parser parses `curl --data="@request file.json" URL`
- **THEN** `--data="@request file.json"` is one argument element
- **THEN** its decoded value is `--data=@request file.json`
- **THEN** it carries path metadata for `request file.json`

#### Scenario: Complete adjacent fragment run
- **WHEN** either parser parses `curl --data=@request".json" URL`
- **THEN** the unquoted prefix and quoted suffix form one argument element
- **THEN** its decoded value is `--data=@request.json`
- **WHEN** either parser parses `curl --data='@$HOME'".json" URL`
- **THEN** the mixed resolver-sensitive value is `DynamicSkip`
- **WHEN** either parser parses `curl --data='@~'"/secret.json" URL`
- **THEN** the value remains `DynamicSkip` after curl's `@` marker is removed

#### Scenario: Native option spelling remains case-sensitive
- **WHEN** either parser parses `git -c key=value commit`
- **THEN** `key=value` is not classified as a path
- **WHEN** either parser parses `git -C /repo commit`
- **THEN** `/repo` is classified as a path
- **THEN** PowerShell cmdlet parameter matching remains case-insensitive

#### Scenario: Case-distinct native bindings are explicit
- **WHEN** either parser parses `wget -o wget.log -O download.bin URL`
- **THEN** both `wget.log` and `download.bin` are classified as paths
- **WHEN** either parser parses `curl -d payload -D headers.txt URL`
- **THEN** `payload` is not classified as a path
- **THEN** `headers.txt` is classified as a path

#### Scenario: Native values may carry path syntax
- **WHEN** either parser parses `curl -d "@request.json" --data=@payload.bin URL`
- **THEN** the spaced element value is `@request.json`
- **THEN** the inline element value remains `--data=@payload.bin`
- **THEN** both value args retain their authored `@` and resolve as paths without it
- **WHEN** either parser parses `curl -d "@-" URL`
- **THEN** `@-` is not classified as a path

#### Scenario: Tar helper commands safe-fail
- **WHEN** either parser parses `tar -F ./helper.sh --info-script=./info.sh --new-volume-script ./next.sh archive`
- **THEN** all three command values are `DynamicSkip` and not paths
- **THEN** the bare `archive` operand remains a path argument rather than a verb

### Requirement: Redirects occupy their authored position
Each redirect SHALL appear as one redirect element at the source position of
its operator and target. Its ordinal among redirect elements SHALL match its
ordinal in `Clause.Redirects`.

#### Scenario: Redirect after an intervening option
- **WHEN** Bash parses `git -C /repo status > status.txt`
- **THEN** the redirect element follows the `status` verb element
- **THEN** its raw slice covers `> status.txt`
- **THEN** its value is `status.txt`

### Requirement: Synthetic and wrapped provenance does not invent source spans
Synthetic cwd attribution SHALL remain in `Clause.Args` and SHALL NOT appear in
`Clause.Elements`. Elements from an expanded command-string wrapper SHALL have
null spans when they cannot be mapped exactly into the outer
`ParsedCommand.Source`.

#### Scenario: Cwd attribution remains synthetic
- **WHEN** Bash parses `cd /repo && git status`
- **THEN** the second clause contains a cwd-attribution `Arg`
- **THEN** its elements are only `git` and `status`

#### Scenario: Bash command-string expansion
- **WHEN** Bash parses `bash -c "git -C /repo status"`
- **THEN** the surfaced clause elements retain their inner raw and decoded values
- **THEN** every surfaced element has null `SourceStart` and `SourceLength`

#### Scenario: Dynamic Bash command string remains an ordinary clause
- **WHEN** Bash parses `bash -c $code`
- **THEN** the outer `bash`, `-c`, and `$code` elements remain source-aligned
- **THEN** the clause is not marked as command-string wrapped

#### Scenario: PowerShell encoded-command expansion
- **WHEN** PowerShell parses a valid encoded command payload
- **THEN** the surfaced clause elements retain their decoded values
- **THEN** every surfaced element has null `SourceStart` and `SourceLength`

#### Scenario: PowerShell wrapper preserves an outer redirect
- **WHEN** PowerShell parses `pwsh -Command "git status" > outer.txt`
- **THEN** the surfaced clause contains the output redirect in both `Redirects` and `Elements`
- **THEN** the inner verb elements have null spans
- **THEN** the redirect element retains its exact outer source span

#### Scenario: PowerShell Invoke-Expression expansion
- **WHEN** PowerShell parses `Invoke-Expression 'git -C C:\repo status'`
- **THEN** the surfaced clause elements retain their inner raw and decoded values
- **THEN** every surfaced element has null `SourceStart` and `SourceLength`

### Requirement: Opaque computed regions remain conservative ordered elements
When a parser feature collapses a complete computed source expression, `Clause.Elements` SHALL
represent the complete payload as one source-aligned opaque argument element
matching the one `DynamicSkip` compatibility argument, rather than exposing
partially interpreted interior tokens.

#### Scenario: Dynamic Invoke-Expression payload
- **WHEN** PowerShell parses `Invoke-Expression $code`
- **THEN** the clause elements are the `Invoke-Expression` verb followed by one source-aligned `$code` argument
- **THEN** the payload element has `Kind=DynamicSkip`

#### Scenario: Inline dynamic Invoke-Expression binding
- **WHEN** PowerShell parses `iex -Command:$code`
- **THEN** `-Command:$code` remains one source-aligned argument element
- **THEN** the element is both option-shaped and `DynamicSkip`
