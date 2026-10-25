using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;

namespace XEngine.XGUI.Editor;

/// <summary>Outcome for one legacy component considered by the XGUI migration preview or upgrade.</summary>
public enum XGuiMigrationUpgradeStatus
{
    Ready,
    Upgraded,
    RequiresManualMigration,
    BlockedByDependency,
    Failed,
}

/// <summary>Value-only per-component result. It intentionally holds no package type or scene reference.</summary>
public sealed class XGuiMigrationUpgradeEntry
{
    internal XGuiMigrationUpgradeEntry(string hierarchyPath, string legacyType, string? targetType,
        XGuiMigrationUpgradeStatus status, string detail)
    {
        HierarchyPath = hierarchyPath;
        LegacyType = legacyType;
        TargetType = targetType;
        Status = status;
        Detail = detail;
    }

    public string HierarchyPath { get; }
    public string LegacyType { get; }
    public string? TargetType { get; }
    public XGuiMigrationUpgradeStatus Status { get; internal set; }
    public string Detail { get; internal set; }
    public int CopiedFieldCount { get; internal set; }
    public int RedirectedReferenceCount { get; internal set; }
    public bool IdentifierPreserved { get; internal set; }
}

/// <summary>
/// Report produced before and after an XGUI migration. Counts let tooling verify expected support
/// components (for example CanvasRenderer) separately from replacement components.
/// </summary>
public sealed class XGuiMigrationUpgradeReport
{
    private readonly XGuiMigrationUpgradeEntry[] _entries;

    internal XGuiMigrationUpgradeReport(int objectsScanned, int componentCountBefore,
        List<XGuiMigrationUpgradeEntry> entries)
    {
        ObjectsScanned = objectsScanned;
        ComponentCountBefore = componentCountBefore;
        _entries = entries.ToArray();
    }

    public int ObjectsScanned { get; }
    public int ComponentCountBefore { get; }
    public int ComponentCountAfter { get; internal set; }
    public int MigratedComponentCount { get; internal set; }
    public int CreatedSupportComponentCount { get; internal set; }
    public int PreservedComponentIdentifierCount { get; internal set; }
    public bool ComponentCountVerified { get; internal set; }
    public IReadOnlyList<XGuiMigrationUpgradeEntry> Entries => _entries;
}

/// <summary>
/// Converts safe Runtime UI compatibility components to their XGUI package implementations. The service
/// performs no file I/O; edits are scene-local, one Undo action, and retain original component GUIDs.
/// It refuses controls that require package templates (currently Dropdown) rather than creating an
/// incomplete hierarchy at runtime.
/// </summary>
public static class XGuiMigrationService
{
    private const string PackageAssemblyName = "XEngine.XGUI.Runtime";
    private const string UiNamespace = "XEngine.Runtime.UI.";
    private static readonly HashSet<string> s_skippedFieldNames = new(StringComparer.Ordinal)
    {
        "_identifier",
        "_enabled",
        "_enabledInHierarchy",
        "_instanceID",
        "_go",
        "_hasStarted",
        "_hasBeenEnabled",
        "_executeAlwaysCached",
        "_updateRegistered",
    };

    /// <summary>Builds a read-only migration preview for all enabled and disabled objects in a scene.</summary>
    public static XGuiMigrationUpgradeReport Preview(Scene? scene)
    {
        List<MigrationPlan> plans = BuildPlan(scene, out XGuiMigrationUpgradeReport report);
        _ = plans;
        return report;
    }

    /// <summary>
    /// Upgrades every ready component in one atomic scene transaction. A failed field transfer restores
    /// the old components before returning, and a successful transaction is exposed as one Undo step.
    /// </summary>
    public static XGuiMigrationUpgradeReport Upgrade(Scene? scene)
    {
        List<MigrationPlan> plans = BuildPlan(scene, out XGuiMigrationUpgradeReport report);
        if (scene == null || plans.Count == 0)
        {
            report.ComponentCountAfter = report.ComponentCountBefore;
            report.ComponentCountVerified = true;
            return report;
        }

        if (Application.IsPlaying)
        {
            MarkFailed(plans, "XGUI migration is unavailable while the scene is playing.");
            report.ComponentCountAfter = CountComponents(scene);
            return report;
        }

        var transaction = new MigrationTransaction(scene, plans);
        try
        {
            transaction.CaptureLegacySnapshots();
            transaction.CreateTargets();
            transaction.CopyFieldsAndRedirectReferences();
            transaction.ReplaceLegacyComponents();
            transaction.CapturePackageSnapshots();

            report.ComponentCountAfter = CountComponents(scene);
            report.MigratedComponentCount = plans.Count;
            report.CreatedSupportComponentCount = transaction.GeneratedSupportComponentCount;
            report.PreservedComponentIdentifierCount = transaction.PreservedIdentifierCount;
            report.ComponentCountVerified = report.ComponentCountAfter
                == report.ComponentCountBefore + report.CreatedSupportComponentCount;
            for (int i = 0; i < plans.Count; i++)
            {
                MigrationPlan plan = plans[i];
                plan.Entry.Status = XGuiMigrationUpgradeStatus.Upgraded;
                plan.Entry.Detail = "Migrated with serialized field and component-reference preservation.";
                plan.Entry.CopiedFieldCount = plan.CopiedFieldCount;
                plan.Entry.RedirectedReferenceCount = plan.RedirectedReferenceCount;
                plan.Entry.IdentifierPreserved = plan.Target?.Identifier == plan.Source.Identifier;
            }

            Undo.RegisterAction("Upgrade Legacy UI to XGUI", transaction.RestoreLegacy, transaction.RestorePackage);
            EditorSceneManager.MarkDirty();
            return report;
        }
        catch (Exception ex)
        {
            transaction.RestoreLegacy();
            MarkFailed(plans, ex.Message);
            report.ComponentCountAfter = CountComponents(scene);
            report.ComponentCountVerified = report.ComponentCountAfter == report.ComponentCountBefore;
            return report;
        }
    }

    private static List<MigrationPlan> BuildPlan(Scene? scene, out XGuiMigrationUpgradeReport report)
    {
        var entries = new List<XGuiMigrationUpgradeEntry>();
        var plans = new List<MigrationPlan>();
        int objectCount = 0;
        int componentCount = 0;
        if (scene == null)
        {
            report = new XGuiMigrationUpgradeReport(0, 0, entries);
            return plans;
        }

        foreach (GameObject gameObject in scene.AllObjects)
        {
            objectCount++;
            string path = GetHierarchyPath(gameObject);
            int index = 0;
            foreach (MonoBehaviour component in gameObject.GetComponents())
            {
                componentCount++;
                Type sourceType = component.GetType();
                if (!XGuiMigrationScanner.IsLegacyCompatibilityComponent(sourceType))
                {
                    index++;
                    continue;
                }

                Type? targetType = GetTargetType(sourceType);
                string legacyName = sourceType.FullName ?? sourceType.Name;
                if (targetType == null)
                {
                    string detail = sourceType == typeof(UIDropdown)
                        ? "Dropdown requires an XGUI template Item Template and overlay root; create the package Dropdown template and migrate its options manually."
                        : "This compatibility component has no standalone XGUI replacement target.";
                    entries.Add(new XGuiMigrationUpgradeEntry(path, legacyName, null,
                        XGuiMigrationUpgradeStatus.RequiresManualMigration, detail));
                    index++;
                    continue;
                }

                string? dependent = FindBlockingDependency(gameObject, component);
                if (dependent != null)
                {
                    entries.Add(new XGuiMigrationUpgradeEntry(path, legacyName, targetType.FullName,
                        XGuiMigrationUpgradeStatus.BlockedByDependency,
                        "Required by " + dependent + ". Remove or update that dependency before migration."));
                    index++;
                    continue;
                }

                var entry = new XGuiMigrationUpgradeEntry(path, legacyName, targetType.FullName,
                    XGuiMigrationUpgradeStatus.Ready, "Ready for a reversible XGUI replacement.");
                entries.Add(entry);
                plans.Add(new MigrationPlan(gameObject, component, targetType, index, entry));
                index++;
            }
        }

        report = new XGuiMigrationUpgradeReport(objectCount, componentCount, entries);
        return plans;
    }

    private static Type? GetTargetType(Type sourceType)
    {
        if (!ReferenceEquals(sourceType.Assembly, typeof(GameCanvas).Assembly)) return null;
        return sourceType.Name switch
        {
            nameof(UIImage) => typeof(Image),
            nameof(TextComponent) => typeof(Text),
            nameof(UIButton) => typeof(Button),
            nameof(UISlider) => typeof(Slider),
            nameof(UIScrollbar) => typeof(Scrollbar),
            nameof(UIScrollRect) => typeof(ScrollRect),
            nameof(UIInputField) => typeof(InputField),
            nameof(RectMask) => typeof(RectMask2D),
            nameof(UILayoutElement) => GetPackageType("LayoutElement"),
            nameof(UIHorizontalLayoutGroup) => GetPackageType("HorizontalLayoutGroup"),
            nameof(UIVerticalLayoutGroup) => GetPackageType("VerticalLayoutGroup"),
            nameof(UIGridLayoutGroup) => GetPackageType("GridLayoutGroup"),
            nameof(UIContentSizeFitter) => GetPackageType("ContentSizeFitter"),
            _ => null,
        };
    }

    private static Type GetPackageType(string typeName)
        => typeof(Button).Assembly.GetType(UiNamespace + typeName)
            ?? throw new InvalidOperationException("XGUI Runtime does not export " + UiNamespace + typeName + ".");

    private static string? FindBlockingDependency(GameObject gameObject, MonoBehaviour source)
    {
        Type sourceType = source.GetType();
        foreach (MonoBehaviour component in gameObject.GetComponents())
        {
            if (ReferenceEquals(component, source)) continue;
            RequireComponentAttribute? requirement = component.GetType().GetCustomAttribute<RequireComponentAttribute>();
            if (requirement == null) continue;
            Type[] requiredTypes = requirement.types;
            for (int i = 0; i < requiredTypes.Length; i++)
            {
                if (requiredTypes[i] == sourceType)
                    return component.GetType().FullName ?? component.GetType().Name;
            }
        }
        return null;
    }

    private static void MarkFailed(List<MigrationPlan> plans, string detail)
    {
        for (int i = 0; i < plans.Count; i++)
        {
            MigrationPlan plan = plans[i];
            plan.Entry.Status = XGuiMigrationUpgradeStatus.Failed;
            plan.Entry.Detail = detail;
        }
    }

    private static int CountComponents(Scene scene)
    {
        int count = 0;
        foreach (GameObject gameObject in scene.AllObjects)
            foreach (MonoBehaviour _ in gameObject.GetComponents())
                count++;
        return count;
    }

    private static string GetHierarchyPath(GameObject gameObject)
    {
        var segments = new Stack<string>();
        for (GameObject? current = gameObject; current != null; current = current.Parent)
            segments.Push(current.Name);
        return string.Join('/', segments);
    }

    private sealed class MigrationPlan
    {
        public MigrationPlan(GameObject gameObject, MonoBehaviour source, Type targetType, int sourceIndex,
            XGuiMigrationUpgradeEntry entry)
        {
            GameObject = gameObject;
            Source = source;
            TargetType = targetType;
            SourceIndex = sourceIndex;
            Entry = entry;
        }

        public GameObject GameObject { get; }
        public MonoBehaviour Source { get; }
        public Type TargetType { get; }
        public int SourceIndex { get; }
        public XGuiMigrationUpgradeEntry Entry { get; }
        public MonoBehaviour? Target { get; set; }
        public ComponentSnapshot? Snapshot { get; set; }
        public int CopiedFieldCount { get; set; }
        public int RedirectedReferenceCount { get; set; }
    }

    private sealed class ComponentSnapshot
    {
        public required Guid GameObjectIdentifier { get; init; }
        public required Guid ComponentIdentifier { get; init; }
        public required Type LegacyType { get; init; }
        public required Type PackageType { get; init; }
        public required int SiblingIndex { get; init; }
        public required bool Enabled { get; init; }
        public required EchoObject LegacyData { get; init; }
        public EchoObject? PackageData { get; set; }
    }

    private sealed class MigrationTransaction
    {
        private readonly Scene _scene;
        private readonly List<MigrationPlan> _plans;
        private readonly Dictionary<MonoBehaviour, MonoBehaviour> _replacements = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, object> _nestedCopies = new(ReferenceEqualityComparer.Instance);
        private readonly List<ComponentSnapshot> _snapshots = new();
        private readonly Dictionary<GameObject, HashSet<Guid>> _originalComponentIdentifiers = new(ReferenceEqualityComparer.Instance);
        private readonly List<Guid> _generatedSupportComponentIdentifiers = new();

        public MigrationTransaction(Scene scene, List<MigrationPlan> plans)
        {
            _scene = scene;
            _plans = plans;
            for (int i = 0; i < plans.Count; i++)
            {
                GameObject gameObject = plans[i].GameObject;
                if (_originalComponentIdentifiers.ContainsKey(gameObject)) continue;
                var identifiers = new HashSet<Guid>();
                foreach (MonoBehaviour component in gameObject.GetComponents()) identifiers.Add(component.Identifier);
                _originalComponentIdentifiers.Add(gameObject, identifiers);
            }
        }

        public int GeneratedSupportComponentCount => _generatedSupportComponentIdentifiers.Count;
        public int PreservedIdentifierCount { get; private set; }

        public void CaptureLegacySnapshots()
        {
            for (int i = 0; i < _plans.Count; i++)
            {
                MigrationPlan plan = _plans[i];
                var snapshot = new ComponentSnapshot
                {
                    GameObjectIdentifier = plan.GameObject.Identifier,
                    ComponentIdentifier = plan.Source.Identifier,
                    LegacyType = plan.Source.GetType(),
                    PackageType = plan.TargetType,
                    SiblingIndex = plan.SourceIndex,
                    Enabled = plan.Source.Enabled,
                    LegacyData = SerializeComponent(plan.Source),
                };
                plan.Snapshot = snapshot;
                _snapshots.Add(snapshot);
            }
        }

        public void CreateTargets()
        {
            for (int i = 0; i < _plans.Count; i++)
            {
                MigrationPlan plan = _plans[i];
                MonoBehaviour? target = plan.GameObject.AddComponent(plan.TargetType);
                if (target == null)
                    throw new InvalidOperationException("Could not create XGUI component " + plan.TargetType.FullName + ".");
                target.Enabled = false;
                plan.Target = target;
                _replacements.Add(plan.Source, target);
            }

            foreach ((GameObject gameObject, HashSet<Guid> originalIdentifiers) in _originalComponentIdentifiers)
            {
                foreach (MonoBehaviour component in gameObject.GetComponents())
                {
                    if (originalIdentifiers.Contains(component.Identifier)) continue;
                    bool isTarget = false;
                    for (int i = 0; i < _plans.Count; i++)
                    {
                        if (ReferenceEquals(_plans[i].Target, component))
                        {
                            isTarget = true;
                            break;
                        }
                    }
                    if (!isTarget) _generatedSupportComponentIdentifiers.Add(component.Identifier);
                }
            }
        }

        public void CopyFieldsAndRedirectReferences()
        {
            var copyContext = new CopyContext(_replacements, _nestedCopies);
            for (int i = 0; i < _plans.Count; i++)
            {
                MigrationPlan plan = _plans[i];
                if (plan.Target == null) throw new InvalidOperationException("XGUI target was not created.");
                CopyComponentFields(plan.Source, plan.Target, copyContext, plan.Entry.HierarchyPath);
                RedirectSelectableGraphic(plan.Source, plan.Target, _replacements, plan.Entry.HierarchyPath);
                plan.CopiedFieldCount = copyContext.CopiedFieldCount - plan.CopiedFieldCount;
                plan.RedirectedReferenceCount = copyContext.RedirectedReferenceCount - plan.RedirectedReferenceCount;
                plan.Target.Identifier = plan.Source.Identifier;
            }
        }

        public void ReplaceLegacyComponents()
        {
            for (int i = 0; i < _plans.Count; i++)
            {
                MigrationPlan plan = _plans[i];
                plan.GameObject.RemoveComponent(plan.Source);
                if (plan.GameObject.GetComponentByIdentifier(plan.Source.Identifier) is not { } current
                    || !ReferenceEquals(current, plan.Target))
                {
                    throw new InvalidOperationException("Could not remove legacy component " + plan.Entry.LegacyType + ".");
                }
            }

            for (int i = 0; i < _plans.Count; i++)
            {
                MigrationPlan plan = _plans[i];
                MonoBehaviour target = plan.Target!;
                target.SetSiblingIndex(plan.SourceIndex);
                target.OnValidate();
                target.Enabled = plan.Snapshot!.Enabled;
                if (target.Identifier == plan.Snapshot.ComponentIdentifier) PreservedIdentifierCount++;
            }
        }

        public void CapturePackageSnapshots()
        {
            for (int i = 0; i < _plans.Count; i++)
            {
                MigrationPlan plan = _plans[i];
                plan.Snapshot!.PackageData = SerializeComponent(plan.Target!);
            }
        }

        public void RestoreLegacy()
        {
            RemoveByIdentifier(GetPackageComponentIdentifiers());
            RemoveByIdentifier(_generatedSupportComponentIdentifiers);
            RestoreSnapshots(package: false);
            EditorSceneManager.MarkDirty();
        }

        public void RestorePackage()
        {
            RemoveByIdentifier(GetLegacyComponentIdentifiers());
            RestoreSnapshots(package: true);
            EditorSceneManager.MarkDirty();
        }

        private List<Guid> GetPackageComponentIdentifiers()
        {
            var identifiers = new List<Guid>(_snapshots.Count);
            for (int i = 0; i < _snapshots.Count; i++) identifiers.Add(_snapshots[i].ComponentIdentifier);
            return identifiers;
        }

        private List<Guid> GetLegacyComponentIdentifiers()
        {
            var identifiers = new List<Guid>(_snapshots.Count);
            for (int i = 0; i < _snapshots.Count; i++) identifiers.Add(_snapshots[i].ComponentIdentifier);
            return identifiers;
        }

        private void RemoveByIdentifier(IReadOnlyList<Guid> identifiers)
        {
            for (int i = 0; i < identifiers.Count; i++)
            {
                MonoBehaviour? component = FindComponent(identifiers[i]);
                component?.GameObject.RemoveComponent(component);
            }
        }

        private void RestoreSnapshots(bool package)
        {
            for (int i = 0; i < _snapshots.Count; i++)
            {
                ComponentSnapshot snapshot = _snapshots[i];
                GameObject gameObject = FindGameObject(snapshot.GameObjectIdentifier)
                    ?? throw new InvalidOperationException("Migration target GameObject no longer exists.");
                MonoBehaviour? component = gameObject.GetComponentByIdentifier(snapshot.ComponentIdentifier);
                if (component == null)
                {
                    component = gameObject.AddComponent(package ? snapshot.PackageType : snapshot.LegacyType);
                    if (component == null) throw new InvalidOperationException("Could not restore migrated UI component.");
                    component.Enabled = false;
                    component.Identifier = snapshot.ComponentIdentifier;
                }
                component.SetSiblingIndex(snapshot.SiblingIndex);
            }

            for (int i = 0; i < _snapshots.Count; i++)
            {
                ComponentSnapshot snapshot = _snapshots[i];
                MonoBehaviour component = FindComponent(snapshot.ComponentIdentifier)
                    ?? throw new InvalidOperationException("Restored XGUI component is unavailable.");
                EchoObject data = package
                    ? snapshot.PackageData ?? throw new InvalidOperationException("Package migration snapshot is unavailable.")
                    : snapshot.LegacyData;
                var context = new SerializationContext { ExternalReferences = new SceneReferenceResolver(_scene, component) };
                Serializer.DeserializeInto(data, component, context);
                ForceEnabledState(component);
                component.OnValidate();
            }
        }

        private EchoObject SerializeComponent(MonoBehaviour component)
        {
            var context = new SerializationContext
            {
                ExternalReferences = new SceneReferenceResolver(_scene, component),
            };
            return Serializer.Serialize(component.GetType(), component, context);
        }

        private GameObject? FindGameObject(Guid identifier)
        {
            foreach (GameObject gameObject in _scene.AllObjects)
                if (gameObject.Identifier == identifier) return gameObject;
            return null;
        }

        private MonoBehaviour? FindComponent(Guid identifier)
        {
            foreach (GameObject gameObject in _scene.AllObjects)
            {
                MonoBehaviour? component = gameObject.GetComponentByIdentifier(identifier);
                if (component != null) return component;
            }
            return null;
        }
    }

    private sealed class SceneReferenceResolver : IExternalReferenceResolver
    {
        private readonly Scene _scene;
        private readonly object _root;

        public SceneReferenceResolver(Scene scene, object root)
        {
            _scene = scene;
            _root = root;
        }

        public object? GetReferenceKey(object value)
        {
            if (ReferenceEquals(value, _root)) return null;
            return value switch
            {
                GameObject gameObject => "g:" + gameObject.Identifier.ToString("N"),
                MonoBehaviour component => "c:" + component.Identifier.ToString("N"),
                _ => null,
            };
        }

        public object? ResolveReference(object key, Type targetType)
        {
            if (key is not string value || value.Length < 3) return null;
            if (!Guid.TryParse(value[2..], out Guid identifier)) return null;
            if (value.StartsWith("g:", StringComparison.Ordinal))
            {
                foreach (GameObject gameObject in _scene.AllObjects)
                    if (gameObject.Identifier == identifier && targetType.IsInstanceOfType(gameObject)) return gameObject;
                return null;
            }

            if (!value.StartsWith("c:", StringComparison.Ordinal)) return null;
            foreach (GameObject gameObject in _scene.AllObjects)
            {
                MonoBehaviour? component = gameObject.GetComponentByIdentifier(identifier);
                if (component != null && targetType.IsInstanceOfType(component)) return component;
            }
            return null;
        }
    }

    private sealed class CopyContext
    {
        public CopyContext(Dictionary<MonoBehaviour, MonoBehaviour> replacements, Dictionary<object, object> nestedCopies)
        {
            Replacements = replacements;
            NestedCopies = nestedCopies;
        }

        public Dictionary<MonoBehaviour, MonoBehaviour> Replacements { get; }
        public Dictionary<object, object> NestedCopies { get; }
        public int CopiedFieldCount { get; set; }
        public int RedirectedReferenceCount { get; set; }
    }

    private static void CopyComponentFields(MonoBehaviour source, MonoBehaviour target, CopyContext context, string path)
    {
        foreach (FieldInfo sourceField in GetSerializableFields(source.GetType()))
        {
            if (s_skippedFieldNames.Contains(sourceField.Name)) continue;
            if (source is Selectable && target is Selectable && sourceField.Name == "_targetGraphic") continue;

            FieldInfo? targetField = FindSerializableField(target.GetType(), sourceField.Name);
            if (targetField == null) continue;
            object? sourceValue = sourceField.GetValue(source);
            if (!TryCopyValue(sourceValue, targetField.FieldType, context, out object? targetValue))
            {
                throw new InvalidOperationException("Cannot preserve " + path + "."
                    + source.GetType().Name + "." + sourceField.Name + ".");
            }
            targetField.SetValue(target, targetValue);
            context.CopiedFieldCount++;
        }
    }

    private static void RedirectSelectableGraphic(MonoBehaviour source, MonoBehaviour target,
        Dictionary<MonoBehaviour, MonoBehaviour> replacements, string path)
    {
        if (source is not Selectable sourceSelectable || target is not Selectable targetSelectable) return;
        UIImage? legacyGraphic = sourceSelectable.TargetGraphic;
        if (legacyGraphic == null) return;
        if (!replacements.TryGetValue(legacyGraphic, out MonoBehaviour? replacement)
            || replacement is not ISelectableGraphic)
        {
            throw new InvalidOperationException("Cannot preserve selectable graphic reference at " + path + ".");
        }
        targetSelectable.TargetGraphicComponent = replacement;
    }

    private static bool TryCopyValue(object? sourceValue, Type targetType, CopyContext context, out object? targetValue)
    {
        if (sourceValue == null)
        {
            targetValue = null;
            return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null;
        }

        Type sourceType = sourceValue.GetType();
        Type? nullableUnderlying = Nullable.GetUnderlyingType(targetType);
        if (nullableUnderlying != null) targetType = nullableUnderlying;

        if (sourceValue is MonoBehaviour sourceComponent)
        {
            if (context.Replacements.TryGetValue(sourceComponent, out MonoBehaviour? replacement))
            {
                if (!targetType.IsInstanceOfType(replacement))
                {
                    targetValue = null;
                    return false;
                }
                targetValue = replacement;
                context.RedirectedReferenceCount++;
                return true;
            }
            if (targetType.IsInstanceOfType(sourceComponent))
            {
                targetValue = sourceComponent;
                return true;
            }
            targetValue = null;
            return false;
        }

        if (sourceValue is EngineObject engineObject)
        {
            if (targetType.IsInstanceOfType(engineObject))
            {
                targetValue = engineObject;
                return true;
            }
            targetValue = null;
            return false;
        }

        if (sourceType.IsEnum && targetType.IsEnum)
        {
            targetValue = Enum.ToObject(targetType, Convert.ToInt64(sourceValue));
            return true;
        }

        if (targetType.IsInstanceOfType(sourceValue) && (sourceType.IsValueType || sourceValue is string))
        {
            targetValue = sourceValue;
            return true;
        }

        if (sourceValue is Array sourceArray && targetType.IsArray && targetType.GetElementType() is { } targetElementType)
        {
            Array arrayCopy = Array.CreateInstance(targetElementType, sourceArray.Length);
            for (int i = 0; i < sourceArray.Length; i++)
            {
                if (!TryCopyValue(sourceArray.GetValue(i), targetElementType, context, out object? element))
                {
                    targetValue = null;
                    return false;
                }
                arrayCopy.SetValue(element, i);
            }
            targetValue = arrayCopy;
            return true;
        }

        if (sourceValue is IList sourceList && typeof(IList).IsAssignableFrom(targetType))
        {
            if (Activator.CreateInstance(targetType) is not IList targetList)
            {
                targetValue = null;
                return false;
            }
            Type elementType = targetType.IsGenericType ? targetType.GetGenericArguments()[0] : typeof(object);
            for (int i = 0; i < sourceList.Count; i++)
            {
                if (!TryCopyValue(sourceList[i], elementType, context, out object? element))
                {
                    targetValue = null;
                    return false;
                }
                targetList.Add(element);
            }
            targetValue = targetList;
            return true;
        }

        if (targetType.IsInstanceOfType(sourceValue) && sourceType.IsValueType)
        {
            targetValue = sourceValue;
            return true;
        }

        if (!targetType.IsAssignableFrom(sourceType) || targetType.IsAbstract || targetType.IsInterface)
        {
            targetValue = null;
            return false;
        }

        if (context.NestedCopies.TryGetValue(sourceValue, out object? knownCopy))
        {
            targetValue = knownCopy;
            return true;
        }

        object? copy;
        try { copy = Activator.CreateInstance(targetType, nonPublic: true); }
        catch { copy = null; }
        if (copy == null)
        {
            targetValue = null;
            return false;
        }

        context.NestedCopies.Add(sourceValue, copy);
        foreach (FieldInfo sourceField in GetSerializableFields(sourceType))
        {
            if (s_skippedFieldNames.Contains(sourceField.Name)) continue;
            FieldInfo? targetField = FindSerializableField(targetType, sourceField.Name);
            if (targetField == null) continue;
            if (!TryCopyValue(sourceField.GetValue(sourceValue), targetField.FieldType, context, out object? nestedValue))
            {
                targetValue = null;
                return false;
            }
            targetField.SetValue(copy, nestedValue);
        }
        targetValue = copy;
        return true;
    }

    private static IEnumerable<FieldInfo> GetSerializableFields(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(flags))
            {
                if (!field.IsPublic && field.GetCustomAttribute<SerializeFieldAttribute>() == null) continue;
                if (field.GetCustomAttribute<SerializeIgnoreAttribute>() != null
                    || field.GetCustomAttribute<NonSerializedAttribute>() != null) continue;
                yield return field;
            }
        }
    }

    private static FieldInfo? FindSerializableField(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, flags);
            if (field == null) continue;
            if (!field.IsPublic && field.GetCustomAttribute<SerializeFieldAttribute>() == null) continue;
            if (field.GetCustomAttribute<SerializeIgnoreAttribute>() != null
                || field.GetCustomAttribute<NonSerializedAttribute>() != null) continue;
            return field;
        }
        return null;
    }

    private static void ForceEnabledState(MonoBehaviour component)
    {
        bool desired = component.Enabled;
        component.Enabled = !desired;
        component.Enabled = desired;
    }
}
