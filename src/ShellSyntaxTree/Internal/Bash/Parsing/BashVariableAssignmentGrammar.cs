// -----------------------------------------------------------------------
// <copyright file="BashVariableAssignmentGrammar.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

internal static class BashVariableAssignmentGrammar
{
    private static readonly HashSet<string> ShellOwnedNames = new(StringComparer.Ordinal)
    {
        "BASH", "BASHOPTS", "BASHPID", "BASH_ALIASES", "BASH_ARGC", "BASH_ARGV",
        "BASH_ARGV0", "BASH_CMDS", "BASH_COMMAND", "BASH_COMPAT", "BASH_ENV",
        "BASH_EXECUTION_STRING", "BASH_LINENO", "BASH_LOADABLES_PATH", "BASH_REMATCH",
        "BASH_SOURCE", "BASH_SUBSHELL", "BASH_VERSINFO", "BASH_VERSION",
        "BASH_XTRACEFD", "CDPATH", "CHILD_MAX", "COLUMNS", "COMP_CWORD", "COMP_KEY",
        "COMP_LINE", "COMP_POINT", "COMP_TYPE", "COMP_WORDBREAKS", "COPROC",
        "DIRSTACK", "EMACS", "ENV", "EPOCHREALTIME", "EPOCHSECONDS", "EUID",
        "EXECIGNORE", "FCEDIT", "FIGNORE", "FUNCNAME", "FUNCNEST", "GLOBIGNORE",
        "GLOBSORT",
        "GROUPS", "HISTCMD", "HISTCONTROL", "HISTFILE", "HISTFILESIZE", "HISTIGNORE",
        "HISTSIZE", "HISTTIMEFORMAT", "HOME", "HOSTFILE", "HOSTNAME", "HOSTTYPE",
        "IFS", "IGNOREEOF", "INPUTRC", "INSIDE_EMACS", "LANG", "LC_ALL",
        "LC_COLLATE", "LC_CTYPE", "LC_MESSAGES", "LC_NUMERIC", "LC_TIME", "LINENO",
        "LINES", "MACHTYPE", "MAIL", "MAILCHECK", "MAILPATH", "MAPFILE", "OLDPWD",
        "OPTARG", "OPTERR", "OPTIND", "OSTYPE", "PATH", "PIPESTATUS",
        "POSIXLY_CORRECT", "PPID", "PROMPT_COMMAND", "PS0", "PS1", "PS2", "PS3",
        "PS4", "PWD", "RANDOM", "READLINE_ARGUMENT", "READLINE_LINE", "READLINE_MARK",
        "READLINE_POINT", "REPLY", "SECONDS", "SHELL", "SHELLOPTS", "SHLVL",
        "SRANDOM", "TIMEFORMAT", "TMOUT", "TMPDIR", "UID", "_", "auto_resume", "histchars",
    };

    internal static bool IsEligibleCommandEnvironmentName(string name)
    {
        if (!IsIdentifier(name) || ShellOwnedNames.Contains(name) ||
            IsShellOwnedFamily(name) ||
            name is "LIBPATH" or "SHLIB_PATH")
        {
            return false;
        }

        return !name.StartsWith("BASH_FUNC_", StringComparison.Ordinal) &&
            !name.StartsWith("LD_", StringComparison.Ordinal) &&
            !name.StartsWith("DYLD_", StringComparison.Ordinal);
    }

    private static bool IsShellOwnedFamily(string name) =>
        name.StartsWith("BASH_", StringComparison.Ordinal) ||
        name.StartsWith("COMP", StringComparison.Ordinal) ||
        name.StartsWith("HIST", StringComparison.Ordinal) ||
        name.StartsWith("READLINE_", StringComparison.Ordinal) ||
        name.StartsWith("LC_", StringComparison.Ordinal) ||
        name.StartsWith("PROMPT", StringComparison.Ordinal) ||
        IsPromptVariable(name);

    private static bool IsPromptVariable(string name)
    {
        if (name.Length < 3 || name[0] != 'P' || name[1] != 'S')
        {
            return false;
        }

        for (var index = 2; index < name.Length; index++)
        {
            if (name[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsIdentifierPart(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierStart(char value) =>
        value == '_' || value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value) || value is >= '0' and <= '9';
}
