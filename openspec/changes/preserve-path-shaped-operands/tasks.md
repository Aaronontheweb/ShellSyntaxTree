## 1. Contract

- [x] 1.1 Update the Bash verb-chain specification with the path-shape boundary and use cases.
- [x] 1.2 Update the PowerShell native-command specification with the same boundary.

## 2. Parser

- [x] 2.1 Stop the Bash greedy verb pass before a path-shaped later token.
- [x] 2.2 Stop the PowerShell native verb pass before the same token shape.

## 3. Verification

- [x] 3.1 Add Bash tests for file operands, flags, separators, command names, and plain subcommands.
- [x] 3.2 Add equivalent PowerShell native-command tests.
- [x] 3.3 Add corpus coverage for the issue #64 reproduction.

## 4. Completion

- [x] 4.1 Update `IMPLEMENTATION_PLAN.md` with the completed issue #64 work.
- [x] 4.2 Run the OpenSpec check, build, tests, header check, and Slopwatch.
