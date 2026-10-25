using XEngine.Editor.Importers;
using XEngine.Terrain;

namespace XEngine.Terrain.Editor;

/// <summary>
/// Imports .terraindata files - Echo-serialized TerrainData objects.
/// </summary>
[ImporterFor(".terraindata")]
public class TerrainDataImporter : AssetImporter
{
    public override int Version => 1;
    public override bool Import(ImportContext ctx) => ImportHelper.ImportEcho<TerrainData>(ctx, "terrain data");
}
