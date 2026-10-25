using System;
using System.Threading;
using System.Collections.Generic;
using System.Globalization;

using XEngine.Ai.Protocol;
using XEngine.Ai.ThreeHost.Client;
using XEngine.Async;
using XEngine.Editor.Core;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Scribe;

using TextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// ★ A1b — Editor face of the shared Windows three.js authoring host. The panel spawns the STA
/// WebView2 host process (owned by the editor HWND), drives it through the same
/// <c>three-authoring/1</c> protocol the CLI uses, and surfaces host diagnostics. The host runs
/// in its own process; the panel only owns its lifetime handle.
/// </summary>
public sealed class ThreeAuthoringPanel : DockPanel
{
    [MenuItem("Window/AI/Three.js Authoring", priority: 41)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(ThreeAuthoringPanel));

    public override string Title => "Three.js Authoring";
    public override string Icon => EditorIcons.Globe;
    public override float HeaderWidth => 34f;

    private const int PreviewSize = 512;
    private const string DefaultSource = "Three/sample.threefx";
    private const string DefaultExportDir = "Three/export";

    // One host process per editor session, shared across panel close/reopen.
    private static ThreeHostProcessClient? _sharedClient;
    private static string? _sharedHostExe;

    private string _source = DefaultSource;
    private string _exportDir = DefaultExportDir;
    private string _timeText = "1.5";
    private string _status = "not started.";
    private string _lastHash = "";
    private string _lastArtifactPath = "";
    private bool _busy;
    private readonly List<string> _diagnostics = new();

    /// <summary>Artifact hash of the last acceptance export (shared with the footer).</summary>
    private static string? _acceptanceHash;

    public override void OnGUI(Paper paper, float width, float height)
    {
        FontFile? font = EditorTheme.DefaultFont;
        if (font == null)
            return;

        if (_sharedClient is { HasExited: true })
        {
            int code = 0;
            _sharedClient = null;
            _busy = false;
            _status = "Host process exited (code " + code + ").";
        }

        // Fill the full panel rect with the shell background: an offscreen capture (MCP
        // panel screenshot) has no editor chrome behind us, so unpainted areas show noise.
        using (paper.Column("tha_root").Width(width).Height(height).Padding(10, 10, 8, 8)
                   .BackgroundColor(EditorTheme.Neutral100).ColBetween(6).Enter())
        {
            DrawStatusRow(paper, font, width);
            DrawActionsRow(paper, font);
            DrawRequestRow(paper, font, width);
            DrawDiagnostics(paper, font, width, height - 150);
            DrawFooter(paper, font, width);
        }
    }

    private void DrawStatusRow(Paper paper, FontFile font, float width)
    {
        string hostState = _sharedClient == null
            ? "stopped"
            : _sharedClient.HasExited ? "exited" : "running (pid " + _sharedClient.ProcessId + ")";
        paper.Box("tha_status").Width(width - 20).Height(20)
            .Text("Host: " + hostState + " — " + _status, font)
            .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
    }

    private void DrawActionsRow(Paper paper, FontFile font)
    {
        using (paper.Row("tha_actions").Width(UnitValue.Auto).Height(30).RowBetween(6).Enter())
        {
            Origami.Button(paper, "tha_launch", _sharedClient == null ? "Launch Host" : "Host Running", LaunchHost)
                .Width(110).Show();
            Origami.Button(paper, "tha_preview", "Preview 512²", RunPreview).Width(110).Show();
            Origami.Button(paper, "tha_export", "Export Sample", RunExport).Width(110).Show();
            Origami.Button(paper, "tha_cancel", "Cancel (kill host)", CancelInFlight).Width(130).Show();
            _ = font;
        }
    }

    private void DrawRequestRow(Paper paper, FontFile font, float width)
    {
        using (paper.Row("tha_source_row").Width(width - 20).Height(24).RowBetween(6).Enter())
        {
            paper.Box("tha_source_label").Width(76).Height(24)
                .Text("Source", font).TextColor(EditorTheme.InkDim)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            Origami.TextField(paper, "tha_source", _source, value => _source = value).Width(UnitValue.StretchOne).Show();
        }
        using (paper.Row("tha_export_row").Width(width - 20).Height(24).RowBetween(6).Enter())
        {
            paper.Box("tha_export_label").Width(76).Height(24)
                .Text("Export dir", font).TextColor(EditorTheme.InkDim)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            Origami.TextField(paper, "tha_export_dir", _exportDir, value => _exportDir = value)
                .Width(UnitValue.StretchOne).Show();
            Origami.TextField(paper, "tha_time", _timeText, value => _timeText = value).Width(56).Show();
        }
    }

    private void DrawDiagnostics(Paper paper, FontFile font, float width, float listHeight)
    {
        Origami.ScrollView(paper, "tha_diag", width - 20, listHeight).Body(() =>
        {
            using (paper.Column("tha_diag_col").Width(width - 32).Height(UnitValue.Auto).Padding(4, 4, 4, 4).ColBetween(2).Enter())
            {
                if (_diagnostics.Count == 0)
                {
                    paper.Box("tha_diag_empty").Width(width - 40).Height(20)
                        .Text("No diagnostics yet. Launch the host, then run a preview or export.", font)
                        .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall - 1)
                        .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                    return;
                }

                for (int i = _diagnostics.Count - 1; i >= 0; i--)
                {
                    string line = _diagnostics[i];
                    paper.Box("tha_diag_" + i).Width(width - 40).Height(18)
                        .Text(line, font)
                        .TextColor(line.Contains("error", StringComparison.Ordinal)
                            ? EditorTheme.Red400
                            : EditorTheme.InkDim)
                        .FontSize(EditorTheme.FontSizeSmall - 1)
                        .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                }
            }
        });
    }

    private void DrawFooter(Paper paper, FontFile font, float width)
    {
        string footer;
        if (_busy)
            footer = "Working…";
        else if (!string.IsNullOrEmpty(_lastHash))
            footer = _lastArtifactPath + "  sha256 " + _lastHash;
        else if (!string.IsNullOrEmpty(_acceptanceHash))
            footer = "acceptance export  sha256 " + _acceptanceHash;
        else
            footer = "No artifact yet.";
        paper.Box("tha_footer").Width(width - 20).Height(20)
            .Text(footer, font).TextColor(EditorTheme.Accent)
            .FontSize(EditorTheme.FontSizeSmall - 1)
            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
    }

    private void LaunchHost()
    {
        if (_sharedClient != null && !_sharedClient.HasExited)
        {
            _status = "Host already running.";
            return;
        }

        Project? project = Project.Current;
        if (project == null || string.IsNullOrEmpty(project.RootPath))
        {
            _status = "Open a project first.";
            return;
        }

        if (!ThreeHostLauncher.TryResolve(null, out string hostExe, out string? error))
        {
            _status = error ?? "Authoring host executable not found.";
            AddDiagnostic("error", _status);
            return;
        }

        nint ownerHwnd = 0;
        EditorApplication? app = EditorApplication.Instance;
        if (app != null)
            ownerHwnd = EditorNativeSurfaceManager.GetOsWindowHandle(XEngine.Runtime.Window.InternalWindow);

        var startInfo = new System.Diagnostics.ProcessStartInfo(hostExe)
        {
            Arguments = ThreeHostLauncher.BuildArguments(
                ThreeAuthoringProtocol.Version,
                project.RootPath,
                PageRootFor(hostExe),
                ownerHwnd,
                showWindow: true,
                width: 960,
                height: 640),
            WorkingDirectory = project.RootPath,
        };

        _sharedClient = new ThreeHostProcessClient(startInfo);
        _sharedHostExe = hostExe;
        _sharedClient.HostLog += line => AddDiagnostic("host", line);
        _status = "Host launched, waiting for WebView2 page…";
    }

    private void RunPreview()
    {
        Project? project = Project.Current;
        if (project == null || string.IsNullOrEmpty(project.RootPath))
        {
            _status = "Open a project first.";
            return;
        }

        RunRequest(project.RootPath, ThreeAuthoringOperation.Preview, outputDirectory: null);
    }

    private void RunExport()
    {
        Project? project = Project.Current;
        if (project == null || string.IsNullOrEmpty(project.RootPath))
        {
            _status = "Open a project first.";
            return;
        }

        RunRequest(project.RootPath, ThreeAuthoringOperation.Export, _exportDir);
    }

    private void RunRequest(
        string projectRoot,
        ThreeAuthoringOperation operation,
        string? outputDirectory)
    {
        if (_sharedClient == null || _sharedClient.HasExited)
            LaunchHost();
        if (_sharedClient == null || _sharedClient.HasExited)
            return;

        _busy = true;
        _status = (operation == ThreeAuthoringOperation.Preview ? "Preview" : "Export") + " in flight…";
        _ = RunRequestAsync(projectRoot, operation, outputDirectory);
    }

    private async XTaskVoid RunRequestAsync(
        string projectRoot,
        ThreeAuthoringOperation operation,
        string? outputDirectory)
    {
        ThreeHostProcessClient? client = _sharedClient;
        var request = new ThreeAuthoringRequest(
            ProtocolVersion: ThreeAuthoringProtocol.Version,
            RequestId: "editor-" + Guid.NewGuid().ToString("N")[..12],
            Operation: operation.ToString(),
            SourcePath: _source,
            OutputDirectory: outputDirectory,
            PreviewId: null,
            TimeSeconds: ParseTime(),
            Width: PreviewSize,
            Height: PreviewSize,
            TimeoutMs: ThreeAuthoringProtocol.DefaultTimeoutMs,
            Intent: "three authoring " + operation.ToString().ToLowerInvariant(),
            Options: null);

        ThreeAuthoringResponse response;
        try
        {
            await XTask.SwitchToThreadPool();
            response = client!.RoundTrip(request, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await XTask.SwitchToMainThread();
            _busy = false;
            _status = operation + " failed: " + ex.Message;
            AddDiagnostic("error", _status);
            return;
        }

        await XTask.SwitchToMainThread();
        _busy = false;
        RecordResponse(response);
    }

    private void CancelInFlight()
    {
        if (_sharedClient == null)
        {
            _status = "No host to cancel.";
            return;
        }

        _sharedClient.Dispose();
        _sharedClient = null;
        _busy = false;
        _status = "Host terminated (cancelled).";
        AddDiagnostic("info", "Host terminated by user cancel.");
    }

    private void RecordResponse(ThreeAuthoringResponse response)
    {
        if (!response.Succeeded)
            _status = "Failed: " + (response.ErrorCode ?? "unknown") + " — " + response.Message;
        else
            _status = response.Message ?? "Done.";

        for (int i = 0; i < response.Diagnostics.Count; i++)
        {
            ThreeAuthoringDiagnostic diagnostic = response.Diagnostics[i];
            AddDiagnostic(diagnostic.Severity, diagnostic.Code + ": " + diagnostic.Message);
        }

        if (response.Manifest is { } manifest && manifest.Artifacts.Count > 0)
        {
            _lastHash = manifest.Artifacts[0].ContentHash;
            _lastArtifactPath = manifest.Artifacts[0].RelativePath;
        }
    }

    private void AddDiagnostic(string severity, string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            + " [" + severity + "] " + message;
        lock (_diagnostics)
        {
            _diagnostics.Add(line);
            if (_diagnostics.Count > 200)
                _diagnostics.RemoveRange(0, _diagnostics.Count - 200);
        }
    }

    private double ParseTime()
    {
        if (!double.TryParse(
                _timeText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double seconds)
            || seconds < 0
            || double.IsNaN(seconds)
            || double.IsInfinity(seconds))
        {
            return 0;
        }
        return seconds;
    }

    private static string PageRootFor(string hostExe) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(hostExe)!, "wwwroot");

    /// <summary>
    /// Acceptance hook (A1b 截图验收): performs a blocking export through the exact same client +
    /// protocol path the panel buttons use and returns the artifact sha256. Driven from
    /// <c>runtime_eval</c> by <c>Tools/capture_three_host.py</c>; not wired to any UI.
    /// </summary>
    public static string? ExportBlockingForAcceptance()
    {
        Project? project = Project.Current;
        if (project == null || string.IsNullOrEmpty(project.RootPath))
            return null;

        if (!ThreeHostLauncher.TryResolve(null, out string hostExe, out string? error))
            throw new InvalidOperationException(error);

        var startInfo = new System.Diagnostics.ProcessStartInfo(hostExe)
        {
            Arguments = ThreeHostLauncher.BuildArguments(
                ThreeAuthoringProtocol.Version,
                project.RootPath,
                PageRootFor(hostExe),
                ownerHwnd: 0,
                showWindow: false,
                width: 960,
                height: 640),
            WorkingDirectory = project.RootPath,
        };

        var request = new ThreeAuthoringRequest(
            ProtocolVersion: ThreeAuthoringProtocol.Version,
            RequestId: "acceptance-" + Guid.NewGuid().ToString("N")[..12],
            Operation: "Export",
            SourcePath: DefaultSource,
            OutputDirectory: DefaultExportDir,
            PreviewId: null,
            TimeSeconds: 1.5,
            Width: PreviewSize,
            Height: PreviewSize,
            TimeoutMs: ThreeAuthoringProtocol.DefaultTimeoutMs,
            Intent: "three authoring acceptance export",
            Options: null);

        using var client = new ThreeHostProcessClient(startInfo);
        ThreeAuthoringResponse response = client.RoundTrip(request, CancellationToken.None);
        if (response.Succeeded && response.Manifest is { } manifest && manifest.Artifacts.Count > 0)
        {
            _acceptanceHash = manifest.Artifacts[0].ContentHash;
            return _acceptanceHash;
        }

        // A2 cache hit: the response carries no manifest; the artifact identity lives in the
        // export directory's state sidecar.
        if (response.Succeeded)
        {
            string? cached = XEngine.Ai.ThreeFx.ThreeFxExportStateFile.TryRead(
                System.IO.Path.Combine(project.RootPath, DefaultExportDir.Replace('/', System.IO.Path.DirectorySeparatorChar)))
                ?.ArtifactSha256;
            if (cached != null)
            {
                _acceptanceHash = cached;
                return cached;
            }
        }

        string message = response.Message ?? response.ErrorCode ?? "unknown failure";
        throw new InvalidOperationException("Acceptance export failed: " + message);
    }
}
