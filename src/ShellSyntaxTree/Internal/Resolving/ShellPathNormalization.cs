// -----------------------------------------------------------------------
// <copyright file="ShellPathNormalization.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree.Internal.Resolving;

/// <summary>
/// String-only path operations whose behavior is identical for the Bash and
/// PowerShell resolvers. Root detection, drive handling, and segment
/// normalization remain shell-specific.
/// </summary>
internal static class ShellPathNormalization
{
    internal static string Join(string baseDirectory, string subpath)
    {
        if (string.IsNullOrEmpty(subpath))
        {
            return baseDirectory;
        }

        var relative = subpath;
        if (relative.Length > 0 && (relative[0] == '/' || relative[0] == '\\'))
        {
            relative = relative.Substring(1);
        }

        return baseDirectory.TrimEnd('/', '\\') + "/" + relative.Replace('\\', '/');
    }

    internal static string NormalizeSeparators(string path)
    {
        if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\')
        {
            return "//" + path.Substring(2).Replace('\\', '/');
        }

        return path.Replace('\\', '/');
    }
}
