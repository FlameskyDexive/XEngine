// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.PackageRegistry;

/// <summary>
/// Maps between XEngine's internal reverse-domain package names (<c>com.xengine.spine</c>) and the
/// scoped npm wire names (<c>@inxep/spine</c>) used on the public registry. Everything inside the
/// engine — package.json <c>name</c>, manifest dependencies, lock entries, PackageMount logical
/// roots — keeps the reverse-domain form; only the registry protocol layer translates. Names that
/// are not <c>com.xengine.*</c> pass through unchanged so custom scoped registries can serve
/// third-party packages under their own naming.
/// </summary>
public static class NpmPackageName
{
    /// <summary>npm scope the official XEngine packages are published under.</summary>
    public const string OfficialScope = "inxep";

    /// <summary>Reverse-domain prefix identifying official XEngine package names.</summary>
    public const string InternalPrefix = "com.xengine.";

    /// <summary>True when <paramref name="name"/> is an official XEngine reverse-domain name.</summary>
    public static bool IsInternalName(string name)
        => name.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase)
           && name.Length > InternalPrefix.Length;

    /// <summary>
    /// <c>com.xengine.spine</c> → <c>@inxep/spine</c>. Non-XEngine names (including already
    /// scoped npm names) return unchanged.
    /// </summary>
    public static string ToNpmName(string name)
    {
        if (!IsInternalName(name)) return name;
        return "@" + OfficialScope + "/" + name[InternalPrefix.Length..];
    }

    /// <summary>
    /// <c>@inxep/spine</c> → <c>com.xengine.spine</c>. Non-<c>@inxep</c> names return
    /// unchanged; a malformed scoped name (empty or single-segment scope) also returns unchanged
    /// so callers can decide how to treat it.
    /// </summary>
    public static string FromNpmName(string name)
    {
        if (!name.StartsWith('@')) return name;
        int slash = name.IndexOf('/');
        if (slash <= 1 || slash == name.Length - 1) return name;
        string scope = name[1..slash];
        if (!string.Equals(scope, OfficialScope, StringComparison.OrdinalIgnoreCase)) return name;
        return InternalPrefix + name[(slash + 1)..];
    }

    /// <summary>
    /// URL path segment for a registry request: npm expects scoped names with the slash encoded
    /// (<c>@inxep%2fspine</c>); unscoped names are used verbatim.
    /// </summary>
    public static string ToUrlSegment(string npmName)
    {
        int slash = npmName.IndexOf('/');
        if (npmName.Length > 0 && npmName[0] == '@' && slash > 1)
            return npmName[..slash] + "%2f" + npmName[(slash + 1)..];
        return npmName;
    }

    /// <summary>
    /// Tarball filename npm derives from a package name + version: scope characters removed,
    /// <c>@inxep/spine</c> + <c>0.2.0</c> → <c>inxep-spine-0.2.0.tgz</c>.
    /// </summary>
    public static string TarballFileName(string npmName, string version)
    {
        string flat = npmName.Replace("@", string.Empty).Replace('/', '-');
        return flat + "-" + version + ".tgz";
    }
}
