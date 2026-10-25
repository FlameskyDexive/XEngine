// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Text.Json;

using XEngine.Echo;
using XEngine.Editor.Build;
using XEngine.Editor.Build.HarmonyOS;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSManagedTypeRootTestComponent : MonoBehaviour
{
    public string Label = "trim-root";
}

[Trait("Category", "Build")]
public sealed class HarmonyOSManagedTypeRootTests : EditorTestHarness
{
    [Fact]
    public void CollectorFindsSerializedSceneTypesAndGeneratesDeterministicRoots()
    {
        var scene = new Scene();
        var gameObject = new GameObject("Managed root");
        gameObject.AddComponent(typeof(HarmonyOSManagedTypeRootTestComponent));
        scene.Add(gameObject);
        Guid sceneGuid = CreateSceneAsset(scene, "ManagedRoots.scene");
        AssetCollector.CollectionResult assets = AssetCollector.Collect([sceneGuid], dependenciesOnly: true);

        HarmonyOSManagedTypeRootCollection roots = HarmonyOSManagedTypeRootCollector.Collect(
            assets,
            CreateDependencies(includeTestAssembly: true));

        HarmonyOSManagedTypeRoot componentRoot = FindRoot(
            roots,
            typeof(HarmonyOSManagedTypeRootTestComponent));
        Assert.Contains(
            componentRoot.Reasons,
            reason => reason.AssetGuid == sceneGuid &&
                reason.AssetPath == "ManagedRoots.scene" &&
                reason.Source == "serialized-$type");

        string source = HarmonyOSManagedTrimRoots.CreateSource(roots);
        Assert.Contains("DynamicDependency", source, StringComparison.Ordinal);
        Assert.Contains(typeof(HarmonyOSManagedTypeRootTestComponent).FullName!, source, StringComparison.Ordinal);
        Assert.Contains(
            typeof(HarmonyOSManagedTypeRootTestComponent).Assembly.GetName().Name!,
            source,
            StringComparison.Ordinal);
        Assert.Contains("namespace XEngine.Generated.HarmonyOS;", source, StringComparison.Ordinal);

        string first = Path.Combine(Project.TempPath, "managed-roots-first.json");
        string second = Path.Combine(Project.TempPath, "managed-roots-second.json");
        HarmonyOSManagedTypeRootReport.Write(first, roots);
        HarmonyOSManagedTypeRootReport.Write(second, roots);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(first));
        Assert.Equal(roots.Types.Count, report.RootElement.GetProperty("typeCount").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void CollectorRejectsSerializedTypeOutsideManagedClosure()
    {
        string qualifiedName = typeof(HarmonyOSManagedTypeRootTestComponent).AssemblyQualifiedName!;
        EchoObject document = EchoObject.ReadFromString(
            "{\n  \"$type\": " + JsonSerializer.Serialize(qualifiedName) + "\n}");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            HarmonyOSManagedTypeRootCollector.CollectDocument(
                document,
                Guid.NewGuid(),
                "MissingAssembly.scene",
                CreateDependencies(includeTestAssembly: false)));

        Assert.Contains("absent from the HarmonyOS managed dependency closure", error.Message, StringComparison.Ordinal);
    }

    private static HarmonyOSManagedTypeRoot FindRoot(HarmonyOSManagedTypeRootCollection roots, Type type)
    {
        string assemblyName = type.Assembly.GetName().Name!;
        for (int i = 0; i < roots.Types.Count; i++)
        {
            HarmonyOSManagedTypeRoot root = roots.Types[i];
            if (root.AssemblyName == assemblyName && root.TypeName == type.FullName)
                return root;
        }
        throw new Xunit.Sdk.XunitException($"Managed type root was not collected for {type.FullName}.");
    }

    private static HarmonyOSManagedDependencyCollection CreateDependencies(bool includeTestAssembly)
    {
        var assemblies = new List<HarmonyOSManagedAssemblyInput>
        {
            CreateAssemblyInput(typeof(EngineObject).Assembly),
        };
        if (includeTestAssembly)
            assemblies.Add(CreateAssemblyInput(typeof(HarmonyOSManagedTypeRootTests).Assembly));
        return new HarmonyOSManagedDependencyCollection(
            Array.AsReadOnly(assemblies.ToArray()),
            Array.Empty<HarmonyOSManagedAssemblyExclusion>());
    }

    private static HarmonyOSManagedAssemblyInput CreateAssemblyInput(Assembly assembly) => new(
        assembly.GetName().Name!,
        assembly.Location,
        "test",
        "managed type root test dependency");
}
