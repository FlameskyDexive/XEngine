// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// <see cref="Scene.StructureVersion"/> must advance on every topology mutation (add/remove,
/// reparent, sibling/root reorder) - the editor hierarchy keys its cached tree on it, so a missed
/// bump means newly created or moved objects never show up until something else dirties the scene.
/// </summary>
public sealed class SceneStructureVersionTests : RuntimeTestBase
{
    [Fact]
    public void AddAndRemoveBumpStructureVersion()
    {
        using var scene = CreateScene(enable: true);
        var go = CreateGameObject("Created");

        long before = scene.StructureVersion;
        scene.Add(go);
        long afterAdd = scene.StructureVersion;
        Assert.True(afterAdd > before, "Scene.Add must bump StructureVersion.");

        scene.Remove(go);
        Assert.True(scene.StructureVersion > afterAdd, "Scene.Remove must bump StructureVersion.");
    }

    [Fact]
    public void SameSceneReparentBumpsStructureVersion()
    {
        using var scene = CreateScene(enable: true);
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        scene.Add(parent);
        scene.Add(child);

        long before = scene.StructureVersion;
        Assert.True(child.SetParent(parent));
        Assert.True(scene.StructureVersion > before, "Same-scene SetParent must bump StructureVersion.");

        long beforeUnparent = scene.StructureVersion;
        Assert.True(child.SetParent(null!));
        Assert.True(scene.StructureVersion > beforeUnparent, "Unparenting must bump StructureVersion.");
    }

    [Fact]
    public void SiblingReorderBumpsStructureVersion()
    {
        using var scene = CreateScene(enable: true);
        var parent = CreateGameObject("Parent");
        var a = CreateGameObject("A");
        var b = CreateGameObject("B");
        scene.Add(parent);
        scene.Add(a);
        scene.Add(b);
        a.SetParent(parent);
        b.SetParent(parent);

        long before = scene.StructureVersion;
        b.SetSiblingIndex(0);
        Assert.True(scene.StructureVersion > before, "GameObject.SetSiblingIndex must bump StructureVersion.");

        long beforeTransformReorder = scene.StructureVersion;
        b.Transform.SetSiblingIndex(1);
        Assert.True(scene.StructureVersion > beforeTransformReorder,
            "Transform.SetSiblingIndex must bump StructureVersion.");
    }

    [Fact]
    public void RootReorderBumpsStructureVersion()
    {
        using var scene = CreateScene(enable: true);
        var first = CreateGameObject("First");
        var second = CreateGameObject("Second");
        scene.Add(first);
        scene.Add(second);

        long before = scene.StructureVersion;
        scene.SetRootIndex(second, 0);
        Assert.True(scene.StructureVersion > before, "Scene.SetRootIndex must bump StructureVersion.");
        Assert.Equal(0, scene.GetRootIndex(second));
    }

    [Fact]
    public void NonTopologyChangesDoNotBumpStructureVersion()
    {
        using var scene = CreateScene(enable: true);
        var go = CreateGameObject("Mover");
        scene.Add(go);

        long before = scene.StructureVersion;
        go.Transform.Position = new Float3(1f, 2f, 3f);
        go.Name = "Renamed";
        Assert.Equal(before, scene.StructureVersion);
    }
}
