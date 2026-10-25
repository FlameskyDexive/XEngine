using System.IO;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Importers;

/// <summary>
/// Imports audio files (.wav, .mp3, .ogg, .flac) into AudioClip assets.
/// </summary>
[ImporterFor(".wav", ".mp3", ".ogg", ".flac")]
public class AudioImporter : AssetImporter
{
    public override int Version => 2;

    public override bool Import(ImportContext ctx)
    {
        try
        {
            // Cached assets must remain playable after the source project is moved or packaged.
            var clip = new AudioClip(File.ReadAllBytes(ctx.AbsolutePath));
            clip.ClipName = Path.GetFileNameWithoutExtension(ctx.AbsolutePath);
            ctx.SetMainAsset(clip);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Failed to import audio: {ctx.AbsolutePath}\n{ex.Message}");
            return false;
        }
        return true;
    }
}
