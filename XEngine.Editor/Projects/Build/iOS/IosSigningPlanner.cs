// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text.Json;

using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

namespace XEngine.Editor.Build.Ios;

internal sealed class IosSigningPlan
{
    public bool Ready { get; init; }
    public string Error { get; init; } = string.Empty;
    public IosSigningStyle Style { get; init; }
    public string TeamId { get; init; } = string.Empty;
    public string BundleId { get; init; } = string.Empty;
    public string CodesignIdentity { get; init; } = string.Empty;
    public string? ProvisioningProfileUuid { get; init; }
    public DateTimeOffset? ProvisioningExpiration { get; init; }
    public string? ProvisioningProfileName { get; init; }
    public bool StripLinkedSymbols { get; init; }
    public bool ArchiveDsym { get; init; } = true;
}

/// <summary>
/// Resolves Debug Automatic vs Release Manual signing and writes
/// <c>verification/ios-signing.json</c>. Does not invent a profile UUID.
/// </summary>
internal static class IosSigningPlanner
{
    internal const string VerificationFileName = "ios-signing.json";
    internal const string AutomaticIdentity = "Apple Development";

    internal const string MissingBundleIdError =
        "iOS signing requires a reverse-DNS Bundle ID on IosBuildProfile.";

    internal const string MissingTeamIdAutomaticError =
        "Automatic signing requires Team ID on IosBuildProfile.";

    internal const string MissingTeamIdManualError =
        "Manual signing requires Team ID on IosBuildProfile.";

    internal const string MissingCertificateError =
        "Release signing requires SigningCertificate on IosBuildProfile.";

    internal const string MissingProfilePathError =
        "Release signing requires ProvisioningProfilePath on IosBuildProfile.";

    /// <summary>
    /// Simulator builds run unsigned — no identity, team, or provisioning profile. dSYM
    /// archiving is skipped too (no App Store symbolication path).
    /// </summary>
    public static IosSigningPlan CreateSimulator()
        => new()
        {
            Ready = true,
            Style = IosSigningStyle.Automatic,
            CodesignIdentity = string.Empty,
            StripLinkedSymbols = false,
            ArchiveDsym = false,
        };

    public static IosSigningPlan Create(IosBuildProfile profile, BuildConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string bundleId = (profile.ApplicationId ?? string.Empty).Trim();
        string teamId = (profile.TeamId ?? string.Empty).Trim();
        string identity = string.IsNullOrWhiteSpace(profile.SigningCertificate)
            ? AutomaticIdentity
            : profile.SigningCertificate.Trim();

        IosSigningStyle style = configuration == BuildConfiguration.Release
            ? IosSigningStyle.Manual
            : (profile.SigningStyle == IosSigningStyle.Manual
                ? IosSigningStyle.Manual
                : IosSigningStyle.Automatic);

        if (string.IsNullOrWhiteSpace(bundleId) || !IosPlayerProjectGenerator.IsValidApplicationId(bundleId))
            return Incomplete(style, teamId, bundleId, identity, MissingBundleIdError);

        if (style == IosSigningStyle.Automatic)
        {
            if (string.IsNullOrWhiteSpace(teamId))
                return Incomplete(style, teamId, bundleId, AutomaticIdentity, MissingTeamIdAutomaticError);

            return new IosSigningPlan
            {
                Ready = true,
                Style = IosSigningStyle.Automatic,
                TeamId = teamId,
                BundleId = bundleId,
                CodesignIdentity = AutomaticIdentity,
                StripLinkedSymbols = false,
                ArchiveDsym = true,
            };
        }

        if (string.IsNullOrWhiteSpace(teamId))
            return Incomplete(style, teamId, bundleId, identity, MissingTeamIdManualError);
        if (string.IsNullOrWhiteSpace(profile.SigningCertificate))
            return Incomplete(style, teamId, bundleId, identity, MissingCertificateError);
        if (string.IsNullOrWhiteSpace(profile.ProvisioningProfilePath))
            return Incomplete(style, teamId, bundleId, identity, MissingProfilePathError);

        try
        {
            IosProvisioningProfileInfo info = IosProvisioningProfileReader.Read(profile.ProvisioningProfilePath);
            if (!string.Equals(info.TeamId, teamId, StringComparison.Ordinal))
            {
                return Incomplete(
                    style,
                    teamId,
                    bundleId,
                    identity,
                    "Provisioning profile team '" + info.TeamId +
                    "' does not match IosBuildProfile.TeamId '" + teamId + "'.");
            }

            if (info.Expiration <= DateTimeOffset.UtcNow)
            {
                return Incomplete(
                    style,
                    teamId,
                    bundleId,
                    identity,
                    "Provisioning profile '" + info.Uuid + "' expired on " + info.Expiration.ToString("u") + ".");
            }

            return new IosSigningPlan
            {
                Ready = true,
                Style = IosSigningStyle.Manual,
                TeamId = teamId,
                BundleId = bundleId,
                CodesignIdentity = identity,
                ProvisioningProfileUuid = info.Uuid,
                ProvisioningExpiration = info.Expiration,
                ProvisioningProfileName = info.Name,
                StripLinkedSymbols = true,
                ArchiveDsym = true,
            };
        }
        catch (Exception ex)
        {
            return Incomplete(style, teamId, bundleId, identity, ex.Message);
        }
    }

    public static void WriteVerification(string exportRoot, IosSigningPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        ArgumentNullException.ThrowIfNull(plan);
        string directory = Path.Combine(exportRoot, "verification");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, VerificationFileName),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    ready = plan.Ready,
                    style = plan.Style.ToString(),
                    teamId = plan.TeamId,
                    bundleId = plan.BundleId,
                    codesignIdentity = plan.CodesignIdentity,
                    provisioningProfileUuid = plan.ProvisioningProfileUuid,
                    provisioningExpiration = plan.ProvisioningExpiration,
                    provisioningProfileName = plan.ProvisioningProfileName,
                    stripLinkedSymbols = plan.StripLinkedSymbols,
                    archiveDsym = plan.ArchiveDsym,
                    error = plan.Error,
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IosSigningPlan Incomplete(
        IosSigningStyle style,
        string teamId,
        string bundleId,
        string identity,
        string error) =>
        new()
        {
            Ready = false,
            Error = error,
            Style = style,
            TeamId = teamId,
            BundleId = bundleId,
            CodesignIdentity = identity,
            StripLinkedSymbols = style == IosSigningStyle.Manual,
            ArchiveDsym = true,
        };
}
