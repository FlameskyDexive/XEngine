using System;
using System.Collections.Generic;
using System.IO;

using Projects = XEngine.Editor.Projects;
using XEngine.Editor.Projects.Scripting;
using XEngine.NodePlay;
using XEngine.OrigamiUI;
using XEngine.Runtime;

namespace XEngine.NodePlay.Editor;

/// <summary>One browsable node/task type: display metadata from the NP* attributes.</summary>
public sealed class NPNodeTypeInfo
{
    public required Type Type;
    public string Name = "";
    public string Category = "";
    public string Description = "";
    public bool Favorite;
}

/// <summary>
/// The add-node browser: category submenus over every NPNode derived type visible to the script
/// assemblies, favourites and recent picks persisted per project (Library/nodeplay-prefs.txt),
/// and the picked type's [NPDescription] surfaced as a toast — inline documentation, M12 style.
/// </summary>
internal static class NodePlayBrowser
{
    private const string PrefsFile = "nodeplay-prefs.txt";

    private static List<NPNodeTypeInfo>? _types;
    private static readonly HashSet<string> Favorites = new(StringComparer.Ordinal);
    private static readonly List<string> Recent = new();
    private static readonly List<string> Preferred = new();

    /// <summary>Preferred type full names (the type-configurer list), most relevant first.</summary>
    public static IReadOnlyList<string> PreferredTypes => Preferred;

    public static bool IsPreferred(string fullName) => Preferred.Contains(fullName);

    public static void TogglePreferred(string fullName)
    {
        EnsureLoaded();
        if (!Preferred.Remove(fullName))
            Preferred.Add(fullName);
        SavePrefs();
    }

    public static IReadOnlyList<NPNodeTypeInfo> Types => TypesFor(null);

    /// <summary>All node types (optionally restricted to a module base), cached per session.</summary>
    public static IReadOnlyList<NPNodeTypeInfo> TypesFor(Type? baseType)
    {
        EnsureLoaded();
        if (baseType == null)
            return _types!;
        var filtered = new List<NPNodeTypeInfo>();
        foreach (NPNodeTypeInfo info in _types!)
        {
            if (baseType.IsAssignableFrom(info.Type))
                filtered.Add(info);
        }

        return filtered;
    }

    public static void ToggleFavorite(string typeName)
    {
        EnsureLoaded();
        if (!Favorites.Add(typeName))
            Favorites.Remove(typeName);
        SavePrefs();
    }

    public static bool IsFavorite(string typeName) => Favorites.Contains(typeName);

    /// <summary>Open the pick menu; <paramref name="canUse"/> filters (module base type), and the
    /// picked type is instantiated through <paramref name="onPick"/> by the caller.</summary>
    public static void Show(Type? baseType, Action<Type> onPick)
    {
        EnsureLoaded();
        ContextMenu.ShowAtPointer(build =>
        {
            foreach (string pref in Preferred)
            {
                NPNodeTypeInfo? info = Find(pref);
                if (info == null || (baseType != null && !baseType.IsAssignableFrom(info.Type)))
                    continue;
                build.Item("◆ " + info.Name, () => Pick(info, onPick));
            }

            if (Preferred.Count > 0)
                build.Separator();

            foreach (string fav in Favorites)
            {
                NPNodeTypeInfo? info = Find(fav);
                if (info == null || (baseType != null && !baseType.IsAssignableFrom(info.Type)))
                    continue;
                build.Item("★ " + info.Name, () => Pick(info, onPick));
            }

            if (Favorites.Count > 0)
                build.Separator();

            var byCategory = new SortedDictionary<string, List<NPNodeTypeInfo>>(StringComparer.Ordinal);
            foreach (NPNodeTypeInfo info in _types!)
            {
                if (baseType != null && !baseType.IsAssignableFrom(info.Type))
                    continue;
                if (!byCategory.TryGetValue(info.Category, out List<NPNodeTypeInfo>? list))
                    byCategory[info.Category] = list = new List<NPNodeTypeInfo>();
                list.Add(info);
            }

            foreach (KeyValuePair<string, List<NPNodeTypeInfo>> kv in byCategory)
            {
                string category = kv.Key;
                build.Submenu(category, sub =>
                {
                    foreach (NPNodeTypeInfo info in kv.Value)
                    {
                        string mark = Favorites.Contains(info.Type.FullName!) ? " ★"
                            : Preferred.Contains(info.Type.FullName!) ? " ◆" : "";
                        NPNodeTypeInfo captured = info;
                        sub.Item(info.Name + mark, () =>
                        {
                            // click = pick; shift-style preference toggle lives in the configurer
                            Pick(captured, onPick);
                        });
                    }
                });
            }

            if (Recent.Count > 0)
            {
                build.Separator();
                build.Submenu("Recent", sub =>
                {
                    for (int i = Recent.Count - 1; i >= 0; i--)
                    {
                        NPNodeTypeInfo? info = Find(Recent[i]);
                        if (info != null)
                            sub.Item(info.Name, () => Pick(info, onPick));
                    }
                });
            }
        });
    }

    private static void Pick(NPNodeTypeInfo info, Action<Type> onPick)
    {
        Recent.Remove(info.Type.FullName!);
        Recent.Add(info.Type.FullName!);
        while (Recent.Count > 6)
            Recent.RemoveAt(0);
        SavePrefs();

        onPick(info.Type);
        if (info.Description.Length > 0)
            Toasts.Info(info.Name, info.Description);
    }

    private static NPNodeTypeInfo? Find(string fullName)
    {
        foreach (NPNodeTypeInfo info in _types!)
        {
            if (info.Type.FullName == fullName)
                return info;
        }

        return null;
    }

    // ---- discovery & persistence -------------------------------------------------------

    private static void EnsureLoaded()
    {
        if (_types != null)
            return;

        var seen = new HashSet<Type>();
        var list = new List<NPNodeTypeInfo>();

        foreach (System.Reflection.Assembly assembly in EnumerateScriptAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (Type type in types)
            {
                if (type.IsAbstract || !typeof(NPNode).IsAssignableFrom(type) || !seen.Add(type))
                    continue;

                var name = (NPNameAttribute?)Attribute.GetCustomAttribute(type, typeof(NPNameAttribute));
                var category = (NPCategoryAttribute?)Attribute.GetCustomAttribute(type, typeof(NPCategoryAttribute));
                var description = (NPDescriptionAttribute?)Attribute.GetCustomAttribute(type, typeof(NPDescriptionAttribute));
                list.Add(new NPNodeTypeInfo
                {
                    Type = type,
                    Name = name?.Name ?? NPTask.SplitCamelCase(type.Name),
                    Category = category?.Category ?? "Misc",
                    Description = description?.Text ?? "",
                    Favorite = false,
                });
            }
        }

        list.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Category, b.Category);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        _types = list;
        LoadPrefs();
    }

    private static IEnumerable<System.Reflection.Assembly> EnumerateScriptAssemblies()
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<System.Reflection.Assembly>? scriptAssemblies = null;
        try
        {
            scriptAssemblies = ScriptAssemblyManager.GetScriptAssemblies();
        }
        catch
        {
            // manager not initialised (headless tests) — fall through to AppDomain scan
        }

        if (scriptAssemblies != null)
        {
            foreach (System.Reflection.Assembly assembly in scriptAssemblies)
            {
                if (emitted.Add(assembly.GetName().Name ?? ""))
                    yield return assembly;
            }
        }

        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (emitted.Add(assembly.GetName().Name ?? ""))
                yield return assembly;
        }
    }

    private static string PrefsPath()
    {
        string? root = Projects.Project.Current?.LibraryPath;
        return root != null ? Path.Combine(root, PrefsFile) : Path.Combine(Path.GetTempPath(), PrefsFile);
    }

    private static void LoadPrefs()
    {
        Favorites.Clear();
        Recent.Clear();
        Preferred.Clear();
        try
        {
            if (!File.Exists(PrefsPath()))
                return;
            foreach (string line in File.ReadAllLines(PrefsPath()))
            {
                if (line.StartsWith("fav:", StringComparison.Ordinal))
                    Favorites.Add(line[4..]);
                else if (line.StartsWith("pref:", StringComparison.Ordinal))
                    Preferred.Add(line[5..]);
                else if (line.StartsWith("recent:", StringComparison.Ordinal))
                    Recent.Add(line[7..]);
            }
        }
        catch
        {
            // corrupt prefs are non-fatal
        }
    }

    private static void SavePrefs()
    {
        try
        {
            using var writer = new StreamWriter(PrefsPath());
            foreach (string fav in Favorites)
                writer.WriteLine("fav:" + fav);
            foreach (string pref in Preferred)
                writer.WriteLine("pref:" + pref);
            foreach (string recent in Recent)
                writer.WriteLine("recent:" + recent);
        }
        catch
        {
        }
    }
}
