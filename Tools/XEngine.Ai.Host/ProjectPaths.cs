using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 — Path resolution helpers over a <see cref="ProjectDescriptor"/>. The project's Assets
/// directory is the canonical root for scene/asset paths. Both the .xengine-project case and the
/// engine-repo case (no .xengine, Assets inferred from a ProjectSettings/Assets convention) are
/// handled; when no Assets dir exists the tools report an empty result rather than throw.
/// </summary>
internal static class ProjectPaths
{
    /// <summary>The project's Assets directory, if present (XEngine convention). Null when absent.</summary>
    public static string? AssetsRoot(this ProjectDescriptor project)
    {
        string direct = Path.Combine(project.ProjectRoot, "Assets");
        if (Directory.Exists(direct)) return direct;

        // The engine repo itself has no Assets/ — common when running tooling against the repo.
        return null;
    }

    /// <summary>Resolve a project-relative asset path to absolute; accept already-absolute paths.</summary>
    public static string ResolveAssetPath(this ProjectDescriptor project, string scenePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenePath);
        if (Path.IsPathRooted(scenePath)) return scenePath;

        // Try under Assets/ first (game project), then directly under the project root.
        string? assets = project.AssetsRoot();
        if (assets is not null)
        {
            string inAssets = Path.Combine(assets, scenePath);
            if (File.Exists(inAssets)) return inAssets;
        }

        string inRoot = Path.Combine(project.ProjectRoot, scenePath);
        return inRoot;
    }
}
