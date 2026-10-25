using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Importers;

/// <summary>
/// Imports .scene files Echo-serialized Scene objects (native XEngine format).
/// </summary>
[ImporterFor(".scene")]
public class SceneImporter : AssetImporter
{
        /// <summary>Main-thread importer: touches main-only editor state / user code.</summary>
        public override bool RequiresMainThread => true;

    public override int Version => 3; // Bumped: re-import to regenerate scenes cached by the pre-fix
                                      // GameObject deserializer (which could drop every object to null).

    public override bool Import(ImportContext ctx)
    {
        try
        {
            string text = File.ReadAllText(ctx.AbsolutePath);
            var echo = EchoObject.ReadFromString(text);

            var serCtx = ImportHelper.CreateTrackingContext();

            var scene = Serializer.Deserialize<Scene>(echo, serCtx);
            if (scene == null)
            {
                var loadedGameAssemblies = new List<string>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name?.Contains("A5EditorProject.Game", StringComparison.OrdinalIgnoreCase) == true)
                        loadedGameAssemblies.Add(assembly.FullName ?? assembly.GetName().Name ?? "<unknown>");
                }
                Debug.LogError($"Failed to import scene: {ctx.AbsolutePath}\nScene deserializer returned null. " +
                    $"Loaded game assemblies: {string.Join(", ", loadedGameAssemblies)}");
                return false;
            }

            ctx.SetMainAsset(scene);

            // ★ A3: forward typed edges (AssetRef roles) first.
            foreach (var dep in serCtx.Dependencies)
                ctx.AddDependency(dep.Guid, dep.Role);

            // Also walk the raw echo for PrefabAssetId references
            // (these are plain Guid strings, not AssetRef fields, so not auto-tracked) → role unknown.
            var prefabDeps = new HashSet<Guid>();
            ImportHelper.CollectAssetDependencies(echo, prefabDeps);
            foreach (var dep in prefabDeps)
                ctx.AddDependency(dep);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to import scene: {ctx.AbsolutePath}\n{ex}");
            return false;
        }
        return true;
    }
}
