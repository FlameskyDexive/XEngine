// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Importers;
using XEngine.Terrain;

namespace XEngine.Terrain.Editor;

/// <summary>Imports Echo-serialized <c>.terrainlayer</c> assets.</summary>
[ImporterFor(TerrainLayer.FileExtension)]
public sealed class TerrainLayerImporter : AssetImporter
{
    public override int Version => 1;
    public override bool Import(ImportContext ctx) => ImportHelper.ImportEcho<TerrainLayer>(ctx, "terrain layer");
}
