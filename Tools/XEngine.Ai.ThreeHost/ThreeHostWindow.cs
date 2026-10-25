using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using XEngine.Ai.Protocol;
using WinFormsWebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace XEngine.Ai.ThreeHost;

/// <summary>
/// The WebView2 authoring window. A plain top-level STA window: the editor owns it through
/// <c>GWLP_HWNDPARENT</c> (never SetParent), so it stays above the editor like every other
/// editor-owned HWND. DPI is per-monitor-v2 and the window is clamped into the monitor work
/// area so the page can never be permanently dragged off-screen (裁剪 acceptance).
/// </summary>
internal sealed class ThreeHostWindow : Form
{
    public const string VirtualHostName = "threehost.appassets.local";

    private readonly ThreeHostOptions _options;
    private readonly WinFormsWebView2 _webView = new();
    private CoreWebView2Environment? _environment;
    private bool _pageReady;
    private string _status = "starting";
    private string _threeRevision = "unknown";

    public ThreeHostWindow(ThreeHostOptions options)
    {
        _options = options;
        Text = "XEngine Three.js Authoring Host";
        StartPosition = FormStartPosition.Manual;
        ClientSize = new System.Drawing.Size(options.Width, options.Height);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        ShowInTaskbar = true;

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);
    }

    /// <summary>Raised once the WebView2 page signalled readiness.</summary>
    public event Action? PageReady;

    public bool IsPageReady => _pageReady;

    public string ThreeRevision => _threeRevision;

    /// <summary>The live CoreWebView2 (null before init / after a process failure).</summary>
    public CoreWebView2? Core => _webView.CoreWebView2;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AssignOwner();
        _ = InitializeWebViewAsync();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AssignOwner(); // The owner can be dropped when the window becomes visible; reassert.
        ClampIntoWorkArea();
    }

    /// <summary>Runs an operation on the UI thread; the WebView2 API is UI-affine.</summary>
    public Task<T> RunOnUIThreadAsync<T>(Func<Task<T>> operation)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(async () =>
        {
            try
            {
                tcs.SetResult(await operation().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public void SetStatus(string status)
    {
        _status = status;
        UpdateTitle();
    }

    private void UpdateTitle() =>
        Text = $"XEngine Three.js Authoring Host — {ThreeAuthoringProtocol.Version} "
            + $"[{_status}] (three r{_threeRevision}, dpi {DeviceDpi})";

    private async Task InitializeWebViewAsync()
    {
        try
        {
            _environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "xengine-three-host", "WebView2"))
                .ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);

            CoreWebView2 core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = true;

            core.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                _options.PageRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;
            core.NavigationCompleted += (_, args) =>
            {
                Console.Error.WriteLine(
                    $"host-trace: navigation completed success={args.IsSuccess} status={args.WebErrorStatus}");
                if (!args.IsSuccess)
                    SetStatus("navigation-failed: " + args.WebErrorStatus);
            };

            SetStatus("loading-page");
            core.Navigate($"https://{VirtualHostName}/index.html");
        }
        catch (Exception ex)
        {
            SetStatus("webview-init-failed");
            Console.Error.WriteLine($"webview_init_failed: {ex.Message}");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        PageMessage? message = JsonSerialization.Deserialize<PageMessage>(e.WebMessageAsJson);
        if (message is null)
            return;

        switch (message.Type)
        {
            case "ready":
                _threeRevision = message.ThreeRevision ?? "unknown";
                _pageReady = true;
                SetStatus("ready");
                PageReady?.Invoke();
                break;
            case "log":
                Console.Error.WriteLine($"page_log: {message.Message}");
                break;
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // Renderer/browser death is a first-class diagnostic: the in-flight request fails with a
        // typed code and the next operation re-creates the WebView2 environment.
        _pageReady = false;
        _environment = null;
        SetStatus("process-failed:" + e.ProcessFailedKind);
        Console.Error.WriteLine(
            $"webview_process_failed: {e.ProcessFailedKind} exit={e.ExitCode}");
    }

    private void AssignOwner()
    {
        if (_options.OwnerHwnd == 0)
            return;

        nint owner = new nint(_options.OwnerHwnd);
        if (!EditorNativeWindowOwner.CanAssignOwner(Handle, owner))
            return;
        SetWindowLongPtr(Handle, GwlpHwndParent, owner);
    }

    private void ClampIntoWorkArea()
    {
        System.Drawing.Rectangle workArea = Screen.GetWorkingArea(this);
        System.Drawing.Size size = Size;
        int x = Math.Min(Bounds.X, workArea.Right - Math.Min(size.Width, workArea.Width));
        int y = Math.Min(Bounds.Y, workArea.Bottom - Math.Min(size.Height, workArea.Height));
        x = Math.Max(x, workArea.Left);
        y = Math.Max(y, workArea.Top);
        if (x != Bounds.X || y != Bounds.Y)
            Location = new System.Drawing.Point(x, y);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ClampIntoWorkArea();
        UpdateTitle();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Any in-flight request will be failed by the server teardown; a user close is a normal
        // shutdown path, not a crash.
        SetStatus("closing");
        base.OnFormClosing(e);
    }

    private sealed record PageMessage(string? Type, string? ThreeRevision, string? Message);

    private const int GwlpHwndParent = -8;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}

/// <summary>Win32 owner helpers for the host window (mirror of the editor's convention).</summary>
internal static class EditorNativeWindowOwner
{
    public static bool CanAssignOwner(nint child, nint owner) =>
        child != nint.Zero && owner != nint.Zero && child != owner;
}
