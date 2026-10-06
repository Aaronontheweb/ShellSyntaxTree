#### 0.4.0-beta.22 2026-10-06 ####

This prerelease fixes security bugs. A Bash line continuation or a carriage return could hide a command from `Parse` (#243). In the examples, `⏎` is a real newline and CR is a carriage return.

## Fixed

- Bash removes a line continuation (a backslash and a newline) before it splits the input into tokens. The lexer did not remove it before it read an expansion. In `echo "$\⏎(touch /tmp/x)"`, Bash runs `touch`, but `Parse` gave only the `echo` occurrence. In `x="$\⏎(touch /tmp/x)"`, `Parse` gave no occurrence. 0.4.0-beta.3 to 0.4.0-beta.21 have this bug.
- The lexer now skips line continuations before it reads the next character of `$(`, `$((`, `${`, `$name`, `$'`, `$"`, the operators `&&`, `||`, `>>`, `<<`, `<<-`, `<<<`, `&>`, and `&>>`, a numeric descriptor, and a descriptor target (`>&\⏎2`). This applies in unquoted text, in double quotes, and in the substitution boundary scan. `BashLineContinuation` owns the rule.
- Bash keeps a line continuation in single quotes, in `$'…'`, in a comment, and in a heredoc body with a quoted delimiter. These stay literal.
- Values: `n=build; rm -rf "$n\⏎dir/"` gave the exact value `builddir/`. Bash reads the variable `ndir`, so it runs `rm -rf /`. The value is now unknown. `"$\⏎n/"` gives `build/`, `"${\⏎x}"` gives the value of `x`, and `x$\⏎'y'` gives `xy`.
- A carriage return could hide a command. Bash reads CR as a word character and `\` + CR as an escaped CR. The lexer read CR as a line end and `\` + CRLF as a line continuation. `echo a\` + CRLF + `touch /tmp/x` and `echo a` + CR + `# ; touch /tmp/x` run `touch` in Bash, but `Parse` gave no `touch` occurrence. A comment now ends only at LF, and a heredoc line with a trailing CR is not its delimiter, as in Bash.
- The value argument of a Bash `--name=value` word is now the part after the first `=` of the decoded word that Bash passes. `curl --data=@p\.json` gave the value `--data=@p.json`, `curl --data=\a""` gave `--data=a`, and `curl --data="a b"` gave `"a b"` with the quotes. Bash passes `@p.json`, `a`, and `a b`. A value that the parser cannot take from the decoded word is Unknown.
- Readers of authored text use the same rule: the redirect operator analysis (`>\⏎>` is an append), `MayPathnameExpand` and `MayFieldSplit` (`"$\⏎@"`, `{a.\⏎.c}`), `CommandWords`, and option names in the per-verb flag tables (`curl -\⏎o /tmp/out` binds the path). `Raw` keeps the exact source slice.

## Fail closed

- `$(\⏎(` and `$\⏎((`: Bash reads arithmetic, but the grammar reads only an exact `$((` marker. Before, the parser read a subshell that runs `1+2`.
- As before: a line continuation in an expanding heredoc body or delimiter (Bash joins the body lines before it looks for the delimiter), and in `$'…'`.
- New: a CR outside quotes, comments, and heredoc bodies, and `\` + CR outside single quotes, comments, and heredoc bodies. This includes CRLF line endings. Bash passes the CR to the program, so a CRLF script does not run as written.

## Changed tests

- Removed the tests that pinned the old CR rules: `command 3\` + CRLF + `>out` and `>&1` (a descriptor across `\` + CRLF), `cat ~\` + CRLF + `/x`, a CRLF heredoc body, `\` + CRLF before a comment in `$( )`, and CRLF as a statement separator. Bash does not read these sources the way the tests claimed. The LF forms stay pinned.
- Corpus entries 166 and 288: the value argument of `curl --data=…` is now `@$HOME.json`, not the whole word.

## Security and compatibility

- Output comparison against 0.4.0-beta.21: 6,416 inputs, 118,080 records in two launch modes. 1,644 records change, in 76 inputs. Every changed input has a backslash before a newline.
- Public API: no change.
- 0.4.0-beta.22 was planned for the literal twins of #242. That change moves to 0.4.0-beta.23.

#### 0.4.0-beta.21 2026-10-06 ####

This prerelease restores the 0.4.0-beta.19 verb-slot rule of `CommandOccurrence.CommandWords` (#240). It reverts the 0.4.0-beta.20 change (#237).

## Changed

- In the verb slot, a plain word directly after an option stays a command word again. After the verb slot, a plain word directly after an option is still skipped as that option's value.
- Reason (owner decision): without per-program grammar, a switch without a value, followed by a subcommand, looks the same as an option and its value. In 0.4.0-beta.20, skipping the word let a short grant cover a hidden program: `git --no-pager push` gave `git`, `xargs -0 rm` gave `xargs`, and `env -i rm` gave `env`.
- Pins: `docker --debug run` gives `docker run`, `ilspycmd -t X /p/x.dll` gives `ilspycmd X`, `git --no-pager push` gives `git push`, and `xargs -0 rm` gives `xargs rm`.

## Security and compatibility

- The library code equals 0.4.0-beta.19. An output comparison over 6,057 inputs (109,960 records in two launch modes) gives output that is byte-identical to 0.4.0-beta.19. Against 0.4.0-beta.20, only `CommandWords` changes, in the same 186 inputs.
- A consumer that keys grants on `CommandWords` gets the 0.4.0-beta.19 keys.
- Public API: no change.

#### 0.4.0-beta.20 2026-10-05 ####

This prerelease applies the option-value rule to the verb slot of `CommandOccurrence.CommandWords` (#237).

## Changed

- A plain word directly after an option is that option's value everywhere, also in the verb slot. The next plain word is the verb. `ilspycmd -t Mattermost.MattermostClient /p/x.dll` gave `ilspycmd Mattermost.MattermostClient`. It now gives `ilspycmd`.
- More examples: `kubectl -n prod get pods` gives `kubectl get pods`, `git -c user.name=x commit -m msg` gives `git commit`, and `dotnet --verbosity q build Foo.sln` gives `dotnet build Foo.sln`.
- No change: an option with an inline value (`cmd --flag=value sub` gives `cmd sub`), a value or a path after an option (`gh -R o/r pr view` gives `gh pr view`), and bare `--` in the verb slot (`git -- push` gives `git push`).

## Security and compatibility

- Accepted trade-off (owner decision): the rules do not know which options take a value. A switch without a value hides the plain word after it. `docker --debug run` gives `docker`, and `git --no-pager log -1` gives `git`. A wrapper is affected too: `xargs -0 rm` gives `xargs`, and `env -i rm` gives `env`. A consumer must not let a grant for a wrapper cover the program that the wrapper runs.
- Fail closed as before: a dynamic word or a bare glob in the verb slot gives `Unknown`, also after an option (`cmd -x $v sub`, `cmd -x * sub`). A word with an unproved value that does not start with `-` is dynamic, so `cmd "$o" value sub` with an unknown `o` gives `Unknown`.
- An output comparison against 0.4.0-beta.19 over 6,057 inputs (109,960 records in two launch modes) found changes only in `CommandWords`, and only in 186 inputs with an option before the verb. In 166 word lists the projection drops the old verb-slot word. In 22 PowerShell word lists a script block or an expansion moves into the verb slot, so the words become `Unknown`.
- A replay of 11,063 Netclaw shell calls changes the words of 1,581 calls. Most are `find . -type f`, `grep -v obj`, `jq -r .x`, and `curl -w FORMAT`. The option value no longer becomes a command word.
- Public API: no change.

#### 0.4.0-beta.19 2026-10-05 ####

This prerelease decodes Bash ANSI-C quotes and publishes pathname-expansion and field-splitting facts for each argument (#232).

## Added

- `AnalyzedArgument.MayPathnameExpand` and `AnalyzedArgument.MayFieldSplit` tell whether Bash can glob or split the word at run time, also when its value is `Unknown`. A fully quoted or escaped word gives false. An unquoted glob character, an unquoted expansion or substitution, or a brace expansion gives true. Every PowerShell argument gives true.
- An ANSI-C string `$'…'` decodes for escapes with one exact ASCII result. `cat a$'b'c` gives the path `/work/abc`.

## Security and compatibility

- Fix: a `$'…'` after other text in a word was a literal `$` and quoted text. `cat ~/.netclaw/$'\x6beys'/key-1.xml` gave the exact path `~/.netclaw/$\x6beys/key-1.xml`, but Bash reads `~/.netclaw/keys/key-1.xml`. It now gives the decoded path.
- An escape without one exact result (`\u`, `\U`, `\c`, NUL, a byte above 127, an unknown escape) and a locale string `$"…"` in any position make the source unparseable. Before, `a$"b"` parsed as the literal `a$b`.
- The word projection reads the authored `$`, so a word with `$'…'` gives `Unknown` command words.
- An output comparison against 0.4.0-beta.18 over 4,373 inputs (95,960 records) found changes only in inputs with `$'` or `$"`, apart from the two new facts. A replay of 11,063 Netclaw shell calls gives no change apart from the two new facts.
- Public API: two new read-only properties on `AnalyzedArgument`.

#### 0.4.0-beta.18 2026-10-05 ####

This prerelease parses bounded Bash arithmetic expansion and loop `break` and `continue`. It also fixes two misreads: a brace word and an arithmetic command (#227).

## Added

- A bounded `$((…))` parses as a value part of a word. Its value is `Unknown`, with `Kind = DynamicSkip`. It is never a path or a command word. The grammar accepts numeric constants, variable reads, nested bounded expansions, parentheses, and the operators `+ - * / % ** << >> & | ^ ~ ! < > <= >= == != && || ?:`.
- Each variable that an arithmetic expansion reads must hold a proved integer: a bound decimal value, the result of an earlier arithmetic expansion, or `$?`, `$#`, `$$`. `n=5; echo $((n*2))`, `for i in 1 2; do echo $((i+1)); done`, and `x=$((1+2)); y=$((x*2))` parse.
- A bounded `break`, `continue`, `exit`, or `return` (no operand, or one decimal operand; a level of 1 or more) parses in a loop. The state pass joins the state at a `break` into the end of its loop and the state at a `continue` into its head. `for d in a b; do continue; done` parses. `cd x || exit 1` no longer makes a later loop, `if`, or assignment unparseable.

## Security and compatibility

- Bash evaluates the value of each variable in an arithmetic context as an expression, and a value such as `a[$(cmd)]` runs `cmd`. So an arithmetic expansion fails closed when it reads a variable without a proved integer value, or when it holds a command substitution, an array subscript, an assignment, `++`, `--`, or the comma operator. `age=$(( ($(date +%s) - $(stat -c %Y "$d")) / 86400 ))` stays unparseable for this reason.
- An arithmetic command `((…))` is unparseable. Before, `((cmd))` parsed as two subshells that run `cmd`, and `p=/safe; (( p = 0 )); cat "$p"` gave `cat` the value `/safe`. Bash assigns `0` to `p`. `( (cmd) )` with a space stays two subshells. `let` stays unparseable.
- A word with an unquoted brace expansion, such as `{a,b}` or `{1..3}`, has an `Unknown` value, no resolved path, and no command word. Before, `cat ~/.netclaw/config/{netclaw,secrets}.json` gave one exact, resolved path, but Bash reads two files. A quoted or escaped brace, `{}`, and `{a}` stay literal. A brace word in a command name or a case word is unparseable.
- Only inputs with `$((`, `((`, a control-transfer builtin, or a brace expansion change. An output comparison over 4,257 inputs, with each initial-state mode, both authored-fact settings, two working directories, and with and without launch facts (93,176 records), found no other changed record. A replay of 11,063 Netclaw shell calls gives 3 more parsed calls, 0 worse calls, and 2 brace words that are now `Unknown`.
- There is no public API change. A consumer gets `Unknown` values and no new node, role, region, or value kind.

#### 0.4.0-beta.17 2026-10-04 ####

This prerelease publishes effective values and binding command words under `FreshNonInteractiveNoStartup` (#224).

## Added

- Fresh mode publishes the effective `Value` of a word that reads a binding, with or without `PublishAuthoredSourceFacts`, as isolated mode does. Before, fresh mode with that option published `Value=Unknown`. `x=/etc/passwd; cat "$x"` now gives `Value=/etc/passwd`, and `for f in a b; do cat "/w/$f"; done` gives `Value={/w/a,/w/b}`.
- A `for-in` loop parses under fresh mode without the option.
- A quoted expansion of a name with one exact value is a command word with the static-word rules. `r=push; git "$r" origin` gives `git push origin`.

## Security and compatibility

- `Unknown` mode with the option stays authored-only: `Value=Unknown` and no binding word.
- An unquoted expansion, a binding with more than one value, an unknown value, and a mix with a launch variable give no binding word. The program word never comes from a binding.
- With the option, an unquoted loop `cd $f` that can split gives an unknown directory, as before in fresh mode. Without the option, it fails atomically, as before (corpus entries 249 and 250). Isolated mode with the option now parses this form too.
- Only effective values, resolved paths after a proved `cd`, and command words change. Each changed fresh-mode record now equals the isolated-mode result or the fresh-mode result without the option. A replay of 6,912 complete Netclaw shell calls gives 193 more proved argument values in 115 rows and 12 fewer `Unknown` word lists, with no worse row.
- There is no public API change.

#### 0.4.0-beta.16 2026-10-03 ####

This prerelease parses `export`, `set --`, and reads of unassigned variables under `FreshNonInteractiveNoStartup` (#221).

## Added

- A read of an unassigned plain `$NAME` or `${NAME}` in fresh mode is an `Unknown` value, as in isolated mode. `rm -rf "$BUILD_DIR/out"` now parses. After an unmodeled variable change (`declare`, `local`, `unset`, `readonly`, `export -n`), the read fails closed as before. `Unknown` mode does not change.
- A bounded `export` in the top-level shell: each `NAME=value` operand is a `ShellState` assignment with the bounded assignment rules, in operand order. A plain `NAME` operand marks the name for export. `export -p` alone is a query. `export REPO_ROOT=/r; bash scripts/build.sh` now parses.
- `set --` followed by words replaces the positional parameters. `$1` to `$9` in an assignment value give `Unknown`. `set -- $line; pid=$1; kill "$pid"` now parses.

## Fixed

- A decoded `bash -c` child now lists only the `ShellState` assignments that every path to it exports. Before, it listed every assignment of its parent, but a child process does not get an unexported variable. A subshell still lists every assignment.

## Security and compatibility

- An export of a shell-owned or loader name (`PATH`, `HOME`, `TMPDIR`, `CDPATH`, `LD_*`, `BASH_ENV`, `IFS`, `SHELLOPTS`, and the others that the assignment rule rejects) stays unparseable. Every other `export` option and every other `set` form (`set -e`, `set -o name`) stay unparseable.
- An export of a supplied launch name revokes the launch fact. The later read gets the new authored value, or `Unknown`, and never the launch value.
- An export in a pipeline stage or a background list runs in a subshell, so its value does not reach a later command.
- A consumer that relied on fresh mode to reject an unassigned read must now check for `Unknown` values. `$HOME` keeps its documented `~` rule.
- Only inputs that were unparseable change, apart from the `bash -c` assignment list above. A replay of 6,912 complete Netclaw shell calls gives 16 more parsed rows and no worse row.
- There is no public API change.

#### 0.4.0-beta.15 2026-10-03 ####

This prerelease parses a heredoc inside a command substitution (#217).

## Added

- `gh pr create --body "$(cat <<'EOF' ... EOF )"` now parses. The scanner skips each heredoc body to its exact delimiter line, so a `)` or a quote in the body cannot end the substitution. The `cat` command is a normal `Substitution` occurrence with its heredoc analysis.
- A command substitution inside an expanding heredoc body is a visible occurrence, as at the top level.

## Fixed

- The heredoc spans of a command in a substitution body now point into the submitted source.

## Security and compatibility

- An unterminated body, a delimiter line with other text, and the heredoc forms that the top level rejects (for example two heredocs on one command) stay unparseable.
- Only inputs that were unparseable change. A replay of 6,912 complete Netclaw shell calls gives 1 more parsed row and no worse row.
- There is no public API change.

#### 0.4.0-beta.14 2026-10-03 ####

This prerelease parses Bash background lists (#215). A single `&` no longer makes the whole source unparseable.

## Added

- A single `&` ends the and-or list before it. The parser wraps that list in a `GroupSyntax` with the new `ShellGroupKind.Background`. Every command in it and after it is a normal occurrence.
- `$!` and `$?` in an assignment value give an `Unknown` binding. `server & PID=$!; kill "$PID"` now parses.

## Security and compatibility

- The background list runs in an asynchronous subshell. Its directory changes and assignments do not reach the next command, and the list has exit status zero.
- An `&` in a wrong position stays unparseable.
- Only inputs that were unparseable change. A replay of 6,912 complete Netclaw shell calls gives 9 more parsed rows (every background row) and no worse row.
- The public API change is additive: `ShellGroupKind.Background`. A consumer that checks group kinds must accept it, or fail closed on it.

#### 0.4.0-beta.13 2026-10-03 ####

This prerelease parses Bash `while`, `until`, `if`, and `case` statements (#212). Each command inside them is a normal occurrence.

## Added

- Public syntax nodes `ConditionLoopSyntax` (with `ConditionLoopKind`), `ConditionalSyntax`, `ConditionalBranchSyntax`, `CaseSyntax`, and `CaseItemSyntax`.
- The occurrence role `Condition` for a command in a loop or branch condition, and `Branch` for a command in an `if` or `case` body. The ancestry regions `Condition` and `Branch` carry the branch or item index.
- A bounded `read` builtin under `FreshNonInteractiveNoStartup`: each name gets an `Unknown` value and a `ShellState` assignment fact. `grep x f | while read l; do echo "$l"; done` now parses.
- A lone `[` is a static program word, so `[ -d /x ]` parses as the test builtin.

## Security and compatibility

- The state pass joins the facts of each path. A directory or a binding that differs between branches or loop iterations is `Unknown`. An `elif` condition runs only after the earlier condition fails, and an `if` without `else` or a `case` with no match can skip every body.
- These forms stay unparseable: a malformed statement, `select`, `[[`, a redirect after `done`, `fi`, or `esac`, the `;&` and `;;&` case terminators, a substitution in a case subject or pattern, `read -a`, `-e`, `-i`, `-p`, `IFS= read`, and nesting past the structural limit of 16. Deep nesting fails closed without recursion past the limit.
- `echo [` now gives a literal `[` argument (`Kind=Literal`, `IsPath=false`) instead of a glob. No other parsed record changes.
- A consumer that checks ancestry node types must add the new node types and the new role and region values. An unknown type or value must fail closed, as before.
- The public API change is additive.

#### 0.4.0-beta.12 2026-10-03 ####

This prerelease accepts more Bash assignment shapes as bounded facts (#209). Each command in them is a normal occurrence, and a value that the parser cannot prove is `Unknown`.

## Added

- A shell-state assignment can use an upper-case name. The name gate is the same as for a prefix: shell-owned, command-resolution, startup, and loader names stay rejected.
- More than one shell-state assignment, a reassignment, and assignments anywhere in a `;`, `&&`, or `||` list, at the top level and in a `for` loop body. A later occurrence lists every live assignment, one for each name.
- A value can have double quotes, `$name` of a bound or launch variable, a leading `~` from the live launch `HOME`, and `$(...)`. `x=$(cmd)` gives the occurrence `cmd` with the role `Substitution`, and `x` is `Unknown`.
- A command-environment prefix can be on a pipeline stage, a list item, or a command in a loop body, a subshell, or a substitution.

## Security and compatibility

- The shapes need `FreshNonInteractiveNoStartup`, as before.
- The parser still rejects: a read of an unproved name; `~` after the start of a value and `~user`; backticks, escapes, ANSI-C and locale quotes, arithmetic, and complex parameter expansion; an assignment-only statement in a pipeline, subshell, substitution, or decoded `bash -c` child; a prefix in a decoded child; an assignment to a loop binding or to a live launch name inside a loop; a `for` binding that reuses an assigned name; and `wait` with an option after an assignment.
- A binding follows the control flow. After `probe || x=1`, `x` is not proved. A `for` loop joins the values of each iteration.
- `ShellVariableAssignment.AuthoredValue` and `EffectiveValue` can now be `Unknown`. A consumer that expected only `Exact` must handle `Unknown`.
- Only inputs that were unparseable change. A replay of 6,912 complete Netclaw shell calls gives 79 more parsed rows and no worse row.
- There is no public API change.

#### 0.4.0-beta.11 2026-10-03 ####

This prerelease lets a consumer read more real commands: a tilde program word, a glob word, and a `bash script.sh` call (#206).

## Added

- A leading `~` or `~/` in a Bash program word expands from a live launch `HOME`. `~/.dotnet/tools/ilspycmd -h` gives the command words `<HOME>/.dotnet/tools/ilspycmd`. `~user`, `~+`, a quoted tilde, and a revoked `HOME` do not expand.
- Pathname-expansion facts. Each Bash glob argument and each glob file-redirect target gets a `ShellValueDomain.PathPattern` value with the new `Glob` fact (`ShellGlobExpansion`). The fact gives the covering directory (with `~` expanded from the live `HOME`), the segments below it (`ShellGlobSegment`), the segment depth, a dot fact for each segment, and `MayStartWithDash`. `ls -d ~/repositories/*/akka*` gives covering directory `<HOME>/repositories` and depth 2. A glob redirect target is now complete, so `echo hi > /tmp/x/*.log` has known command words.

## Fixed

- The script operand of `bash` or `sh` ends the command-string scan. `bash scripts/audit.sh --repo-root ~/repositories` is now complete, with the command words `bash`. In 0.4.0-beta.10, each later word could select command-string mode, so the occurrence was incomplete. `-o`, `-O`, `+o`, `+O`, `--rcfile`, and `--init-file` take the next word as a value. `bash -c '...'` decoding does not change.

## Not changed

- Command words do not stop at a word that names a file. `git show dev:Directory.Build.props` keeps `dev:Directory.Build.props` as a command word. No shell fact proves that a word with a dot or a colon names a file. The file system or the program grammar decides that. A dot rule would also drop operands that are not files, such as `nginx.service` or `host.example.com`, and widen the grants that use them.

## Security and compatibility

- The glob facts need `FreshNonInteractiveNoStartup` and a `LaunchEnvironment` with at least one live fact. That contract fixes the shell options: `globstar`, `dotglob`, `nullglob`, `failglob`, `nocaseglob`, and `extglob` are off, and `globskipdots` is on. The source cannot change them: `set`, a mutating `shopt`, `source`, and `eval` fail the parse. A decoded `bash -c` child gets no glob facts.
- A glob word stays unresolved when it has a `..` segment, an empty or `.` segment after the first pattern segment, a variable, a command substitution, a brace, a quoted or escaped wildcard, or a backslash. A relative glob needs the caller `WorkingDirectory`. The parser does not read the file system.
- With no `LaunchEnvironment` or an empty one, the only output change is the script-operand rule. No corpus input changes.
- A consumer that adopts the glob facts must check protected paths against the whole covering subtree to the segment depth. For example, `cat ~/.netclaw/*/tool-approvals.json` has covering directory `~/.netclaw` and depth 2.
- The public API change is additive: `ShellValueDomain.PathPattern.Glob`, `ShellGlobExpansion`, and `ShellGlobSegment`.

#### 0.4.0-beta.10 2026-10-03 ####

This prerelease fixes the resolved path of a relative Bash `cd` operand.

## Fixed

- A bare relative `cd` operand now gets a resolved path when the launch facts prove that `CDPATH` is unset (#203). In 0.4.0-beta.9, `cd sub && cat f` gave the correct directory `<cwd>/sub`, but `Arg.Resolved` on `sub` was empty. `sub` now resolves to the same path as the computed directory. `cd a && cd b` resolves `b` to `<cwd>/a/b`.

## Security and compatibility

- With no `LaunchEnvironment`, the output is identical to 0.4.0-beta.9. A comparison of the complete public output for all corpus inputs shows no change.
- With launch facts, only the resolved path of the `cd` operand changes. The working directories and all other facts stay the same.
- The operand stays unresolved when `CDPATH` is not proved unset, when a statement can change `CDPATH`, under the `Unknown` initial-state mode, and with no `WorkingDirectory`. `cd -`, `cd -P`, `cd -@`, dynamic operands, and a decoded `bash -c` child also stay unresolved.
- PowerShell has no `CDPATH` search. `Set-Location sub` already gave a resolved path, so PowerShell does not change.
- There is no public API change.

#### 0.4.0-beta.9 2026-10-03 ####

This prerelease lets a caller supply environment facts that its process launcher proves. With them, the parser resolves more real commands.

## Added

- Add `ShellParserOptions.LaunchEnvironment` and the `ShellLaunchEnvironment` class (#200). The caller states which variables are set, exported, and scalar, with exact values, and which variables are unset.
- Resolve a supplied Bash variable in `$NAME`, `${NAME}`, and `"$NAME/x"` forms. The value goes into path facts, `cd` targets, the working directory after `cd`, redirect targets, and command words. `cd "$TMPDIR/out" && sed -n 1,2p f` runs `sed` in `<TMPDIR>/out`.
- Resolve `$HOME`, `~`, and `cd` with no operand from a supplied `HOME`.
- Resolve a relative Bash `cd` when the caller sets `WorkingDirectory` and proves that `CDPATH` is unset. `cd src && make build` runs `make` in `<cwd>/src`. `cd a && cd b` resolves each step.
- Resolve a program path from a supplied value. `"$TMPDIR/tool" arg` gives the command words `<TMPDIR>/tool arg`. The value must contain `/`.
- Resolve a supplied PowerShell `$env:NAME` or `${env:NAME}` value. The parser uses the existing rule for `$env:USERPROFILE`: no earlier command can have changed process-wide state.

## Security and compatibility

- With no caller option, the output is identical to 0.4.0-beta.8. The complete corpus projection gives the same result with no option and with an empty launch environment.
- The parser uses the facts only under `FreshNonInteractiveNoStartup` or `IsolatedNonInteractive` for Bash, and `IsolatedNonInteractiveNoProfile` for PowerShell. Startup content can change any variable.
- A statement that can change a variable revokes the supplied facts: an unmodeled variable mutation, `wait` with an option, a shell-state assignment, or a `for` binding with the same name. A revoked `HOME` makes `~` unknown. It does not fall back to a default.
- A variable that the caller did not supply behaves as before. A decoded `bash -c` child and a heredoc body get no launch facts.
- An unquoted value that can split or glob is not one proved word. A path from a launch value must be absolute.
- The parser rejects names that the shell owns or can change at startup, such as `PATH`, `PWD`, `IFS`, `BASH_ENV`, and `PSModulePath`. It also rejects a supplied `HOME` that is empty or that disagrees with `HomeDirectory`.
- A resolved launch word keeps `ArgKind.EnvVar`. `Arg.Resolved` holds the substituted path.
- The public API change is additive. The facts grant no authority.

#### 0.4.0-beta.8 2026-10-03 ####

This prerelease adds a position rule to `CommandWords`, so option values no longer become command words.

## Changed

- Add the verb slot: the first command word after the program word (#197). Until it is filled, the beta.7 rules apply.
- After the verb slot, skip a plain word directly after an option as that option's value. `dotnet build -c Release` gives `dotnet build`, and `git commit -m fix` gives `git commit`.
- After the verb slot, skip expansions, split words, brace lists, and bare globs as arguments. `git add *` gives `git add`.
- Plain words that do not follow an option stay. `git push origin feature-x` is unchanged.

## Fixed

- A brace list that the parser reports as a resolved path no longer hides a verb. In 0.4.0-beta.7, `git {push,a/b}` gave `git`, but Bash runs `git push a/b`. It now gives `Unknown`.

## Security and compatibility

- The verb slot keeps the strict rules: `git -p filter-branch`, `git $SUB`, `git *`, `git {push,log}`, and `git "push"` give the same results as in beta.7.
- Limit: after the verb slot, a sub-subcommand that follows an option is skipped. `git remote -v add evil url` gives `git remote evil url`. The top-level verb stays protected.
- `git log --oneline main` gives `git log`, because `main` follows an option after the verb slot.
- `rm -f {a,b}.txt` and `du -sh *` give `Unknown`, because the verb slot is still empty.
- The public API does not change.

#### 0.4.0-beta.7 2026-10-02 ####

This prerelease adds a command-words fact and reports more general path operands.

## Added

- Add `CommandOccurrence.CommandWords` and the closed `ShellCommandWords` family (#194). `Known.Words` gives the program word, then every later plain literal word. `gh -R o/r pr view 123` and `gh pr view 123 -R o/r` both give `gh pr view`.
- Skip options, paths, globs, words with a digit, text with whitespace, and redirect targets. PowerShell parameter tokens are options.
- Count a quoted single word as a command word. `git "push"` and `git \push` give `git push`.
- Return `Unknown` for an expansion outside an option or a path, such as `git $(cmd)` or `git {push,log}`, and for any word that can split into more words.
- Return `Unknown` for a bare glob such as `*` or `p?sh`, because the shell can replace it with any file name. A glob that contains `/`, such as `./*`, is skipped as a path pattern. `du -sh *` gives `Unknown`, and `du -sh ./*` gives `du`.
- Keep a plain word after an option, because the parser cannot tell an option value from a subcommand. `pgrep -x name` gives `pgrep name`.

## Fixed

- Report the bare operands `.` and `..` as paths for a program without a per-verb rule (#193). `df -h .` now reports `.` as a resolved path.
- Report an unquoted glob such as `*` or `*.cs` as a path pattern for a Bash command or a PowerShell native command without a per-verb rule. `du -sh *` now reports `*` with `IsPath = true`.

## Security and compatibility

- `CommandWords` is `Unknown` when the occurrence is incomplete or its command name is dynamic.
- `CommandWords` is a parser fact. It does not grant authority.
- A new glob path fact keeps `Kind = Glob`, `Resolved = null`, and an `Unknown` value. It adds no tree access and no loop pattern.
- A quoted or escaped `*` stays data. Per-verb rules such as `grep` patterns and `curl` URLs still win.
- After an unknown `cd` target, `.` becomes `DynamicSkip`, the same as `./x`.
- The public API change is additive.

#### 0.4.0-beta.6 2026-09-30 ####

This prerelease accepts more than one bounded Bash assignment prefix before one external command.

## Fixed

- Parse `X=1 Y=2 ls -la` and other simple commands with one or more command-environment prefixes (#189).
- Publish one exact `CommandEnvironment` fact for each prefix in source order.
- Keep the command name and arguments identical to the single-prefix result.

## Security and compatibility

- Apply the existing name and value gates to each prefix. One unbounded value, such as `Y=$(id)`, rejects the complete input.
- Reject repeated prefix names, because only the last value reaches the command.
- Keep assignment-only lists such as `X=1 Y=2`, redirects before the command name, builtins, wrappers, pipelines, and condition lists fail closed.
- Require `FreshNonInteractiveNoStartup` as before. `Unknown` and `IsolatedNonInteractive` still reject every prefix.
- Preserve the public API exactly. This release changes parser behavior only.

#### 0.4.0-beta.5 2026-09-22 ####

This prerelease accepts PowerShell horizontal whitespace around a bounded scalar assignment operator.

## Fixed

- Accept ASCII spaces or tabs before and after one exact `=` in the bounded PowerShell assignment form.
- Preserve the full exact assignment source span, including accepted whitespace.

## Security and compatibility

- Reject comments, continuations, newlines, Unicode whitespace, compound operators, chained or tuple targets, and unsupported right-hand sides.
- Keep provider, scoped, typed, member, indexed, redirected, piped, grouped, and multi-command assignment forms fail closed.
- Preserve the public API exactly. This release changes parser behavior only.

#### 0.4.0-beta.4 2026-09-21 ####

This prerelease publishes bounded assignment facts for simple Bash and PowerShell commands.

## Added

- Add exact `ShellVariableAssignment` facts to each affected command occurrence.
- Distinguish persistent shell state from one-command Bash environment prefixes.
- Add a fresh Bash process mode with an explicit startup and option baseline.
- Add one isolated PowerShell scalar assignment before one ordinary command.

## Security and compatibility

- Keep unknown state, dynamic values, substitutions, arrays, redirects, pipelines, groups, and unsupported scopes fail closed.
- Reject Bash tilde, ANSI-C quote, locale quote, joined quote, and other shell-native value expansions.
- Reject Bash builtins, shell-owned names, lookup variables, startup variables, loader variables, and future special-name families for prefixes.
- Require the PowerShell assignment as the first statement and exactly one ordinary command after it.
- Expose every accepted assignment to the consumer. The new facts grant no authority.

#### 0.4.0-beta.3 2026-09-17 ####

This prerelease adds bounded Bash directory facts for complete static compounds.

## Added

- Add `BashParser.TryProjectFiniteScopes` and parser-owned scoped occurrences.
- Preserve exact path facts for each reachable directory after `&&`, `||`, and `;`.
- Preserve both success and failure directories after an exact `cd` target.

## Security and compatibility

- Reject unknown directory effects, nested execution, changed source slices, and excess scopes.
- Keep dynamic operands unknown inside an exact directory.
- Preserve the existing parser result and public methods. The new API grants no authority.

#### 0.4.0-beta.2 2026-09-14 ####

This maintenance prerelease publishes the post-beta.1 PowerShell approval
projection cases verified from live Netclaw prompts.

## Added

- Handle the remaining split/index/join projection as a bounded no-command
  case, preserving authored data while exposing no executable command.
- Add sanitized corpus coverage for the live PowerShell prompt shapes now
  handled by the parser.

## Security and compatibility

- Keep dynamic and executable split/index/join variants fail closed as
  unparseable or dynamically skipped rather than guessing their meaning.
- Preserve the beta.1 public API and all existing Bash and PowerShell facts.

#### 0.4.0-beta.1 2026-09-14 ####

This implementation slice adds bounded PowerShell approval-fatigue evidence
while keeping unsupported mutation syntax fail closed.

## Added

- Add `ShellValueDomain.OrderedList` for audited, source-ordered PowerShell
  collections, plus source-authentic `Select-Object -Index` integer ranges.
- Add `CommandOccurrence.FileSystemTreeAccesses` and the closed
  `ShellFileSystemTreeAccess` / `ShellTreeTraversalMode` contract for audited
  `Get-ChildItem` tree-access effects, including dialect-specific link
  traversal and explicit Unknown markers.
- Pin independently proved Windows PowerShell 5.1 callback receiver binding
  for `ForEach-Object` and `Where-Object`.

## Fixed

- Reject authored prefix and postfix `++` / `--` mutations inside supported
  PowerShell script-block regions until their state transfer is modeled.
- Retain only exact-span direct-source sibling leaves for the paired
  diagnostic result; clear commands, clauses, arguments, redirects, and all
  positive authorization facts so the result can only support an additive
  consumer hard-deny scan.

## Security and compatibility

- Keep wrapper-decoded diagnostic leaves spanless and preserve atomic failure
  for unbalanced, unsupported, or resource-limit syntax.
- Keep tree-access evidence executable-effect data only; it grants no path
  authority and remains Unknown for dynamic, ambiguous, non-filesystem, or
  unproved roots and traversal controls.
- Upgrade `Microsoft.SourceLink.GitHub` to 10.0.400 to restore the package
  supply-chain fix tracked by Dependabot PR #156.

#### 0.3.5 2026-08-14 ####

This additive release distinguishes audited non-filesystem operand data from
compatibility path heuristics. Security consumers can stop treating bounded
translation sets as filesystem scope without relaxing independent shell facts.

## Added

- Add `AnalyzedArgument.AuthoredNonFileSystemValue`. The non-null default is
  `Unknown`; positive `Exact` and `FiniteSet` values require an audited operand
  binding.
- Add one audited Bash `tr` entry. It proves bounded options and translation
  sets are non-filesystem data.

## Fixed

- Keep `tr` as a single-token command identity, so `tr abc def` retains both
  arguments.
- Stop classifying the quoted `\n` translation set as a compatibility path
  that resolves to a false `n` child scope.

## Security and compatibility

- Keep `AuthoredPathShape` independent. The exact `\n` word remains
  Windows-shaped lexical evidence.
- Let consumers omit only the same argument's broad compatibility path facts
  after a positive non-filesystem proof.
- Preserve redirects, substitutions, command effects, completeness, working
  directories, and every other argument as independent policy inputs.
- Preserve every 0.3.4 public signature. The new property is additive.

#### 0.3.4 2026-08-13 ####

This additive release publishes parser-owned working-directory effects for
each executable command occurrence. Security consumers can reason about a
causal directory transition without reconstructing shell builtins or treating
the syntax fact as authority.

## Added

- Add the closed `ShellWorkingDirectoryEffect` family with `Unknown`,
  `Unchanged`, and `ChangesOnSuccess(Target)` alternatives.
- Add `CommandOccurrence.WorkingDirectoryEffect` with a non-null `Unknown`
  default for compatibility and malformed internal facts.
- Publish bounded Bash `cd`, `command cd`, and `builtin cd` effects while
  retaining strict `pushd`, `popd`, hidden execution, and invalid-shape
  boundaries.
- Publish selected-dialect native PowerShell `Set-Location` effects from the
  same parameter-binding pass that computes success and failure flow.

## Security and compatibility

- Keep authorization, path containment, prerequisite coverage, ancestry, and
  real fallback-directory checks in the consumer.
- Require normalized absolute local targets. Reject relative, non-normalized,
  mixed-style, malformed, or unsupported target domains as `Unknown`.
- Preserve atomic failure for hidden Bash execution and strict PowerShell
  location-stack, provider, script, dynamic-identity, and unmodeled-region
  boundaries.
- Preserve all 0.3.3 public signatures. Both target-framework API comparisons
  report five additive members and zero breaking changes.

## Consumer validation

- Add input/output examples and default-deny pseudocode to the consumer guide.
- Add sanitized Bash corpus cases for causal chains, directory-stack
  invalidation, and finite loop targets.
- Validate native PowerShell aliases, common parameters, unambiguous aliases
  and prefixes, dynamic switch values, invalid switch values, and bounded or
  unknown targets.

#### 0.3.3 2026-08-13 ####

This additive release publishes bounded local-filesystem values for audited
authored arguments. It lets security consumers evaluate static loop operands
through their own path policy without treating broad compatibility heuristics
or pre-field-splitting words as filesystem authority.

## Added

- Add `AnalyzedArgument.AuthoredFileSystemValue`. The non-null default is
  `Unknown`; v0.3.3 publishes only normalized `Exact` and `FiniteSet` domains.
- Add a separate audited local-filesystem binding catalog. The initial entries
  cover Bash `cat` operands and the selected PowerShell dialect's exact
  `Get-Content -LiteralPath` value.
- Add generated Bash and PowerShell corpus expectations plus consumer-guide
  input/output examples for bounded loops, inline parameters, filters,
  rename fragments, remote endpoints, and transform-sensitive words.

## Fixed

- Keep slash-prefixed OpenSSL `-subj` Distinguished Names classified as
  non-path data in Bash and native PowerShell.
- Preserve subcommand-specific OpenSSL boundaries: `x509 -serial` remains a
  valueless switch and `ca -key` gains no universal filesystem role.

## Security and compatibility

- Require an audited parser-owned operand binding, exact one-field transform
  proof, an exact occurrence working directory, and successful local path
  resolution before publishing the stronger filesystem value.
- Do not derive the fact from compatibility `IsPath`, `FileVerbs`, lexical
  path shape, generic positional fallback, or executable-private policy.
- Keep active field splitting, pathname expansion, stream sentinels, unknown
  cwd, remote endpoints, non-filesystem providers, dynamic identity,
  substitutions, unresolved redirects, incomplete control flow, and
  over-limit unions strict.
- Preserve all 0.3.2 public signatures. Both target-framework API comparisons
  report exactly one additive member: `AuthoredFileSystemValue`.

## Consumer validation

- Require consumers to accept only `Exact` or `FiniteSet`, check every path
  through their own trust-zone policy, and independently enforce occurrence
  completeness, identity, redirects, substitutions, ancestry, and every other
  argument.
- Update the sanitized D14 approval evidence so downstream Netclaw validation
  can bind the new parser fact without accepting raw `AuthoredValue` as a path.

#### 0.3.2 2026-08-12 ####

This patch release accepts statically proved Bash command identities that use
the current user's home-tilde prefix. It preserves the existing fail-closed
boundary for command identities that remain dynamic.

## Fixed

- Parse eligible `~/...` executable paths as static command identities when a
  home directory is available to the resolver.
- Preserve quoted or escaped tilde text as literal command identity.

## Security and compatibility

- Keep named-user tilde, variable-derived identity, unknown home state, and
  other unresolved executable forms unparseable.
- Keep authored-only loop publication scoped to the source submitted to the
  parser. Decoded `bash -c` and `sh -c` children require their own proved
  isolated state instead of inheriting the outer opt-in.
- Preserve the public API and package layout from 0.3.1.

## Consumer validation

- Add direct positive and negative parser cases plus existing structural,
  native Bash oracle, public API, and full-suite validation.
- Unblock Netclaw approval analysis for ordinary agent commands that invoke a
  static executable beneath the current user's home directory.

#### 0.3.1 2026-08-11 ####

This additive release supplies bounded shell facts needed to reduce approval
fatigue without moving product policy into ShellSyntaxTree. Existing parser
options preserve every 0.3.0 default.

## Added

- Add library-owned `ShellValueDomain.IntegerRange` and `Concatenation`
  alternatives. Double-quoted Bash `$?` is `IntegerRange(0, 255)` and quoted
  literal-plus-status words remain symbolic without enumerating their product.
- Add `AnalyzedArgument.AuthoredValue` for the bounded pre-field-splitting
  authored word and `AuthoredPathShape` for lexical POSIX or Windows shape.
  Path shape is not filesystem operand semantics and never grants authority.
- Add default-false `BashParserOptions.PublishAuthoredSourceFacts`. Opted-in
  consumers can inspect finite authored words in supported static loops while
  effective values, redirects, cwd, dynamic identities, hidden execution, and
  unsupported regions remain conservative.

## Security and compatibility

- Preserve 0.3.0 behavior when the new option is not selected. Static Bash
  loops under unknown initial state remain unparseable with empty command and
  clause projections.
- Keep unquoted status, positional parameters, redirect targets, computed
  identities, runtime iterators, explicit attribute mutation, ambient values,
  and unknown future value alternatives fail closed.
- Preserve PowerShell behavior exactly: `AuthoredValue` equals the existing
  effective `Value`, and `AuthoredPathShape` is `Unknown` in 0.3.1.

## Consumer validation

- Add exact sanitized D02, D10, and D14 approval-harvest regressions, native
  Bash status and tilde oracles, public API and malformed-projector snapshots,
  PowerShell compatibility coverage, and PII audit coverage for OpenSpec
  evidence.
- Expand the consumer guide with input/output examples, an explicit authored-
  source threat-model boundary, lexical-shape counterexamples, and a recursive
  fail-closed value-domain switch.

#### 0.3.0 2026-08-11 ####

This stable release promotes the complete v0.3 structured shell-analysis
contract validated by the `0.3.0-alpha.*` series. It preserves the stable v0.2
public API and conservative `Clauses` projection while adding a typed,
occurrence-oriented authorization surface. Unknown, incomplete, dynamic, and
unsupported execution-bearing forms remain fail closed.

## Added

- Add `ParsedCommand.Syntax` for display and diagnostics and
  `ParsedCommand.Commands` for authorization. Every supported authored command
  occurrence carries its structural role and ancestry, completeness,
  effective arguments, working-directory domain, and explicit redirects.
- Add closed, parser-owned record families for bounded values, redirect
  operations and sources, command substitutions, heredocs, here-strings, and
  PowerShell command-owned execution regions. Unknown runtime alternatives and
  enum values remain detectable by consumers.
- Add bounded Bash `for ... in` and PowerShell `foreach` analysis, including
  occurrence-specific exact, finite, pattern, joined-state, substitution, cwd,
  and redirect facts under the documented initial-state contracts.
- Add explicit PowerShell 7 and Windows PowerShell 5.1 dialect selection.
  Same-language static child hosts use the matching dialect; Bash and
  PowerShell remain separate top-level grammars and never cross-parse each
  other's command payloads.

## Security and compatibility

- Preserve the stable v0.2 `ParsedCommand`, `Clause`, `Arg`, and `Redirect`
  contract and its conservative projection throughout v0.3. The experimental
  `0.3.0-alpha.*` API is not a compatibility boundary and has no retained
  aliases or adapters.
- Keep incomplete executable regions, computed identities, hidden execution,
  unbounded values, unknown cwd or redirect targets, unsupported syntax, and
  source-observed state invalidation strict. Consumers can prompt or deny;
  ShellSyntaxTree never converts uncertainty into authority.
- Require the component that selects the executor to select the matching
  parser, PowerShell dialect, and initial-state assertion. A fallback executor
  must be reparsed and reauthorized before execution.
- Keep executable-specific normalization and filesystem authority in the
  consumer. ShellSyntaxTree supplies syntax facts; it does not define a policy
  engine or a stable serialized wire format.

## Consumer validation

- Expand the consumer guide with input-to-output examples for occurrences,
  arguments, loops, substitutions, redirects, cwd propagation, PowerShell
  execution regions, safe-fail handling, and the separation between syntax
  display and authorization traversal.
- Validate the release candidate with 2,870 ShellSyntaxTree tests, native Bash
  and PowerShell oracles, PII-audited corpora, public-API snapshots, package
  creation, strict OpenSpec validation, and Linux plus Windows CI.
- Validate the corrected prerelease in Netclaw through 240 approval-catalog
  rows: 199 Bash, 36 PowerShell 7, and five Windows PowerShell 5.1 cases. The
  downstream matrix covers ordinary commands, redirects, loops,
  substitutions, execution regions, stored grants, hard denies, and
  fail-closed unknown forms.

#### 0.3.0-alpha.6 2026-08-10 ####

## Changed

- Replace the experimental v0.3 sparse `EffectiveArguments` coordinate overlay
  with one parser-owned `AnalyzedArgument` per authored non-cwd argument. Each
  entry directly references its `Arg`, source `ClauseElement`, and effective
  value, including many-to-one inline option bindings.
- Replace the experimental value, redirect-source, and redirect-operation
  property bags with closed record families intended for runtime type
  matching. Ancestry frames now reference actual syntax nodes, and execution
  regions reference their actual host argument.
- Remove alpha-only public syntax kinds, condition/branch vocabulary, analysis
  limits, and other shapes the stable parser never emits. Every new v0.3 result
  is parser-owned and every read-only list introduced by v0.3 is defensively
  backed; stable v0.2 construction and list semantics remain unchanged.

## Consumer migration

- v0.3 security consumers authorize `ParsedCommand.Commands` and use
  `ParsedCommand.Syntax` only for display and diagnostics. The conservative
  v0.2 `Clauses` projection remains supported throughout v0.3, including every
  v0.3.x release; no removal version is scheduled.
- The new v0.3 records and `ParsedCommand` members change generated record
  equality, hashing, `ToString()`, and reflection-based serialization output.
  ShellSyntaxTree does not define a stable serialized wire format. Persisted
  results require a consumer-owned, versioned DTO or explicit serializer
  mapping that fails closed on unknown runtime alternatives and enum values.
- No source or binary compatibility is provided for `0.3.0-alpha.*` packages.
  Stable v0.2 remains the compatibility boundary. Alpha consumers must migrate
  to `CommandOccurrence.Arguments` and pattern-match the closed value and
  redirect families; no aliases or obsolete adapters preserve the old model.
- `PwshParserOptions.Dialect` is additive and defaults to `PowerShell7` for
  compatibility. Native Windows consumers select it only for a compatible
  PowerShell 7.6 host (`>=7.6.4` and `<7.7`) and select
  `WindowsPowerShell51` when falling back to `powershell.exe`; the host,
  parser dialect, approval policy, and executor identity must agree. The new
  property also participates in options-record equality, hashing, `ToString()`,
  reflection, and default serialization shape.

#### 0.3.0-alpha.5 2026-08-10 ####

This prerelease adds an explicit host-selected PowerShell dialect contract and
keeps Bash and PowerShell analysis at the native host boundary. The v0.2
projection remains supported, and the public API changes are additive.

## Added

- Add `PwshDialect` and `PwshParserOptions.Dialect`, defaulting to the
  compatible PowerShell 7 behavior and failing closed for unknown enum values.
- Model Windows PowerShell 5.1 as an explicit native-Windows fallback with its
  own alias catalog, receiver conservatism, and rejection of unsupported
  `&&` / `||` pipeline-chain syntax.
- Carry the selected dialect through nested PowerShell parsing and select the
  matching dialect for static same-language `pwsh` and `powershell.exe` child
  hosts.

## Security and compatibility

- Keep shell languages separate: Bash treats `pwsh` as an ordinary external
  command, and PowerShell treats `bash` as an ordinary external command. The
  parser never interprets a child command string in the other language.
- Require consumers to select `PowerShell7` only for a compatible PowerShell
  7.6 host (`>=7.6.4` and `<7.7`) and use `WindowsPowerShell51` for the native
  `powershell.exe` fallback. Host selection, parser dialect, approval policy,
  and executor identity must agree.
- Preserve the v0.2 compatibility projection and report exactly two additive
  public API changes: the dialect enum and the parser-options property.
- Expand the generated PowerShell corpus to 504 entries. Linux and Windows CI
  validate it with hash-pinned PowerShell 7.6.4; Windows additionally validates
  the Windows PowerShell 5.1 dialect with the native executable.

#### 0.3.0-alpha.4 2026-08-09 ####

This prerelease corrects PowerShell occurrence completeness to describe the
authored command text instead of ambient command resolution. It does not
change the public v0.3 API surface, and the conservative v0.2 projection
remains available.

## Changed

- Keep static authored PowerShell commands, pipeline stages, decoded child
  commands, and proved script-block receivers complete under the default
  initial-state mode.
- Use an explicit native or `.ps1` path spelling to select authored argument
  binding without inspecting the executable, `PATH`, profiles, modules,
  aliases, functions, inherited variables, or prior runspace state.
- Keep unqualified `.ps1` binding, dynamic command identities, computed
  execution, unknown receivers, and unsupported executable syntax strict.

## Security and compatibility

- Preserve `Unknown` effective loop values when the submitted source does not
  prove the runtime binding value.
- Invalidate later command proofs after a matching mutation that is visible in
  the submitted source.
- Keep provider-sensitive repeated loops incomplete when an unknown path could
  mutate command resolution before a later visit.
- Preserve all public signatures, the v0.2 compatibility projection, and the
  491-case generated PowerShell corpus.

#### 0.3.0-alpha.3 2026-08-09 ####

This prerelease adds the PowerShell state and value proofs needed for the
Netclaw approval-policy integration. It does not change the public v0.3 API
surface, and the conservative v0.2 projection remains available.

## Added

- Apply the explicit PowerShell initial-state contract to command identity,
  native-versus-cmdlet argument binding, working-directory attribution,
  redirects, automatic `HOME`, and `USERPROFILE` values.
- Preserve argument-binding provenance through static current-scope
  `Invoke-Expression` payloads while keeping decoded child-host state isolated.
- Expand the generated PowerShell corpus from 422 to 491 entries with direct,
  wrapper, alias, script, redirect, child-host, and unknown-state cases.

## Security and compatibility

- Treat aliases as capable of shadowing built-ins and path-shaped command
  names; exact argument binding requires constrained, unmutated command
  resolution.
- Invalidate following authorization state after uninspected scripts and
  unproved in-process invocations instead of retaining stale exact values.
- Keep supported non-pipeline bodies of unknown receivers visible and
  incomplete, while failing an unproved interior pipeline atomically with
  empty authorization projections.
- Preserve ordinary v0.2 compatibility leaves under ambient uncertainty and
  keep the public v0.3 API snapshot unchanged.

#### 0.3.0-alpha.2 2026-08-09 ####

This prerelease completes the stable-v0.3 boundary between PowerShell script
blocks proved to be data and blocks that may execute. It does not change the
public v0.3 API surface, and the conservative v0.2 projection remains
available.

## Added

- Keep script blocks passed to a proved `Write-Output` receiver opaque under a
  constrained PowerShell baseline instead of inventing nested command
  occurrences.
- Preserve unknown script-block receivers as visible, incomplete execution
  regions, and expose proved local `Invoke-Command` bodies as synchronous
  command occurrences.

## Security and compatibility

- Require bounded command-resolution proof before classifying a script block as
  data. Default runspace state remains conservative.
- Track exact command mutations through authored and canonical alias identities.
  This prevents exact module-qualified-looking aliases and `echo` alias chains
  from hiding an executable script block.
- Preserve unrelated-name precision and reset runspace-local mutations at fresh
  parallel child-runspace boundaries without clearing process-wide uncertainty.
- Expand the generated PowerShell corpus to 422 entries, all validated against
  the live PowerShell parser and the PII audit.

#### 0.3.0-alpha.1 2026-08-09 ####

This prerelease refreshes the Netclaw validation package with the Bash
redirect and command-resolution slices completed after `0.3.0-alpha`. It does
not change the public v0.3 API surface, and the conservative v0.2 projection
remains available.

## Added

- Added bounded Bash heredoc analysis with explicit delimiter, body, expansion,
  tab-stripping, completeness, and substitution facts.
- Added Bash `<<<` here-string analysis. Exact and finite data include Bash's
  trailing newline, remain non-path, and preserve independently executable
  substitutions as command occurrences.

## Security and compatibility

- Reject command-resolution mutation through unsupported `exec`, `hash`,
  alias, shell-option, builtin-enable, and reserved execution forms before a
  later occurrence can inherit an unsafe executable identity.
- Keep unknown here-string values structurally visible without guessing their
  data, and fail malformed redirect forms atomically.
- Pin the unchanged public API with reflection, equality, hashing, string, and
  unknown-enum compatibility tests, and document occurrence-first consumer
  authorization.

#### 0.3.0-alpha 2026-08-08 ####

This prerelease exposes the v0.3 structured-analysis API for Netclaw
integration. It keeps the v0.2 compatibility projection for existing
consumers. Unknown or unsupported forms continue to fail closed.

## Added

- Added `ParsedCommand.Syntax` and `ParsedCommand.Commands`. Consumers can now
  inspect every supported command occurrence in nested shell structure.
- Added typed syntax nodes, occurrence roles, ancestry, completeness facts,
  value domains, and explicit redirect analysis.
- Added bounded Bash `for ... in` and PowerShell `foreach` analysis. Exact and
  finite loop values require the documented isolated initial-state modes.
- Added command-substitution and PowerShell execution-region discovery for the
  supported v0.3 grammar.
- Added explicit file, stream, and descriptor redirect facts. Static descriptor
  operations no longer require a consumer to infer safety from raw text.

## Compatibility and security

- Kept all v0.2 `ParsedCommand.Clauses`, `Clause`, `Arg`, and `Redirect`
  members. The compatibility projection remains conservative.
- Kept incomplete occurrences, unknown values, dynamic command identities,
  and unsupported execution-bearing syntax fail closed.
- The corpus now contains 268 Bash cases and 417 PowerShell cases. Both corpora
  pass the PII audit. Every PowerShell input has a real-`pwsh` parse check,
  and targeted real-Bash tests pin supported Bash semantics.

#### 0.2.0 2026-08-05 ####

This stable release includes all behavior and API surface from the
`0.2.0-alpha` and `0.2.0-beta.1` prereleases, plus the final `0.2.0`
hardening and release-readiness work.

#### 0.2.0-beta.1 2026-07-22

## Fixed

- **Preserved hyphenated PowerShell native options for safer parsing (#60)**
  PowerShell native options now keep their full hyphenated form when present in
  command text. Parameter forms like `-Native-Flag` and `-Native-Flag=value`
  now stay correctly grouped instead of being split in ways that could confuse
  downstream approvals. The parser also avoids over-reading ambiguous
  colon-value combinations by marking those cases as `DynamicSkip` when the
  shape is unclear.
  See [#60](https://github.com/Aaronontheweb/ShellSyntaxTree/issues/60) for
  details.

#### 0.2.0-alpha May 19th 2026

First **PowerShell** parser. ShellSyntaxTree now ships two `IShellParser`
implementations — `BashParser` (unchanged) and the new `PwshParser` — both
emitting the same `ParsedCommand` AST a consumer already walks for bash.
Shipped as an **alpha** prerelease so Netclaw can validate the new parser
and the breaking `Clause` rename before promotion to a stable `0.2.0`.

**BREAKING: `Clause.IsBashCWrapped` renamed to `Clause.IsCommandStringWrapped`**

The v0.1 field `Clause.IsBashCWrapped` is renamed `Clause.IsCommandStringWrapped`.
The meaning is unchanged and now shell-neutral — *true when the clause is the
result of recursing into a command-string wrapper*: bash `bash -c "..."` /
`sh -c "..."`, or PowerShell `pwsh -Command "..."` / `pwsh -EncodedCommand ...`.

| Old (v0.1) | New (v0.2.0) |
|---|---|
| `Clause.IsBashCWrapped` | `Clause.IsCommandStringWrapped` |

A breaking AST change on a `0.x` minor is permitted by `SPEC.md` Appendix A
when `RELEASE_NOTES.md` carries the old→new mapping (above) and Netclaw is
updated in lockstep. Consumers: rename every `IsBashCWrapped` reference;
there is no behavior change beyond the identifier.

**BREAKING (source-compatible): `BashParserOptions` reparented**

`BashParserOptions` is now a sealed record deriving from the new abstract
`ShellParserOptions` base; `HomeDirectory` / `WorkingDirectory` move to the
base. The object-initializer shape is unchanged —
`new BashParserOptions { HomeDirectory = ..., WorkingDirectory = ... }`
still compiles. Only code that named `BashParserOptions` as a *base type*
or reflected over its declared members is affected.

**New public surface**

- `PwshParser : IShellParser` — the PowerShell parser. `Parse` throws
  `ArgumentNullException` on null and never throws on a well-formed string,
  exactly like `BashParser`.
- `PwshParserOptions` — configuration record for `PwshParser` (empty in
  v0.2.0; resolver knobs live on `ShellParserOptions`).
- `ShellParserOptions` — the shared, abstract resolver-configuration base.
- `VerbChain.CanonicalVerb` (additive) — the alias-resolved canonical verb,
  non-null only when an alias was rewritten (`ls` → `Get-ChildItem`). Null
  for every bash clause. Consumers gate on `CanonicalVerb ?? Tokens[0]`.
- `VerbChain.IsDynamic` (additive) — true when the command name is a
  dynamic token the parser cannot statically identify (`& $exe`,
  `& { ... }`). Always false for bash clauses; a consumer MUST route a
  dynamic clause to safe-fail.

**PowerShell parser capabilities (SPEC.POWERSHELL.md)**

- Parses PowerShell command pipelines into the shared `ParsedCommand` AST —
  per-clause verbs, args, parameters, redirects, and the `&&` / `||` / `;`
  / `|` / newline compound operators.
- Recognizes cmdlets (`Verb-Noun`), native commands, and the complete
  built-in alias set; resolves aliases to their canonical cmdlet while
  preserving the verbatim typed token.
- The §6.5 parameter-binding model — switch vs. value-binding decisions
  from static tables, colon-form `-Name:value`, prefix matching.
- Per-cmdlet / per-parameter path-arg extraction (`-Path`, `-LiteralPath`,
  `-Destination`, positional rules).
- `Set-Location <dir>; cmd` cwd propagation, including through `( ... )`
  grouping (PowerShell `( )` is not a subshell).
- Recursion into `pwsh -Command "<inner>"`, `pwsh -c`, and
  `pwsh -EncodedCommand <base64>` (base64 / UTF-16LE decode, BOM strip),
  depth-5 capped; inner clauses surface with `IsCommandStringWrapped=true`.
- Marks dynamic-content tokens (`$var`, subexpressions, script blocks,
  splatting, comma-arrays) `DynamicSkip`; control flow, definitions, and
  other script-level constructs safe-fail to `IsUnparseable=true`.
- A 64 KiB input cap guards the per-shell-call hot path.

**Corpus & validation**

- 211 hand-authored PowerShell corpus entries under
  `Corpus/powershell/`, exceeding every SPEC.POWERSHELL.md §13 category
  minimum. The corpus runner and PII audit are directory-routed by shell.
- A real-`pwsh` validation gate (`PwshOracleTests`) feeds every PowerShell
  corpus input to `[Parser]::ParseInput` and enforces the §13 oracle
  matrix; a `PwshAliases`-vs-live-`Get-Alias` completeness `[Fact]`
  confirms the alias table has no gaps.
- `tools/PwshCorpusTool` — the corpus authoring aid (see `TOOLING.md`).

## Added

- **Added source-ordered clause-element provenance (`Clause.Elements`) for richer approvals (#62, #68)**
  Clause-level elements now preserve source order and metadata such as spans,
  decoded values, path facts, redirects, and verb-relative placement for both
  Bash and PowerShell. This adds additive, shell-neutral data for downstream
  security consumers and preserves compatibility with prior AST shapes.
  See [#62](https://github.com/Aaronontheweb/ShellSyntaxTree/issues/62) and
  [#68](https://github.com/Aaronontheweb/ShellSyntaxTree/issues/68).

- **Preserved path-shaped command operands after native chains (#65)**
  Path-shaped operands now remain intact through native option parsing, so
  command strings that mix native options and path-like inputs keep their
  intended argument shape instead of being split or dropped by parser heuristics.
  See [#65](https://github.com/Aaronontheweb/ShellSyntaxTree/issues/65).

## Fixed

- **Preserved static `Invoke-Expression` payload parsing in PowerShell (#63, #67)**
  Static `Invoke-Expression` / `iex` command strings now follow the same
  safe-recursion path as `pwsh -Command`: known-safe payloads recurse with the
  existing depth/size limits, while dynamic content stays conservative via
  `DynamicSkip` / `IsUnparseable` and remains safe-fail for approvals.
  See [#63](https://github.com/Aaronontheweb/ShellSyntaxTree/issues/63) and
  [#67](https://github.com/Aaronontheweb/ShellSyntaxTree/issues/67).

---

#### 0.1.5 May 16th 2026 ####

Stable promotion of 0.1.5-beta. No code changes from the beta; this release
drops the pre-release suffix now that the newline-as-statement-separator
behavior change (SPEC §4) has been validated against Netclaw's live gate
evaluator. Consumers on `0.1.5-beta` can upgrade directly.

See the 0.1.5-beta notes below for the full list of changes in this version.

---

#### 0.1.5-beta May 15th 2026 ####

Newline-as-statement-separator. Public API surface unchanged; the
*content* of `ParsedCommand.Clauses` changes for any input that spans
multiple lines. Shipped as a **beta** prerelease so Netclaw can validate
the AST-shape change against its live gate evaluator before this is
promoted to a stable `0.1.5`.

**BEHAVIOR CHANGE: a bare newline now separates clauses (SPEC §4)**

- A bare newline outside quotes, heredoc bodies, line continuations, and
  `$()` / backtick substitutions is now a statement separator equivalent
  to `;` — the clause after it carries `CompoundOperator.Sequence`.
  Before this release `BashCommandParser` only split clauses on `&&` /
  `||` / `;` / `|`, so `cmd1\ncmd2` parsed to a single clause `[cmd1]`
  with `cmd2` wrongly absorbed as an argument.
- The lexer flags the newline-bearing `Whitespace` token — and the
  newline after a heredoc terminator — with a new internal
  `IsStatementSeparator` bit; `FilterSignificant` retains those tokens
  and `SplitIntoSegments` splits clauses on them.
- Consecutive newlines, leading and trailing newlines, and a newline
  immediately after a compound operator (`cmd1 &&\ncmd2`) all collapse —
  they never produce an empty clause.
- A heredoc followed by a command on the next line now parses to two
  clauses (previously the heredoc clause and the following command
  merged into one).
- A control-flow keyword opening a newline-separated clause
  (`echo hi\nfor i in 1 2 3`) safe-fails to `IsUnparseable=true`, exactly
  as it would after `;`.

Examples that change:

- `cmd1\ncmd2` → two clauses `[cmd1]`, `[cmd2]` (was one clause `[cmd1]`
  with `cmd2` as an arg).
- `git pull # done\ndotnet build` → two clauses (was one).

**Behavior notes**

- Public API surface is unchanged (no `PublicApiSnapshotTests` delta).
- SPEC.md updates: §4 grammar (`compound_op` includes `NEWLINE`, new
  notes bullet), §5 `WHITESPACE` tokenization, §15 versioning, §16
  sequencing note.
- Corpus: 11 new entries (139–149) covering newline separation, blank
  lines, leading/trailing newlines, newline after an operator, newline
  inside quotes and subshells, line continuation, comment-then-newline,
  and the control-flow-after-newline safe-fail. Entry 126's note is
  corrected — newline-as-separator is no longer a pending gap.
- Unit tests: 8 new `BashLexerTests` cases + 14 new
  `BashCommandParserTests` cases; the stale comment in
  `Comment_between_two_statements_preserves_both_clauses` is corrected.

---

#### 0.1.4 May 15th 2026 ####

Stable promotion of 0.1.4-alpha. No code changes from the alpha; this release
drops the pre-release suffix to signal that the v0.1 public API surface is
considered production-ready for Bash parsing use cases (see SPEC.md §17
acceptance criteria). Consumers on any `0.1.x-alpha` can upgrade directly.

See the 0.1.4-alpha notes below for the full list of changes in this version.

---

#### 0.1.4-alpha May 12th 2026 ####

Greedy verb-chain extraction. Public API surface (`VerbChain`, `Clause`)
unchanged; the *content* of `Clause.Verb.Tokens` changes for many inputs.

**BEHAVIOR CHANGE: verb-chain length is no longer table-driven (#27)**

- The `BashArity` static lookup table and `ProbeArity()` method have been
  **removed**. The parser walks consecutive verb-like Word tokens from
  the start of each clause, transparently consuming flag-with-value
  pairs (e.g. `git -C /repo`), and stops at the first non-verb-like
  token, the first plain flag, or the first non-Word token.
- A token is "verb-like" when its kind is `Word`, length 1–64, first
  character is an ASCII lowercase letter, and remaining characters are
  in `[a-z0-9._-]`. The strict allow-list naturally excludes flags,
  paths (`/`, `\`, `~`), env-var refs (`$VAR`), URLs (`://`), globs,
  numeric tokens, and uppercase user-named identifiers like migration
  names — without requiring per-case predicate logic. See SPEC §6.1.
- For known FILE verbs (`cat`, `ls`, `bash`, `cd`, `chmod`, `grep`,
  `find`, …) the verb chain stops at exactly one token to preserve
  per-verb positional-arg classification. The flag-with-value
  consumption still runs so `tar -C /path` and `curl -o file` style
  values still pick up `IsPath=true` via `FlagValueIsPath`.

Examples that change:

- `git push origin main` → verb `[git, push, origin, main]` (was
  `[git, push]`).
- `git worktree list` (and arbitrary CLI subcommand chains) → fully
  extracted as `[git, worktree, list]` (was `[git, worktree]`).
- `freshdesk ticket list --status open` → `[freshdesk, ticket, list]`
  (was `[freshdesk]` because freshdesk wasn't in the BashArity table).
- `kubectl get pods my-pod` → `[kubectl, get, pods, my-pod]` (was
  `[kubectl, get]`).
- `aws s3 cp src dst` → `[aws, s3, cp, src, dst]` (was `[aws, s3]`).
- `dotnet ef migrations add InitialCreate` → `[dotnet, ef, migrations,
  add]` (was `[dotnet, ef]`). `InitialCreate` stays in args because the
  predicate rejects uppercase first character.
- `cat README` → still `[cat]` (FileVerb carveout preserves `IsPath` on
  bare-name targets).
- `echo hello` → `[echo, hello]` (echo is not a FILE verb).

`Clause.Verb` is now documented as a **convenience hint, not a security
contract** (SPEC §6.1.1). Consumers needing security-grade verb
identification should pattern-prefix match against the raw token
stream: a command matches an approval pattern *P* iff the first
`len(P.verb_prefix)` command tokens equal `P.verb_prefix`. This punts
depth choice to the consumer and accommodates the parser's deliberate
over-extraction on bare-word args. Auto-proposed patterns should default
to the full extracted verb chain (greedy match): a subsequent variation
re-prompts rather than silently auto-grants.

**Behavior notes**

- Public API surface is unchanged (no `PublicApiSnapshotTests` delta).
- SPEC.md updates: §3 `VerbChain`, §4 grammar, §6.1 verb-chain
  extraction (rewritten end-to-end), new §6.1.1 consumer
  pattern-matching guidance, §7 flag-with-value note, §12 worked
  examples, §15 versioning, §16 implementation sequencing.
- Corpus: 7 new entries (132–138) pin the issue #27 headline cases;
  10 existing entries flipped to the new shape (`04_echo_hello`,
  `11_git_push_origin_main`, `13_git_checkout_dev`, `17_docker_run_nginx`,
  `27_make_install`, `45_echo_append_log`, `84_subshell_nested`,
  `91_bash_c_simple`, `96_bash_c_nested_depth_2`,
  `100_bash_c_nested_depth_3`, `130_netclaw_repro_leading_comment_pipeline`).
- Unit tests: 8 pinned `BashCommandParserTests` cases updated to the new
  expected verb chains.

#### 0.1.3-alpha May 12th 2026 ####

Bash line comment handling. Public API unchanged.

**Fixed**

- **Bash line comments are now recognized and skipped (#25).** `BashLexer`
  treats `#` at a word boundary (start of input, or preceded by
  whitespace, a newline, or any operator) as the start of a comment
  that runs to the next newline. The comment text is emitted as a new
  internal `BashTokenKind.Comment` token for source fidelity and is
  filtered by the parser alongside `Whitespace` / `Continuation`, so
  it contributes no verb, args, redirects, or flags to any clause.
  Comment-only input parses to `Clauses = []`, `IsUnparseable = false`,
  matching the existing empty-/whitespace-only path. Quoting and
  escape rules are honored: `#` inside single or double quotes is
  literal, `#` in the interior of an unquoted word (e.g. `abc#def`)
  is literal, and `\#` outside quotes is literal.

  Before this fix, `# Extract worktree branches\ngit worktree list`
  parsed to a single clause with verb chain `[#, Extract]` — the
  comment text leaked into downstream approval prompts and broke
  approval-state caching in consumers that did asymmetric verb-chain
  extraction (persistence-time vs. retry-authorization saw different
  verb sets, causing tool calls to fail after the user had already
  clicked Approve).

**Behavior notes**

- Public API surface is unchanged (no `PublicApiSnapshotTests` delta).
- SPEC.md §4 / §5: new "Comment handling" subsection in §5 documents
  the boundary rules; §4 BNF notes that comments are
  whitespace-equivalent at the lexer level.
- Corpus: 9 new entries (123–131) pin every case from the issue
  report, plus the two Netclaw repros (sanitized paths per §14).
- v0.1 still does not treat top-level newlines as statement separators
  (SPEC §4 gap, tracked separately in IMPLEMENTATION_PLAN NEXT) — a
  comment between two commands on separate lines requires an explicit
  `;` separator to split into two clauses.

#### 0.1.2-alpha May 11th 2026 ####

Three parser correctness fixes. Public API unchanged.

**Fixed**

- **Single-quoted strings are now literal per SPEC §5 (B2).** Previously
  `echo '$HOME'` produced `Kind=Tilde` because the resolver substituted
  `$HOME` uniformly regardless of quote style. Now the lexer marks
  single-quoted `QuotedString` tokens with the internal `IsSingleQuoted`
  flag, and the resolver bypasses tilde / `$HOME` / `$VAR` / glob /
  `filesystem::` handling for them. `echo '$HOME'` stays `Kind=Literal`,
  `Resolved=null`; `cat '/etc/passwd'` still resolves a path. Matches
  bash semantics.
- **`LooksLikePath` no longer false-positives on a lone trailing
  backslash (B3).** A double-quoted token like `"foo\\"` lexes to
  Value `foo\`; the trailing `\` is an escape-collapse artifact, not a
  meaningful path signal. The heuristic now requires a backslash at a
  non-trailing position. Forward-slash behavior is unchanged — `dir/`
  still classifies as a path (trailing `/` is a meaningful bash
  directory hint).
- **Control-flow keyword detection precedes paren-balance (B4).**
  Previously `case x in a) ;; esac` produced `IsUnparseable=true` with
  reason `unbalanced parens at position N` because the `)` in `a)`
  tripped `SplitIntoSegments` before the per-clause keyword check
  could fire. The anomaly pass now scans the token stream for
  control-flow keywords at verb position (start of input or after
  `&&` / `||` / `;` / `|` / `(`) and short-circuits with the helpful
  `control-flow keyword 'case' is not supported in v0.1` reason
  before downstream checks run. SPEC §11 now pins the full diagnostic
  precedence order.

**Behavior notes**

- Public API surface is unchanged (no `PublicApiSnapshotTests` delta).
- SPEC.md §8: new "Step 0: Single-quoted bypass" preamble; LooksLikePath
  heuristic updated to call out the trailing-backslash carve-out.
- SPEC.md §11: new "Diagnostic precedence" section enumerating the
  order in which unparseable conditions are checked.
- Corpus entries 104 (`echo 'literal $HOME'`) and 109 (`echo "trailing
  backslash\\"`) updated to the corrected outputs. Four new entries
  (119–122) pin the regression guards: single-quoted absolute paths
  still resolve, `cd dir/` still classifies as a path, single-quoted
  `$VAR` stays literal under `rm`, and `case x in a) ;; esac` now
  reports the control-flow keyword reason instead of a paren-balance
  error.

#### 0.1.1-alpha May 11th 2026 ####

Bug fix release for v0.1.0-alpha consumers.

**Fixed**

- **`2>&1` fd-dup redirects no longer produce phantom `<cwd>/&1` file
  targets.** The parser now recognizes POSIX fd-dup / fd-close shorthand
  (`&N`, `&N-`, `&-`) on redirect targets and carries the raw token
  verbatim on `Redirect.Target` with `Redirect.IsDynamicSkip = true`.
  Existing consumers that already skip redirects with
  `IsDynamicSkip = true` get correct behavior with no code changes.
  (B1)

**Behavior notes**

- Public API surface is unchanged. `Redirect.Target` xmldoc and SPEC.md
  §3 / §4 are clarified to document the fd-dup rule.
- The Blazor sample's basename-startswith-`&` workaround has been
  removed; the sample now relies solely on `Redirect.IsDynamicSkip`.

#### 0.1.0-alpha May 10th 2026 ####

First publishable cut of ShellSyntaxTree — a focused .NET library that
parses bash command strings into a structured AST for security-gate
evaluators. Hand-rolled, AOT-trim friendly, no native dependencies.

**What's in this release**

- `IShellParser` interface + `BashParser` implementation per locked
  v0.1 contract (SPEC.md §2 / §3)
- Bash lexer: words, quoted strings, operators, opaque substitutions
  (`$()` / backticks → DynamicSkip), arithmetic (`$((...))`) and complex
  parameter expansion (`${var//.../...}`) → IsUnparseable
- Verb tables (BashArity, CwdVerbs, FileVerbs, FlagsWithValue) + per-verb
  path-arg rules + flag-with-value-aware verb-chain probe
- Path resolver: tilde / `$HOME` expansion, `filesystem::` prefix strip,
  glob detection (covering-dir heuristic preserved), cross-platform
  forward-slash normalization
- cd-in-compound attribution: synthetic `Arg.IsCwdAttribution` propagated
  to subsequent clauses; `cd $VAR` produces a DynamicSkip attribution
  signal
- Subshell isolation via attribution stack with monotonic IDs (handles
  sibling subshells `(a) && (b)` cleanly)
- `bash -c` / `sh -c` recursion (cap at depth 5 → outer
  `ParsedCommand.IsUnparseable=true`)
- 115-entry corpus across all 11 SPEC §13 categories, validated by
  `CorpusRunnerTests` with a polished `AstAssert.Equal` helper
- PII audit `[Fact]` enforcing SPEC §14 sanitization patterns

**Public API surface (locked per SPEC §2 / §3)**

`IShellParser`, `BashParser`, `BashParserOptions`, `ParsedCommand`,
`Clause`, `VerbChain`, `Arg`, `Redirect` (records); `ArgKind`,
`RedirectDirection`, `CompoundOperator` (enums). Multi-target
`netstandard2.0;net8.0`; AOT-friendly (`<IsAotCompatible>true</IsAotCompatible>`).

**Verification**

353 tests passing on Linux + Windows. `dotnet pack` produces
`ShellSyntaxTree.0.1.0-alpha.nupkg` with embedded README, icon, and
SourceLink metadata.

**Known limitations (tracked for v0.1.x)**

- `pushd` / `popd` parse as CwdVerbs but don't propagate cwd
  attribution (only `cd` / `chdir` do in v0.1)
- `tar` falls through to the default per-verb rule (no action-flag
  awareness)
- `docker -v "/host:/container"` is a single literal arg with
  `IsPath=false` (no colon-split in v0.1)
- Single-quoted `'$HOME'` is substituted by the resolver (bash
  semantics: doesn't substitute in single quotes)

**Documentation**

- `SPEC.md` — locked v0.1 contract (the source of truth for parser
  behavior)
- `openspec/changes/` — change-proposal history with rationale for the
  eight v0.1 SPEC interpretations resolved during planning
- `README.md` — quick-start usage
