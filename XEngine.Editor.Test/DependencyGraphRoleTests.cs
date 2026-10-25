// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Proves A3: the dependency graph now carries role labels on edges, while the legacy Guid-only API
/// stays backward compatible. This lets an AI distinguish "references as texture" from "references as
/// prefab" — the gap the binary graph couldn't fill.
/// </summary>
public class DependencyGraphRoleTests
{
    [Fact]
    public void TypedEdges_CarryRoles_AndGuidApiStaysCompatible()
    {
        var graph = new DependencyGraph();
        Guid material = Guid.NewGuid();
        Guid texture = Guid.NewGuid();
        Guid prefab = Guid.NewGuid();

        // A3 typed overload: material depends on a texture AS a texture.
        graph.SetDependencies(material, new[]
        {
            new DependencyEdge(texture, "Texture"),
        }, typed: true);

        // Legacy Guid overload: prefab depends on material (role defaults to "References").
        graph.SetDependencies(prefab, new[] { material });

        // Role-aware query distinguishes the texture ref.
        var materialDeps = graph.GetDependenciesWithRoles(material);
        Assert.Single(materialDeps);
        foreach (var edge in materialDeps)
        {
            Assert.Equal(texture, edge.Target);
            Assert.Equal("Texture", edge.Role);
        }

        var prefabDeps = graph.GetDependenciesWithRoles(prefab);
        Assert.Single(prefabDeps);
        foreach (var edge in prefabDeps)
        {
            Assert.Equal(material, edge.Target);
            Assert.Equal("References", edge.Role); // default for the Guid overload
        }

        // Legacy Guid queries still work unchanged.
        Assert.Contains(texture, graph.GetDependencies(material));
        Assert.Contains(material, graph.GetDependents(texture));
    }

    /// <summary>★ A3-cont: an AssetRef&lt;Texture2D&gt; serializing records a typed dependency whose Role
    /// is the AssetRef's generic type name. This is the A3 core contract, tested without the heavy
    /// importer/D3D12 stack (which is unrelated environment-flaky).</summary>
    [Fact]
    public void AssetRefSerialization_RecordsTypedDependencyRole()
    {
        var ctx = new DependencySerializationContext();
        // A Texture2D AssetRef pointing at a fake guid.
        var texRef = new AssetRef<XEngine.Runtime.Resources.Texture2D> { AssetID = Guid.NewGuid() };
        var compound = EchoObject.NewCompound();
        texRef.Serialize(ref compound, ctx);

        Assert.Single(ctx.Dependencies);
        foreach (var dep in ctx.Dependencies)
        {
            Assert.Equal(texRef.AssetID, dep.Guid);
            Assert.Equal("Texture2D", dep.Role); // typeof(T).Name — the A3 role
        }
        // And the Guid view stays consistent.
        Assert.Contains(texRef.AssetID, ctx.GuidsOnly);
    }
}
