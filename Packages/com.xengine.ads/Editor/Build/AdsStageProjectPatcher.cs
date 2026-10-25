// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XEngine.Ads.Editor.Build;

/// <summary>Build-time snapshot of the active vendor's ads configuration.</summary>
public sealed class AdsBuildConfig
{
    public string ProviderId = "mock";
    public bool UseTestAds = true;
    public string VendorAppId = string.Empty;
    public string RewardVideoSlotId = string.Empty;
    public string InterstitialSlotId = string.Empty;

    /// <summary>
    /// Pangle only: path to the vendor <c>openadsdk.har</c> (distributed through the CSJ
    /// portal's own channels, not the public ohpm registry). Required for pangle builds.
    /// </summary>
    public string HarSourcePath = string.Empty;

    public bool IsActive => ProviderId is "huawei-adskit" or "pangle";
}

/// <summary>
/// Pure file operations that inject ads support into a materialized HarmonyOS Stage project:
/// the vendor ArkTS glue, the EntryAbility wiring, module permissions, and the runtime
/// <c>Settings/Ads.json</c> payload. Deliberately free of editor services so the package test
/// project can drive it directly; <see cref="AdsStageProjectContributor"/> supplies the editor
/// state around it. Every step is idempotent — repeated builds over a reused export stay
/// correct.
/// </summary>
public static class AdsStageProjectPatcher
{
    private const string EntryEtsRoot = "entry/src/main/ets";
    private const string GlueEtsDirectory = EntryEtsRoot + "/xengineads";
    private const string EntryAbilityRelativePath = EntryEtsRoot + "/entryability/EntryAbility.ets";
    private const string PayloadSettingsRelativePath = "entry/src/main/resources/resfile/Settings";
    private const string AdsPayloadFileName = "Ads.json";

    /// <summary>Package-relative ArkTS glue source for <paramref name="providerId"/>.</summary>
    public static string GluePackageRelativePath(string providerId) => providerId switch
    {
        "huawei-adskit" => "Native/glue/XengineAdsHuawei.ets",
        "pangle" => "Native/glue/XengineAdsPangle.ets",
        _ => throw new ArgumentOutOfRangeException(nameof(providerId), providerId, "Unknown ads provider."),
    };

    /// <summary>
    /// Applies the full ads contribution. Accumulates human-readable errors instead of
    /// throwing so one build reports every problem at once.
    /// </summary>
    public static void Apply(string stageProjectDirectory, string packageRoot, AdsBuildConfig config, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (!config.IsActive)
            return;

        string glueSource = Path.Combine(packageRoot, GluePackageRelativePath(config.ProviderId).Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(glueSource))
        {
            errors.Add($"ads glue source is missing from the package: {GluePackageRelativePath(config.ProviderId)}");
            return;
        }
        string glueTargetDirectory = Path.Combine(stageProjectDirectory, GlueEtsDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(glueTargetDirectory);
        string glueTarget = Path.Combine(glueTargetDirectory, Path.GetFileName(glueSource));
        File.Copy(glueSource, glueTarget, overwrite: true);

        PatchEntryAbility(stageProjectDirectory, Path.GetFileName(glueSource), errors);
        EnsurePermissions(stageProjectDirectory, errors);

        if (config.ProviderId == "pangle")
        {
            // The vendor har rides into the HAP through the generated project's libs dir;
            // hvigor unpacks and packs it with the entry module.
            HarmonyOSStageProjectPatchStub.EnsureLocalHarDependency(stageProjectDirectory, "openadsdk", config.HarSourcePath, errors);
        }

        WritePayloadSettings(stageProjectDirectory, config, errors);
    }

    /// <summary>
    /// Wires the glue into the generated EntryAbility: a side-effect import and a
    /// <c>setAdsContext(this.context)</c> call at the top of <c>onCreate</c>.
    /// </summary>
    public static void PatchEntryAbility(string stageProjectDirectory, string glueFileName, List<string> errors)
    {
        string entryAbilityPath = Path.Combine(stageProjectDirectory, EntryAbilityRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(entryAbilityPath))
        {
            errors.Add($"Stage EntryAbility is missing: {EntryAbilityRelativePath}");
            return;
        }
        string source = File.ReadAllText(entryAbilityPath);
        string moduleName = Path.GetFileNameWithoutExtension(glueFileName);
        if (source.Contains("xengineads/" + moduleName, StringComparison.Ordinal))
            return; // already wired by a previous build

        string importLine = $"import {{ setAdsContext }} from '../xengineads/{moduleName}';";
        int lastImportEnd = source.LastIndexOf("import ", StringComparison.Ordinal);
        if (lastImportEnd < 0)
        {
            source = importLine + Environment.NewLine + source;
        }
        else
        {
            int lineEnd = source.IndexOf('\n', lastImportEnd);
            int insertAt = lineEnd < 0 ? source.Length : lineEnd + 1;
            source = source.Insert(insertAt, importLine + Environment.NewLine);
        }

        const string onCreateAnchor = "onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {";
        int anchor = source.IndexOf(onCreateAnchor, StringComparison.Ordinal);
        if (anchor < 0)
        {
            errors.Add("Stage EntryAbility has no onCreate anchor; cannot wire the ads glue context.");
            return;
        }
        int bodyStart = anchor + onCreateAnchor.Length;
        source = source.Insert(bodyStart, Environment.NewLine + "    setAdsContext(this.context);");
        File.WriteAllText(entryAbilityPath, source, new UTF8Encoding(false));
    }

    /// <summary>Network permission is required for every vendor's ad requests.</summary>
    public static void EnsurePermissions(string stageProjectDirectory, List<string> errors)
    {
        try
        {
            HarmonyOSStageProjectPatchStub.EnsureModulePermissions(stageProjectDirectory, "ohos.permission.INTERNET");
        }
        catch (Exception exception)
        {
            errors.Add("failed to ensure module permissions: " + exception.Message);
        }
    }

    /// <summary>
    /// Writes the flat runtime settings payload the game reads through
    /// <c>AdsBootstrapper.TryLoadFromPlayerSettings</c>.
    /// </summary>
    public static void WritePayloadSettings(string stageProjectDirectory, AdsBuildConfig config, List<string> errors)
    {
        try
        {
            string settingsDirectory = Path.Combine(stageProjectDirectory, PayloadSettingsRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(Path.Combine(settingsDirectory, AdsPayloadFileName), BuildPayloadJson(config), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            errors.Add("failed to write the ads settings payload: " + exception.Message);
        }
    }

    public static string BuildPayloadJson(AdsBuildConfig config)
    {
        var json = new StringBuilder(192);
        json.Append("{\n");
        json.Append("  \"schema\": \"xengine.ads.settings.v1\",\n");
        json.Append("  \"providerId\": \"").Append(Escape(config.ProviderId)).Append("\",\n");
        json.Append("  \"useTestAds\": ").Append(config.UseTestAds ? "true" : "false").Append(",\n");
        json.Append("  \"appId\": \"").Append(Escape(config.VendorAppId)).Append("\",\n");
        json.Append("  \"rewardVideoSlotId\": \"").Append(Escape(config.RewardVideoSlotId)).Append("\",\n");
        json.Append("  \"interstitialSlotId\": \"").Append(Escape(config.InterstitialSlotId)).Append("\"\n");
        json.Append("}");
        return json.ToString();
    }

    private static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': escaped.Append("\\\""); break;
                case '\\': escaped.Append("\\\\"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                default: escaped.Append(c); break;
            }
        }
        return escaped.ToString();
    }
}

/// <summary>
/// Minimal standalone copy of the engine's Stage-project patch helpers for package-local use
/// (the engine helpers live in the editor assembly, which the package test project does not
/// reference). Kept behavior-compatible with <c>HarmonyOSStageProjectPatch</c>.
/// </summary>
public static class HarmonyOSStageProjectPatchStub
{
    public static void EnsureModulePermissions(string stageProjectDirectory, params string[] permissions)
    {
        string modulePath = Path.Combine(stageProjectDirectory, "entry", "src", "main", "module.json5");
        if (!File.Exists(modulePath))
            throw new FileNotFoundException("HarmonyOS Stage module manifest is missing.", modulePath);
        string json = File.ReadAllText(modulePath);

        var missing = new List<string>();
        foreach (string permission in permissions)
        {
            if (!string.IsNullOrWhiteSpace(permission) && !json.Contains(permission, StringComparison.Ordinal))
                missing.Add(permission);
        }
        if (missing.Count == 0)
            return;

        int arrayStart = json.IndexOf("\"requestPermissions\"", StringComparison.Ordinal);
        if (arrayStart >= 0)
        {
            int openBracket = json.IndexOf('[', arrayStart);
            if (openBracket < 0)
                throw new InvalidDataException($"module.json5 has 'requestPermissions' without '[': {modulePath}");
            string entries = string.Join(Environment.NewLine, missing.ConvertAll(p => "        { \"name\": \"" + p + "\" },"));
            json = json.Insert(openBracket + 1, Environment.NewLine + entries);
        }
        else
        {
            int insertion = json.IndexOf("\"abilities\"", StringComparison.Ordinal);
            if (insertion < 0)
                throw new InvalidDataException($"module.json5 has no 'abilities' anchor: {modulePath}");
            string block = "    \"requestPermissions\": [ " +
                string.Join(", ", missing.ConvertAll(p => "{ \"name\": \"" + p + "\" }")) + " ]," + Environment.NewLine;
            json = json.Insert(insertion, block);
        }
        File.WriteAllText(modulePath, json, new UTF8Encoding(false));
    }

    /// <summary>
    /// Copies a vendor HAR into <c>entry/libs/&lt;moduleName&gt;.har</c> and references it from the
    /// entry module's <c>oh-package.json5</c> as <c>file:./libs/&lt;moduleName&gt;.har</c>
    /// (DevEco local-har convention; matches the CSJ openadsdk integration steps).
    /// </summary>
    public static void EnsureLocalHarDependency(string stageProjectDirectory, string moduleName, string harSourcePath, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(harSourcePath))
        {
            errors.Add($"pangle requires the vendor '{moduleName}.har'; set the har path in Project Settings → Ads");
            return;
        }
        if (!File.Exists(harSourcePath))
        {
            errors.Add($"pangle har not found at '{harSourcePath}' (Project Settings → Ads)");
            return;
        }
        if (!string.Equals(Path.GetExtension(harSourcePath), ".har", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"pangle har path must point at a .har archive: '{harSourcePath}'");
            return;
        }

        try
        {
            string libsRoot = Path.Combine(stageProjectDirectory, "entry", "libs");
            Directory.CreateDirectory(libsRoot);
            File.Copy(harSourcePath, Path.Combine(libsRoot, moduleName + ".har"), overwrite: true);

            string packagePath = Path.Combine(stageProjectDirectory, "entry", "oh-package.json5");
            if (!File.Exists(packagePath))
                throw new FileNotFoundException("Stage entry oh-package.json5 is missing.", packagePath);
            string json = File.ReadAllText(packagePath);
            string reference = "\"" + moduleName + "\": \"file:./libs/" + moduleName + ".har\"";
            if (json.Contains(reference, StringComparison.Ordinal))
                return;

            int dependencies = json.IndexOf("\"dependencies\"", StringComparison.Ordinal);
            int openBrace = dependencies >= 0 ? json.IndexOf('{', dependencies) : -1;
            if (openBrace < 0)
                throw new InvalidDataException($"Stage oh-package.json5 has no 'dependencies' block: {packagePath}");
            json = json.Insert(openBrace + 1, Environment.NewLine + "        " + reference + ",");
            File.WriteAllText(packagePath, json, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            errors.Add("failed to inject the pangle har dependency: " + exception.Message);
        }
    }
}
