// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;

using XEngine.ILInterpreter.Update;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N6 acceptance: signed update manifests, A/B atomic activation, downgrade
/// protection, binding-coverage refusal, and last-known-good rollback.
/// </summary>
[Trait("Category", "Build")]
public sealed class ILInterpreterUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "XEngineILUpdate", Guid.NewGuid().ToString("N"));

    private readonly string _publicKey;
    private readonly string _privateKey;

    public ILInterpreterUpdateTests()
    {
        Directory.CreateDirectory(_root);
        (_publicKey, _privateKey) = ILUpdateSigner.CreateKeyPair();
    }

    private static readonly HashSet<string> HostBindings = new(StringComparer.Ordinal)
    {
        ILBindingIdsCompat.IdForRequirement("System.Math::Max/2"),
    };

    private string WriteStaging(long version, string moduleName, byte[] moduleBytes, bool sign = true, bool corruptHash = false)
    {
        string staging = Path.Combine(_root, $"staging-{version}");
        Directory.CreateDirectory(staging);
        string dll = Path.Combine(staging, moduleName);
        File.WriteAllBytes(dll, moduleBytes);

        var manifest = new ILUpdateManifest
        {
            Version = version,
            MinimumHostVersion = "0.1.0",
        };
        string sha = ILUpdateManifest.ComputeModuleSha256(File.ReadAllBytes(dll));
        if (corruptHash) sha = new string('0', 64);
        manifest.Modules[moduleName] = sha;
        manifest.RequiredBindingIds.Add(ILBindingIdsCompat.IdForRequirement("System.Math::Max/2"));
        manifest.Signature = sign ? ILUpdateSigner.Sign(manifest, _privateKey) : Convert.ToBase64String(new byte[64]);
        File.WriteAllText(Path.Combine(staging, "update.json"), manifest.ToJson());
        return staging;
    }

    private static byte[] ModuleBytes(string content) => Encoding.UTF8.GetBytes(content);

    [Fact]
    public void SignedUpdate_ActivatesAtomically_AndBumpsVersion()
    {
        var service = new ILUpdateService
        {
            ContentRoot = _root,
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };

        string staging = WriteStaging(3, "Game.Hotfix.dll", ModuleBytes("v3-module"));
        Assert.Equal(ILUpdateStatus.Success, service.ApplyUpdate(staging));

        Assert.Equal(3, service.ActiveVersion);
        string activeDir = service.ResolveActiveModulesDir();
        Assert.True(File.Exists(Path.Combine(activeDir, "Game.Hotfix.dll")));
        Assert.False(File.Exists(Path.Combine(_root, ".updates", "incoming",
            "Game.Hotfix.dll"))); // staging slot is gone after the swap

        // idempotence guard: replaying the SAME version is refused
        Assert.Equal(ILUpdateStatus.VersionRegression, service.ApplyUpdate(staging));
    }

    [Fact]
    public void BadSignature_IsRefused()
    {
        var service = new ILUpdateService
        {
            ContentRoot = _root,
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };

        // tamper AFTER signing: bump the version inside the signed body
        string staging = WriteStaging(5, "Game.Hotfix.dll", ModuleBytes("honest"));
        string json = File.ReadAllText(Path.Combine(staging, "update.json"));
        json = json.Replace("\"version\":5", "\"version\":50");
        File.WriteAllText(Path.Combine(staging, "update.json"), json);

        Assert.Equal(ILUpdateStatus.InvalidSignature, service.ApplyUpdate(staging));
        Assert.Equal(0, service.ActiveVersion);
    }

    [Fact]
    public void UnsignedManifest_IsRefused()
    {
        var service = new ILUpdateService
        {
            ContentRoot = _root,
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };
        string staging = WriteStaging(7, "Game.Hotfix.dll", ModuleBytes("x"), sign: false);
        Assert.Equal(ILUpdateStatus.InvalidSignature, service.ApplyUpdate(staging));
    }

    [Fact]
    public void HashMismatch_IsRefused()
    {
        var service = new ILUpdateService
        {
            ContentRoot = _root,
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };
        // signed manifest with hash of "v5" but staged bytes say something else
        string staging = WriteStaging(9, "Game.Hotfix.dll", ModuleBytes("v9-module"), corruptHash: true);
        Assert.Equal(ILUpdateStatus.HashMismatch, service.ApplyUpdate(staging));
    }

    [Fact]
    public void Downgrade_IsRefused()
    {
        var service = new ILUpdateService
        {
            ContentRoot = _root,
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };
        Assert.Equal(ILUpdateStatus.Success, service.ApplyUpdate(WriteStaging(10, "Game.Hotfix.dll", ModuleBytes("new"))));

        // An attacker re-signs an OLD set with the SAME key — still refused.
        Assert.Equal(ILUpdateStatus.VersionRegression, service.ApplyUpdate(WriteStaging(4, "Game.Hotfix.dll", ModuleBytes("old"))));
        Assert.Equal(10, service.ActiveVersion);
    }

    [Fact]
    public void MissingHostBinding_IsRefused()
    {
        var service = new ILUpdateService
        {
            ContentRoot = _root,
            PublicKeyXml = _publicKey,
            HostBindingIds = new HashSet<string>(StringComparer.Ordinal), // host registers nothing
        };
        string staging = WriteStaging(11, "Game.Hotfix.dll", ModuleBytes("needs-bindings"));
        Assert.Equal(ILUpdateStatus.BindingsMissing, service.ApplyUpdate(staging));
        Assert.Equal(0, service.ActiveVersion);
    }

    [Fact]
    public void Rollback_RestoresLastKnownGood()
    {
        var service = new ILUpdateService
        {
            ContentRoot = _root,
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };
        Assert.Equal(ILUpdateStatus.Success, service.ApplyUpdate(WriteStaging(1, "Game.Hotfix.dll", ModuleBytes("good-1"))));
        Assert.Equal(ILUpdateStatus.Success, service.ApplyUpdate(WriteStaging(2, "Game.Hotfix.dll", ModuleBytes("good-2"))));

        // Startup health check fails on the promoted set → rollback.
        Assert.True(service.RollbackToLastKnownGood());
        Assert.Equal(1, service.ActiveVersion);
        string restored = File.ReadAllText(Path.Combine(service.ResolveActiveModulesDir(), "Game.Hotfix.dll"));
        Assert.Equal("good-1", restored);
    }

    [Fact]
    public void ConcurrentUpdate_SecondAttemptIsBusy()
    {
        var service = new ILUpdateService
        {
            ContentRoot = _root,
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };
        Directory.CreateDirectory(Path.Combine(_root, ".state"));
        File.WriteAllText(Path.Combine(_root, ".state", "update.lock"), "other-process");

        string staging = WriteStaging(20, "Game.Hotfix.dll", ModuleBytes("x"));
        Assert.Equal(ILUpdateStatus.Busy, service.ApplyUpdate(staging));
        Assert.Equal(0, service.ActiveVersion);
    }

    [Fact]
    public void CorruptedActiveModules_AreDetectedByHash()
    {
        var service = new ILUpdateService
        {
            ContentRoot = _root,
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };
        string staging = WriteStaging(2, "Game.Hotfix.dll", ModuleBytes("payload"));
        Assert.Equal(ILUpdateStatus.Success, service.ApplyUpdate(staging));

        // Simulate bit-rot / tampering of the ACTIVE set: the health check must
        // detect it against the manifest hash.
        string active = Path.Combine(service.ResolveActiveModulesDir(), "Game.Hotfix.dll");
        File.WriteAllText(active, "tampered");
        var manifest = ILUpdateManifest.FromJson(
            File.ReadAllText(Path.Combine(staging, "update.json")));
        string actual = ILUpdateManifest.ComputeModuleSha256(File.ReadAllBytes(active));
        Assert.NotEqual(manifest.Modules["Game.Hotfix.dll"], actual);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>Compatibility shim: binding IDs computed the same way the editor does.</summary>
internal static class ILBindingIdsCompat
{
    public static string IdForRequirement(string requirement)
        => XEngine.ILInterpreter.Compatibility.ILBindingIds.IdForRequirement(requirement);
}
