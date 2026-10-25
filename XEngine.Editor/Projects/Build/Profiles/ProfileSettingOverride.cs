// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Editor.Build.Profiles;

/// <summary>
/// One Player Settings field overridden for the duration of a build driven by a
/// <see cref="BuildProfile"/>.
/// </summary>
/// <remarks>
/// <para>Matches Unity's "Player Settings overrides" on a build profile: the project-wide value in
/// <c>ProjectSettings/{SettingsName}.yaml</c> stays untouched, and the override is only visible in
/// the artifacts that <c>BuildPipeline.ExportSettings</c> writes.</para>
/// <para><see cref="Value"/> is stored as Echo text rather than a typed field so a single list can
/// carry ints, bools, strings and enums without a discriminated union. It is decoded against the
/// static type of the target member when applied.</para>
/// </remarks>
public sealed class ProfileSettingOverride
{
    /// <summary>
    /// The settings group's registered name - the <c>Name</c> passed to
    /// <c>[ProjectSettings]</c>, which is also the <c>ProjectSettings/{Name}.yaml</c> file stem.
    /// </summary>
    public string SettingsName = "";

    /// <summary>Name of the public field or property to override. Top level only in v1.</summary>
    public string FieldPath = "";

    /// <summary>Echo-encoded replacement value.</summary>
    public string Value = "";

    public bool Enabled = true;

    public bool IsValid()
        => !string.IsNullOrWhiteSpace(SettingsName) && !string.IsNullOrWhiteSpace(FieldPath);
}
