// -----------------------------------------------------------------------
// <copyright file="PublicCollection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ShellSyntaxTree.Internal;

internal static class PublicCollection
{
    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (source.Count == 0)
        {
            return Array.Empty<T>();
        }

        var copy = new T[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            copy[index] = source[index];
        }

        return new ReadOnlyCollection<T>(copy);
    }

    internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        return Copy(source is IReadOnlyList<T> list
            ? list
            : new List<T>(source));
    }
}
