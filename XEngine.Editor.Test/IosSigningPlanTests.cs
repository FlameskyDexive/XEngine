// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using XEngine.Editor.Build.Ios;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class IosSigningPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineIosSigningTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DebugAutomatic_RequiresTeamId()
    {
        var profile = new IosBuildProfile { ApplicationId = "com.xengine.game" };

        IosSigningPlan plan = IosSigningPlanner.Create(profile, BuildConfiguration.Debug);

        Assert.False(plan.Ready);
        Assert.Equal(IosSigningStyle.Automatic, plan.Style);
        Assert.Equal(IosSigningPlanner.AutomaticIdentity, plan.CodesignIdentity);
        Assert.False(plan.StripLinkedSymbols);
        Assert.Contains("Team ID", plan.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugAutomatic_RecordsTeamAndAppleDevelopment()
    {
        var profile = new IosBuildProfile
        {
            ApplicationId = "com.xengine.game",
            TeamId = "ABCDE12345",
        };

        IosSigningPlan plan = IosSigningPlanner.Create(profile, BuildConfiguration.Debug);

        Assert.True(plan.Ready);
        Assert.Equal(IosSigningStyle.Automatic, plan.Style);
        Assert.Equal("ABCDE12345", plan.TeamId);
        Assert.Equal("com.xengine.game", plan.BundleId);
        Assert.Equal("Apple Development", plan.CodesignIdentity);
        Assert.Null(plan.ProvisioningProfileUuid);
        Assert.False(plan.StripLinkedSymbols);
        Assert.True(plan.ArchiveDsym);
    }

    [Fact]
    public void ReleaseManual_RequiresProvisioningProfile()
    {
        var profile = new IosBuildProfile
        {
            ApplicationId = "com.xengine.game",
            TeamId = "ABCDE12345",
            SigningCertificate = "Apple Distribution: XEngine (ABCDE12345)",
        };

        IosSigningPlan plan = IosSigningPlanner.Create(profile, BuildConfiguration.Release);

        Assert.False(plan.Ready);
        Assert.Equal(IosSigningStyle.Manual, plan.Style);
        Assert.Equal(IosSigningPlanner.MissingProfilePathError, plan.Error);
        Assert.Null(plan.ProvisioningProfileUuid);
    }

    [Fact]
    public void ReleaseManual_RecordsUuidAndExpiryFromXmlPlist()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "profile.plist");
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddYears(1);
        File.WriteAllText(
            path,
            IosProvisioningProfileReader.CreateXmlFixture(
                "11111111-2222-3333-4444-555555555555",
                "ABCDE12345",
                "XEngine Dev",
                expiry));
        var profile = new IosBuildProfile
        {
            ApplicationId = "com.xengine.game",
            TeamId = "ABCDE12345",
            SigningCertificate = "Apple Distribution: XEngine (ABCDE12345)",
            ProvisioningProfilePath = path,
        };

        IosSigningPlan plan = IosSigningPlanner.Create(profile, BuildConfiguration.Release);

        Assert.True(plan.Ready);
        Assert.Equal(IosSigningStyle.Manual, plan.Style);
        Assert.Equal("11111111-2222-3333-4444-555555555555", plan.ProvisioningProfileUuid);
        Assert.Equal("XEngine Dev", plan.ProvisioningProfileName);
        Assert.True(plan.StripLinkedSymbols);
        Assert.NotNull(plan.ProvisioningExpiration);
        Assert.Equal(expiry.UtcDateTime, plan.ProvisioningExpiration!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ReleaseManual_RefusesCmsMobileprovisionOnWindows()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "profile.mobileprovision");
        File.WriteAllBytes(path, [0x30, 0x82, 0x01, 0x00, 0x06, 0x09]);
        var profile = new IosBuildProfile
        {
            ApplicationId = "com.xengine.game",
            TeamId = "ABCDE12345",
            SigningCertificate = "Apple Distribution: XEngine (ABCDE12345)",
            ProvisioningProfilePath = path,
        };

        IosSigningPlan plan = IosSigningPlanner.Create(profile, BuildConfiguration.Release);

        Assert.False(plan.Ready);
        Assert.Equal(IosProvisioningProfileReader.CmsUnsupportedError, plan.Error);
        Assert.Null(plan.ProvisioningProfileUuid);
    }

    [Fact]
    public void WriteVerification_DoesNotInventProfileUuid()
    {
        Directory.CreateDirectory(_root);
        var plan = IosSigningPlanner.Create(
            new IosBuildProfile { ApplicationId = "com.xengine.game", TeamId = "ABCDE12345" },
            BuildConfiguration.Release);

        IosSigningPlanner.WriteVerification(_root, plan);

        using JsonDocument json = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_root, "verification", IosSigningPlanner.VerificationFileName)));
        Assert.False(json.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("provisioningProfileUuid").ValueKind);
        Assert.Equal("Manual", json.RootElement.GetProperty("style").GetString());
        Assert.Contains("ProvisioningProfilePath", json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
