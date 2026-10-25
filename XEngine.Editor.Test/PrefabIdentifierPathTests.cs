// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using XEngine.Echo;
using XEngine.Editor.Prefabs;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class PrefabIdentifierPathTests : EditorTestHarness
{
    [Theory]
    [InlineData("a1000000000000000000000000000001")]
    [InlineData("c1000000000000000000000000000001")]
    public void Refresh_GuidPrefixPreservesPropertyOverride(string liveId)
    {
        var source = new GameObject("Prefix source");
        var original = source.AddComponent<OverrideComp>();
        original.A = 1; original.B = 1;
        Guid assetId = CreatePrefabAsset(source, "Prefix.prefab");
        var instance = GetPrefab(assetId)!.Instantiate()!;
        var component = instance.GetComponent<OverrideComp>()!;
        component.Identifier = Guid.ParseExact(liveId, "N");
        component.A = 99;
        PrefabUtility.DetectComponentOverrides(instance, component);
        Assert.Single(instance.PrefabOverrides);
        Assert.Equal(liveId + ".A", instance.PrefabOverrides[0].Path);
        var scene = new Scene();
        scene.Add(instance);
        Scene.Load(scene);

        original.B = 2;
        File.WriteAllText(AssetAbsolutePath("Prefix.prefab"),
            Serializer.Serialize(typeof(object), source).WriteToString());
        Assets.Reimport(assetId);
        PrefabUtility.RefreshAllInstances(assetId);

        var refreshed = Assert.Single(Scene.Current!.RootObjects).GetComponent<OverrideComp>()!;
        string? evidence = Environment.GetEnvironmentVariable("XENGINE_TEST_SCREENSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "prefab-prefix-" + liveId[0] + ".json"),
                JsonSerializer.Serialize(new
                {
                    liveId, refreshedId = refreshed.Identifier.ToString("N"),
                    expectedA = 99, expectedB = 2, actualA = refreshed.A, actualB = refreshed.B,
                }));
        }
        Assert.Equal(Guid.ParseExact(liveId, "N"), refreshed.Identifier);
        Assert.Equal(99, refreshed.A);
        Assert.Equal(2, refreshed.B);
    }
}
