// -----------------------------------------------------------------------
// <copyright file="PiiAuditTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Sdk;

namespace ShellSyntaxTree.Tests.Corpus;

/// <summary>
/// PII audit gate per SPEC §14 / SPEC.POWERSHELL.md §14. Scans every JSON
/// entry under the executable corpus and the pre-implementation design corpus
/// for the forbidden patterns listed in the sanitization table. The audit
/// reads from the build-output copies so CI runs against the same bytes a
/// developer's local <c>dotnet test</c> would.
/// </summary>
/// <remarks>
/// Scanning policy:
/// <list type="bullet">
///   <item>Only <c>input</c>, <c>notes</c>, and <c>raw</c> string fields
///         are scanned. Synthetic resolved paths and the
///         <c>&lt;dynamic-cwd&gt;</c> sentinel surface in other fields and
///         would generate noise (e.g. <c>/work/foo</c> from
///         WorkingDirectory pinning).</item>
///   <item>A small allowlist of generic placeholder usernames is honored:
///         <c>user, test, foo, dev, runner, gh-actions, ci</c>. Anything
///         else under <c>/home/</c> or <c>/Users/</c> trips the audit.</item>
///   <item>Match collection is exhaustive (no fail-fast) so a single CI
///         run surfaces every offending file at once.</item>
/// </list>
/// </remarks>
public class PiiAuditTests
{
    // -------- allowed-placeholder allowlists --------

    private static readonly HashSet<string> AllowedHomeUsernames =
        new(StringComparer.Ordinal)
        {
            "user", "test", "foo", "dev", "runner", "gh-actions", "ci",
        };

    private static readonly HashSet<string> AllowedUsersUsernames =
        new(StringComparer.Ordinal)
        {
            "user", "test", "foo", "dev", "runner", "ci",
        };

    private static readonly HashSet<string> AllowedRepoNames =
        new(StringComparer.Ordinal)
        {
            "sample-repo", "project", "repo",
        };

    // -------- pattern set (SPEC §14 transposed to detection regex) --------

    private static readonly Regex SlackChannelPattern =
        new(@"\bD[A-Z0-9]{10}\b", RegexOptions.Compiled);

    private static readonly Regex SlackThreadPattern =
        new(@"\b\d{10}\.\d{6}\b", RegexOptions.Compiled);

    private static readonly Regex EmailPattern =
        new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);

    private static readonly Regex LongKeyPattern =
        new(@"[A-Za-z0-9]{32,}", RegexOptions.Compiled);

    // The home/users patterns capture the username segment; we then allow-list it.
    private static readonly Regex HomePattern =
        new(@"/home/([a-zA-Z0-9_.-]+)/", RegexOptions.Compiled);

    private static readonly Regex UsersPattern =
        new(@"/Users/([a-zA-Z0-9_.-]+)/", RegexOptions.Compiled);

    // Repository-path pattern: /home/<user>/repositories/<org>/<repo>/... where
    // <repo> isn't one of the public-corpus placeholders.
    private static readonly Regex RepoPathPattern =
        new(@"/home/[^/]+/repositories/[^/]+/([a-zA-Z0-9_.-]+)/", RegexOptions.Compiled);

    // SPEC.POWERSHELL.md §14: a concrete C:\Users\<username>\ path (mixed
    // slashes allowed). A literal $env:USERNAME / $env:USERPROFILE reference
    // is not PII and is not matched here.
    private static readonly Regex WindowsUserPattern =
        new(@"[A-Za-z]:[\\/]Users[\\/]([A-Za-z0-9_.-]+)[\\/]", RegexOptions.Compiled);

    // SPEC.POWERSHELL.md §14: a UNC \\<hostname>\share path.
    private static readonly Regex UncHostPattern =
        new(@"\\\\([A-Za-z0-9_.-]+)\\", RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedUncHosts =
        new(StringComparer.Ordinal) { "internal-host.example" };

    [Fact]
    public void Corpus_contains_no_pii_per_spec_section_14()
    {
        var hits = new List<string>();
        var roots = new[] { "Corpus", "DesignCorpus" };
        foreach (var relativeRoot in roots)
        {
            var root = Path.Combine(AppContext.BaseDirectory, relativeRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(
                root, "*.json", SearchOption.AllDirectories).OrderBy(f => f))
            {
                var name = Path.GetRelativePath(AppContext.BaseDirectory, file)
                    .Replace('\\', '/');
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    Walk(doc.RootElement, name, fieldPath: string.Empty, hits);
                }
                catch (JsonException ex)
                {
                    hits.Add($"{name}: failed to parse JSON for PII audit: {ex.Message}");
                }
            }
        }

        if (hits.Count > 0)
        {
            throw new XunitException(
                "PII audit found forbidden patterns in corpus JSON. SPEC §14 sanitization is mandatory.\n"
                + "  Fix each entry below before merging.\n"
                + string.Join("\n", hits.Select(h => "  - " + h)));
        }
    }

    private static void Walk(JsonElement element, string fileName, string fieldPath, List<string> hits)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var childPath = string.IsNullOrEmpty(fieldPath)
                        ? prop.Name
                        : fieldPath + "." + prop.Name;
                    Walk(prop.Value, fileName, childPath, hits);
                }
                break;

            case JsonValueKind.Array:
                var idx = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, fileName, fieldPath + "[" + idx + "]", hits);
                    idx++;
                }
                break;

            case JsonValueKind.String:
                if (ShouldScan(fieldPath))
                {
                    ScanString(element.GetString() ?? string.Empty, fileName, fieldPath, hits);
                }
                break;
        }
    }

    /// <summary>
    /// True when a long alphanumeric run has too few distinct characters to
    /// be a credential — e.g. the repeated-character filler of the
    /// over-cap corpus entry. A real API key has high character diversity.
    /// </summary>
    private static bool IsLowEntropyRun(string token)
    {
        var distinct = new HashSet<char>();
        foreach (var c in token)
        {
            distinct.Add(c);
            if (distinct.Count > 4)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decide whether a JSON string at <paramref name="fieldPath"/> is in
    /// scope for the audit. SPEC §14: scan <c>input</c>, <c>notes</c>,
    /// and any <c>raw</c> nested under args. Skip synthetic fields like
    /// <c>resolved</c>, <c>target</c>, etc. — those carry parser-produced
    /// paths (e.g. <c>/home/test/file</c>) that we explicitly want to allow.
    /// </summary>
    private static bool ShouldScan(string fieldPath)
    {
        if (fieldPath == "input") return true;
        if (fieldPath == "notes") return true;
        if (fieldPath.EndsWith(".raw", StringComparison.Ordinal)) return true;
        return false;
    }

    private static void ScanString(string value, string fileName, string fieldPath, List<string> hits)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        // Slack channel IDs.
        foreach (Match m in SlackChannelPattern.Matches(value))
        {
            hits.Add($"{fileName} ({fieldPath}): slack channel id '{m.Value}' (SPEC §14)");
        }

        // Slack thread IDs.
        foreach (Match m in SlackThreadPattern.Matches(value))
        {
            hits.Add($"{fileName} ({fieldPath}): slack thread id '{m.Value}' (SPEC §14)");
        }

        // Email addresses.
        foreach (Match m in EmailPattern.Matches(value))
        {
            hits.Add($"{fileName} ({fieldPath}): email '{m.Value}' (SPEC §14)");
        }

        // Long alphanumeric tokens (potential API keys). A base64
        // -EncodedCommand payload and a repeated-character filler are
        // intentional corpus content, not leaked secrets — exempt them.
        var isEncodedCommand =
            value.IndexOf("-EncodedCommand", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("-e ", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isEncodedCommand)
        {
            foreach (Match m in LongKeyPattern.Matches(value))
            {
                if (IsLowEntropyRun(m.Value))
                {
                    continue;
                }

                hits.Add($"{fileName} ({fieldPath}): long token '{m.Value.Substring(0, Math.Min(8, m.Value.Length))}…' ({m.Value.Length} chars; SPEC §14 key pattern)");
            }
        }

        // /home/<user>/ — allowlist generic placeholders.
        foreach (Match m in HomePattern.Matches(value))
        {
            var user = m.Groups[1].Value;
            if (!AllowedHomeUsernames.Contains(user))
            {
                hits.Add($"{fileName} ({fieldPath}): /home/{user}/ — not in allowed-placeholder list (SPEC §14)");
            }
        }

        // /Users/<user>/ (macOS).
        foreach (Match m in UsersPattern.Matches(value))
        {
            var user = m.Groups[1].Value;
            if (!AllowedUsersUsernames.Contains(user))
            {
                hits.Add($"{fileName} ({fieldPath}): /Users/{user}/ — not in allowed-placeholder list (SPEC §14)");
            }
        }

        // /home/<u>/repositories/<org>/<repo>/ — repo name allowlist.
        foreach (Match m in RepoPathPattern.Matches(value))
        {
            var repo = m.Groups[1].Value;
            if (!AllowedRepoNames.Contains(repo))
            {
                hits.Add($"{fileName} ({fieldPath}): repository path '/repositories/.../{repo}/' — not in allowed-placeholder list (SPEC §14)");
            }
        }

        // C:\Users\<username>\ (SPEC.POWERSHELL.md §14).
        foreach (Match m in WindowsUserPattern.Matches(value))
        {
            var user = m.Groups[1].Value;
            if (!AllowedUsersUsernames.Contains(user))
            {
                hits.Add($"{fileName} ({fieldPath}): Windows user path 'Users\\{user}\\' — not in allowed-placeholder list (SPEC.POWERSHELL.md §14)");
            }
        }

        // UNC \\<hostname>\share (SPEC.POWERSHELL.md §14).
        foreach (Match m in UncHostPattern.Matches(value))
        {
            var host = m.Groups[1].Value;
            if (!AllowedUncHosts.Contains(host))
            {
                hits.Add($"{fileName} ({fieldPath}): UNC host '\\\\{host}\\' — not in allowed-placeholder list (SPEC.POWERSHELL.md §14)");
            }
        }
    }
}
