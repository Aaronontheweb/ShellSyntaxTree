// -----------------------------------------------------------------------
// <copyright file="PwshFileSystemTreeAccessProjection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

/// <summary>
/// Publishes the narrow, dialect-pinned filesystem tree effects proved for
/// Get-ChildItem. Compatibility path classification is not sufficient for
/// this projection: every parameter that affects binding or traversal must
/// be an exact catalog member.
/// </summary>
internal static class PwshFileSystemTreeAccessProjection
{
    private static readonly HashSet<string> ValueParameters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "-Attributes", "-Depth", "-ErrorAction", "-ErrorVariable",
            "-Exclude", "-Filter", "-Include", "-InformationAction",
            "-InformationVariable", "-LiteralPath", "-OutBuffer",
            "-OutVariable", "-Path", "-PipelineVariable", "-ProgressAction",
            "-WarningAction", "-WarningVariable",
        };

    private static readonly HashSet<string> SwitchParameters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "-Debug", "-Directory", "-File", "-Force", "-FollowSymlink",
            "-Hidden", "-Name", "-ReadOnly", "-Recurse", "-System",
            "-Verbose",
        };

    internal static bool TryApply(
        IReadOnlyList<CommandOccurrence> commands,
        PwshParserOptions options,
        out IReadOnlyList<CommandOccurrence> projected)
    {
        projected = Array.Empty<CommandOccurrence>();
        if (commands is null || options is null)
        {
            return false;
        }

        var result = new CommandOccurrence[commands.Count];
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            if (command is null || !TryCreateAccesses(command, options, out var accesses))
            {
                return false;
            }

            var candidate = command with { FileSystemTreeAccesses = accesses };
            if (!HasValidAccesses(candidate))
            {
                return false;
            }

            result[index] = candidate;
        }

        projected = result;
        return true;
    }

    internal static bool HasValidAccesses(CommandOccurrence command)
    {
        if (command is null || command.FileSystemTreeAccesses is null)
        {
            return false;
        }

        for (var index = 0; index < command.FileSystemTreeAccesses.Count; index++)
        {
            var access = command.FileSystemTreeAccesses[index];
            if (access is null ||
                !Enum.IsDefined(typeof(ShellTreeTraversalMode), access.Traversal) ||
                access.Root is null ||
                access.Root.Kind is not (
                    ShellValueDomainKind.Unknown or
                    ShellValueDomainKind.Exact or
                    ShellValueDomainKind.Pattern))
            {
                return false;
            }

            if (access.RootArgument is null)
            {
                var isUnknownMarker = access.Root is ShellValueDomain.Unknown &&
                    access.Traversal == ShellTreeTraversalMode.Unknown;
                var isImplicitRoot = access.Root is ShellValueDomain.Exact root &&
                    command.WorkingDirectory is ShellValueDomain.Exact cwd &&
                    string.Equals(root.Value, cwd.Value, StringComparison.OrdinalIgnoreCase);
                if (!isUnknownMarker && !isImplicitRoot)
                {
                    return false;
                }
            }
            else if (!ContainsReference(command.Arguments, access.RootArgument) ||
                access.RootArgument.Argument.IsFlag ||
                !access.RootArgument.Argument.IsPath ||
                !RootMatchesArgument(command, access))
            {
                return false;
            }

            if ((access.Root is ShellValueDomain.Exact exact &&
                 string.IsNullOrEmpty(exact.Value)) ||
                (access.Root is ShellValueDomain.PathPattern pattern &&
                 (string.IsNullOrEmpty(pattern.Pattern) ||
                  string.IsNullOrEmpty(pattern.CoveringDirectory))))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateAccesses(
        CommandOccurrence command,
        PwshParserOptions options,
        out IReadOnlyList<ShellFileSystemTreeAccess> accesses)
    {
        accesses = Array.Empty<ShellFileSystemTreeAccess>();
        if (!IsGetChildItem(command))
        {
            return true;
        }

        var binding = Bind(command, options.Dialect);
        if (!binding.IsExact ||
            binding.Roots.Count != 1 ||
            binding.Traversal == ShellTreeTraversalMode.Unknown)
        {
            accesses = UnknownMarker;
            return true;
        }

        var rootArgument = binding.Roots[0];
        if (!TryCreateRoot(rootArgument, binding.LiteralPath, command, options, out var root))
        {
            accesses = UnknownMarker;
            return true;
        }

        accesses = new[]
        {
            new ShellFileSystemTreeAccess
            {
                RootArgument = rootArgument,
                Root = root,
                Traversal = binding.Traversal,
            },
        };
        return true;
    }

    private static bool IsGetChildItem(CommandOccurrence command)
    {
        if (command.Clause?.Verb is not { IsDynamic: false } verb ||
            verb.Tokens.Count != 1)
        {
            return false;
        }

        var identity = verb.CanonicalVerb ?? verb.Tokens[0];
        return string.Equals(identity, "Get-ChildItem", StringComparison.OrdinalIgnoreCase);
    }

    private static BindingResult Bind(CommandOccurrence command, PwshDialect dialect)
    {
        if (!command.IsComplete ||
            dialect is not (PwshDialect.PowerShell7 or PwshDialect.WindowsPowerShell51))
        {
            return BindingResult.Unknown;
        }

        var roots = new List<AnalyzedArgument>();
        var literalPath = false;
        var sawNamedRoot = false;
        var recurse = SwitchState.False;
        var follow = SwitchState.False;
        int? depth = null;
        var traversalUnknown = false;
        var sawRecurse = false;
        var sawFollow = false;
        var sawDepth = false;

        for (var index = 0; index < command.Arguments.Count; index++)
        {
            var current = command.Arguments[index];
            if (!current.Argument.IsFlag)
            {
                roots.Add(current);
                continue;
            }

            var name = current.Argument.Raw;
            if (ValueParameters.Contains(name))
            {
                if ((dialect == PwshDialect.WindowsPowerShell51 &&
                     string.Equals(name, "-ProgressAction", StringComparison.OrdinalIgnoreCase)) ||
                    index + 1 >= command.Arguments.Count ||
                    command.Arguments[index + 1].Argument.IsFlag)
                {
                    return BindingResult.Unknown;
                }

                var value = command.Arguments[++index];
                if (string.Equals(name, "-Path", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "-LiteralPath", StringComparison.OrdinalIgnoreCase))
                {
                    if (sawNamedRoot || roots.Count != 0)
                    {
                        return BindingResult.Unknown;
                    }

                    roots.Add(value);
                    sawNamedRoot = true;
                    literalPath = string.Equals(
                        name,
                        "-LiteralPath",
                        StringComparison.OrdinalIgnoreCase);
                }
                else if (string.Equals(name, "-Depth", StringComparison.OrdinalIgnoreCase))
                {
                    if (sawDepth)
                    {
                        return BindingResult.Unknown;
                    }

                    sawDepth = true;
                    if (!int.TryParse(
                            value.Argument.Raw,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var parsedDepth))
                    {
                        traversalUnknown = true;
                    }
                    else
                    {
                        depth = parsedDepth;
                    }
                }

                continue;
            }

            if (!SwitchParameters.Contains(name) ||
                (dialect == PwshDialect.WindowsPowerShell51 &&
                 string.Equals(name, "-FollowSymlink", StringComparison.OrdinalIgnoreCase)))
            {
                return BindingResult.Unknown;
            }

            var state = ReadSwitchState(command.Arguments, ref index);
            if (state == SwitchState.Unknown)
            {
                traversalUnknown = true;
            }

            if (string.Equals(name, "-Recurse", StringComparison.OrdinalIgnoreCase))
            {
                if (sawRecurse)
                {
                    return BindingResult.Unknown;
                }

                sawRecurse = true;
                recurse = state;
            }
            else if (string.Equals(name, "-FollowSymlink", StringComparison.OrdinalIgnoreCase))
            {
                if (sawFollow)
                {
                    return BindingResult.Unknown;
                }

                sawFollow = true;
                follow = state;
            }
        }

        if (roots.Count == 0)
        {
            if (command.WorkingDirectory is not ShellValueDomain.Exact)
            {
                return BindingResult.Unknown;
            }

            roots.Add(null!);
        }

        if (roots.Count != 1)
        {
            return BindingResult.Unknown;
        }

        if (traversalUnknown)
        {
            return new BindingResult(
                roots,
                literalPath,
                ShellTreeTraversalMode.Unknown,
                IsExact: true);
        }

        var recursive = recurse == SwitchState.True || depth > 0;
        var traversal = !recursive
            ? ShellTreeTraversalMode.DirectChildren
            : dialect == PwshDialect.WindowsPowerShell51 || follow == SwitchState.True
                ? ShellTreeTraversalMode.RecursiveMayFollowLinks
                : ShellTreeTraversalMode.RecursiveWithoutFollowingLinks;
        return new BindingResult(roots, literalPath, traversal, IsExact: true);
    }

    private static SwitchState ReadSwitchState(
        IReadOnlyList<AnalyzedArgument> arguments,
        ref int index)
    {
        if (index + 1 >= arguments.Count ||
            !ReferenceEquals(arguments[index].Element, arguments[index + 1].Element))
        {
            return SwitchState.True;
        }

        var parameter = arguments[index];
        index++;
        var combined = parameter.Element.Raw;
        var colon = combined.IndexOf(':');
        if (colon <= 0 ||
            !string.Equals(
                combined.Substring(0, colon),
                parameter.Argument.Raw,
                StringComparison.OrdinalIgnoreCase))
        {
            return SwitchState.Unknown;
        }

        var authored = combined.Substring(colon + 1);
        if (string.Equals(authored, "$true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authored, "${true}", StringComparison.OrdinalIgnoreCase))
        {
            return SwitchState.True;
        }

        if (string.Equals(authored, "$false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authored, "${false}", StringComparison.OrdinalIgnoreCase))
        {
            return SwitchState.False;
        }

        return SwitchState.Unknown;
    }

    private static bool TryCreateRoot(
        AnalyzedArgument? argument,
        bool literalPath,
        CommandOccurrence command,
        PwshParserOptions options,
        out ShellValueDomain root)
    {
        if (argument is null)
        {
            root = command.WorkingDirectory;
            return root is ShellValueDomain.Exact;
        }

        if (argument.Argument.IsPath && argument.Argument.Resolved is not null &&
            argument.Argument.Kind is ArgKind.Literal or ArgKind.Tilde)
        {
            root = new ShellValueDomain.Exact(argument.Argument.Resolved);
            return true;
        }

        var decoded = GetDecodedArgumentValue(command, argument);
        if (!literalPath && decoded is not null && argument.Argument.IsPath &&
            argument.Argument.Kind == ArgKind.Glob &&
            TryCreateLeafPattern(decoded, command, options, out var pattern))
        {
            root = pattern;
            return true;
        }

        root = new ShellValueDomain.Unknown();
        return false;
    }

    private static bool TryCreateLeafPattern(
        string value,
        CommandOccurrence command,
        PwshParserOptions options,
        out ShellValueDomain.PathPattern pattern)
    {
        pattern = null!;
        var globIndex = value.IndexOfAny(new[] { '*', '?', '[' });
        if (globIndex < 0 || value.IndexOfAny(new[] { '/', '\\' }, globIndex) >= 0)
        {
            return false;
        }

        var separator = value.LastIndexOfAny(new[] { '/', '\\' }, globIndex);
        if (separator == 0 ||
            IsDriveRelativeLeafPattern(value) ||
            IsIncompleteUncLeafPattern(value, separator))
        {
            return false;
        }

        var coveringSource = separator < 0 ? "." : value.Substring(0, separator);
        if (separator == 2 && value.Length > 1 && value[1] == ':')
        {
            coveringSource = value.Substring(0, 3);
        }

        if (command.WorkingDirectory is not ShellValueDomain.Exact cwd)
        {
            return false;
        }

        var resolution = PwshResolver.Resolve(
            ShellValue.Literal(coveringSource),
            treatAsPath: true,
            options with { WorkingDirectory = cwd.Value },
            workingDirectoryUnknown: false,
            ShellResolutionConsumer.PowerShellCmdletLiteralPath);
        if (resolution.Kind is not (ArgKind.Literal or ArgKind.Tilde) ||
            !resolution.IsPath || resolution.Resolved is null)
        {
            return false;
        }

        pattern = new ShellValueDomain.PathPattern(value, resolution.Resolved);
        return true;
    }

    private static bool IsIncompleteUncLeafPattern(string value, int separator)
    {
        if (value.Length < 2 ||
            value[0] is not ('/' or '\\') ||
            value[1] is not ('/' or '\\'))
        {
            return false;
        }

        var shareSeparator = value.IndexOfAny(new[] { '/', '\\' }, 2);
        return shareSeparator <= 2 || separator <= shareSeparator ||
            value.AsSpan(2, shareSeparator - 2) is "?" or ".";
    }

    private static bool IsDriveRelativeLeafPattern(string value)
    {
        return value.Length >= 3 &&
            ((value[0] >= 'A' && value[0] <= 'Z') ||
             (value[0] >= 'a' && value[0] <= 'z')) &&
            value[1] == ':' &&
            value[2] is not ('/' or '\\');
    }

    private static bool ContainsReference(
        IReadOnlyList<AnalyzedArgument> arguments,
        AnalyzedArgument candidate)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (ReferenceEquals(arguments[index], candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RootMatchesArgument(
        CommandOccurrence command,
        ShellFileSystemTreeAccess access)
    {
        var argument = access.RootArgument!;
        if (access.Root is ShellValueDomain.Unknown)
        {
            return access.Traversal == ShellTreeTraversalMode.Unknown;
        }

        if (access.Root is ShellValueDomain.Exact exact)
        {
            if (argument.Argument.Resolved is not null &&
                string.Equals(
                    exact.Value,
                    argument.Argument.Resolved,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return argument.AuthoredFileSystemValue is ShellValueDomain.Exact authored &&
                string.Equals(exact.Value, authored.Value, StringComparison.OrdinalIgnoreCase);
        }

        if (access.Root is not ShellValueDomain.PathPattern pattern ||
            argument.Argument.Kind != ArgKind.Glob ||
            GetDecodedArgumentValue(command, argument) is not { } decoded ||
            !string.Equals(pattern.Pattern, decoded, StringComparison.Ordinal) ||
            command.WorkingDirectory is not ShellValueDomain.Exact cwd ||
            !TryCreateLeafPattern(
                decoded,
                command,
                new PwshParserOptions { WorkingDirectory = cwd.Value },
                out var expected))
        {
            return false;
        }

        return string.Equals(
            pattern.CoveringDirectory,
            expected.CoveringDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetDecodedArgumentValue(
        CommandOccurrence command,
        AnalyzedArgument argument)
    {
        AnalyzedArgument? sharedFlag = null;
        var sharedCount = 0;
        for (var index = 0; index < command.Arguments.Count; index++)
        {
            var candidate = command.Arguments[index];
            if (!ReferenceEquals(candidate.Element, argument.Element))
            {
                continue;
            }

            sharedCount++;
            if (candidate.Argument.IsFlag)
            {
                sharedFlag = candidate;
            }
        }

        if (sharedCount == 1)
        {
            return argument.Element.Value;
        }

        if (sharedCount != 2 || sharedFlag is null)
        {
            return null;
        }

        var prefix = sharedFlag.Argument.Raw;
        var combined = argument.Element.Value;
        if (combined.Length <= prefix.Length + 1 ||
            !combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            combined[prefix.Length] is not (':' or '='))
        {
            return null;
        }

        return combined.Substring(prefix.Length + 1);
    }

    private static IReadOnlyList<ShellFileSystemTreeAccess> UnknownMarker { get; } =
        new[]
        {
            new ShellFileSystemTreeAccess
            {
                Root = new ShellValueDomain.Unknown(),
                Traversal = ShellTreeTraversalMode.Unknown,
            },
        };

    private enum SwitchState
    {
        Unknown,
        False,
        True,
    }

    private sealed record BindingResult(
        IReadOnlyList<AnalyzedArgument?> Roots,
        bool LiteralPath,
        ShellTreeTraversalMode Traversal,
        bool IsExact)
    {
        internal static BindingResult Unknown { get; } = new(
            Array.Empty<AnalyzedArgument?>(),
            false,
            ShellTreeTraversalMode.Unknown,
            false);
    }
}
