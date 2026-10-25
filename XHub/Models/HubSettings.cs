using System;

namespace XHub.Models;

public enum HubLanguage
{
    /// <summary>Follow the OS UI culture at startup.</summary>
    System = 0,
    SimplifiedChinese = 1,
    English = 2,
}

/// <summary>
/// User-facing settings persisted in <c>settings.json</c> (planning doc §4.1). Only fields that
/// are already functional ship here; later nodes append their own keys without a migration.
/// </summary>
public sealed class HubSettings
{
    public HubLanguage Language { get; set; } = HubLanguage.System;
    public bool VerboseLog { get; set; }

    /// <summary>
    /// Root for hub-managed editor installs; empty = the default under the data root. Applied at
    /// startup — changes take effect on the next launch and never move existing installs.
    /// </summary>
    public string EditorsRootOverride { get; set; } = "";

    /// <summary>Download cache root; empty = the default under the data root. Same restart semantics.</summary>
    public string DownloadsRootOverride { get; set; } = "";

    /// <summary>Resolved language for display purposes (never "System" once applied).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveLanguageCode => Language switch
    {
        HubLanguage.SimplifiedChinese => "zh-CN",
        HubLanguage.English => "en-US",
        _ => SystemLanguageCode,
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public static string SystemLanguageCode
        => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
            ? "zh-CN"
            : "en-US";
}
