using ShellSyntaxTree;

namespace ShellSyntaxTree.Cli.Sample.Commands;

/// <summary>
/// Picks the <see cref="IShellParser"/> for a <c>--shell</c> choice. The
/// rest of the sample works against the shell-neutral <see cref="IShellParser"/>
/// / <see cref="ParsedCommand"/> surface — adding PowerShell required no
/// change to the explain / audit logic.
/// </summary>
internal static class ShellParserFactory
{
    public static IShellParser Create(string shell) => shell switch
    {
        "pwsh" => new PwshParser(),
        _ => new BashParser(),
    };
}
