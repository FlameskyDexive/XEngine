// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Projects.Build;

namespace XEngine.Editor.Build.HarmonyOS;

internal enum HarmonyOSBuildErrorCode
{
    PackageBuildFailed,
    PackageVerificationFailed,
    SigningReferenceInvalid,
    ArtifactPublicationFailed,
    StageContributionFailed,
}

internal static class HarmonyOSSigningReference
{
    internal static string Validate(string value)
    {
        if (string.Equals(value, "None", StringComparison.Ordinal))
            return "None";
        const string prefix = "EnvironmentVariable:";
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.SigningReferenceInvalid,
                "HarmonyOS signing reference must be None or an environment-variable reference.");

        string name = value[prefix.Length..];
        if (name.Length == 0 || !IsIdentifierStart(name[0]))
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.SigningReferenceInvalid,
                "HarmonyOS signing environment variable name is invalid.");
        for (int i = 1; i < name.Length; i++)
        {
            if (!IsIdentifierPart(name[i]))
                throw new HarmonyOSBuildException(
                    HarmonyOSBuildErrorCode.SigningReferenceInvalid,
                    "HarmonyOS signing environment variable name is invalid.");
        }
        return "EnvironmentVariable";
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
}

internal sealed class HarmonyOSBuildException : Exception
{
    internal HarmonyOSBuildException(
        HarmonyOSBuildErrorCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    internal HarmonyOSBuildErrorCode Code { get; }
}

/// <summary>Inputs for one provider Hvigor package build.</summary>
internal sealed class HarmonyOSPackageBuildRequest
{
    public string StageProjectDirectory { get; init; } = string.Empty;
    public string ArtifactRelativePath { get; init; } = string.Empty;
    public string PublishedOutputDirectory { get; init; } = string.Empty;
    public HarmonyOSOutputMode OutputMode { get; init; }
    public string HvigorExecutablePath { get; init; } = string.Empty;
    public IReadOnlyList<string> HvigorArguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string> Redactions { get; init; } = [];
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Package still confined to the generated Stage tree and not yet published.</summary>
internal sealed class HarmonyOSPendingPackage
{
    public string StageProjectDirectory { get; init; } = string.Empty;
    public string ArtifactPath { get; init; } = string.Empty;
    public string PublishedOutputDirectory { get; init; } = string.Empty;
    public HarmonyOSOutputMode OutputMode { get; init; }
    public HarmonyOSProcessResult Process { get; init; } = new(0, 0, string.Empty, string.Empty, string.Empty, false, false);
}

/// <summary>
/// Runs the locked Hvigor command and returns an unverified package confined to the generated
/// project. Publication is intentionally a separate post-verification operation.
/// </summary>
internal sealed class HarmonyOSPackageBuilder(IHarmonyOSProcessRunner? processRunner = null)
{
    private readonly IHarmonyOSProcessRunner _processRunner = processRunner ?? new HarmonyOSProcessRunner();

    internal async Task<HarmonyOSPendingPackage> BuildAsync(
        HarmonyOSPackageBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        string stageRoot = Path.GetFullPath(request.StageProjectDirectory);
        string artifactPath = GetContainedPath(stageRoot, request.ArtifactRelativePath);
        HarmonyOSProcessResult process;
        try
        {
            process = await _processRunner.RunAsync(new HarmonyOSProcessCommand
            {
                ExecutablePath = request.HvigorExecutablePath,
                Arguments = request.HvigorArguments,
                WorkingDirectory = stageRoot,
                Environment = request.Environment,
                Timeout = request.Timeout,
                Redactions = request.Redactions,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.PackageBuildFailed,
                "HarmonyOS Hvigor package build could not start.",
                exception);
        }

        if (process.TimedOut || process.ExitCode != 0)
        {
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.PackageBuildFailed,
                "HarmonyOS Hvigor package build failed: " + process.CombinedOutput);
        }

        if (!File.Exists(artifactPath) || new FileInfo(artifactPath).Length == 0)
        {
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.PackageBuildFailed,
                $"HarmonyOS Hvigor completed without producing the declared artifact '{request.ArtifactRelativePath}'.");
        }

        return new HarmonyOSPendingPackage
        {
            StageProjectDirectory = stageRoot,
            ArtifactPath = artifactPath,
            PublishedOutputDirectory = Path.GetFullPath(request.PublishedOutputDirectory),
            OutputMode = request.OutputMode,
            Process = process,
        };
    }

    /// <summary>
    /// Publishes a verified package and its redacted receipt together. The final directory is never
    /// overwritten; an empty pre-created directory is replaced by one atomic sibling move.
    /// </summary>
    internal async Task<string> PublishVerifiedAsync(
        HarmonyOSPendingPackage package,
        HarmonyOSPackageVerification verification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(verification);
        if (!verification.Success)
        {
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.PackageVerificationFailed,
                "HarmonyOS package verification failed; no artifact was published.");
        }
        if (string.IsNullOrWhiteSpace(verification.ReceiptJson))
        {
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.PackageVerificationFailed,
                "HarmonyOS package verification produced no receipt; no artifact was published.");
        }

        string stageRoot = Path.GetFullPath(package.StageProjectDirectory);
        string artifactPath = Path.GetFullPath(package.ArtifactPath);
        if (!File.Exists(artifactPath) || !IsContainedPath(stageRoot, artifactPath))
        {
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.ArtifactPublicationFailed,
                "HarmonyOS package artifact is missing or escaped its generated Stage directory.");
        }

        string outputDirectory = Path.GetFullPath(package.PublishedOutputDirectory);
        if (PathsEqual(stageRoot, outputDirectory) || IsContainedPath(stageRoot, outputDirectory) || IsContainedPath(outputDirectory, stageRoot))
        {
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.ArtifactPublicationFailed,
                "HarmonyOS published output cannot overlap the generated Stage directory.");
        }
        string parent = Path.GetDirectoryName(outputDirectory)
            ?? throw new HarmonyOSBuildException(HarmonyOSBuildErrorCode.ArtifactPublicationFailed, "HarmonyOS published output has no parent directory.");
        string stagingDirectory = Path.Combine(
            parent,
            "." + Path.GetFileName(outputDirectory) + ".xengine-harmonyos-" + Guid.NewGuid().ToString("N") + ".staging");

        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(stagingDirectory);
            string artifactName = Path.GetFileName(artifactPath);
            string stagedArtifact = Path.Combine(stagingDirectory, artifactName);
            File.Copy(artifactPath, stagedArtifact, overwrite: false);
            string receiptPath = stagedArtifact + ".receipt.json";
            await File.WriteAllTextAsync(
                receiptPath,
                verification.ReceiptJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(outputDirectory))
            {
                if (HasEntries(outputDirectory))
                {
                    throw new HarmonyOSBuildException(
                        HarmonyOSBuildErrorCode.ArtifactPublicationFailed,
                        $"HarmonyOS published output directory is not empty: {outputDirectory}");
                }
                Directory.Delete(outputDirectory, recursive: false);
            }
            Directory.Move(stagingDirectory, outputDirectory);
            return Path.Combine(outputDirectory, artifactName);
        }
        catch (HarmonyOSBuildException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HarmonyOSBuildException(
                HarmonyOSBuildErrorCode.ArtifactPublicationFailed,
                "HarmonyOS verified package could not be published: " + exception.Message,
                exception);
        }
        finally
        {
            TryDeleteStaging(stagingDirectory);
        }
    }

    private static void ValidateRequest(HarmonyOSPackageBuildRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StageProjectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PublishedOutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HvigorExecutablePath);
        if (request.OutputMode is not HarmonyOSOutputMode.Hap and not HarmonyOSOutputMode.App)
            throw new ArgumentOutOfRangeException(nameof(request.OutputMode), "HarmonyOS package builds require HAP or APP output mode.");
        if (!Directory.Exists(request.StageProjectDirectory))
            throw new DirectoryNotFoundException($"HarmonyOS Stage project is missing: {request.StageProjectDirectory}");
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request.Timeout), "HarmonyOS package timeout must be positive.");

        string extension = Path.GetExtension(request.ArtifactRelativePath);
        string expected = request.OutputMode == HarmonyOSOutputMode.Hap ? ".hap" : ".app";
        if (!string.Equals(extension, expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"HarmonyOS {request.OutputMode} output must declare a {expected} artifact.", nameof(request.ArtifactRelativePath));
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath[0] == '/' ||
            relativePath.Contains('\\') || Path.IsPathFullyQualified(relativePath))
            throw new InvalidDataException($"HarmonyOS package artifact path is invalid: {relativePath}");
        string[] segments = relativePath.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] is "." or "..")
                throw new InvalidDataException($"HarmonyOS package artifact path is invalid: {relativePath}");
        }

        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"HarmonyOS package artifact path escapes Stage: {relativePath}");
        return path;
    }

    private static bool IsContainedPath(string root, string path)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedPath.StartsWith(prefix, comparison);
    }

    private static bool PathsEqual(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }

    private static void TryDeleteStaging(string path)
    {
        if (!Path.GetFileName(path).Contains(".xengine-harmonyos-", StringComparison.Ordinal) ||
            !path.EndsWith(".staging", StringComparison.Ordinal))
        {
            return;
        }
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
