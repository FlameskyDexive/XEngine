// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.IO;

using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.Build.Android;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.Runtime;

namespace XEngine.Editor.Build;

/// <summary>
/// Main class that should handle build starting/logging.
/// It also supports starting the build as a separate process and receive logs from that.
/// </summary>
public static class ProjectBuilder
{
    public static void BuildLog(string message, LogSeverity severity = LogSeverity.Normal)
    {
        if (Program.BuildMode)
        {
            var logMessage = new BuildSettingsPanel.BuildStatusReport()
            {
                Severity = severity,
                Type = BuildSettingsPanel.BuildStatusReport.BuildStatusReportType.Info,
                Message = message,
            };
            var serializedOutput = Serializer.Serialize(logMessage).WriteToString();
            string serializedBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(serializedOutput));
            Console.WriteLine(serializedBase64);
        }
        else
        {
            switch (severity)
            {
                case LogSeverity.Error:
                    Runtime.Debug.LogError(message);
                    break;
                case LogSeverity.Warning:
                    Runtime.Debug.LogWarning(message);
                    break;
                case LogSeverity.Success:
                    Runtime.Debug.LogSuccess(message);
                    break;
                default:
                    Runtime.Debug.Log(message);
                    break;
            }
        }
    }

    public static void BuildProgressLog(string message, float progress, LogSeverity severity = LogSeverity.Normal)
    {
        if (Program.BuildMode)
        {
            var logMessage = new BuildSettingsPanel.BuildStatusReport()
            {
                Severity = severity,
                Type = BuildSettingsPanel.BuildStatusReport.BuildStatusReportType.Progress,
                Message = message,
                Progress = progress
            };
            var serializedOutput = Serializer.Serialize(logMessage).WriteToString();
            string serializedBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(serializedOutput));
            Console.WriteLine(serializedBase64);
        }
        else
        {
            switch (severity)
            {
                case LogSeverity.Error:
                    Runtime.Debug.LogError(message);
                    break;
                case LogSeverity.Warning:
                    Runtime.Debug.LogWarning(message);
                    break;
                case LogSeverity.Success:
                    Runtime.Debug.LogSuccess(message);
                    break;
                default:
                    Runtime.Debug.Log(message);
                    break;
            }
        }
    }

    public static void ProcessBuildLog(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrEmpty(args.Data))
        {
            try
            {
                Console.WriteLine($"[BEGIN]{args.Data}[END]");
                var serializedData = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args.Data));
                var echoData = EchoObject.ReadFromString(serializedData);
                var logData = Serializer.Deserialize<BuildSettingsPanel.BuildStatusReport>(echoData);

                if (logData != null)
                {
                    BuildSettingsPanel.BuildState = logData.Message;
                    if (logData.Type == BuildSettingsPanel.BuildStatusReport.BuildStatusReportType.Progress)
                        BuildSettingsPanel.BuildProgress = logData.Progress;

                    switch (logData.Severity)
                    {
                        case LogSeverity.Error:
                            Runtime.Debug.LogError(logData.Message);
                            break;
                        case LogSeverity.Warning:
                            Runtime.Debug.LogWarning(logData.Message);
                            break;
                        case LogSeverity.Success:
                            Runtime.Debug.LogSuccess(logData.Message);
                            break;
                        default:
                            Runtime.Debug.Log(logData.Message);
                            break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }

    /// <summary>
    /// Runs the build as a separate process that runs separate from the main editor process.
    /// To do, it launches the editor with build arguments to trigger an automatic build of the project.
    /// </summary>
    /// <param name="outputPath">The output path for the build.</param>
    /// <returns></returns>
    public static BuildProgress StartBuildProcess(string? outputPath)
    {
        BuildProgress progress = null;
        // Ask for output folder
        EditorApplication.OpenFileDialog(FileDialogMode.SelectFolder, outputPath => {
            {
                Runtime.Debug.Log($"{Project.Current.RootPath}");
                Runtime.Debug.Log($"{outputPath}");

                ProcessStartInfo psi = new ProcessStartInfo()
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = $"-build \"{Project.Current.RootPath}\" -o \"{outputPath}\"",

                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                };
                progress = new BuildProgress();
                var process = new System.Diagnostics.Process()
                {
                    StartInfo = psi
                };
                process.OutputDataReceived += ProcessBuildLog;
                process.Start();
                process.BeginOutputReadLine();
            }
        }, outputPath);

        return progress;
    }

    public static BuildProgress StartBuildAsync(bool andRun, string? outputPath)
    {
        BuildSettings? settings;
        try
        {
            settings = EditorRegistries.GetSettings<BuildSettings>();
        }
        catch
        {
            Runtime.Debug.LogError("BuildSettings not found.");

            return null;
        }

        // Invocation output (CLI -o) wins; otherwise the active profile / shared default. Never
        // persist that destination back into BuildSettings — a headless build of three profiles
        // in a row must leave the project's authored output directory alone.
        string destination = ResolvedBuildOptions.From(settings, outputPath).OutputDirectory;
        if (string.IsNullOrWhiteSpace(destination)) return null;

        var pipeline = ResolvePipeline(settings);

        Runtime.Debug.Log($"[Build] Starting {pipeline.DisplayName} build to {destination}...", LogSeverity.Normal);

        var progress = new BuildProgress();
        var projectPath = Project.Current?.RootPath ?? "";

        var assetSettings = EditorRegistries.GetSettings<AssetSettings>();
        if (assetSettings != null && assetSettings.AsyncAssetLoading)
        {
            // Now that rendering is handled by a separate thread, we should be able to run
            // the build in a separate thread as well without having any issues
            System.Threading.Tasks.Task task = System.Threading.Tasks.Task.Run(() =>
            {
                ProcessBuild(projectPath, pipeline, settings, destination, progress, andRun);
            });

            if (Program.BuildMode)
            {
                task.Wait();
            }
        }
        else
        {
            ProcessBuild(projectPath, pipeline, settings, destination, progress, andRun);
        }

        return progress;
    }

    public static void ProcessBuild(string projectPath, BuildPipeline pipeline, BuildSettings settings, string outputPath, BuildProgress progress, bool andRun)
    {
        try
        {
            Console.WriteLine($"[BEGIN]{projectPath}[END]");
            var result = pipeline.BuildAsync(
                projectPath, settings, outputPath, progress).GetAwaiter().GetResult();
            if (result.Success)
            {
                var postErrors = BuildPostProcessor.RunAll(new BuildPostContext
                {
                    ProjectPath = projectPath,
                    OutputDirectory = result.OutputPath,
                    Platform = pipeline.Id,
                });
                if (postErrors.Count > 0)
                {
                    result.Success = false;
                    result.Errors = string.Join(Environment.NewLine, postErrors);
                    progress.Complete(result);
                    HandleBuildResult(result);
                    if (Program.BuildMode)
                        Environment.ExitCode = 1;
                    return;
                }
            }
            if (andRun && result.Success)
                result = LaunchBuildResult(projectPath, pipeline, result, settings, progress);
            progress.Complete(result);

            HandleBuildResult(result);
            if (Program.BuildMode)
                Environment.ExitCode = result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            progress.Log($"FATAL: {ex.Message}", Runtime.LogSeverity.Error);
            progress.Complete(new BuildResult { Success = false, Errors = ex.ToString(), });
            if (Program.BuildMode)
                Environment.ExitCode = 1;
        }
    }

    private static BuildResult LaunchBuildResult(
        string projectPath,
        BuildPipeline pipeline,
        BuildResult result,
        BuildSettings settings,
        BuildProgress progress)
    {
        BuildLaunchResult launch = pipeline.LaunchAsync(new BuildLaunchRequest
        {
            ProjectPath = projectPath,
            Settings = settings,
            Build = result,
            Progress = progress,
        }).GetAwaiter().GetResult();

        if (launch.Handled)
        {
            if (!string.IsNullOrWhiteSpace(launch.Diagnostics))
                progress.Log(launch.Diagnostics, launch.Success ? LogSeverity.Normal : LogSeverity.Error);
            progress.Log("[Build & Run] " + launch.Message, launch.Success ? LogSeverity.Success : LogSeverity.Error);
            if (!launch.Success)
            {
                result.Success = false;
                result.Errors = string.IsNullOrWhiteSpace(result.Errors)
                    ? launch.Message
                    : result.Errors + Environment.NewLine + launch.Message;
            }
            return result;
        }

        string exe = pipeline.GetExecutablePath(result.OutputPath, settings);
        if (!File.Exists(exe))
            return result;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            progress.Log("[Build & Run] Launched " + Path.GetFileName(exe) + ".", LogSeverity.Success);
        }
        catch (Exception exception)
        {
            Runtime.Debug.LogError("[Build] Failed to launch: " + exception.Message);
        }
        return result;
    }

    private static void HandleBuildResult(BuildResult result)
    {
        if (result.Success)
        {
            BuildLog($"[Build] SUCCESS: {result.AssetCount} assets -> {result.OutputPath} ({result.Duration.TotalSeconds:F1}s)", LogSeverity.Success);
            return;
        }

        BuildLog($"[Build] FAILED: {result.Errors}", LogSeverity.Error);
    }

    /// <summary>
    /// Selects the build pipeline to run based on <see cref="BuildSettings.ActivePipelineId"/>.
    /// This mirrors the reflection scan in <see cref="BuildSettingsPanel.GetBuildPlatforms"/> so the
    /// CLI/ProjectBuilder and the Build panel always agree. A blank id falls back to the desktop
    /// pipeline for back-compat with projects saved before multi-platform support; an unknown id
    /// is a hard error rather than a silent desktop build.
    /// </summary>
    internal static BuildPipeline ResolvePipeline(BuildSettings settings)
    {
        string wanted = settings.ActivePipelineId ?? "";

        // First registered non-abstract BuildPipeline subclass whose Id matches.
        foreach (var type in ScriptAssemblyManager.GetAllTypes())
        {
            if (!type.IsSubclassOf(typeof(BuildPipeline)) || type.IsAbstract)
                continue;

            BuildPipeline? instance = null;
            try { instance = Activator.CreateInstance(type) as BuildPipeline; }
            catch { continue; }
            if (instance == null)
                continue;

            if (string.IsNullOrEmpty(wanted))
            {
                // Back-compat: no persisted selection -> desktop is the historical default.
                if (type == typeof(DesktopBuildPipeline))
                    return instance;
                continue;
            }

            if (string.Equals(instance.Id, wanted, StringComparison.OrdinalIgnoreCase))
                return instance;
        }

        if (string.IsNullOrEmpty(wanted))
            return new DesktopBuildPipeline();

        throw new InvalidOperationException(
            $"Unknown or unavailable build pipeline id '{wanted}'. " +
            "Select a platform in the Build panel or pass a valid --platform value.");
    }
}
