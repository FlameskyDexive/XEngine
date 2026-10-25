// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace XEngine.Editor.Build.Ios;

internal sealed record IosProvisioningProfileInfo(
    string Uuid,
    DateTimeOffset Expiration,
    string TeamId,
    string Name);

/// <summary>
/// Reads a decoded XML provisioning plist. Windows cannot unwrap Apple CMS
/// <c>.mobileprovision</c> files; those must be signed on macOS.
/// </summary>
internal static class IosProvisioningProfileReader
{
    internal const string CmsUnsupportedError =
        "Windows cannot unwrap Apple CMS .mobileprovision files. Export the profile as XML plist, or sign on macOS.";

    internal const string BinaryPlistUnsupportedError =
        "Binary plist provisioning profiles cannot be decoded on this host. Export XML plist, or sign on macOS.";

    public static IosProvisioningProfileInfo Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Provisioning profile not found: " + path, path);

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 1 && bytes[0] == 0x30)
            throw new InvalidDataException(CmsUnsupportedError);
        if (bytes.Length >= 6 && bytes[0] == (byte)'b' && bytes[1] == (byte)'p' && bytes[2] == (byte)'l')
            throw new InvalidDataException(BinaryPlistUnsupportedError);

        string text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("BEGIN PKCS7", StringComparison.Ordinal) ||
            text.Contains("BEGIN CMS", StringComparison.Ordinal))
        {
            throw new InvalidDataException(CmsUnsupportedError);
        }

        if (!text.Contains("<plist", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Provisioning profile is not an XML plist.");

        string uuid = RequireTaggedValue(text, "UUID", "string");
        string name = RequireTaggedValue(text, "Name", "string");
        string expirationText = RequireTaggedValue(text, "ExpirationDate", "date");
        if (!DateTimeOffset.TryParse(
                expirationText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset expiration))
        {
            throw new InvalidDataException("Provisioning profile ExpirationDate is not a valid timestamp.");
        }

        string? teamId = TryReadTeamIdentifier(text);
        if (string.IsNullOrWhiteSpace(teamId))
            throw new InvalidDataException("Provisioning profile is missing TeamIdentifier.");

        return new IosProvisioningProfileInfo(uuid, expiration, teamId, name);
    }

    internal static string CreateXmlFixture(
        string uuid,
        string teamId,
        string name,
        DateTimeOffset expiration) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>UUID</key>
          <string>{uuid}</string>
          <key>Name</key>
          <string>{name}</string>
          <key>ExpirationDate</key>
          <date>{expiration.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}</date>
          <key>TeamIdentifier</key>
          <array>
            <string>{teamId}</string>
          </array>
        </dict>
        </plist>
        """;

    private static string? TryReadTeamIdentifier(string plist)
    {
        int keyIndex = IndexOfKey(plist, "TeamIdentifier");
        if (keyIndex < 0)
            return null;

        int cursor = keyIndex + "<key>TeamIdentifier</key>".Length;
        int array = IndexOfTag(plist, "<array", cursor);
        int str = IndexOfTag(plist, "<string", cursor);
        if (array >= 0 && (str < 0 || array < str))
            return ReadFirstTagValue(plist, "string", array);

        return ReadFirstTagValue(plist, "string", cursor);
    }

    private static string RequireTaggedValue(string plist, string key, string tag)
    {
        int keyIndex = IndexOfKey(plist, key);
        if (keyIndex < 0)
            throw new InvalidDataException("Provisioning profile is missing " + key + ".");

        string? value = ReadFirstTagValue(plist, tag, keyIndex + ("<key>" + key + "</key>").Length);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Provisioning profile " + key + " is empty.");
        return value;
    }

    private static int IndexOfKey(string plist, string key) =>
        plist.IndexOf("<key>" + key + "</key>", StringComparison.Ordinal);

    private static int IndexOfTag(string plist, string tag, int start) =>
        plist.IndexOf(tag, start, StringComparison.OrdinalIgnoreCase);

    private static string? ReadFirstTagValue(string plist, string tag, int start)
    {
        string open = "<" + tag;
        int openIndex = IndexOfTag(plist, open, start);
        if (openIndex < 0)
            return null;
        int valueStart = plist.IndexOf('>', openIndex);
        if (valueStart < 0)
            return null;
        valueStart++;
        int close = plist.IndexOf("</" + tag + ">", valueStart, StringComparison.OrdinalIgnoreCase);
        if (close < 0)
            return null;
        return plist[valueStart..close].Trim();
    }
}
