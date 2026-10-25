// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Globalization;
using System.IO;
using System.Text;

using DotRecast.Detour;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>Exports baked navmesh data to OBJ / JSON / Detour binary.</summary>
public static class NavMeshExport
{
    public static void WriteObj(NavMeshData data, string path)
        => File.WriteAllText(path, ToObjString(data.RuntimeMesh));

    public static void WriteObj(DtNavMesh mesh, string path)
        => File.WriteAllText(path, ToObjString(mesh));

    public static void WriteJson(NavMeshData data, string path)
        => File.WriteAllText(path, ToJsonString(data));

    public static void WriteBin(NavMeshData data, string path)
        => File.WriteAllBytes(path, data.TileData ?? []);

    public static string ToObjString(DtNavMesh mesh)
    {
        NavMeshTriangulation tri = NavMeshGeometry.Extract(mesh);
        var sb = new StringBuilder(tri.Vertices.Length * 24);
        sb.AppendLine("# XEngine NavMesh");
        for (int i = 0; i < tri.Vertices.Length; i++)
        {
            Float3 v = tri.Vertices[i];
            sb.Append("v ");
            sb.Append(v.X.ToString("G9", CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(v.Y.ToString("G9", CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(v.Z.ToString("G9", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        for (int i = 0; i + 2 < tri.Indices.Length; i += 3)
        {
            sb.Append("f ");
            sb.Append(tri.Indices[i] + 1);
            sb.Append(' ');
            sb.Append(tri.Indices[i + 1] + 1);
            sb.Append(' ');
            sb.Append(tri.Indices[i + 2] + 1);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ToJsonString(NavMeshData data)
    {
        NavMeshTriangulation tri = NavMeshGeometry.Extract(data.RuntimeMesh);
        var sb = new StringBuilder(512);
        sb.Append('{');
        AppendKey(sb, "settingsHash"); sb.Append(data.SettingsHash); sb.Append(',');
        AppendKey(sb, "agentTypeId"); sb.Append(data.AgentTypeId); sb.Append(',');
        AppendKey(sb, "tileSize"); sb.Append(data.TileSize); sb.Append(',');
        AppendKey(sb, "tileCountX"); sb.Append(data.TileCountX); sb.Append(',');
        AppendKey(sb, "tileCountZ"); sb.Append(data.TileCountZ); sb.Append(',');
        AppendKey(sb, "bounds");
        sb.Append("{\"min\":"); AppendVec(sb, data.Bounds.Min);
        sb.Append(",\"max\":"); AppendVec(sb, data.Bounds.Max);
        sb.Append("},");
        AppendKey(sb, "areas");
        sb.Append('[');
        for (int i = 0; i < data.Areas.Count; i++)
        {
            if (i > 0) sb.Append(',');
            NavMeshAreaInfo a = data.Areas[i];
            sb.Append("{\"id\":"); sb.Append(a.Id);
            sb.Append(",\"name\":"); AppendString(sb, a.Name);
            sb.Append(",\"cost\":"); sb.Append(a.Cost.ToString("G9", CultureInfo.InvariantCulture));
            sb.Append('}');
        }
        sb.Append("],");
        AppendKey(sb, "tiles");
        sb.Append('[');
        for (int i = 0; i < data.Tiles.Count; i++)
        {
            if (i > 0) sb.Append(',');
            NavMeshTileSummary t = data.Tiles[i];
            sb.Append("{\"tileX\":"); sb.Append(t.TileX);
            sb.Append(",\"tileZ\":"); sb.Append(t.TileZ);
            sb.Append(",\"polyCount\":"); sb.Append(t.PolyCount);
            sb.Append(",\"vertCount\":"); sb.Append(t.VertCount);
            sb.Append('}');
        }
        sb.Append("],");
        AppendKey(sb, "triangleCount"); sb.Append(tri.Indices.Length / 3); sb.Append(',');
        AppendKey(sb, "vertices");
        sb.Append('[');
        for (int i = 0; i < tri.Vertices.Length; i++)
        {
            if (i > 0) sb.Append(',');
            AppendVec(sb, tri.Vertices[i]);
        }
        sb.Append("],");
        AppendKey(sb, "indices");
        sb.Append('[');
        for (int i = 0; i < tri.Indices.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(tri.Indices[i]);
        }
        sb.Append("],");
        AppendKey(sb, "triangleAreas");
        sb.Append('[');
        for (int i = 0; i < tri.Areas.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(tri.Areas[i]);
        }
        sb.Append(']');
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendKey(StringBuilder sb, string key)
    {
        sb.Append('"');
        sb.Append(key);
        sb.Append("\":");
    }

    private static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        if (!string.IsNullOrEmpty(value))
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
        }
        sb.Append('"');
    }

    private static void AppendVec(StringBuilder sb, Float3 v)
    {
        sb.Append('[');
        sb.Append(v.X.ToString("G9", CultureInfo.InvariantCulture));
        sb.Append(',');
        sb.Append(v.Y.ToString("G9", CultureInfo.InvariantCulture));
        sb.Append(',');
        sb.Append(v.Z.ToString("G9", CultureInfo.InvariantCulture));
        sb.Append(']');
    }
}
