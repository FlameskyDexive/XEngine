// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text.Json;

using XEngine.Async;

namespace XEngine.Ads;

/// <summary>
/// Loads the <c>Settings/Ads.json</c> payload the build writes into the packaged content root
/// (resfile on HarmonyOS) and initializes <see cref="AdsService"/> with the configured
/// vendor provider. Editor/desktop games typically construct an <see cref="AdsSettings"/> and
/// a provider directly; the bootstrap path is for players built through the ads package's
/// Stage-project contribution.
/// </summary>
public static class AdsBootstrapper
{
    public const string PayloadFileName = "Ads.json";

    /// <summary>Loads the payload next to the player's exported Settings directory.</summary>
    public static bool TryLoadFromPlayerSettings(out AdsSettings settings) =>
        TryLoad(Path.Combine(XEngine.Runtime.Application.DataPath, "Settings"), out settings);

    /// <summary>Parses the payload from an explicit settings directory; never throws.</summary>
    public static bool TryLoad(string settingsDirectory, out AdsSettings settings)
    {
        settings = new AdsSettings();
        try
        {
            string path = Path.Combine(settingsDirectory, PayloadFileName);
            if (!File.Exists(path))
                return false;
            return TryParse(File.ReadAllText(path), ref settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Convenience flow: load the payload and configure + initialize the matching provider
    /// through <paramref name="providerFactory"/>. Returns false (and leaves
    /// <see cref="AdsService"/> untouched) when no payload exists.
    /// </summary>
    public static XTask InitializeFromPlayerSettings(
        Func<string, IAdProvider?> providerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        if (!TryLoadFromPlayerSettings(out AdsSettings settings))
            return XTask.FromCanceled(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(true));
        IAdProvider? provider = providerFactory(settings.ProviderId);
        if (provider == null)
            throw new InvalidOperationException($"no ads provider registered for '{settings.ProviderId}'");
        AdsService.Configure(provider);
        return AdsService.Initialize(settings, cancellationToken);
    }

    internal static bool TryParse(string json, ref AdsSettings settings)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string providerId = root.TryGetProperty("providerId", out JsonElement providerElement) &&
                                providerElement.ValueKind == JsonValueKind.String
                ? providerElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrEmpty(providerId))
                return false;

            settings.ProviderId = providerId;
            if (root.TryGetProperty("useTestAds", out JsonElement testElement) &&
                testElement.ValueKind == JsonValueKind.False)
            {
                settings.UseTestAds = false;
            }

            AdsProviderConfig config = settings.GetOrCreateProvider(providerId);
            if (root.TryGetProperty("appId", out JsonElement appElement) && appElement.ValueKind == JsonValueKind.String)
                config.AppId = appElement.GetString() ?? string.Empty;
            if (root.TryGetProperty("rewardVideoSlotId", out JsonElement rewardElement) && rewardElement.ValueKind == JsonValueKind.String)
                config.RewardVideoSlotId = rewardElement.GetString() ?? string.Empty;
            if (root.TryGetProperty("interstitialSlotId", out JsonElement interstitialElement) && interstitialElement.ValueKind == JsonValueKind.String)
                config.InterstitialSlotId = interstitialElement.GetString() ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
