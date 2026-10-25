// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Runtime;

namespace XEngine.Editor.Build.Ios;

internal sealed record IosManagedAssemblyInput(
    string Name,
    string SourcePath,
    string Role,
    string Reason);

/// <summary>Inputs to a relocatable net10.0-ios Mono Full AOT player export.</summary>
internal sealed class IosPlayerProjectOptions
{
    public string ProjectName { get; init; } = "XEngineGame";
    public string ApplicationId { get; init; } = "com.xengine.game";
    public string ApplicationLabel { get; init; } = "XEngine";
    public string VersionName { get; init; } = "1.0";
    public int VersionCode { get; init; } = 1;
    public string RuntimeIdentifier { get; init; } = "ios-arm64";
    public Guid DefaultSceneGuid { get; init; }
    public string SupportedOSPlatformVersion { get; init; } = "16.0";
    public int PreferredFramesPerSecond { get; init; } = 60;
    public AssetPackagingMode PackagingMode { get; init; } = AssetPackagingMode.LooseFiles;
    public string? NativeLibraryRoot { get; init; }
    public string TeamId { get; init; } = string.Empty;
    public string CodesignIdentity { get; init; } = "Apple Development";
    public bool AutomaticSigning { get; init; } = true;
    public string CodesignProvision { get; init; } = string.Empty;
    public IosManagedStrippingMode ManagedStripping { get; init; } = IosManagedStrippingMode.Disabled;
    public IosManagedTypeRootCollection ManagedTypeRoots { get; init; } = IosManagedTypeRootCollection.Empty;
    public IReadOnlyList<IosManagedAssemblyInput> ManagedAssemblies { get; init; } = [];
}

/// <summary>Paths emitted by one deterministic iOS player export.</summary>
internal sealed record IosPlayerProjectLayout(
    string RootDirectory,
    string ProjectPath,
    string InfoPlistPath,
    string OwnershipManifestPath,
    string AssemblyName,
    string RuntimeIdentifier,
    string ContentDirectory,
    bool NativeAudioPackaged);

/// <summary>
/// Creates a relocatable <c>net10.0-ios</c> player project, including codesign MSBuild
/// properties and <c>PrivacyInfo.xcprivacy</c>. Device builds use Mono AOT; ARM64 Simulator
/// builds enable the Mono interpreter for development. Apple tooling still runs only on macOS + Xcode.
/// </summary>
internal static class IosPlayerProjectGenerator
{
    private const int OwnershipSchemaVersion = 1;
    internal const string PlayerRootsSourceFileName = "XEngine.iOS.PlayerRoots.g.cs";
    private static readonly UTF8Encoding s_utf8NoBom = new(false);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static async Task<IosPlayerProjectLayout> GenerateAsync(
        string outputDirectory,
        IosPlayerProjectOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);

        string root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && HasEntries(root))
            throw new IOException($"iOS export directory must be empty: {root}");

        ValidateOptions(options);
        IosManagedTrimRoots.ValidateRequestedMode(options.ManagedStripping);
        IReadOnlyList<IosManagedAssemblyInput> assemblies = ValidateAssemblies(options.ManagedAssemblies);
        IosManagedTypeRootCollection typeRoots = options.ManagedTypeRoots ?? IosManagedTypeRootCollection.Empty;
        string assemblyName = GetAssemblyName(options.ProjectName);
        string playerDirectory = Path.Combine(root, "Player");
        string projectPath = Path.Combine(playerDirectory, assemblyName + ".csproj");
        string infoPlistPath = Path.Combine(playerDirectory, "Info.plist");
        string ownershipManifestPath = Path.Combine(root, "xengine-ios-project.json");

        Directory.CreateDirectory(playerDirectory);
        IosAssetPackager.WriteContentLayout(playerDirectory);
        bool nativeAudioPackaged = IosNativeAudioContract.TryCopy(
            options.NativeLibraryRoot,
            playerDirectory,
            options.RuntimeIdentifier);
        IosNativeAudioContract.WriteVerification(root, nativeAudioPackaged, options.RuntimeIdentifier);
        await WriteTextAsync(
                projectPath,
                CreateProject(options, assemblyName, assemblies, nativeAudioPackaged),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(infoPlistPath, CreateInfoPlist(options, assemblyName), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(playerDirectory, "PrivacyInfo.xcprivacy"),
                CreatePrivacyManifest(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(playerDirectory, "IosPlayer.cs"),
                IosManagedPlayerSource.CreatePlayer(options),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(playerDirectory, "IosMetalView.cs"),
                IosManagedPlayerSource.CreateMetalView(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(playerDirectory, PlayerRootsSourceFileName),
                IosManagedTrimRoots.CreateSource(typeRoots),
                cancellationToken)
            .ConfigureAwait(false);
        IosManagedTypeRootReport.Write(
            Path.Combine(root, "trimming", IosManagedTypeRootReport.FileName),
            typeRoots);
        if (options.ManagedStripping == IosManagedStrippingMode.Safe)
        {
            IosManagedTrimRoots.WriteDescriptor(
                Path.Combine(playerDirectory, "trimming", IosManagedTrimRoots.DescriptorFileName),
                assemblyName,
                assemblies);
        }
        await CopyAssembliesAsync(assemblies, Path.Combine(playerDirectory, "Managed"), cancellationToken)
            .ConfigureAwait(false);
        await WriteOwnershipManifestAsync(
                root,
                ownershipManifestPath,
                nativeAudioPackaged,
                options.RuntimeIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        return new IosPlayerProjectLayout(
            root,
            projectPath,
            infoPlistPath,
            ownershipManifestPath,
            assemblyName,
            options.RuntimeIdentifier,
            Path.Combine(playerDirectory, "Content"),
            nativeAudioPackaged);
    }

    internal static string GetAssemblyName(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        return SanitizeIdentifier(projectName) + ".iOS";
    }

    private static void ValidateOptions(IosPlayerProjectOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectName))
            throw new ArgumentException("iOS project name cannot be empty.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApplicationId) || !IsValidApplicationId(options.ApplicationId))
        {
            throw new ArgumentException(
                "iOS application id must be a reverse-DNS identifier such as com.xengine.game.",
                nameof(options));
        }
        if (options.RuntimeIdentifier is not ("ios-arm64" or "iossimulator-arm64"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RuntimeIdentifier,
                "iOS export supports ios-arm64 (device) and iossimulator-arm64 (simulator).");
        }
        if (string.IsNullOrWhiteSpace(options.SupportedOSPlatformVersion))
            throw new ArgumentException("SupportedOSPlatformVersion cannot be empty.", nameof(options));
        if (options.PreferredFramesPerSecond is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(options), "PreferredFramesPerSecond must be 1-120.");
        if (options.VersionCode < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "VersionCode must be at least 1.");
    }

    private static IReadOnlyList<IosManagedAssemblyInput> ValidateAssemblies(
        IReadOnlyList<IosManagedAssemblyInput> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        if (assemblies.Count == 0)
            throw new ArgumentException("iOS export requires at least the XEngine.Runtime assembly.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<IosManagedAssemblyInput>(assemblies.Count);
        for (int i = 0; i < assemblies.Count; i++)
        {
            IosManagedAssemblyInput assembly = assemblies[i];
            if (string.IsNullOrWhiteSpace(assembly.Name))
                throw new ArgumentException("Managed assembly name cannot be empty.");
            if (string.IsNullOrWhiteSpace(assembly.SourcePath) || !File.Exists(assembly.SourcePath))
                throw new FileNotFoundException($"Managed assembly is missing: {assembly.SourcePath}");
            if (!names.Add(assembly.Name))
                throw new InvalidDataException($"Duplicate managed assembly '{assembly.Name}'.");
            result.Add(assembly);
        }

        return result;
    }

    private static string CreateProject(
        IosPlayerProjectOptions options,
        string assemblyName,
        IReadOnlyList<IosManagedAssemblyInput> assemblies,
        bool nativeAudioPackaged)
    {
        var references = new StringBuilder();
        var trimmerRoots = new StringBuilder();
        for (int i = 0; i < assemblies.Count; i++)
        {
            string fileName = Path.GetFileName(assemblies[i].SourcePath);
            references.AppendLine($"""
                    <Reference Include="{Xml(assemblies[i].Name)}">
                      <HintPath>Managed/{Xml(fileName)}</HintPath>
                      <Private>true</Private>
                    </Reference>
                """);
            trimmerRoots.AppendLine($"    <TrimmerRootAssembly Include=\"{Xml(assemblies[i].Name)}\" />");
        }
        if (options.ManagedStripping == IosManagedStrippingMode.Safe)
        {
            trimmerRoots.AppendLine(
                $"    <TrimmerRootDescriptor Include=\"trimming/{IosManagedTrimRoots.DescriptorFileName}\" />");
        }

        string identity = string.IsNullOrWhiteSpace(options.CodesignIdentity)
            ? "Apple Development"
            : options.CodesignIdentity;
        string automatic = options.AutomaticSigning ? "true" : "false";
        bool simulator = options.RuntimeIdentifier.StartsWith("iossimulator", StringComparison.Ordinal);
        string interpreter = simulator ? "true" : "false";
        string signingProps = simulator
            ? // Simulator apps run unsigned — no identity, team, or provisioning.
              """
                    <EnableCodeSigning>false</EnableCodeSigning>
              """
            : $"""
                    <EnableCodeSigning>true</EnableCodeSigning>
                    <CodesignKey>{Xml(identity)}</CodesignKey>
                    <DevelopmentTeam>{Xml(options.TeamId)}</DevelopmentTeam>
                    <EnableAutomaticProvisioning>{automatic}</EnableAutomaticProvisioning>
                    <CodesignProvision>{Xml(options.CodesignProvision)}</CodesignProvision>
              """;

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-ios</TargetFramework>
                <RuntimeIdentifier>{Xml(options.RuntimeIdentifier)}</RuntimeIdentifier>
                <OutputType>Exe</OutputType>
                <AssemblyName>{Xml(assemblyName)}</AssemblyName>
                <RootNamespace>XEngine.Generated.iOS</RootNamespace>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <SupportedOSPlatformVersion>{Xml(options.SupportedOSPlatformVersion)}</SupportedOSPlatformVersion>
                <ApplicationId>{Xml(options.ApplicationId)}</ApplicationId>
                <ApplicationTitle>{Xml(options.ApplicationLabel)}</ApplicationTitle>
                <ApplicationDisplayVersion>{Xml(options.VersionName)}</ApplicationDisplayVersion>
                <ApplicationVersion>{options.VersionCode}</ApplicationVersion>
                <PublishAot>false</PublishAot>
                <UseInterpreter>{interpreter}</UseInterpreter>
                <MtouchLink>SdkOnly</MtouchLink>
                <PublishTrimmed>true</PublishTrimmed>
                <TrimMode>partial</TrimMode>
                <DefineConstants>$(DefineConstants);XENGINE_IOS;XENGINE_MOBILE</DefineConstants>
                <XEngineEnableAnalyzers>false</XEngineEnableAnalyzers>
                <ApplicationManifest>Info.plist</ApplicationManifest>
            {signingProps}
                <XEngineManagedStrippingMode>{options.ManagedStripping}</XEngineManagedStrippingMode>
                <ILLinkTreatWarningsAsErrors>{(options.ManagedStripping == IosManagedStrippingMode.Safe ? "true" : "false")}</ILLinkTreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
            {references}  </ItemGroup>
              <ItemGroup>
                <BundleResource Include="Content\**\*" />
                <BundleResource Include="PrivacyInfo.xcprivacy" />
              </ItemGroup>
            {CreateNativeAudioItems(nativeAudioPackaged)}  <ItemGroup>
            {trimmerRoots}  </ItemGroup>
            </Project>
            """;
    }

    private static string CreatePrivacyManifest() =>
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>NSPrivacyTracking</key>
          <false/>
          <key>NSPrivacyTrackingDomains</key>
          <array/>
          <key>NSPrivacyCollectedDataTypes</key>
          <array/>
          <key>NSPrivacyAccessedAPITypes</key>
          <array/>
        </dict>
        </plist>
        """;

    private static string CreateNativeAudioItems(bool nativeAudioPackaged)
    {
        if (!nativeAudioPackaged)
            return string.Empty;
        return """
              <ItemGroup>
                <NativeReference Include="NativeLibraries\libminiaudioex.dylib">
                  <Kind>Dynamic</Kind>
                  <RelativePath>libminiaudioex.dylib</RelativePath>
                  <SmartLink>false</SmartLink>
                </NativeReference>
              </ItemGroup>

            """;
    }

    private static string CreateInfoPlist(IosPlayerProjectOptions options, string assemblyName) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>CFBundleDevelopmentRegion</key>
          <string>en</string>
          <key>CFBundleDisplayName</key>
          <string>{Xml(options.ApplicationLabel)}</string>
          <key>CFBundleExecutable</key>
          <string>{Xml(assemblyName)}</string>
          <key>CFBundleIdentifier</key>
          <string>{Xml(options.ApplicationId)}</string>
          <key>CFBundleInfoDictionaryVersion</key>
          <string>6.0</string>
          <key>CFBundleName</key>
          <string>{Xml(options.ApplicationLabel)}</string>
          <key>CFBundlePackageType</key>
          <string>APPL</string>
          <key>CFBundleShortVersionString</key>
          <string>{Xml(options.VersionName)}</string>
          <key>CFBundleVersion</key>
          <string>{options.VersionCode}</string>
          <key>LSRequiresIPhoneOS</key>
          <true/>
          <key>MinimumOSVersion</key>
          <string>{Xml(options.SupportedOSPlatformVersion)}</string>
          <key>UIApplicationSceneManifest</key>
          <dict>
            <key>UIApplicationSupportsMultipleScenes</key>
            <false/>
            <key>UISceneConfigurations</key>
            <dict>
              <key>UIWindowSceneSessionRoleApplication</key>
              <array>
                <dict>
                  <key>UISceneConfigurationName</key>
                  <string>Default Configuration</string>
                  <key>UISceneDelegateClassName</key>
                  <string>SceneDelegate</string>
                </dict>
              </array>
            </dict>
          </dict>
          <key>UILaunchScreen</key>
          <dict/>
          <key>UIRequiredDeviceCapabilities</key>
          <array>
            <string>arm64</string>
            <string>metal</string>
          </array>
          <key>UISupportedInterfaceOrientations</key>
          <array>
            <string>UIInterfaceOrientationPortrait</string>
            <string>UIInterfaceOrientationLandscapeLeft</string>
            <string>UIInterfaceOrientationLandscapeRight</string>
          </array>
        </dict>
        </plist>
        """;

    private static async Task CopyAssembliesAsync(
        IReadOnlyList<IosManagedAssemblyInput> assemblies,
        string managedDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(managedDirectory);
        for (int i = 0; i < assemblies.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(managedDirectory, Path.GetFileName(assemblies[i].SourcePath));
            await using FileStream source = File.OpenRead(assemblies[i].SourcePath);
            await using FileStream dest = File.Create(destination);
            await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteOwnershipManifestAsync(
        string root,
        string manifestPath,
        bool nativeAudioPackaged,
        string runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        string normalizedManifestPath = Path.GetFullPath(manifestPath);
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        var entries = new List<object>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            if (string.Equals(Path.GetFullPath(files[i]), normalizedManifestPath, StringComparison.OrdinalIgnoreCase))
                continue;
            string relativePath = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            entries.Add(new
            {
                path = relativePath,
                sha256 = await HashFileAsync(files[i], cancellationToken).ConfigureAwait(false),
            });
        }

        await WriteTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = OwnershipSchemaVersion,
                    platform = "ios",
                    runtimeIdentifier,
                    nativeAudioLibrary = IosNativeAudioContract.LibraryFileName,
                    nativeAudioPackaged,
                    files = entries,
                }, s_jsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, contents.Replace("\r\n", "\n"), s_utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string SanitizeIdentifier(string value)
    {
        var result = new StringBuilder(value.Length + 1);
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
            result.Append('_');
        for (int i = 0; i < value.Length; i++)
            result.Append(IsIdentifierPart(value[i]) ? value[i] : '_');
        return result.ToString();
    }

    internal static bool IsValidApplicationId(string applicationId)
    {
        if (applicationId.Length == 0 || applicationId.StartsWith('.') || applicationId.EndsWith('.'))
            return false;
        string[] parts = applicationId.Split('.');
        if (parts.Length < 2)
            return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                return false;
            for (int c = 0; c < parts[i].Length; c++)
            {
                char ch = parts[i][c];
                if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                    return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }
}
