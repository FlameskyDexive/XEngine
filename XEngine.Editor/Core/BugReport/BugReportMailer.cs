using System;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Threading;

using XEngine.Async;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;

namespace XEngine.Editor.Core.BugReport;

/// <summary>
/// SMTP delivery for bug reports (System.Net.Mail — in-box on net10.0, no extra package).
/// Network I/O runs on the thread pool via XTask; the completion callback runs on the
/// main thread so the dialog can update its UI directly.
/// </summary>
public static class BugReportMailer
{
    private const int SendTimeoutMs = 30_000;

    public static bool IsConfigured(EditorSettings s)
        => !string.IsNullOrWhiteSpace(s.BugReportSmtpHost)
           && !string.IsNullOrWhiteSpace(s.BugReportSmtpUserName)
           && !string.IsNullOrWhiteSpace(s.BugReportSmtpPassword);

    /// <summary>
    /// Sends the bundle files as attachments. <paramref name="onDone"/> fires on the main
    /// thread with (success, technical message). Call from the main thread; all file paths
    /// must already exist (see <see cref="BugReportBuilder.WriteBundle"/>).
    /// </summary>
    public static async XTaskVoid SendAsync(
        BugReportPayload payload,
        string bundleDirectory,
        string logsPath,
        string? screenshotPath,
        Action<bool, string> onDone)
    {
        bool ok = false;
        string result = "";

        var cts = new CancellationTokenSource();
        cts.CancelAfterSlim(SendTimeoutMs);
        try
        {
            await XTask.SwitchToThreadPool();

            var settings = EditorSettings.Instance;
            using var message = BuildMessage(payload, logsPath, screenshotPath, settings);
            using var smtp = new SmtpClient(settings.BugReportSmtpHost, settings.BugReportSmtpPort)
            {
                EnableSsl = settings.BugReportSmtpEnableSsl,
                Credentials = new NetworkCredential(settings.BugReportSmtpUserName, settings.BugReportSmtpPassword),
                Timeout = SendTimeoutMs - 2_000,
            };

            await smtp.SendMailAsync(message, cts.Token).AsXTask();
            ok = true;
        }
        catch (OperationCanceledException)
        {
            result = $"SMTP timed out after {SendTimeoutMs / 1000}s";
        }
        catch (Exception ex)
        {
            result = ex.InnerException?.Message ?? ex.Message;
        }
        finally
        {
            cts.Dispose();
        }

        await XTask.SwitchToMainThread();
        onDone(ok, result);
    }

    private static MailMessage BuildMessage(BugReportPayload payload, string logsPath,
        string? screenshotPath, EditorSettings settings)
    {
        var message = new MailMessage
        {
            From = new MailAddress(
                string.IsNullOrWhiteSpace(settings.BugReportFromAddress)
                    ? settings.BugReportSmtpUserName
                    : settings.BugReportFromAddress,
                "XEngine Bug Reporter"),
            Subject = $"[XEngine][{payload.Category}]"
                      + (Project.Current != null ? $"[{Project.Current.Name}]" : "")
                      + $" {payload.Title}",
            Body = BugReportBuilder.BuildReportText(payload),
        };
        message.To.Add(settings.BugReportToAddress);

        if (!string.IsNullOrWhiteSpace(payload.UserEmail))
        {
            try { message.ReplyToList.Add(new MailAddress(payload.UserEmail.Trim())); }
            catch (FormatException) { /* not a mail address - skip rather than fail the report */ }
        }

        if (payload.AttachLogs && File.Exists(logsPath))
            message.Attachments.Add(new Attachment(logsPath));
        if (screenshotPath != null && File.Exists(screenshotPath))
            message.Attachments.Add(new Attachment(screenshotPath));
        if (payload.CrashFilePath != null && File.Exists(payload.CrashFilePath))
            message.Attachments.Add(new Attachment(payload.CrashFilePath));

        return message;
    }
}
