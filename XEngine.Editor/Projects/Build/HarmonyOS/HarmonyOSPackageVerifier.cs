// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Projects.Build;

namespace XEngine.Editor.Build.HarmonyOS;

internal sealed class HarmonyOSPackageVerificationRequest
{
    public string ArtifactPath { get; init; } = string.Empty;
    public HarmonyOSOutputMode OutputMode { get; init; }
    public string ApplicationId { get; init; } = string.Empty;
    public int VersionCode { get; init; }
    public string Abi { get; init; } = string.Empty;
    public string BundleFingerprint { get; init; } = string.Empty;
    public string OfficialSdkHash { get; init; } = string.Empty;
    public string XEngineCommit { get; init; } = string.Empty;
    public string SigningReference { get; init; } = "None";
    public HarmonyOSBuildProvenanceInput? Provenance { get; init; }
    public IReadOnlyCollection<string> AllowedElfDependencies { get; init; } = [];
    public IReadOnlyCollection<string> ForbiddenPlaintext { get; init; } = [];
    /// <summary>
    /// Prebuilt native libraries (file name → SHA-256) the pipeline staged into
    /// <c>libs/&lt;abi&gt;/</c> (e.g. the FFmpeg shared libraries). Each must be present in the
    /// package with the exact expected bytes; an empty collection expects none.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExpectedNativePayloadFiles { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyCollection<string> RequiredNativeSymbols { get; init; } = HarmonyOSPackageVerifier.DefaultNativeSymbols;
    public HarmonyOSProcessCommand? ProviderVerifierCommand { get; init; }
}

internal sealed class HarmonyOSPackageVerification
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public string ReceiptJson { get; init; } = string.Empty;
    public HarmonyOSProcessResult? ProviderVerifierResult { get; init; }
}

/// <summary>Independent HAP/APP archive, ELF, dependency, and receipt verifier.</summary>
internal sealed class HarmonyOSPackageVerifier(IHarmonyOSProcessRunner? processRunner = null)
{
    /// <summary>
    /// .NET runtime native helpers + libc++_shared.so that ship beside libxengine_player.so/libxengine_host.so
    /// into libs/&lt;abi&gt;/ and are expected package contents. Mirrors the player payload inspector's set.
    /// </summary>
    private static readonly HashSet<string> s_allowedShippedRuntimeLibraries = new(StringComparer.Ordinal)
    {
        "libSystem.Globalization.Native.so",
        "libSystem.IO.Compression.Native.so",
        "libSystem.IO.Ports.Native.so",
        "libSystem.Native.so",
        "libSystem.Security.Cryptography.Native.OpenSsl.so",
        "libc++_shared.so",
        "libSystem.Net.Security.Native.so",
        "libcrypto.so.3",
        "libssl.so.3",
    };

    internal static readonly string[] DefaultNativeSymbols =
    [
        "xengine_player_abi_version",
        "xengine_player_create",
        "xengine_player_attach_surface",
        "xengine_player_resize_surface",
        "xengine_player_detach_surface",
        "xengine_player_set_resumed",
        "xengine_player_touch",
        "xengine_player_key",
        "xengine_player_mouse",
        "xengine_player_mouse_wheel",
        "xengine_player_focus",
        "xengine_player_tick",
        "xengine_player_destroy",
    ];

    private readonly IHarmonyOSProcessRunner? _processRunner = processRunner;

    internal async Task<HarmonyOSPackageVerification> VerifyAsync(
        HarmonyOSPackageVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<string>();
        var nativeBuildIds = new Dictionary<string, string>(StringComparer.Ordinal);
        HarmonyOSProcessResult? providerResult = null;
        if (request.ProviderVerifierCommand != null)
        {
            if (_processRunner == null)
                errors.Add("A provider verifier command was supplied without a process runner.");
            else
            {
                providerResult = await _processRunner.RunAsync(request.ProviderVerifierCommand, cancellationToken).ConfigureAwait(false);
                if (providerResult.TimedOut || providerResult.ExitCode != 0)
                    errors.Add($"Provider verifier failed with exit code {providerResult.ExitCode}.");
            }
        }

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(request.ArtifactPath);
            ValidateModuleIdentity(archive, request, errors);
            ValidatePayloadHashManifest(archive, errors);
            nativeBuildIds = ValidateNativePayload(archive, request, errors);
            ScanForbiddenPlaintext(archive, request.ForbiddenPlaintext, errors);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            errors.Add("HarmonyOS package archive is invalid: " + exception.Message);
        }

        string artifactHash = await ComputeSha256Async(request.ArtifactPath, cancellationToken).ConfigureAwait(false);
        var receipt = HarmonyOSBuildReceipt.Create(new HarmonyOSBuildReceiptInput
        {
            Action = request.OutputMode == HarmonyOSOutputMode.Hap ? "verify-hap" : "verify-app",
            BundleFingerprint = request.BundleFingerprint,
            OfficialSdkHash = request.OfficialSdkHash,
            XEngineCommit = request.XEngineCommit,
            SigningReference = request.SigningReference,
            ArtifactRelativePath = Path.GetFileName(request.ArtifactPath),
            ArtifactSha256 = artifactHash,
            ArtifactBytes = new FileInfo(request.ArtifactPath).Length,
            Provenance = request.Provenance,
            NativeBuildIds = nativeBuildIds,
            Findings = SanitizeFindings(errors, request.ForbiddenPlaintext),
        });
        return new HarmonyOSPackageVerification
        {
            Success = errors.Count == 0,
            Errors = errors.AsReadOnly(),
            ReceiptJson = receipt.ToJson(),
            ProviderVerifierResult = providerResult,
        };
    }

    private static void ValidateRequest(HarmonyOSPackageVerificationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApplicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Abi);
        if (!File.Exists(request.ArtifactPath))
            throw new FileNotFoundException("HarmonyOS package artifact is missing.", request.ArtifactPath);
        string expectedExtension = request.OutputMode == HarmonyOSOutputMode.Hap ? ".hap" : ".app";
        if (!string.Equals(Path.GetExtension(request.ArtifactPath), expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"HarmonyOS package artifact must use {expectedExtension}.", nameof(request.ArtifactPath));
        if (request.VersionCode < 1)
            throw new ArgumentOutOfRangeException(nameof(request.VersionCode));
        _ = HarmonyOSAbiMapper.ToRuntimeIdentifier(request.Abi);
        _ = HarmonyOSSigningReference.Validate(request.SigningReference);
        if (request.Provenance == null)
            throw new ArgumentException("HarmonyOS package verification requires structured receipt provenance.", nameof(request.Provenance));
    }

    private static void ValidateModuleIdentity(ZipArchive archive, HarmonyOSPackageVerificationRequest request, List<string> errors)
    {
        ZipArchiveEntry? module = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith("module.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("module.json5", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("app.json5", StringComparison.OrdinalIgnoreCase))
            {
                module = entry;
                break;
            }
        }
        if (module == null)
        {
            errors.Add("Package module identity manifest is missing.");
            return;
        }

        using StreamReader reader = new(module.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        if (!text.Contains(request.ApplicationId, StringComparison.Ordinal))
            errors.Add("Package module identity does not contain the expected application ID.");
        if (!text.Contains(request.VersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            errors.Add("Package module identity does not contain the expected version code.");
    }

    private static Dictionary<string, string> ValidateNativePayload(ZipArchive archive, HarmonyOSPackageVerificationRequest request, List<string> errors)
    {
        string expectedPrefix = "libs/" + request.Abi + "/";
        string playerPath = expectedPrefix + "libxengine_player.so";
        string hostPath = expectedPrefix + "libxengine_host.so";
        ZipArchiveEntry? playerLibrary = null;
        ZipArchiveEntry? hostLibrary = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith("libs/", StringComparison.Ordinal) || !name.EndsWith(".so", StringComparison.Ordinal))
                continue;
            if (name.Equals(playerPath, StringComparison.Ordinal) && playerLibrary == null)
                playerLibrary = entry;
            else if (name.Equals(hostPath, StringComparison.Ordinal) && hostLibrary == null)
                hostLibrary = entry;
            else if (s_allowedShippedRuntimeLibraries.Contains(Path.GetFileName(name))
                || request.ExpectedNativePayloadFiles.ContainsKey(Path.GetFileName(name)))
            {
                // .NET runtime native helpers + libc++_shared.so ship beside the player/host and are
                // expected package contents (the NativeAOT player links them dynamically).
            }
            else
                errors.Add("Unexpected native library in package: " + name);
        }
        if (playerLibrary == null)
        {
            errors.Add("ABI payload is missing " + playerPath + ".");
        }
        if (hostLibrary == null)
            errors.Add("ABI payload is missing " + hostPath + ".");

        ValidateExpectedNativePayload(archive, request, errors);

        var buildIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (playerLibrary != null)
        {
            string? buildId = ValidateLibraryElf(playerLibrary, request, request.RequiredNativeSymbols, errors);
            if (buildId != null)
                buildIds["libxengine_player.so"] = buildId;
        }
        if (hostLibrary != null)
        {
            string? buildId = ValidateLibraryElf(hostLibrary, request, Array.Empty<string>(), errors);
            if (buildId != null)
                buildIds["libxengine_host.so"] = buildId;
        }
        return buildIds;
    }

    /// <summary>
    /// Verifies every expected prebuilt native library (e.g. the FFmpeg shared libraries)
    /// is present under <c>libs/&lt;abi&gt;/</c> in the package with the exact staged bytes.
    /// These carry versioned sonames (libavcodec.so.62) the per-library ELF walker above
    /// intentionally skips, so they get a dedicated presence + hash gate.
    /// </summary>
    private static void ValidateExpectedNativePayload(
        ZipArchive archive,
        HarmonyOSPackageVerificationRequest request,
        List<string> errors)
    {
        if (request.ExpectedNativePayloadFiles.Count == 0)
            return;
        string prefix = "libs/" + request.Abi + "/";
        var packaged = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                packaged.TryAdd(name[prefix.Length..], entry);
        }
        foreach (KeyValuePair<string, string> expected in request.ExpectedNativePayloadFiles)
        {
            if (!packaged.TryGetValue(expected.Key, out ZipArchiveEntry? entry))
            {
                errors.Add("ABI payload is missing " + prefix + expected.Key + ".");
                continue;
            }
            using Stream stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            string actual = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
            if (!string.Equals(actual, expected.Value, StringComparison.Ordinal))
                errors.Add($"Native payload hash mismatch for {prefix}{expected.Key}: staged {expected.Value[..12]}…, packaged {actual[..12]}….");
        }
    }

    private static string? ValidateLibraryElf(
        ZipArchiveEntry library,
        HarmonyOSPackageVerificationRequest request,
        IReadOnlyCollection<string> requiredSymbols,
        List<string> errors)
    {
        using Stream stream = library.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        string? buildId = null;
        try
        {
            buildId = HarmonyOSElfBuildIdReader.ReadRequired(bytes, library.FullName);
        }
        catch (InvalidDataException exception)
        {
            errors.Add(exception.Message);
        }
        ValidateElf(bytes, request, requiredSymbols, errors);
        return buildId;
    }

    private static void ValidatePayloadHashManifest(ZipArchive archive, List<string> errors)
    {
        ZipArchiveEntry? manifest = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = entry.FullName.Replace('\\', '/');
            if (!path.EndsWith("/" + HarmonyOSAssetPayloadInspector.HashManifestFileName, StringComparison.Ordinal) &&
                !string.Equals(path, HarmonyOSAssetPayloadInspector.HashManifestFileName, StringComparison.Ordinal))
            {
                continue;
            }
            if (manifest != null)
            {
                errors.Add("Package contains more than one payload hash manifest.");
                return;
            }
            manifest = entry;
        }
        if (manifest == null)
        {
            errors.Add("Package payload hash manifest is missing.");
            return;
        }

        try
        {
            using Stream stream = manifest.Open();
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 1 ||
                !root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
            {
                errors.Add("Package payload hash manifest has an unsupported schema.");
            }
        }
        catch (JsonException)
        {
            errors.Add("Package payload hash manifest is invalid JSON.");
        }
    }

    private static void ValidateElf(
        byte[] bytes,
        HarmonyOSPackageVerificationRequest request,
        IReadOnlyCollection<string> requiredSymbols,
        List<string> errors)
    {
        if (bytes.Length < 64 || bytes[0] != 0x7f || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
        {
            errors.Add("Native player is not an ELF file.");
            return;
        }
        if (bytes[4] != 2 || bytes[5] != 1)
        {
            errors.Add("Native player ELF must be 64-bit little-endian.");
            return;
        }
        ushort machine = ReadUInt16(bytes, 18);
        ushort expectedMachine = request.Abi == "arm64-v8a" ? (ushort)183 : (ushort)62;
        if (machine != expectedMachine)
            errors.Add($"ELF machine {machine} does not match ABI {request.Abi}.");

        ulong programOffset = ReadUInt64(bytes, 32);
        ushort programEntrySize = ReadUInt16(bytes, 54);
        ushort programCount = ReadUInt16(bytes, 56);
        ulong dynamicOffset = 0;
        ulong dynamicSize = 0;
        var loadSegments = new List<ElfSegment>();
        for (int i = 0; i < programCount; i++)
        {
            ulong offset = programOffset + (ulong)(i * programEntrySize);
            if (!Fits(bytes, offset, 56))
                continue;
            uint type = ReadUInt32(bytes, offset);
            ulong fileOffset = ReadUInt64(bytes, offset + 8);
            ulong virtualAddress = ReadUInt64(bytes, offset + 16);
            ulong fileSize = ReadUInt64(bytes, offset + 32);
            if (type == 1)
                loadSegments.Add(new ElfSegment(fileOffset, virtualAddress, fileSize));
            else if (type == 2)
            {
                dynamicOffset = fileOffset;
                dynamicSize = fileSize;
            }
        }
        if (dynamicOffset == 0 || dynamicSize < 16 || !Fits(bytes, dynamicOffset, dynamicSize))
        {
            errors.Add("Native player ELF has no valid dynamic section.");
            return;
        }

        ulong stringTableAddress = 0;
        ulong stringTableSize = 0;
        var neededOffsets = new List<ulong>();
        for (ulong offset = dynamicOffset; offset + 16 <= dynamicOffset + dynamicSize; offset += 16)
        {
            long tag = ReadInt64(bytes, offset);
            ulong value = ReadUInt64(bytes, offset + 8);
            if (tag == 0)
                break;
            if (tag == 1)
                neededOffsets.Add(value);
            else if (tag == 5)
                stringTableAddress = value;
            else if (tag == 10)
                stringTableSize = value;
        }
        if (stringTableAddress == 0 || stringTableSize == 0)
        {
            errors.Add("Native player ELF has no valid dynamic string table.");
            return;
        }
        ulong stringTableOffset = 0;
        foreach (ElfSegment segment in loadSegments)
        {
            if (stringTableAddress >= segment.VirtualAddress && stringTableAddress < segment.VirtualAddress + segment.FileSize)
            {
                stringTableOffset = segment.FileOffset + (stringTableAddress - segment.VirtualAddress);
                break;
            }
        }
        if (stringTableOffset == 0 || !Fits(bytes, stringTableOffset, stringTableSize))
        {
            errors.Add("Native player ELF dynamic string table is outside the file.");
            return;
        }
        var allowed = new HashSet<string>(request.AllowedElfDependencies, StringComparer.Ordinal);
        for (int i = 0; i < neededOffsets.Count; i++)
        {
            string dependency = ReadCString(bytes, stringTableOffset + neededOffsets[i], stringTableOffset + stringTableSize);
            if (!allowed.Contains(dependency))
                errors.Add("Unexpected DT_NEEDED dependency: " + dependency);
        }

        ValidateDynamicSymbols(bytes, requiredSymbols, errors);
    }

    private static void ValidateDynamicSymbols(
        byte[] bytes,
        IReadOnlyCollection<string> requiredSymbols,
        List<string> errors)
    {
        if (requiredSymbols.Count == 0)
            return;

        ulong sectionOffset = ReadUInt64(bytes, 40);
        ushort sectionEntrySize = ReadUInt16(bytes, 58);
        ushort sectionCount = ReadUInt16(bytes, 60);
        if (sectionOffset == 0 || sectionCount == 0 || sectionEntrySize < 64 ||
            !Fits(bytes, sectionOffset, (ulong)sectionEntrySize * sectionCount))
        {
            errors.Add("Native player ELF has no valid dynamic symbol table.");
            return;
        }

        ElfSection? dynamicSymbols = null;
        var sections = new ElfSection[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            ulong offset = sectionOffset + (ulong)(i * sectionEntrySize);
            ElfSection section = new(
                ReadUInt32(bytes, offset),
                ReadUInt32(bytes, offset + 4),
                ReadUInt64(bytes, offset + 24),
                ReadUInt64(bytes, offset + 32),
                ReadUInt32(bytes, offset + 40),
                ReadUInt64(bytes, offset + 56));
            sections[i] = section;
            if (section.Type == 11)
                dynamicSymbols = section;
        }
        if (!dynamicSymbols.HasValue || dynamicSymbols.Value.Link >= sections.Length)
        {
            errors.Add("Native player ELF has no valid dynamic symbol table.");
            return;
        }

        ElfSection stringTable = sections[dynamicSymbols.Value.Link];
        ulong symbolEntrySize = dynamicSymbols.Value.EntrySize == 0 ? 24u : dynamicSymbols.Value.EntrySize;
        if (stringTable.Type != 3 || stringTable.Size == 0 || symbolEntrySize < 24 ||
            dynamicSymbols.Value.Size < symbolEntrySize ||
            dynamicSymbols.Value.Size % symbolEntrySize != 0 ||
            !Fits(bytes, stringTable.Offset, stringTable.Size) ||
            !Fits(bytes, dynamicSymbols.Value.Offset, dynamicSymbols.Value.Size))
        {
            errors.Add("Native player ELF dynamic symbol table is malformed.");
            return;
        }

        var exportedNames = new HashSet<string>(StringComparer.Ordinal);
        ulong symbolCount = dynamicSymbols.Value.Size / symbolEntrySize;
        for (ulong i = 0; i < symbolCount; i++)
        {
            ulong symbolOffset = dynamicSymbols.Value.Offset + i * symbolEntrySize;
            uint nameOffset = ReadUInt32(bytes, symbolOffset);
            if (nameOffset >= stringTable.Size)
                continue;
            string name = ReadCString(
                bytes,
                stringTable.Offset + nameOffset,
                stringTable.Offset + stringTable.Size);
            if (name.Length != 0)
                exportedNames.Add(name);
        }
        foreach (string required in requiredSymbols)
        {
            if (!exportedNames.Contains(required))
                errors.Add("Native player is missing exported symbol: " + required);
        }
    }

    private static void ScanForbiddenPlaintext(ZipArchive archive, IReadOnlyCollection<string> forbidden, List<string> errors)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.Length == 0 || entry.Length > 4 * 1024 * 1024)
                continue;
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string text = reader.ReadToEnd();
            foreach (string value in forbidden)
            {
                if (!string.IsNullOrEmpty(value) && text.Contains(value, StringComparison.Ordinal))
                    errors.Add("Package entry contains forbidden material: " + entry.FullName);
            }
        }
    }

    private static IReadOnlyList<string> SanitizeFindings(List<string> errors, IReadOnlyCollection<string> forbidden)
    {
        var result = new List<string>(errors.Count);
        foreach (string error in errors)
        {
            string sanitized = error;
            foreach (string value in forbidden)
            {
                if (!string.IsNullOrEmpty(value))
                    sanitized = sanitized.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            }
            // Findings are published into the build receipt, which must never carry local paths or
            // credential fragments. The explicit forbidden list only covers known roots; verifier/tool
            // diagnostics frequently embed other absolute paths (DevEco SDK, temp staging). Strip any
            // remaining Windows/unix absolute path so RequireEvidenceText accepts the value.
            sanitized = HarmonyOSReceiptPathSanitizer.SanitizePaths(sanitized);
            result.Add(sanitized);
        }
        return result;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static bool Fits(byte[] data, ulong offset, ulong length) => offset <= (ulong)data.Length && length <= (ulong)data.Length - offset;
    private static ushort ReadUInt16(byte[] data, ulong offset) => BitConverter.ToUInt16(data, checked((int)offset));
    private static uint ReadUInt32(byte[] data, ulong offset) => BitConverter.ToUInt32(data, checked((int)offset));
    private static ulong ReadUInt64(byte[] data, ulong offset) => BitConverter.ToUInt64(data, checked((int)offset));
    private static long ReadInt64(byte[] data, ulong offset) => BitConverter.ToInt64(data, checked((int)offset));

    private static string ReadCString(byte[] data, ulong offset, ulong end)
    {
        int start = checked((int)offset);
        int limit = checked((int)end);
        int current = start;
        while (current < limit && data[current] != 0)
            current++;
        return Encoding.UTF8.GetString(data, start, current - start);
    }

    private readonly record struct ElfSegment(ulong FileOffset, ulong VirtualAddress, ulong FileSize);
    private readonly record struct ElfSection(
        uint Name,
        uint Type,
        ulong Offset,
        ulong Size,
        uint Link,
        ulong EntrySize);
}
