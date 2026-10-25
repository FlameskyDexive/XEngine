// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.Text.Json;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using XEngine.Editor.Build.Ios;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class IosPlayerProjectTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineIosPlayerProjectTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenerateAsync_WritesNet10IosMonoFullAotPlayer()
    {
        Directory.CreateDirectory(_testRoot);
        string firstRoot = Path.Combine(_testRoot, "first");
        string secondRoot = Path.Combine(_testRoot, "second");
        IosPlayerProjectOptions options = CreateOptions();

        IosPlayerProjectLayout first = await IosPlayerProjectGenerator.GenerateAsync(firstRoot, options);
        IosPlayerProjectLayout second = await IosPlayerProjectGenerator.GenerateAsync(secondRoot, options);

        Assert.Equal("ios-arm64", first.RuntimeIdentifier);
        Assert.Equal(IosPlayerProjectGenerator.GetAssemblyName("My Game"), first.AssemblyName);
        Assert.True(File.Exists(first.ProjectPath));
        Assert.True(File.Exists(first.InfoPlistPath));
        Assert.True(File.Exists(first.OwnershipManifestPath));
        Assert.Equal(EnumerateFileBytes(firstRoot), EnumerateFileBytes(secondRoot));

        XDocument project = XDocument.Load(first.ProjectPath);
        Assert.Equal("net10.0-ios", Property(project, "TargetFramework"));
        Assert.Equal("ios-arm64", Property(project, "RuntimeIdentifier"));
        Assert.Equal("16.0", Property(project, "SupportedOSPlatformVersion"));
        Assert.Equal("false", Property(project, "PublishAot"));
        Assert.Equal("false", Property(project, "UseInterpreter"));
        Assert.Equal("SdkOnly", Property(project, "MtouchLink"));
        Assert.Contains("XENGINE_IOS", Property(project, "DefineConstants"), StringComparison.Ordinal);
        Assert.Contains("XENGINE_MOBILE", Property(project, "DefineConstants"), StringComparison.Ordinal);
        Assert.Equal("XEngine.Generated.iOS", Property(project, "RootNamespace"));
        string projectText = await File.ReadAllTextAsync(first.ProjectPath);
        Assert.DoesNotContain("PackageReference Include=\"Microsoft.iOS\"", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("Silk.NET.Metal", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpMetal", projectText, StringComparison.Ordinal);
        XElement runtimeReference = Assert.Single(project.Descendants("Reference"));
        Assert.Equal("XEngine.Runtime", runtimeReference.Attribute("Include")!.Value);
        Assert.Equal("Managed/XEngine.Runtime.dll", runtimeReference.Element("HintPath")!.Value);
        Assert.False(Path.IsPathRooted(runtimeReference.Element("HintPath")!.Value));
        Assert.True(File.Exists(Path.Combine(firstRoot, "Player", "Managed", "XEngine.Runtime.dll")));

        string infoPlist = await File.ReadAllTextAsync(first.InfoPlistPath);
        Assert.Contains("com.xengine.testgame", infoPlist, StringComparison.Ordinal);
        Assert.Contains("<string>16.0</string>", infoPlist, StringComparison.Ordinal);
        Assert.Contains("<string>metal</string>", infoPlist, StringComparison.Ordinal);
        Assert.Contains("<string>arm64</string>", infoPlist, StringComparison.Ordinal);
        XDocument infoPlistDocument = XDocument.Parse(infoPlist);
        XElement executableKey = Assert.Single(infoPlistDocument.Descendants("key")
            .Where(key => key.Value == "CFBundleExecutable"));
        Assert.Equal("My_Game.iOS", executableKey.ElementsAfterSelf().First().Value);
        XElement sceneManifestKey = Assert.Single(infoPlistDocument.Descendants("key")
            .Where(key => key.Value == "UIApplicationSceneManifest"));
        XElement sceneManifest = sceneManifestKey.ElementsAfterSelf().First();
        Assert.Equal("false", sceneManifest.Descendants("key")
            .Single(key => key.Value == "UIApplicationSupportsMultipleScenes")
            .ElementsAfterSelf().First().Name.LocalName);
        Assert.Equal("SceneDelegate", sceneManifest.Descendants("key")
            .Single(key => key.Value == "UISceneDelegateClassName")
            .ElementsAfterSelf().First().Value);

        string playerSource = await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "IosPlayer.cs"));
        Assert.Contains("_host.InitializeAudio(44100, 2048);", playerSource, StringComparison.Ordinal);
        Assert.Contains("assetBackend.LoadScene(sceneGuid)", playerSource, StringComparison.Ordinal);
        Assert.Contains("class SceneDelegate : UIWindowSceneDelegate", playerSource, StringComparison.Ordinal);
        Assert.Contains("override UIWindow? Window { get; set; }", playerSource, StringComparison.Ordinal);
        Assert.Contains("override void WillConnect(", playerSource, StringComparison.Ordinal);
        Assert.Contains("new UIWindow(windowScene)", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("base.WillConnect", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new UIWindow(UIScreen.MainScreen.Bounds)", playerSource, StringComparison.Ordinal);
        string metalViewSource = await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "IosMetalView.cs"));
        string rootsSource = await File.ReadAllTextAsync(Path.Combine(
            firstRoot,
            "Player",
            IosPlayerProjectGenerator.PlayerRootsSourceFileName));
        AssertNoSyntaxErrors(playerSource);
        AssertNoSyntaxErrors(metalViewSource);
        AssertNoSyntaxErrors(rootsSource);
        Assert.DoesNotContain("ConfigureGenerated", rootsSource, StringComparison.Ordinal);
        Assert.Contains("namespace XEngine.Generated.iOS;", rootsSource, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(firstRoot, "trimming", IosManagedTypeRootReport.FileName)));
        Assert.Contains("AttachLayer", playerSource, StringComparison.Ordinal);
        Assert.Contains("CADisplayLink", playerSource, StringComparison.Ordinal);
        Assert.Contains("PreferredFramesPerSecond = 60", playerSource, StringComparison.Ordinal);
        Assert.Contains("InitializePlatformHost", playerSource, StringComparison.Ordinal);
        Assert.Contains("IosPlatformHost", playerSource, StringComparison.Ordinal);
        Assert.Contains("DispatchTouch", metalViewSource, StringComparison.Ordinal);
        Assert.Contains("TouchesBegan", metalViewSource, StringComparison.Ordinal);
        Assert.Contains("IosTouchAction", metalViewSource, StringComparison.Ordinal);
        Assert.Contains("UpdateSafeAreaInsets", playerSource, StringComparison.Ordinal);
        Assert.Contains("SafeAreaInsets", playerSource, StringComparison.Ordinal);
        Assert.Contains("UITextField", playerSource, StringComparison.Ordinal);
        Assert.Contains("CommitText", playerSource, StringComparison.Ordinal);
        Assert.Contains("DeleteSurroundingText", playerSource, StringComparison.Ordinal);
        Assert.Contains("GCController", playerSource, StringComparison.Ordinal);
        Assert.Contains("GamepadConnected", playerSource, StringComparison.Ordinal);
        Assert.Contains("DidEnterBackgroundNotification", playerSource, StringComparison.Ordinal);
        Assert.Contains("WillEnterForegroundNotification", playerSource, StringComparison.Ordinal);
        Assert.Contains("IosPlatformServices", playerSource, StringComparison.Ordinal);
        Assert.Contains("IosAssetHost.Configure", playerSource, StringComparison.Ordinal);
        Assert.Contains("BindPlayerIfConfigured", playerSource, StringComparison.Ordinal);
        Assert.Contains("AssetPackagingMode.LooseFiles", playerSource, StringComparison.Ordinal);
        int viewDidLoadStart = playerSource.IndexOf("public override void ViewDidLoad()", StringComparison.Ordinal);
        int viewDidLayoutStart = playerSource.IndexOf(
            "public override void ViewDidLayoutSubviews()",
            viewDidLoadStart,
            StringComparison.Ordinal);
        string viewDidLoadSource = playerSource[viewDidLoadStart..viewDidLayoutStart];
        Assert.True(
            viewDidLoadSource.IndexOf("ConfigureSandbox();", StringComparison.Ordinal) <
            viewDidLoadSource.IndexOf("IosAssetHost.BindPlayerIfConfigured(", StringComparison.Ordinal));
        Assert.True(
            viewDidLoadSource.IndexOf("IosAssetHost.BindPlayerIfConfigured(", StringComparison.Ordinal) <
            viewDidLoadSource.IndexOf("_host.AttachLayer(", StringComparison.Ordinal));
        Assert.Contains("NSBundle.MainBundle", playerSource, StringComparison.Ordinal);
        Assert.Contains("NSSearchPathDirectory.DocumentDirectory", playerSource, StringComparison.Ordinal);
        Assert.Contains("NSSearchPathDirectory.CachesDirectory", playerSource, StringComparison.Ordinal);
        Assert.Contains("GetTemporaryDirectory", playerSource, StringComparison.Ordinal);
        Assert.Contains("ConfigureSandbox", playerSource, StringComparison.Ordinal);
        Assert.Contains("ConfigureAudioSession", playerSource, StringComparison.Ordinal);
        Assert.Contains("AVAudioSessionCategory.Playback", playerSource, StringComparison.Ordinal);
        Assert.Contains("AVAudioSession.InterruptionNotification", playerSource, StringComparison.Ordinal);
        Assert.Contains("HandleAudioInterruption", playerSource, StringComparison.Ordinal);
        Assert.Contains("InitializeAudio", playerSource, StringComparison.Ordinal);
        Assert.Contains("RegisterMiniAudioResolver", playerSource, StringComparison.Ordinal);
        Assert.Contains("libminiaudioex.dylib", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UIBackgroundModes", infoPlist, StringComparison.Ordinal);
        Assert.DoesNotContain("miniaudioex.dll", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("win-x64", projectText, StringComparison.Ordinal);
        Assert.False(first.NativeAudioPackaged);
        Assert.True(File.Exists(Path.Combine(firstRoot, "verification", "ios-native-audio.json")));
        Assert.Contains("BundleResource Include=\"Content\\**\\*\"", projectText, StringComparison.Ordinal);
        Assert.Contains("BundleResource Include=\"PrivacyInfo.xcprivacy\"", projectText, StringComparison.Ordinal);
        Assert.Contains("<EnableCodeSigning>true</EnableCodeSigning>", projectText, StringComparison.Ordinal);
        Assert.Contains("<CodesignKey>Apple Development</CodesignKey>", projectText, StringComparison.Ordinal);
        Assert.Contains("<EnableAutomaticProvisioning>true</EnableAutomaticProvisioning>", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("adb", projectText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ios-deploy", projectText, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(firstRoot, "Player", "PrivacyInfo.xcprivacy")));
        string privacy = await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "PrivacyInfo.xcprivacy"));
        Assert.Contains("NSPrivacyTracking", privacy, StringComparison.Ordinal);
        Assert.True(Directory.Exists(first.ContentDirectory));
        Assert.True(File.Exists(Path.Combine(first.ContentDirectory, "ios-asset-layout.json")));
        Assert.True(Directory.Exists(Path.Combine(first.ContentDirectory, "Shaders")));
        Assert.DoesNotContain("Assembly.LoadFrom", playerSource, StringComparison.Ordinal);
        Assert.Contains("UIImpactFeedbackGenerator", playerSource, StringComparison.Ordinal);
        Assert.Contains("StopDisplayLink", playerSource, StringComparison.Ordinal);
        Assert.Contains("layerClass", metalViewSource, StringComparison.Ordinal);
        Assert.Contains("CAMetalLayer", metalViewSource, StringComparison.Ordinal);
        Assert.Contains("DrawableSize", metalViewSource, StringComparison.Ordinal);
        Assert.Contains("NativeScale", metalViewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Assembly.LoadFrom", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Silk.NET.Metal", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpMetal", playerSource, StringComparison.Ordinal);

        using JsonDocument ownership = JsonDocument.Parse(await File.ReadAllTextAsync(first.OwnershipManifestPath));
        Assert.Equal(1, ownership.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ios", ownership.RootElement.GetProperty("platform").GetString());
        Assert.Equal("ios-arm64", ownership.RootElement.GetProperty("runtimeIdentifier").GetString());
        Assert.False(ownership.RootElement.GetProperty("nativeAudioPackaged").GetBoolean());
    }

    [Fact]
    public async Task GenerateAsync_IncludesEveryManagedAssemblyInTheAotProject()
    {
        Directory.CreateDirectory(_testRoot);
        string output = Path.Combine(_testRoot, "with-runtime-closure");
        IosPlayerProjectOptions options = CreateOptions(includeEcho: true);

        IosPlayerProjectLayout layout = await IosPlayerProjectGenerator.GenerateAsync(output, options);

        XDocument project = XDocument.Load(layout.ProjectPath);
        XElement[] references = project.Descendants("Reference").ToArray();
        Assert.Equal(2, references.Length);
        Assert.Contains(references, reference => reference.Attribute("Include")?.Value == "XEngine.Runtime");
        Assert.Contains(references, reference => reference.Attribute("Include")?.Value == "Echo");
        Assert.True(File.Exists(Path.Combine(output, "Player", "Managed", "XEngine.Runtime.dll")));
        Assert.True(File.Exists(Path.Combine(output, "Player", "Managed", "Echo.dll")));
        Assert.Contains("<TrimmerRootAssembly Include=\"Echo\" />", await File.ReadAllTextAsync(layout.ProjectPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ConfiguresBundleBackendForIosAssetBundles()
    {
        Directory.CreateDirectory(_testRoot);
        string output = Path.Combine(_testRoot, "asset-bundle-player");
        IosPlayerProjectOptions options = CreateOptions(
            packagingMode: AssetPackagingMode.AssetBundle);

        IosPlayerProjectLayout layout = await IosPlayerProjectGenerator.GenerateAsync(output, options);

        string playerSource = await File.ReadAllTextAsync(Path.Combine(output, "Player", "IosPlayer.cs"));
        Assert.Contains("AssetPackagingMode.AssetBundle", playerSource, StringComparison.Ordinal);
        Assert.Contains("new Guid(\"00000000-0000-0000-0000-000000000000\")", playerSource, StringComparison.Ordinal);
        AssertNoSyntaxErrors(playerSource);
        Assert.True(File.Exists(layout.ProjectPath));
    }

    [Fact]
    public async Task GenerateAsync_PackagesIosArm64MiniaudioWhenPresent()
    {
        Directory.CreateDirectory(_testRoot);
        string nativeRoot = Path.Combine(_testRoot, "Libraries");
        string source = IosNativeAudioContract.GetSourcePath(nativeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(
            source,
            IosNativeAudioContractTests.CreateThinDylib(
                IosMachOInspector.CpuTypeArm64,
                IosMachOInspector.RequiredAudioSymbols));
        string output = Path.Combine(_testRoot, "with-audio");
        IosPlayerProjectOptions options = CreateOptions(nativeRoot);

        IosPlayerProjectLayout layout = await IosPlayerProjectGenerator.GenerateAsync(output, options);

        Assert.True(layout.NativeAudioPackaged);
        Assert.True(File.Exists(IosNativeAudioContract.GetPackagedPath(Path.Combine(output, "Player"))));
        string projectText = await File.ReadAllTextAsync(layout.ProjectPath);
        Assert.Contains("NativeReference Include=\"NativeLibraries\\libminiaudioex.dylib\"", projectText, StringComparison.Ordinal);
        Assert.Contains("<Kind>Dynamic</Kind>", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("miniaudioex.dll", projectText, StringComparison.Ordinal);
        using JsonDocument ownership = JsonDocument.Parse(await File.ReadAllTextAsync(layout.OwnershipManifestPath));
        Assert.True(ownership.RootElement.GetProperty("nativeAudioPackaged").GetBoolean());
    }

    [Fact]
    public async Task GenerateAsync_PackagesSimulatorSpecificMiniaudio()
    {
        Directory.CreateDirectory(_testRoot);
        string nativeRoot = Path.Combine(_testRoot, "Libraries");
        string deviceSource = IosNativeAudioContract.GetSourcePath(nativeRoot);
        string simulatorSource = IosNativeAudioContract.GetSourcePath(nativeRoot, "iossimulator-arm64");
        Directory.CreateDirectory(Path.GetDirectoryName(deviceSource)!);
        Directory.CreateDirectory(Path.GetDirectoryName(simulatorSource)!);
        await File.WriteAllBytesAsync(
            deviceSource,
            IosNativeAudioContractTests.CreateThinDylib(
                IosMachOInspector.CpuTypeArm64,
                IosMachOInspector.RequiredAudioSymbols,
                IosMachOInspector.PlatformIos));
        byte[] simulatorBytes = IosNativeAudioContractTests.CreateThinDylib(
            IosMachOInspector.CpuTypeArm64,
            IosMachOInspector.RequiredAudioSymbols,
            IosMachOInspector.PlatformIosSimulator);
        await File.WriteAllBytesAsync(simulatorSource, simulatorBytes);

        string output = Path.Combine(_testRoot, "simulator-with-audio");
        IosPlayerProjectOptions options = CreateOptions(nativeRoot, runtimeIdentifier: "iossimulator-arm64");
        IosPlayerProjectLayout layout = await IosPlayerProjectGenerator.GenerateAsync(output, options);

        Assert.True(layout.NativeAudioPackaged);
        Assert.Equal("iossimulator-arm64", Property(XDocument.Load(layout.ProjectPath), "RuntimeIdentifier"));
        Assert.Equal("true", Property(XDocument.Load(layout.ProjectPath), "UseInterpreter"));
        string simulatorPlayerSource = await File.ReadAllTextAsync(Path.Combine(output, "Player", "IosPlayer.cs"));
        Assert.Contains("Simulator audio initialization skipped", simulatorPlayerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_host.InitializeAudio(44100, 2048);", simulatorPlayerSource, StringComparison.Ordinal);
        Assert.Equal(simulatorBytes, await File.ReadAllBytesAsync(
            IosNativeAudioContract.GetPackagedPath(Path.Combine(output, "Player"))));
        string projectText = await File.ReadAllTextAsync(layout.ProjectPath);
        Assert.Contains("<RelativePath>libminiaudioex.dylib</RelativePath>", projectText, StringComparison.Ordinal);
        using JsonDocument verification = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(output, "verification", IosNativeAudioContract.VerificationFileName)));
        Assert.Equal("iossimulator-arm64", verification.RootElement.GetProperty("runtimeIdentifier").GetString());
    }

    [Fact]
    public void CollectSettingsAssetGuids_FindsNestedAssetReferences()
    {
        Guid pipelineGuid = Guid.Parse("a7c40002-09e0-4d2f-9f7f-5a5c5e5a0002");
        Guid globalSettingsGuid = Guid.Parse("a7c40003-09e0-4d2f-9f7f-5a5c5e5a0003");
        var settings = XEngine.Echo.EchoObject.ReadFromString(
            "{\"Graphics\":{\"DefaultPipeline\":{\"AssetID\":\"" + pipelineGuid + "\"}," +
            "\"PipelineGlobalSettings\":{\"AssetID\":\"" + globalSettingsGuid + "\"}}," +
            "\"Quality\":{\"PreviewPipeline\":{\"AssetID\":\"00000000-0000-0000-0000-000000000000\"}}}");

        HashSet<Guid> guids = IosAssetPackager.CollectSettingsAssetGuids([settings]);

        Assert.Equal(2, guids.Count);
        Assert.Contains(pipelineGuid, guids);
        Assert.Contains(globalSettingsGuid, guids);
    }

    [Fact]
    public async Task GenerateAsync_RejectsNonEmptyDirectory()
    {
        Directory.CreateDirectory(_testRoot);
        await File.WriteAllTextAsync(Path.Combine(_testRoot, "stale.txt"), "nope");
        await Assert.ThrowsAsync<IOException>(() =>
            IosPlayerProjectGenerator.GenerateAsync(_testRoot, CreateOptions()));
    }

    [Fact]
    public async Task GenerateAsync_SafeWritesLinkerDescriptorAndDynamicDependencies()
    {
        Directory.CreateDirectory(_testRoot);
        string output = Path.Combine(_testRoot, "safe");
        var roots = new IosManagedTypeRootCollection(
        [
            new IosManagedTypeRoot(
                "XEngine.Runtime",
                "XEngine.Runtime.GameObject",
                [new IosManagedTypeRootReason(Guid.Empty, "Test.scene", "serialized-$type")]),
        ]);
        IosPlayerProjectOptions options = CreateOptions(
            managedStripping: IosManagedStrippingMode.Safe,
            managedTypeRoots: roots);

        IosPlayerProjectLayout layout = await IosPlayerProjectGenerator.GenerateAsync(output, options);

        string projectText = await File.ReadAllTextAsync(layout.ProjectPath);
        Assert.Contains("XEngineManagedStrippingMode>Safe", projectText, StringComparison.Ordinal);
        Assert.Contains("TrimmerRootDescriptor Include=\"trimming/XEngine.iOS.linker.xml\"", projectText, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "Player", "trimming", IosManagedTrimRoots.DescriptorFileName)));
        string rootsSource = await File.ReadAllTextAsync(Path.Combine(
            output,
            "Player",
            IosPlayerProjectGenerator.PlayerRootsSourceFileName));
        Assert.Contains("DynamicDependency", rootsSource, StringComparison.Ordinal);
        Assert.Contains("XEngine.Runtime.GameObject", rootsSource, StringComparison.Ordinal);
        using JsonDocument report = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "trimming", IosManagedTypeRootReport.FileName)));
        Assert.Equal(1, report.RootElement.GetProperty("typeCount").GetInt32());
    }

    [Fact]
    public async Task GenerateAsync_FullTrimmingIsBlocked()
    {
        Directory.CreateDirectory(_testRoot);
        IosPlayerProjectOptions options = CreateOptions(managedStripping: IosManagedStrippingMode.Full);
        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            IosPlayerProjectGenerator.GenerateAsync(Path.Combine(_testRoot, "full"), options));
        Assert.Equal(IosManagedTrimRoots.FullBlockedError, ex.Message);
    }

    [Fact]
    public void GetAssemblyName_SanitizesProjectName()
    {
        Assert.Equal("My_Game.iOS", IosPlayerProjectGenerator.GetAssemblyName("My Game"));
        Assert.Equal("_1Title.iOS", IosPlayerProjectGenerator.GetAssemblyName("1Title"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static IosPlayerProjectOptions CreateOptions(
        string? nativeLibraryRoot = null,
        IosManagedStrippingMode managedStripping = IosManagedStrippingMode.Disabled,
        IosManagedTypeRootCollection? managedTypeRoots = null,
        bool includeEcho = false,
        string runtimeIdentifier = "ios-arm64",
        AssetPackagingMode packagingMode = AssetPackagingMode.LooseFiles) => new()
    {
        ProjectName = "My Game",
        ApplicationId = "com.xengine.testgame",
        ApplicationLabel = "My Game",
        VersionName = "1.2.3",
        VersionCode = 7,
        RuntimeIdentifier = runtimeIdentifier,
        SupportedOSPlatformVersion = "16.0",
        PreferredFramesPerSecond = 60,
        PackagingMode = packagingMode,
        NativeLibraryRoot = nativeLibraryRoot,
        ManagedStripping = managedStripping,
        ManagedTypeRoots = managedTypeRoots ?? IosManagedTypeRootCollection.Empty,
        ManagedAssemblies = includeEcho
            ?
            [
                new IosManagedAssemblyInput(
                    "XEngine.Runtime",
                    typeof(EngineObject).Assembly.Location,
                    "engine-runtime",
                    "test Runtime root"),
                new IosManagedAssemblyInput(
                    "Echo",
                    typeof(XEngine.Echo.EchoObject).Assembly.Location,
                    "engine-runtime",
                    "test Runtime dependency"),
            ]
            :
            [
                new IosManagedAssemblyInput(
                    "XEngine.Runtime",
                    typeof(EngineObject).Assembly.Location,
                    "engine-runtime",
                    "test Runtime root"),
            ],
    };

    private static string Property(XDocument project, string name) =>
        project.Descendants(name).Single().Value;

    private static void AssertNoSyntaxErrors(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        foreach (Diagnostic diagnostic in tree.GetDiagnostics())
            Assert.NotEqual(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    private static IReadOnlyList<string> EnumerateFileBytes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + ":" + Convert.ToBase64String(File.ReadAllBytes(path)))
            .ToArray();
}
