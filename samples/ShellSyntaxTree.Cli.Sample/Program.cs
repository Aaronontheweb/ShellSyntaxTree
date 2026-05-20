using System.CommandLine;
using ShellSyntaxTree.Cli.Sample.Commands;

// System.CommandLine 2.0.7 (stable) — the 2.0.0-beta5.25558.101 referenced in
// internal notes was never published to nuget.org. The 2.x stable API exposes
// SetAction(parseResult => ...) and parseResult.GetValue(arg) instead of the
// older SetHandler(...) pattern used in pre-beta5 builds.
var commandArg = new Argument<string>("command")
{
    Description = "The shell command (or single line) to analyze.",
};

var shellOption = new Option<string>("--shell")
{
    Description = "Which parser to use: 'bash' (default) or 'pwsh'.",
    DefaultValueFactory = _ => "bash",
};
shellOption.AcceptOnlyFromAmong("bash", "pwsh");

var explainCmd = new Command("explain", "Pretty-print the parsed AST.")
{
    commandArg,
    shellOption,
};
explainCmd.SetAction(parseResult => ExplainCommand.Run(
    parseResult.GetValue(commandArg) ?? string.Empty,
    parseResult.GetValue(shellOption) ?? "bash"));

var auditCmd = new Command("audit", "Run the built-in policy against the command.")
{
    commandArg,
    shellOption,
};
auditCmd.SetAction(parseResult => AuditCommand.Run(
    parseResult.GetValue(commandArg) ?? string.Empty,
    parseResult.GetValue(shellOption) ?? "bash"));

var root = new RootCommand("ShellSyntaxTree sample CLI — bash & PowerShell")
{
    explainCmd,
    auditCmd,
};

return root.Parse(args).Invoke();
