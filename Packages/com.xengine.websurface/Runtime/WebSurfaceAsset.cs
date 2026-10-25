using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XEngine.WebSurface;

/// <summary>
/// What the current platform + RHI + negotiated transport can actually do. Reported from the
/// active backend after probing, never inferred from a platform name (plan contract: report the
/// real transport, not the OS).
/// </summary>
public sealed record WebSurfaceCapabilities
{
    public required string Platform { get; init; }

    /// <summary>cpu-shared-buffer | gpu-shared-texture | web-image-copy | android-arraybuffer | arkweb-buffer.</summary>
    public required string Transport { get; init; }

    public bool SupportsFrames { get; init; }
    public bool SupportsPointerInput { get; init; }
    public bool SupportsKeyboardFocus { get; init; }
    public bool SupportsIme { get; init; }
    public int MaxTextureSize { get; init; }

    public static WebSurfaceCapabilities CpuBaseline(string platform, int maxTextureSize = 4096) => new()
    {
        Platform = platform,
        Transport = "cpu-shared-buffer",
        SupportsFrames = true,
        SupportsPointerInput = true,
        SupportsKeyboardFocus = true,
        // B6: engine-driven text sessions exist, but the focused-host path for the OS's native
        // IME into a surface (soft keyboard on touch platforms, IME reach-through on desktop)
        // is wired per-platform; a backend flips this only after that path works.
        SupportsIme = false,
        MaxTextureSize = maxTextureSize,
    };
}

/// <summary>
/// Per-surface quality settings from the asset. The adaptive ladder is
/// 1024²@30 → 512²@30 → 512²@15; B10 owns switching, B1 only carries the settings.
/// </summary>
public sealed record WebSurfaceQuality
{
    public int Width { get; init; } = 1024;
    public int Height { get; init; } = 1024;
    public int FramerateCapHz { get; init; } = 30;

    public static WebSurfaceQuality Default { get; } = new();
    public static WebSurfaceQuality Half { get; } = new() { Width = 512, Height = 512 };
    public static WebSurfaceQuality Lowest { get; } = new() { Width = 512, Height = 512, FramerateCapHz = 15 };
}

/// <summary>
/// A `.websurface` source asset: content entry, domain allow-list, quality and required
/// capabilities. Parsing/validation stays pure so the CLI can validate offline (mirrors .threefx).
/// </summary>
public sealed record WebSurfaceAsset
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Content entry: a local project-relative path or an HTTPS URL.</summary>
    public string ContentEntry { get; init; } = "";

    /// <summary>For HTTPS content: navigation outside these hosts fails the session. Local content needs none.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = Array.Empty<string>();

    public WebSurfaceQuality Quality { get; init; } = WebSurfaceQuality.Default;

    public bool TransparentBackground { get; init; }

    /// <summary>Capabilities the session refuses to run without.</summary>
    public bool RequiresIme { get; init; }

    public bool IsLocalContent => !ContentEntry.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex HostRegex =
        new("^https://([^/:?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string? Host
    {
        get
        {
            if (IsLocalContent)
                return null;
            Match match = HostRegex.Match(ContentEntry);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
