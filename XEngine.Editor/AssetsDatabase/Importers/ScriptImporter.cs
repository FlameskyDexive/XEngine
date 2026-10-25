using System.IO;

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Scripting;

namespace XEngine.Editor.Importers;

/// <summary>
/// Tracks .cs script files. Does not produce an EngineObject -
/// scripts are compiled externally. Triggers recompilation only when
/// the script is newer than the compiled assembly.
/// </summary>
[ImporterFor(".cs")]
public class ScriptImporter : AssetImporter
{
        /// <summary>Main-thread importer: touches main-only editor state / user code.</summary>
        public override bool RequiresMainThread => true;

    public override int Version => 1;

    public override bool IsEditorOnlyAsset => true;

    public override bool Import(ImportContext ctx)
    {
        // Only request recompile if the script is newer than the compiled assembly
        var project = Project.Current;
        if (project != null && File.Exists(ctx.AbsolutePath))
        {
            var scriptTime = File.GetLastWriteTimeUtc(ctx.AbsolutePath);
            bool needsRecompile = true;

            // Check if Game assembly exists and is newer
            if (File.Exists(project.GameAssemblyPath))
            {
                var dllTime = File.GetLastWriteTimeUtc(project.GameAssemblyPath);
                needsRecompile = scriptTime > dllTime;
            }

            if (needsRecompile)
                ScriptAssemblyManager.RequestRecompile();
        }

        return true;
    }
}
