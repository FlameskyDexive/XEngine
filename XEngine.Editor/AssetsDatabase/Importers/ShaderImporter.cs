using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using XEngine.Editor.Build;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Editor.Importers;

[ImporterFor(".shader")]
public class ShaderImporter : AssetImporter
{
        /// <summary>Main-thread importer: touches main-only editor state / user code.</summary>
        public override bool RequiresMainThread => true;

    private static readonly ConditionalWeakTable<Project, ShaderArtifactStore> s_artifactStores = new();

    // Reimport source shaders when embedded HLSL includes change, so cached ShaderPass sources
    // carry the current Metal-specific resource declarations into target artifact builds.
    public override int Version => 5;

    public override bool Import(ImportContext ctx)
    {
        string source = File.ReadAllText(ctx.AbsolutePath);
        string dir = Path.GetDirectoryName(ctx.AbsolutePath) ?? "";
        var hlslDependencyHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        // Resolve #include directives:
        // 1. Relative to the shader file's directory
        // 2. Relative to the project's Assets root
        // 3. Built-in engine includes (Fragment.glsl, PBR.glsl, Lighting.glsl, etc.)
        string? IncludeResolver(string includePath)
        {
            if (Path.IsPathRooted(includePath))
                return File.Exists(includePath) ? ReadInclude(includePath) : null;

            // 1. Relative to shader file
            string fullPath = Path.Combine(dir, includePath);
            if (File.Exists(fullPath))
                return ReadInclude(fullPath);

            // 2. Relative to Assets root
            if (Project.Current != null)
            {
                string assetsPath = Path.Combine(Project.Current.AssetsPath, includePath);
                if (File.Exists(assetsPath))
                    return ReadInclude(assetsPath);
            }

            // 3. Built-in engine includes (embedded resources)
            // The includePath may be a full absolute path like "C:/.../Assets/Fragment.glsl"
            // Extract just the filename and try loading from the built-in defaults
            string fileName = Path.GetFileName(includePath);
            try
            {
                string content = EmbeddedResources.ReadAllText($"Assets/Defaults/{fileName}");
                if (string.Equals(Path.GetExtension(fileName), ".hlsl", StringComparison.OrdinalIgnoreCase))
                    hlslDependencyHashes[$"embedded://Assets/Defaults/{fileName}"] = ShaderArtifactKey.ComputeDependencyHash(content);
                return content;
            }
            catch
            {
                return null;
            }
        }

        string ReadInclude(string path)
        {
            string absolutePath = Path.GetFullPath(path);
            string content = File.ReadAllText(absolutePath);
            Project? project = Project.Current;
            string identity = $"shader-relative/{NormalizePath(Path.GetRelativePath(dir, absolutePath))}";

            if (project != null && TryGetAssetRelativePath(project, absolutePath, out string assetPath))
            {
                identity = $"Assets/{assetPath}";
                if (EditorAssetBackend.Instance != null)
                {
                    Guid dependencyGuid = EditorAssetBackend.Instance.PathToGuid(assetPath);
                    if (dependencyGuid != Guid.Empty)
                        ctx.AddDependency(dependencyGuid);
                }
            }

            if (string.Equals(Path.GetExtension(absolutePath), ".hlsl", StringComparison.OrdinalIgnoreCase))
                hlslDependencyHashes[identity] = ShaderArtifactKey.ComputeDependencyHash(content);

            return content;
        }

        if (!ShaderParser.ParseShader(ctx.AbsolutePath, source, IncludeResolver, out Shader? shader) || shader == null)
        {
            if (Project.Current is Project project)
                ShaderArtifactBuildPackager.DeleteImportIndex(project, ctx.AssetGuid);
            Debug.LogError($"Failed to parse shader: {ctx.AbsolutePath}");
            return false;
        }

        shader.Name = Path.GetFileNameWithoutExtension(ctx.AbsolutePath);

        Project? activeProject = Project.Current;
        if (activeProject == null)
        {
            Debug.LogError("Cannot compile offline shader artifacts without an active project.");
            return false;
        }

        // Point the runtime's on-demand variant compiles at the project artifact store so
        // draw-time keyword combinations (SKINNED, HAS_UV2, …) persist across sessions instead
        // of re-running the full Slang compile on every Play (the dominant Play-start cost).
        ShaderPass.EditorArtifactStore = GetArtifactStore(activeProject);

        try
        {
            if (!CompileOfflineArtifacts(activeProject, ctx.AssetGuid, shader, hlslDependencyHashes))
                return false;
        }
        catch
        {
            if (Project.Current is Project project)
                ShaderArtifactBuildPackager.DeleteImportIndex(project, ctx.AssetGuid);
            throw;
        }

        ctx.SetMainAsset(shader);
        return true;
    }

    internal static ShaderArtifactStore GetArtifactStore(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return s_artifactStores.GetValue(
            project,
            static value => new ShaderArtifactStore(Path.Combine(value.CachePath, "shaders")));
    }

    /// <summary>
    /// Wires the runtime's on-demand variant compile cache (<see cref="ShaderPass.EditorArtifactStore"/>)
    /// to this project's artifact store. Called at project open (before any draws) and again per
    /// shader import, so both fresh and warm sessions hit the persisted variants.
    /// </summary>
    internal static void LinkRuntimeArtifactStore(Project project)
        => ShaderPass.EditorArtifactStore = GetArtifactStore(project);

    private static bool CompileOfflineArtifacts(
        Project project,
        Guid shaderAssetGuid,
        Shader shader,
        IReadOnlyDictionary<string, string> dependencyHashes)
    {
        ShaderArtifactStore store = GetArtifactStore(project);
        ShaderArtifactStoreStats before = store.GetStats();
        RuntimeSlangShaderCompiler? compiler = null;
        string compilerIdentity = RuntimeSlangShaderCompiler.CompilerIdentity;
        int artifactCount = 0;
        var artifactKeys = new List<ShaderArtifactKey>();

        foreach (ShaderPass pass in shader.Passes)
        {
            if (!pass.HasHlsl)
                continue;

            if (!CompileVariant(pass, string.Empty, null))
            {
                ShaderArtifactBuildPackager.DeleteImportIndex(project, shaderAssetGuid);
                return false;
            }
            foreach (string variantKey in shader.OfflineVariantKeys)
            {
                if (!CompileVariant(pass, variantKey, CreateKeywordSet(variantKey)))
                {
                    ShaderArtifactBuildPackager.DeleteImportIndex(project, shaderAssetGuid);
                    return false;
                }
            }
        }

        ShaderArtifactBuildPackager.WriteImportIndex(project, shaderAssetGuid, artifactKeys);

        ShaderArtifactStoreStats after = store.GetStats();
        Debug.Log(
            $"[ShaderImporter] offline artifacts={artifactCount}, " +
            $"memoryHits={after.MemoryHits - before.MemoryHits}, diskHits={after.DiskHits - before.DiskHits}, " +
            $"misses={after.Misses - before.Misses}, writes={after.Writes - before.Writes}, " +
            $"bytes={after.BytesWritten - before.BytesWritten}");
        return true;

        bool CompileVariant(
            ShaderPass pass,
            string variantKey,
            IReadOnlyDictionary<string, bool>? keywords)
        {
            bool vulkanSucceeded = CompileArtifact(
                project,
                shaderAssetGuid,
                pass,
                GraphicsBackend.Vulkan,
                variantKey,
                keywords,
                dependencyHashes,
                ref compiler,
                out ShaderArtifactKey vulkanKey);
            artifactCount++;
            if (vulkanSucceeded)
                artifactKeys.Add(vulkanKey);

            bool d3d12Succeeded = CompileArtifact(
                project,
                shaderAssetGuid,
                pass,
                GraphicsBackend.Direct3D12,
                variantKey,
                keywords,
                dependencyHashes,
                ref compiler,
                out ShaderArtifactKey d3d12Key);
            artifactCount++;
            if (d3d12Succeeded)
                artifactKeys.Add(d3d12Key);
            return vulkanSucceeded && d3d12Succeeded;
        }
    }

    internal static bool CompileBuiltInOfflineArtifacts(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (Guid shaderGuid in BuildShaderInventory.GetBuiltInShaderGuids(project))
        {
            if (BuiltInAssets.Get(shaderGuid) is not Shader shader)
                return false;
            if (!CompileOfflineArtifacts(project, shaderGuid, shader, new Dictionary<string, string>()))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Compiles only the collected build variants that are absent from their import indexes. The
    /// import cache retains its normal default and declared variants; target packagers can then
    /// select the collected subset without accidentally treating a missing variant as a fallback.
    /// </summary>
    internal static bool CompileArtifactSelections(
        Project project,
        IReadOnlyList<ShaderArtifactBuildSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0)
            return true;

        var selectionsByShader = new SortedDictionary<Guid, List<ShaderArtifactBuildSelection>>();
        for (int i = 0; i < selections.Count; i++)
        {
            ShaderArtifactBuildSelection selection = selections[i];
            if (selection.ShaderAssetGuid == Guid.Empty)
                throw new ArgumentException("Shader artifact selection cannot contain an empty asset GUID.", nameof(selections));
            if (string.IsNullOrWhiteSpace(selection.PassName))
                throw new ArgumentException("Shader artifact selection cannot contain an empty pass name.", nameof(selections));
            if (!selectionsByShader.TryGetValue(selection.ShaderAssetGuid, out List<ShaderArtifactBuildSelection>? group))
            {
                group = [];
                selectionsByShader.Add(selection.ShaderAssetGuid, group);
            }
            if (!group.Contains(selection))
                group.Add(selection);
        }

        ShaderArtifactStore store = GetArtifactStore(project);
        foreach (KeyValuePair<Guid, List<ShaderArtifactBuildSelection>> entry in selectionsByShader)
        {
            Guid shaderAssetGuid = entry.Key;
            List<ShaderArtifactBuildSelection> required = entry.Value;
            required.Sort(CompareSelections);
            if (!TryReadImportIndex(project, shaderAssetGuid, out List<ShaderArtifactKey>? artifactKeys))
                return false;

            var indexedKeysBySelection = new Dictionary<ShaderArtifactBuildSelection, List<ShaderArtifactKey>>();
            for (int keyIndex = 0; keyIndex < artifactKeys.Count; keyIndex++)
            {
                ShaderArtifactKey key = artifactKeys[keyIndex];
                if (!store.Contains(key))
                {
                    Debug.LogError($"Shader artifact {key.Value} is missing for asset {shaderAssetGuid}.");
                    return false;
                }
                try
                {
                    using FileStream stream = File.OpenRead(store.GetArtifactPath(key));
                    ShaderArtifact artifact = ShaderArtifactCodec.Read(stream, key);
                    var indexedSelection = new ShaderArtifactBuildSelection(
                        shaderAssetGuid,
                        artifact.PassName,
                        artifact.Backend,
                        artifact.VariantKey,
                        RuntimeSlangShaderCompiler.GetArtifactTargetEnvironment(
                            artifact.Backend,
                            artifact.TargetProfile));
                    if (!indexedKeysBySelection.TryGetValue(indexedSelection, out List<ShaderArtifactKey>? indexedKeys))
                    {
                        indexedKeys = [];
                        indexedKeysBySelection.Add(indexedSelection, indexedKeys);
                    }
                    indexedKeys.Add(key);
                }
                catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
                {
                    Debug.LogError($"Shader artifact {key.Value} is corrupt for asset {shaderAssetGuid}: {exception.Message}");
                    return false;
                }
            }

            Shader? shader = AssetDatabase.Get(shaderAssetGuid) as Shader;
            if (shader == null)
            {
                Debug.LogError($"Unable to load shader asset {shaderAssetGuid} for Android variant compilation.");
                return false;
            }

            RuntimeSlangShaderCompiler? compiler = null;
            bool changedIndex = false;
            for (int selectionIndex = 0; selectionIndex < required.Count; selectionIndex++)
            {
                ShaderArtifactBuildSelection selection = required[selectionIndex];
                ShaderPass? pass = FindPass(shader, selection.PassName);
                if (pass == null || !pass.HasHlsl)
                {
                    Debug.LogError(
                        $"Shader {shader.Name} does not provide an HLSL pass '{selection.PassName}' " +
                        $"required by Android variant collection.");
                    return false;
                }

                IReadOnlyDictionary<string, bool>? keywords = string.IsNullOrEmpty(selection.VariantKey)
                    ? null
                    : CreateKeywordSet(selection.VariantKey);
                if (!CompileArtifact(
                        project,
                        shaderAssetGuid,
                        pass,
                        selection.Backend,
                        selection.VariantKey,
                        keywords,
                        new Dictionary<string, string>(),
                        ref compiler,
                        out ShaderArtifactKey key,
                        selection.TargetEnvironment))
                {
                    return false;
                }

                if (indexedKeysBySelection.TryGetValue(selection, out List<ShaderArtifactKey>? indexedKeys))
                {
                    for (int indexedKey = 0; indexedKey < indexedKeys.Count; indexedKey++)
                    {
                        ShaderArtifactKey previousKey = indexedKeys[indexedKey];
                        if (previousKey == key)
                            continue;

                        artifactKeys.Remove(previousKey);
                        changedIndex = true;
                    }
                }

                if (!artifactKeys.Contains(key))
                {
                    artifactKeys.Add(key);
                    changedIndex = true;
                }
            }

            if (changedIndex)
                ShaderArtifactBuildPackager.WriteImportIndex(project, shaderAssetGuid, artifactKeys);
        }

        return true;
    }

    private static bool CompileArtifact(
        Project project,
        Guid shaderAssetGuid,
        ShaderPass pass,
        GraphicsBackend backend,
        string variantKey,
        IReadOnlyDictionary<string, bool>? keywords,
        IReadOnlyDictionary<string, string> dependencyHashes,
        ref RuntimeSlangShaderCompiler? compiler,
        out ShaderArtifactKey key,
        ShaderTargetEnvironment targetEnvironment = ShaderTargetEnvironment.Default)
    {
        ShaderArtifactStore store = GetArtifactStore(project);
        ShaderCompileRequest request = pass.CreateHlslCompileRequest(
            backend,
            keywords,
            variantKey,
            targetEnvironment);
        string compilerIdentity = RuntimeSlangShaderCompiler.CompilerIdentity;
        string targetProfile = RuntimeSlangShaderCompiler.GetArtifactTargetProfile(backend, targetEnvironment);
        key = ShaderArtifactKey.Create(
            request,
            compilerIdentity,
            targetProfile,
            dependencyHashes,
            RuntimeSlangShaderCompiler.MatrixLayout);
        if (store.Contains(key))
            return true;

        ShaderCompileResult result = (compiler ??= new RuntimeSlangShaderCompiler()).Compile(request);
        if (!result.Success || result.VertexBytecode == null || result.FragmentBytecode == null)
        {
            Debug.LogError(
                $"Offline Slang compile failed for {pass.ShaderName}/{pass.Name} ({backend}): " +
                (result.ErrorMessage ?? "Compiler returned incomplete bytecode."));
            return false;
        }

        var bytecode = new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            result.Format,
            result.VertexBytecode,
            result.FragmentBytecode,
            result.BindingLayout,
            result.VertexInputLayout);
        var artifact = new ShaderArtifact(
            backend,
            ShaderCompilerKind.Slang,
            compilerIdentity,
            ShaderArtifactKey.ComputeSourceHash(request),
            targetProfile,
            RuntimeSlangShaderCompiler.MatrixLayout,
            pass.ShaderName,
            pass.Name,
            variantKey,
            request.VertexEntryPoint,
            request.FragmentEntryPoint,
            [],
            result.Diagnostics,
            bytecode);
        store.Put(key, artifact);
        return true;
    }

    private static bool TryReadImportIndex(
        Project project,
        Guid shaderAssetGuid,
        out List<ShaderArtifactKey> artifactKeys)
    {
        artifactKeys = [];
        string indexPath = ShaderArtifactBuildPackager.GetImportIndexPath(project, shaderAssetGuid);
        if (!File.Exists(indexPath))
        {
            Debug.LogError($"Shader artifact index is missing for asset {shaderAssetGuid}. Reimport the shader before building.");
            return false;
        }
        try
        {
            using FileStream stream = File.OpenRead(indexPath);
            ShaderArtifactCatalog index = ShaderArtifactCatalog.Read(stream);
            if (index.Entries.Count != 1 ||
                !index.TryGetArtifacts(shaderAssetGuid, out IReadOnlyList<ShaderArtifactKey>? keys))
            {
                Debug.LogError($"Shader artifact index does not match asset {shaderAssetGuid}.");
                return false;
            }
            artifactKeys = new List<ShaderArtifactKey>(keys);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            Debug.LogError($"Shader artifact index is corrupt for asset {shaderAssetGuid}: {exception.Message}");
            return false;
        }
    }

    private static ShaderPass? FindPass(Shader shader, string passName)
    {
        foreach (ShaderPass pass in shader.Passes)
        {
            if (string.Equals(pass.Name, passName, StringComparison.Ordinal))
                return pass;
        }
        return null;
    }

    private static Dictionary<string, bool> CreateKeywordSet(string variantKey)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        string[] keywords = variantKey.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < keywords.Length; i++)
            result.Add(keywords[i], true);
        return result;
    }

    private static int CompareSelections(ShaderArtifactBuildSelection left, ShaderArtifactBuildSelection right)
    {
        int comparison = StringComparer.Ordinal.Compare(left.PassName, right.PassName);
        if (comparison != 0)
            return comparison;
        comparison = left.Backend.CompareTo(right.Backend);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.VariantKey, right.VariantKey);
    }

    private static bool TryGetAssetRelativePath(Project project, string absolutePath, out string assetPath)
    {
        assetPath = NormalizePath(Path.GetRelativePath(project.AssetsPath, absolutePath));
        return !Path.IsPathRooted(assetPath) &&
               !assetPath.Equals("..", StringComparison.Ordinal) &&
               !assetPath.StartsWith("../", StringComparison.Ordinal);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
