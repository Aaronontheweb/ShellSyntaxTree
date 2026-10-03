// -----------------------------------------------------------------------
// <copyright file="BashSubstitutionHeredocTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins a heredoc inside a Bash command substitution (#217). The scanner
/// skips each body to its delimiter line, so a <c>)</c> or a quote in the
/// body cannot end the substitution. The inner command is a normal
/// <c>Substitution</c> occurrence with its heredoc analysis.
/// </summary>
public class BashSubstitutionHeredocTests
{
    private static readonly BashParser Parser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
    });

    private const string PullRequest =
        "gh pr create --title t --body \"$(cat <<'EOF'\nfixes (#1) \"quoted\" text\nEOF\n)\"";

    [Fact]
    public void Literal_heredoc_in_a_substitution_gives_the_inner_command()
    {
        var parsed = Parser.Parse(PullRequest);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(2, parsed.Commands.Count);
        var cat = parsed.Commands[0];
        Assert.Equal(CommandOccurrenceRole.Substitution, cat.ImmediateRole);
        Assert.Equal("cat", string.Join(" ", Assert.IsType<ShellCommandWords.Known>(cat.CommandWords).Words));
        var heredoc = Assert.IsType<HereDocumentRedirectAnalysis>(Assert.Single(cat.Redirects));
        Assert.True(heredoc.IsComplete);
        Assert.Equal(HereDocumentExpansionMode.Literal, heredoc.Document.ExpansionMode);
        Assert.Equal("fixes (#1) \"quoted\" text\n", heredoc.Document.Body.Raw);
        var gh = parsed.Commands[1];
        Assert.Equal(
            "gh pr create",
            string.Join(" ", Assert.IsType<ShellCommandWords.Known>(gh.CommandWords).Words));
    }

    [Fact]
    public void Heredoc_spans_point_into_the_submitted_source()
    {
        var parsed = Parser.Parse(PullRequest);

        var document = Assert.IsType<HereDocumentRedirectAnalysis>(parsed.Commands[0].Redirects.Single()).Document;
        Assert.Equal(
            document.Body.Raw,
            PullRequest.Substring(document.Body.SourceStart!.Value, document.Body.SourceLength!.Value));
        Assert.Equal(
            document.Delimiter.Raw,
            PullRequest.Substring(document.Delimiter.SourceStart!.Value, document.Delimiter.SourceLength!.Value));
    }

    [Fact]
    public void Tab_stripping_heredoc_ends_at_a_tabbed_delimiter()
    {
        var parsed = Parser.Parse("x=$(cat <<-EOF\n\t\ttext )\n\tEOF\n); echo done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var heredoc = Assert.IsType<HereDocumentRedirectAnalysis>(parsed.Commands[0].Redirects.Single());
        Assert.True(heredoc.Document.StripLeadingTabs);
    }

    [Fact]
    public void Here_string_in_a_substitution_stays_supported()
    {
        var parsed = Parser.Parse("echo \"$(cat <<<word)\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.IsType<HereStringRedirectAnalysis>(parsed.Commands[0].Redirects.Single());
    }

    [Fact]
    public void Substitution_in_an_expanding_heredoc_body_is_a_visible_command()
    {
        var parsed = Parser.Parse("rm \"$(cat <<EOF\n$(id)\nEOF\n)\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(new[] { "id", "cat", "rm" }, parsed.Commands.Select(c => c.Clause.Verb.Tokens[0]));
        Assert.All(parsed.Commands.Take(2), c => Assert.Equal(CommandOccurrenceRole.Substitution, c.ImmediateRole));
    }

    [Theory]
    [InlineData("rm \"$(cat <<EOF\nvalue\n)\"")]
    [InlineData("rm \"$(cat <<EOF\nvalue\nEOF)\"")]
    [InlineData("rm \"$(cat <<EOF\nvalue\n EOF\n)\"")]
    [InlineData("rm \"$(cat <<EOF\nvalue\nEOF\r\n)\"")]
    [InlineData("rm \"$(cat <<A <<B\na\nA\nb\nB\n)\"")]
    [InlineData("rm \"$(cat <<EOF\n$unset_name\nEOF\n)\"")]
    [InlineData("rm \"$(cat <<\n)\"")]
    public void Unsupported_or_unterminated_heredoc_fails_closed(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Theory]
    [InlineData(PullRequest)]
    [InlineData("x=$(cat <<-EOF\n\t\ttext )\n\tEOF\n); echo done")]
    public void Every_prefix_parses_without_an_exception(string source)
    {
        for (var length = 0; length <= source.Length; length++)
        {
            var prefix = source.Substring(0, length);
            var exception = Record.Exception(() =>
            {
                Parser.Parse(prefix);
                Parser.TryProjectFiniteScopes(prefix, out _);
            });
            Assert.True(exception is null, $"{prefix}: {exception}");
        }
    }
}
