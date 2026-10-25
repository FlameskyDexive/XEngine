// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSPlayerProjectTests : IDisposable
{
    private static readonly string[] s_packageIds =
    [
        "OpenHarmony.NET.Runtime.NativeAot.arm64-v8a",
        "OpenHarmony.NET.Runtime.NativeAot.x86_64",
        "OpenHarmony.NET.PublishAotCross",
        "OpenHarmony.NDK.Bindings",
        "OpenHarmony.NET.Player.Sdk",
        "OpenHarmony.NET.HapTooling",
    ];

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSPlayerProjectTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenerateAsync_WritesOnlyLockedFeedAndTemplateReferences()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        string output = Path.Combine(_testRoot, "export");

        HarmonyOSPlayerProjectLayout layout = await HarmonyOSPlayerProjectGenerator.GenerateAsync(
            output,
            CreateOptions(bundle, abi: "x86_64"));

        string project = await File.ReadAllTextAsync(layout.ProjectPath);
        string playerSourcePath = Path.Combine(output, "Player", "HarmonyOSPlayer.cs");
        string playerSource = await File.ReadAllTextAsync(playerSourcePath);
        string playerRootsSourcePath = Path.Combine(output, "Player", HarmonyOSManagedTrimRoots.SourceFileName);
        string playerRootsSource = await File.ReadAllTextAsync(playerRootsSourcePath);
        SyntaxTree playerSyntax = CSharpSyntaxTree.ParseText(
            playerSource,
            new CSharpParseOptions(LanguageVersion.Preview));
        SyntaxTree playerRootsSyntax = CSharpSyntaxTree.ParseText(
            playerRootsSource,
            new CSharpParseOptions(LanguageVersion.Preview));
        foreach (Diagnostic diagnostic in playerSyntax.GetDiagnostics())
            Assert.NotEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        foreach (Diagnostic diagnostic in playerRootsSyntax.GetDiagnostics())
            Assert.NotEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        var referencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] platformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < platformAssemblies.Length; i++)
            referencePaths.Add(platformAssemblies[i]);
        Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < loadedAssemblies.Length; i++)
        {
            if (!loadedAssemblies[i].IsDynamic
                && !string.IsNullOrEmpty(loadedAssemblies[i].Location)
                && File.Exists(loadedAssemblies[i].Location))
                referencePaths.Add(loadedAssemblies[i].Location);
        }
        var metadataReferences = new List<MetadataReference>(referencePaths.Count);
        foreach (string referencePath in referencePaths)
            metadataReferences.Add(MetadataReference.CreateFromFile(referencePath));
        CSharpCompilation playerCompilation = CSharpCompilation.Create(
            "HarmonyOSGeneratedPlayerContract",
            [playerSyntax, playerRootsSyntax],
            metadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var compilationErrors = new StringBuilder();
        foreach (Diagnostic diagnostic in playerCompilation.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
                compilationErrors.AppendLine(diagnostic.ToString());
        }
        Assert.True(compilationErrors.Length == 0, compilationErrors.ToString());
        string app = await File.ReadAllTextAsync(Path.Combine(layout.StageProjectDirectory, "app.json5"));
        string entry = await File.ReadAllTextAsync(Path.Combine(
            layout.StageProjectDirectory,
            "entry",
            "src",
            "main",
            "ets",
            "EntryAbility.ets"));
        HarmonyOSPackagingContract packagingContract = await HarmonyOSPackagingContract.ReadAsync(
            layout.StageProjectDirectory);
        Assert.Equal("linux-musl-x64", layout.RuntimeIdentifier);
        Assert.Contains("linux-musl-x64", project, StringComparison.Ordinal);
        Assert.Contains("RestoreSources", project, StringComparison.Ordinal);
        Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", project, StringComparison.Ordinal);
        Assert.Contains("<NativeLib>Shared</NativeLib>", project, StringComparison.Ordinal);
        Assert.Contains("<Reference Include=\"XEngine.Runtime\">", project, StringComparison.Ordinal);
        Assert.Contains("<TrimmerRootAssembly Include=\"XEngine.Runtime\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\Engine\\OHOS", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bundle.Root, project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EntryPoint = \"xengine_player_abi_version\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_create\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_attach_surface\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_resize_surface\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_detach_surface\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_set_resumed\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_touch\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_key\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_mouse\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_mouse_wheel\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_focus\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_tick\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_text_input_active\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_text_input_type\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_destroy\"", playerSource, StringComparison.Ordinal);
        Assert.Contains("StructSize != sizeof(PlayerCreateArgs)", playerSource, StringComparison.Ordinal);
        Assert.Contains("CreateVulkanSurface", playerSource, StringComparison.Ordinal);
        Assert.Contains("GCHandle.Alloc", playerSource, StringComparison.Ordinal);
        Assert.Contains("deferOwnerThreadBinding: true", playerSource, StringComparison.Ordinal);
        Assert.Contains("AssetPackagingMode.XEnginePak", playerSource, StringComparison.Ordinal);
        Assert.Contains("01234567-89ab-cdef-0123-456789abcdef", playerSource, StringComparison.Ordinal);
        Assert.Contains(
            "global::XEngine.Generated.HarmonyOS.XEnginePlayerRoots.Preserve();",
            playerSource,
            StringComparison.Ordinal);
        Assert.Contains("namespace XEngine.Generated.HarmonyOS;", playerRootsSource, StringComparison.Ordinal);
        Assert.Contains("DynamicDependency", playerRootsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\Engine\\OHOS", playerSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("com.xengine.smoke", app, StringComparison.Ordinal);
        Assert.Contains("\"versionCode\":42", app, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", app, StringComparison.Ordinal);
        Assert.Contains("libxengine_player.so", entry, StringComparison.Ordinal);
        Assert.Equal("outputs/Smoke_Game.HarmonyOS.hap", packagingContract.Hap.ArtifactRelativePath);
        Assert.True(File.Exists(layout.OwnershipManifestPath));
        Assert.Equal(s_packageIds.Length, Directory.GetFiles(Path.Combine(output, "feed"), "*.nupkg").Length);
        Assert.True(File.Exists(Path.Combine(
            output,
            "Player",
            "Managed",
            "XEngine.Runtime.dll")));
        Assert.True(File.Exists(Path.Combine(
            layout.StageProjectDirectory,
            "entry",
            "src",
            "main",
            "resources",
            "resfile",
            "xengine-harmonyos-payload.json")));
        Assert.True(File.Exists(Path.Combine(
            layout.StageProjectDirectory,
            "entry",
            "src",
            "main",
            "cpp",
            "xengine",
            "xengine_host_napi.cpp")));
        byte[] branding = EmbeddedBrandingPng();
        Assert.Equal(branding, await File.ReadAllBytesAsync(Path.Combine(
            layout.StageProjectDirectory,
            "AppScope",
            "resources",
            "base",
            "media",
            "app_icon.png")));
        Assert.Equal(branding, await File.ReadAllBytesAsync(Path.Combine(
            layout.StageProjectDirectory,
            "entry",
            "src",
            "main",
            "resources",
            "base",
            "media",
            "startIcon.png")));

        using JsonDocument ownership = JsonDocument.Parse(await File.ReadAllTextAsync(layout.OwnershipManifestPath));
        var ownedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement file in ownership.RootElement.GetProperty("files").EnumerateArray())
            ownedPaths.Add(file.GetProperty("path").GetString() ?? string.Empty);
        Assert.Contains("Stage/entry/src/main/resources/resfile/xengine-harmonyos-payload.json", ownedPaths);
        Assert.Contains("Stage/entry/src/main/cpp/xengine/xengine_host_napi.cpp", ownedPaths);
        Assert.Contains("Player/Managed/XEngine.Runtime.dll", ownedPaths);
        Assert.Contains("Player/HarmonyOSPlayer.cs", ownedPaths);
        Assert.Contains("Player/XEngine.HarmonyOS.PlayerRoots.g.cs", ownedPaths);
        Assert.Contains("Player/trimming/harmonyos-managed-type-roots.json", ownedPaths);
    }

    [Fact]
    public async Task RefreshOwnershipManifestAsync_IncludesPublishedPlayerAndExcludesManifest()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        string output = Path.Combine(_testRoot, "export-refresh");
        HarmonyOSPlayerProjectLayout layout = await HarmonyOSPlayerProjectGenerator.GenerateAsync(
            output,
            CreateOptions(bundle));
        string playerLibraryPath = Path.Combine(
            layout.StageProjectDirectory,
            "entry",
            "src",
            "main",
            "cpp",
            "xengine",
            "libxengine_player.so");
        byte[] playerLibrary = [0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3, 4];
        await File.WriteAllBytesAsync(playerLibraryPath, playerLibrary);

        await HarmonyOSPlayerProjectGenerator.RefreshOwnershipManifestAsync(
            layout,
            bundle.Lock.Fingerprint);

        using JsonDocument ownership = JsonDocument.Parse(await File.ReadAllTextAsync(layout.OwnershipManifestPath));
        var ownedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement file in ownership.RootElement.GetProperty("files").EnumerateArray())
        {
            ownedFiles.Add(
                file.GetProperty("path").GetString() ?? string.Empty,
                file.GetProperty("sha256").GetString() ?? string.Empty);
        }
        const string playerLibraryRelativePath = "Stage/entry/src/main/cpp/xengine/libxengine_player.so";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(playerLibrary)).ToLowerInvariant(), ownedFiles[playerLibraryRelativePath]);
        Assert.DoesNotContain("xengine-harmonyos-project.json", ownedFiles.Keys);
    }

    [Fact]
    public async Task GenerateAsync_RejectsNonEmptyExportDirectory()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        string output = Path.Combine(_testRoot, "export");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "foreign.txt"), "x", new UTF8Encoding(false));

        await Assert.ThrowsAsync<IOException>(() =>
            HarmonyOSPlayerProjectGenerator.GenerateAsync(output, CreateOptions(bundle)));
    }

    [Fact]
    public async Task GenerateAsync_RejectsTemplateWithoutVersionCodeToken()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        string templatePath = Path.Combine(bundle.Root, "templates", "stage-player", "app.json5");
        await File.WriteAllTextAsync(
            templatePath,
            "{\"bundleName\":\"{{APPLICATION_ID}}\",\"api\":{{API_LEVEL}}}",
            new UTF8Encoding(false));
        var files = new List<HarmonyOSSdkBundleLockedFile>(bundle.Lock.Files);
        for (int i = 0; i < files.Count; i++)
        {
            if (files[i].RelativePath == "templates/stage-player/app.json5")
            {
                files[i] = await CreateLockedFileAsync(bundle.Root, files[i].RelativePath);
                break;
            }
        }
        HarmonyOSSdkBundleLock updatedLock = CreateLock(bundle.Lock, files, new List<HarmonyOSSdkBundleLockedPackage>(bundle.Lock.Packages));
        string output = Path.Combine(_testRoot, "export-missing-version-token");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HarmonyOSPlayerProjectGenerator.GenerateAsync(output, CreateOptions(bundle with { Lock = updatedLock })));

        Assert.True(Directory.Exists(output));
    }

    [Fact]
    public async Task GenerateAsync_RejectsUnownedTemplateFileBeforeCreatingOutput()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(bundle.Root, "templates", "stage-player", "unowned.txt"),
            "unowned",
            new UTF8Encoding(false));
        string output = Path.Combine(_testRoot, "export");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HarmonyOSPlayerProjectGenerator.GenerateAsync(output, CreateOptions(bundle)));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task GenerateAsync_RejectsLockedPackagesWithConflictingFeedFileNames()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        HarmonyOSSdkBundleLockedPackage existingPackage = bundle.Lock.Packages[0];
        string conflictingRelativePath = "feed/nested/" + Path.GetFileName(existingPackage.RelativePath);
        string conflictingPath = Path.Combine(bundle.Root, conflictingRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(conflictingPath)!);
        await File.WriteAllTextAsync(conflictingPath, "conflicting package", new UTF8Encoding(false));
        HarmonyOSSdkBundleLockedFile conflictingFile = await CreateLockedFileAsync(bundle.Root, conflictingRelativePath);

        var files = new List<HarmonyOSSdkBundleLockedFile>(bundle.Lock.Files) { conflictingFile };
        var packages = new List<HarmonyOSSdkBundleLockedPackage>(bundle.Lock.Packages)
        {
            new()
            {
                Id = "Contoso.OpenHarmony.Conflict",
                Version = existingPackage.Version,
                RelativePath = conflictingRelativePath,
                Sha256 = conflictingFile.Sha256,
            },
        };
        HarmonyOSSdkBundleLock conflictLock = CreateLock(bundle.Lock, files, packages);
        string output = Path.Combine(_testRoot, "export");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HarmonyOSPlayerProjectGenerator.GenerateAsync(output, CreateOptions(bundle with { Lock = conflictLock })));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task GenerateAsync_RejectsPayloadWithoutManifestBeforeCreatingOutput()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        HarmonyOSPlayerProjectOptions options = CreateOptions(bundle);
        File.Delete(Path.Combine(options.AssetPayloadDirectory, "xengine-harmonyos-payload.json"));
        string output = Path.Combine(_testRoot, "export");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HarmonyOSPlayerProjectGenerator.GenerateAsync(output, options));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task GenerateAsync_RejectsUnownedNativeHostFileBeforeCreatingOutput()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        HarmonyOSPlayerProjectOptions options = CreateOptions(bundle);
        await File.WriteAllTextAsync(
            Path.Combine(options.NativeHostSourceDirectory, "unowned.cpp"),
            "void unowned() {}",
            new UTF8Encoding(false));
        string output = Path.Combine(_testRoot, "export");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HarmonyOSPlayerProjectGenerator.GenerateAsync(output, options));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ManagedPlayerSource_ExportsTextInputQueryEntryPoints()
    {
        string source = HarmonyOSManagedPlayerSource.Create(
            new HarmonyOSPlayerProjectOptions(),
            [new HarmonyOSVerifiedInputFile("Game.dll", "Game.dll")]);

        Assert.Contains("EntryPoint = \"xengine_player_text_input_active\"", source, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"xengine_player_text_input_type\"", source, StringComparison.Ordinal);
        Assert.Contains("LastHarmonyOSInputType", source, StringComparison.Ordinal);
        Assert.Contains("LastTextInputActive", source, StringComparison.Ordinal);
        Assert.Contains(
            "global::XEngine.Generated.HarmonyOS.XEnginePlayerRoots.Preserve();",
            source,
            StringComparison.Ordinal);
        Assert.Contains("private const uint AbiVersion = 8;", source, StringComparison.Ordinal);
        Assert.Contains("SetKeepScreenOn", source, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public async Task GenerateAsync_StagesNativeLibraryPayloadIntoLibsAbi()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        string output = Path.Combine(_testRoot, "export-native-payload");
        HarmonyOSPlayerProjectOptions options = CreateOptions(
            bundle,
            "x86_64",
            CreateNativeLibraryPayloadFixture());

        HarmonyOSPlayerProjectLayout layout = await HarmonyOSPlayerProjectGenerator.GenerateAsync(
            output,
            options);

        string libsRoot = Path.Combine(layout.StageProjectDirectory, "entry", "libs", "x86_64");
        Assert.True(File.Exists(Path.Combine(libsRoot, "libavutil.so.60")), "libavutil.so.60 must be staged");
        Assert.True(File.Exists(Path.Combine(libsRoot, "libswresample.so.6")), "libswresample.so.6 must be staged");
        Assert.True(File.Exists(Path.Combine(libsRoot, "libswscale.so.9")), "libswscale.so.9 must be staged");
        Assert.True(File.Exists(Path.Combine(libsRoot, "libavcodec.so.62")), "libavcodec.so.62 must be staged");
        Assert.True(File.Exists(Path.Combine(libsRoot, "libavformat.so.62")), "libavformat.so.62 must be staged");
        Assert.True(File.Exists(Path.Combine(libsRoot, "libavfilter.so.11")), "libavfilter.so.11 must be staged");
        // Non-library riders (license text) must not leak into libs/<abi>/.
        Assert.False(File.Exists(Path.Combine(libsRoot, "FFmpeg-LICENSE.txt")));
        Assert.Equal(6, Directory.GetFiles(libsRoot).Length);
        string manifest = await File.ReadAllTextAsync(layout.OwnershipManifestPath);
        Assert.Contains("Stage/entry/libs/x86_64/libavcodec.so.62", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RejectsNativeLibraryPayloadMissingRequiredSoname()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        string output = Path.Combine(_testRoot, "export-native-payload-incomplete");
        string payload = CreateNativeLibraryPayloadFixture();
        File.Delete(Path.Combine(payload, "libavfilter.so.11"));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => HarmonyOSPlayerProjectGenerator.GenerateAsync(
                output,
                CreateOptions(bundle, nativeLibraryPayloadDirectory: payload)));
        Assert.Contains("libavfilter.so.11", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_SkipsMissingNativeLibraryPayloadDirectory()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        string output = Path.Combine(_testRoot, "export-native-payload-missing");

        HarmonyOSPlayerProjectLayout layout = await HarmonyOSPlayerProjectGenerator.GenerateAsync(
            output,
            CreateOptions(bundle, nativeLibraryPayloadDirectory: Path.Combine(_testRoot, "does-not-exist")));

        Assert.False(Directory.Exists(Path.Combine(layout.StageProjectDirectory, "entry", "libs")));
    }

    private string CreateNativeLibraryPayloadFixture()
    {
        string root = Path.Combine(_testRoot, "native-library-payload");
        Directory.CreateDirectory(root);
        foreach (string soname in new[]
                 {
                     "libavutil.so.60",
                     "libswresample.so.6",
                     "libswscale.so.9",
                     "libavcodec.so.62",
                     "libavformat.so.62",
                     "libavfilter.so.11",
                 })
        {
            File.WriteAllText(Path.Combine(root, soname), "payload-" + soname, new UTF8Encoding(false));
        }
        File.WriteAllText(Path.Combine(root, "FFmpeg-LICENSE.txt"), "LGPL 2.1", new UTF8Encoding(false));
        return root;
    }

    private HarmonyOSPlayerProjectOptions CreateOptions(
        BundleFixture bundle,
        string abi = "arm64-v8a",
        string? nativeLibraryPayloadDirectory = null) => new()
    {
        ProjectName = "Smoke Game",
        ApplicationId = "com.xengine.smoke",
        ApplicationLabel = "Smoke Game",
        VersionCode = 42,
        ApiLevel = 13,
        Abi = abi,
        BundleRoot = bundle.Root,
        BundleLock = bundle.Lock,
        BundleFingerprint = bundle.Lock.Fingerprint,
        AssetPayloadDirectory = CreateAssetPayloadFixture(),
        NativeHostSourceDirectory = CreateNativeHostFixture(),
        NativeLibraryPayloadDirectory = nativeLibraryPayloadDirectory,
        PackagingMode = XEngine.Runtime.AssetPackagingMode.XEnginePak,
        DefaultSceneGuid = new Guid("01234567-89ab-cdef-0123-456789abcdef"),
        ManagedTypeRoots = new HarmonyOSManagedTypeRootCollection(
        [
            new HarmonyOSManagedTypeRoot(
                typeof(XEngine.Runtime.Game).Assembly.GetName().Name!,
                typeof(XEngine.Runtime.Game).FullName!,
                Array.Empty<HarmonyOSManagedTypeRootReason>()),
        ]),
        ManagedAssemblies =
        [
            new HarmonyOSManagedAssemblyInput(
                "XEngine.Runtime",
                typeof(XEngine.Runtime.Game).Assembly.Location,
                "engine-runtime",
                "HarmonyOS-target XEngine Runtime root"),
        ],
    };

    private string CreateAssetPayloadFixture()
    {
        string root = Path.Combine(_testRoot, "asset-payload");
        Directory.CreateDirectory(Path.Combine(root, "Content"));
        File.WriteAllText(
            Path.Combine(root, "xengine-harmonyos-payload.json"),
            "{\"schemaVersion\":1,\"files\":[]}",
            new UTF8Encoding(false));
        return root;
    }

    [Fact]
    public async Task GenerateAsync_ReplacesTemplateDefaultBranding()
    {
        BundleFixture bundle = await CreateBundleFixtureAsync();
        string output = Path.Combine(_testRoot, "export-branding");

        HarmonyOSPlayerProjectLayout layout = await HarmonyOSPlayerProjectGenerator.GenerateAsync(
            output,
            CreateOptions(bundle));

        byte[] branding = EmbeddedBrandingPng();
        string mediaRoot = Path.Combine(layout.StageProjectDirectory, "AppScope", "resources", "base", "media");
        string entryMediaRoot = Path.Combine(layout.StageProjectDirectory, "entry", "src", "main", "resources", "base", "media");
        Assert.Equal(branding, await File.ReadAllBytesAsync(Path.Combine(mediaRoot, "app_icon.png")));
        Assert.Equal(branding, await File.ReadAllBytesAsync(Path.Combine(mediaRoot, "foreground.png")));
        Assert.Equal(branding, await File.ReadAllBytesAsync(Path.Combine(entryMediaRoot, "foreground.png")));
        Assert.Equal(branding, await File.ReadAllBytesAsync(Path.Combine(entryMediaRoot, "startIcon.png")));
    }

    private static byte[] EmbeddedBrandingPng()
    {
        using Stream stream = typeof(HarmonyOSPlayerProjectGenerator).Assembly
            .GetManifestResourceStream("XEngine.Editor.Resources.xengine.png")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private string CreateNativeHostFixture()
    {
        string root = Path.Combine(_testRoot, "native-host");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "add_library(xengine_host SHARED xengine_host_napi.cpp)", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "xengine_host_napi.cpp"), "#include \"xengine_player_abi.h\"", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "xengine_player_abi.h"), "#pragma once", new UTF8Encoding(false));
        return root;
    }

    private static HarmonyOSSdkBundleLock CreateLock(
        HarmonyOSSdkBundleLock source,
        List<HarmonyOSSdkBundleLockedFile> files,
        List<HarmonyOSSdkBundleLockedPackage> packages)
    {
        var result = new HarmonyOSSdkBundleLock
        {
            SchemaVersion = source.SchemaVersion,
            ReleaseId = source.ReleaseId,
            PackageVersion = source.PackageVersion,
            BundleRelativePath = source.BundleRelativePath,
            ArchiveSha256 = source.ArchiveSha256,
            ManifestSha256 = source.ManifestSha256,
            SourceKind = source.SourceKind,
            SourceUri = source.SourceUri,
            Files = files,
            Packages = packages,
        };
        return new HarmonyOSSdkBundleLock
        {
            SchemaVersion = result.SchemaVersion,
            ReleaseId = result.ReleaseId,
            PackageVersion = result.PackageVersion,
            BundleRelativePath = result.BundleRelativePath,
            ArchiveSha256 = result.ArchiveSha256,
            ManifestSha256 = result.ManifestSha256,
            SourceKind = result.SourceKind,
            SourceUri = result.SourceUri,
            Files = result.Files,
            Packages = result.Packages,
            Fingerprint = HarmonyOSSdkBundleLockFile.ComputeFingerprint(result),
        };
    }

    private async Task<BundleFixture> CreateBundleFixtureAsync()
    {
        const string releaseId = "10.0.10-ohos.2-preview.1";
        string root = Path.Combine(_testRoot, "bundle");
        string templateRoot = Path.Combine(root, "templates", "stage-player");
        string feedRoot = Path.Combine(root, "feed");
        Directory.CreateDirectory(templateRoot);
        Directory.CreateDirectory(feedRoot);

        await File.WriteAllTextAsync(
            Path.Combine(templateRoot, "template-manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                files = new[]
                {
                    "app.json5",
                    "AppScope/resources/base/media/app_icon.png",
                    "AppScope/resources/base/media/foreground.png",
                    "entry/src/main/ets/EntryAbility.ets",
                    "entry/src/main/resources/base/media/foreground.png",
                    "entry/src/main/resources/base/media/startIcon.png",
                    "xengine-packaging.json",
                },
            }),
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(templateRoot, "app.json5"),
            "{\"bundleName\":\"{{APPLICATION_ID}}\",\"label\":\"{{APPLICATION_LABEL}}\",\"versionCode\":{{VERSION_CODE}},\"api\":{{API_LEVEL}}}",
            new UTF8Encoding(false));
        string entryPath = Path.Combine(templateRoot, "entry", "src", "main", "ets", "EntryAbility.ets");
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        await File.WriteAllTextAsync(
            entryPath,
            "export const abi = '{{ABI}}'; export const player = '{{PLAYER_LIBRARY}}';",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(templateRoot, "xengine-packaging.json"),
            """
            {
              "schemaVersion": 1,
              "hap": { "buildTarget": "XEngineBuildHap", "verifyTarget": "XEngineVerifyHap", "artifact": "outputs/{{ASSEMBLY_NAME}}.hap" },
              "app": { "buildTarget": "XEngineBuildApp", "verifyTarget": "XEngineVerifyApp", "artifact": "outputs/{{ASSEMBLY_NAME}}.app" },
              "allowedElfDependencies": ["libace_napi.z.so", "libhilog_ndk.z.so", "libnative_window.so", "libxengine_player.so"]
            }
            """,
            new UTF8Encoding(false));

        string[] brandingPaths =
        [
            "AppScope/resources/base/media/app_icon.png",
            "AppScope/resources/base/media/foreground.png",
            "entry/src/main/resources/base/media/foreground.png",
            "entry/src/main/resources/base/media/startIcon.png",
        ];
        for (int i = 0; i < brandingPaths.Length; i++)
        {
            string path = Path.Combine(templateRoot, brandingPaths[i].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [0]);
        }

        var files = new List<HarmonyOSSdkBundleLockedFile>
        {
            await CreateLockedFileAsync(root, "templates/stage-player/template-manifest.json"),
            await CreateLockedFileAsync(root, "templates/stage-player/app.json5"),
            await CreateLockedFileAsync(root, "templates/stage-player/AppScope/resources/base/media/app_icon.png"),
            await CreateLockedFileAsync(root, "templates/stage-player/AppScope/resources/base/media/foreground.png"),
            await CreateLockedFileAsync(root, "templates/stage-player/entry/src/main/ets/EntryAbility.ets"),
            await CreateLockedFileAsync(root, "templates/stage-player/entry/src/main/resources/base/media/foreground.png"),
            await CreateLockedFileAsync(root, "templates/stage-player/entry/src/main/resources/base/media/startIcon.png"),
            await CreateLockedFileAsync(root, "templates/stage-player/xengine-packaging.json"),
        };
        var packages = new List<HarmonyOSSdkBundleLockedPackage>(s_packageIds.Length);
        for (int i = 0; i < s_packageIds.Length; i++)
        {
            string id = s_packageIds[i];
            string relativePath = "feed/" + id + "." + releaseId + ".nupkg";
            string packagePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllTextAsync(packagePath, "package=" + id, new UTF8Encoding(false));
            HarmonyOSSdkBundleLockedFile file = await CreateLockedFileAsync(root, relativePath);
            files.Add(file);
            packages.Add(new HarmonyOSSdkBundleLockedPackage
            {
                Id = id,
                Version = releaseId,
                RelativePath = relativePath,
                Sha256 = file.Sha256,
            });
        }

        var bundleLock = new HarmonyOSSdkBundleLock
        {
            ReleaseId = releaseId,
            PackageVersion = releaseId,
            BundleRelativePath = "ThirdParty/OpenHarmonySdk/" + releaseId,
            ArchiveSha256 = new string('a', 64),
            ManifestSha256 = new string('b', 64),
            SourceKind = "Fixture",
            Files = files,
            Packages = packages,
        };
        string lockPath = Path.Combine(_testRoot, "openharmony-sdk-lock.json");
        await HarmonyOSSdkBundleLockFile.WriteAsync(lockPath, bundleLock);
        return new BundleFixture(root, await HarmonyOSSdkBundleLockFile.ReadAsync(lockPath));
    }

    private static async Task<HarmonyOSSdkBundleLockedFile> CreateLockedFileAsync(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        FileInfo info = new(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new HarmonyOSSdkBundleLockedFile
        {
            RelativePath = relativePath,
            Length = info.Length,
            Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant(),
        };
    }

    private sealed record BundleFixture(string Root, HarmonyOSSdkBundleLock Lock);
}
