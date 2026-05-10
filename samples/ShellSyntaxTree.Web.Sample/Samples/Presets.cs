namespace ShellSyntaxTree.Web.Sample.Samples;

public static class Presets
{
    public static readonly (string Name, string Script)[] All =
    {
        ("Build script", "set -e\ncd /repo\ngit pull origin main\ndocker build -t myapp .\ndocker push myapp:latest > /tmp/push.log 2>&1\necho done"),
        ("With subshell", "cd /a && (cd /b && cmd1) && cmd2"),
        ("With bash -c", "cd /repo && bash -c \"git status && git push\""),
        ("Unparseable (control flow)", "for i in 1 2 3; do echo $i; done"),
        ("Dynamic cwd", "cd $REPO_DIR && rm -rf node_modules && npm install"),
    };
}
