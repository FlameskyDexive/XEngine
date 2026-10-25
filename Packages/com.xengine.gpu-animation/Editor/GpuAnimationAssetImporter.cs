using XEngine.Editor.Importers;

namespace XEngine.GpuAnimation.Editor;

/// <summary>
/// Imports .gpuanim files — Echo-serialized <see cref="BakedGpuAnimation"/> assets.
/// </summary>
[ImporterFor(".gpuanim")]
public class GpuAnimationAssetImporter : AssetImporter
{
    public override int Version => 1;

    public override bool Import(ImportContext ctx) => ImportHelper.ImportEcho<BakedGpuAnimation>(ctx, "gpu animation data");
}
