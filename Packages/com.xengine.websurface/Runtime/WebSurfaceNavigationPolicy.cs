using System;
using System.IO;

namespace XEngine.WebSurface;

/// <summary>The decision for one navigation attempt inside a hosted surface.</summary>
public readonly record struct WebSurfaceNavigationDecision(bool Allow, string? BlockedReason)
{
    public static WebSurfaceNavigationDecision Allowed() => new(true, null);
    public static WebSurfaceNavigationDecision Blocked(string reason) => new(false, reason);
}

/// <summary>
/// Platform-neutral navigation policy for WebSurface sessions: resolves the navigation target
/// for local content (virtual host mapped under a content root, with escape checks) and decides
/// whether an in-flight navigation may proceed (HTTPS-only remote content, host allow-list).
/// Pure so the CLI can validate an asset offline and the tests can pin the security boundary;
/// the platform backends only carry out the decisions.
/// </summary>
public static class WebSurfaceNavigationPolicy
{
    /// <summary>Virtual host name local content is mapped to (always on the navigation allow-list).</summary>
    public const string LocalVirtualHost = "websurface.local";

    /// <summary>
    /// Resolves the navigation target for the asset's content entry. Local entries are mapped to
    /// <c>https://{LocalVirtualHost}/&lt;relative&gt;</c> under <paramref name="contentRoot"/>;
    /// entries that escape the root or do not exist throw. HTTPS entries pass through unchanged.
    /// </summary>
    public static string ResolveTarget(WebSurfaceAsset asset, string contentRoot)
    {
        if (!asset.IsLocalContent)
            return asset.ContentEntry;

        string root = Path.GetFullPath(contentRoot);
        string entry = Path.GetFullPath(Path.Combine(root, asset.ContentEntry));
        if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"WebSurface content entry escapes the content root: {asset.ContentEntry}");
        if (!File.Exists(entry))
            throw new InvalidOperationException(
                $"WebSurface content entry does not exist: {asset.ContentEntry}");

        string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
        return $"https://{LocalVirtualHost}/{relative}";
    }

    /// <summary>
    /// Decides one navigation. Local virtual-host traffic and about: frames are allowed; remote
    /// content must be HTTPS on the asset's allow-list; everything else is blocked with a typed
    /// reason (the backend fails the session with it).
    /// </summary>
    public static WebSurfaceNavigationDecision DecideNavigation(WebSurfaceAsset asset, string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri))
            return WebSurfaceNavigationDecision.Blocked($"navigation_blocked: unparseable uri '{uriText}'.");

        if (uri.Host.Equals(LocalVirtualHost, StringComparison.OrdinalIgnoreCase))
            return WebSurfaceNavigationDecision.Allowed();
        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
            return WebSurfaceNavigationDecision.Allowed();

        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return WebSurfaceNavigationDecision.Blocked(
                $"navigation_blocked: insecure scheme '{uri.Scheme}' is not allowed ({uriText}).");

        foreach (string allowed in asset.AllowedHosts)
        {
            if (uri.Host.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                return WebSurfaceNavigationDecision.Allowed();
        }

        return WebSurfaceNavigationDecision.Blocked(
            $"navigation_blocked: host '{uri.Host}' is not in the surface allow-list ({uriText}).");
    }
}
