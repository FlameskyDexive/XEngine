using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.Web;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.Runtime;

namespace XEngine.Editor.Build;

/// <summary>
/// Build pipeline for Browser / WeChat / Douyin (<c>net10.0</c> + <c>browser-wasm</c>).
/// Generates a relocatable player, then publishes when <c>wasm-tools</c> is installed.
/// </summary>
public class WebBuildPipeline : BuildPipeline
{
    internal const string MissingWasmToolsError =
        "Generated net10.0 browser-wasm player sources. `dotnet publish -r browser-wasm` requires the wasm-tools workload.";

    public override string DisplayName => "Web";

    public override string Id => "xengine.web";

    public override string Icon => EditorIcons.Globe;

    public override string[] SupportedRuntimeIdentifiers => ["browser-wasm"];

    public override BuildResult Build(BuildSettings settings, BuildProgress? progress)
        => BuildAsync(Project.Current?.RootPath ?? "", settings, outputDirectory: null, progress)
            .GetAwaiter().GetResult();

    public override async Task<BuildResult> BuildAsync(
        string projectPath,
        BuildSettings settings,
        string? outputDirectory = null,
        BuildProgress? progress = null,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(settings);
            string exportRoot = PrepareExportRoot(projectPath, outputDirectory);
            progress?.Log("[Web] Generating browser-wasm player project...", 0.2f);

            WebBuildProfile profile = settings.GetProfile<WebBuildProfile>(GetType());
            WebPlayerProjectLayout layout = await WebPlayerProjectGenerator.GenerateAsync(
                    exportRoot,
                    CreateOptions(profile),
                    cancellation)
                .ConfigureAwait(false);

            progress?.Log("[Web] Player sources written to " + layout.RootDirectory, 0.5f);

            WebPublishPlan publish = WebPublishPlanner.Create(
                layout.ProjectPath,
                layout.RootDirectory,
                settings.Config);
            progress?.Log("[Web] Publishing browser-wasm (wasm-tools, IsBrowserTarget=true)...", 0.7f);
            await WebPublishPlanner.RunAsync(publish, cancellation).ConfigureAwait(false);

            string framework = Path.Combine(publish.WwwRootDirectory, "_framework");
            if (!Directory.Exists(framework))
            {
                return Failure(
                    "dotnet publish did not produce wwwroot/_framework at " + framework + ".",
                    stopwatch.Elapsed,
                    layout.RootDirectory);
            }

            WebPublishPlanner.WriteStableFrameworkAliases(framework);
            string wechatDirectory = Path.Combine(layout.RootDirectory, "wechat");
            if (Directory.Exists(wechatDirectory))
                WeChatMiniGamePackager.Pack(framework, wechatDirectory);
            string douyinDirectory = Path.Combine(layout.RootDirectory, "douyin");
            if (Directory.Exists(douyinDirectory))
                WebPublishPlanner.CopyDouyinRuntimeAliases(framework, douyinDirectory);
            WebPackageSizeSnapshot size = WebPublishPlanner.WriteSizeReport(layout.RootDirectory, framework);
            string sizeReport = Path.Combine(layout.RootDirectory, "verification", WebPublishPlanner.SizeReportFileName);
            size.Enforce(sizeReport);
            WebPublishPlanner.WriteRuntimeBaseline(layout.RootDirectory);
            progress?.Log("[Web] Published " + publish.WwwRootDirectory, 0.95f);
            return new BuildResult
            {
                Success = true,
                OutputPath = layout.RootDirectory,
                Log = "Published " + publish.WwwRootDirectory + " (" + profile.SubTarget + ")",
                Duration = stopwatch.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return Failure(ex.Message, stopwatch.Elapsed);
        }
    }

    public override string GetExecutablePath(string outputPath, BuildSettings settings)
    {
        string productName = ResolveProductName();
        string assemblyName = WebPlayerProjectGenerator.GetAssemblyName(productName);
        string published = Path.Combine(outputPath, "publish", "wwwroot", "index.html");
        if (File.Exists(published))
            return published;
        string www = Path.Combine(outputPath, "Player", "wwwroot", "index.html");
        if (File.Exists(www))
            return www;
        return Path.Combine(outputPath, "Player", assemblyName + ".csproj");
    }

    private static WebPlayerProjectOptions CreateOptions(WebBuildProfile profile)
    {
        string productName = ResolveProductName();
        string runtimeProject = WebPlayerProjectGenerator.FindRuntimeProject(
            Path.GetDirectoryName(typeof(EngineObject).Assembly.Location) ?? Directory.GetCurrentDirectory());
        bool simd = profile.SubTarget == WebSubTarget.Douyin ? false : profile.WasmEnableSIMD;
        return new WebPlayerProjectOptions
        {
            ProjectName = productName,
            ApplicationId = string.IsNullOrWhiteSpace(profile.ApplicationId)
                ? "com.xengine.game"
                : profile.ApplicationId,
            ApplicationLabel = productName,
            SubTarget = profile.SubTarget,
            WasmEnableSIMD = simd,
            CanvasSelector = string.IsNullOrWhiteSpace(profile.CanvasSelector)
                ? "#canvas"
                : profile.CanvasSelector,
            RuntimeProjectPath = runtimeProject,
        };
    }

    private static string ResolveProductName()
    {
        string productName = "XEngine";
        GeneralSettings? general = EditorRegistries.GetSettings<GeneralSettings>();
        if (!string.IsNullOrWhiteSpace(general?.ProductName))
            productName = general.ProductName;
        return productName;
    }

    private static string PrepareExportRoot(string projectPath, string? outputDirectory)
    {
        string raw = string.IsNullOrWhiteSpace(outputDirectory) ? "Builds" : outputDirectory;
        string resolved = Path.GetFullPath(Path.Combine(
            string.IsNullOrWhiteSpace(projectPath) ? Directory.GetCurrentDirectory() : projectPath,
            raw));

        string exportRoot = Path.Combine(resolved, "xengine-web-player");
        if (Directory.Exists(exportRoot) && HasEntries(exportRoot))
            exportRoot = Path.Combine(resolved, "xengine-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportRoot);
        return exportRoot;
    }

    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }

    private static BuildResult Failure(string errors, TimeSpan duration, string? outputPath = null) => new()
    {
        Success = false,
        Errors = errors,
        OutputPath = outputPath ?? string.Empty,
        Duration = duration,
    };
}
