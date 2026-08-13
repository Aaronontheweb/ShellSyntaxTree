// -----------------------------------------------------------------------
// <copyright file="BashPerVerbRulesTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree.Internal.Bash.Verbs;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Unit tests for <see cref="BashPerVerbRules"/>. Covers SPEC §7's per-verb
/// path-arg overrides and the flag-value path classification table.
/// </summary>
public class BashPerVerbRulesTests
{
    private static VerbChain Verb(params string[] tokens) =>
        new() { Tokens = tokens };

    // ---------------------------------------------------------------- chmod/chown/chgrp

    [Fact]
    public void Chmod_first_positional_is_not_a_path()
    {
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("chmod"), 0, "755"));
    }

    [Fact]
    public void Chmod_second_positional_is_a_path()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("chmod"), 1, "/etc/passwd"));
    }

    [Fact]
    public void Chown_first_positional_is_not_a_path()
    {
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("chown"), 0, "user:group"));
    }

    [Fact]
    public void Chown_second_positional_is_a_path()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("chown"), 1, "/var/log"));
    }

    [Fact]
    public void Chgrp_first_is_group_rest_are_paths()
    {
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("chgrp"), 0, "wheel"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("chgrp"), 1, "/var/log"));
    }

    // ---------------------------------------------------------------- ln

    [Fact]
    public void Ln_all_positionals_are_paths()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("ln"), 0, "/a/source"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("ln"), 1, "/b/target"));
    }

    // ---------------------------------------------------------------- find

    [Fact]
    public void Find_first_positional_is_a_path()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("find"), 0, "/var/log"));
    }

    [Fact]
    public void Find_predicate_args_are_not_paths()
    {
        // Predicate args like `-name`, `"*.log"`, `-type`, `f` are not paths.
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("find"), 1, "*.log"));
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("find"), 2, "f"));
    }

    // ---------------------------------------------------------------- grep / rg

    [Fact]
    public void Grep_first_positional_is_pattern()
    {
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("grep"), 0, "pattern"));
    }

    [Fact]
    public void Grep_second_positional_is_path()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("grep"), 1, "/etc/hosts"));
    }

    [Fact]
    public void Rg_first_is_pattern_rest_paths()
    {
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("rg"), 0, "regex"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("rg"), 1, "/path"));
    }

    // ---------------------------------------------------------------- sed / awk

    [Fact]
    public void Sed_first_is_script_rest_paths()
    {
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("sed"), 0, "s/foo/bar/"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("sed"), 1, "file.txt"));
    }

    [Fact]
    public void Awk_first_is_program_rest_paths()
    {
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("awk"), 0, "{print $1}"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("awk"), 1, "input.txt"));
    }

    // ---------------------------------------------------------------- tar (default rule)

    [Fact]
    public void Tar_default_rule_marks_all_positionals_as_paths()
    {
        // Locked interpretation #8: no action-flag awareness in v0.1.
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("tar"), 0, "archive.tar.gz"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("tar"), 1, "/target"));
    }

    // ---------------------------------------------------------------- curl / wget

    [Fact]
    public void Curl_url_positional_is_not_a_path()
    {
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("curl"), 0, "https://example.com"));
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("curl"), 1, "https://other.example"));
    }

    [Fact]
    public void Wget_url_positional_is_not_a_path()
    {
        Assert.False(BashPerVerbRules.IsPositionalPathArg(Verb("wget"), 0, "https://example.com"));
    }

    // ---------------------------------------------------------------- scp / rsync / sftp

    [Fact]
    public void Scp_all_positionals_are_paths()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("scp"), 0, "user@host:/path"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("scp"), 1, "/local"));
    }

    [Fact]
    public void Rsync_all_positionals_are_paths()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("rsync"), 0, "src/"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("rsync"), 1, "dst/"));
    }

    // ---------------------------------------------------------------- cd / chdir / pushd / popd

    [Fact]
    public void Cd_first_positional_is_path()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("cd"), 0, "/tmp"));
    }

    [Fact]
    public void Chdir_first_positional_is_path()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("chdir"), 0, "/tmp"));
    }

    [Fact]
    public void Pushd_first_positional_is_path()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("pushd"), 0, "/tmp"));
    }

    [Fact]
    public void Popd_first_positional_is_path()
    {
        // popd takes no positional path arg in real bash, but if one's
        // typed we still classify it as a path slot. CwdVerbs are also
        // FileVerbs by the table.
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("popd"), 0, "+1"));
    }

    // ---------------------------------------------------------------- default file-verb rule

    [Fact]
    public void Default_file_verb_rule_all_positionals_are_paths()
    {
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("cat"), 0, "file.txt"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("rm"), 0, "/tmp/foo"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("cp"), 0, "/src"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("cp"), 1, "/dst"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("mv"), 0, "/src"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(Verb("ls"), 0, "/tmp"));
    }

    // ---------------------------------------------------------------- non-file-verb fallback

    [Fact]
    public void Non_file_verb_uses_looks_like_path_heuristic_path_shaped()
    {
        // unknown verb + path-shaped token → IsPath=true.
        Assert.True(BashPerVerbRules.IsPositionalPathArg(
            Verb("totally-unknown-verb"), 0, "/etc/foo"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(
            Verb("totally-unknown-verb"), 0, "./config.json"));
    }

    [Fact]
    public void Non_file_verb_uses_looks_like_path_heuristic_word_shaped()
    {
        // unknown verb + plain word → IsPath=false.
        Assert.False(BashPerVerbRules.IsPositionalPathArg(
            Verb("totally-unknown-verb"), 0, "argument"));
        Assert.False(BashPerVerbRules.IsPositionalPathArg(
            Verb("totally-unknown-verb"), 1, "main"));
    }

    [Fact]
    public void Git_args_use_looks_like_path_fallback()
    {
        // git is in BashArity but NOT in FileVerbs, so its positionals
        // fall back to LooksLikePath. `origin` / `main` are not path-shaped;
        // `/local/repo` is.
        Assert.False(BashPerVerbRules.IsPositionalPathArg(
            Verb("git", "push"), 0, "origin"));
        Assert.False(BashPerVerbRules.IsPositionalPathArg(
            Verb("git", "push"), 1, "main"));
        Assert.True(BashPerVerbRules.IsPositionalPathArg(
            Verb("git", "clone"), 0, "/local/repo"));
    }

    // ---------------------------------------------------------------- ValueOfFlagIsPath

    [Fact]
    public void Git_dash_C_value_is_a_path()
    {
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("git", "-C"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("git", "--git-dir"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("git", "--work-tree"));
    }

    [Fact]
    public void Curl_output_and_dump_header_values_are_paths_data_is_not()
    {
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("curl", "-o"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("curl", "--output"));
        Assert.False(BashPerVerbRules.ValueOfFlagIsPath("curl", "-d"));
        Assert.False(BashPerVerbRules.ValueOfFlagIsPath("curl", "--data"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("curl", "-D"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("curl", "--dump-header"));
    }

    [Fact]
    public void Wget_log_and_document_output_values_are_paths()
    {
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("wget", "-o"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("wget", "--output-file"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("wget", "-O"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("wget", "--output-document"));
    }

    [Fact]
    public void Docker_dash_f_value_is_a_path_dash_v_is_not()
    {
        // Locked interpretation #8.
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("docker", "-f"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("docker", "--file"));
        Assert.False(BashPerVerbRules.ValueOfFlagIsPath("docker", "-v"));
        Assert.False(BashPerVerbRules.ValueOfFlagIsPath("docker", "--volume"));
    }

    [Fact]
    public void Tar_dash_f_and_dash_C_values_are_paths()
    {
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("tar", "-f"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("tar", "--file"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("tar", "-C"));
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("tar", "--directory"));
    }

    [Theory]
    [InlineData("-subj", true, false)]
    [InlineData("-serial", false, false)]
    [InlineData("-key", false, false)]
    public void Openssl_option_table_contains_only_stable_cross_subcommand_semantics(
        string flag,
        bool consumesValue,
        bool valueIsPath)
    {
        Assert.True(BashVerbs.FlagsWithValue.TryGetValue("openssl", out var flags));
        Assert.Equal(consumesValue, flags.Contains(flag));
        Assert.Equal(valueIsPath, BashPerVerbRules.ValueOfFlagIsPath("openssl", flag));
    }

    [Fact]
    public void Unknown_verb_or_flag_returns_false()
    {
        Assert.False(BashPerVerbRules.ValueOfFlagIsPath("unknown", "-x"));
        Assert.False(BashPerVerbRules.ValueOfFlagIsPath("git", "--unknown"));
    }

    [Fact]
    public void Native_flag_lookup_is_case_sensitive_while_verb_lookup_is_not()
    {
        Assert.True(BashPerVerbRules.ValueOfFlagIsPath("GIT", "-C"));
        Assert.False(BashPerVerbRules.ValueOfFlagIsPath("GIT", "-c"));
        Assert.False(BashPerVerbRules.ValueOfFlagIsPath("Git", "--Git-Dir"));
    }

    [Theory]
    [InlineData("git", "-c", true, false, false)]
    [InlineData("git", "-C", true, true, false)]
    [InlineData("curl", "-d", true, false, false)]
    [InlineData("curl", "-D", true, true, false)]
    [InlineData("curl", "-o", true, true, false)]
    [InlineData("curl", "-O", false, false, false)]
    [InlineData("curl", "--data", true, false, false)]
    [InlineData("curl", "--dump-header", true, true, false)]
    [InlineData("curl", "--output", true, true, false)]
    [InlineData("wget", "-o", true, true, false)]
    [InlineData("wget", "-O", true, true, false)]
    [InlineData("wget", "--output-file", true, true, false)]
    [InlineData("wget", "--output-document", true, true, false)]
    [InlineData("tar", "-c", false, false, false)]
    [InlineData("tar", "-C", true, true, false)]
    [InlineData("tar", "-f", true, true, false)]
    [InlineData("tar", "-F", true, false, true)]
    [InlineData("tar", "--info-script", true, false, true)]
    [InlineData("tar", "--new-volume-script", true, false, true)]
    public void Native_option_binding_matrix(
        string verb,
        string flag,
        bool consumesValue,
        bool valueIsPath,
        bool valueIsOpaqueCommand)
    {
        Assert.True(BashVerbs.FlagsWithValue.TryGetValue(verb, out var flags));
        Assert.Equal(consumesValue, flags.Contains(flag));
        Assert.Equal(valueIsPath, BashPerVerbRules.ValueOfFlagIsPath(verb, flag));
        Assert.Equal(
            valueIsOpaqueCommand,
            BashPerVerbRules.ValueOfFlagIsOpaqueCommand(verb, flag));
    }

    [Theory]
    [InlineData("-d", "payload", false, "payload")]
    [InlineData("--data", "name=Jane", false, "name=Jane")]
    [InlineData("-d", "@request.json", true, "request.json")]
    [InlineData("--data", "@/etc/passwd", true, "/etc/passwd")]
    [InlineData("-d", "@-", false, "@-")]
    [InlineData("--data-raw", "@request.json", false, "@request.json")]
    public void Curl_data_file_reference_is_operand_sensitive(
        string flag,
        string value,
        bool isPath,
        string expectedPathValue)
    {
        var actual = BashPerVerbRules.TryGetFlagValuePath(
            "curl", flag, value, out var pathValue);

        Assert.Equal(isPath, actual);
        Assert.Equal(expectedPathValue, pathValue);
    }

    // ---------------------------------------------------------------- empty verb chain

    [Fact]
    public void Empty_verb_chain_falls_back_to_looks_like_path()
    {
        var empty = new VerbChain();
        Assert.True(BashPerVerbRules.IsPositionalPathArg(empty, 0, "/etc/foo"));
        Assert.False(BashPerVerbRules.IsPositionalPathArg(empty, 0, "argument"));
    }
}
