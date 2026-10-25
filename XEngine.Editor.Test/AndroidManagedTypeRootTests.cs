// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Text.Json;

using XEngine.Echo;
using XEngine.Editor.Build;
using XEngine.Editor.Build.Android;
using XEngine.Runtime;
using XEngine.Runtime.MeshFeatures;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidManagedTypeRootTestComponent : MonoBehaviour
{
    public string Label = "trim-root";

    [SerializeField]
    private AndroidManagedNestedPayload _payload = new();
}

public sealed class AndroidManagedNestedPayload
{
    public int Score = 7;
}

[Preserve]
public sealed class AndroidExplicitPreserveRoot
{
    private AndroidExplicitPreserveRoot() { }
    public string Value = "preserved";
}

public sealed class AndroidManagedTypeRootMeshFeature : MeshFeatureSpec
{
    public override string Key => "android-test";
    public override string DisplayName => "Android Test";
    public override Type FeatureType => typeof(AndroidManagedNestedPayload);
    public override int Version => 1;
    public override void PopulateDefaults(EchoObject settings) { }
    public override EngineObject? TryGenerate(Mesh mesh, EchoObject? settings) => null;
}

public static class AndroidManagedLifecycleRoot
{
    [OnAssemblyLoad]
    public static void OnAssemblyLoad() { }

    [Preserve]
    public sealed class NestedPreserveRoot
    {
    }
}

[Trait("Category", "Build")]
public sealed class AndroidManagedTypeRootTests : EditorTestHarness
{
    [Fact]
    public void CollectorFindsSerializedSceneTypesAndGeneratesDeterministicRoots()
    {
        var scene = new Scene();
        var gameObject = new GameObject("Managed root");
        gameObject.AddComponent(typeof(AndroidManagedTypeRootTestComponent));
        scene.Add(gameObject);
        Guid sceneGuid = CreateSceneAsset(scene, "ManagedRoots.scene");
        AssetCollector.CollectionResult assets = AssetCollector.Collect([sceneGuid], dependenciesOnly: true);

        AndroidManagedTypeRootCollection roots = AndroidManagedTypeRootCollector.Collect(
            assets,
            CreateDependencies(includeTestAssembly: true));

        AndroidManagedTypeRoot componentRoot = FindRoot(
            roots,
            typeof(AndroidManagedTypeRootTestComponent));
        Assert.Contains(
            componentRoot.Reasons,
            reason => reason.AssetGuid == sceneGuid &&
                reason.AssetPath == "ManagedRoots.scene" &&
                reason.Source == "serialized-$type");

        AndroidManagedTypeRoot nestedRoot = FindRoot(roots, typeof(AndroidManagedNestedPayload));
        Assert.Contains(
            nestedRoot.Reasons,
            reason => reason.AssetGuid == sceneGuid &&
                reason.Source.Contains("serialized-field:", StringComparison.Ordinal) &&
                reason.Source.EndsWith("._payload", StringComparison.Ordinal));
        Assert.Contains(
            FindRoot(roots, typeof(AndroidExplicitPreserveRoot)).Reasons,
            reason => reason.Source == "preserve-attribute");
        Assert.Contains(
            FindRoot(roots, typeof(AndroidManagedTypeRootMeshFeature)).Reasons,
            reason => reason.Source == "mesh-feature-registry");
        Assert.Contains(
            FindRoot(roots, typeof(AndroidManagedLifecycleRoot)).Reasons,
            reason => reason.Source == "script-lifecycle-attribute");
        Assert.Contains(
            FindRoot(roots, typeof(AndroidManagedLifecycleRoot.NestedPreserveRoot)).Reasons,
            reason => reason.Source == "preserve-attribute");

        string source = AndroidManagedTrimRoots.CreateSource(roots);
        Assert.Contains("DynamicDependency", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeTypeRegistry.ConfigureGenerated", source, StringComparison.Ordinal);
        Assert.Contains("AndroidManagedLifecycleRoot/NestedPreserveRoot", source, StringComparison.Ordinal);
        AndroidManagedTypeRoot nestedPreserveRoot = FindRoot(roots, typeof(AndroidManagedLifecycleRoot.NestedPreserveRoot));
        Assert.Contains(
            JsonSerializer.Serialize(nestedPreserveRoot.TypeName + ", " + nestedPreserveRoot.AssemblyName),
            source,
            StringComparison.Ordinal);
        Assert.Contains(typeof(AndroidManagedTypeRootTestComponent).FullName!, source, StringComparison.Ordinal);
        Assert.Contains(
            typeof(AndroidManagedTypeRootTestComponent).Assembly.GetName().Name!,
            source,
            StringComparison.Ordinal);

        string first = Path.Combine(Project.TempPath, "managed-roots-first.json");
        string second = Path.Combine(Project.TempPath, "managed-roots-second.json");
        AndroidManagedTypeRootReport.Write(first, roots);
        AndroidManagedTypeRootReport.Write(second, roots);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(first));
        Assert.Equal(roots.Types.Count, report.RootElement.GetProperty("typeCount").GetInt32());
    }

    [Fact]
    public void CollectorReadsPackageProvidedPrefabsThroughTheMountTable()
    {
        // Regression: package assets ("Packages/<pkg>/...") were resolved against Assets/ and failed
        // with "asset source is missing"; they must resolve through the package mount table.
        string packageRoot = Path.Combine(Project.PackagesPath, "com.test.embed");
        Directory.CreateDirectory(Path.Combine(packageRoot, "Assets"));
        File.WriteAllText(
            Path.Combine(packageRoot, "package.json"),
            "{\"name\":\"com.test.embed\",\"version\":\"0.0.1\"}");

        var gameObject = new GameObject("Pkg root");
        gameObject.AddComponent(typeof(AndroidManagedTypeRootTestComponent));
        gameObject.ClearPrefabDataRecursive();
        Guid savedId = gameObject.AssetID;
        gameObject.AssetID = Guid.Empty;
        EchoObject echo = Serializer.Serialize(typeof(object), gameObject);
        gameObject.AssetID = savedId;
        File.WriteAllText(Path.Combine(packageRoot, "Assets", "PkgPrefab.prefab"), echo.WriteToString());

        Projects.Packages.PackageManager.Invalidate();
        string logicalPath = "Packages/com.test.embed/Assets/PkgPrefab.prefab";
        Guid prefabGuid = Assets.ImportFile(logicalPath);
        Assert.NotEqual(Guid.Empty, prefabGuid);

        AssetCollector.CollectionResult packageAssets = AssetCollector.Collect([prefabGuid], dependenciesOnly: true);
        AndroidManagedTypeRootCollection roots = AndroidManagedTypeRootCollector.Collect(
            packageAssets,
            CreateDependencies(includeTestAssembly: true));

        AndroidManagedTypeRoot componentRoot = FindRoot(roots, typeof(AndroidManagedTypeRootTestComponent));
        Assert.Contains(
            componentRoot.Reasons,
            reason => reason.AssetGuid == prefabGuid &&
                reason.AssetPath == logicalPath &&
                reason.Source == "serialized-$type");
    }

    [Fact]
    public void CollectorRejectsSerializedTypeOutsideManagedClosure()
    {
        string qualifiedName = typeof(AndroidManagedTypeRootTestComponent).AssemblyQualifiedName!;
        EchoObject document = EchoObject.ReadFromString(
            "{\n  \"$type\": " + JsonSerializer.Serialize(qualifiedName) + "\n}");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidManagedTypeRootCollector.CollectDocument(
                document,
                Guid.NewGuid(),
                "MissingAssembly.scene",
                CreateDependencies(includeTestAssembly: false)));

        Assert.Contains("absent from the Android managed dependency closure", error.Message, StringComparison.Ordinal);
    }

    private static AndroidManagedTypeRoot FindRoot(AndroidManagedTypeRootCollection roots, Type type)
    {
        string assemblyName = type.Assembly.GetName().Name!;
        for (int i = 0; i < roots.Types.Count; i++)
        {
            AndroidManagedTypeRoot root = roots.Types[i];
            if (root.AssemblyName == assemblyName && root.TypeName == type.FullName)
                return root;
        }
        throw new Xunit.Sdk.XunitException($"Managed type root was not collected for {type.FullName}.");
    }

    private static AndroidManagedDependencyCollection CreateDependencies(bool includeTestAssembly)
    {
        var assemblies = new List<AndroidManagedAssemblyInput>
        {
            CreateAssemblyInput(typeof(EngineObject).Assembly),
            CreateAssemblyInput(typeof(XEngine.Vector.Float3).Assembly),
            // Prefab assets report the wrapped Echo document as their main asset type.
            CreateAssemblyInput(typeof(EchoObject).Assembly),
            // The ai.navigation package registers nav-mesh feature specs whose settings types live
            // in DotRecast; every closure that includes the test assembly walks those registries.
            CreateAssemblyInput(typeof(DotRecast.Recast.RcPartition).Assembly),
        };
        if (includeTestAssembly)
            assemblies.Add(CreateAssemblyInput(typeof(AndroidManagedTypeRootTests).Assembly));
        return new AndroidManagedDependencyCollection(
            Array.AsReadOnly(assemblies.ToArray()),
            Array.Empty<AndroidManagedAssemblyExclusion>());
    }

    private static AndroidManagedAssemblyInput CreateAssemblyInput(Assembly assembly) => new(
        assembly.GetName().Name!,
        assembly.Location,
        "test",
        "managed type root test dependency");
}
