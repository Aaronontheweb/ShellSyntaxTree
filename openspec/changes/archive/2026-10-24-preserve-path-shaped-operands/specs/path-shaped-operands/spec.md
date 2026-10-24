## ADDED Requirements

### Requirement: A path-shaped operand terminates a native verb chain
The parser SHALL stop a native verb chain before each later word token that matches the existing path-shape rules.

#### Scenario: Bash file operand after a multi-token command
- **WHEN** Bash parses `git diff install-skills.sh` with `/home/user/repo` as the working directory
- **THEN** the verb tokens are `git` and `diff`
- **THEN** `install-skills.sh` is an argument with `IsPath` set to `true`
- **THEN** the resolved path is `/home/user/repo/install-skills.sh`

#### Scenario: PowerShell native file operand after a multi-token command
- **WHEN** PowerShell parses `git diff install-skills.sh` with `/home/user/repo` as the working directory
- **THEN** the verb tokens are `git` and `diff`
- **THEN** `install-skills.sh` is an argument with `IsPath` set to `true`
- **THEN** the resolved path is `/home/user/repo/install-skills.sh`

#### Scenario: A path-shaped command name remains the command
- **WHEN** Bash parses `deploy.sh status`
- **THEN** `deploy.sh` remains the first verb token

#### Scenario: A PowerShell native path-shaped command name remains the command
- **WHEN** PowerShell parses `deploy.sh status`
- **THEN** `deploy.sh` remains the first verb token

### Requirement: Path classification uses one canonical rule
The native verb pass SHALL use the same path-shape rules as the argument path classifier.

#### Scenario: An unknown command has a file operand
- **WHEN** Bash parses `acme inspect report.json` with `/home/user/repo` as the working directory
- **THEN** the verb tokens are `acme` and `inspect`
- **THEN** `report.json` is a resolved path argument

#### Scenario: A real non-Git command has a file operand
- **WHEN** Bash parses `kubectl apply deployment.yaml` with `/home/user/repo` as the working directory
- **THEN** the verb tokens are `kubectl` and `apply`
- **THEN** `deployment.yaml` is a resolved path argument

#### Scenario: An explicit separator gives equivalent path metadata
- **WHEN** Bash parses `git diff -- install-skills.sh` with `/home/user/repo` as the working directory
- **THEN** `install-skills.sh` has the same path classification and resolved value as the form without `--`

### Requirement: Plain lowercase subcommands keep the greedy behavior
The parser SHALL keep each later lowercase word in the verb chain when the word has no path shape.

#### Scenario: Unknown private CLI subcommands
- **WHEN** Bash parses `freshdesk ticket list --status open`
- **THEN** the verb tokens are `freshdesk`, `ticket`, and `list`
- **THEN** `--status` and `open` remain arguments

#### Scenario: Bare Git values remain narrow by default
- **WHEN** Bash parses `git push origin main`
- **THEN** the verb tokens are `git`, `push`, `origin`, and `main`

#### Scenario: Path evidence wins over an extension-shaped subcommand
- **WHEN** Bash parses `tool plugin.sh list`
- **THEN** the only verb token is `tool`
- **THEN** `plugin.sh` is a path argument
- **THEN** `list` is a non-path argument
