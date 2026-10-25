// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>
/// Strips absolute local paths and credential fragments from verifier/tool diagnostics so they can
/// be safely published into a build receipt. Mirrors the patterns <see cref="HarmonyOSBuildReceipt"/>
/// rejects via RequireEvidenceText; findings come from untrusted tool output and frequently embed
/// paths outside the explicit forbidden-plaintext list (DevEco SDK, temp staging), so they must be
/// scrubbed before the receipt writer sees them.
/// </summary>
internal static class HarmonyOSReceiptPathSanitizer
{
    private static readonly Regex s_credentialAssignment = new(
        @"(?:^|[\s;])(password|passphrase|token|private[_-]?key|secret)\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_windowsAbsolutePath = new(
        @"[A-Za-z]:[\\/](?:[^\s;:""|<>]*[\\/])+[^\s;:""|<>]*",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_unixLocalPath = new(
        @"/(?:Users|home|tmp|var|opt|mnt)/(?:[^\s;:""|<>]*/)*[^\s;:""|<>]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// Replaces Windows/unix absolute paths and credential-assignment fragments with [REDACTED].
    /// Idempotent: the replacement token contains no path separators and won't be re-matched.
    /// </summary>
    internal static string SanitizePaths(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        string sanitized = s_windowsAbsolutePath.Replace(value, "[REDACTED]");
        sanitized = s_unixLocalPath.Replace(sanitized, "[REDACTED]");
        sanitized = s_credentialAssignment.Replace(sanitized, match =>
        {
            // Keep the leading space/separator; redact only the key=value tail.
            int eq = match.Value.IndexOf('=');
            return eq < 0 ? "[REDACTED]" : match.Value[..eq] + "=[REDACTED]";
        });
        return sanitized;
    }
}
