// -----------------------------------------------------------------------
// <copyright file="IsExternalInit.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
// Polyfill for `init` accessors on netstandard2.0. The C# compiler emits a
// reference to System.Runtime.CompilerServices.IsExternalInit for any
// init-only setter; that type ships in net5.0+ but is absent from
// netstandard2.0. Declaring it internally is the standard workaround and
// is bit-compatible with the framework type — the runtime never inspects
// it, only the compiler does at the call site.
#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices;

using System.ComponentModel;

[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
#endif
