using System;
using System.IO;

using XEngine.Async;
using XEngine.Editor.Core.BugReport;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI.Popups;

/// <summary>Data <see cref="CrashHandler"/> injects into the dialog when it prompts about a crash.</summary>
public sealed class BugReportPrefill
{
    public string Title = "";
    public int CategoryIndex;
    public string Description = "";
    public string? CrashFilePath;
}

/// <summary>
/// Unity-style bug reporter modal (Help/Report a Bug). Collects a title, category,
/// description and optional contact email, attaches recent logs plus a game-view screenshot,
/// and mails the report via SMTP. A local copy is always written to Logs/BugReports/ first,
/// so a failed send never loses the report. SMTP credentials live in EditorSettings.
/// </summary>
public static class BugReporterDialog
{
    public const int CategoryBug = 0;
    public const int CategoryCrash = 1;
    public const int CategoryPerformance = 2;
    public const int CategoryFeature = 3;
    public const int CategoryOther = 4;

    // Per-open state reset each time Open() is called
    private static DialogModal? s_modal;
    private static string s_title = "";
    private static int s_category = CategoryBug;
    private static string s_description = "";
    private static string s_email = "";
    private static bool s_attachLogs = true;
    private static bool s_attachScreenshot = true;
    private static string? s_crashFile;
    private static bool s_sending;
    private static string s_error = "";
    private static string s_sysInfoPreview = "";

    // Inline SMTP settings (persisted to EditorSettings on Save)
    private static bool s_smtpExpanded;
    private static string s_smtpHost = "";
    private static string s_smtpPort = "587";
    private static bool s_smtpSsl = true;
    private static string s_smtpUser = "";
    private static string s_smtpPass = "";
    private static string s_smtpTo = "";

    public static void Open(BugReportPrefill? prefill = null)
    {
        s_title = prefill?.Title ?? "";
        s_category = prefill?.CategoryIndex ?? CategoryBug;
        s_description = prefill?.Description ?? "";
        s_crashFile = prefill?.CrashFilePath;
        s_email = EditorSettings.Instance.BugReportUserEmail;
        s_attachLogs = true;
        s_attachScreenshot = true;
        s_sending = false;
        s_error = "";
        s_sysInfoPreview = BugReportBuilder.BuildSystemInfo();

        LoadSmtpEditState();
        s_smtpExpanded = !BugReportMailer.IsConfigured(EditorSettings.Instance);

        s_modal = new DialogModal
        {
            Title = Loc.Get("bugreport.title"),
            DrawContent = DrawContent,
            Width = 560,
            Icon = EditorIcons.Bug_I,
        };
        Modal.Push(s_modal);
    }

    private static string[] CategoryLabels() =>
    [
        Loc.Get("bugreport.cat.bug"),
        Loc.Get("bugreport.cat.crash"),
        Loc.Get("bugreport.cat.performance"),
        Loc.Get("bugreport.cat.feature"),
        Loc.Get("bugreport.cat.other"),
    ];

    // ── Form ─────────────────────────────────────────────────────────

    private static void DrawContent(Paper paper)
    {
        var font = EditorTheme.DefaultFont;

        using (paper.Column("bug_body").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                   .Padding(14, 14, 4, 10).RowBetween(8).Enter())
        {
            EditorGUI.Row(paper, "bug_title", Loc.Get("bugreport.title_field"), () =>
                Origami.TextField(paper, "bug_title_f", s_title, v => s_title = v)
                    .Placeholder(Loc.Get("bugreport.title_ph")).Width(UnitValue.Stretch()).Show());

            EditorGUI.Row(paper, "bug_cat", Loc.Get("bugreport.category"), () =>
                Origami.Dropdown(paper, "bug_cat_f", s_category, idx => s_category = idx, CategoryLabels())
                    .Width(220).Show());

            EditorGUI.Row(paper, "bug_email", Loc.Get("bugreport.email"), () =>
                Origami.TextField(paper, "bug_email_f", s_email, v => s_email = v)
                    .Placeholder(Loc.Get("bugreport.email_ph")).Width(UnitValue.Stretch()).Show());

            Origami.TextArea(paper, "bug_desc", s_description, v => s_description = v, rows: 8)
                .Placeholder(Loc.Get("bugreport.description_ph")).Width(UnitValue.Stretch()).Show();

            // Attachment checkboxes; the crash record note appears when prefilled.
            using (paper.Row("bug_attach").Width(UnitValue.Stretch()).Height(EditorTheme.RowHeight).RowBetween(18).Enter())
            {
                Origami.Checkbox(paper, "bug_logs", s_attachLogs, v => s_attachLogs = v)
                    .LabelRight(Loc.Get("bugreport.attach_logs")).Show();
                Origami.Checkbox(paper, "bug_shot", s_attachScreenshot, v => s_attachScreenshot = v)
                    .LabelRight(Loc.Get("bugreport.attach_screenshot")).Show();
            }

            if (s_crashFile != null && font != null)
            {
                paper.Box("bug_crash")
                    .Width(UnitValue.Stretch()).Height(18)
                    .Text(Loc.Get("bugreport.attach_crash", new { file = Path.GetFileName(s_crashFile) }), font)
                    .TextColor(EditorTheme.Amber400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft);
            }

            // Read-only system info preview (what the report will contain).
            Origami.Foldout(paper, "bug_sys", Loc.Get("bugreport.sysinfo"))
                .Body(() =>
                {
                    if (font != null)
                        paper.Box("bug_sys_txt")
                            .Width(UnitValue.Stretch()).Height(UnitValue.Auto).MinHeight(40)
                            .Text(s_sysInfoPreview, font)
                            .TextColor(EditorTheme.Ink300)
                            .FontSize(EditorTheme.FontSizeSmall)
                            .Alignment(TextAlignment.Left)
                            .Wrap(Scribe.TextWrapMode.Wrap);
                });

            DrawSmtpSection(paper, font);

            // Error / status hint. Always reserve the row so the layout doesn't jump.
            if (font != null && s_error.Length > 0)
            {
                paper.Box("bug_err")
                    .Width(UnitValue.Stretch()).Height(UnitValue.Auto).MinHeight(18)
                    .Text(s_error, font)
                    .TextColor(EditorTheme.Red400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.Left)
                    .Wrap(Scribe.TextWrapMode.Wrap);
            }
        }

        // Footer buttons right-aligned Cancel + Send. While sending, Send becomes a
        // visually-disabled "Sending..." box so the click can't re-enter.
        using (paper.Row("bug_btns").Height(EditorTheme.RowHeight)
                   .ChildLeft(UnitValue.Stretch()).RowBetween(8).Enter())
        {
            Origami.Button(paper, "bug_cancel", Loc.Get("common.cancel"), () => Close()).Width(100).Show();

            if (s_sending)
            {
                using (paper.Box("bug_send_busy")
                           .Width(110).Height(EditorTheme.RowHeight).Rounded(3)
                           .BackgroundColor(EditorTheme.Neutral200)
                           .BorderWidth(1).BorderColor(EditorTheme.Neutral100).Enter())
                {
                    if (font != null)
                        paper.Box("bug_send_busy_lbl")
                            .Width(UnitValue.Stretch()).Height(EditorTheme.RowHeight)
                            .Text(Loc.Get("bugreport.sending"), font)
                            .TextColor(EditorTheme.Ink300)
                            .FontSize(EditorTheme.FontSize)
                            .Alignment(TextAlignment.MiddleCenter);
                }
            }
            else
            {
                Origami.Button(paper, "bug_send", Loc.Get("bugreport.send"), DoSend)
                    .Primary().Width(110).Show();
            }
        }
    }

    // ── SMTP settings ────────────────────────────────────────────────

    private static void DrawSmtpSection(Paper paper, Scribe.FontFile? font)
    {
        bool configured = BugReportMailer.IsConfigured(EditorSettings.Instance);
        string header = configured
            ? Loc.Get("bugreport.smtp.title")
            : Loc.Get("bugreport.smtp.title") + " — " + Loc.Get("bugreport.smtp.unconfigured");

        Origami.Foldout(paper, "bug_smtp", header)
            .DefaultExpanded(s_smtpExpanded)
            .Body(() =>
            {
                using (paper.Column("bug_smtp_body").Width(UnitValue.Stretch()).RowBetween(6).Enter())
                {
                    EditorGUI.Row(paper, "bug_smtp_host", Loc.Get("bugreport.smtp.host"), () =>
                        Origami.TextField(paper, "bug_smtp_host_f", s_smtpHost, v => s_smtpHost = v)
                            .Width(UnitValue.Stretch()).Show());
                    EditorGUI.Row(paper, "bug_smtp_port", Loc.Get("bugreport.smtp.port"), () =>
                        Origami.TextField(paper, "bug_smtp_port_f", s_smtpPort, v => s_smtpPort = v)
                            .Width(100).Show());
                    Origami.Checkbox(paper, "bug_smtp_ssl", s_smtpSsl, v => s_smtpSsl = v)
                        .LabelRight(Loc.Get("bugreport.smtp.ssl")).Show();
                    EditorGUI.Row(paper, "bug_smtp_user", Loc.Get("bugreport.smtp.user"), () =>
                        Origami.TextField(paper, "bug_smtp_user_f", s_smtpUser, v => s_smtpUser = v)
                            .Width(UnitValue.Stretch()).Show());
                    EditorGUI.Row(paper, "bug_smtp_pass", Loc.Get("bugreport.smtp.password"), () =>
                        Origami.PasswordField(paper, "bug_smtp_pass_f", s_smtpPass, v => s_smtpPass = v)
                            .Width(UnitValue.Stretch()).Show());
                    EditorGUI.Row(paper, "bug_smtp_to", Loc.Get("bugreport.smtp.to"), () =>
                        Origami.TextField(paper, "bug_smtp_to_f", s_smtpTo, v => s_smtpTo = v)
                            .Width(UnitValue.Stretch()).Show());

                    Origami.Button(paper, "bug_smtp_save", Loc.Get("bugreport.smtp.save"), SaveSmtpSettings)
                        .Primary().Width(100).Show();

                    if (font != null)
                    {
                        paper.Box("bug_smtp_hint")
                            .Width(UnitValue.Stretch()).Height(UnitValue.Auto).MinHeight(16)
                            .Text(Loc.Get("bugreport.smtp.hint"), font)
                            .TextColor(EditorTheme.Ink300)
                            .FontSize(EditorTheme.FontSizeSmall)
                            .Alignment(TextAlignment.Left)
                            .Wrap(Scribe.TextWrapMode.Wrap);
                    }
                }
            });
    }

    private static void LoadSmtpEditState()
    {
        var s = EditorSettings.Instance;
        s_smtpHost = s.BugReportSmtpHost;
        s_smtpPort = s.BugReportSmtpPort.ToString();
        s_smtpSsl = s.BugReportSmtpEnableSsl;
        s_smtpUser = s.BugReportSmtpUserName;
        s_smtpPass = s.BugReportSmtpPassword;
        s_smtpTo = s.BugReportToAddress;
    }

    private static void SaveSmtpSettings()
    {
        var s = EditorSettings.Instance;
        s.BugReportSmtpHost = s_smtpHost.Trim();
        s.BugReportSmtpPort = int.TryParse(s_smtpPort.Trim(), out int port) && port > 0 ? port : 587;
        s.BugReportSmtpEnableSsl = s_smtpSsl;
        s.BugReportSmtpUserName = s_smtpUser.Trim();
        s.BugReportSmtpPassword = s_smtpPass;
        if (!string.IsNullOrWhiteSpace(s_smtpTo.Trim()))
            s.BugReportToAddress = s_smtpTo.Trim();
        s.Save();
        s_error = "";
    }

    // ── Send flow ────────────────────────────────────────────────────

    private static void DoSend()
    {
        if (s_sending) return;

        if (string.IsNullOrWhiteSpace(s_title) || string.IsNullOrWhiteSpace(s_description))
        {
            s_error = Loc.Get("bugreport.err_required");
            return;
        }

        var settings = EditorSettings.Instance;
        if (!BugReportMailer.IsConfigured(settings))
        {
            s_smtpExpanded = true;
            s_error = Loc.Get("bugreport.err_smtp");
            return;
        }

        s_sending = true;
        s_error = "";

        settings.BugReportUserEmail = s_email.Trim();
        settings.Save();

        var payload = new BugReportPayload
        {
            Title = s_title.Trim(),
            Category = CategoryLabels()[s_category],
            Description = s_description.Trim(),
            UserEmail = s_email.Trim(),
            AttachLogs = s_attachLogs,
            AttachScreenshot = s_attachScreenshot,
            CrashFilePath = s_crashFile,
        };

        string bundleDir;
        string logsPath;
        string? screenshotPath;
        try
        {
            // Main-thread capture (GPU readback) and the local copy that always survives.
            bundleDir = BugReportBuilder.WriteBundle(payload, out logsPath, out screenshotPath);
        }
        catch (Exception ex)
        {
            s_sending = false;
            s_error = Loc.Get("bugreport.bundle_failed", new { error = ex.Message });
            return;
        }

        BugReportMailer.SendAsync(payload, bundleDir, logsPath, screenshotPath, (ok, message) => OnSendDone(ok, message, bundleDir))
            .Forget();
    }

    private static void OnSendDone(bool ok, string message, string bundleDir)
    {
        s_sending = false;
        if (ok)
        {
            Close();
            Toasts.Success(Loc.Get("bugreport.sent"), Loc.Get("bugreport.sent_msg"));
            return;
        }

        s_error = Loc.Get("bugreport.send_failed", new { error = message })
                  + "\n" + Loc.Get("bugreport.saved_local", new { path = bundleDir });
        Toasts.Error(Loc.Get("bugreport.send_failed_title"), message);
    }

    private static void Close()
    {
        if (s_modal != null) Modal.Remove(s_modal);
        else Modal.Pop();
        s_modal = null;
    }
}
