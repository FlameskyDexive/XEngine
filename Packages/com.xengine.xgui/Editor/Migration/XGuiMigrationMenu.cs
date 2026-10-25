using XEngine.Editor.Core;
using XEngine.Editor;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;

namespace XEngine.XGUI.Editor;

/// <summary>Read-only compatibility scan. It never rewrites Echo data or silently drops components.</summary>
internal static class XGuiMigrationMenu
{
    [MenuItem("Tools/XGUI/Scan Legacy UI", priority: 80, Icon = XEngine.Editor.Theming.EditorIcons.MagnifyingGlass)]
    private static void ScanLegacyUi()
    {
        Scene? scene = Scene.Current;
        if (scene == null)
        {
            Debug.LogWarning("[XGUI Migration] No scene is loaded.");
            return;
        }

        XGuiMigrationReport report = XGuiMigrationScanner.Scan(scene);
        Debug.Log("[XGUI Migration] Scan complete: objects=" + report.ObjectsScanned
            + ", package components=" + report.PackageComponentCount
            + ", legacy-compatible components=" + report.LegacyCompatibilityCount
            + ", missing XGUI components=" + report.MissingXGuiComponentCount
            + ". No files changed.");

        for (int i = 0; i < report.Findings.Count; i++)
        {
            XGuiMigrationFinding finding = report.Findings[i];
            if (finding.Kind != XGuiMigrationFindingKind.MissingXGuiComponent) continue;
            Debug.LogWarning("[XGUI Migration] Missing XGUI component at '" + finding.HierarchyPath
                + "': " + finding.SerializedType);
        }
    }

    [MenuItem("Tools/XGUI/Upgrade Legacy UI in Scene", priority: 81, Icon = XEngine.Editor.Theming.EditorIcons.ArrowsRotate)]
    private static void UpgradeLegacyUi()
    {
        Scene? scene = Scene.Current;
        if (scene == null)
        {
            Debug.LogWarning("[XGUI Migration] No scene is loaded.");
            return;
        }

        XGuiMigrationUpgradeReport report = XGuiMigrationService.Upgrade(scene);
        Debug.Log("[XGUI Migration] Upgrade complete: migrated=" + report.MigratedComponentCount
            + ", support components=" + report.CreatedSupportComponentCount
            + ", preserved component GUIDs=" + report.PreservedComponentIdentifierCount
            + ", component count=" + report.ComponentCountBefore + " -> " + report.ComponentCountAfter
            + ", count verified=" + report.ComponentCountVerified + ".");

        for (int i = 0; i < report.Entries.Count; i++)
        {
            XGuiMigrationUpgradeEntry entry = report.Entries[i];
            if (entry.Status is XGuiMigrationUpgradeStatus.Upgraded or XGuiMigrationUpgradeStatus.Ready) continue;
            Debug.LogWarning("[XGUI Migration] " + entry.Status + " at '" + entry.HierarchyPath
                + "' for " + entry.LegacyType + ": " + entry.Detail);
        }
    }
}
