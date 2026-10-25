// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// AssetBundleCollectorSetting - the build-time asset-to-bundle grouping configuration. Hierarchical:
// a Setting holds Packages; each Package holds Groups; each Group holds Collectors; each Collector
// points at a folder/file in the project's Assets/ tree plus the rules that decide which assets it
// contributes, how they're addressed, and which bundle they land in.
//
// Independent rewrite of YooAsset's BundleCollectorSetting (Apache-2.0). The package/group/collector
// shape matches; the rule enums are XEngine-flavored.

using System;
using System.Collections.Generic;

using XEngine.Editor.Core;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;

namespace XEngine.Editor.AssetBundles.Collector;

/// <summary>How the assets under a collector are packed into bundles.</summary>
public enum CollectorPackRule
{
    InheritGroup = -1,
    /// <summary>Pack the whole collector folder into a single bundle (one bundle per collector).</summary>
    PackTogether,

    /// <summary>Pack each top-level asset in its own bundle (granular, Unity-5-style).</summary>
    PackSeparately,

    /// <summary>Pack each subdirectory under the collector into its own bundle.</summary>
    PackByDirectory,
    /// <summary>All collectors in this group share one bundle.</summary>
    PackByGroup,
}

/// <summary>How an asset's addressable address is derived.</summary>
public enum CollectorAddressRule
{
    /// <summary>Address = the asset's path relative to the collector root, no extension (Unity-style).</summary>
    ByPath,

    /// <summary>Address = the asset's file name without extension.</summary>
    ByFileName,

    /// <summary>Address = the asset's GUID (fully decoupled from path).</summary>
    ByGuid,

    /// <summary>Addressing disabled: the asset is only loadable by GUID, not by a friendly string.</summary>
    NoAddress,
    /// <summary>Project-relative path without extension, including the collector's directories.</summary>
    ByAssetPath,
}

/// <summary>Which assets under a collector are included.</summary>
public enum CollectorFilterRule
{
    /// <summary>Main assets only (the file itself, not its sub-assets).</summary>
    MainAssets,

    /// <summary>Main assets plus every sub-asset (e.g. sprites from an atlas, meshes from a model).</summary>
    IncludeSubAssets,

    /// <summary>Sub-assets only.</summary>
    SubAssetsOnly,
}

/// <summary>One collector: a source path + the rules that shape what it contributes.</summary>
[Serializable]
public class AssetBundleCollector
{
    /// <summary>Path relative to the project's Assets/ folder (a folder or a single file).</summary>
    public string CollectPath = "";

    /// <summary>Optional type-name filter (e.g. "XEngine.Runtime.Resources.Texture2D"). Empty = all types.</summary>
    public string AssetTypeFilter = "";

    public CollectorPackRule PackRule = CollectorPackRule.InheritGroup;
    public CollectorAddressRule AddressRule = CollectorAddressRule.ByPath;
    public CollectorFilterRule FilterRule = CollectorFilterRule.MainAssets;

    /// <summary>User tags applied to every asset this collector contributes (drives download grouping).</summary>
    public List<string> Tags = new();

    /// <summary>Free-form annotation surfaced in the collector window (consumed by user tooling, not the build).</summary>
    public string UserData = "";
}

/// <summary>A group of collectors that share a namespace and optional shared tags.</summary>
[Serializable]
public class AssetBundleCollectorGroup
{
    public string GroupName = "";
    public string GroupDescription = "";
    /// <summary>Disabled groups are skipped entirely at collect time (YooAsset group-active-rule analog).</summary>
    public bool Enabled = true;
    public CollectorPackRule PackRule = CollectorPackRule.PackSeparately;
    public List<string> Tags = new();
    public List<AssetBundleCollector> Collectors = new();
}

/// <summary>A named package - the unit the runtime loads via AssetPackage.</summary>
[Serializable]
public class AssetBundleCollectorPackage
{
    public string PackageName = "default";
    public string PackageDescription = "";
    public bool EnableAddressable = true;
    /// <summary>Lowercase every addressable address generated for this package.</summary>
    public bool LocationToLower = false;
    /// <summary>Package-wide extension deny list ("png,meta") — matching entries are never collected.</summary>
    public string IgnoreExtensions = "";
    public List<AssetBundleCollectorGroup> Groups = new();
}

/// <summary>
/// Root of the collector configuration. Persisted as a project setting ([ProjectSettings]). The
/// AssetBundleBuilder consumes this to decide which assets go in which bundles.
/// </summary>
[ProjectSettings("AssetBundles", EditorIcons.Box, order: 60, exportToBuild: false)]
public sealed class AssetBundleCollectorSetting : ProjectSettingsBase
{
    public List<AssetBundleCollectorPackage> Packages = new();

    // -- View state for the Bundle Collector window (Window/AssetBundles/Bundle Collector). --
    /// <summary>Show the package selector list in the collector window's left column.</summary>
    public bool ShowPackagesView = false;
    /// <summary>Render rule enums with display aliases instead of raw enum names.</summary>
    public bool ShowRuleAlias = true;
    /// <summary>Pixel width of the collector window's Groups column (draggable splitter).</summary>
    public float CollectorSplitWidth = 300f;

    public AssetBundleCollectorSetting()
    {
        // Default: one empty package so the UI has something to show.
        if (Packages.Count == 0)
            Packages.Add(new AssetBundleCollectorPackage { PackageName = "default" });
    }

    public override void ResetToDefaults()
    {
        Packages.Clear();
        Packages.Add(new AssetBundleCollectorPackage { PackageName = "default" });
    }

    /// <summary>Editing moved to the standalone Bundle Collector window (YooAsset-style);
    /// the settings page only routes there.</summary>
    public override void OnGUI(Paper paper, float width)
    {
        Origami.Header(paper, "ab_collector_h", "AssetBundle Collectors").Underline().Show();
        paper.Box("ab_collector_hint").Height(34)
            .Text("Collector editing lives in the dedicated Bundle Collector window " +
                  "(Window/AssetBundles/Bundle Collector).", EditorTheme.DefaultFont)
            .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSize)
            .Alignment(XEngine.PaperUI.TextAlignment.MiddleLeft);
        Origami.Button(paper, "ab_collector_open", "Open Bundle Collector", () =>
            EditorApplication.Instance?.OpenPanel(typeof(GUI.Panels.BundleCollectorPanel))).Show();
    }
}
