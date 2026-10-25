using XEngine.XTS;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N8 gate: signed content updates — verification of signature and file
/// hashes before activation, atomic promotion with one-generation rollback
/// copy, and startup recovery that falls back to previous/factory when the
/// current content is corrupt.
/// </summary>
public class XtsUpdateServiceTests : IDisposable
{
    private static readonly string SigningKey = Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes("xts-n8-test-signing-key"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "xts-n8-" + Guid.NewGuid().ToString("N")[..8]);

    private string MakeContent(string marker, string version)
    {
        var dir = Path.Combine(_root, "candidate-" + marker);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.js"), $"// content {marker}\nglobalThis.__version = '{marker}';\n");
        File.WriteAllText(Path.Combine(dir, "data.json"), $"{{\"v\":\"{marker}\"}}");
        var service = new XtsUpdateService(_root, SigningKey);
        service.WriteManifest(dir, version);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Activate_GoodCandidate_PromotesAndKeepsPrevious()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var service = new XtsUpdateService(_root, SigningKey);
        var factory = MakeContent("factory", "0.1.0");
        service.SeedFactory(factory, "0.1.0");

        var candidate = MakeContent("update1", "0.2.0");
        var errors = service.ActivateCandidate(candidate, "0.2.0");
        Assert.True(errors.Count == 0, string.Join("; ", errors));

        var (root, problems) = service.LoadCurrentWithRollback();
        Assert.Empty(problems);
        Assert.Contains("__version = 'update1'", File.ReadAllText(Path.Combine(root, "main.js")));

        // previous still holds factory content for rollback
        Assert.Contains("__version = 'factory'",
            File.ReadAllText(Path.Combine(service.PreviousRoot, "main.js")));
    }

    [Fact]
    public void Activate_TamperedFile_IsRefused()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var service = new XtsUpdateService(_root, SigningKey);
        service.SeedFactory(MakeContent("factory", "0.1.0"), "0.1.0");
        var candidate = MakeContent("evil", "0.2.0");

        // tamper AFTER the manifest was signed
        File.WriteAllText(Path.Combine(candidate, "main.js"),
            "// content evil\nglobalThis.__version = 'TAMPERED';\n");

        var errors = service.ActivateCandidate(candidate, "0.2.0");
        Assert.Contains(errors, e => e.Contains("hash mismatch") && e.Contains("main.js"));

        // current content is untouched
        Assert.Contains("__version = 'factory'",
            File.ReadAllText(Path.Combine(service.CurrentRoot, "main.js")));
    }

    [Fact]
    public void Activate_BadSignature_IsRefused()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var service = new XtsUpdateService(_root, SigningKey);
        service.SeedFactory(MakeContent("factory", "0.1.0"), "0.1.0");
        var candidate = MakeContent("update1", "0.2.0");

        // re-sign with the WRONG key: hashes match but the publisher is not trusted
        var attacker = new XtsUpdateService(_root, Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("attacker-key")));
        var manifest = XtsContentManifest.Parse(
            File.ReadAllText(Path.Combine(candidate, XtsContentManifest.ManifestFileName)));
        manifest.Sign(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("attacker-key")));
        File.WriteAllText(Path.Combine(candidate, XtsContentManifest.ManifestFileName),
            System.Text.Json.JsonSerializer.Serialize(manifest));

        var errors = service.ActivateCandidate(candidate, "0.2.0");
        Assert.Contains(errors, e => e.Contains("signature verification failed"));
    }

    [Fact]
    public void StartupRollback_CorruptCurrent_FallsBackToPrevious()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var service = new XtsUpdateService(_root, SigningKey);
        service.SeedFactory(MakeContent("factory", "0.1.0"), "0.1.0");
        var candidate = MakeContent("update1", "0.2.0");
        Assert.Empty(service.ActivateCandidate(candidate, "0.2.0"));

        // simulate a corrupted current content (deleted entry)
        File.Delete(Path.Combine(service.CurrentRoot, "main.js"));

        var (root, problems) = service.LoadCurrentWithRollback();
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("rolled back to previous"));
        Assert.Contains("__version = 'factory'",
            File.ReadAllText(Path.Combine(root, "main.js")));
    }

    [Fact]
    public void StartupRollback_NoIntactFallback_ReportsCleanly()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var service = new XtsUpdateService(_root, SigningKey);
        service.SeedFactory(MakeContent("factory", "0.1.0"), "0.1.0");
        service.ActivateCandidate(MakeContent("update1", "0.2.0"), "0.2.0");

        // corrupt current AND previous
        File.Delete(Path.Combine(service.CurrentRoot, "main.js"));
        File.WriteAllText(Path.Combine(service.PreviousRoot, "main.js"), "// corrupted");

        var (root, problems) = service.LoadCurrentWithRollback();
        // previous is corrupt, so the last resort is the immutable factory baseline
        Assert.Contains(problems, p => p.Contains("rolled back to factory"));
        Assert.Contains("__version = 'factory'",
            File.ReadAllText(Path.Combine(root, "main.js")));
        Assert.True(File.Exists(Path.Combine(root, XtsContentManifest.ManifestFileName)));
    }
}
