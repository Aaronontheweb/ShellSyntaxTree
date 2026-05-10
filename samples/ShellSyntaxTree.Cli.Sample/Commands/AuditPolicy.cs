using ShellSyntaxTree;

namespace ShellSyntaxTree.Cli.Sample.Commands;

internal enum AuditOutcome
{
    Ok,
    Warn,
    Deny,
}

internal readonly record struct AuditDecision(AuditOutcome Outcome, string? Reason)
{
    public static AuditDecision Ok() => new(AuditOutcome.Ok, null);

    public static AuditDecision Warn(string reason) => new(AuditOutcome.Warn, reason);

    public static AuditDecision Deny(string reason) => new(AuditOutcome.Deny, reason);
}

/// <summary>
/// A deliberately small, illustrative policy. Not a substitute for a real
/// allow-list — its job is to demonstrate the shape of consuming
/// <see cref="ParsedCommand"/> from an evaluator.
/// </summary>
internal static class AuditPolicy
{
    // Zones that should never be touched by a sandboxed command.
    private static readonly string[] ProtectedPrefixes =
    {
        "/etc/", "/usr/", "/bin/", "/sbin/", "/lib/",
    };

    // Workspace prefixes considered "safe" for destructive verbs.
    private static readonly string[] WorkspacePrefixes =
    {
        "/tmp/", "/work/",
    };

    public static AuditDecision Evaluate(Clause clause, ParsedCommand parent, Clause? nextClause)
    {
        if (parent.IsUnparseable)
        {
            return AuditDecision.Warn($"unparseable: {parent.UnparseableReason ?? "unknown"} — consumer should safe-fail");
        }

        // Rule 1: rm -rf outside workspace.
        if (IsRmRf(clause))
        {
            foreach (var arg in clause.Args)
            {
                if (!arg.IsPath || arg.IsCwdAttribution)
                {
                    continue;
                }

                var resolved = arg.Resolved ?? arg.Raw;
                if (resolved.StartsWith('/') && !StartsWithAny(resolved, WorkspacePrefixes))
                {
                    return AuditDecision.Deny($"rm -rf outside workspace: {resolved}");
                }
            }
        }

        // Rule 2: any path resolved into a protected zone.
        foreach (var arg in clause.Args)
        {
            if (!arg.IsPath || arg.IsCwdAttribution)
            {
                continue;
            }

            var resolved = arg.Resolved ?? arg.Raw;
            if (StartsWithAny(resolved, ProtectedPrefixes))
            {
                return AuditDecision.Deny($"protected zone: {resolved}");
            }
        }

        // Rule 3: curl | bash / sh — classic remote-code-execution shape.
        if (clause.Verb.Joined == "curl" && nextClause is not null
            && nextClause.Operator == CompoundOperator.Pipe
            && (nextClause.Verb.Joined == "bash" || nextClause.Verb.Joined == "sh"))
        {
            return AuditDecision.Warn("curl pipe to shell: review carefully");
        }

        // Rule 4: dynamic content masquerading as a path-shaped argument.
        // We can't perfectly reconstruct intent, but a DynamicSkip arg that
        // wasn't classified as a path AND sits next to verbs that take paths
        // is enough to flag for review.
        if (VerbTakesPaths(clause.Verb.Joined))
        {
            foreach (var arg in clause.Args)
            {
                if (arg.Kind == ArgKind.DynamicSkip && !arg.IsPath && !arg.IsFlag)
                {
                    return AuditDecision.Warn("dynamic content in path-arg slot — context unknown");
                }
            }
        }

        return AuditDecision.Ok();
    }

    private static bool IsRmRf(Clause clause)
    {
        if (clause.Verb.Joined != "rm")
        {
            return false;
        }

        foreach (var arg in clause.Args)
        {
            if (!arg.IsFlag)
            {
                continue;
            }

            // Match either combined (-rf, -fr) or doubled (--recursive) forms.
            if (arg.Raw is "-rf" or "-fr" or "-rfv" or "-fvr" or "-Rf")
            {
                return true;
            }

            if (arg.Raw.StartsWith("-") && !arg.Raw.StartsWith("--")
                && arg.Raw.Contains('r', StringComparison.OrdinalIgnoreCase)
                && arg.Raw.Contains('f', StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (arg.Raw is "--recursive" or "--force")
            {
                // Either alone is insufficient — only combined --recursive --force counts.
                // Cheap check: see if any other flag in the clause is the other half.
                foreach (var other in clause.Args)
                {
                    if (ReferenceEquals(other, arg))
                    {
                        continue;
                    }

                    if (arg.Raw == "--recursive" && other.Raw == "--force")
                    {
                        return true;
                    }

                    if (arg.Raw == "--force" && other.Raw == "--recursive")
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool VerbTakesPaths(string verb) => verb switch
    {
        "cd" or "rm" or "cp" or "mv" or "cat" or "ls" or "mkdir" or "rmdir"
        or "touch" or "chmod" or "chown" or "tar" or "zip" or "unzip"
        or "find" or "grep" => true,
        _ => false,
    };

    private static bool StartsWithAny(string value, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
