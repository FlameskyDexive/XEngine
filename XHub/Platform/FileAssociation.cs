using System;
using System.Runtime.Versioning;
using XHub.Infrastructure;

namespace XHub.Platform;

/// <summary>
/// Registers the <c>.xengine</c> file association per-user (HKCU, no elevation) — §5.
/// The Inno Setup installer performs the same registration at install time; this command
/// covers zip/manual installs: <c>XHub.exe --register-file-association</c>.
/// </summary>
public static class FileAssociation
{
    private const string ProgId = "XEngine.Project";
    private const string Extension = ".xengine";

    /// <summary>Writes HKCU keys pointing at the running executable. Returns the command written.</summary>
    [SupportedOSPlatform("windows")]
    public static string Register()
    {
        string exePath = Environment.ProcessPath
            ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        string command = $"\"{exePath}\" --open \"%1\"";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Extension))
            key.SetValue(null, ProgId);

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            key.SetValue(null, "XEngine Project");

        using (var iconKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + @"\DefaultIcon"))
            iconKey.SetValue(null, $"\"{exePath}\",0");

        using (var openKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command"))
            openKey.SetValue(null, command);

        HubLog.Info($".xengine file association registered: {command}");
        return command;
    }
}
