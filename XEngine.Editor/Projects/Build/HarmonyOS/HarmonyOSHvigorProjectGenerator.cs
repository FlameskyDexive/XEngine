// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.HarmonyOS;

internal sealed record HarmonyOSStageTemplateValues(
    string ApplicationId,
    string ApplicationLabel,
    int VersionCode,
    int ApiLevel,
    string Abi,
    string PlayerLibrary,
    string AssemblyName);

/// <summary>Materializes only Stage files declared by the locked Bundle template manifest.</summary>
internal static class HarmonyOSHvigorProjectGenerator
{
    private static readonly UTF8Encoding s_utf8NoBom = new(false);

    internal static async Task MaterializeAsync(
        string destinationRoot,
        IReadOnlyList<HarmonyOSVerifiedTemplateFile> files,
        HarmonyOSStageTemplateValues values,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(values);
        Directory.CreateDirectory(destinationRoot);

        bool versionCodeTokenUsed = false;
        for (int i = 0; i < files.Count; i++)
        {
            HarmonyOSVerifiedTemplateFile file = files[i];
            string destination = GetDestinationPath(destinationRoot, file.RelativePath);
            string content = await File.ReadAllTextAsync(file.SourcePath, cancellationToken).ConfigureAwait(false);
            string materialized = ReplaceTokens(content, file.RelativePath, values, out bool fileUsedVersionCodeToken);
            versionCodeTokenUsed |= fileUsedVersionCodeToken;
            string? directory = Path.GetDirectoryName(destination);
            if (directory != null)
                Directory.CreateDirectory(directory);
            string normalized = materialized.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
            await File.WriteAllTextAsync(destination, normalized, s_utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
        if (!versionCodeTokenUsed)
            throw new InvalidDataException("HarmonyOS Stage template must contain the required VERSION_CODE token.");
    }

    private static string ReplaceTokens(
        string content,
        string templatePath,
        HarmonyOSStageTemplateValues values,
        out bool usedVersionCodeToken)
    {
        var result = new StringBuilder(content.Length);
        usedVersionCodeToken = false;
        int offset = 0;
        while (offset < content.Length)
        {
            int opening = content.IndexOf("{{", offset, StringComparison.Ordinal);
            int closing = content.IndexOf("}}", offset, StringComparison.Ordinal);
            if (closing >= 0 && (opening < 0 || closing < opening))
                throw new InvalidDataException($"HarmonyOS Stage template has an unmatched token terminator: {templatePath}");
            if (opening < 0)
            {
                result.Append(content, offset, content.Length - offset);
                break;
            }

            result.Append(content, offset, opening - offset);
            int end = content.IndexOf("}}", opening + 2, StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidDataException($"HarmonyOS Stage template has an unterminated token: {templatePath}");
            string token = content.Substring(opening + 2, end - opening - 2);
            usedVersionCodeToken |= token == "VERSION_CODE";
            result.Append(GetTokenValue(token, templatePath, values));
            offset = end + 2;
        }
        return result.ToString();
    }

    private static string GetTokenValue(string token, string templatePath, HarmonyOSStageTemplateValues values) => token switch
    {
        "APPLICATION_ID" => values.ApplicationId,
        "APPLICATION_LABEL" => values.ApplicationLabel,
        "VERSION_CODE" => values.VersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "API_LEVEL" => values.ApiLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "ABI" => values.Abi,
        "PLAYER_LIBRARY" => values.PlayerLibrary,
        "ASSEMBLY_NAME" => values.AssemblyName,
        _ => throw new InvalidDataException($"HarmonyOS Stage template contains an unsupported token '{token}': {templatePath}"),
    };

    private static string GetDestinationPath(string destinationRoot, string relativePath)
    {
        string prefix = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!destination.StartsWith(prefix, comparison))
            throw new InvalidDataException($"HarmonyOS Stage template file escapes destination: {relativePath}");
        return destination;
    }
}
