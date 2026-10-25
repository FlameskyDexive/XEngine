using System;
using System.IO;
using System.Text.Json;

namespace XEngine.Editor.Projects;

/// <summary>
/// Represents a XEngine project on disk.
/// A project is a folder containing an Assets/ directory and a .xengine marker file.
/// </summary>
public class Project
{
    private ProjectDirectoryLease? _directoryLease;
    public static Project? Current { get; private set; }

    public string Name { get; private set; }
    public string RootPath { get; private set; }

    // Standard directories
    public string AssetsPath => Path.Combine(RootPath, "Assets");
    public string LibraryPath => Path.Combine(RootPath, "Library");
    public string CachePath => Path.Combine(RootPath, "Library", "cache");
    public string ThumbnailsPath => Path.Combine(RootPath, "Library", "thumbnails");
    public string ProjectSettingsPath => Path.Combine(RootPath, "ProjectSettings");
    public string PackagesPath => Path.Combine(RootPath, "Packages");
    public string TempPath => Path.Combine(RootPath, "Temp");
    public string LogsPath => Path.Combine(RootPath, "Logs");
    public string XEngineFilePath => Path.Combine(RootPath, $"{Name}.xengine");
    public string MetadataDbPath => Path.Combine(LibraryPath, "metadata.db");
    public string EditorStatePath => Path.Combine(LibraryPath, "EditorState.json");

    // Build
    public string BuildTempPath => Path.Combine(TempPath, "PlayerBuild");

    // Script compilation
    public string ScriptAssemblyPath => Path.Combine(LibraryPath, "ScriptAssemblies");
    public string GameAssemblyPath => Path.Combine(ScriptAssemblyPath, $"{Name}.Game.dll");
    public string EditorAssemblyPath => Path.Combine(ScriptAssemblyPath, $"{Name}.Editor.dll");
    public string GameCsprojPath => Path.Combine(RootPath, $"{Name}.Game.csproj");
    public string EditorCsprojPath => Path.Combine(RootPath, $"{Name}.Editor.csproj");
    public string AutoSaveScenePath => Path.Combine(LibraryPath, "~autosave.scene");

    private Project(string rootPath, string name)
    {
        RootPath = Path.GetFullPath(rootPath);
        Name = name;
    }

    /// <summary>
    /// Create a new project at the given path.
    /// </summary>
    public static Project Create(string parentFolder, string projectName, bool exclusive = false)
    {
        string rootPath = Path.Combine(parentFolder, projectName);

        if (Directory.Exists(rootPath) && Directory.GetFileSystemEntries(rootPath).Length > 0)
            throw new InvalidOperationException($"Directory '{rootPath}' already exists and is not empty.");

        var project = new Project(rootPath, projectName);
        if (exclusive)
            project._directoryLease = ProjectDirectoryLease.Acquire(rootPath);
        try
        {
            project.EnsureDirectories();
            project.WriteXEngineFile();
            UniversalProjectTemplate.Seed(project);

            // Create a default .gitignore
            string gitignore = Path.Combine(rootPath, ".gitignore");
            if (!File.Exists(gitignore))
            {
                File.WriteAllText(gitignore,
                    "Library/\nTemp/\nLogs/\n*.csproj\n*.sln\n*.slnx\n.vs/\nbin/\nobj/\n# MacOS\n.DS_Store\n");
            }

            // Non-destructive MSBuild customization surface. The generated .csproj files are rewritten on
            // every recompile, so this checked-in file is where users add their own references.
            string buildProps = Path.Combine(rootPath, "Directory.Build.props");
            if (!File.Exists(buildProps))
                File.WriteAllText(buildProps, DirectoryBuildPropsTemplate);

            // ★ AI agent harness — seed the agent scaffolding (.mcp.json / AGENTS.md / skills, 三件套)
            // so a fresh project is immediately agent-operable. Best-effort: a missing engine layout
            // (no cli/skills, no harness/) must never block project creation.
            TrySeedHarness(rootPath);

            return project;
        }
        catch
        {
            project._directoryLease?.Dispose();
            throw;
        }
    }

    private static void TrySeedHarness(string rootPath)
    {
        try
        {
            var result = XEngine.Ai.Harness.HarnessInstaller.Install(rootPath);
            if (!result.Success)
            {
                Console.Error.WriteLine($"[xengine] agent harness not seeded: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[xengine] agent harness seeding failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Open an existing project from a root folder or .xengine file path.
    /// </summary>
    public static Project Open(string path, bool exclusive = false)
    {
        string rootPath;

        if (File.Exists(path) && path.EndsWith(".xengine", StringComparison.OrdinalIgnoreCase))
        {
            rootPath = Path.GetDirectoryName(path)!;
        }
        else if (Directory.Exists(path))
        {
            rootPath = path;
        }
        else
        {
            throw new DirectoryNotFoundException($"Project path not found: {path}");
        }

        rootPath = Path.GetFullPath(rootPath);

        // Validate it's a project must have Assets/ folder
        string assetsDir = Path.Combine(rootPath, "Assets");
        if (!Directory.Exists(assetsDir))
            throw new InvalidOperationException($"Not a valid XEngine project: missing Assets/ folder in '{rootPath}'");

        // Find the project name from .xengine file or folder name
        string name = Path.GetFileName(rootPath);
        var xengineFiles = Directory.GetFiles(rootPath, "*.xengine");
        if (xengineFiles.Length > 0)
        {
            try
            {
                string json = File.ReadAllText(xengineFiles[0]);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                    name = nameProp.GetString() ?? name;
            }
            catch { }
        }

        var project = new Project(rootPath, name);
        if (exclusive)
            project._directoryLease = ProjectDirectoryLease.Acquire(rootPath);
        try
        {
            project.EnsureDirectories();

            // Write .xengine file if missing
            if (xengineFiles.Length == 0)
                project.WriteXEngineFile();

            return project;
        }
        catch
        {
            project._directoryLease?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Set this project as the currently active project.
    /// </summary>
    /// <param name="addToRecent">
    /// When true (default) the project is added to the user's recent-projects list. Tests and other
    /// non-interactive callers pass false to avoid polluting that list.
    /// </param>
    public void SetActive(bool addToRecent = true)
    {
        Current = this;
        if (addToRecent)
            RecentProjects.AddRecent(RootPath, Name);
        Runtime.Debug.Log($"Opened project: {Name} at {RootPath}");
    }

    /// <summary>
    /// Clears the active project (<see cref="Current"/> becomes null). Used when closing a project
    /// or tearing down a test environment.
    /// </summary>
    public static void CloseCurrent()
    {
        Current?._directoryLease?.Dispose();
        if (Current != null) Current._directoryLease = null;
        Current = null;
    }

    /// <summary>
    /// Check if a directory looks like a valid XEngine project.
    /// </summary>
    public static bool IsValidProject(string path)
    {
        if (!Directory.Exists(path)) return false;
        return Directory.Exists(Path.Combine(path, "Assets"));
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(AssetsPath);
        Directory.CreateDirectory(LibraryPath);
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(ThumbnailsPath);
        Directory.CreateDirectory(ProjectSettingsPath);
        Directory.CreateDirectory(PackagesPath);
        Directory.CreateDirectory(TempPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(ScriptAssemblyPath);
    }

    private void WriteXEngineFile()
    {
        var data = new
        {
            name = Name,
            engine = "XEngine",
            version = "0.0.1",
            created = DateTime.UtcNow.ToString("o")
        };

        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(XEngineFilePath, json);
    }

    private const string DirectoryBuildPropsTemplate =
        """
        <Project>

          <!--
            Directory.Build.props (XEngine user MSBuild customization)

            This is where you add NuGet packages for your game. XEngine regenerates the .csproj files on
            every recompile, but never touches this file, and MSBuild imports it into every generated
            user project automatically (editor script assemblies and the shipped player build). Add
            PackageReference, ProjectReference, analyzers, or any other MSBuild here.

            Packages in the default group below are available to your scripts in the editor AND bundled
            into player builds. To keep a package out of player builds (editor tooling only), put it in
            the editor group, which is scoped to the *.Editor assembly by name.
          -->

          <!-- Available everywhere: editor scripts, runtime scripts, and shipped player builds. -->
          <ItemGroup>
            <!-- <PackageReference Include="Newtonsoft.Json" Version="13.0.3" /> -->
            <!-- <ProjectReference Include="..\Shared\Shared.csproj" /> -->
          </ItemGroup>

          <!-- Editor only: never shipped in a player build. -->
          <ItemGroup Condition="$(MSBuildProjectName.EndsWith('.Editor'))">
            <!-- <PackageReference Include="ImGui.NET" Version="1.91.6.1" /> -->
          </ItemGroup>

        </Project>
        """;
}
