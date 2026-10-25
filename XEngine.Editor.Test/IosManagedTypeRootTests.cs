// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Text.Json;

using XEngine.Echo;
using XEngine.Editor.Build;
using XEngine.Editor.Build.Ios;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class IosManagedTypeRootTestComponent : MonoBehaviour
{
    public string Label = "trim-root";
}

[Trait("Category", "Build")]
public sealed class IosManagedTypeRootTests : EditorTestHarness
{
    [Fact]
    public void CollectorFindsSerializedSceneTypesAndGeneratesDeterministicRoots()
    {
        var scene = new Scene();
        var gameObject = new GameObject("Managed root");
        gameObject.AddComponent(typeof(IosManagedTypeRootTestComponent));
        scene.Add(gameObject);
        Guid sceneGuid = CreateSceneAsset(scene, "ManagedRoots.scene");
        AssetCollector.CollectionResult assets = AssetCollector.Collect([sceneGuid], dependenciesOnly: true);

        IosManagedTypeRootCollection roots = IosManagedTypeRootCollector.Collect(
            assets,
            CreateAssemblies(includeTestAssembly: true));

        IosManagedTypeRoot componentRoot = FindRoot(roots, typeof(IosManagedTypeRootTestComponent));
        Assert.Contains(
            componentRoot.Reasons,
            reason => reason.AssetGuid == sceneGuid &&
                reason.AssetPath == "ManagedRoots.scene" &&
                reason.Source == "serialized-$type");

        string source = IosManagedTrimRoots.CreateSource(roots);
        Assert.Contains("DynamicDependency", source, StringComparison.Ordinal);
        Assert.Contains(typeof(IosManagedTypeRootTestComponent).FullName!, source, StringComparison.Ordinal);
        Assert.Contains("namespace XEngine.Generated.iOS;", source, StringComparison.Ordinal);
        Assert.Contains("ConfigureGenerated", source, StringComparison.Ordinal);

        string first = Path.Combine(Project.TempPath, "ios-managed-roots-first.json");
        string second = Path.Combine(Project.TempPath, "ios-managed-roots-second.json");
        IosManagedTypeRootReport.Write(first, roots);
        IosManagedTypeRootReport.Write(second, roots);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(first));
        Assert.Equal(roots.Types.Count, report.RootElement.GetProperty("typeCount").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void CollectorRejectsSerializedTypeOutsideManagedClosure()
    {
        string qualifiedName = typeof(IosManagedTypeRootTestComponent).AssemblyQualifiedName!;
        EchoObject document = EchoObject.ReadFromString(
            "{\n  \"$type\": " + JsonSerializer.Serialize(qualifiedName) + "\n}");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            IosManagedTypeRootCollector.CollectDocument(
                document,
                Guid.NewGuid(),
                "MissingAssembly.scene",
                CreateAssemblies(includeTestAssembly: false)));
        Assert.Contains("absent from the iOS managed dependency closure", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullManagedTrimmingIsBlocked()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => IosManagedTrimRoots.ValidateRequestedMode(IosManagedStrippingMode.Full));
        Assert.Equal(IosManagedTrimRoots.FullBlockedError, error.Message);
        IosManagedTrimRoots.ValidateRequestedMode(IosManagedStrippingMode.Safe);
        IosManagedTrimRoots.ValidateRequestedMode(IosManagedStrippingMode.Disabled);
    }

    [Fact]
    public void EmptyGeneratedRootsDoNotSealTheRuntimeTypeRegistry()
    {
        string source = IosManagedTrimRoots.CreateSource(IosManagedTypeRootCollection.Empty);

        Assert.Contains("internal static void Preserve()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureGenerated", source, StringComparison.Ordinal);
    }

    private static IosManagedTypeRoot FindRoot(IosManagedTypeRootCollection roots, Type type)
    {
        string assemblyName = type.Assembly.GetName().Name!;
        for (int i = 0; i < roots.Types.Count; i++)
        {
            IosManagedTypeRoot root = roots.Types[i];
            if (root.AssemblyName == assemblyName && root.TypeName == type.FullName)
                return root;
        }
        throw new Xunit.Sdk.XunitException($"Managed type root was not collected for {type.FullName}.");
    }

    private static IosManagedAssemblyInput[] CreateAssemblies(bool includeTestAssembly)
    {
        var assemblies = new List<IosManagedAssemblyInput>
        {
            CreateAssemblyInput(typeof(EngineObject).Assembly),
        };
        if (includeTestAssembly)
            assemblies.Add(CreateAssemblyInput(typeof(IosManagedTypeRootTests).Assembly));
        return assemblies.ToArray();
    }

    private static IosManagedAssemblyInput CreateAssemblyInput(Assembly assembly) => new(
        assembly.GetName().Name!,
        assembly.Location,
        "test",
        "managed type root test dependency");
}
