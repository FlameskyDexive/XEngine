// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Linq;

namespace XEngine.Editor.Build.Android;

public enum AndroidOutputMode
{
    ExportProject,
    Apk,
    Aab,
}

/// <summary>Supported Android Activity screen-orientation policies for generated Players.</summary>
public enum AndroidScreenOrientation
{
    Unspecified,
    Portrait,
    Landscape,
    Sensor,
    SensorPortrait,
    SensorLandscape,
    FullSensor,
}

internal static class AndroidAbiMapper
{
    private static readonly IReadOnlyDictionary<string, string> s_abiToRid =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["arm64-v8a"] = "android-arm64",
            ["armeabi-v7a"] = "android-arm",
            ["x86_64"] = "android-x64",
        };

    internal static string ToRuntimeIdentifier(string abi)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(abi);
        return s_abiToRid.TryGetValue(abi, out string? rid)
            ? rid
            : throw new ArgumentOutOfRangeException(nameof(abi), abi, "Unsupported Android ABI.");
    }

    internal static string ToAbi(string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        foreach (KeyValuePair<string, string> pair in s_abiToRid)
        {
            if (string.Equals(pair.Value, runtimeIdentifier, StringComparison.Ordinal))
                return pair.Key;
        }
        throw new ArgumentOutOfRangeException(
            nameof(runtimeIdentifier), runtimeIdentifier, "Unsupported Android runtime identifier.");
    }

    internal static IReadOnlyList<string> Normalize(IEnumerable<string> abis)
    {
        ArgumentNullException.ThrowIfNull(abis);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string abi in abis)
        {
            _ = ToRuntimeIdentifier(abi);
            if (seen.Add(abi))
                result.Add(abi);
        }
        if (result.Count == 0)
            throw new ArgumentException("At least one Android ABI is required.", nameof(abis));
        return result;
    }
}

internal sealed class AndroidPlayerProjectOptions
{
    public string ProjectName { get; init; } = "XEngineGame";
    public string ApplicationId { get; init; } = "com.xengine.game";
    public string ApplicationLabel { get; init; } = "XEngine";
    /// <summary>Optional project PNG for the application launcher icon.</summary>
    public string? ApplicationIconPath { get; init; }
    public int VersionCode { get; init; } = 1;
    public string VersionName { get; init; } = "1.0";
    public int MinSdkVersion { get; init; } = 24;
    public int TargetSdkVersion { get; init; } = 36;
    public AndroidScreenOrientation ScreenOrientation { get; init; } = AndroidScreenOrientation.Unspecified;
    public bool ResizableActivity { get; init; } = true;
    public IReadOnlyList<string> AdditionalPermissions { get; init; } = Array.Empty<string>();
    public AndroidOutputMode OutputMode { get; init; } = AndroidOutputMode.ExportProject;
    public AndroidManagedStrippingMode ManagedStripping { get; init; } = AndroidManagedStrippingMode.Disabled;
    public IReadOnlyList<string> TargetAbis { get; init; } = ["arm64-v8a"];
    public string ToolchainFingerprint { get; init; } = string.Empty;
    public string? ToolchainLockPath { get; init; }
    public AndroidShaderVariantCollection? ShaderVariants { get; init; }
    public AndroidShaderVariantTraceCollection? ApprovedShaderVariantTrace { get; init; }
    public string? ShaderPackageDirectory { get; init; }
    /// <summary>Build payload copied to the generated APK assets/xengine directory.</summary>
    public string? AssetPayloadDirectory { get; init; }
    public bool EnableDevelopmentShaderTrace { get; init; }
    public Guid DefaultSceneGuid { get; init; }
    public XEngine.Runtime.AssetPackagingMode AssetPackagingMode { get; init; } =
        XEngine.Runtime.AssetPackagingMode.XEnginePak;
    public AndroidTrimmingReport? TrimmingReport { get; init; }
    public AndroidManagedDependencyCollection? ManagedDependencies { get; init; }
    public AndroidManagedTypeRootCollection? ManagedTypeRoots { get; init; }
    public string? NativeLibraryRoot { get; init; }
    public AndroidPackageSizeBudget PackageSizeBudget { get; init; } = AndroidPackageSizeBudget.Disabled;
}

internal sealed record AndroidPlayerProjectLayout(
    string RootDirectory,
    string ProjectPath,
    string ManifestPath,
    string OwnershipManifestPath,
    string? ToolchainLockPath,
    string AssemblyName,
    int MinSdkVersion,
    IReadOnlyList<string> TargetAbis,
    IReadOnlyList<string> RuntimeIdentifiers,
    IReadOnlyList<string> ManagedAssemblyNames,
    IReadOnlyList<string> NativeAudioAbis,
    AndroidOutputMode OutputMode,
    bool IncludesShaderPackage,
    bool IncludesAssetPayload,
    string? AssetManifestPath,
    XEngine.Runtime.AssetPackagingMode AssetPackagingMode,
    AndroidPackageSizeBudget PackageSizeBudget);

internal static partial class AndroidPlayerProjectGenerator
{
    private const int OwnershipSchemaVersion = 1;
    private static readonly string[] s_requiredPermissions =
    [
        "android.permission.ACCESS_NETWORK_STATE",
        "android.permission.VIBRATE",
    ];
    private static readonly UTF8Encoding s_utf8NoBom = new(false);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationIdRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*){2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionNameRegex();

    internal static async Task<AndroidPlayerProjectLayout> GenerateAsync(
        string outputDirectory,
        AndroidPlayerProjectOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        string root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && HasEntries(root))
            throw new IOException($"Android export directory must be empty: {root}");
        Directory.CreateDirectory(root);

        IReadOnlyList<string> abis = AndroidAbiMapper.Normalize(options.TargetAbis);
        IReadOnlyList<string> permissions = NormalizePermissions(options.AdditionalPermissions);
        var rids = new string[abis.Count];
        for (int i = 0; i < abis.Count; i++)
            rids[i] = AndroidAbiMapper.ToRuntimeIdentifier(abis[i]);
        string assemblyName = GetAssemblyName(options.ProjectName);
        string projectPath = Path.Combine(root, assemblyName + ".csproj");
        string manifestPath = Path.Combine(root, "Properties", "AndroidManifest.xml");
        string sourcePath = Path.Combine(root, "MainActivity.cs");
        string globalJsonPath = Path.Combine(root, "global.json");
        string readmePath = Path.Combine(root, "README.md");
        string? copiedLockPath = null;
        IReadOnlyList<string> managedAssemblyNames = GetManagedAssemblyNames(options.ManagedDependencies);
        IReadOnlyList<string> nativeAudioAbis = GetNativeAudioAbis(options.NativeLibraryRoot, abis);
        bool includesAssetPayload = !string.IsNullOrWhiteSpace(options.AssetPayloadDirectory);

        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await WriteTextAsync(projectPath, CreateProject(options, assemblyName, rids, nativeAudioAbis), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(manifestPath, CreateManifest(options, permissions), cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(sourcePath, CreateMainActivity(options, permissions), cancellationToken).ConfigureAwait(false);
        AndroidManagedTypeRootCollection managedTypeRoots =
            options.ManagedTypeRoots ?? AndroidManagedTypeRootCollection.Empty;
        await WriteTextAsync(
                Path.Combine(root, AndroidManagedTrimRoots.SourceFileName),
                AndroidManagedTrimRoots.CreateSource(managedTypeRoots),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(root, "r8", "xengine-r8.rules"),
                CreateR8Rules(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(globalJsonPath, CreateGlobalJson(), cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(root, "Directory.Build.props"),
                CreateDirectoryBuildProps(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(readmePath, CreateReadme(options, assemblyName), cancellationToken).ConfigureAwait(false);
        await WriteLauncherIconAsync(root, options.ApplicationIconPath, cancellationToken).ConfigureAwait(false);
        await WriteDefaultThemesAsync(root, cancellationToken).ConfigureAwait(false);

        bool includesShaderPackage = !string.IsNullOrWhiteSpace(options.ShaderPackageDirectory);
        if (includesShaderPackage)
            CopyShaderPackage(options.ShaderPackageDirectory!, root);
        if (includesAssetPayload)
            CopyAssetPayload(options.AssetPayloadDirectory!, root);

        if (options.ManagedDependencies != null)
            await CopyManagedDependenciesAsync(options.ManagedDependencies, root, cancellationToken).ConfigureAwait(false);
        CopyNativeAudioLibraries(options.NativeLibraryRoot, root, nativeAudioAbis);

        if (options.ShaderVariants != null)
        {
            AndroidShaderVariantCollector.WriteReport(
                Path.Combine(root, "shader-variants", "android-vulkan.json"),
                options.ShaderVariants);
        }
        if (options.ApprovedShaderVariantTrace != null && options.ApprovedShaderVariantTrace.Entries.Count > 0)
        {
            AndroidShaderVariantTrace.WriteReport(
                Path.Combine(root, "shader-variants", "approved-runtime-trace.json"),
                options.ApprovedShaderVariantTrace);
        }

        if (options.TrimmingReport != null)
        {
            AndroidTrimmingReport.WriteReport(
                Path.Combine(root, "trimming", "android-trimming.json"),
                options.TrimmingReport);
        }

        AndroidManagedTypeRootReport.Write(
            Path.Combine(root, "trimming", AndroidManagedTypeRootReport.FileName),
            managedTypeRoots);

        if (options.ManagedStripping == AndroidManagedStrippingMode.Safe)
        {
            AndroidManagedTrimRoots.WriteDescriptor(
                Path.Combine(root, "trimming", AndroidManagedTrimRoots.DescriptorFileName),
                assemblyName,
                options.ManagedDependencies);
        }

        if (!string.IsNullOrWhiteSpace(options.ToolchainLockPath))
        {
            string sourceLock = Path.GetFullPath(options.ToolchainLockPath);
            if (!File.Exists(sourceLock))
                throw new FileNotFoundException("Android toolchain lock is missing.", sourceLock);
            copiedLockPath = Path.Combine(root, "toolchain", AndroidToolchainLockFile.DefaultFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(copiedLockPath)!);
            await using FileStream source = new(sourceLock, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream destination = new(copiedLockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        string ownershipPath = Path.Combine(root, "xengine-android-project.json");
        var generatedFiles = new List<object>();
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        for (int i = 0; i < files.Length; i++)
        {
            string path = files[i];
            generatedFiles.Add(new
            {
                path = Path.GetRelativePath(root, path).Replace('\\', '/'),
                sha256 = await HashFileAsync(path, cancellationToken).ConfigureAwait(false),
                bytes = new FileInfo(path).Length,
            });
        }

        var ownership = new
        {
            schemaVersion = OwnershipSchemaVersion,
            generator = "XEngine.AndroidPlayerProjectGenerator",
            project = assemblyName,
            applicationId = options.ApplicationId,
            outputMode = options.OutputMode.ToString(),
            targetFramework = "net10.0-android",
            minSdk = options.MinSdkVersion,
            targetSdk = options.TargetSdkVersion,
            permissions,
            targetAbis = abis,
            runtimeIdentifiers = rids,
            managedAssemblies = managedAssemblyNames,
            managedTypeRootCount = managedTypeRoots.Types.Count,
            nativeAudioAbis,
            includesShaderPackage,
            includesAssetPayload,
            assetManifest = includesAssetPayload ? "Assets/xengine/android-assets.json" : null,
            assetPackagingMode = options.AssetPackagingMode.ToString(),
            packageSizeBudget = options.PackageSizeBudget,
            toolchainFingerprint = options.ToolchainFingerprint,
            generatedFiles,
        };
        await WriteTextAsync(
            ownershipPath,
            JsonSerializer.Serialize(ownership, s_jsonOptions),
            cancellationToken).ConfigureAwait(false);

        return new AndroidPlayerProjectLayout(
            root,
            projectPath,
            manifestPath,
            ownershipPath,
            copiedLockPath,
            assemblyName,
            options.MinSdkVersion,
            abis,
            rids,
            managedAssemblyNames,
            nativeAudioAbis,
            options.OutputMode,
            includesShaderPackage,
            includesAssetPayload,
            includesAssetPayload ? Path.Combine(root, "Assets", "xengine", "android-assets.json") : null,
            options.AssetPackagingMode,
            options.PackageSizeBudget);
    }

    private static void ValidateOptions(AndroidPlayerProjectOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectName))
            throw new ArgumentException("Android project name is required.", nameof(options));
        if (!ApplicationIdRegex().IsMatch(options.ApplicationId))
            throw new ArgumentException(
                "Android application id must contain at least two lowercase Java identifier segments.",
                nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApplicationLabel))
            throw new ArgumentException("Android application label is required.", nameof(options));
        if (options.VersionCode <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Android version code must be positive.");
        if (string.IsNullOrWhiteSpace(options.VersionName))
            throw new ArgumentException("Android version name is required.", nameof(options));
        if (options.MinSdkVersion < 24)
            throw new ArgumentOutOfRangeException(nameof(options), "XEngine Android requires min SDK 24 or newer.");
        if (options.TargetSdkVersion < options.MinSdkVersion)
            throw new ArgumentOutOfRangeException(nameof(options), "Target SDK cannot be lower than min SDK.");
        if (!Enum.IsDefined(options.ScreenOrientation))
            throw new ArgumentOutOfRangeException(nameof(options), "Unsupported Android screen orientation.");
        options.PackageSizeBudget.Validate();
        _ = AndroidAbiMapper.Normalize(options.TargetAbis);
        if (!string.IsNullOrWhiteSpace(options.ShaderPackageDirectory) &&
            !Directory.Exists(options.ShaderPackageDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Android shader package directory does not exist: {options.ShaderPackageDirectory}");
        }
        if (!string.IsNullOrWhiteSpace(options.AssetPayloadDirectory) &&
            !Directory.Exists(options.AssetPayloadDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Android asset payload directory does not exist: {options.AssetPayloadDirectory}");
        }
        if (!string.IsNullOrWhiteSpace(options.AssetPayloadDirectory) &&
            !File.Exists(Path.Combine(options.AssetPayloadDirectory, "android-assets.json")))
        {
            throw new InvalidDataException(
                "Android asset payload is missing android-assets.json.");
        }
        if ((!string.IsNullOrWhiteSpace(options.ShaderPackageDirectory) ||
             !string.IsNullOrWhiteSpace(options.AssetPayloadDirectory)) &&
            (options.ManagedDependencies == null ||
             !ContainsAssembly(options.ManagedDependencies.Assemblies, "XEngine.Runtime")))
        {
            throw new ArgumentException(
                "Android packaged runtime assets require XEngine.Runtime in the managed closure.",
                nameof(options));
        }
    }

    private static string CreateProject(
        AndroidPlayerProjectOptions options,
        string assemblyName,
        IReadOnlyList<string> runtimeIdentifiers,
        IReadOnlyList<string> nativeAudioAbis)
    {
        string packageFormat = options.OutputMode == AndroidOutputMode.Aab ? "aab" : "apk";
        bool safeTrimming = options.ManagedStripping == AndroidManagedStrippingMode.Safe;
        string publishTrimmed = safeTrimming
            ? "<PublishTrimmed Condition=\"'$(Configuration)' != 'Debug'\">true</PublishTrimmed>"
            : "<PublishTrimmed>false</PublishTrimmed>";
        string trimMode = safeTrimming
            ? "<TrimMode Condition=\"'$(Configuration)' != 'Debug'\">partial</TrimMode>"
            : "<TrimMode>copyused</TrimMode>";
        string trimDescriptor = safeTrimming
            ? $"<TrimmerRootDescriptor Include=\"trimming/{AndroidManagedTrimRoots.DescriptorFileName}\" />"
            : string.Empty;
        string trimWarningPolicy = safeTrimming
            ? "<ILLinkTreatWarningsAsErrors>true</ILLinkTreatWarningsAsErrors>"
            : string.Empty;
        string ridProperty = runtimeIdentifiers.Count == 1
            ? $"<RuntimeIdentifier>{Xml(runtimeIdentifiers[0])}</RuntimeIdentifier>"
            : $"<RuntimeIdentifiers>{Xml(string.Join(';', runtimeIdentifiers))}</RuntimeIdentifiers>";
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android</TargetFramework>
                <OutputType>Exe</OutputType>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>{Xml(assemblyName)}</AssemblyName>
                <RootNamespace>XEngine.Generated.Android</RootNamespace>
                <ApplicationId>{Xml(options.ApplicationId)}</ApplicationId>
                <ApplicationTitle>{Xml(options.ApplicationLabel)}</ApplicationTitle>
                <ApplicationVersion>{options.VersionCode.ToString(CultureInfo.InvariantCulture)}</ApplicationVersion>
                <ApplicationDisplayVersion>{Xml(options.VersionName)}</ApplicationDisplayVersion>
                <SupportedOSPlatformVersion>{options.MinSdkVersion.ToString(CultureInfo.InvariantCulture)}</SupportedOSPlatformVersion>
                <AndroidSdkPlatformVersion>android-{options.TargetSdkVersion.ToString(CultureInfo.InvariantCulture)}</AndroidSdkPlatformVersion>
                <AndroidUseLatestPlatformSdk>false</AndroidUseLatestPlatformSdk>
                <AndroidPackageFormat>{packageFormat}</AndroidPackageFormat>
                <XEngineManagedStrippingMode>{options.ManagedStripping}</XEngineManagedStrippingMode>
                {publishTrimmed}
                {trimMode}
                {trimWarningPolicy}
                <RunAOTCompilation>false</RunAOTCompilation>
                <EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>
                <AndroidUseDebugRuntime>false</AndroidUseDebugRuntime>
                <AndroidStoreUncompressedFileExtensions>.xenginepak;.bundle;.bytes;.xshaderpack</AndroidStoreUncompressedFileExtensions>
                {ridProperty}
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <AndroidLinkTool>r8</AndroidLinkTool>
                <AndroidCreateProguardMappingFile>true</AndroidCreateProguardMappingFile>
                <AndroidR8IgnoreWarnings>false</AndroidR8IgnoreWarnings>
              </PropertyGroup>
              <ItemGroup>
                <AndroidManifest Include="Properties/AndroidManifest.xml" />
                <AndroidResource Include="Resources/mipmap/ic_launcher.png" />
                <AndroidResource Include="Resources/values/**" />
                {(string.IsNullOrWhiteSpace(options.ShaderPackageDirectory)
                    ? string.Empty
                    : "<AndroidResource Include=\"Resources/raw/android_vulkan.xshaderpack\" />")}
                {(string.IsNullOrWhiteSpace(options.AssetPayloadDirectory)
                    ? string.Empty
                    : "<AndroidAsset Include=\"Assets/xengine/**\" />")}
                {trimDescriptor}
                <ProguardConfiguration Include="r8/xengine-r8.rules" />
                {CreateNativeAudioItems(nativeAudioAbis)}
              </ItemGroup>
              {CreateManagedReferences(options.ManagedDependencies)}
            </Project>
            """;
    }

    private static string CreateManifest(
        AndroidPlayerProjectOptions options,
        IReadOnlyList<string> permissions)
    {
        XNamespace android = "http://schemas.android.com/apk/res/android";
        var manifest = new XElement(
            "manifest",
            new XAttribute(XNamespace.Xmlns + "android", android),
            new XAttribute("package", options.ApplicationId),
            new XElement("uses-sdk",
                new XAttribute(android + "minSdkVersion", options.MinSdkVersion),
                new XAttribute(android + "targetSdkVersion", options.TargetSdkVersion)));
        for (int i = 0; i < permissions.Count; i++)
        {
            manifest.Add(new XElement(
                "uses-permission",
                new XAttribute(android + "name", permissions[i])));
        }

        manifest.Add(
            new XElement("uses-feature",
                new XAttribute(android + "name", "android.hardware.vulkan.version"),
                new XAttribute(android + "version", "4198400"),
                new XAttribute(android + "required", "true")),
            new XElement("application",
                new XAttribute(android + "allowBackup", "false"),
                new XAttribute(android + "enableOnBackInvokedCallback", "true"),
                new XAttribute(android + "extractNativeLibs", "false"),
                new XAttribute(android + "icon", "@mipmap/ic_launcher"),
                new XAttribute(android + "roundIcon", "@mipmap/ic_launcher"),
                new XAttribute(android + "resizeableActivity", options.ResizableActivity ? "true" : "false"),
                new XAttribute(android + "usesCleartextTraffic", "false"),
                new XAttribute(android + "theme", "@style/Theme.XEngine.Splash")));
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), manifest);
        return document.Declaration + Environment.NewLine + document.ToString(SaveOptions.None);
    }

    internal static IReadOnlyList<string> NormalizePermissions(IEnumerable<string> additionalPermissions)
    {
        ArgumentNullException.ThrowIfNull(additionalPermissions);
        var permissions = new SortedSet<string>(s_requiredPermissions, StringComparer.Ordinal);
        foreach (string permission in additionalPermissions)
        {
            if (string.IsNullOrWhiteSpace(permission))
                throw new ArgumentException("Android permission names cannot be empty.", nameof(additionalPermissions));

            string normalized = permission.Trim();
            if (!IsValidPermissionName(normalized))
            {
                throw new ArgumentException(
                    $"Android permission '{permission}' must be a package-style name such as android.permission.CAMERA.",
                    nameof(additionalPermissions));
            }
            permissions.Add(normalized);
        }

        var result = new string[permissions.Count];
        permissions.CopyTo(result);
        return result;
    }

    internal static bool IsValidPermissionName(string permission) =>
        PermissionNameRegex().IsMatch(permission);

    private static string CreateMainActivity(
        AndroidPlayerProjectOptions options,
        IReadOnlyList<string> permissions)
    {
        string label = JsonSerializer.Serialize(options.ApplicationLabel);
        string declaredPermissions = JsonSerializer.Serialize(permissions);
        string assetBackend = string.IsNullOrWhiteSpace(options.AssetPayloadDirectory) ? "null" : "_assetBackend";
        string expectedSceneGuid = options.DefaultSceneGuid == Guid.Empty
            ? "Guid.Empty"
            : $"new Guid(\"{options.DefaultSceneGuid:D}\")";
        string[] managedAssemblyNames = options.ManagedDependencies?.Assemblies
            .Select(static assembly => assembly.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        string managedAssemblies = string.Join(", ", managedAssemblyNames.Select(static name => JsonSerializer.Serialize(name)));
        return $$"""
            using Android.App;
            using Android.Content;
            using Android.Content.PM;
            using Android.Content.Res;
            using Android.Graphics;
            using Android.Hardware.Input;
            using Android.Net;
            using Android.Media;
            using Android.OS;
            using Android.Runtime;
            using Android.Views;
            using Android.Views.InputMethods;
            using Android.Window;

            using System;
            using System.Runtime.InteropServices;
            using System.Collections.Generic;
            using System.IO;
            using System.Linq;
            using System.Reflection;
            using System.Security.Cryptography;
            using System.Text.Json;
            using System.Threading;
            using System.Threading.Tasks;

            using XEngine.Runtime;
            using EngineScreenOrientation = XEngine.Runtime.ScreenOrientation;
            using ScreenOrientation = global::Android.Content.PM.ScreenOrientation;
            {{(string.IsNullOrWhiteSpace(options.ShaderPackageDirectory) &&
                string.IsNullOrWhiteSpace(options.AssetPayloadDirectory)
                ? string.Empty
                : "using XEngine.Runtime.AssetStreaming;\n")}}
            {{(string.IsNullOrWhiteSpace(options.ShaderPackageDirectory) && !options.EnableDevelopmentShaderTrace
                ? string.Empty
                : "using XEngine.Runtime.RHI.Shaders;\n")}}
            using XEngine.Runtime.Platforms.Android;

            namespace XEngine.Generated.Android;

            [Activity(
                Label = {{label}},
                MainLauncher = true,
                Exported = true,
                ScreenOrientation = {{GetScreenOrientation(options.ScreenOrientation)}},
                ConfigurationChanges = ConfigChanges.Orientation |
                    ConfigChanges.ScreenSize |
                    ConfigChanges.UiMode |
                    ConfigChanges.KeyboardHidden)]
            public sealed class MainActivity : Activity,
                ISurfaceHolderCallback,
                Choreographer.IFrameCallback,
                AudioManager.IOnAudioFocusChangeListener,
                IOnBackInvokedCallback,
                InputManager.IInputDeviceListener
            {
                private static readonly string[] s_declaredPermissions = {{declaredPermissions}};
                private AndroidPlatformHost? _host;
                private XEngineAndroidGame? _game;
                private XEngineSurfaceView? _surfaceView;
                private nint _nativeWindow;
                private bool _resumed;
                private bool _framePosted;
                private bool _backCallbackRegistered;
                private AudioManager? _audioManager;
                private InputManager? _inputManager;
                private AudioFocusRequestClass? _audioFocusRequest;
                private XEngineAudioDeviceCallback? _audioDeviceCallback;
                private Handler? _audioHandler;
                private Handler? _gamepadHandler;
                private bool _hasAudioFocus;
                private IDisposable? _platformServicesRegistration;
                private AndroidPermissionRequestCoordinator? _permissionRequests;
                private const int PlatformPermissionRequestCode = 0x5845;
                private const string PermissionPreferencesName = "xengine.permissions";
                private const string BuildIdIntentExtra = "xengine.build-id";
                {{(string.IsNullOrWhiteSpace(options.AssetPayloadDirectory)
                    ? string.Empty
                    : "private AssetVirtualFileSystem? _assetVfs;\n                private MaterializingAssetStreamProvider? _assetProvider;\n                private PlayerAssetBackendBase? _assetBackend;")}}
                {{(options.EnableDevelopmentShaderTrace
                    ? "private IDisposable? _shaderUsageTrace;\n"
                    : string.Empty)}}

                protected override void OnCreate(Bundle? savedInstanceState)
                {
                    base.OnCreate(savedInstanceState);
                    XEnginePlayerRoots.Preserve();
                    LogDeploymentBuildId(Intent);

                    {{(string.IsNullOrWhiteSpace(options.AssetPayloadDirectory)
                        ? string.Empty
                        : "ConfigureAssetHost();")}}
                    {{(string.IsNullOrWhiteSpace(options.ShaderPackageDirectory)
                        ? string.Empty
                        : "ConfigureShaderArtifacts();")}}
                    {{(options.EnableDevelopmentShaderTrace
                        ? "ConfigureShaderUsageTrace();"
                        : string.Empty)}}

                    _permissionRequests = new AndroidPermissionRequestCoordinator(
                        GetPermissionStatus,
                        DispatchPermissionRequest);
                    _platformServicesRegistration = ConfigurePlatformServices();
                    {{(string.IsNullOrWhiteSpace(options.AssetPayloadDirectory)
                        ? string.Empty
                        : "PlayerSettingsLoader.ApplyOrientation(_assetProvider!);")}}
                    _host = new AndroidPlatformHost(ReadClipboard, WriteClipboard, SetSoftKeyboardVisible);
                    _game = new XEngineAndroidGame(
                        {{assetBackend}},
                        {{expectedSceneGuid}},
                        [{{managedAssemblies}}]);
                    _game.InitializePlatformHost({{label}}, 1, 1);
                    global::Android.Util.Log.Info(
                        "XEngine",
                        $"platform locale={PlatformServices.LocaleName} timezone={PlatformServices.TimeZoneId} network={PlatformServices.NetworkReachability}");
                    ConfigureAudioHost();
                    ConfigureGamepadHost();
                    ConfigureEdgeToEdge();
                    _surfaceView = new XEngineSurfaceView(this, _host);
                    _surfaceView.Holder!.AddCallback(this);
                    SetContentView(_surfaceView);
                    RegisterBackCallback();
                }

                public override bool DispatchKeyEvent(KeyEvent? e)
                {
                    if (e?.KeyCode == Keycode.Back)
                    {
                        if (e.Action == KeyEventActions.Up)
                            HandleSystemBack();
                        return true;
                    }
                    if (e != null &&
                        IsGamepadSource((int)e.Source) &&
                        (e.Action == KeyEventActions.Down || e.Action == KeyEventActions.Up) &&
                        _host?.HandleGamepadKey(e.DeviceId, (int)e.KeyCode, e.Action == KeyEventActions.Down) == true)
                        return true;
                    if (e != null &&
                        (e.Action == KeyEventActions.Down || e.Action == KeyEventActions.Up) &&
                        _host?.HandleKey((int)e.KeyCode, e.Action == KeyEventActions.Down) == true)
                        return true;
                    return base.DispatchKeyEvent(e);
                }

                public override bool DispatchGenericMotionEvent(MotionEvent? e)
                {
                    if (e != null &&
                        e.ActionMasked == MotionEventActions.Move &&
                        IsGamepadSource((int)e.Source) &&
                        _host?.HandleGamepadMotion(
                            e.DeviceId,
                            AxisValue(e, 0),
                            AxisValue(e, 1),
                            PreferAxis(AxisValue(e, 11), AxisValue(e, 12)),
                            PreferAxis(AxisValue(e, 14), AxisValue(e, 13)),
                            Math.Max(AxisValue(e, 17), AxisValue(e, 23)),
                            Math.Max(AxisValue(e, 18), AxisValue(e, 22)),
                            AxisValue(e, 15),
                            AxisValue(e, 16)) == true)
                        return true;
                    return base.DispatchGenericMotionEvent(e);
                }

                public void OnBackInvoked()
                {
                    HandleSystemBack();
                }

                public override void OnRequestPermissionsResult(
                    int requestCode,
                    string[]? permissions,
                    global::Android.Content.PM.Permission[]? grantResults)
                {
                    base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
                    if (requestCode != PlatformPermissionRequestCode || _permissionRequests == null)
                        return;
                    string? permission = permissions != null && permissions.Length > 0
                        ? permissions[0]
                        : null;
                    PlatformPermissionStatus status;
                    if (string.IsNullOrWhiteSpace(permission))
                    {
                        status = PlatformPermissionStatus.Unsupported;
                    }
                    else if (grantResults != null &&
                             grantResults.Length > 0 &&
                             grantResults[0] == global::Android.Content.PM.Permission.Granted)
                    {
                        status = PlatformPermissionStatus.Granted;
                    }
                    else
                    {
                        status = ShouldShowPermissionRationale(permission)
                            ? PlatformPermissionStatus.Denied
                            : PlatformPermissionStatus.DeniedPermanently;
                    }
                    if (permission != null)
                        RecordPermissionDecision(permission, status);
                    bool matched = _permissionRequests?.Complete(permission, status) == true;
                    global::Android.Util.Log.Info(
                        "XEngine",
                        $"permission result name={permission ?? "<missing>"} status={status} matched={matched}");
                }

                public void OnAudioFocusChange(AudioFocus focusChange)
                {
                    _hasAudioFocus = focusChange == AudioFocus.Gain;
                    _host?.AudioFocusChanged((int)focusChange);
                    global::Android.Util.Log.Info(
                        "XEngine",
                        $"audio focus={(int)focusChange} running={_host?.AudioHandler.Stats.IsPlaybackRunning}");
                }

                public override void OnWindowFocusChanged(bool hasFocus)
                {
                    base.OnWindowFocusChanged(hasFocus);
                    _host?.FocusChanged(hasFocus);
                }

                protected override void OnResume()
                {
                    base.OnResume();
                    _resumed = true;
                    RequestAudioFocus();
                    ReportCurrentOrientation();
                    _host?.Resume();
                    LogPermissionStatuses();
                    ScheduleFrame();
                }

                protected override void OnPause()
                {
                    StopFrames();
                    _resumed = false;
                    _host?.Pause();
                    AbandonAudioFocus();
                    base.OnPause();
                }

                protected override void OnDestroy()
                {
                    StopFrames();
                    {{(string.IsNullOrWhiteSpace(options.AssetPayloadDirectory)
                        ? string.Empty
                        : "DisposeAssetHost();")}}
                    {{(options.EnableDevelopmentShaderTrace
                        ? "_shaderUsageTrace?.Dispose();\n                     _shaderUsageTrace = null;"
                        : string.Empty)}}
                    DisposeGamepadHost();
                    DisposeAudioHost();
                    _permissionRequests?.Dispose();
                    _permissionRequests = null;
                    _platformServicesRegistration?.Dispose();
                    _platformServicesRegistration = null;
                    UnregisterBackCallback();
                    ReleaseSurface();
                    _surfaceView?.Holder?.RemoveCallback(this);
                    _host?.Dispose();
                    _host = null;
                    _game = null;
                    base.OnDestroy();
                }

                public void SurfaceCreated(ISurfaceHolder holder)
                {
                    if (_nativeWindow != nint.Zero)
                        return;
                    _nativeWindow = AndroidNativeWindow.Acquire(holder.Surface!);
                    _host!.AttachSurface(
                        _nativeWindow,
                        Math.Max(1, _surfaceView!.Width),
                        Math.Max(1, _surfaceView.Height));
                    ScheduleFrame();
                }

                public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
                {
                    if (_nativeWindow == nint.Zero)
                        SurfaceCreated(holder);
                    global::Android.Util.Log.Info(
                        "XEngine",
                        $"surface changed={width}x{height} view={Math.Max(1, _surfaceView?.Width ?? 1)}x{Math.Max(1, _surfaceView?.Height ?? 1)}");
                    _host?.ResizeSurface(width, height);
                }

                public void SurfaceDestroyed(ISurfaceHolder holder)
                {
                    StopFrames();
                    ReleaseSurface();
                }

                public void DoFrame(long frameTimeNanos)
                {
                    _framePosted = false;
                    if (!_resumed || _nativeWindow == nint.Zero)
                        return;
                    _host?.Tick(frameTimeNanos);
                    ScheduleFrame();
                }

                private void ScheduleFrame()
                {
                    if (_framePosted || !_resumed || _nativeWindow == nint.Zero)
                        return;
                    _framePosted = true;
                    Choreographer.Instance!.PostFrameCallback(this);
                }

                private void StopFrames()
                {
                    if (!_framePosted)
                        return;
                    Choreographer.Instance!.RemoveFrameCallback(this);
                    _framePosted = false;
                }

                private void ReleaseSurface()
                {
                    if (_nativeWindow == nint.Zero)
                        return;
                    _host?.DetachSurface();
                    AndroidNativeWindow.Release(_nativeWindow);
                    _nativeWindow = nint.Zero;
                }

                private void HandleSystemBack()
                {
                    if (_host?.HandleBack() != true)
                        Finish();
                }

                private static void LogDeploymentBuildId(Intent? intent)
                {
                    string? buildId = intent?.GetStringExtra(BuildIdIntentExtra);
                    if (!string.IsNullOrWhiteSpace(buildId))
                        global::Android.Util.Log.Info("XEngine", "build-id=" + buildId);
                }

                private void RegisterBackCallback()
                {
                    if (!OperatingSystem.IsAndroidVersionAtLeast(33))
                        return;
                    OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(
                        IOnBackInvokedDispatcher.PriorityDefault,
                        this);
                    _backCallbackRegistered = true;
                }

                private void UnregisterBackCallback()
                {
                    if (!_backCallbackRegistered || !OperatingSystem.IsAndroidVersionAtLeast(33))
                        return;
                    OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(this);
                    _backCallbackRegistered = false;
                }

                private string ReadClipboard()
                {
                    ClipboardManager? clipboard = GetSystemService(ClipboardService) as ClipboardManager;
                    return clipboard?.PrimaryClip?.GetItemAt(0)?.CoerceToText(this)?.ToString() ?? string.Empty;
                }

                private void WriteClipboard(string value)
                {
                    ClipboardManager? clipboard = GetSystemService(ClipboardService) as ClipboardManager;
                    if (clipboard != null)
                        clipboard.PrimaryClip = ClipData.NewPlainText("XEngine", value);
                }

                private void SetSoftKeyboardVisible(bool visible)
                {
                    if (_surfaceView == null)
                        return;
                    InputMethodManager? manager = GetSystemService(InputMethodService) as InputMethodManager;
                    if (visible)
                    {
                        _surfaceView.RequestFocus();
                        manager?.RestartInput(_surfaceView);
                        manager?.ShowSoftInput(_surfaceView, ShowFlags.Implicit);
                    }
                    else if (_surfaceView.WindowToken != null)
                    {
                        manager?.HideSoftInputFromWindow(_surfaceView.WindowToken, HideSoftInputFlags.None);
                    }
                }

                private void ConfigureEdgeToEdge()
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
                    {
                        Window?.SetDecorFitsSystemWindows(false);
                        return;
                    }

                    if (!OperatingSystem.IsAndroidVersionAtLeast(30) && Window?.DecorView is View decorView)
                    {
                        decorView.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.LayoutStable |
                            SystemUiFlags.LayoutFullscreen |
                            SystemUiFlags.LayoutHideNavigation);
                    }
                }

                private const int SourceDpad = 0x00000201;
                private const int SourceGamepad = 0x00000401;
                private const int SourceJoystick = 0x01000010;

                private void ConfigureGamepadHost()
                {
                    _inputManager = GetSystemService(InputService) as InputManager;
                    if (_inputManager == null)
                        return;
                    _gamepadHandler = new Handler(Looper.MainLooper!);
                    _inputManager.RegisterInputDeviceListener(this, _gamepadHandler);
                    int[]? deviceIds = InputDevice.GetDeviceIds();
                    if (deviceIds == null)
                        return;
                    for (int i = 0; i < deviceIds.Length; i++)
                        RegisterGamepadDevice(deviceIds[i]);
                }

                private void DisposeGamepadHost()
                {
                    if (_inputManager != null)
                        _inputManager.UnregisterInputDeviceListener(this);
                    _gamepadHandler?.Dispose();
                    _gamepadHandler = null;
                    _inputManager = null;
                }

                public void OnInputDeviceAdded(int deviceId) => RegisterGamepadDevice(deviceId);

                public void OnInputDeviceRemoved(int deviceId) => _host?.GamepadDisconnected(deviceId);

                public void OnInputDeviceChanged(int deviceId) => RegisterGamepadDevice(deviceId);

                private void RegisterGamepadDevice(int deviceId)
                {
                    InputDevice? device = InputDevice.GetDevice(deviceId);
                    if (device != null && IsGamepadSource((int)device.Sources))
                        _host?.GamepadConnected(deviceId);
                }

                private static bool IsGamepadSource(int source) =>
                    (source & SourceGamepad) == SourceGamepad ||
                    (source & SourceJoystick) == SourceJoystick ||
                    (source & SourceDpad) == SourceDpad;

                private static float AxisValue(MotionEvent motionEvent, int axis) =>
                    motionEvent.GetAxisValue((Axis)axis);

                private static float PreferAxis(float primary, float fallback) =>
                    MathF.Abs(primary) >= 0.0001f ? primary : fallback;

                private IDisposable ConfigurePlatformServices()
                {
                    return PlatformServices.Configure(new AndroidPlatformServices(
                        localeName: () => Java.Util.Locale.Default?.ToLanguageTag() ?? string.Empty,
                        timeZoneId: () => Java.Util.TimeZone.Default?.ID ?? string.Empty,
                        networkReachability: GetNetworkReachability,
                        openUrl: TryOpenUrl,
                        setKeepScreenOn: SetKeepScreenOn,
                        vibrate: TryVibrate,
                        getPermissionStatus: GetPermissionStatus,
                        shouldShowPermissionRationale: ShouldShowPermissionRationale,
                        requestPermissionAsync: RequestPermissionAsync,
                        openApplicationSettings: TryOpenApplicationSettings,
                        trySetScreenOrientation: SetEngineScreenOrientation));
                }

                private PlatformNetworkReachability GetNetworkReachability()
                {
                    ConnectivityManager? manager = GetSystemService(ConnectivityService) as ConnectivityManager;
                    Network? network = manager?.ActiveNetwork;
                    NetworkCapabilities? capabilities = network == null ? null : manager?.GetNetworkCapabilities(network);
                    if (capabilities == null)
                        return PlatformNetworkReachability.NotReachable;
                    if (capabilities.HasTransport(TransportType.Wifi) ||
                        capabilities.HasTransport(TransportType.Ethernet))
                        return PlatformNetworkReachability.ReachableViaLocalAreaNetwork;
                    if (capabilities.HasTransport(TransportType.Cellular))
                        return PlatformNetworkReachability.ReachableViaCarrierDataNetwork;
                    return PlatformNetworkReachability.Unknown;
                }

                private bool TryOpenUrl(string url)
                {
                    try
                    {
                        using var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
                        intent.AddFlags(ActivityFlags.NewTask);
                        StartActivity(intent);
                        return true;
                    }
                    catch (ActivityNotFoundException)
                    {
                        return false;
                    }
                }

                private bool SetKeepScreenOn(bool enabled)
                {
                    if (enabled)
                        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
                    else
                        Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
                    return Window != null;
                }

                private bool SetEngineScreenOrientation(EngineScreenOrientation requested, ScreenAutoRotatePolicy policy)
                {
                    // The engine calls from its frame thread; RequestedOrientation is Activity
                    // state, so hop to the UI thread like every other framework call here.
                    RunOnUiThread(() =>
                    {
                        try { RequestedOrientation = MapScreenOrientation(requested, policy); }
                        catch (global::Java.Lang.Exception exception)
                        {
                            global::Android.Util.Log.Warn("XEngine", "requested orientation failed: " + exception.Message);
                        }
                    });
                    return true;
                }

                // Android.Content.PM and XEngine.Runtime both declare ScreenOrientation and both
                // namespaces are imported; every bare mention here is the Android enum via the
                // file-level using alias, the engine one goes through EngineScreenOrientation.
                private static global::Android.Content.PM.ScreenOrientation MapScreenOrientation(
                    EngineScreenOrientation requested,
                    ScreenAutoRotatePolicy policy)
                {
                    if (requested == EngineScreenOrientation.AutoRotation)
                        return MapAutoRotationPolicy(policy);
                    return requested switch
                    {
                        EngineScreenOrientation.Portrait => ScreenOrientation.Portrait,
                        EngineScreenOrientation.PortraitUpsideDown => ScreenOrientation.ReversePortrait,
                        // Unity's LandscapeLeft = home/gesture bar on the right, which Android
                        // calls plain Landscape on a portrait-natural device (and vice versa).
                        EngineScreenOrientation.LandscapeLeft => ScreenOrientation.Landscape,
                        EngineScreenOrientation.LandscapeRight => ScreenOrientation.ReverseLandscape,
                        _ => ScreenOrientation.Unspecified,
                    };
                }

                private static global::Android.Content.PM.ScreenOrientation MapAutoRotationPolicy(ScreenAutoRotatePolicy policy)
                {
                    bool portraitFamily = policy.AutoRotateToPortrait || policy.AutoRotateToPortraitUpsideDown;
                    bool landscapeFamily = policy.AutoRotateToLandscapeLeft || policy.AutoRotateToLandscapeRight;
                    if (portraitFamily && landscapeFamily)
                    {
                        // Android has no sensor mode for mixed sets (e.g. Portrait +
                        // LandscapeLeft); full sensor is the closest OS behavior.
                        return ScreenOrientation.FullSensor;
                    }
                    if (portraitFamily)
                    {
                        if (!policy.AutoRotateToPortrait)
                            return ScreenOrientation.ReversePortrait;
                        return policy.AutoRotateToPortraitUpsideDown
                            ? ScreenOrientation.SensorPortrait
                            : ScreenOrientation.Portrait;
                    }
                    if (landscapeFamily)
                    {
                        if (!policy.AutoRotateToLandscapeLeft)
                            return ScreenOrientation.ReverseLandscape;
                        return policy.AutoRotateToLandscapeRight
                            ? ScreenOrientation.SensorLandscape
                            : ScreenOrientation.Landscape;
                    }
                    // Every orientation disabled: keep whatever the window is in now.
                    return ScreenOrientation.Unspecified;
                }

                private void ReportCurrentOrientation()
                {
                    try
                    {
                        global::Android.Views.Display? display = WindowManager?.DefaultDisplay;
                        if (display == null)
                            return;
                        int rotation = (int)display.Rotation;
                        bool naturalPortrait = IsDisplayNaturallyPortrait(display, rotation);
                        EngineScreenOrientation orientation = (naturalPortrait, rotation) switch
                        {
                            (true, 1) => EngineScreenOrientation.LandscapeLeft,
                            (true, 2) => EngineScreenOrientation.PortraitUpsideDown,
                            (true, 3) => EngineScreenOrientation.LandscapeRight,
                            (false, 0) => EngineScreenOrientation.LandscapeLeft,
                            (false, 1) => EngineScreenOrientation.PortraitUpsideDown,
                            (false, 2) => EngineScreenOrientation.LandscapeRight,
                            (false, 3) => EngineScreenOrientation.Portrait,
                            _ => EngineScreenOrientation.Portrait,
                        };
                        XEngine.Runtime.Screen.ReportDeviceOrientation(orientation);
                    }
                    catch (global::Java.Lang.Exception)
                    {
                        // Display queries can throw mid-teardown; the next change re-reports.
                    }
                }

                private static bool IsDisplayNaturallyPortrait(global::Android.Views.Display display, int rotation)
                {
                    var size = new global::Android.Graphics.Point();
                    display.GetRealSize(size);
                    // While rotated 90/270 the real size reports the swapped (landscape) frame,
                    // so a wider-than-tall size proves the natural frame is portrait.
                    if (rotation == 1 || rotation == 3)
                        return size.X > size.Y;
                    return size.Y >= size.X;
                }

                public override void OnConfigurationChanged(Configuration? newConfig)
                {
                    base.OnConfigurationChanged(newConfig);
                    ReportCurrentOrientation();
                }

                private bool TryVibrate(TimeSpan duration)
                {
                    Vibrator? vibrator = GetSystemService(VibratorService) as Vibrator;
                    if (vibrator == null || !vibrator.HasVibrator)
                        return false;
                    long milliseconds = Math.Max(1L, (long)duration.TotalMilliseconds);
                    if (OperatingSystem.IsAndroidVersionAtLeast(26))
                        vibrator.Vibrate(VibrationEffect.CreateOneShot(milliseconds, VibrationEffect.DefaultAmplitude));
                    else
                        vibrator.Vibrate(milliseconds);
                    return true;
                }

                private PlatformPermissionStatus GetPermissionStatus(string permission)
                {
                    if (!IsPermissionDeclared(permission))
                        return PlatformPermissionStatus.NotDeclared;
                    try
                    {
                        if (CheckSelfPermission(permission) == global::Android.Content.PM.Permission.Granted)
                        {
                            ClearPermanentPermissionDenial(permission);
                            return PlatformPermissionStatus.Granted;
                        }
                        return IsPermissionPermanentlyDenied(permission)
                            ? PlatformPermissionStatus.DeniedPermanently
                            : PlatformPermissionStatus.Denied;
                    }
                    catch (global::Java.Lang.SecurityException)
                    {
                        return PlatformPermissionStatus.Unsupported;
                    }
                }

                private ValueTask<PlatformPermissionStatus> RequestPermissionAsync(
                    string permission,
                    CancellationToken cancellationToken)
                {
                    return _permissionRequests?.RequestAsync(permission, cancellationToken) ??
                        ValueTask.FromResult(PlatformPermissionStatus.Unsupported);
                }

                private void DispatchPermissionRequest(string permission)
                {
                    RunOnUiThread(() =>
                    {
                        if (IsFinishing || IsDestroyed)
                        {
                            _permissionRequests?.Complete(permission, PlatformPermissionStatus.Unsupported);
                            return;
                        }
                        try
                        {
                            RequestPermissions([permission], PlatformPermissionRequestCode);
                        }
                        catch (global::Java.Lang.SecurityException)
                        {
                            _permissionRequests?.Complete(permission, PlatformPermissionStatus.Unsupported);
                        }
                    });
                }

                private bool ShouldShowPermissionRationale(string permission)
                {
                    if (!IsPermissionDeclared(permission))
                        return false;
                    try
                    {
                        return CheckSelfPermission(permission) != global::Android.Content.PM.Permission.Granted &&
                            ShouldShowRequestPermissionRationale(permission);
                    }
                    catch (global::Java.Lang.SecurityException)
                    {
                        return false;
                    }
                }

                private bool TryOpenApplicationSettings()
                {
                    try
                    {
                        using var intent = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings);
                        intent.SetData(global::Android.Net.Uri.Parse("package:" + PackageName));
                        StartActivity(intent);
                        return true;
                    }
                    catch (global::Android.Content.ActivityNotFoundException)
                    {
                        return false;
                    }
                }

                private static bool IsPermissionDeclared(string permission) =>
                    Array.BinarySearch(s_declaredPermissions, permission, StringComparer.Ordinal) >= 0;

                private bool IsPermissionPermanentlyDenied(string permission) =>
                    GetSharedPreferences(PermissionPreferencesName, FileCreationMode.Private)?
                        .GetBoolean("permanent:" + permission, false) == true;

                private void ClearPermanentPermissionDenial(string permission)
                {
                    if (!IsPermissionPermanentlyDenied(permission))
                        return;
                    RecordPermissionDecision(permission, PlatformPermissionStatus.Granted);
                }

                private void RecordPermissionDecision(string permission, PlatformPermissionStatus status)
                {
                    ISharedPreferences? preferences =
                        GetSharedPreferences(PermissionPreferencesName, FileCreationMode.Private);
                    using ISharedPreferencesEditor? editor = preferences?.Edit();
                    editor?.PutBoolean("requested:" + permission, true);
                    editor?.PutBoolean(
                        "permanent:" + permission,
                        status == PlatformPermissionStatus.DeniedPermanently);
                    editor?.Apply();
                }

                private void LogPermissionStatuses()
                {
                    for (int i = 0; i < s_declaredPermissions.Length; i++)
                    {
                        string permission = s_declaredPermissions[i];
                        global::Android.Util.Log.Info(
                            "XEngine",
                            $"permission status name={permission} status={GetPermissionStatus(permission)} rationale={ShouldShowPermissionRationale(permission)}");
                    }
                }

                private void ConfigureAudioHost()
                {
                    _audioManager = GetSystemService(AudioService) as AudioManager;
                    if (_audioManager == null || _host == null)
                        return;

                    int sampleRate = ReadAudioProperty(AudioManager.PropertyOutputSampleRate, 48000);
                    int framesPerBuffer = ReadAudioProperty(AudioManager.PropertyOutputFramesPerBuffer, 192);
                    bool nativeReady = _host.InitializeAudio(sampleRate, framesPerBuffer);
                    _audioHandler = new Handler(Looper.MainLooper!);
                    _audioDeviceCallback = new XEngineAudioDeviceCallback(this);
                    _audioManager.RegisterAudioDeviceCallback(_audioDeviceCallback, _audioHandler);
                    ReportAudioRoute();

                    if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    {
                        AudioAttributes attributes = new AudioAttributes.Builder()
                            .SetUsage(AudioUsageKind.Game)
                            .SetContentType(AudioContentType.Music)
                            .Build();
                        _audioFocusRequest = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
                            .SetAudioAttributes(attributes)
                            .SetOnAudioFocusChangeListener(this, _audioHandler)
                            .SetWillPauseWhenDucked(true)
                            .Build();
                    }
                    global::Android.Util.Log.Info(
                        "XEngine",
                        $"audio configured native={nativeReady} sampleRate={sampleRate} frames={framesPerBuffer}");
                }

                private void RequestAudioFocus()
                {
                    if (_audioManager == null || _host == null)
                        return;
                    AudioFocusRequest result = OperatingSystem.IsAndroidVersionAtLeast(26) && _audioFocusRequest != null
                        ? _audioManager.RequestAudioFocus(_audioFocusRequest)
                        : _audioManager.RequestAudioFocus(this, global::Android.Media.Stream.Music, AudioFocus.Gain);
                    _hasAudioFocus = result == AudioFocusRequest.Granted;
                    _host.AudioFocusChanged(_hasAudioFocus
                        ? (int)AudioFocus.Gain
                        : (int)AudioFocus.LossTransient);
                    global::Android.Util.Log.Info("XEngine", $"audio focus-request={(int)result}");
                }

                private void AbandonAudioFocus()
                {
                    if (!_hasAudioFocus || _audioManager == null)
                        return;
                    if (OperatingSystem.IsAndroidVersionAtLeast(26) && _audioFocusRequest != null)
                        _audioManager.AbandonAudioFocusRequest(_audioFocusRequest);
                    else
                        _audioManager.AbandonAudioFocus(this);
                    _hasAudioFocus = false;
                    _host?.AudioFocusChanged((int)AudioFocus.LossTransient);
                    global::Android.Util.Log.Info("XEngine", "audio focus-abandoned");
                }

                private void ReportAudioRoute(AudioDeviceInfo? preferred = null)
                {
                    if (_audioManager == null || _host == null)
                        return;
                    AudioDeviceInfo? output = preferred;
                    if (output == null || !output.IsSink)
                    {
                        AudioDeviceInfo[]? devices = _audioManager.GetDevices(GetDevicesTargets.Outputs);
                        if (devices == null)
                            return;
                        for (int i = 0; i < devices.Length; i++)
                        {
                            if (devices[i].IsSink)
                            {
                                output = devices[i];
                                break;
                            }
                        }
                    }
                    int sampleRate = ReadAudioProperty(AudioManager.PropertyOutputSampleRate, 48000);
                    int framesPerBuffer = ReadAudioProperty(AudioManager.PropertyOutputFramesPerBuffer, 192);
                    int deviceType = output == null ? 0 : (int)output.Type;
                    _host.AudioRouteChanged(deviceType, sampleRate, framesPerBuffer);
                    global::Android.Util.Log.Info(
                        "XEngine",
                        $"audio route={deviceType} sampleRate={sampleRate} frames={framesPerBuffer}");
                }

                private int ReadAudioProperty(string property, int fallback)
                {
                    return int.TryParse(_audioManager?.GetProperty(property), out int value) && value > 0
                        ? value
                        : fallback;
                }

                private void DisposeAudioHost()
                {
                    AbandonAudioFocus();
                    if (_audioManager != null && _audioDeviceCallback != null)
                        _audioManager.UnregisterAudioDeviceCallback(_audioDeviceCallback);
                    _audioDeviceCallback?.Dispose();
                    _audioDeviceCallback = null;
                    _audioFocusRequest?.Dispose();
                    _audioFocusRequest = null;
                    _audioHandler?.Dispose();
                    _audioHandler = null;
                    _audioManager = null;
                }

                private sealed class XEngineAudioDeviceCallback : AudioDeviceCallback
                {
                    private readonly MainActivity _activity;

                    internal XEngineAudioDeviceCallback(MainActivity activity)
                    {
                        _activity = activity;
                    }

                    public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices)
                    {
                        AudioDeviceInfo? output = null;
                        if (addedDevices != null)
                        {
                            for (int i = 0; i < addedDevices.Length; i++)
                            {
                                if (addedDevices[i].IsSink)
                                {
                                    output = addedDevices[i];
                                    break;
                                }
                            }
                        }
                        _activity.ReportAudioRoute(output);
                    }

                    public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices)
                    {
                        _activity.ReportAudioRoute();
                    }
                }

                {{(string.IsNullOrWhiteSpace(options.AssetPayloadDirectory)
                    ? string.Empty
                    : CreateAssetHostSource(options))}}
                {{(string.IsNullOrWhiteSpace(options.ShaderPackageDirectory)
                    ? string.Empty
                    : """
                private void ConfigureShaderArtifacts()
                {
                    System.IO.Stream packageStream = Resources!.OpenRawResource(Resource.Raw.android_vulkan);
                    var provider = new ZipAssetStreamProvider(
                        packageStream,
                        "Android res/raw/android_vulkan.xshaderpack");
                    ShaderArtifactRuntimeLibrary.Configure(provider);
                }
                """)}}
                {{(options.EnableDevelopmentShaderTrace
                    ? """
                private void ConfigureShaderUsageTrace()
                {
                    _shaderUsageTrace = ShaderArtifactRuntimeLibrary.EnableDevelopmentUsageTrace();
                    global::Android.Util.Log.Info(
                        "XEngine",
                        "shader usage trace=" + global::System.IO.Path.Combine(
                            XEngine.Runtime.Application.PersistentDataPath,
                            "Diagnostics",
                            ShaderArtifactUsageTrace.FileName));
                }
                """
                    : string.Empty)}}
            }

            internal sealed class XEngineAndroidGame : XEngine.Runtime.Game
            {
                private readonly XEngine.Runtime.PlayerAssetBackendBase? _assetBackend;
                private readonly Guid _expectedSceneGuid;
                private readonly string[] _managedAssemblyNames;

                internal XEngineAndroidGame(
                    XEngine.Runtime.PlayerAssetBackendBase? assetBackend,
                    Guid expectedSceneGuid,
                    string[] managedAssemblyNames)
                {
                    _assetBackend = assetBackend;
                    _expectedSceneGuid = expectedSceneGuid;
                    _managedAssemblyNames = managedAssemblyNames;
                }

                public override void Initialize()
                {
                    XEngine.Runtime.Application.IsPlaying = true;
                    XEngine.Runtime.Application.IsEditor = false;
                    XEngine.Runtime.Application.IsPlayerBuild = true;
                    XEngine.Runtime.Application.ConfigureRuntimePlatform(XEngine.Runtime.ApplicationRuntimePlatform.Android);
                    XEngine.Runtime.Application.DataPath = XEngine.Runtime.Application.DataPath.Length == 0
                        ? AppContext.BaseDirectory
                        : XEngine.Runtime.Application.DataPath;

                    for (int i = 0; i < _managedAssemblyNames.Length; i++)
                    {
                        try
                        {
                            Assembly.Load(new AssemblyName(_managedAssemblyNames[i]));
                        }
                        catch (Exception exception)
                        {
                            XEngine.Runtime.Debug.LogWarning($"[Android] Managed assembly '{_managedAssemblyNames[i]}' could not be loaded: {exception.Message}");
                        }
                    }

                    XEngine.Runtime.BuiltInAssets.Initialize();
                    if (_assetBackend == null)
                    {
                        XEngine.Runtime.Debug.Log("[Android] No runtime asset backend configured; running clear-frame host.");
                        return;
                    }

                    XEngine.Runtime.AssetDatabase.Current = _assetBackend;
                    XEngine.Runtime.GameResources.Initialize(_assetBackend.ResourcesMap);
                    Guid sceneGuid = _assetBackend.DefaultSceneGuid;
                    if (_expectedSceneGuid != Guid.Empty && sceneGuid != _expectedSceneGuid)
                    {
                        XEngine.Runtime.Debug.LogError($"[Android] Asset manifest default scene {sceneGuid} does not match build scene {_expectedSceneGuid}.");
                        sceneGuid = _expectedSceneGuid;
                    }

                    if (sceneGuid == Guid.Empty)
                    {
                        XEngine.Runtime.Debug.Log("[Android] No enabled build scene; rendering clear frame.");
                        return;
                    }

                    XEngine.Runtime.Resources.Scene? scene = _assetBackend.LoadScene(sceneGuid);
                    if (scene == null)
                        XEngine.Runtime.Debug.LogError($"[Android] Failed to load default scene {sceneGuid}.");
                    else
                    {
                        XEngine.Runtime.Resources.Scene.Load(scene);
                        XEngine.Runtime.Debug.Log($"[Android] Loaded default scene {sceneGuid}.");
                    }
                }

                public override void OnUpdate(XEngine.Runtime.Resources.Scene? scene)
                {
                    _assetBackend?.TickIdleSweep();
                    scene?.Update();
                }

                public override void OnRender(XEngine.Runtime.Resources.Scene? scene) => scene?.Render();

                public override void OnGui(
                    XEngine.Runtime.Resources.Scene? scene,
                    XEngine.PaperUI.Paper paper) => scene?.OnGui(paper);
            }

            internal sealed class XEngineSurfaceView : SurfaceView
            {
                private readonly AndroidPlatformHost _host;

                internal XEngineSurfaceView(Context context, AndroidPlatformHost host) : base(context)
                {
                    _host = host;
                    FocusableInTouchMode = true;
                    RequestFocus();
                }

                public override bool OnTouchEvent(MotionEvent? e)
                {
                    if (e == null)
                        return false;

                    MotionEventActions action = e.ActionMasked;
                    int actionIndex = e.ActionIndex;
                    if (action == MotionEventActions.Down || action == MotionEventActions.PointerDown)
                    {
                        SendBegin(e, actionIndex);
                        return true;
                    }
                    if (action == MotionEventActions.Move)
                    {
                        for (int i = 0; i < e.PointerCount; i++)
                        {
                            SendHistory(e, i);
                            SendMove(e, i);
                        }
                        return true;
                    }

                    if (action == MotionEventActions.Up || action == MotionEventActions.PointerUp)
                    {
                        SendEnd(e, actionIndex, false);
                        return true;
                    }
                    if (action == MotionEventActions.Cancel)
                    {
                        for (int i = 0; i < e.PointerCount; i++)
                            SendEnd(e, i, true);
                        return true;
                    }
                    return true;
                }

                public override global::Android.Views.WindowInsets OnApplyWindowInsets(
                    global::Android.Views.WindowInsets insets)
                {
                    int left;
                    int top;
                    int right;
                    int bottom;
                    if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    {
                        var systemInsets = insets.GetInsetsIgnoringVisibility(
                            global::Android.Views.WindowInsets.Type.SystemBars() |
                            global::Android.Views.WindowInsets.Type.DisplayCutout());
                        left = Math.Max(0, systemInsets.Left);
                        top = Math.Max(0, systemInsets.Top);
                        right = Math.Max(0, systemInsets.Right);
                        bottom = Math.Max(0, systemInsets.Bottom);
                    }
                    else
                    {
                        left = Math.Max(0, insets.StableInsetLeft);
                        top = Math.Max(0, insets.StableInsetTop);
                        right = Math.Max(0, insets.StableInsetRight);
                        bottom = Math.Max(0, insets.StableInsetBottom);
                        if (OperatingSystem.IsAndroidVersionAtLeast(28) && insets.DisplayCutout is { } cutout)
                        {
                            left = Math.Max(left, cutout.SafeInsetLeft);
                            top = Math.Max(top, cutout.SafeInsetTop);
                            right = Math.Max(right, cutout.SafeInsetRight);
                            bottom = Math.Max(bottom, cutout.SafeInsetBottom);
                        }
                    }

                    _host.UpdateWindowInsets(left, top, right, bottom, Math.Max(1, Width), Math.Max(1, Height));
                    global::Android.Util.Log.Info(
                        "XEngine",
                        $"window insets={left},{top},{right},{bottom} view={Math.Max(1, Width)}x{Math.Max(1, Height)}");
                    return base.OnApplyWindowInsets(insets);
                }

                protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
                {
                    base.OnSizeChanged(width, height, oldWidth, oldHeight);
                    _host.UpdateInputViewSize(width, height);
                }

                public override bool OnCheckIsTextEditor() => true;

                public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
                {
                    if (outAttrs == null)
                        return null;
                    int inputType = _host.InputHandler.LastAndroidInputType;
                    if (inputType == 0)
                        inputType = (int)global::Android.Text.InputTypes.ClassText;
                    outAttrs.InputType = (global::Android.Text.InputTypes)inputType;
                    outAttrs.ImeOptions = (global::Android.Views.InputMethods.ImeFlags)ImeAction.Done;
                    return new XEngineInputConnection(this, _host);
                }

                private void SendBegin(MotionEvent e, int index)
                {
                    _host.TouchBegan(e.GetPointerId(index), e.GetX(index), e.GetY(index), e.GetPressure(index));
                }

                private void SendMove(MotionEvent e, int index)
                {
                    _host.TouchMoved(e.GetPointerId(index), e.GetX(index), e.GetY(index), e.GetPressure(index));
                }

                private void SendHistory(MotionEvent e, int pointerIndex)
                {
                    int historySize = e.HistorySize;
                    for (int historyIndex = 0; historyIndex < historySize; historyIndex++)
                    {
                        _host.TouchHistory(
                            e.GetPointerId(pointerIndex),
                            e.GetHistoricalX(pointerIndex, historyIndex),
                            e.GetHistoricalY(pointerIndex, historyIndex),
                            e.GetHistoricalPressure(pointerIndex, historyIndex),
                            e.GetHistoricalEventTime(historyIndex));
                    }
                }

                private void SendEnd(MotionEvent e, int index, bool canceled)
                {
                    _host.TouchEnded(e.GetPointerId(index), e.GetX(index), e.GetY(index), e.GetPressure(index), canceled);
                }

                private sealed class XEngineInputConnection : BaseInputConnection
                {
                    private readonly AndroidPlatformHost _host;

                    internal XEngineInputConnection(View targetView, AndroidPlatformHost host)
                        : base(targetView, true)
                    {
                        _host = host;
                    }

                    public override bool CommitText(global::Java.Lang.ICharSequence? text, int newCursorPosition)
                    {
                        _host.InputHandler.CommitText(text?.ToString() ?? string.Empty);
                        return true;
                    }

                    public override bool SetComposingText(global::Java.Lang.ICharSequence? text, int newCursorPosition)
                    {
                        _host.InputHandler.SetComposingText(text?.ToString() ?? string.Empty, newCursorPosition);
                        return true;
                    }

                    public override bool DeleteSurroundingText(int beforeLength, int afterLength)
                    {
                        _host.InputHandler.DeleteSurroundingText(beforeLength, afterLength);
                        return true;
                    }

                    public override bool SetSelection(int start, int end)
                    {
                        _host.InputHandler.SetSelection(start, end - start);
                        return true;
                    }
                }
            }

            internal static class AndroidNativeWindow
            {
                [DllImport("libandroid.so", EntryPoint = "ANativeWindow_fromSurface")]
                private static extern nint FromSurface(nint environment, nint surface);

                [DllImport("libandroid.so", EntryPoint = "ANativeWindow_release")]
                private static extern void ReleaseNative(nint window);

                internal static nint Acquire(Surface surface)
                {
                    nint window = FromSurface(JNIEnv.Handle, surface.Handle);
                    if (window == nint.Zero)
                        throw new InvalidOperationException("ANativeWindow_fromSurface returned null.");
                    return window;
                }

                internal static void Release(nint window)
                {
                    if (window != nint.Zero)
                        ReleaseNative(window);
                }
            }
            """;
    }

    private static string GetScreenOrientation(AndroidScreenOrientation orientation) => orientation switch
    {
        // Fully qualified: the generated source also imports XEngine.Runtime, whose
        // ScreenOrientation enum would otherwise make the simple name ambiguous.
        AndroidScreenOrientation.Unspecified => "global::Android.Content.PM.ScreenOrientation.Unspecified",
        AndroidScreenOrientation.Portrait => "global::Android.Content.PM.ScreenOrientation.Portrait",
        AndroidScreenOrientation.Landscape => "global::Android.Content.PM.ScreenOrientation.Landscape",
        AndroidScreenOrientation.Sensor => "global::Android.Content.PM.ScreenOrientation.Sensor",
        AndroidScreenOrientation.SensorPortrait => "global::Android.Content.PM.ScreenOrientation.SensorPortrait",
        AndroidScreenOrientation.SensorLandscape => "global::Android.Content.PM.ScreenOrientation.SensorLandscape",
        AndroidScreenOrientation.FullSensor => "global::Android.Content.PM.ScreenOrientation.FullSensor",
        _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Unsupported Android screen orientation."),
    };

    private static string CreateGlobalJson() =>
        """
        {
          "sdk": {
            "version": "10.0.300",
            "rollForward": "disable",
            "allowPrerelease": false
          }
        }
        """;

    /// <summary>The export must own its intermediate output. A parent repo may redirect android
    /// builds to a shared cache (e.g. XEngine's Directory.Build.props moves obj under
    /// Build/AndroidIntermediate), which silently detaches the intermediates the package builder
    /// cleans between the unstripped and stripped passes and inspects for .dbg.so debug
    /// companions. A project-local Directory.Build.props shadows that redirect and keeps the
    /// export self-contained wherever it is generated.</summary>
    private static string CreateDirectoryBuildProps() =>
        """
        <Project>
          <PropertyGroup>
            <BaseIntermediateOutputPath>obj\</BaseIntermediateOutputPath>
            <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
          </PropertyGroup>
        </Project>
        """;

    private static string CreateR8Rules() =>
        """
        # Generated by XEngine. This file is consumed only by Release R8 builds.
        # Keep the Java-visible .NET Android wrappers and callback targets that are reached from
        # Android framework/JNI rather than Java bytecode. The workload contributes its own
        # generated rules; these rules cover the generated XEngine Activity and native callbacks.
        -keep class mono.android.** { *; }
        -keep class crc**.** { *; }
        -keepclasseswithmembers class * {
            native <methods>;
        }
        -keepclassmembers class * extends android.app.Activity {
            public <init>(...);
        }
        -keepclassmembers class * extends android.view.View {
            public <init>(android.content.Context);
            public <init>(android.content.Context, android.util.AttributeSet);
        }
        """;

    private static string CreateReadme(AndroidPlayerProjectOptions options, string assemblyName)
    {
        string extension = options.OutputMode == AndroidOutputMode.Aab ? "aab" : "apk";
        return $"""
            # XEngine Android Export

            This is a generated .NET Android project. It is the supported G0 export contract; it is
            not advertised as a standalone Gradle application.

            Build with the .NET SDK and Android workload versions recorded in
            `toolchain/android-toolchain-lock.json`:

            ```powershell
            dotnet build {assemblyName}.csproj -c Release
            ```

            The configured package format is `{extension}`. Generated-file ownership and hashes are
            recorded in `xengine-android-project.json`.
            """;
    }

    private static string CreateAssetHostSource(AndroidPlayerProjectOptions options)
    {
        string mode = options.AssetPackagingMode.ToString();
        string expectedSceneGuid = options.DefaultSceneGuid == Guid.Empty
            ? "Guid.Empty"
            : $"new Guid(\"{options.DefaultSceneGuid:D}\")";
        string bundleRootDeclaration = mode == "AssetBundle"
            ? "var bundlePackageRoots = new List<string>();"
            : string.Empty;
        string bundleRootCollection = mode == "AssetBundle"
            ? """
                            if (descriptorPath.StartsWith("Content/", StringComparison.Ordinal) &&
                                descriptorPath.EndsWith(".version", StringComparison.Ordinal))
                            {
                                int separator = descriptorPath.LastIndexOf('/');
                                if (separator > "Content/".Length)
                                    bundlePackageRoots.Add(descriptorPath[..separator]);
                            }
              """
            : string.Empty;
        string bundleRootSort = mode == "AssetBundle"
            ? "bundlePackageRoots.Sort(StringComparer.Ordinal);"
            : string.Empty;
        string backendInitialization = mode switch
        {
            "LooseFiles" or "XEnginePak" =>
                "_assetBackend = new PlayerAssetBackend(XEngine.Runtime.AssetPackagingMode." + mode +
                ", _assetProvider, \"Content\");",
            "AssetBundle" =>
                "_assetBackend = BundleAssetBackend.CreateFromProvider(_assetProvider, bundlePackageRoots, " +
                expectedSceneGuid + ");",
            _ => string.Empty,
        };
        return $$"""
                private void ConfigureAssetHost()
                {
                    AssetManager assetManager = Assets!;
                    string filesPath = FilesDir?.AbsolutePath ?? throw new InvalidOperationException("Android files directory is unavailable.");
                    string cachePath = CacheDir?.AbsolutePath ?? throw new InvalidOperationException("Android cache directory is unavailable.");
                    string noBackupPath = NoBackupFilesDir?.AbsolutePath ?? throw new InvalidOperationException("Android no-backup directory is unavailable.");
                    XEngine.Runtime.Application.ConfigurePlatformPaths(
                        filesPath,
                        "assets/xengine",
                        filesPath,
                        cachePath,
                        noBackupPath);

                    var source = new CallbackAssetStreamProvider(
                        "Android APK assets/xengine",
                        path =>
                        {
                            string packagePath = path.Length == 0 ? "xengine" : "xengine/" + path;
                            try
                            {
                                return assetManager.Open(packagePath, Access.Streaming);
                            }
                            catch (Exception)
                            {
                                return null;
                            }
                        },
                        (directory, extension) =>
                        {
                            string packageDirectory = directory.Length == 0 ? "xengine" : "xengine/" + directory;
                            string[] names = assetManager.List(packageDirectory) ?? Array.Empty<string>();
                            var files = new List<string>(names.Length);
                            for (int i = 0; i < names.Length; i++)
                            {
                                if (names[i].EndsWith(extension, StringComparison.Ordinal))
                                    files.Add(directory.Length == 0 ? names[i] : directory + "/" + names[i]);
                            }
                            return files;
                        });

                    var descriptors = new List<PackagedAssetDescriptor>();
                    {{bundleRootDeclaration}}
                    using (global::System.IO.Stream descriptorStream = source.OpenRead("android-assets.json") ??
                        throw new InvalidDataException("Android asset descriptor is missing."))
                    using (JsonDocument descriptorDocument = JsonDocument.Parse(descriptorStream))
                    {
                        JsonElement entries = descriptorDocument.RootElement.GetProperty("assets");
                        foreach (JsonElement entry in entries.EnumerateArray())
                        {
                            string descriptorPath = entry.GetProperty("path").GetString()!;
                            descriptors.Add(new PackagedAssetDescriptor(
                                descriptorPath,
                                entry.GetProperty("sha256").GetString()!,
                                entry.GetProperty("bytes").GetInt64(),
                                entry.GetProperty("requiresSeek").GetBoolean()));
                            {{bundleRootCollection}}
                        }
                    }
                    {{bundleRootSort}}

                    VerifyTextureVariantContract(source, {{(mode == "AssetBundle" ? "bundlePackageRoots" : "Array.Empty<string>()")}});

                    _assetProvider = new MaterializingAssetStreamProvider(
                        source,
                        global::System.IO.Path.Combine(cachePath, "xengine-assets"),
                        descriptors,
                        memoryThresholdBytes: 1024 * 1024,
                        ownsSource: true);
                    _assetVfs = new AssetVirtualFileSystem();
                    _assetVfs.MountProvider("Assets", _assetProvider, ownsProvider: true);
                    _assetVfs.MountProvider("Files", new DirectoryAssetStreamProvider(filesPath), ownsProvider: true);
                    _assetVfs.MountProvider("Cache", new DirectoryAssetStreamProvider(cachePath), ownsProvider: true);
                    _assetVfs.MountProvider("NoBackup", new DirectoryAssetStreamProvider(noBackupPath), ownsProvider: true);

                    {{backendInitialization}}
                    global::Android.Util.Log.Info(
                        "XEngine",
                        "asset mounts Assets=" + (!_assetVfs.IsWritableMount("Assets")) +
                        " Files=" + _assetVfs.IsWritableMount("Files") +
                        " Cache=" + _assetVfs.IsWritableMount("Cache") +
                        " NoBackup=" + _assetVfs.IsWritableMount("NoBackup") +
                        " packaging={{mode}}");
                }

                private static void VerifyTextureVariantContract(
                    IAssetStreamProvider source,
                    IReadOnlyList<string> bundlePackageRoots)
                {
                    using global::System.IO.Stream? manifest = source.OpenRead("Settings/android-texture-variants.json");
                    if (manifest == null)
                    {
                        global::Android.Util.Log.Warn("XEngine", "texture variants contract missing");
                        return;
                    }

                    using JsonDocument document = JsonDocument.Parse(manifest);
                    if (!document.RootElement.TryGetProperty("textures", out JsonElement textures) ||
                        textures.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException("Android texture variants contract has no textures array.");
                    }

                    int verified = 0;
                    int skipped = 0;
                    foreach (JsonElement texture in textures.EnumerateArray())
                    {
                        string payloadPath = texture.GetProperty("payloadPath").GetString() ?? string.Empty;
                        string expectedHash = texture.TryGetProperty("packagedPayloadSha256", out JsonElement packagedHash)
                            ? packagedHash.GetString() ?? string.Empty
                            : texture.GetProperty("sourcePayloadSha256").GetString() ?? string.Empty;
                        if (payloadPath.Length == 0 || expectedHash.Length == 0)
                        {
                            skipped++;
                            continue;
                        }

                        global::System.IO.Stream? payload = source.OpenRead("Content/" + payloadPath);
                        if (payload == null && bundlePackageRoots.Count > 0 &&
                            Guid.TryParse(texture.GetProperty("guid").GetString(), out Guid textureGuid))
                        {
                            payload = TryReadBundlePayload(source, bundlePackageRoots, textureGuid);
                        }
                        if (payload == null)
                        {
                            // AssetBundle payloads are addressed through their package manifest rather
                            // than a top-level {guid}.asset file. Bundle-level verification remains in
                            // the package/hash gate; this host check only covers directly readable files.
                            skipped++;
                            continue;
                        }

                        using (payload)
                        {
                            using SHA256 sha = SHA256.Create();
                            string actualHash = Convert.ToHexString(sha.ComputeHash(payload));
                            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException(
                                    "Android texture payload hash mismatch for " + payloadPath +
                                    ": expected " + expectedHash + ", got " + actualHash + ".");
                            }
                        }
                        verified++;
                    }

                    global::Android.Util.Log.Info(
                        "XEngine",
                        "texture variants contract verified=" + verified + " skipped=" + skipped);
                }

                private static global::System.IO.Stream? TryReadBundlePayload(
                    IAssetStreamProvider source,
                    IReadOnlyList<string> bundlePackageRoots,
                    Guid textureGuid)
                {
                    for (int rootIndex = 0; rootIndex < bundlePackageRoots.Count; rootIndex++)
                    {
                        IReadOnlyList<string> files = source.GetFiles(bundlePackageRoots[rootIndex], ".bundle");
                        for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
                        {
                            using global::System.IO.Stream? bundleStream = source.OpenRead(files[fileIndex]);
                            if (bundleStream == null)
                                continue;
                            using var buffer = new global::System.IO.MemoryStream();
                            bundleStream.CopyTo(buffer);
                            using var bundle = global::XEngine.Runtime.AssetBundles.AssetBundle.LoadFromMemory(buffer.ToArray());
                            if (!bundle.TryReadAssetPayload(textureGuid, out byte[] payload))
                                continue;
                            return new global::System.IO.MemoryStream(payload, writable: false);
                        }
                    }
                    return null;
                }

                private void DisposeAssetHost()
                {
                    _assetBackend?.Dispose();
                    _assetBackend = null;
                    _assetVfs?.Dispose();
                    _assetVfs = null;
                    _assetProvider = null;
                }
""";
    }

    private static void CopyAssetPayload(string sourceDirectory, string projectRoot)
    {
        string source = Path.GetFullPath(sourceDirectory);
        string manifest = Path.Combine(source, "android-assets.json");
        if (!File.Exists(manifest))
            throw new InvalidDataException("Android asset payload is missing android-assets.json.");

        string destinationRoot = Path.Combine(projectRoot, "Assets", "xengine");
        Directory.CreateDirectory(destinationRoot);
        string destinationPrefix = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        for (int i = 0; i < files.Length; i++)
        {
            string relative = Path.GetRelativePath(source, files[i]).Replace('\\', '/');
            if (relative.Length == 0 || relative.StartsWith("../", StringComparison.Ordinal) ||
                relative.Contains("/../", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Android asset payload contains an escaping path: {relative}");
            }
            string destination = Path.GetFullPath(Path.Combine(
                destinationRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(destinationPrefix, PathComparison))
                throw new InvalidDataException($"Android asset payload escapes destination: {relative}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(files[i], destination, overwrite: false);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
        JsonElement entries = document.RootElement.GetProperty("assets");
        string strippingReportPath = document.RootElement.GetProperty("assetStrippingReport").GetString() ?? string.Empty;
        if (!IsSafeAssetPath(strippingReportPath))
            throw new InvalidDataException("Android asset descriptor has an invalid stripping report path.");
        string strippingReport = Path.Combine(destinationRoot, strippingReportPath.Replace('/', Path.DirectorySeparatorChar));
        if (!strippingReport.StartsWith(destinationPrefix, PathComparison) || !File.Exists(strippingReport))
            throw new InvalidDataException("Android asset descriptor references a missing stripping report.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string path = entry.GetProperty("path").GetString() ?? string.Empty;
            if (!seen.Add(path) || !IsSafeAssetPath(path))
            {
                throw new InvalidDataException($"Android asset descriptor contains an invalid path: {path}");
            }
            string physical = Path.Combine(destinationRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!physical.StartsWith(destinationPrefix, PathComparison))
                throw new InvalidDataException($"Android asset descriptor escapes destination: {path}");
            if (!File.Exists(physical))
                throw new InvalidDataException($"Android asset descriptor references missing payload: {path}");
        }
    }

    private static bool IsSafeAssetPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path[0] == '/')
            return false;
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] == "." || segments[i] == "..")
                return false;
        }
        return true;
    }

    private static void CopyShaderPackage(string sourceDirectory, string projectRoot)
    {
        string source = Path.GetFullPath(sourceDirectory);
        string destination = Path.Combine(projectRoot, "Resources", "raw");
        Directory.CreateDirectory(destination);

        string[] files = Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
            throw new InvalidDataException($"Android shader package is empty: {source}");

        bool hasCatalog = false;
        bool hasArtifact = false;
        for (int i = 0; i < files.Length; i++)
        {
            string fileName = Path.GetFileName(files[i]);
            if (string.Equals(fileName, "catalog.xshadercatalog", StringComparison.Ordinal))
                hasCatalog = true;
            else if (fileName.EndsWith(".xshader", StringComparison.Ordinal))
                hasArtifact = true;
            else
            {
                throw new InvalidDataException(
                    $"Android shader package contains unsupported file '{fileName}'.");
            }
        }

        if (!hasCatalog)
            throw new InvalidDataException("Android shader package is missing catalog.xshadercatalog.");
        if (!hasArtifact)
            throw new InvalidDataException("Android shader package contains no artifact payload.");

        string packagePath = Path.Combine(destination, "android_vulkan.xshaderpack");
        using FileStream packageStream = new(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var package = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: false);
        for (int i = 0; i < files.Length; i++)
        {
            string fileName = Path.GetFileName(files[i]);
            ZipArchiveEntry entry = package.CreateEntry(fileName, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using Stream input = File.OpenRead(files[i]);
            using Stream output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static async Task WriteLauncherIconAsync(string projectRoot, string? applicationIconPath, CancellationToken cancellationToken)
    {
        string destination = Path.Combine(projectRoot, "Resources", "mipmap", "ic_launcher.png");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using Stream? source = string.IsNullOrWhiteSpace(applicationIconPath)
            ? typeof(AndroidPlayerProjectGenerator).Assembly.GetManifestResourceStream("XEngine.Editor.Resources.xengine.png")
            : File.OpenRead(applicationIconPath);
        if (source == null)
            throw new InvalidOperationException("Missing embedded XEngine launcher icon.");

        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDefaultThemesAsync(string projectRoot, CancellationToken cancellationToken)
    {
        // Single base theme pair. Do NOT add a values-v31 override on the framework
        // Theme.SplashScreen/postSplashScreenTheme here: the locked Unity-derived Android SDK
        // ships platform jars with a trimmed resources.arsc that lacks those symbols, and aapt2
        // link fails with APT2260 ("resource android:style/Theme.SplashScreen not found").
        const string baseStyles = """
            <?xml version="1.0" encoding="utf-8"?>
            <resources>
              <style name="Theme.XEngine.App" parent="@android:style/Theme.Material.NoActionBar.Fullscreen" />
              <style name="Theme.XEngine.Splash" parent="@style/Theme.XEngine.App" />
            </resources>
            """;

        await WriteTextAsync(
                Path.Combine(projectRoot, "Resources", "values", "styles.xml"),
                baseStyles,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<string> GetNativeAudioAbis(
        string? nativeLibraryRoot,
        IReadOnlyList<string> targetAbis)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(nativeLibraryRoot))
            return result;
        string root = Path.GetFullPath(nativeLibraryRoot);
        for (int i = 0; i < targetAbis.Count; i++)
        {
            string rid = AndroidAbiMapper.ToRuntimeIdentifier(targetAbis[i]);
            if (File.Exists(Path.Combine(root, rid, "native", "libminiaudioex.so")))
                result.Add(targetAbis[i]);
        }
        return result;
    }

    private static string CreateNativeAudioItems(IReadOnlyList<string> nativeAudioAbis)
    {
        if (nativeAudioAbis.Count == 0)
            return string.Empty;
        var builder = new StringBuilder();
        for (int i = 0; i < nativeAudioAbis.Count; i++)
        {
            string path = $"NativeLibraries/{nativeAudioAbis[i]}/libminiaudioex.so";
            builder.Append("<AndroidNativeLibrary Include=\"").Append(path).AppendLine("\" />");
        }
        return builder.ToString().TrimEnd();
    }

    private static void CopyNativeAudioLibraries(
        string? nativeLibraryRoot,
        string projectRoot,
        IReadOnlyList<string> nativeAudioAbis)
    {
        if (string.IsNullOrWhiteSpace(nativeLibraryRoot))
            return;
        string root = Path.GetFullPath(nativeLibraryRoot);
        for (int i = 0; i < nativeAudioAbis.Count; i++)
        {
            string rid = AndroidAbiMapper.ToRuntimeIdentifier(nativeAudioAbis[i]);
            string source = Path.Combine(root, rid, "native", "libminiaudioex.so");
            string destination = Path.Combine(
                projectRoot,
                "NativeLibraries",
                nativeAudioAbis[i],
                "libminiaudioex.so");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static async Task CopyManagedDependenciesAsync(
        AndroidManagedDependencyCollection dependencies,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        string managedDirectory = Path.Combine(projectRoot, "Managed");
        Directory.CreateDirectory(managedDirectory);
        var reportEntries = new List<object>();
        for (int i = 0; i < dependencies.Assemblies.Count; i++)
        {
            AndroidManagedAssemblyInput assembly = dependencies.Assemblies[i];
            string destination = Path.Combine(managedDirectory, assembly.Name + ".dll");
            await using (FileStream source = new(
                assembly.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream output = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            reportEntries.Add(new
            {
                name = assembly.Name,
                file = "Managed/" + assembly.Name + ".dll",
                role = assembly.Role,
                status = "kept",
                reason = assembly.Reason,
                bytes = new FileInfo(destination).Length,
                sha256 = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false),
            });
        }
        for (int i = 0; i < dependencies.Exclusions.Count; i++)
        {
            AndroidManagedAssemblyExclusion exclusion = dependencies.Exclusions[i];
            reportEntries.Add(new
            {
                name = exclusion.Name,
                file = (string?)null,
                role = exclusion.Role,
                status = "excluded",
                reason = exclusion.Reason,
                bytes = 0L,
                sha256 = (string?)null,
            });
        }
        string reportPath = Path.Combine(projectRoot, "dependencies", "android-managed-dependencies.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await WriteTextAsync(
            reportPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                target = "Android",
                assemblies = reportEntries,
            }, s_jsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreateManagedReferences(AndroidManagedDependencyCollection? dependencies)
    {
        if (dependencies == null || dependencies.Assemblies.Count == 0)
            return string.Empty;
        var builder = new StringBuilder();
        builder.AppendLine("<ItemGroup>");
        for (int i = 0; i < dependencies.Assemblies.Count; i++)
        {
            string name = dependencies.Assemblies[i].Name;
            builder.Append("  <Reference Include=\"").Append(Xml(name)).AppendLine("\">");
            builder.Append("    <HintPath>Managed/").Append(Xml(name)).AppendLine(".dll</HintPath>");
            builder.AppendLine("    <Private>true</Private>");
            builder.AppendLine("    <SpecificVersion>false</SpecificVersion>");
            builder.AppendLine("  </Reference>");
        }
        builder.Append("</ItemGroup>");
        return builder.ToString();
    }

    private static IReadOnlyList<string> GetManagedAssemblyNames(AndroidManagedDependencyCollection? dependencies)
    {
        if (dependencies == null)
            return [];
        var names = new string[dependencies.Assemblies.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = dependencies.Assemblies[i].Name;
        return names;
    }

    private static bool ContainsAssembly(
        IReadOnlyList<AndroidManagedAssemblyInput> assemblies,
        string name)
    {
        for (int i = 0; i < assemblies.Count; i++)
        {
            if (string.Equals(assemblies[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task WriteTextAsync(string path, string value, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
        await File.WriteAllTextAsync(path, normalized, s_utf8NoBom, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string SanitizeIdentifier(string value)
    {
        var result = new StringBuilder(value.Length + 1);
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
            result.Append('_');
        for (int i = 0; i < value.Length; i++)
            result.Append(IsIdentifierPart(value[i]) ? value[i] : '_');
        return result.ToString();
    }

    internal static string GetAssemblyName(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        return SanitizeIdentifier(projectName) + ".Android";
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
