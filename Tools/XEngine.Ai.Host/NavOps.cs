using System.Globalization;
using System.IO;

using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// Offline NavMesh helpers. Parses Echo <c>.navmesh</c> text and runs Detour queries against the
/// <c>TileData</c> ByteArray — no <c>XEngine.Runtime</c> reference.
/// </summary>
internal static class NavOps
{
    public static NavInspectResult Inspect(string echoText, string targetAsset)
    {
        EchoObject root = EchoObject.ReadFromString(echoText);
        EchoObject? settings = root.Get("Settings");
        var settingsMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (settings != null && settings.TagType == EchoType.Compound)
        {
            foreach (string key in settings.GetNames())
            {
                if (key is "$id" or "$type" or "$ref") continue;
                if (!settings.TryGet(key, out EchoObject? child) || child == null) continue;
                if (child.TagType is EchoType.Compound or EchoType.List) continue;
                settingsMap[key] = Scalar(child);
            }
        }

        var tiles = new List<NavTileSummary>();
        foreach (EchoObject tile in Children(root.Get("Tiles")))
        {
            tiles.Add(new NavTileSummary(
                Int(tile, "TileX", 0),
                Int(tile, "TileZ", 0),
                Int(tile, "PolyCount", 0),
                Int(tile, "VertCount", 0)));
        }

        NormalizeByteArray(root, "TileData");
        NormalizeByteArray(root, "TileCacheData");
        byte[] tileData = ReadBytes(root.Get("TileData"));
        byte[] cache = ReadBytes(root.Get("TileCacheData"));
        return new NavInspectResult(
            targetAsset,
            Str(root, "Name", Path.GetFileNameWithoutExtension(targetAsset)),
            Str(root, "$type", "XEngine.Runtime.Navigation.NavMeshData"),
            Int(root, "SettingsHash", 0),
            Int(root, "AgentTypeId", 0),
            Int(root, "TileSize", 0),
            Int(root, "TileCountX", 0),
            Int(root, "TileCountZ", 0),
            Vec(root.Get("BoundsMin")),
            Vec(root.Get("BoundsMax")),
            settingsMap,
            tiles,
            tileData.Length,
            cache.Length,
            ProvenancedDelta.ContentHash(root));
    }

    public static NavQueryPathResult QueryPath(string echoText, string targetAsset, NavVec3 from, NavVec3 to)
    {
        EchoObject root = EchoObject.ReadFromString(echoText);
        byte[] tileData = ReadBytes(root.Get("TileData"));
        if (tileData.Length == 0)
            return new NavQueryPathResult(targetAsset, false, "Failed", [], "NavMesh has no TileData payload.");

        DtNavMesh mesh;
        try
        {
            mesh = ReadMesh(tileData);
        }
        catch (Exception ex)
        {
            return new NavQueryPathResult(targetAsset, false, "Failed", [], ex.Message);
        }

        var query = new DtNavMeshQuery(mesh);
        var filter = new DtQueryDefaultFilter();
        var extents = new RcVec3f(2f, 4f, 2f);
        var start = new RcVec3f(from.X, from.Y, from.Z);
        var end = new RcVec3f(to.X, to.Y, to.Z);

        DtStatus startStatus = query.FindNearestPoly(start, extents, filter, out long startRef, out RcVec3f startPt, out _);
        DtStatus endStatus = query.FindNearestPoly(end, extents, filter, out long endRef, out RcVec3f endPt, out _);
        if (startStatus.Failed() || endStatus.Failed() || startRef == 0 || endRef == 0)
            return new NavQueryPathResult(targetAsset, false, "Failed", [], "Could not snap start/end onto the navmesh.");

        var corridor = new long[256];
        DtStatus pathStatus = query.FindPath(startRef, endRef, startPt, endPt, filter, corridor, out int pathCount, corridor.Length);
        if (pathStatus.Failed() || pathCount <= 0)
            return new NavQueryPathResult(targetAsset, false, "Failed", [], "FindPath failed.");

        var straight = new DtStraightPath[64];
        DtStatus straightStatus = query.FindStraightPath(startPt, endPt, corridor.AsSpan(0, pathCount), pathCount,
            straight, out int straightCount, straight.Length, 0);
        if (straightStatus.Failed() || straightCount <= 0)
            return new NavQueryPathResult(targetAsset, false, "Failed", [], "FindStraightPath failed.");

        var corners = new NavVec3[straightCount];
        for (int i = 0; i < straightCount; i++)
        {
            RcVec3f p = straight[i].pos;
            corners[i] = new NavVec3(p.X, p.Y, p.Z);
        }

        string status = pathStatus.IsPartial() ? "Partial" : "Complete";
        return new NavQueryPathResult(targetAsset, true, status, corners, null);
    }

    public static NavExportResult Export(string echoText, string targetAsset, string format, string outputPath)
    {
        EchoObject root = EchoObject.ReadFromString(echoText);
        byte[] tileData = ReadBytes(root.Get("TileData"));
        if (tileData.Length == 0)
            throw new InvalidOperationException("NavMesh has no TileData payload.");

        string kind = format.Trim().ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");

        int verts = 0;
        int tris = 0;
        int bytes;
        if (kind is "bin" or "detour" or "meshset")
        {
            File.WriteAllBytes(outputPath, tileData);
            bytes = tileData.Length;
            CountGeometry(ReadMesh(tileData), out verts, out tris);
        }
        else
        {
            DtNavMesh mesh = ReadMesh(tileData);
            CountGeometry(mesh, out verts, out tris);
            if (kind == "obj")
            {
                string obj = ToObj(mesh);
                File.WriteAllText(outputPath, obj);
                bytes = Encoding.UTF8.GetByteCount(obj);
            }
            else if (kind == "json")
            {
                string json = ToJson(mesh, root);
                File.WriteAllText(outputPath, json);
                bytes = Encoding.UTF8.GetByteCount(json);
            }
            else
            {
                throw new ArgumentException("format must be obj, json, or bin.");
            }
        }

        return new NavExportResult(targetAsset, kind, outputPath, bytes, verts, tris);
    }

    public static DtNavMesh ReadMesh(byte[] tileData)
    {
        using var stream = new MemoryStream(tileData, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return new DtMeshSetReader().Read(reader);
    }

    public static byte[] WriteMeshSet(DtNavMesh mesh)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            new DtMeshSetWriter().Write(writer, mesh, RcByteOrder.LITTLE_ENDIAN, cCompatibility: false);
        return stream.ToArray();
    }

    static string ToObj(DtNavMesh mesh)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("# XEngine NavMesh");
        int vertexIndex = 1;
        int maxTiles = mesh.GetMaxTiles();
        for (int t = 0; t < maxTiles; t++)
        {
            DtMeshTile? tile = mesh.GetTile(t);
            DtMeshHeader? header = tile?.data?.header;
            if (header == null || header.polyCount <= 0) continue;
            DtMeshData data = tile!.data;
            for (int p = 0; p < header.polyCount; p++)
            {
                DtPoly poly = data.polys[p];
                if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION) continue;
                if (poly.vertCount < 3) continue;
                int v0 = vertexIndex;
                for (int v = 0; v < poly.vertCount; v++)
                {
                    int i = poly.verts[v] * 3;
                    sb.Append("v ");
                    sb.Append(data.verts[i].ToString("G9", CultureInfo.InvariantCulture));
                    sb.Append(' ');
                    sb.Append(data.verts[i + 1].ToString("G9", CultureInfo.InvariantCulture));
                    sb.Append(' ');
                    sb.Append(data.verts[i + 2].ToString("G9", CultureInfo.InvariantCulture));
                    sb.AppendLine();
                    vertexIndex++;
                }

                for (int i = 1; i + 1 < poly.vertCount; i++)
                {
                    sb.Append("f ");
                    sb.Append(v0);
                    sb.Append(' ');
                    sb.Append(v0 + i);
                    sb.Append(' ');
                    sb.Append(v0 + i + 1);
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    static string ToJson(DtNavMesh mesh, EchoObject root)
    {
        var verts = new List<string>();
        var indices = new List<int>();
        int maxTiles = mesh.GetMaxTiles();
        for (int t = 0; t < maxTiles; t++)
        {
            DtMeshTile? tile = mesh.GetTile(t);
            DtMeshHeader? header = tile?.data?.header;
            if (header == null || header.polyCount <= 0) continue;
            DtMeshData data = tile!.data;
            for (int p = 0; p < header.polyCount; p++)
            {
                DtPoly poly = data.polys[p];
                if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION) continue;
                if (poly.vertCount < 3) continue;
                int baseIndex = verts.Count;
                for (int v = 0; v < poly.vertCount; v++)
                {
                    int i = poly.verts[v] * 3;
                    verts.Add(string.Create(CultureInfo.InvariantCulture,
                        $"[{data.verts[i]:G9},{data.verts[i + 1]:G9},{data.verts[i + 2]:G9}]"));
                }

                for (int i = 1; i + 1 < poly.vertCount; i++)
                {
                    indices.Add(baseIndex);
                    indices.Add(baseIndex + i);
                    indices.Add(baseIndex + i + 1);
                }
            }
        }

        var sb = new StringBuilder(256);
        sb.Append("{\"settingsHash\":");
        sb.Append(Int(root, "SettingsHash", 0).ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"triangleCount\":");
        sb.Append((indices.Count / 3).ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"vertices\":[");
        sb.Append(string.Join(",", verts));
        sb.Append("],\"indices\":[");
        sb.Append(string.Join(",", indices));
        sb.Append("]}");
        return sb.ToString();
    }

    static void CountGeometry(DtNavMesh mesh, out int verts, out int tris)
    {
        verts = 0;
        tris = 0;
        int maxTiles = mesh.GetMaxTiles();
        for (int t = 0; t < maxTiles; t++)
        {
            DtMeshTile? tile = mesh.GetTile(t);
            DtMeshHeader? header = tile?.data?.header;
            if (header == null) continue;
            verts += header.vertCount;
            for (int p = 0; p < header.polyCount; p++)
            {
                DtPoly poly = tile!.data.polys[p];
                if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION) continue;
                if (poly.vertCount >= 3)
                    tris += poly.vertCount - 2;
            }
        }
    }

    static IReadOnlyList<EchoObject> Children(EchoObject? node)
    {
        if (node == null) return [];
        if (node.TagType == EchoType.List) return node.List;
        if (node.TryGet("$values", out EchoObject? values) && values != null && values.TagType == EchoType.List)
            return values.List;
        return [];
    }

    static void NormalizeByteArray(EchoObject root, string key)
    {
        if (!root.TryGet(key, out EchoObject? tag) || tag == null) return;
        if (tag.TagType == EchoType.ByteArray && tag.Value == null)
            tag.ByteArrayValue = [];
    }

    static byte[] ReadBytes(EchoObject? tag)
    {
        if (tag == null) return [];
        if (tag.TagType == EchoType.ByteArray) return tag.ByteArrayValue ?? [];
        string? b64 = tag.TagType == EchoType.String ? tag.StringValue : null;
        return string.IsNullOrEmpty(b64) ? [] : Convert.FromBase64String(b64);
    }

    static NavVec3 Vec(EchoObject? o)
        => new(Float(o, "X", 0f), Float(o, "Y", 0f), Float(o, "Z", 0f));

    static string Str(EchoObject o, string key, string def)
        => o.Get(key)?.StringValue ?? def;

    static int Int(EchoObject o, string key, int def)
        => ToInt(o.Get(key), def);

    static float Float(EchoObject? o, string key, float def)
        => o == null ? def : ToFloat(o.Get(key), def);

    static int ToInt(EchoObject? tag, int def)
    {
        if (tag == null) return def;
        return tag.TagType switch
        {
            EchoType.Int => tag.IntValue,
            EchoType.Long => (int)tag.LongValue,
            EchoType.Float => (int)tag.FloatValue,
            EchoType.Double => (int)tag.DoubleValue,
            _ => int.TryParse(Scalar(tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def,
        };
    }

    static float ToFloat(EchoObject? tag, float def)
    {
        if (tag == null) return def;
        return tag.TagType switch
        {
            EchoType.Float => tag.FloatValue,
            EchoType.Double => (float)tag.DoubleValue,
            EchoType.Int => tag.IntValue,
            EchoType.Long => tag.LongValue,
            _ => float.TryParse(Scalar(tag), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def,
        };
    }

    static string Scalar(EchoObject tag) => tag.TagType switch
    {
        EchoType.String => tag.StringValue ?? "",
        EchoType.Bool => tag.BoolValue ? "true" : "false",
        EchoType.Float => tag.FloatValue.ToString("G9", CultureInfo.InvariantCulture),
        EchoType.Double => tag.DoubleValue.ToString("G9", CultureInfo.InvariantCulture),
        EchoType.Int => tag.IntValue.ToString(CultureInfo.InvariantCulture),
        EchoType.Long => tag.LongValue.ToString(CultureInfo.InvariantCulture),
        EchoType.ByteArray => $"<{tag.ByteArrayValue?.Length ?? 0} bytes>",
        _ => tag.ToString() ?? "",
    };
}
