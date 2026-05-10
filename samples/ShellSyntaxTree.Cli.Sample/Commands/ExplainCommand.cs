using System.Text;
using ShellSyntaxTree;

namespace ShellSyntaxTree.Cli.Sample.Commands;

/// <summary>
/// Pretty-prints the AST returned by <see cref="BashParser"/>.
/// Marker tokens (<c>[flag]</c>, <c>[path]</c>, <c>[cwd-attr]</c>,
/// <c>[dyn-skip]</c>, <c>[glob]</c>, <c>[literal]</c>) are stable so that
/// downstream tooling can grep the output without re-parsing the AST.
/// </summary>
internal static class ExplainCommand
{
    public static int Run(string command)
    {
        var parser = new BashParser();
        var parsed = parser.Parse(command);

        var sb = new StringBuilder();
        sb.Append("Source: ").AppendLine(parsed.Source);
        sb.AppendLine();

        if (parsed.IsUnparseable)
        {
            sb.Append("UNPARSEABLE: ").AppendLine(parsed.UnparseableReason ?? "(no reason given)");
            Console.Write(sb.ToString());
            return 0;
        }

        for (var i = 0; i < parsed.Clauses.Count; i++)
        {
            var clause = parsed.Clauses[i];
            sb.Append("Clause ").Append(i)
              .Append("  (Operator: ").Append(clause.Operator).Append(')');

            if (clause.IsSubshell || clause.IsBashCWrapped)
            {
                sb.Append("  IsSubshell=").Append(clause.IsSubshell)
                  .Append("  IsBashCWrapped=").Append(clause.IsBashCWrapped);
            }

            sb.AppendLine();

            var verbLabel = clause.Verb.Tokens.Count == 0 ? "(none)" : clause.Verb.Joined;
            sb.Append("  Verb: ").AppendLine(verbLabel);

            if (clause.Args.Count > 0)
            {
                sb.AppendLine("  Args:");
                foreach (var arg in clause.Args)
                {
                    AppendArgLine(sb, arg);
                }
            }

            if (clause.Redirects.Count > 0)
            {
                sb.AppendLine("  Redirects:");
                foreach (var redirect in clause.Redirects)
                {
                    sb.Append("    ").Append(FormatDirection(redirect.Direction))
                      .Append("  -> ");
                    if (redirect.IsDynamicSkip)
                    {
                        sb.Append("[dyn-skip] ").AppendLine(redirect.Target);
                    }
                    else
                    {
                        sb.AppendLine(redirect.Target);
                    }
                }
            }

            if (i + 1 < parsed.Clauses.Count)
            {
                sb.AppendLine();
            }
        }

        Console.Write(sb.ToString());
        return 0;
    }

    private static void AppendArgLine(StringBuilder sb, Arg arg)
    {
        var marker = ClassifyArg(arg);
        sb.Append("    ").Append(marker.PadRight(11)).Append(' ').Append(arg.Raw);

        if (arg.Resolved is not null && !string.Equals(arg.Resolved, arg.Raw, StringComparison.Ordinal))
        {
            sb.Append("  ->  ").Append(arg.Resolved);
        }
        else if (arg.IsPath && arg.Resolved is not null)
        {
            sb.Append("  ->  ").Append(arg.Resolved);
        }

        sb.AppendLine();
    }

    private static string ClassifyArg(Arg arg)
    {
        if (arg.IsCwdAttribution)
        {
            return "[cwd-attr]";
        }

        if (arg.Kind == ArgKind.DynamicSkip)
        {
            return "[dyn-skip]";
        }

        if (arg.Kind == ArgKind.Glob)
        {
            return "[glob]";
        }

        if (arg.IsFlag)
        {
            return "[flag]";
        }

        if (arg.IsPath)
        {
            return "[path]";
        }

        return "[literal]";
    }

    private static string FormatDirection(RedirectDirection direction) => direction switch
    {
        RedirectDirection.In => "In  ",
        RedirectDirection.Out => "Out ",
        RedirectDirection.Append => "App ",
        RedirectDirection.ErrOut => "Err ",
        RedirectDirection.ErrAppend => "ErrA",
        _ => direction.ToString(),
    };
}
