// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Linq;

using XEngine.Runtime.AssetBundles;

using Xunit;

namespace XEngine.Runtime.Test.AssetBundles;

/// <summary>
/// Tests for the PackageManifest binary serialization and the dependency-resolution API
/// (<see cref="AssetBundleManifest"/>, the Unity-5-style wrapper).
/// </summary>
public class PackageManifestTests
{
    private static PackageManifest BuildDiamondManifest()
    {
        // Diamond: a depends on b,c ; b and c both depend on d.
        //   a
        //  / \
        // b   c
        //  \ /
        //   d
        var m = new PackageManifest
        {
            PackageName = "test",
            PackageVersion = "1.0.0",
            BuildTarget = AssetBundlePlatform.Any,
            EnableAddressable = true,
        };

        // Order matters: index 0=a, 1=b, 2=c, 3=d. Dependencies reference by index.
        m.BundleList.Add(MakeBundle("a.bundle", deps: new[] { 1, 2 }));
        m.BundleList.Add(MakeBundle("b.bundle", deps: new[] { 3 }));
        m.BundleList.Add(MakeBundle("c.bundle", deps: new[] { 3 }));
        m.BundleList.Add(MakeBundle("d.bundle", deps: Array.Empty<int>()));

        m.AssetList.Add(new ManifestAsset { Address = "textures/grass", AssetPath = "Assets/Grass.png", AssetGuid = Guid.NewGuid(), BundleId = 0 });
        m.AssetList.Add(new ManifestAsset { Address = "shaders/base", AssetPath = "Assets/Base.shader", AssetGuid = Guid.NewGuid(), BundleId = 3 });

        m.BuildIndexes();
        return m;
    }

    private static BundleInfo MakeBundle(string name, int[] deps)
    {
        return new BundleInfo
        {
            BundleName = name,
            FileName = name,
            BundleGuid = BundleHash.FromGuid(Guid.NewGuid()),
            FileHash = BundleHash.FromGuid(Guid.NewGuid()),
            FileCrc = (uint)name.GetHashCode(),
            FileSize = 1024,
            DependentBundleIds = deps,
        };
    }

    [Fact]
    public void SerializeDeserialize_RoundTripsAllFields()
    {
        var original = BuildDiamondManifest();
        var bytes = PackageManifestSerializer.Serialize(original);
        var restored = PackageManifestSerializer.Deserialize(bytes);

        Assert.NotNull(restored);
        Assert.Equal(original.PackageName, restored!.PackageName);
        Assert.Equal(original.PackageVersion, restored.PackageVersion);
        Assert.Equal(original.BuildTarget, restored.BuildTarget);
        Assert.True(restored.EnableAddressable);
        Assert.Equal(original.BundleList.Count, restored.BundleList.Count);
        Assert.Equal(original.AssetList.Count, restored.AssetList.Count);

        // Dependency arrays round-trip.
        Assert.Equal(original.BundleList[0].DependentBundleIds, restored.BundleList[0].DependentBundleIds);
        Assert.Equal(original.BundleList[1].DependentBundleIds, restored.BundleList[1].DependentBundleIds);
    }

    [Fact]
    public void WrongMagic_ReturnsNull()
    {
        var bytes = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var restored = PackageManifestSerializer.Deserialize(bytes);
        Assert.Null(restored);
    }

    [Fact]
    public void TryGetAssetByAddress_ResolvesCorrectly()
    {
        var m = BuildDiamondManifest();
        Assert.True(m.TryGetAssetByAddress("textures/grass", out var a));
        Assert.Equal(0, a.BundleId);
    }

    [Fact]
    public void TryGetBundleByName_ResolvesCorrectly()
    {
        var m = BuildDiamondManifest();
        Assert.True(m.TryGetBundleByName("b.bundle", out var b));
        Assert.Contains(3, b.DependentBundleIds);
    }

    [Fact]
    public void ReferrerBundleIds_BuiltOnIndex_ForReverseEdges()
    {
        var m = BuildDiamondManifest();
        // d is referenced by both b and c.
        Assert.True(m.TryGetBundleByName("d.bundle", out var d));
        Assert.Equal(2, d.ReferrerBundleIds.Length);
        var referrers = d.ReferrerBundleIds.OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 1, 2 }, referrers);
    }

    [Fact]
    public void GetAllBundleDependencies_Diamond_ProducesCorrectClosure()
    {
        var m = BuildDiamondManifest();
        Assert.True(m.TryGetBundleByName("a.bundle", out var a));
        var closure = m.GetAllBundleDependencies(a);

        var names = closure.Select(b => b.BundleName).OrderBy(n => n).ToArray();
        // Closure of a's deps: b, c, d (and a itself). All four should appear.
        Assert.Contains("a.bundle", names);
        Assert.Contains("b.bundle", names);
        Assert.Contains("c.bundle", names);
        Assert.Contains("d.bundle", names);
        Assert.Equal(4, names.Length);
    }

    [Fact]
    public void GetAllDependencies_Diamond_ExcludesItself()
    {
        var m = BuildDiamondManifest();
        var abm = new AssetBundleManifest(m);
        var deps = abm.GetAllDependencies("a.bundle");

        // a's dependency closure is {b, c, d} - a itself excluded.
        Assert.DoesNotContain("a.bundle", deps);
        Assert.Contains("b.bundle", deps);
        Assert.Contains("c.bundle", deps);
        Assert.Contains("d.bundle", deps);
        Assert.Equal(3, deps.Length);
    }

    [Fact]
    public void GetDirectDependencies_ReturnsOneLevel()
    {
        var m = BuildDiamondManifest();
        var abm = new AssetBundleManifest(m);
        var direct = abm.GetDirectDependencies("a.bundle");
        Assert.Equal(2, direct.Length);
        Assert.Contains("b.bundle", direct);
        Assert.Contains("c.bundle", direct);
        Assert.DoesNotContain("d.bundle", direct); // d is transitive, not direct
    }

    [Fact]
    public void GetAssetBundleHashAndCrc_ReturnStoredValues()
    {
        var m = BuildDiamondManifest();
        var abm = new AssetBundleManifest(m);
        Assert.True(m.TryGetBundleByName("a.bundle", out var a));
        Assert.Equal(a.FileHash, abm.GetAssetBundleHash("a.bundle"));
        Assert.Equal(a.FileCrc, abm.GetAssetBundleCrc("a.bundle"));
    }

    [Fact]
    public void GetAllReferencingBundles_ReturnsReverseEdges()
    {
        var m = BuildDiamondManifest();
        var abm = new AssetBundleManifest(m);
        var refs = abm.GetAllReferencingBundles("d.bundle");
        Assert.Equal(2, refs.Length);
        Assert.Contains("b.bundle", refs);
        Assert.Contains("c.bundle", refs);
    }

    [Fact]
    public void GetDirectDependencies_UnknownBundle_Throws()
    {
        var m = BuildDiamondManifest();
        var abm = new AssetBundleManifest(m);
        Assert.Throws<KeyNotFoundException>(() => abm.GetDirectDependencies("does-not-exist.bundle"));
    }

    [Fact]
    public void TagLookup_FindsAssetsByTag()
    {
        var m = new PackageManifest { PackageName = "t", EnableAddressable = true };
        m.BundleList.Add(MakeBundle("a.bundle", Array.Empty<int>()));
        m.AssetList.Add(new ManifestAsset
        {
            Address = "ui/main",
            AssetPath = "Assets/UI.png",
            AssetGuid = Guid.NewGuid(),
            BundleId = 0,
            Tags = new[] { "ui", "preload" },
        });
        m.AssetList.Add(new ManifestAsset
        {
            Address = "fx/explode",
            AssetPath = "Assets/FX.png",
            AssetGuid = Guid.NewGuid(),
            BundleId = 0,
            Tags = new[] { "fx" },
        });
        m.BuildIndexes();

        Assert.Single(m.GetAssetsByTag("ui"));
        Assert.Single(m.GetAssetsByTag("fx"));
        Assert.Single(m.GetAssetsByTag("preload"));
        Assert.Empty(m.GetAssetsByTag("missing"));
    }

    [Fact]
    public void BuildIndexes_DuplicateAddress_KeepsFirstAndDoesNotThrow()
    {
        var m = new PackageManifest { PackageName = "dup", EnableAddressable = true };
        m.BundleList.Add(MakeBundle("a.bundle", Array.Empty<int>()));
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        m.AssetList.Add(new ManifestAsset
        {
            Address = "Footer",
            AssetPath = "Texts/Footer.txt",
            AssetGuid = first,
            BundleId = 0,
        });
        m.AssetList.Add(new ManifestAsset
        {
            Address = "Footer",
            AssetPath = "Images/Footer.png",
            AssetGuid = second,
            BundleId = 0,
        });

        m.BuildIndexes();

        Assert.True(m.TryGetAssetByAddress("Footer", out ManifestAsset asset));
        Assert.Equal(first, asset.AssetGuid);
        Assert.Equal("Texts/Footer.txt", asset.AssetPath);
        Assert.True(m.TryGetAssetByGuid(second, out ManifestAsset byGuid));
        Assert.Equal(second, byGuid.AssetGuid);
    }
}
