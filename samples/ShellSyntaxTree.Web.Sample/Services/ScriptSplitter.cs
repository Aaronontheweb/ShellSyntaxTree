using System.Text;

namespace ShellSyntaxTree.Web.Sample.Services;

/// <summary>
/// Split a multi-line script into individual top-level commands.
/// Skips blank lines and # comments. Folds backslash + newline
/// continuations. Preserves heredoc bodies as part of their command.
/// Edge cases (control flow, function bodies) are passed through
/// unchanged — the parser will mark them IsUnparseable.
/// </summary>
internal static class ScriptSplitter
{
    public static IEnumerable<string> Split(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            yield break;
        }

        var lines = script.Replace("\r\n", "\n").Split('\n');
        var pending = new StringBuilder();
        string? heredocDelim = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Heredoc body: keep collecting verbatim until the delimiter line.
            if (heredocDelim is not null)
            {
                pending.Append('\n').Append(line);
                if (line.Trim() == heredocDelim)
                {
                    heredocDelim = null;
                    yield return pending.ToString();
                    pending.Clear();
                }

                continue;
            }

            // Skip blank lines and pure comment lines only when we are
            // not in the middle of collecting a continued command.
            if (pending.Length == 0)
            {
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }
            }

            // Fold backslash continuation.
            if (line.EndsWith('\\'))
            {
                pending.Append(line.AsSpan(0, line.Length - 1));
                continue;
            }

            pending.Append(line);

            // Look for a starting heredoc (`<<DELIM` or `<<-DELIM`, with the
            // delimiter possibly quoted). If found, switch to body-collection mode.
            heredocDelim = TryExtractHeredocDelimiter(pending.ToString());
            if (heredocDelim is not null)
            {
                continue;
            }

            var emit = pending.ToString();
            pending.Clear();

            if (!string.IsNullOrWhiteSpace(emit))
            {
                yield return emit;
            }
        }

        if (pending.Length > 0)
        {
            yield return pending.ToString();
        }
    }

    private static string? TryExtractHeredocDelimiter(string command)
    {
        // Minimal heredoc detection: look for << (optionally <<-) followed by a
        // token. Good enough for the common shapes seen in build scripts;
        // anything weirder will still parse, just as one logical command.
        var idx = command.IndexOf("<<", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var after = command.AsSpan(idx + 2).TrimStart();
        if (after.Length == 0)
        {
            return null;
        }

        // Strip leading '-' (heredoc dash form) and optional quotes.
        if (after[0] == '-')
        {
            after = after[1..].TrimStart();
        }

        if (after.Length == 0)
        {
            return null;
        }

        var quote = after[0] is '"' or '\'' ? after[0] : (char?)null;
        if (quote is not null)
        {
            after = after[1..];
            var end = after.IndexOf(quote.Value);
            if (end < 0)
            {
                return null;
            }

            return after[..end].ToString();
        }

        // Unquoted: read identifier-shaped chars.
        var len = 0;
        while (len < after.Length && (char.IsLetterOrDigit(after[len]) || after[len] == '_'))
        {
            len++;
        }

        if (len == 0)
        {
            return null;
        }

        return after[..len].ToString();
    }
}
