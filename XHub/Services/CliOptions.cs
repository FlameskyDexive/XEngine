using System;
using System.IO;
using XHub.Infrastructure;
using XHub.Models;

namespace XHub.Services;

/// <summary>
/// XHub command line (planning doc §4.3): file association (<c>--open</c>), deep links
/// (<c>--page</c> / <c>--installation</c>), and automation seams used for acceptance
/// (<c>--data-dir</c>, <c>--screenshot</c>, <c>--lang</c>). Unknown args are logged, never fatal.
/// </summary>
public sealed class CliOptions
{
    public string? OpenPath { get; private set; }
    public HubPageId? Page { get; private set; }
    public string? InstallationId { get; private set; }
    public string? SearchSeed { get; private set; }
    public string? FeedOverride { get; private set; }
    public string? InstallVersion { get; private set; }
    public bool Modules { get; private set; }
    public bool RegisterFileAssociation { get; private set; }
    public string? DataDir { get; private set; }
    public string? ScreenshotPath { get; private set; }
    public int ScreenshotDelayMs { get; private set; } = 500;
    public HubLanguage? Language { get; private set; }
    public bool Verbose { get; private set; }
    public bool Activate { get; private set; }

    /// <summary>True when this launch carries a request worth forwarding to a running instance.</summary>
    public bool HasForwardableRequest =>
        OpenPath != null || Page.HasValue || InstallationId != null || SearchSeed != null || Modules || Activate;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--open" when i + 1 < args.Length:
                    options.OpenPath = args[++i];
                    break;
                case "--page" when i + 1 < args.Length:
                    options.Page = ParsePage(args[++i]);
                    break;
                case "--installation" when i + 1 < args.Length:
                    options.InstallationId = args[++i];
                    break;
                case "--search" when i + 1 < args.Length:
                    options.SearchSeed = args[++i];
                    break;
                case "--feed" when i + 1 < args.Length:
                    options.FeedOverride = args[++i];
                    break;
                case "--install" when i + 1 < args.Length:
                    options.InstallVersion = args[++i];
                    break;
                case "--modules":
                    options.Modules = true;
                    break;
                case "--register-file-association":
                    options.RegisterFileAssociation = true;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    options.DataDir = args[++i];
                    break;
                case "--screenshot" when i + 1 < args.Length:
                    options.ScreenshotPath = args[++i];
                    break;
                case "--delay-ms" when i + 1 < args.Length && int.TryParse(args[++i], out int delay):
                    options.ScreenshotDelayMs = Math.Clamp(delay, 0, 60_000);
                    break;
                case "--lang" when i + 1 < args.Length:
                    options.Language = ParseLanguage(args[++i]);
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                case "--activate":
                    options.Activate = true;
                    break;
                case "--version":
                    Console.WriteLine(VersionInfo.DisplayVersion);
                    Environment.Exit(0);
                    break;
                default:
                    // File-association launches may pass the document path bare (argv[1]) —
                    // treat any .xengine file / existing directory as --open (§5).
                    if (!arg.StartsWith('-') && (arg.EndsWith(".xengine", StringComparison.OrdinalIgnoreCase) || Directory.Exists(arg)))
                    {
                        options.OpenPath ??= arg;
                        break;
                    }
                    HubLog.Warning($"Ignored unrecognized argument: {arg}");
                    break;
            }
        }
        return options;
    }

    private static HubPageId ParsePage(string value) => value.Trim().ToLowerInvariant() switch
    {
        "projects" or "project" or "项目" => HubPageId.Projects,
        "installations" or "versions" or "installers" or "engines" => HubPageId.Installations,
        "templates" or "template" => HubPageId.Templates,
        "downloads" or "download" or "tasks" => HubPageId.Downloads,
        "settings" or "设置" => HubPageId.Settings,
        _ => HubPageId.Projects,
    };

    private static HubLanguage ParseLanguage(string value) => value.Trim().ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "zh-hans" or "chinese" => HubLanguage.SimplifiedChinese,
        "en" or "en-us" or "english" => HubLanguage.English,
        _ => HubLanguage.System,
    };
}

/// <summary>Primary navigation ids shared by the shell, CLI deep links and IPC forwarding.</summary>
public enum HubPageId
{
    Projects = 0,
    Installations = 1,
    Templates = 2,
    Downloads = 3,
    Settings = 4,
}

public static class VersionInfo
{
    public const string DisplayVersion = "XHub 0.1.0";
}
