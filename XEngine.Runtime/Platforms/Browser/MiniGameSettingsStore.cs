// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text;

namespace XEngine.Runtime.Platforms.Browser;

/// <summary>
/// v1 mini-game settings. Small keys only (<see cref="KeyPrefix"/>, ≤ <see cref="MaxValueBytes"/>).
/// The JS adapter drains <see cref="TakePendingWrite"/> onto <c>wx.setStorageSync</c> /
/// <c>tt.setStorageSync</c>. Large saves are out of scope (user dir / cloud).
/// </summary>
public static class MiniGameSettingsStore
{
    public const string KeyPrefix = "xengine.settings.";
    public const int MaxValueBytes = 1024;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
    private static string? s_pendingPacked;

    public static bool IsSettingsKey(string key) =>
        !string.IsNullOrEmpty(key) && key.StartsWith(KeyPrefix, StringComparison.Ordinal);

    public static void Hydrate(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!IsSettingsKey(key) || Encoding.UTF8.GetByteCount(value) > MaxValueBytes)
            return;
        lock (Gate)
            Values[key] = value;
    }

    public static bool TrySet(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!IsSettingsKey(key) || Encoding.UTF8.GetByteCount(value) > MaxValueBytes)
            return false;
        lock (Gate)
        {
            Values[key] = value;
            s_pendingPacked = key + "\n" + value;
        }

        return true;
    }

    public static string Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (Gate)
            return Values.TryGetValue(key, out string? value) ? value : string.Empty;
    }

    /// <summary>Packed <c>key\\nvalue</c> for the JS adapter to write with setStorageSync.</summary>
    public static string? TakePendingWrite()
    {
        lock (Gate)
        {
            string? packed = s_pendingPacked;
            s_pendingPacked = null;
            return packed;
        }
    }

    public static void ResetForTests()
    {
        lock (Gate)
        {
            Values.Clear();
            s_pendingPacked = null;
        }
    }
}
