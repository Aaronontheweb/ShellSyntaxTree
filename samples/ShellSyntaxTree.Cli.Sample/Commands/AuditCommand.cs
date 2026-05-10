using System.Text;
using ShellSyntaxTree;

namespace ShellSyntaxTree.Cli.Sample.Commands;

internal static class AuditCommand
{
    public static int Run(string command)
    {
        var parser = new BashParser();
        var parsed = parser.Parse(command);

        var anyDeny = false;
        var anyWarn = false;

        if (parsed.IsUnparseable)
        {
            var decision = AuditPolicy.Evaluate(new Clause(), parsed, null);
            EmitLine(0, "(unparseable input)", decision);
            return decision.Outcome == AuditOutcome.Deny ? 1 : 2;
        }

        for (var i = 0; i < parsed.Clauses.Count; i++)
        {
            var clause = parsed.Clauses[i];
            var next = i + 1 < parsed.Clauses.Count ? parsed.Clauses[i + 1] : null;

            var decision = AuditPolicy.Evaluate(clause, parsed, next);
            EmitLine(i, SummarizeClause(clause), decision);

            switch (decision.Outcome)
            {
                case AuditOutcome.Deny: anyDeny = true; break;
                case AuditOutcome.Warn: anyWarn = true; break;
            }
        }

        if (anyDeny)
        {
            return 1;
        }

        if (anyWarn)
        {
            return 2;
        }

        return 0;
    }

    private static void EmitLine(int index, string summary, AuditDecision decision)
    {
        var label = decision.Outcome switch
        {
            AuditOutcome.Ok => "OK  ",
            AuditOutcome.Warn => "WARN",
            AuditOutcome.Deny => "DENY",
            _ => "?   ",
        };

        var sb = new StringBuilder();
        sb.Append(label).Append("  | Clause ").Append(index).Append(": ").Append(summary);
        if (!string.IsNullOrEmpty(decision.Reason))
        {
            sb.Append(" — ").Append(decision.Reason);
        }

        Console.WriteLine(sb.ToString());
    }

    private static string SummarizeClause(Clause clause)
    {
        var verb = clause.Verb.Tokens.Count == 0 ? "(no verb)" : clause.Verb.Joined;
        var args = new StringBuilder();
        foreach (var arg in clause.Args)
        {
            if (arg.IsCwdAttribution)
            {
                continue;
            }

            args.Append(' ').Append(arg.Raw);
        }

        return args.Length == 0 ? verb : verb + args;
    }
}
