// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.MeshFeatures;
using XEngine.Echo;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class RuntimeTypeRegistryTests
{
    [Fact]
    public void RegisterProvidesDeterministicAliasesAndDiscovery()
    {
        RuntimeTypeRegistry.Register(typeof(RegistryMeshFeature));

        Assert.Same(
            typeof(RegistryMeshFeature),
            RuntimeTypeRegistry.Resolve(typeof(RegistryMeshFeature).AssemblyQualifiedName!));
        Assert.Same(
            typeof(RegistryMeshFeature),
            RuntimeTypeRegistry.Resolve(typeof(RegistryMeshFeature).FullName!));
        Assert.Contains(
            typeof(RegistryMeshFeature),
            RuntimeTypeRegistry.FindTypesImplementing(typeof(MeshFeatureSpec)));
    }

    [Fact]
    public void RegisterAliasResolvesVersionedNameAndInvalidatesCachedMiss()
    {
        const string legacyTypeName = "Legacy.RegistryMeshFeature, Legacy.Ui, Version=1.0.0.0, Culture=neutral";
        string targetTypeName = typeof(RegistryMeshFeature).AssemblyQualifiedName!;

        RuntimeTypeRegistry.Register(typeof(RegistryMeshFeature));

        // A failed lookup is cached by Echo. RegisterAlias must invalidate that negative result.
        Assert.Null(TypeNameRegistry.ResolveFullTypeName(legacyTypeName));

        RuntimeTypeRegistry.RegisterAlias(legacyTypeName, targetTypeName);

        Assert.Same(typeof(RegistryMeshFeature), TypeNameRegistry.ResolveFullTypeName(legacyTypeName));
        Assert.Same(typeof(RegistryMeshFeature), RuntimeTypeRegistry.Resolve("Legacy.RegistryMeshFeature, Legacy.Ui"));
    }

    private sealed class RegistryMeshFeature : MeshFeatureSpec
    {
        public override string Key => "registry-test";
        public override string DisplayName => "Registry Test";
        public override Type FeatureType => typeof(RegistryMeshFeature);
        public override int Version => 1;
        public override void PopulateDefaults(XEngine.Echo.EchoObject settings) { }
        public override EngineObject? TryGenerate(Resources.Mesh mesh, XEngine.Echo.EchoObject? settings) => null;
    }
}
