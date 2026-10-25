// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

namespace XEngine.Editor.Build.Profiles;

/// <summary>
/// Applies a profile's <see cref="ProfileSettingOverride"/> list to the live project settings for the
/// duration of a build, then puts every touched value back.
/// </summary>
/// <remarks>
/// <para>The settings that get exported into a player are the in-memory singletons in
/// <see cref="EditorRegistries.SettingsEntries"/>, not the YAML on disk, so an override has to
/// mutate those singletons to have any effect. Doing that permanently would make building a profile
/// silently rewrite the user's Player Settings, so this is a scope: it snapshots the previous value
/// of every member it touches and restores it on <see cref="Dispose"/>, including when the build
/// throws.</para>
/// <para>Nothing is written to <c>ProjectSettings/*.yaml</c> while the scope is open. The editor's
/// own save path is driven by user edits, and the restore happens before any of those can run,
/// because a build holds the main thread.</para>
/// </remarks>
public sealed class ProfileSettingsOverrideScope : IDisposable
{
    private readonly List<(object Target, MemberInfo Member, object? PreviousValue)> _restore = [];
    private readonly List<string> _warnings = [];
    private bool _disposed;

    private ProfileSettingsOverrideScope() { }

    /// <summary>Number of overrides that were successfully applied.</summary>
    public int AppliedCount => _restore.Count;

    /// <summary>Overrides that could not be applied, with the reason. Surfaced in the build log.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Apply every enabled override on <paramref name="profile"/>. A null profile, or one with no
    /// overrides, produces an inert scope so callers can wrap unconditionally.
    /// </summary>
    public static ProfileSettingsOverrideScope Apply(BuildProfile? profile)
    {
        var scope = new ProfileSettingsOverrideScope();
        if (profile == null) return scope;

        foreach (ProfileSettingOverride entry in profile.SettingOverrides)
        {
            if (entry == null || !entry.Enabled) continue;

            if (!entry.IsValid())
            {
                scope._warnings.Add("Skipped an override with no settings group or field name.");
                continue;
            }

            scope.ApplyOne(entry);
        }

        return scope;
    }

    private void ApplyOne(ProfileSettingOverride entry)
    {
        ProjectSettingsBase? instance = FindSettingsInstance(entry.SettingsName);
        if (instance == null)
        {
            _warnings.Add($"No settings group named '{entry.SettingsName}'.");
            return;
        }

        MemberInfo? member = FindMember(instance.GetType(), entry.FieldPath);
        if (member == null)
        {
            _warnings.Add($"'{entry.SettingsName}' has no writable field or property '{entry.FieldPath}'.");
            return;
        }

        Type memberType = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
        if (!TryConvert(entry.Value, memberType, out object? converted))
        {
            _warnings.Add($"Cannot read '{entry.Value}' as {memberType.Name} for '{entry.SettingsName}.{entry.FieldPath}'.");
            return;
        }

        object? previous = member is FieldInfo f ? f.GetValue(instance) : ((PropertyInfo)member).GetValue(instance);
        _restore.Add((instance, member, previous));

        if (member is FieldInfo setField) setField.SetValue(instance, converted);
        else ((PropertyInfo)member).SetValue(instance, converted);
    }

    private static ProjectSettingsBase? FindSettingsInstance(string settingsName)
    {
        foreach (EditorRegistries.SettingsEntry entry in EditorRegistries.SettingsEntries)
        {
            if (string.Equals(entry.Name, settingsName, StringComparison.OrdinalIgnoreCase))
                return entry.Instance;
        }

        return null;
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        FieldInfo? field = type.GetField(name, flags);
        if (field != null && !field.IsInitOnly) return field;

        PropertyInfo? property = type.GetProperty(name, flags);
        if (property != null && property.CanRead && property.CanWrite) return property;

        return null;
    }

    /// <summary>
    /// Decode an authored override value against the target member's static type. Deliberately
    /// limited to the scalar shapes a Player Setting actually uses - anything structured belongs in
    /// the settings asset itself rather than in a one-line override.
    /// </summary>
    private static bool TryConvert(string raw, Type targetType, out object? value)
    {
        value = null;
        Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        string text = raw ?? "";

        try
        {
            if (type == typeof(string)) { value = text; return true; }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AssetRef<>) &&
                Guid.TryParse(text.Trim(), out Guid assetId))
            {
                value = Activator.CreateInstance(type, assetId);
                return true;
            }

            if (type.IsEnum)
            {
                if (!Enum.TryParse(type, text.Trim(), ignoreCase: true, out object? parsed)) return false;
                value = parsed;
                return true;
            }

            if (type == typeof(bool))
            {
                if (!bool.TryParse(text.Trim(), out bool b)) return false;
                value = b;
                return true;
            }

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)
                || type == typeof(int) || type == typeof(long) || type == typeof(short)
                || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong)
                || type == typeof(ushort) || type == typeof(sbyte))
            {
                value = Convert.ChangeType(text.Trim(), type, CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>Log a one-line summary of what this scope did, for the build log.</summary>
    public void LogSummary()
    {
        if (AppliedCount > 0)
            Debug.Log($"[Build Profiles] Applied {AppliedCount} Player Settings override(s) for this build.");

        foreach (string warning in _warnings)
            Debug.LogWarning($"[Build Profiles] {warning}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Reverse order so two overrides targeting the same member unwind to the original value.
        for (int i = _restore.Count - 1; i >= 0; i--)
        {
            (object target, MemberInfo member, object? previous) = _restore[i];
            try
            {
                if (member is FieldInfo field) field.SetValue(target, previous);
                else ((PropertyInfo)member).SetValue(target, previous);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Build Profiles] Failed to restore '{member.Name}' after the build: {ex.Message}");
            }
        }

        _restore.Clear();
    }
}
