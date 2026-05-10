// -----------------------------------------------------------------------
// <copyright file="BashResolverTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree.Internal.Resolving;
using Xunit;

namespace ShellSyntaxTree.Tests.Resolving;

/// <summary>
/// Unit tests for <see cref="BashResolver"/> — SPEC §8 path-token resolver,
/// plus the <c>LooksLikePath</c> heuristic. Coverage is intentionally broad
/// across the table (tilde, $HOME, env-var DynamicSkip, glob, absolute /
/// relative paths, filesystem:: prefix, combined cases).
/// </summary>
public class BashResolverTests
{
    private static BashParserOptions OptionsFor(string? home = "/home/test", string? wd = "/work") =>
        new() { HomeDirectory = home, WorkingDirectory = wd };

    // ---------------------------------------------------------------- tilde

    [Fact]
    public void Tilde_alone_in_path_slot_expands_to_home()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("~", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Tilde, kind);
        Assert.Equal("/home/test", resolved);
        Assert.True(isPath);
    }

    [Fact]
    public void Tilde_with_subpath_in_path_slot_joins_to_home()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("~/file.txt", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Tilde, kind);
        Assert.Equal("/home/test/file.txt", resolved);
        Assert.True(isPath);
    }

    [Fact]
    public void Tilde_user_in_path_slot_is_dynamic_skip()
    {
        // ~bob and ~bob/path → unsupported in v0.1 → DynamicSkip.
        var (kind, resolved, isPath) = BashResolver.Resolve("~bob/file", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.DynamicSkip, kind);
        Assert.Null(resolved);
        Assert.False(isPath);
    }

    [Fact]
    public void Tilde_user_in_non_path_slot_is_tilde_no_resolve()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("~bob", treatAsPath: false, OptionsFor());
        Assert.Equal(ArgKind.Tilde, kind);
        Assert.Null(resolved);
        Assert.False(isPath);
    }

    [Fact]
    public void Tilde_in_non_path_slot_is_tilde_kind_no_resolve()
    {
        // Non-path slot still classifies Kind=Tilde for consumer visibility
        // but doesn't resolve (the slot is, by definition, not a path).
        var (kind, resolved, isPath) = BashResolver.Resolve("~/foo", treatAsPath: false, OptionsFor());
        Assert.Equal(ArgKind.Tilde, kind);
        Assert.Null(resolved);
        Assert.False(isPath);
    }

    // ---------------------------------------------------------------- $HOME

    [Fact]
    public void Dollar_HOME_in_path_slot_substitutes()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("$HOME/Downloads", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Tilde, kind); // treat as tilde-equivalent
        Assert.Equal("/home/test/Downloads", resolved);
        Assert.True(isPath);
    }

    [Fact]
    public void Brace_HOME_in_path_slot_substitutes()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("${HOME}/Downloads", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Tilde, kind);
        Assert.Equal("/home/test/Downloads", resolved);
        Assert.True(isPath);
    }

    [Fact]
    public void Dollar_HOME_alone_in_path_slot_substitutes()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("$HOME", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Tilde, kind);
        Assert.Equal("/home/test", resolved);
        Assert.True(isPath);
    }

    // ---------------------------------------------------------------- other env vars

    [Fact]
    public void Other_env_var_in_path_slot_is_dynamic_skip()
    {
        // SPEC §12 example: rm $UNRESOLVED/foo.
        var (kind, resolved, isPath) = BashResolver.Resolve("$UNRESOLVED/foo", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.DynamicSkip, kind);
        Assert.Null(resolved);
        Assert.False(isPath);
    }

    [Fact]
    public void Brace_env_var_in_path_slot_is_dynamic_skip()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("${PATH}/bin/foo", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.DynamicSkip, kind);
        Assert.Null(resolved);
        Assert.False(isPath);
    }

    [Fact]
    public void Other_env_var_in_non_path_slot_is_env_var()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("$REPO", treatAsPath: false, OptionsFor());
        Assert.Equal(ArgKind.EnvVar, kind);
        Assert.Null(resolved);
        Assert.False(isPath);
    }

    [Fact]
    public void Mixed_HOME_and_other_var_still_dynamic_skip_in_path_slot()
    {
        // $HOME expands first; then the surviving $OTHER triggers DynamicSkip.
        var (kind, resolved, isPath) = BashResolver.Resolve(
            "$HOME/$OTHER/file", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.DynamicSkip, kind);
        Assert.Null(resolved);
        Assert.False(isPath);
    }

    // ---------------------------------------------------------------- glob

    [Fact]
    public void Star_glob_in_path_slot_is_glob_with_is_path_true()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("*.txt", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Glob, kind);
        Assert.Null(resolved);
        Assert.True(isPath); // locked interpretation #3 — covering-dir signal
    }

    [Fact]
    public void Glob_with_dir_in_path_slot_is_glob_is_path_true()
    {
        var (kind, _, isPath) = BashResolver.Resolve("/tmp/*.bak", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Glob, kind);
        Assert.True(isPath);
    }

    [Fact]
    public void Question_glob_in_path_slot_is_glob()
    {
        var (kind, _, isPath) = BashResolver.Resolve("?file", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Glob, kind);
        Assert.True(isPath);
    }

    [Fact]
    public void Bracket_glob_in_path_slot_is_glob()
    {
        var (kind, _, isPath) = BashResolver.Resolve("[ab]", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Glob, kind);
        Assert.True(isPath);
    }

    [Fact]
    public void Glob_in_non_path_slot_is_glob_is_path_false()
    {
        var (kind, _, isPath) = BashResolver.Resolve("*.txt", treatAsPath: false, OptionsFor());
        Assert.Equal(ArgKind.Glob, kind);
        Assert.False(isPath);
    }

    // ---------------------------------------------------------------- filesystem:: prefix

    [Fact]
    public void Filesystem_prefix_is_stripped_then_resolved()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve(
            "filesystem::/path/to/foo", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Literal, kind);
        Assert.Equal("/path/to/foo", resolved);
        Assert.True(isPath);
    }

    // ---------------------------------------------------------------- absolute + relative paths

    [Fact]
    public void Absolute_unix_path_is_literal()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("/etc/hosts", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Literal, kind);
        Assert.Equal("/etc/hosts", resolved);
        Assert.True(isPath);
    }

    [Fact]
    public void Relative_path_in_path_slot_joins_to_working_directory()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve("foo.txt", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Literal, kind);
        Assert.Equal("/work/foo.txt", resolved);
        Assert.True(isPath);
    }

    [Fact]
    public void Relative_dot_slash_joins_to_working_directory()
    {
        var (kind, resolved, _) = BashResolver.Resolve("./foo.txt", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Literal, kind);
        Assert.Equal("/work/foo.txt", resolved);
    }

    [Fact]
    public void Windows_drive_letter_is_treated_as_rooted()
    {
        // Path.GetFullPath honors drive letters; on Linux it may normalize
        // to a relative-feeling path. Either way, the resolver must NOT
        // crash and must classify as Literal (rooted path), not DynamicSkip.
        var (kind, resolved, isPath) = BashResolver.Resolve(
            "C:\\Users\\foo", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Literal, kind);
        Assert.NotNull(resolved);
        Assert.True(isPath);
    }

    // ---------------------------------------------------------------- non-path slot literals

    [Fact]
    public void Plain_literal_in_non_path_slot_is_literal()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve(
            "origin", treatAsPath: false, OptionsFor());
        Assert.Equal(ArgKind.Literal, kind);
        Assert.Null(resolved);
        Assert.False(isPath);
    }

    [Fact]
    public void Plain_literal_in_path_slot_resolves()
    {
        var (kind, resolved, isPath) = BashResolver.Resolve(
            "input", treatAsPath: true, OptionsFor());
        Assert.Equal(ArgKind.Literal, kind);
        Assert.Equal("/work/input", resolved);
        Assert.True(isPath);
    }

    // ---------------------------------------------------------------- LooksLikePath

    [Fact]
    public void LooksLikePath_unix_absolute_is_true()
    {
        Assert.True(BashResolver.LooksLikePath("/etc/hosts"));
    }

    [Fact]
    public void LooksLikePath_relative_dot_is_true()
    {
        Assert.True(BashResolver.LooksLikePath("./foo"));
        Assert.True(BashResolver.LooksLikePath("../bar"));
    }

    [Fact]
    public void LooksLikePath_tilde_is_true()
    {
        Assert.True(BashResolver.LooksLikePath("~/foo"));
        Assert.True(BashResolver.LooksLikePath("~"));
    }

    [Fact]
    public void LooksLikePath_with_slash_is_true()
    {
        Assert.True(BashResolver.LooksLikePath("a/b"));
        Assert.True(BashResolver.LooksLikePath("a\\b"));
    }

    [Fact]
    public void LooksLikePath_with_extension_is_true()
    {
        Assert.True(BashResolver.LooksLikePath("readme.md"));
        Assert.True(BashResolver.LooksLikePath("config.json"));
        Assert.True(BashResolver.LooksLikePath("data.tar.gz"));
        Assert.True(BashResolver.LooksLikePath("archive.zip"));
    }

    [Fact]
    public void LooksLikePath_plain_word_is_false()
    {
        Assert.False(BashResolver.LooksLikePath("origin"));
        Assert.False(BashResolver.LooksLikePath("main"));
        Assert.False(BashResolver.LooksLikePath("0755"));
        Assert.False(BashResolver.LooksLikePath("pattern"));
    }

    [Fact]
    public void LooksLikePath_windows_drive_letter_is_true()
    {
        Assert.True(BashResolver.LooksLikePath("C:\\foo"));
        Assert.True(BashResolver.LooksLikePath("D:"));
    }

    [Fact]
    public void LooksLikePath_unc_path_is_true()
    {
        Assert.True(BashResolver.LooksLikePath("\\\\server\\share"));
    }

    // ---------------------------------------------------------------- option fallbacks

    [Fact]
    public void Home_falls_back_to_environment_user_profile_when_null()
    {
        // We can't pin a specific value (it depends on the test runner's
        // environment), but the resolver must produce *something* non-empty.
        var options = OptionsFor(home: null);
        var (kind, resolved, isPath) = BashResolver.Resolve("~", treatAsPath: true, options);
        Assert.Equal(ArgKind.Tilde, kind);
        Assert.True(isPath);
        Assert.False(string.IsNullOrEmpty(resolved));
    }

    [Fact]
    public void Working_directory_null_falls_back_to_environment_cwd()
    {
        // Relative path with no explicit WorkingDirectory falls back to
        // Environment.CurrentDirectory. The result is host-dependent, but
        // *some* absolute resolved value must come back.
        var options = OptionsFor(wd: null);
        var (kind, resolved, _) = BashResolver.Resolve("foo.txt", treatAsPath: true, options);
        Assert.Equal(ArgKind.Literal, kind);
        Assert.NotNull(resolved);
        Assert.NotEqual("foo.txt", resolved);
    }
}
