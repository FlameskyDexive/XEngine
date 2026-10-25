using System;
using System.Collections.Generic;
using System.Text;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;

namespace XEngine.XGUI.Editor;

/// <summary>Classifies UI components found by the read-only XGUI migration scan.</summary>
public enum XGuiMigrationFindingKind
{
    PackageComponent,
    LegacyCompatibilityComponent,
    MissingXGuiComponent,
}

/// <summary>A value-only migration finding that does not retain a scene or a package component instance.</summary>
public sealed class XGuiMigrationFinding
{
    internal XGuiMigrationFinding(XGuiMigrationFindingKind kind, string hierarchyPath,
        string componentType, string? serializedType)
    {
        Kind = kind;
        HierarchyPath = hierarchyPath;
        ComponentType = componentType;
        SerializedType = serializedType;
    }

    public XGuiMigrationFindingKind Kind { get; }
    public string HierarchyPath { get; }
    public string ComponentType { get; }
    public string? SerializedType { get; }
}

/// <summary>Immutable result of a read-only XGUI compatibility scan.</summary>
public sealed class XGuiMigrationReport
{
    private readonly XGuiMigrationFinding[] _findings;

    internal XGuiMigrationReport(int objectsScanned, List<XGuiMigrationFinding> findings)
    {
        ObjectsScanned = objectsScanned;
        _findings = findings.ToArray();
        for (int i = 0; i < _findings.Length; i++)
        {
            switch (_findings[i].Kind)
            {
                case XGuiMigrationFindingKind.PackageComponent:
                    PackageComponentCount++;
                    break;
                case XGuiMigrationFindingKind.LegacyCompatibilityComponent:
                    LegacyCompatibilityCount++;
                    break;
                case XGuiMigrationFindingKind.MissingXGuiComponent:
                    MissingXGuiComponentCount++;
                    break;
            }
        }
    }

    public int ObjectsScanned { get; }
    public int PackageComponentCount { get; }
    public int LegacyCompatibilityCount { get; }
    public int MissingXGuiComponentCount { get; }
    public IReadOnlyList<XGuiMigrationFinding> Findings => _findings;
}

/// <summary>
/// Scans the loaded scene without changing Echo data, components, or package registrations. Results hold
/// only strings and enum values so an editor diagnostics window cannot pin an unloadable script assembly.
/// </summary>
public static class XGuiMigrationScanner
{
    private const string UiNamespace = "XEngine.Runtime.UI.";
    private const string PackageAssembly = "XEngine.XGUI.Runtime";

    /// <summary>Scans every scene object, including disabled objects that would be skipped by runtime traversal.</summary>
    public static XGuiMigrationReport Scan(Scene? scene)
    {
        var findings = new List<XGuiMigrationFinding>();
        if (scene == null)
            return new XGuiMigrationReport(0, findings);

        int objectCount = 0;
        foreach (GameObject gameObject in scene.AllObjects)
        {
            objectCount++;
            string path = GetHierarchyPath(gameObject);
            foreach (MonoBehaviour component in gameObject.GetComponents())
            {
                if (component is MissingMonobehaviour missing)
                {
                    string? serializedType = GetSerializedType(missing.ComponentData);
                    if (IsXGuiSerializedType(serializedType))
                    {
                        findings.Add(new XGuiMigrationFinding(XGuiMigrationFindingKind.MissingXGuiComponent,
                            path, nameof(MissingMonobehaviour), serializedType));
                    }
                    continue;
                }

                Type componentType = component.GetType();
                if (IsPackageComponent(componentType))
                {
                    findings.Add(new XGuiMigrationFinding(XGuiMigrationFindingKind.PackageComponent,
                        path, componentType.FullName ?? componentType.Name, null));
                }
                else if (IsLegacyCompatibilityComponent(componentType))
                {
                    findings.Add(new XGuiMigrationFinding(XGuiMigrationFindingKind.LegacyCompatibilityComponent,
                        path, componentType.FullName ?? componentType.Name, null));
                }
            }
        }

        return new XGuiMigrationReport(objectCount, findings);
    }

    internal static bool IsLegacyCompatibilityComponent(Type type)
    {
        if (!ReferenceEquals(type.Assembly, typeof(GameCanvas).Assembly)) return false;
        return type.Name is "UIImage"
            or "TextComponent"
            or "UIButton"
            or "UISlider"
            or "UIScrollbar"
            or "UIScrollRect"
            or "UIInputField"
            or "UIDropdown"
            or "Selectable"
            or "RectMask"
            or "UILayoutElement"
            or "UILayoutGroup"
            or "UIHorizontalOrVerticalLayoutGroup"
            or "UIHorizontalLayoutGroup"
            or "UIVerticalLayoutGroup"
            or "UIGridLayoutGroup"
            or "UIContentSizeFitter";
    }

    private static bool IsPackageComponent(Type type)
        => string.Equals(type.Assembly.GetName().Name, PackageAssembly, StringComparison.Ordinal);

    private static bool IsXGuiSerializedType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)
            || !typeName.StartsWith(UiNamespace, StringComparison.Ordinal))
        {
            return false;
        }

        if (typeName.Contains(PackageAssembly, StringComparison.Ordinal)) return true;

        int comma = typeName.IndexOf(',');
        string simpleName = (comma < 0 ? typeName : typeName[..comma]);
        int dot = simpleName.LastIndexOf('.');
        return dot >= 0 && IsLegacyCompatibilityTypeName(simpleName[(dot + 1)..]);
    }

    // Serialized $type strings, not live types: this list must keep matching data written before the
    // Runtime layout classes took their UI prefix, so both spellings appear.
    private static bool IsLegacyCompatibilityTypeName(string typeName)
        => typeName is "UIImage"
            or "TextComponent"
            or "UIButton"
            or "UISlider"
            or "UIScrollbar"
            or "UIScrollRect"
            or "UIInputField"
            or "UIDropdown"
            or "RectMask"
            or "UILayoutElement"
            or "UILayoutGroup"
            or "UIHorizontalOrVerticalLayoutGroup"
            or "UIHorizontalLayoutGroup"
            or "UIVerticalLayoutGroup"
            or "UIGridLayoutGroup"
            or "UIContentSizeFitter"
            or "LayoutElement"
            or "LayoutGroup"
            or "HorizontalOrVerticalLayoutGroup"
            or "HorizontalLayoutGroup"
            or "VerticalLayoutGroup"
            or "GridLayoutGroup"
            or "ContentSizeFitter";

    private static string? GetSerializedType(EchoObject? componentData)
    {
        if (componentData == null || componentData.TagType != EchoType.Compound
            || !componentData.TryGet("$type", out EchoObject? typeTag)
            || string.IsNullOrWhiteSpace(typeTag?.StringValue))
        {
            return null;
        }

        return typeTag.StringValue;
    }

    private static string GetHierarchyPath(GameObject gameObject)
    {
        var result = new StringBuilder();
        AppendHierarchyPath(result, gameObject);
        return result.ToString();
    }

    private static void AppendHierarchyPath(StringBuilder result, GameObject gameObject)
    {
        if (gameObject.Parent != null)
        {
            AppendHierarchyPath(result, gameObject.Parent);
            result.Append('/');
        }
        result.Append(gameObject.Name);
    }
}
