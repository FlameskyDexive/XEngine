using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using XEngine.OrigamiUI;
using XEngine.Vector;

namespace XEngine.Editor.Core;

/// <summary>Versioned workspace layout payload. Unknown panel data remains inside DockLayoutJson.</summary>
public sealed class EditorWorkspaceLayout
{
    public const int CurrentVersion = 2;
    public const string FileName = "workspaceLayout.v2";

    public int Version { get; private set; } = CurrentVersion;
    public List<EditorSurfaceLayout> Hosts { get; } = new();

    /// <summary>
    /// Restores non-main hosts into an already-created workspace. The caller owns the
    /// native-window creation step; this method only reconstructs logical dock trees.
    /// </summary>
    public int RestoreSecondaryHosts(
        EditorWorkspace workspace,
        Func<string, JsonObject?, DockPanel?> panelFactory)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(panelFactory);
        int restored = 0;

        for (int i = 0; i < Hosts.Count; i++)
        {
            EditorSurfaceLayout saved = Hosts[i];
            if (saved.IsMain || workspace.FindHost(saved.Id) != null
                || string.IsNullOrWhiteSpace(saved.DockLayoutJson))
                continue;

            try
            {
                var dock = new DockSpace(DockNode.Leaf());
                DockNode? root = DockSerializer.Deserialize(
                    saved.DockLayoutJson, dock.FloatingWindows, panelFactory);
                if (root == null)
                    continue;
                dock.Root = root;
                var host = new EditorSurfaceHost(saved.Id, dock, saved.LogicalBounds);
                host.MonitorId = saved.MonitorId;
                host.DpiScale = saved.DpiScale;
                if (workspace.RegisterHost(host))
                    restored++;
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
            {
                // One malformed secondary host must not prevent the main host from opening.
            }
        }

        // Capture preserves host order; applying front-to-back reconstructs the same z-order.
        for (int i = 0; i < Hosts.Count; i++)
        {
            EditorSurfaceLayout saved = Hosts[i];
            if (workspace.FindHost(saved.Id) != null)
                workspace.BringToFront(saved.Id);
        }
        return restored;
    }

    public static EditorWorkspaceLayout Capture(EditorWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var layout = new EditorWorkspaceLayout();
        for (int i = 0; i < workspace.Hosts.Count; i++)
        {
            EditorSurfaceHost host = workspace.Hosts[i];
            layout.Hosts.Add(new EditorSurfaceLayout
            {
                Id = host.Id,
                LogicalBounds = host.ScreenBounds,
                IsMain = host.Id == 0,
                MonitorId = host.MonitorId,
                DpiScale = host.DpiScale,
                DockLayoutJson = DockSerializer.Serialize(host.DockSpace),
            });
        }
        return layout;
    }

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["version"] = Version,
            ["hosts"] = new JsonArray(),
        };
        JsonArray hosts = (JsonArray)root["hosts"]!;
        for (int i = 0; i < Hosts.Count; i++)
        {
            EditorSurfaceLayout host = Hosts[i];
            Rect bounds = host.LogicalBounds;
            var entry = new JsonObject
            {
                ["id"] = host.Id,
                ["main"] = host.IsMain,
                ["monitor"] = host.MonitorId,
                ["dpi"] = host.DpiScale,
                ["bounds"] = new JsonObject
                {
                    ["x"] = bounds.Min.X,
                    ["y"] = bounds.Min.Y,
                    ["width"] = bounds.Max.X - bounds.Min.X,
                    ["height"] = bounds.Max.Y - bounds.Min.Y,
                },
            };
            if (!string.IsNullOrWhiteSpace(host.DockLayoutJson))
                entry["dock"] = JsonNode.Parse(host.DockLayoutJson);
            hosts.Add(entry);
        }
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Clamps logical host bounds into the current virtual desktop after monitor fallback.</summary>
    public void ClampToDesktop(Rect desktop)
    {
        float desktopWidth = MathF.Max(1f, desktop.Max.X - desktop.Min.X);
        float desktopHeight = MathF.Max(1f, desktop.Max.Y - desktop.Min.Y);
        for (int i = 0; i < Hosts.Count; i++)
        {
            EditorSurfaceLayout host = Hosts[i];
            Rect bounds = host.LogicalBounds;
            float width = Math.Clamp(bounds.Max.X - bounds.Min.X, 1f, desktopWidth);
            float height = Math.Clamp(bounds.Max.Y - bounds.Min.Y, 1f, desktopHeight);
            float x = Math.Clamp(bounds.Min.X, desktop.Min.X, desktop.Max.X - width);
            float y = Math.Clamp(bounds.Min.Y, desktop.Min.Y, desktop.Max.Y - height);
            host.LogicalBounds = new Rect(x, y, x + width, y + height);
        }
    }

    public static bool TryParse(string json, out EditorWorkspaceLayout? layout)
    {
        layout = null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["version"]?.GetValue<int>() != CurrentVersion)
                return false;
            if (root["hosts"] is not JsonArray hosts) return false;

            var parsed = new EditorWorkspaceLayout();
            var ids = new HashSet<int>();
            bool hasMain = false;
            for (int i = 0; i < hosts.Count; i++)
            {
                if (hosts[i] is not JsonObject entry || entry["bounds"] is not JsonObject bounds)
                    return false;
                int id = entry["id"]?.GetValue<int>() ?? -1;
                float x = bounds["x"]?.GetValue<float>() ?? float.NaN;
                float y = bounds["y"]?.GetValue<float>() ?? float.NaN;
                float width = bounds["width"]?.GetValue<float>() ?? float.NaN;
                float height = bounds["height"]?.GetValue<float>() ?? float.NaN;
                if (id < 0 || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width)
                    || !float.IsFinite(height) || width < 0f || height < 0f)
                    return false;
                bool isMain = entry["main"]?.GetValue<bool>() ?? false;
                if (!ids.Add(id) || (isMain && hasMain)) return false;
                hasMain |= isMain;

                parsed.Hosts.Add(new EditorSurfaceLayout
                {
                    Id = id,
                    IsMain = isMain,
                    MonitorId = entry["monitor"]?.GetValue<string>() ?? string.Empty,
                    DpiScale = entry["dpi"]?.GetValue<float>() is float dpi && float.IsFinite(dpi)
                        ? Math.Clamp(dpi, 0.5f, 8f) : 1f,
                    LogicalBounds = new Rect(x, y, x + width, y + height),
                    DockLayoutJson = entry["dock"]?.ToJsonString(),
                });
            }
            layout = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    public static bool TryLoad(string path, out EditorWorkspaceLayout? layout)
    {
        try { return TryParse(File.ReadAllText(path), out layout); }
        catch (IOException) { layout = null; return false; }
        catch (UnauthorizedAccessException) { layout = null; return false; }
    }

    /// <summary>Writes a complete payload beside the destination, then atomically promotes it.</summary>
    public void SaveAtomic(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, ToJson(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed class EditorSurfaceLayout
{
    public int Id { get; init; }
    public bool IsMain { get; init; }
    public string MonitorId { get; init; } = string.Empty;
    public float DpiScale { get; init; } = 1f;
    public Rect LogicalBounds { get; set; }
    public string? DockLayoutJson { get; init; }
}
