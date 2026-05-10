using System.Text;
using ShellSyntaxTree;

namespace ShellSyntaxTree.Web.Sample.Services;

/// <summary>
/// Renders a list of <see cref="ParsedCommand"/> (one per script line)
/// as a Mermaid flowchart source string. Keeps escaping concerns local
/// so the rest of the sample can think in terms of <c>ParsedCommand</c>.
/// </summary>
public static class MermaidRenderer
{
    public static string Render(IReadOnlyList<ParsedCommand> parses)
    {
        // Return empty so the host page can show its own placeholder UI
        // instead of a fake "(no commands)" mermaid node.
        if (parses.Count == 0 || parses.All(p => !p.IsUnparseable && p.Clauses.Count == 0))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        // LR if any single parse has more than 4 clauses, else TD.
        var orientation = "TD";
        foreach (var parse in parses)
        {
            if (parse.Clauses.Count > 4)
            {
                orientation = "LR";
                break;
            }
        }

        sb.Append("flowchart ").Append(orientation).Append('\n');
        sb.Append("    classDef unparseable fill:#fee,stroke:#b00,stroke-width:2px\n");
        sb.Append("    classDef dynamic fill:#ffe,stroke:#b80\n");
        sb.Append("    classDef redirect fill:#eef,stroke:#88a\n");

        var subshellSerial = 0;
        var bashCSerial = 0;

        // First pass: emit nodes (and their subgraph groupings).
        for (var lineIdx = 0; lineIdx < parses.Count; lineIdx++)
        {
            var parse = parses[lineIdx];

            if (parse.IsUnparseable)
            {
                var nodeId = NodeId(lineIdx, 0);
                var reason = Truncate(parse.UnparseableReason ?? "unknown", 40);
                sb.Append("    ").Append(nodeId).Append('[').Append('"')
                  .Append("?<br/>UNPARSEABLE<br/>").Append(EscapeLabel(reason))
                  .Append("\"]\n");
                sb.Append("    class ").Append(nodeId).Append(" unparseable\n");
                continue;
            }

            // Group consecutive same-flag clauses into a single subgraph so we
            // don't open / close subgraphs around every clause.
            var c = 0;
            while (c < parse.Clauses.Count)
            {
                var clause = parse.Clauses[c];

                if (clause.IsSubshell)
                {
                    subshellSerial++;
                    sb.Append("    subgraph SH").Append(subshellSerial).Append(" [\"( ... )\"]\n");
                    var groupEnd = c;
                    while (groupEnd < parse.Clauses.Count && parse.Clauses[groupEnd].IsSubshell)
                    {
                        EmitClauseNode(sb, parse, lineIdx, groupEnd);
                        groupEnd++;
                    }

                    sb.Append("    end\n");
                    c = groupEnd;
                }
                else if (clause.IsBashCWrapped)
                {
                    bashCSerial++;
                    sb.Append("    subgraph BC").Append(bashCSerial).Append(" [\"bash -c\"]\n");
                    var groupEnd = c;
                    while (groupEnd < parse.Clauses.Count && parse.Clauses[groupEnd].IsBashCWrapped)
                    {
                        EmitClauseNode(sb, parse, lineIdx, groupEnd);
                        groupEnd++;
                    }

                    sb.Append("    end\n");
                    c = groupEnd;
                }
                else
                {
                    EmitClauseNode(sb, parse, lineIdx, c);
                    c++;
                }
            }
        }

        // Second pass: edges within a line + redirects.
        for (var lineIdx = 0; lineIdx < parses.Count; lineIdx++)
        {
            var parse = parses[lineIdx];
            if (parse.IsUnparseable)
            {
                continue;
            }

            for (var i = 0; i + 1 < parse.Clauses.Count; i++)
            {
                var from = NodeId(lineIdx, i);
                var to = NodeId(lineIdx, i + 1);
                var op = OperatorLabel(parse.Clauses[i + 1].Operator);
                sb.Append("    ").Append(from).Append(" -->|\"").Append(op).Append("\"| ").Append(to).Append('\n');
            }

            for (var i = 0; i < parse.Clauses.Count; i++)
            {
                var clause = parse.Clauses[i];
                for (var r = 0; r < clause.Redirects.Count; r++)
                {
                    var redirect = clause.Redirects[r];
                    if (redirect.IsDynamicSkip)
                    {
                        continue;
                    }

                    // `2>&1` and similar `N>&M` fd-dup targets aren't files;
                    // the parser resolves `&1` against the cwd into something
                    // like `/work/&1`, so check the basename, not the prefix.
                    // v0.1.x candidate: have the parser surface fd-dup targets
                    // distinctly (e.g. IsFdDup) so consumers don't have to
                    // string-match.
                    var basename = redirect.Target.LastIndexOf('/') is var lastSlash and >= 0
                        ? redirect.Target[(lastSlash + 1)..]
                        : redirect.Target;
                    if (basename.StartsWith('&'))
                    {
                        continue;
                    }

                    var fileNode = $"F{NodeId(lineIdx, i)}R{r}";
                    sb.Append("    ").Append(fileNode).Append("[(\"")
                      .Append(EscapeLabel(redirect.Target)).Append("\")]\n");
                    sb.Append("    class ").Append(fileNode).Append(" redirect\n");

                    var label = redirect.Direction switch
                    {
                        RedirectDirection.In => "stdin",
                        RedirectDirection.Out => "stdout",
                        RedirectDirection.Append => "append",
                        RedirectDirection.ErrOut => "stderr-out",
                        RedirectDirection.ErrAppend => "stderr-append",
                        _ => redirect.Direction.ToString(),
                    };

                    sb.Append("    ").Append(NodeId(lineIdx, i))
                      .Append(" -.->|\"").Append(label).Append("\"| ").Append(fileNode).Append('\n');
                }
            }
        }

        // Third pass: dotted edges connecting sequential lines (last clause -> first clause).
        for (var lineIdx = 0; lineIdx + 1 < parses.Count; lineIdx++)
        {
            var thisParse = parses[lineIdx];
            var nextParse = parses[lineIdx + 1];

            if (thisParse.Clauses.Count == 0 && !thisParse.IsUnparseable)
            {
                continue;
            }

            if (nextParse.Clauses.Count == 0 && !nextParse.IsUnparseable)
            {
                continue;
            }

            var fromIdx = thisParse.IsUnparseable ? 0 : thisParse.Clauses.Count - 1;
            var from = NodeId(lineIdx, fromIdx);
            var to = NodeId(lineIdx + 1, 0);
            sb.Append("    ").Append(from).Append(" -.-> ").Append(to).Append('\n');
        }

        return sb.ToString();
    }

    private static void EmitClauseNode(StringBuilder sb, ParsedCommand parse, int lineIdx, int clauseIdx)
    {
        var clause = parse.Clauses[clauseIdx];
        var nodeId = NodeId(lineIdx, clauseIdx);

        var label = BuildLabel(clause);
        sb.Append("    ").Append(nodeId).Append('[').Append('"').Append(label).Append("\"]\n");

        if (HasDynamicSkip(clause))
        {
            sb.Append("    class ").Append(nodeId).Append(" dynamic\n");
        }
    }

    private static string BuildLabel(Clause clause)
    {
        var sb = new StringBuilder();
        var verb = clause.Verb.Tokens.Count == 0 ? "(no verb)" : clause.Verb.Joined;
        sb.Append(EscapeLabel(verb));

        // First 2 path args (preferred) or first 2 non-flag args as fallback
        // so verbs like `echo done` or `set -e` don't render as bare labels.
        var pathArgs = new List<string>();
        var nonFlagArgs = new List<string>();
        string? cwdAttr = null;
        foreach (var arg in clause.Args)
        {
            if (arg.IsCwdAttribution)
            {
                cwdAttr = arg.Resolved ?? arg.Raw;
                continue;
            }

            if (arg.IsPath)
            {
                pathArgs.Add(arg.Resolved ?? arg.Raw);
            }
            else if (!arg.IsFlag)
            {
                nonFlagArgs.Add(arg.Resolved ?? arg.Raw);
            }
        }

        var labelExtras = pathArgs.Count > 0 ? pathArgs : nonFlagArgs;
        if (labelExtras.Count > 0)
        {
            sb.Append("<br/>");
            for (var i = 0; i < Math.Min(2, labelExtras.Count); i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(EscapeLabel(labelExtras[i]));
            }

            if (labelExtras.Count > 2)
            {
                sb.Append(" ...");
            }
        }

        if (cwdAttr is not null)
        {
            sb.Append("<br/>cwd: ").Append(EscapeLabel(cwdAttr));
        }
        else if (HasDynamicCwdSignal(clause))
        {
            sb.Append("<br/>cwd: ?");
        }

        return sb.ToString();
    }

    private static bool HasDynamicCwdSignal(Clause clause)
    {
        // A clause whose verb is cd-ish but whose first arg is DynamicSkip
        // is the canonical "cwd: ?" shape. Worth showing in the label.
        if (clause.Verb.Joined != "cd")
        {
            return false;
        }

        foreach (var arg in clause.Args)
        {
            if (arg.IsCwdAttribution)
            {
                continue;
            }

            return arg.Kind == ArgKind.DynamicSkip;
        }

        return false;
    }

    private static bool HasDynamicSkip(Clause clause)
    {
        foreach (var arg in clause.Args)
        {
            if (arg.Kind == ArgKind.DynamicSkip)
            {
                return true;
            }
        }

        foreach (var r in clause.Redirects)
        {
            if (r.IsDynamicSkip)
            {
                return true;
            }
        }

        return false;
    }

    private static string OperatorLabel(CompoundOperator op) => op switch
    {
        CompoundOperator.AndIf => "&amp;&amp;",
        CompoundOperator.OrIf => "||",
        CompoundOperator.Sequence => ";",
        CompoundOperator.Pipe => "\\|",
        _ => "",
    };

    private static string NodeId(int line, int clause) => $"L{line}C{clause}";

    private static string EscapeLabel(string s)
    {
        // Inside mermaid node labels, the safest path is to strip / escape the
        // small set of chars that bite. Keep this in lock-step with what
        // mermaid.js 11 accepts inside ["..."] double-quoted labels.
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("&quot;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '|': sb.Append("&#124;"); break;
                case '(': sb.Append("&#40;"); break;
                case ')': sb.Append("&#41;"); break;
                case '[': sb.Append("&#91;"); break;
                case ']': sb.Append("&#93;"); break;
                case '{': sb.Append("&#123;"); break;
                case '}': sb.Append("&#125;"); break;
                case '&': sb.Append("&amp;"); break;
                case '\\': sb.Append("&#92;"); break;
                case '`': sb.Append("&#96;"); break;
                // Apostrophe is safe inside `["..."]` double-quoted labels and
                // mermaid 11 doesn't decode `&#39;` cleanly there — leave bare.
                case '\'': sb.Append('\''); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
