using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using XEngine.Editor.Projects;
using XEngine.Rosetta;
using XEngine.Runtime;

namespace XEngine.Editor.Core.BugReport;

/// <summary>
/// Last-resort crash capture for the editor. <see cref="AppDomain.UnhandledException"/> fires
/// while the process is terminating, so the handler only writes a crash record to disk — no UI,
/// no mail (both need the frame loop that will never run again). On the next editor start,
/// <see cref="PromptUnreportedCrashes"/> picks the record up and opens the Bug Reporter
/// prefilled with it, which is the reliable equivalent of Unity's post-crash reporter.
/// </summary>
public static class CrashHandler
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Observed late, GC-finalizer time; process survives — log only.
            Debug.LogWarning($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    /// <summary>Crash records live with the project when one is open, else under LocalAppData.</summary>
    private static string CrashesDirectory(Project? project)
        => project?.LogsPath is { } logs
            ? Path.Combine(logs, "Crashes")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XEngine", "Crashes");

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Best effort, nothing more: file I/O only, every step guarded so a failure in the
        // handler never masks the original crash.
        try
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown");
            string path = WriteCrashRecord(ex, Project.Current, e.IsTerminating);
            ShowCrashDialog(ex, path);
        }
        catch
        {
            // Nothing safe left to do while the process tears down.
        }
    }

    /// <summary>
    /// Unity-style crash dialog: the editor is dying anyway, so a blocking native MessageBox is
    /// the one UI that always works (no message loop / renderer needed). Yes opens the full
    /// report in the default text editor; No just lets the process exit.
    /// </summary>
    private static void ShowCrashDialog(Exception ex, string reportPath)
    {
        if (!OperatingSystem.IsWindows()) return;

        const uint mbIconError = 0x10u, mbYesNo = 0x4u;
        string text =
            "XEngine Editor 遇到致命错误，即将退出。\n\n"
            + $"{ex.GetType().Name}: {ex.Message}\n\n"
            + "完整报错日志已写入：\n" + reportPath + "\n\n"
            + "是否打开这份报错日志？";
        string caption = "XEngine Editor";

        int choice = MessageBoxW(IntPtr.Zero, text, caption, mbYesNo | mbIconError);
        if (choice == 6) // IDYES
        {
            try { System.Diagnostics.Process.Start("notepad.exe", $"\"{reportPath}\""); }
            catch { }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Writes Logs/Crashes/crash-&lt;timestamp&gt;.txt synchronously. Returns the path.</summary>
    internal static string WriteCrashRecord(Exception ex, Project? project, bool isTerminating)
    {
        string dir = CrashesDirectory(project);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var sb = new StringBuilder();
        sb.AppendLine($"Time:        {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        sb.AppendLine($"Terminating: {isTerminating}");
        sb.AppendLine($"Exception:   {ex.GetType().FullName}");
        sb.AppendLine($"Message:     {ex.Message}");
        sb.AppendLine("Stack:");
        sb.AppendLine(ex.StackTrace ?? "(no stack trace)");
        sb.AppendLine();

        try
        {
            sb.AppendLine("---- System info ----");
            sb.AppendLine(BugReportBuilder.BuildSystemInfo());
            sb.AppendLine("---- Recent logs ----");
            sb.AppendLine(GUI.Panels.ConsolePanel.DumpRecentLogs());
        }
        catch
        {
            // Log store/SystemInfo are shared with the main thread; if we can't read them
            // while it is dying, the record above is still valuable.
        }

        try
        {
            // Terminal Vulkan device loss: the runtime captured VK_EXT_device_fault details right
            // before this exception propagated. Persist the vendor binary next to this record so
            // it can be decoded offline (e.g. NVIDIA crash dump parser).
            string faultSummary = XEngine.Runtime.Backends.Vulkan.VulkanGraphicsDevice.LastDeviceFaultSummary;
            byte[]? faultBlob = XEngine.Runtime.Backends.Vulkan.VulkanGraphicsDevice.LastDeviceFaultVendorBlob;
            if (faultBlob is { Length: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("---- GPU device fault (VK_EXT_device_fault) ----");
                sb.AppendLine(faultSummary.Length > 0 ? faultSummary : "(no driver description)");
                string blobPath = Path.Combine(dir, $"devicefault-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
                File.WriteAllBytes(blobPath, faultBlob);
                sb.AppendLine($"Vendor binary ({faultBlob.Length} bytes) written to: {blobPath}");
            }
        }
        catch
        {
            // Diagnostics only; never mask the original crash.
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// After a project opens, look for crash records not yet surfaced. Opens the Bug Reporter
    /// prefilled with the newest one and marks every pending record immediately so a crash
    /// storm cannot queue one dialog per subsequent launch. Files stay on disk as <c>.reported.txt</c>
    /// logs after the dialog is dismissed.
    /// </summary>
    public static void PromptUnreportedCrashes()
    {
        try
        {
            if (!TryConsumeUnreportedCrashFiles(
                    new[] { CrashesDirectory(Project.Current), CrashesDirectory(null) },
                    out string? latestReported, out string? summary)
                || latestReported == null || summary == null)
                return;

            GUI.Popups.BugReporterDialog.Open(new GUI.Popups.BugReportPrefill
            {
                Title = Loc.Get("bugreport.crash.title"),
                CategoryIndex = GUI.Popups.BugReporterDialog.CategoryCrash,
                Description = Loc.Get("bugreport.crash.intro") + "\n\n" + summary,
                CrashFilePath = latestReported,
            });
        }
        catch
        {
            // Crash prompting is advisory; never block editor startup.
        }
    }

    /// <summary>
    /// Pending crash records whose names match <c>crash-yyyyMMdd-HHmmss.txt</c>.
    /// <c>Directory.GetFiles(..., "crash-*.txt")</c> also returns <c>.reported.txt</c> files
    /// (the glob treats <c>*</c> as matching <c>20260822-014213.reported</c>); those are skipped.
    /// Sorted oldest-to-newest by filename (not full path) so two crash directories cannot
    /// invert recency when drive letters sort differently.
    /// </summary>
    internal static string[] FindUnreportedCrashFiles(params string[] directories)
    {
        var found = new List<string>();
        foreach (string dir in directories)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (string file in Directory.GetFiles(dir, "crash-*.txt"))
            {
                if (IsReportedCrashFile(file)) continue;
                found.Add(file);
            }
        }

        found.Sort(static (a, b) =>
            string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        return found.ToArray();
    }

    internal static bool IsReportedCrashFile(string path)
        => path.EndsWith(".reported.txt", StringComparison.OrdinalIgnoreCase);

    internal static string ReportedPath(string crashPath)
        => Path.ChangeExtension(crashPath, ".reported.txt");

    /// <summary>
    /// Surfaces the newest pending crash and marks the entire backlog reported in one go.
    /// Returns false when nothing is pending.
    /// </summary>
    internal static bool TryConsumeUnreportedCrashFiles(
        IReadOnlyList<string> directories, out string? latestReported, out string? summary)
    {
        latestReported = null;
        summary = null;

        string[] dirs = directories as string[] ?? CopyDirectories(directories);
        string[] pending = FindUnreportedCrashFiles(dirs);
        if (pending.Length == 0) return false;

        string latest = pending[^1];
        summary = CrashSummary(latest);

        // Mark older records first so a failure mid-loop still re-prompts the newest next time.
        for (int i = 0; i < pending.Length - 1; i++)
            MarkReported(pending[i]);
        latestReported = MarkReported(latest);
        return true;
    }

    private static string[] CopyDirectories(IReadOnlyList<string> directories)
    {
        var copy = new string[directories.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = directories[i];
        return copy;
    }

    /// <summary>The exception header of a crash record (everything above the first ---- section).</summary>
    private static string CrashSummary(string path)
    {
        string[] lines = File.ReadAllLines(path);
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            if (line.StartsWith("----", StringComparison.Ordinal)) break;
            sb.AppendLine(line);
            if (sb.Length > 2000) break;
        }
        return sb.ToString().TrimEnd();
    }

    internal static string MarkReported(string path)
    {
        string reported = ReportedPath(path);
        if (File.Exists(reported)) File.Delete(reported);
        File.Move(path, reported);
        return reported;
    }

    /// <summary>Diagnostics: raises an unhandled exception on the main thread so the crash
    /// path (record + next-start prompt) can be verified end to end. The editor will exit.</summary>
    public static void ThrowTestException()
        => throw new InvalidOperationException("XEngine crash-handler test exception (Help/Diagnostics). This crash is intentional.");
}
