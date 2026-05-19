namespace ShellSyntaxTree.Web.Sample.Samples;

public static class Presets
{
    public static readonly (string Name, string Script)[] Bash =
    {
        ("Build script", "set -e\ncd /repo\ngit pull origin main\ndocker build -t myapp .\ndocker push myapp:latest > /tmp/push.log 2>&1\necho done"),
        ("With subshell", "cd /a && (cd /b && cmd1) && cmd2"),
        ("With bash -c", "cd /repo && bash -c \"git status && git push\""),
        ("Unparseable (control flow)", "for i in 1 2 3; do echo $i; done"),
        ("Dynamic cwd", "cd $REPO_DIR && rm -rf node_modules && npm install"),
    };

    public static readonly (string Name, string Script)[] Pwsh =
    {
        ("Cmdlet pipeline", "Get-ChildItem -Path C:\\logs -Recurse\ngci | ? { $_.Length -gt 1mb } | rm"),
        ("Alias resolution", "ls C:\\temp\ndel C:\\temp\\old.txt"),
        ("Set-Location propagation", "cd C:\\repo\ngit status"),
        ("With pwsh -Command", "pwsh -Command \"Remove-Item C:\\tmp\\x\""),
        ("Unparseable (control flow)", "foreach ($f in 1, 2, 3) { Write-Output $f }"),
        ("Dynamic call operator", "& $exe arg1"),
    };

    public static (string Name, string Script)[] For(string shell) =>
        shell == "pwsh" ? Pwsh : Bash;
}
