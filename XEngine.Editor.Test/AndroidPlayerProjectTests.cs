// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Android;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidPlayerProjectTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineAndroidPlayerProjectTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("arm64-v8a", "android-arm64")]
    [InlineData("armeabi-v7a", "android-arm")]
    [InlineData("x86_64", "android-x64")]
    public void AbiMappingIsExplicitAndReversible(string abi, string runtimeIdentifier)
    {
        Assert.Equal(runtimeIdentifier, AndroidAbiMapper.ToRuntimeIdentifier(abi));
        Assert.Equal(abi, AndroidAbiMapper.ToAbi(runtimeIdentifier));
    }

    [Fact]
    public void AbiMappingRejectsUnknownValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AndroidAbiMapper.ToRuntimeIdentifier("x86"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AndroidAbiMapper.ToAbi("linux-arm64"));
    }

    [Theory]
    [InlineData(AndroidOutputMode.ExportProject, "apk")]
    [InlineData(AndroidOutputMode.Apk, "apk")]
    [InlineData(AndroidOutputMode.Aab, "aab")]
    public async Task GeneratorCreatesStandaloneDeterministicProject(AndroidOutputMode mode, string packageFormat)
    {
        Directory.CreateDirectory(_testRoot);
        string lockPath = Path.Combine(_testRoot, "source-lock.json");
        await File.WriteAllTextAsync(lockPath, "{\"fingerprint\":\"test\"}\n");
        AndroidPlayerProjectOptions options = CreateOptions(mode, lockPath);
        string firstRoot = Path.Combine(_testRoot, "first");
        string secondRoot = Path.Combine(_testRoot, "second");

        AndroidPlayerProjectLayout first = await AndroidPlayerProjectGenerator.GenerateAsync(firstRoot, options);
        AndroidPlayerProjectLayout second = await AndroidPlayerProjectGenerator.GenerateAsync(secondRoot, options);

        Assert.Equal(["arm64-v8a"], first.TargetAbis);
        Assert.Equal(24, first.MinSdkVersion);
        Assert.Equal(["android-arm64"], first.RuntimeIdentifiers);
        Assert.True(File.Exists(first.ProjectPath));
        Assert.True(File.Exists(first.ManifestPath));
        Assert.True(File.Exists(first.ToolchainLockPath));
        Assert.Equal(
            EnumerateFileBytes(firstRoot),
            EnumerateFileBytes(secondRoot));

        XDocument project = XDocument.Load(first.ProjectPath);
        Assert.Equal("net10.0-android", Property(project, "TargetFramework"));
        Assert.Equal("android-36", Property(project, "AndroidSdkPlatformVersion"));
        Assert.Equal(packageFormat, Property(project, "AndroidPackageFormat"));
        Assert.Equal("android-arm64", Property(project, "RuntimeIdentifier"));
        Assert.Equal("Safe", Property(project, "XEngineManagedStrippingMode"));
        Assert.Equal("false", Property(project, "AndroidUseDebugRuntime"));
        XElement publishTrimmed = project.Descendants("PublishTrimmed").Single();
        Assert.Equal("true", publishTrimmed.Value);
        Assert.Equal("'$(Configuration)' != 'Debug'", publishTrimmed.Attribute("Condition")!.Value);
        XElement trimMode = project.Descendants("TrimMode").Single();
        Assert.Equal("partial", trimMode.Value);
        Assert.Equal("'$(Configuration)' != 'Debug'", trimMode.Attribute("Condition")!.Value);
        Assert.Equal("true", Property(project, "ILLinkTreatWarningsAsErrors"));
        XElement r8Properties = Assert.Single(project.Descendants("PropertyGroup"), element =>
            element.Attribute("Condition")?.Value == "'$(Configuration)' == 'Release'");
        Assert.Equal("r8", r8Properties.Element("AndroidLinkTool")!.Value);
        Assert.Equal("true", r8Properties.Element("AndroidCreateProguardMappingFile")!.Value);
        Assert.Equal("false", r8Properties.Element("AndroidR8IgnoreWarnings")!.Value);
        Assert.Contains(project.Descendants("ProguardConfiguration"), element =>
            element.Attribute("Include")?.Value == "r8/xengine-r8.rules");
        string r8Rules = await File.ReadAllTextAsync(Path.Combine(firstRoot, "r8", "xengine-r8.rules"));
        Assert.Contains("-keep class mono.android.**", r8Rules, StringComparison.Ordinal);
        Assert.Contains("-keep class crc**.**", r8Rules, StringComparison.Ordinal);
        Assert.Contains(
            project.Descendants("TrimmerRootDescriptor"),
            element => element.Attribute("Include")?.Value ==
                "trimming/" + AndroidManagedTrimRoots.DescriptorFileName);
        Assert.Equal("XEngine.Generated.Android", Property(project, "RootNamespace"));
        XElement runtimeReference = Assert.Single(project.Descendants("Reference"));
        Assert.Equal("XEngine.Runtime", runtimeReference.Attribute("Include")!.Value);
        Assert.Equal("Managed/XEngine.Runtime.dll", runtimeReference.Element("HintPath")!.Value);
        Assert.False(Path.IsPathRooted(runtimeReference.Element("HintPath")!.Value));
        Assert.True(File.Exists(Path.Combine(firstRoot, "Managed", "XEngine.Runtime.dll")));
        string dependencyReportPath = Path.Combine(
            firstRoot,
            "dependencies",
            "android-managed-dependencies.json");
        Assert.True(File.Exists(dependencyReportPath));
        string trimmingReportPath = Path.Combine(firstRoot, "trimming", "android-trimming.json");
        Assert.True(File.Exists(trimmingReportPath));
        string trimDescriptorPath = Path.Combine(
            firstRoot,
            "trimming",
            AndroidManagedTrimRoots.DescriptorFileName);
        Assert.True(File.Exists(trimDescriptorPath));
        string trimDescriptor = await File.ReadAllTextAsync(trimDescriptorPath);
        Assert.Contains(
            "fullname=\"" + AndroidPlayerProjectGenerator.GetAssemblyName("My Game") + "\"",
            trimDescriptor,
            StringComparison.Ordinal);
        Assert.Contains("fullname=\"XEngine.Runtime\"", trimDescriptor, StringComparison.Ordinal);
        string playerRootsSource = await File.ReadAllTextAsync(Path.Combine(
            firstRoot,
            AndroidManagedTrimRoots.SourceFileName));
        Assert.Contains("internal static void Preserve()", playerRootsSource, StringComparison.Ordinal);
        string managedTypeRootsReport = Path.Combine(
            firstRoot,
            "trimming",
            AndroidManagedTypeRootReport.FileName);
        Assert.True(File.Exists(managedTypeRootsReport));
        using JsonDocument trimmingReport = JsonDocument.Parse(await File.ReadAllTextAsync(trimmingReportPath));
        Assert.Equal(5, trimmingReport.RootElement.GetProperty("categories").GetArrayLength());
        string generatedSource = await File.ReadAllTextAsync(Path.Combine(firstRoot, "MainActivity.cs"));
        Assert.Contains("XEnginePlayerRoots.Preserve();", generatedSource, StringComparison.Ordinal);
        Assert.Contains("BuildIdIntentExtra = \"xengine.build-id\"", generatedSource, StringComparison.Ordinal);
        Assert.Contains("LogDeploymentBuildId(Intent);", generatedSource, StringComparison.Ordinal);
        Assert.Contains("\"build-id=\" + buildId", generatedSource, StringComparison.Ordinal);
        Assert.Contains("new XEngineSurfaceView(this, _host)", generatedSource, StringComparison.Ordinal);
        Assert.Contains("ISurfaceHolderCallback", generatedSource, StringComparison.Ordinal);
        Assert.Contains("Choreographer.IFrameCallback", generatedSource, StringComparison.Ordinal);
        Assert.Contains("ANativeWindow_fromSurface", generatedSource, StringComparison.Ordinal);
        Assert.Contains("OnTouchEvent(MotionEvent? e)", generatedSource, StringComparison.Ordinal);
        Assert.Contains("GetHistoricalX(pointerIndex, historyIndex)", generatedSource, StringComparison.Ordinal);
        Assert.Contains("GetHistoricalPressure(pointerIndex, historyIndex)", generatedSource, StringComparison.Ordinal);
        Assert.Contains("_host.TouchHistory(", generatedSource, StringComparison.Ordinal);
        Assert.Contains("ConfigureEdgeToEdge();", generatedSource, StringComparison.Ordinal);
        Assert.Contains("ScreenOrientation = global::Android.Content.PM.ScreenOrientation.Unspecified", generatedSource, StringComparison.Ordinal);
        Assert.Contains("SetDecorFitsSystemWindows(false)", generatedSource, StringComparison.Ordinal);
        Assert.Contains("OnApplyWindowInsets", generatedSource, StringComparison.Ordinal);
        Assert.Contains("StableInsetLeft", generatedSource, StringComparison.Ordinal);
        Assert.Contains("DisplayCutout", generatedSource, StringComparison.Ordinal);
        Assert.Contains("GetInsetsIgnoringVisibility", generatedSource, StringComparison.Ordinal);
        Assert.Contains("StatusBarVisibility", generatedSource, StringComparison.Ordinal);
        Assert.Contains("_host.UpdateWindowInsets(", generatedSource, StringComparison.Ordinal);
        Assert.Contains("_host.UpdateInputViewSize(", generatedSource, StringComparison.Ordinal);
        Assert.Contains("window insets={left},{top},{right},{bottom}", generatedSource, StringComparison.Ordinal);
        Assert.Contains("OnCreateInputConnection(EditorInfo? outAttrs)", generatedSource, StringComparison.Ordinal);
        Assert.Contains("LastAndroidInputType", generatedSource, StringComparison.Ordinal);
        Assert.Contains("RestartInput(_surfaceView)", generatedSource, StringComparison.Ordinal);
        Assert.Contains("(global::Android.Views.InputMethods.ImeFlags)ImeAction.Done", generatedSource, StringComparison.Ordinal);
        Assert.Contains("BaseInputConnection", generatedSource, StringComparison.Ordinal);
        Assert.Contains("DispatchKeyEvent(KeyEvent? e)", generatedSource, StringComparison.Ordinal);
        Assert.Contains("DispatchGenericMotionEvent(MotionEvent? e)", generatedSource, StringComparison.Ordinal);
        Assert.Contains("InputManager.IInputDeviceListener", generatedSource, StringComparison.Ordinal);
        Assert.Contains("RegisterInputDeviceListener", generatedSource, StringComparison.Ordinal);
        Assert.Contains("InputDevice.GetDeviceIds()", generatedSource, StringComparison.Ordinal);
        Assert.Contains("HandleGamepadMotion", generatedSource, StringComparison.Ordinal);
        Assert.Contains("IOnBackInvokedCallback", generatedSource, StringComparison.Ordinal);
        Assert.Contains("AudioManager.IOnAudioFocusChangeListener", generatedSource, StringComparison.Ordinal);
        Assert.Contains("RequestAudioFocus", generatedSource, StringComparison.Ordinal);
        Assert.Contains("AudioFocus.LossTransient", generatedSource, StringComparison.Ordinal);
        Assert.Contains("RegisterAudioDeviceCallback", generatedSource, StringComparison.Ordinal);
        Assert.Contains("RegisterOnBackInvokedCallback", generatedSource, StringComparison.Ordinal);
        Assert.Contains("PlatformServices.Configure", generatedSource, StringComparison.Ordinal);
        Assert.Contains("GetNetworkReachability", generatedSource, StringComparison.Ordinal);
        Assert.Contains("AndroidPermissionRequestCoordinator", generatedSource, StringComparison.Ordinal);
        Assert.Contains("RequestPermissions([permission]", generatedSource, StringComparison.Ordinal);
        Assert.Contains("ShouldShowPermissionRationale", generatedSource, StringComparison.Ordinal);
        Assert.Contains("PlatformPermissionStatus.DeniedPermanently", generatedSource, StringComparison.Ordinal);
        Assert.Contains("PlatformPermissionStatus.NotDeclared", generatedSource, StringComparison.Ordinal);
        Assert.Contains("Settings.ActionApplicationDetailsSettings", generatedSource, StringComparison.Ordinal);
        Assert.Contains("permission status name={permission}", generatedSource, StringComparison.Ordinal);
        Assert.Contains("TryVibrate(TimeSpan duration)", generatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("OnBackPressed", generatedSource, StringComparison.Ordinal);
        Assert.Contains("_host!.AttachSurface", generatedSource, StringComparison.Ordinal);
        Assert.Contains("_host?.DetachSurface", generatedSource, StringComparison.Ordinal);
        Assert.Contains("_host?.Tick(frameTimeNanos)", generatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Vulkan host pending", generatedSource, StringComparison.Ordinal);

        XDocument manifest = XDocument.Load(first.ManifestPath);
        XNamespace android = "http://schemas.android.com/apk/res/android";
        XElement application = Assert.Single(manifest.Root!.Elements("application"));
        Assert.Equal("false", application.Attribute(android + "allowBackup")!.Value);
        Assert.Equal("true", application.Attribute(android + "enableOnBackInvokedCallback")!.Value);
        Assert.Equal("@mipmap/ic_launcher", application.Attribute(android + "icon")!.Value);
        Assert.Equal("@mipmap/ic_launcher", application.Attribute(android + "roundIcon")!.Value);
        Assert.Equal("@style/Theme.XEngine.Splash", application.Attribute(android + "theme")!.Value);
        Assert.Equal("true", application.Attribute(android + "resizeableActivity")!.Value);
        Assert.Equal("false", application.Attribute(android + "usesCleartextTraffic")!.Value);
        Assert.Contains(manifest.Root.Elements("uses-permission"), permission =>
            permission.Attribute(android + "name")!.Value == "android.permission.ACCESS_NETWORK_STATE");
        Assert.Contains(manifest.Root.Elements("uses-permission"), permission =>
            permission.Attribute(android + "name")!.Value == "android.permission.VIBRATE");
        int permissionCount = 0;
        foreach (XElement _ in manifest.Root.Elements("uses-permission"))
            permissionCount++;
        Assert.Equal(2, permissionCount);
        XElement vulkan = Assert.Single(manifest.Root.Elements("uses-feature"));
        Assert.Equal("android.hardware.vulkan.version", vulkan.Attribute(android + "name")!.Value);
        Assert.Equal("4198400", vulkan.Attribute(android + "version")!.Value);
        Assert.Equal(
            EmbeddedBrandingPng(),
            await File.ReadAllBytesAsync(Path.Combine(firstRoot, "Resources", "mipmap", "ic_launcher.png")));
        string baseStyles = await File.ReadAllTextAsync(Path.Combine(firstRoot, "Resources", "values", "styles.xml"));
        Assert.Contains("Theme.XEngine.App", baseStyles, StringComparison.Ordinal);
        Assert.Contains("Theme.XEngine.Splash", baseStyles, StringComparison.Ordinal);
        // The locked Unity-derived SDK trims Theme.SplashScreen from its platform jars, so the
        // generator must not emit a values-v31 splash override (aapt2 link fails with APT2260).
        Assert.False(File.Exists(Path.Combine(firstRoot, "Resources", "values-v31", "styles.xml")));

        using JsonDocument ownership = JsonDocument.Parse(await File.ReadAllTextAsync(first.OwnershipManifestPath));
        Assert.Equal(1, ownership.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("0123456789ABCDEF", ownership.RootElement.GetProperty("toolchainFingerprint").GetString());
        foreach (JsonElement file in ownership.RootElement.GetProperty("generatedFiles").EnumerateArray())
        {
            string path = file.GetProperty("path").GetString()!;
            Assert.False(Path.IsPathRooted(path));
            Assert.DoesNotContain('\\', path);
            Assert.Equal(64, file.GetProperty("sha256").GetString()!.Length);
        }
    }

    [Fact]
    public async Task GeneratorEmitsDisplayProfileIntoActivityAndManifest()
    {
        AndroidPlayerProjectOptions options = CreateOptions(
            AndroidOutputMode.ExportProject,
            screenOrientation: AndroidScreenOrientation.Landscape,
            resizableActivity: false);
        string output = Path.Combine(_testRoot, "display-profile");

        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(output, options);

        string source = await File.ReadAllTextAsync(Path.Combine(output, "MainActivity.cs"));
        Assert.Contains(
            "ScreenOrientation = global::Android.Content.PM.ScreenOrientation.Landscape",
            source,
            StringComparison.Ordinal);

        XDocument manifest = XDocument.Load(layout.ManifestPath);
        XNamespace android = "http://schemas.android.com/apk/res/android";
        XElement application = Assert.Single(manifest.Root!.Elements("application"));
        Assert.Equal("false", application.Attribute(android + "resizeableActivity")!.Value);
    }

    [Fact]
    public async Task GeneratorRejectsUnknownScreenOrientation()
    {
        AndroidPlayerProjectOptions options = CreateOptions(
            AndroidOutputMode.ExportProject,
            screenOrientation: (AndroidScreenOrientation)999);

        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            AndroidPlayerProjectGenerator.GenerateAsync(Path.Combine(_testRoot, "invalid-orientation"), options));

        Assert.Contains("screen orientation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeneratorAppliesPackagedOrientationBeforePlatformSurfaceLoop()
    {
        string payload = Path.Combine(_testRoot, "orientation-payload");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(Path.Combine(payload, "Reports"));
        await File.WriteAllTextAsync(
            Path.Combine(payload, "Reports", "asset-stripping.json"),
            "{}\n");
        await File.WriteAllTextAsync(
            Path.Combine(payload, "android-assets.json"),
            "{\"assetStrippingReport\":\"Reports/asset-stripping.json\",\"assets\":[]}");

        AndroidPlayerProjectOptions options = CreateOptions(
            AndroidOutputMode.ExportProject,
            assetPayloadDirectory: payload);
        string output = Path.Combine(_testRoot, "orientation-order");

        await AndroidPlayerProjectGenerator.GenerateAsync(output, options);

        string source = await File.ReadAllTextAsync(Path.Combine(output, "MainActivity.cs"));
        int services = source.IndexOf("_platformServicesRegistration = ConfigurePlatformServices();", StringComparison.Ordinal);
        int orientation = source.IndexOf("PlayerSettingsLoader.ApplyOrientation(_assetProvider!);", StringComparison.Ordinal);
        int hostLoop = source.IndexOf("_game.InitializePlatformHost", StringComparison.Ordinal);
        Assert.True(services >= 0 && orientation > services && hostLoop > orientation);
    }

    [Fact]
    public async Task GeneratorNormalizesAdditionalPermissionsIntoManifestAndOwnershipReport()
    {
        AndroidPlayerProjectOptions options = CreateOptions(
            AndroidOutputMode.ExportProject,
            additionalPermissions:
            [
                " android.permission.CAMERA ",
                "android.permission.CAMERA",
                "android.permission.VIBRATE",
                "com.example.game.permission.MULTIPLAYER",
            ]);
        string output = Path.Combine(_testRoot, "additional-permissions");

        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(output, options);

        XDocument manifest = XDocument.Load(layout.ManifestPath);
        XNamespace android = "http://schemas.android.com/apk/res/android";
        var names = new List<string>();
        foreach (XElement permissionElement in manifest.Root!.Elements("uses-permission"))
            names.Add(permissionElement.Attribute(android + "name")!.Value);
        Assert.Equal(
        [
            "android.permission.ACCESS_NETWORK_STATE",
            "android.permission.CAMERA",
            "android.permission.VIBRATE",
            "com.example.game.permission.MULTIPLAYER",
        ], names);

        using JsonDocument ownership = JsonDocument.Parse(await File.ReadAllTextAsync(layout.OwnershipManifestPath));
        var recorded = new List<string>();
        foreach (JsonElement permissionElement in ownership.RootElement.GetProperty("permissions").EnumerateArray())
            recorded.Add(permissionElement.GetString()!);
        Assert.Equal(names, recorded);

        string source = await File.ReadAllTextAsync(Path.Combine(output, "MainActivity.cs"));
        Assert.Contains("android.permission.CAMERA", source, StringComparison.Ordinal);
        Assert.Contains("com.example.game.permission.MULTIPLAYER", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CAMERA")]
    [InlineData("android.permission.BAD-NAME")]
    public async Task GeneratorRejectsInvalidAdditionalPermission(string permission)
    {
        AndroidPlayerProjectOptions options = CreateOptions(
            AndroidOutputMode.ExportProject,
            additionalPermissions: [permission]);

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            AndroidPlayerProjectGenerator.GenerateAsync(
                Path.Combine(_testRoot, "invalid-permission-" + Guid.NewGuid().ToString("N")),
                options));

        Assert.Contains("permission", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeneratorIncludesDevelopmentShaderTraceOnlyWhenEnabled()
    {
        Directory.CreateDirectory(_testRoot);
        string lockPath = Path.Combine(_testRoot, "trace-lock.json");
        await File.WriteAllTextAsync(lockPath, "{\"fingerprint\":\"test\"}\n");

        string developmentRoot = Path.Combine(_testRoot, "development");
        await AndroidPlayerProjectGenerator.GenerateAsync(
            developmentRoot,
            CreateOptions(AndroidOutputMode.ExportProject, lockPath, enableDevelopmentShaderTrace: true));
        string releaseRoot = Path.Combine(_testRoot, "release");
        await AndroidPlayerProjectGenerator.GenerateAsync(
            releaseRoot,
            CreateOptions(AndroidOutputMode.ExportProject, lockPath));

        string developmentSource = await File.ReadAllTextAsync(Path.Combine(developmentRoot, "MainActivity.cs"));
        Assert.Contains("ShaderArtifactRuntimeLibrary.EnableDevelopmentUsageTrace()", developmentSource, StringComparison.Ordinal);
        Assert.Contains("ShaderArtifactUsageTrace.FileName", developmentSource, StringComparison.Ordinal);
        Assert.Contains("_shaderUsageTrace?.Dispose();", developmentSource, StringComparison.Ordinal);

        string releaseSource = await File.ReadAllTextAsync(Path.Combine(releaseRoot, "MainActivity.cs"));
        Assert.DoesNotContain("EnableDevelopmentUsageTrace", releaseSource, StringComparison.Ordinal);
        Assert.DoesNotContain("shader usage trace=", releaseSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratorPackagesOnlyAvailableMiniaudioAbis()
    {
        string nativeRoot = Path.Combine(_testRoot, "native-source");
        string source = Path.Combine(nativeRoot, "android-arm64", "native", "libminiaudioex.so");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, [0x7f, 0x45, 0x4c, 0x46]);
        AndroidPlayerProjectOptions options = CreateOptions(
            AndroidOutputMode.ExportProject,
            targetAbis: ["arm64-v8a", "x86_64"],
            nativeLibraryRoot: nativeRoot);
        string output = Path.Combine(_testRoot, "native-project");

        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(output, options);

        Assert.Equal(["arm64-v8a"], layout.NativeAudioAbis);
        Assert.True(File.Exists(Path.Combine(
            output,
            "NativeLibraries",
            "arm64-v8a",
            "libminiaudioex.so")));
        Assert.False(Directory.Exists(Path.Combine(output, "NativeLibraries", "x86_64")));
        string project = await File.ReadAllTextAsync(layout.ProjectPath);
        Assert.Contains(
            "<AndroidNativeLibrary Include=\"NativeLibraries/arm64-v8a/libminiaudioex.so\" />",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain("NativeLibraries/x86_64", project, StringComparison.Ordinal);
        using JsonDocument ownership = JsonDocument.Parse(
            await File.ReadAllTextAsync(layout.OwnershipManifestPath));
        Assert.Equal<string?>(
            ["arm64-v8a"],
            ownership.RootElement.GetProperty("nativeAudioAbis")
                .EnumerateArray()
                .Select(element => element.GetString())
                .ToArray());
    }

    [Fact]
    public async Task GeneratedMainActivityDisambiguatesAndroidEnumsAndCastsImeTypes()
    {
        // Regression pins for generated-code compile breaks that only surface when the staging
        // csproj is actually compiled: CS0104 (ScreenOrientation ambiguity between
        // Android.Content.PM and XEngine.Runtime) and CS0266 (InputTypes/ImeFlags implicit
        // conversions) in MainActivity.cs.
        Directory.CreateDirectory(_testRoot);
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(
            Path.Combine(_testRoot, "main-activity"),
            CreateOptions(AndroidOutputMode.Apk));
        string mainActivity = await File.ReadAllTextAsync(Path.Combine(layout.RootDirectory, "MainActivity.cs"));

        Assert.Contains(
            "using ScreenOrientation = global::Android.Content.PM.ScreenOrientation;",
            mainActivity,
            StringComparison.Ordinal);
        Assert.Contains(
            "using EngineScreenOrientation = XEngine.Runtime.ScreenOrientation;",
            mainActivity,
            StringComparison.Ordinal);
        Assert.Contains(
            "inputType = (int)global::Android.Text.InputTypes.ClassText;",
            mainActivity,
            StringComparison.Ordinal);
        Assert.Contains(
            "(global::Android.Views.InputMethods.ImeFlags)ImeAction.Done",
            mainActivity,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedProjectOwnsItsIntermediateOutputDirectory()
    {
        // Regression: a staging project generated inside a repo whose Directory.Build.props
        // redirects android obj/ to a shared cache (XEngine's Build/AndroidIntermediate) silently
        // detaches the intermediates the package builder cleans and inspects between the
        // unstripped and stripped passes. The export must pin its own obj/ via a local
        // Directory.Build.props so it stays self-contained wherever it is generated.
        Directory.CreateDirectory(_testRoot);
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(
            Path.Combine(_testRoot, "self-contained"),
            CreateOptions(AndroidOutputMode.Apk));
        string buildProps = await File.ReadAllTextAsync(Path.Combine(layout.RootDirectory, "Directory.Build.props"));

        Assert.Contains("<BaseIntermediateOutputPath>obj\\</BaseIntermediateOutputPath>", buildProps, StringComparison.Ordinal);
        Assert.Contains(
            "<MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>",
            buildProps,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Com.XEngine.Game")]
    [InlineData("single")]
    [InlineData("com.xengine.game-")]
    public async Task GeneratorRejectsInvalidApplicationId(string applicationId)
    {
        AndroidPlayerProjectOptions options = WithApplicationId(applicationId);

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            AndroidPlayerProjectGenerator.GenerateAsync(Path.Combine(_testRoot, Guid.NewGuid().ToString("N")), options));

        Assert.Contains("application id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeneratorRefusesNonEmptyOutput()
    {
        string output = Path.Combine(_testRoot, "non-empty");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "owned-by-user.txt"), "keep");

        await Assert.ThrowsAsync<IOException>(() => AndroidPlayerProjectGenerator.GenerateAsync(
            output,
            CreateOptions(AndroidOutputMode.ExportProject)));

        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(output, "owned-by-user.txt")));
    }

    [Fact]
    public async Task GeneratorIncludesVulkanShaderPackageAsAndroidRawResource()
    {
        string source = Path.Combine(_testRoot, "shader-package");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "catalog.xshadercatalog"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(source, "artifact.xshader"), [4, 5, 6]);

        AndroidPlayerProjectOptions options = CreateOptions(AndroidOutputMode.ExportProject, shaderPackageDirectory: source);
        string output = Path.Combine(_testRoot, "shader-project");
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(output, options);

        Assert.True(layout.IncludesShaderPackage);
        string shaderPackage = Path.Combine(output, "Resources", "raw", "android_vulkan.xshaderpack");
        Assert.True(File.Exists(shaderPackage));
        using (ZipArchive archive = ZipFile.OpenRead(shaderPackage))
        {
            Assert.NotNull(archive.GetEntry("catalog.xshadercatalog"));
            Assert.NotNull(archive.GetEntry("artifact.xshader"));
            Assert.All(archive.Entries, entry =>
                Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0), entry.LastWriteTime.DateTime));
        }
        string project = await File.ReadAllTextAsync(layout.ProjectPath);
        Assert.Contains(
            "<AndroidResource Include=\"Resources/raw/android_vulkan.xshaderpack\" />",
            project,
            StringComparison.Ordinal);
        string sourceText = await File.ReadAllTextAsync(Path.Combine(output, "MainActivity.cs"));
        Assert.Contains("Resources!.OpenRawResource(Resource.Raw.android_vulkan)", sourceText, StringComparison.Ordinal);
        Assert.Contains("new ZipAssetStreamProvider", sourceText, StringComparison.Ordinal);
        Assert.Contains("ShaderArtifactRuntimeLibrary.Configure(provider)", sourceText, StringComparison.Ordinal);

        using JsonDocument ownership = JsonDocument.Parse(
            await File.ReadAllTextAsync(layout.OwnershipManifestPath));
        Assert.True(ownership.RootElement.GetProperty("includesShaderPackage").GetBoolean());
    }

    [Fact]
    public async Task GeneratorCarriesDefaultSceneGuidIntoActivity()
    {
        Guid expectedScene = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        AndroidPlayerProjectOptions options = CreateOptions(
            AndroidOutputMode.ExportProject,
            defaultSceneGuid: expectedScene);
        string output = Path.Combine(_testRoot, "default-scene-project");

        await AndroidPlayerProjectGenerator.GenerateAsync(output, options);

        string source = await File.ReadAllTextAsync(Path.Combine(output, "MainActivity.cs"));
        Assert.Contains(
            "new Guid(\"01234567-89ab-cdef-0123-456789abcdef\")",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratorCopiesAndroidAssetPayloadAndConfiguresAllSandboxMounts()
    {
        string source = Path.Combine(_testRoot, "asset-payload");
        string content = Path.Combine(source, "Content");
        Directory.CreateDirectory(content);
        byte[] pak = [1, 3, 5, 7, 11];
        await File.WriteAllBytesAsync(Path.Combine(content, "data_0.xenginepak"), pak);
        string hash = Convert.ToHexString(SHA256.HashData(pak));
        string strippingReportRelativePath = "Reports/" + AndroidAssetStrippingReport.FileName;
        string strippingReportPath = Path.Combine(source, "Reports", AndroidAssetStrippingReport.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(strippingReportPath)!);
        await File.WriteAllTextAsync(
            strippingReportPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                packagingMode = "XEnginePak",
                physicalPayloadVerified = true,
            }));
        byte[] strippingReport = await File.ReadAllBytesAsync(strippingReportPath);
        string strippingReportHash = Convert.ToHexString(SHA256.HashData(strippingReport));
        await File.WriteAllTextAsync(
            Path.Combine(source, "android-assets.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                root = "xengine",
                packagingMode = "XEnginePak",
                assetStrippingReport = strippingReportRelativePath,
                assets = new object[]
                {
                    new { path = "Content/data_0.xenginepak", sha256 = hash, bytes = pak.Length, requiresSeek = true },
                    new { path = strippingReportRelativePath, sha256 = strippingReportHash, bytes = strippingReport.Length, requiresSeek = false },
                },
            }));

        AndroidPlayerProjectOptions options = CreateOptions(
            AndroidOutputMode.ExportProject,
            assetPayloadDirectory: source,
            assetPackagingMode: XEngine.Runtime.AssetPackagingMode.XEnginePak);
        string output = Path.Combine(_testRoot, "asset-project");
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(output, options);

        Assert.True(layout.IncludesAssetPayload);
        Assert.Equal(XEngine.Runtime.AssetPackagingMode.XEnginePak, layout.AssetPackagingMode);
        Assert.Equal(
            Path.Combine(output, "Assets", "xengine", "android-assets.json"),
            layout.AssetManifestPath);
        Assert.Equal(
            pak,
            await File.ReadAllBytesAsync(Path.Combine(output, "Assets", "xengine", "Content", "data_0.xenginepak")));
        Assert.True(File.Exists(Path.Combine(output, "Assets", "xengine", "Reports", AndroidAssetStrippingReport.FileName)));

        string project = await File.ReadAllTextAsync(layout.ProjectPath);
        Assert.Contains("<AndroidAsset Include=\"Assets/xengine/**\" />", project, StringComparison.Ordinal);
        Assert.Contains(
            "<AndroidStoreUncompressedFileExtensions>.xenginepak;.bundle;.bytes;.xshaderpack</AndroidStoreUncompressedFileExtensions>",
            project,
            StringComparison.Ordinal);
        string sourceText = await File.ReadAllTextAsync(Path.Combine(output, "MainActivity.cs"));
        Assert.Contains("new CallbackAssetStreamProvider", sourceText, StringComparison.Ordinal);
        Assert.Contains("MountProvider(\"Assets\"", sourceText, StringComparison.Ordinal);
        Assert.Contains("MountProvider(\"Files\"", sourceText, StringComparison.Ordinal);
        Assert.Contains("MountProvider(\"Cache\"", sourceText, StringComparison.Ordinal);
        Assert.Contains("MountProvider(\"NoBackup\"", sourceText, StringComparison.Ordinal);
        Assert.Contains("private PlayerAssetBackendBase? _assetBackend", sourceText, StringComparison.Ordinal);
        Assert.Contains(
            "new PlayerAssetBackend(XEngine.Runtime.AssetPackagingMode.XEnginePak, _assetProvider, \"Content\")",
            sourceText,
            StringComparison.Ordinal);

        AndroidPlayerProjectOptions bundleOptions = CreateOptions(
            AndroidOutputMode.ExportProject,
            assetPayloadDirectory: source,
            assetPackagingMode: XEngine.Runtime.AssetPackagingMode.AssetBundle,
            defaultSceneGuid: Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));
        string bundleOutput = Path.Combine(_testRoot, "asset-bundle-project");
        await AndroidPlayerProjectGenerator.GenerateAsync(bundleOutput, bundleOptions);
        string bundleSource = await File.ReadAllTextAsync(Path.Combine(bundleOutput, "MainActivity.cs"));
        Assert.Contains("var bundlePackageRoots = new List<string>()", bundleSource, StringComparison.Ordinal);
        Assert.Contains("descriptorPath.EndsWith(\".version\"", bundleSource, StringComparison.Ordinal);
        Assert.Contains(
            "BundleAssetBackend.CreateFromProvider(_assetProvider, bundlePackageRoots, new Guid(\"01234567-89ab-cdef-0123-456789abcdef\"))",
            bundleSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("new PlayerAssetBackend(", bundleSource, StringComparison.Ordinal);

        string packagePath = Path.Combine(_testRoot, "asset-payload.apk");
        using (ZipArchive package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry descriptor = package.CreateEntry(
                "assets/xengine/android-assets.json",
                CompressionLevel.NoCompression);
            await using (Stream outputStream = descriptor.Open())
            await using (Stream inputStream = File.OpenRead(layout.AssetManifestPath!))
                await inputStream.CopyToAsync(outputStream);
            ZipArchiveEntry pakEntry = package.CreateEntry(
                "assets/xengine/Content/data_0.xenginepak",
                CompressionLevel.NoCompression);
            await using (Stream outputStream = pakEntry.Open())
                await outputStream.WriteAsync(pak);
            ZipArchiveEntry reportEntry = package.CreateEntry(
                "assets/xengine/" + strippingReportRelativePath,
                CompressionLevel.NoCompression);
            await using (Stream outputStream = reportEntry.Open())
                await outputStream.WriteAsync(strippingReport);
        }
        using (ZipArchive package = ZipFile.OpenRead(packagePath))
            AndroidPackageBuilder.VerifyAssetPayload(package, layout);

        using JsonDocument ownership = JsonDocument.Parse(
            await File.ReadAllTextAsync(layout.OwnershipManifestPath));
        Assert.True(ownership.RootElement.GetProperty("includesAssetPayload").GetBoolean());
        Assert.Equal("Assets/xengine/android-assets.json", ownership.RootElement.GetProperty("assetManifest").GetString());
    }

    [Fact]
    public void PackageArchiveValidationAcceptsExactAbiAndRejectsLeakage()
    {
        string[] valid =
        [
            "AndroidManifest.xml",
            "assemblies/XEngine.Android.dll",
            "lib/arm64-v8a/libmonodroid.so",
        ];
        AndroidPackageBuilder.VerifyArchiveEntries(valid, AndroidOutputMode.Apk, ["arm64-v8a"]);

        InvalidDataException abiError = Assert.Throws<InvalidDataException>(() =>
            AndroidPackageBuilder.VerifyArchiveEntries(
                [.. valid, "lib/x86_64/libmonodroid.so"],
                AndroidOutputMode.Apk,
                ["arm64-v8a"]));
        Assert.Contains("ABI set", abiError.Message, StringComparison.Ordinal);

        InvalidDataException compilerError = Assert.Throws<InvalidDataException>(() =>
            AndroidPackageBuilder.VerifyArchiveEntries(
                [.. valid, "assemblies/XEngine.Slang.Compiler.dll"],
                AndroidOutputMode.Apk,
                ["arm64-v8a"]));
        Assert.Contains("runtime compiler", compilerError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArchiveValidationUnderstandsAabLayout()
    {
        AndroidPackageBuilder.VerifyArchiveEntries(
        [
            "base/manifest/AndroidManifest.xml",
            "base/lib/x86_64/libmonodroid.so",
        ], AndroidOutputMode.Aab, ["x86_64"]);
    }

    [Fact]
    public void PackageBuildRetryOnlyMatchesKnownWindowsArchiveLock()
    {
        Assert.True(AndroidPackageBuilder.IsRetryableArchiveLockFailure(
            "error XABAA7000: Renaming temporary file failed: Permission denied"));
        Assert.False(AndroidPackageBuilder.IsRetryableArchiveLockFailure(
            "error XABAA7000: invalid Android resource"));
        Assert.False(AndroidPackageBuilder.IsRetryableArchiveLockFailure(
            "Renaming temporary file failed: file not found"));
    }

    [Fact]
    public void PackageBuildCleanRemovesOnlyGeneratedOutputs()
    {
        string root = Path.Combine(_testRoot, "clean-package-outputs");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        File.WriteAllText(Path.Combine(root, "Assets", "keep.txt"), "keep");

        AndroidPackageBuilder.CleanPackageBuildOutputs(root);

        Assert.False(Directory.Exists(Path.Combine(root, "bin")));
        Assert.False(Directory.Exists(Path.Combine(root, "obj")));
        Assert.True(File.Exists(Path.Combine(root, "Assets", "keep.txt")));
    }

    [Theory]
    [InlineData("android-arm64")]
    [InlineData("android-arm")]
    public void VendoredMiniaudioElfUses16KbLoadAlignment(string runtimeIdentifier)
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string library = Path.Combine(
            repositoryRoot,
            "Libraries",
            runtimeIdentifier,
            "native",
            "libminiaudioex.so");

        AndroidElfInspector.RequireLoadAlignment(library, 16 * 1024);
        using FileStream stream = File.OpenRead(library);
        AndroidElfInspector.RequireMachine(
            stream,
            library,
            runtimeIdentifier == "android-arm64" ? "arm64-v8a" : "armeabi-v7a");
        stream.Position = 0;
        AndroidElfDynamicMetadata metadata = AndroidElfInspector.ReadDynamicMetadata(stream, library);
        Assert.Contains("libandroid.so", metadata.NeededLibraries);
        Assert.False(metadata.HasTextRelocations);
        Assert.Equal("libminiaudioex.so", metadata.Soname);
        Assert.NotNull(metadata.GnuBuildId);
        Assert.NotEmpty(metadata.GnuBuildId);
        stream.Position = 0;
        AndroidElfDynamicSymbols symbols = AndroidElfInspector.ReadDynamicSymbols(stream, library);
        Assert.Contains("malloc", symbols.UndefinedSymbols);
        Assert.Contains("ma_device_init", symbols.DefinedSymbols);
    }

    [Fact]
    public void ElfInspectorRejects4KbLoadAlignment()
    {
        string path = Path.Combine(_testRoot, "four-kilobyte.so");
        Directory.CreateDirectory(_testRoot);
        using (FileStream stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 });
            writer.Write(new byte[8]);
            writer.Write((ushort)3);
            writer.Write((ushort)183);
            writer.Write(1u);
            writer.Write(0ul);
            writer.Write(64ul);
            writer.Write(0ul);
            writer.Write(0u);
            writer.Write((ushort)64);
            writer.Write((ushort)56);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(1u);
            writer.Write(5u);
            writer.Write(0ul);
            writer.Write(0ul);
            writer.Write(0ul);
            writer.Write(1ul);
            writer.Write(1ul);
            writer.Write(0x1000ul);
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidElfInspector.RequireLoadAlignment(path, 16 * 1024));

        Assert.Contains("0x1000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ElfInspectorRejectsMismatchedAbiMachine()
    {
        string path = Path.Combine(_testRoot, "arm64.so");
        Directory.CreateDirectory(_testRoot);
        using (FileStream stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 });
            writer.Write(new byte[8]);
            writer.Write((ushort)3);
            writer.Write((ushort)183);
            writer.Write(1u);
            writer.Write(0ul);
            writer.Write(64ul);
            writer.Write(0ul);
            writer.Write(0u);
            writer.Write((ushort)64);
            writer.Write((ushort)56);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(1u);
            writer.Write(5u);
            writer.Write(0ul);
            writer.Write(0ul);
            writer.Write(0ul);
            writer.Write(1ul);
            writer.Write(1ul);
            writer.Write(0x4000ul);
        }

        using FileStream input = File.OpenRead(path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidElfInspector.RequireMachine(input, path, "x86_64"));

        Assert.Contains("machine 183", error.Message, StringComparison.Ordinal);
        Assert.Contains("x86_64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ElfInspectorReadsNeededLibrariesAndDetectsTextRelocations()
    {
        string path = Path.Combine(_testRoot, "textrel.so");
        Directory.CreateDirectory(_testRoot);
        WriteDynamicElf(path, "libexample.so", hasTextRelocations: true);

        using FileStream input = File.OpenRead(path);
        AndroidElfDynamicMetadata metadata = AndroidElfInspector.ReadDynamicMetadata(input, path);

        Assert.Equal(["libexample.so"], metadata.NeededLibraries);
        Assert.True(metadata.HasTextRelocations);
        Assert.Equal("libtextrel.so", metadata.Soname);
        Assert.Equal("000102030405060708090A0B0C0D0E0F10111213", metadata.GnuBuildId);
    }

    [Fact]
    public void ElfInspectorRequiresSafeSonameAndGnuBuildId()
    {
        var missingIdentity = new AndroidElfDynamicMetadata([], false, null, null);
        InvalidDataException missingError = Assert.Throws<InvalidDataException>(() =>
            AndroidElfInspector.RequireIdentity(missingIdentity, "example"));
        Assert.Contains("DT_SONAME", missingError.Message, StringComparison.Ordinal);

        var unsafeIdentity = new AndroidElfDynamicMetadata([], false, "../libother.so", "AABB");
        InvalidDataException unsafeError = Assert.Throws<InvalidDataException>(() =>
            AndroidElfInspector.RequireIdentity(unsafeIdentity, "example"));
        Assert.Contains("DT_SONAME", unsafeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeElfInventoryIsStableAndSorted()
    {
        string path = Path.Combine(_testRoot, "verification", AndroidNativeElfInventory.FileName);
        AndroidNativeElfInventory.Write(path,
        [
            new AndroidNativeElfInventoryEntry("lib/arm64-v8a/libz.so", "arm64-v8a", "libz.so", "BB", ["libb.so", "liba.so"]),
            new AndroidNativeElfInventoryEntry("lib/arm64-v8a/liba.so", "arm64-v8a", "liba.so", "AA", ["libc.so"]),
        ]);

        using JsonDocument inventory = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, inventory.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement[] entries = inventory.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal("lib/arm64-v8a/liba.so", entries[0].GetProperty("path").GetString());
        Assert.Equal("liba.so", entries[0].GetProperty("soname").GetString());
        Assert.Equal(
            new[] { "liba.so", "libb.so" },
            entries[1].GetProperty("neededLibraries").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Fact]
    public async Task NativeSymbolAuditIsStableAndSorted()
    {
        string path = Path.Combine(_testRoot, "verification", AndroidNativeSymbolAudit.FileName);
        AndroidNativeSymbolAudit.Write(path, 24,
        [
            new AndroidNativeSymbolAuditEntry(
                "lib/arm64-v8a/libz.so",
                "arm64-v8a",
                3,
                1,
                1,
                1,
                ["libz.so", "libc.so"]),
            new AndroidNativeSymbolAuditEntry(
                "lib/arm64-v8a/liba.so",
                "arm64-v8a",
                2,
                0,
                2,
                0,
                ["libandroid.so"]),
        ]);

        using JsonDocument audit = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, audit.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(24, audit.RootElement.GetProperty("minSdkVersion").GetInt32());
        JsonElement[] entries = audit.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal("lib/arm64-v8a/liba.so", entries[0].GetProperty("path").GetString());
        Assert.Equal(2, entries[0].GetProperty("systemResolvedSymbolCount").GetInt32());
        Assert.Equal(
            new[] { "libc.so", "libz.so" },
            entries[1].GetProperty("systemLibraries").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Fact]
    public async Task NativeSymbolArchivePreservesUnstrippedElfAndManagedPdb()
    {
        string root = Path.Combine(_testRoot, "native-symbols");
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(
            root,
            CreateOptions(AndroidOutputMode.Apk));
        string packagePath = Path.Combine(_testRoot, "native-symbols.apk");
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string miniaudio = Path.Combine(
            repositoryRoot,
            "Libraries",
            "android-arm64",
            "native",
            "libminiaudioex.so");
        using (ZipArchive package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = package.CreateEntry(
                "lib/arm64-v8a/libminiaudioex.so",
                CompressionLevel.NoCompression);
            await using Stream output = entry.Open();
            await using Stream input = File.OpenRead(miniaudio);
            await input.CopyToAsync(output);
        }

        string managedOutputDirectory = Path.Combine(root, "bin", "Release", "net10.0-android", "android-arm64");
        Directory.CreateDirectory(managedOutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(managedOutputDirectory, "My_Game.Android.pdb"), "symbols");

        AndroidNativeSymbolArchiveSnapshot archive = AndroidNativeSymbolArchive.Create(layout, packagePath, "Release");

        Assert.Single(archive.ManagedEntries);
        AndroidNativeSymbolArchiveNativeEntry native = Assert.Single(archive.NativeEntries);
        Assert.Equal("lib/arm64-v8a/libminiaudioex.so", native.PackagePath);
        Assert.Equal("package-unstripped", native.SymbolSource);
        Assert.True(native.SymbolHasStaticSymbolTable);
        using (ZipArchive zip = ZipFile.OpenRead(archive.ArchivePath))
        {
            Assert.NotNull(zip.GetEntry("native/arm64-v8a/libminiaudioex.so"));
            Assert.NotNull(zip.GetEntry("managed/My_Game.Android.pdb"));
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidNativeSymbolArchive.VerifyAndWrite(archive, layout, packagePath));
        Assert.Contains("static symbol table", error.Message, StringComparison.Ordinal);

        string debugDirectory = Path.Combine(
            root,
            "obj",
            "Release",
            "net10.0-android",
            "android-arm64",
            "app_shared_libraries",
            "arm64-v8a");
        Directory.CreateDirectory(debugDirectory);
        File.Copy(miniaudio, Path.Combine(debugDirectory, "libminiaudioex.dbg.so"));
        AndroidNativeSymbolArchiveSnapshot mismatched = archive with
        {
            NativeEntries = [native with { GnuBuildId = "different-build-id" }],
        };

        AndroidNativeSymbolArchiveSnapshot reconciled =
            AndroidNativeSymbolArchive.ReconcileWithStrippedArtifact(mismatched, layout, "Release", packagePath);

        AndroidNativeSymbolArchiveNativeEntry reconciledNative = Assert.Single(reconciled.NativeEntries);
        Assert.Equal("generated-debug-companion", reconciledNative.SymbolSource);
        Assert.Equal(native.GnuBuildId, reconciledNative.GnuBuildId);
    }

    [Fact]
    public void NativeSymbolArchiveOnlyRejectsSizeGrowthWhenSymbolsWereStrippable()
    {
        var assemblyStore = new AndroidNativeSymbolArchiveNativeEntry(
            "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so",
            "arm64-v8a",
            "native/arm64-v8a/libassemblies.arm64-v8a.blob.so",
            "package-unstripped",
            1024,
            "SHA256",
            "BUILD-ID",
            SymbolHasStaticSymbolTable: false);

        Assert.False(AndroidNativeSymbolArchive.IsUnexpectedStripSizeIncrease(assemblyStore, 1032));
        Assert.True(AndroidNativeSymbolArchive.IsUnexpectedStripSizeIncrease(
            assemblyStore with { SymbolHasStaticSymbolTable = true },
            1032));
    }

    [Fact]
    public async Task PackagedManagedAssetValidationRequiresClosureAndRejectsEditorAssemblies()
    {
        string root = Path.Combine(_testRoot, "managed-store");
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(
            root,
            CreateOptions(AndroidOutputMode.ExportProject));
        string assets = Path.Combine(
            root,
            "obj",
            "Release",
            "net10.0-android",
            "android-arm64",
            "android",
            "assets",
            "arm64-v8a");
        Directory.CreateDirectory(assets);
        File.Copy(
            typeof(XEngine.Runtime.EngineObject).Assembly.Location,
            Path.Combine(assets, "XEngine.Runtime.dll"));

        AndroidPackageBuilder.VerifyPackagedManagedAssets(layout, "Release", "android-arm64");

        File.Copy(
            typeof(AndroidBuildPipeline).Assembly.Location,
            Path.Combine(assets, "XEngine.Editor.dll"));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidPackageBuilder.VerifyPackagedManagedAssets(layout, "Release", "android-arm64"));
        Assert.Contains("forbidden", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackagedManagedAssetValidationAcceptsCoreClrAssemblyStore()
    {
        string root = Path.Combine(_testRoot, "coreclr-managed-store-input");
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(
            root,
            CreateOptions(AndroidOutputMode.ExportProject));
        string storePath = Path.Combine(
            root,
            "obj",
            "Release",
            "net10.0-android",
            "android-arm64",
            "app_shared_libraries",
            "arm64-v8a",
            "libxamarin-app.so");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        await File.WriteAllTextAsync(storePath, "coreclr-managed-store");

        AndroidPackageBuilder.VerifyPackagedManagedAssets(layout, "Release", "android-arm64");
    }

    [Fact]
    public async Task PackageAssemblyStoreValidationRequiresDirectManagedClosure()
    {
        string root = Path.Combine(_testRoot, "managed-store-archive");
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(
            root,
            CreateOptions(AndroidOutputMode.Apk));
        string packagePath = Path.Combine(_testRoot, "managed-store.apk");
        using (ZipArchive package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry store = package.CreateEntry(
                "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so",
                CompressionLevel.NoCompression);
            await using Stream output = store.Open();
            await output.WriteAsync("store\0XEngine.Runtime.dll\0"u8.ToArray());
        }

        using (ZipArchive package = ZipFile.OpenRead(packagePath))
            AndroidPackageBuilder.VerifyManagedAssemblyStore(package, layout);

        string missingPackagePath = Path.Combine(_testRoot, "managed-store-missing.apk");
        using (ZipArchive package = ZipFile.Open(missingPackagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry store = package.CreateEntry(
                "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so",
                CompressionLevel.NoCompression);
            await using Stream output = store.Open();
            await output.WriteAsync("store\0XEngine.Runtime.dll.extra\0"u8.ToArray());
        }
        using (ZipArchive package = ZipFile.OpenRead(missingPackagePath))
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                AndroidPackageBuilder.VerifyManagedAssemblyStore(package, layout));
            Assert.Contains("XEngine.Runtime", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PackageAssemblyStoreValidationRecognizesWrappedManagedAssemblies()
    {
        string root = Path.Combine(_testRoot, "wrapped-managed-store-archive");
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(
            root,
            CreateOptions(AndroidOutputMode.Apk));
        string packagePath = Path.Combine(_testRoot, "wrapped-managed-store.apk");
        using (ZipArchive package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry wrapped = package.CreateEntry(
                "lib/arm64-v8a/lib_XEngine.Runtime.dll.so",
                CompressionLevel.NoCompression);
            await using Stream output = wrapped.Open();
            await output.WriteAsync("wrapped-runtime"u8.ToArray());
        }

        using (ZipArchive package = ZipFile.OpenRead(packagePath))
            AndroidPackageBuilder.VerifyManagedAssemblyStore(package, layout);
    }

    [Fact]
    public async Task PackageAssemblyStoreValidationVerifiesCoreClrStore()
    {
        string root = Path.Combine(_testRoot, "coreclr-managed-store-archive");
        AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(
            root,
            CreateOptions(AndroidOutputMode.Apk));
        string storePath = Path.Combine(
            root,
            "obj",
            "Release",
            "net10.0-android",
            "android-arm64",
            "app_shared_libraries",
            "arm64-v8a",
            "libxamarin-app.so");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        byte[] storeBytes = "coreclr-managed-store"u8.ToArray();
        await File.WriteAllBytesAsync(storePath, storeBytes);

        string packagePath = Path.Combine(_testRoot, "coreclr-managed-store.apk");
        using (ZipArchive package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry coreClr = package.CreateEntry(
                "lib/arm64-v8a/libcoreclr.so",
                CompressionLevel.NoCompression);
            await using (Stream coreOutput = coreClr.Open())
                await coreOutput.WriteAsync("coreclr"u8.ToArray());
            ZipArchiveEntry store = package.CreateEntry(
                "lib/arm64-v8a/libxamarin-app.so",
                CompressionLevel.NoCompression);
            await using Stream storeOutput = store.Open();
            await storeOutput.WriteAsync(storeBytes);
        }

        using (ZipArchive package = ZipFile.OpenRead(packagePath))
            AndroidPackageBuilder.VerifyManagedAssemblyStore(package, layout, "Release");

        string mismatchedPackagePath = Path.Combine(_testRoot, "coreclr-managed-store-mismatched.apk");
        using (ZipArchive package = ZipFile.Open(mismatchedPackagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry coreClr = package.CreateEntry(
                "lib/arm64-v8a/libcoreclr.so",
                CompressionLevel.NoCompression);
            await using (Stream coreOutput = coreClr.Open())
                await coreOutput.WriteAsync("coreclr"u8.ToArray());
            ZipArchiveEntry store = package.CreateEntry(
                "lib/arm64-v8a/libxamarin-app.so",
                CompressionLevel.NoCompression);
            await using Stream storeOutput = store.Open();
            await storeOutput.WriteAsync("mismatched-store"u8.ToArray());
        }

        using (ZipArchive package = ZipFile.OpenRead(mismatchedPackagePath))
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                AndroidPackageBuilder.VerifyManagedAssemblyStore(package, layout, "Release"));
            Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PipelineOutputGuardProtectsProjectRootsAndExistingFiles()
    {
        string project = Path.Combine(_testRoot, "Project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "Library"));
        Directory.CreateDirectory(Path.Combine(project, "Temp"));
        string output = Path.Combine(project, "Builds", "Android");

        Assert.False(AndroidBuildPipeline.IsUsableOutputDirectory("", output, project));
        Assert.False(AndroidBuildPipeline.IsUsableOutputDirectory(project, project, project));
        Assert.False(AndroidBuildPipeline.IsUsableOutputDirectory("Assets", Path.Combine(project, "Assets"), project));
        Assert.True(AndroidBuildPipeline.IsUsableOutputDirectory("Builds/Android", output, project));

        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "user-file"), "keep");
        Assert.False(AndroidBuildPipeline.IsUsableOutputDirectory("Builds/Android", output, project));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch { }
    }

    private AndroidPlayerProjectOptions CreateOptions(
        AndroidOutputMode mode,
        string? lockPath = null,
        string? shaderPackageDirectory = null,
        IReadOnlyList<string>? targetAbis = null,
        string? nativeLibraryRoot = null,
        string? assetPayloadDirectory = null,
        XEngine.Runtime.AssetPackagingMode assetPackagingMode = XEngine.Runtime.AssetPackagingMode.XEnginePak,
        Guid defaultSceneGuid = default,
        bool enableDevelopmentShaderTrace = false,
        AndroidScreenOrientation screenOrientation = AndroidScreenOrientation.Unspecified,
        bool resizableActivity = true,
        IReadOnlyList<string>? additionalPermissions = null) => new()
    {
        ProjectName = "My Game",
        ApplicationId = "com.xengine.mygame",
        ApplicationLabel = "My Game",
        VersionCode = 7,
        VersionName = "1.2.3",
        MinSdkVersion = 24,
        TargetSdkVersion = 36,
        ScreenOrientation = screenOrientation,
        ResizableActivity = resizableActivity,
        AdditionalPermissions = additionalPermissions ?? Array.Empty<string>(),
        OutputMode = mode,
        TargetAbis = targetAbis ?? ["arm64-v8a"],
        ToolchainFingerprint = "0123456789ABCDEF",
        ToolchainLockPath = lockPath,
        ShaderPackageDirectory = shaderPackageDirectory,
        AssetPayloadDirectory = assetPayloadDirectory,
        AssetPackagingMode = assetPackagingMode,
        DefaultSceneGuid = defaultSceneGuid,
        EnableDevelopmentShaderTrace = enableDevelopmentShaderTrace,
        NativeLibraryRoot = nativeLibraryRoot,
        ManagedStripping = AndroidManagedStrippingMode.Safe,
        TrimmingReport = AndroidTrimmingReport.Create(
            AndroidManagedStrippingMode.Safe,
            assetCount: 3,
            shaderVariantCount: 2,
            ["arm64-v8a"]),
        ManagedDependencies = RuntimeDependencies(),
        ManagedTypeRoots = AndroidManagedTypeRootCollection.Empty,
    };

    private static AndroidPlayerProjectOptions WithApplicationId(string applicationId) => new()
    {
        ProjectName = "My Game",
        ApplicationId = applicationId,
        ApplicationLabel = "My Game",
        VersionCode = 7,
        VersionName = "1.2.3",
        MinSdkVersion = 24,
        TargetSdkVersion = 36,
        OutputMode = AndroidOutputMode.ExportProject,
        TargetAbis = ["arm64-v8a"],
    };

    private static string Property(XDocument project, string name) =>
        project.Descendants(name).Single().Value;

    private static byte[] EmbeddedBrandingPng()
    {
        using Stream stream = typeof(AndroidPlayerProjectGenerator).Assembly
            .GetManifestResourceStream("XEngine.Editor.Resources.xengine.png")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static AndroidManagedDependencyCollection RuntimeDependencies()
    {
        string runtimePath = typeof(XEngine.Runtime.EngineObject).Assembly.Location;
        return new AndroidManagedDependencyCollection(
            [
                new AndroidManagedAssemblyInput(
                    "XEngine.Runtime",
                    runtimePath,
                    "engine-runtime",
                    "test Runtime root"),
            ],
            [
                new AndroidManagedAssemblyExclusion(
                    "XEngine.Editor",
                    "test",
                    "Editor assembly excluded"),
            ]);
    }

    private static IReadOnlyList<string> EnumerateFileBytes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + ":" + Convert.ToBase64String(File.ReadAllBytes(path)))
            .ToArray();

    private static void WriteDynamicElf(string path, string dependency, bool hasTextRelocations)
    {
        const string soname = "libtextrel.so";
        byte[] dependencyBytes = System.Text.Encoding.UTF8.GetBytes(dependency + "\0" + soname + "\0");
        byte[] buildId = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();
        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 });
        writer.Write(new byte[8]);
        writer.Write((ushort)3);
        writer.Write((ushort)183);
        writer.Write(1u);
        writer.Write(0ul);
        writer.Write(64ul);
        writer.Write(0ul);
        writer.Write(0u);
        writer.Write((ushort)64);
        writer.Write((ushort)56);
        writer.Write((ushort)3);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(1u);
        writer.Write(5u);
        writer.Write(0ul);
        writer.Write(0ul);
        writer.Write(0ul);
        writer.Write(1024ul);
        writer.Write(1024ul);
        writer.Write(0x4000ul);
        writer.Write(2u);
        writer.Write(6u);
        writer.Write(256ul);
        writer.Write(256ul);
        writer.Write(256ul);
        writer.Write(96ul);
        writer.Write(96ul);
        writer.Write(8ul);
        writer.Write(4u);
        writer.Write(0u);
        writer.Write(400ul);
        writer.Write(0ul);
        writer.Write(0ul);
        writer.Write(36ul);
        writer.Write(36ul);
        writer.Write(4ul);
        stream.Position = 256;
        writer.Write(5L);
        writer.Write(512ul);
        writer.Write(10L);
        writer.Write((ulong)dependencyBytes.Length);
        writer.Write(1L);
        writer.Write(0ul);
        writer.Write(14L);
        writer.Write((ulong)(dependency.Length + 1));
        if (hasTextRelocations)
        {
            writer.Write(30L);
            writer.Write(0x4ul);
        }
        writer.Write(0L);
        writer.Write(0ul);
        stream.Position = 400;
        writer.Write(4u);
        writer.Write((uint)buildId.Length);
        writer.Write(3u);
        writer.Write(new byte[] { (byte)'G', (byte)'N', (byte)'U', 0 });
        writer.Write(buildId);
        stream.Position = 512;
        writer.Write(dependencyBytes);
    }
}
