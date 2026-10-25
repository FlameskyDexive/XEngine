using System;
using System.Collections.Generic;
using System.ComponentModel;
using XHub.Models;

namespace XHub.Resources;

/// <summary>
/// Runtime localization for XHub (zh-CN / en-US). Bindings go through the indexer
/// (<c>{Binding [nav.projects], Source={x:Static loc:Loc.Instance}}</c>); switching the language
/// raises an all-properties change so every bound label refreshes in place.
/// Keys are shared between both dictionaries — the test suite enforces parity so a missing
/// translation fails the build instead of leaking raw keys into the UI.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    // Field order matters: the dictionaries must initialize before Instance, whose ctor reads them.
    private static readonly Dictionary<string, string> Chinese = new()
    {
        // ── Shell / navigation ─────────────────────────────────────
        ["app.name"] = "XHub",
        ["nav.projects"] = "项目",
        ["nav.installations"] = "引擎版本",
        ["nav.templates"] = "模板",
        ["nav.downloads"] = "下载任务",
        ["nav.settings"] = "设置",

        // ── Common ─────────────────────────────────────────────────
        ["common.open"] = "打开",
        ["common.cancel"] = "取消",
        ["common.close"] = "关闭",
        ["common.ok"] = "确定",
        ["common.remove"] = "移除",
        ["common.browse"] = "浏览…",
        ["common.search"] = "搜索",
        ["common.language"] = "语言",
        ["common.system"] = "跟随系统",

        // ── Modules / toolchains (N7) ──────────────────────────────
        ["installations.modules"] = "管理模块…",
        ["modules.title"] = "管理模块",
        ["modules.install"] = "安装",
        ["modules.platform.android"] = "Android",
        ["modules.platform.harmonyos"] = "HarmonyOS",
        ["modules.platform.ios"] = "iOS",
        ["modules.status.notDetected"] = "未检测到",
        ["modules.status.installing"] = "安装中…",
        ["modules.status.installed"] = "已安装",
        ["modules.status.failed"] = "失败",
        ["modules.status.licenseRequired"] = "需要先阅读并接受 Android SDK 许可条款",
        ["modules.android.license"] = "我已阅读并接受 Google Android SDK 许可条款（developer.android.com/studio/terms）",
        ["modules.android.licenseHint"] = "许可需显式同意后才会运行 sdkmanager。",
        ["modules.harmonyos.importBundle"] = "导入本地 Bundle…",
        ["modules.ios.hostLimit"] = "iOS 工具链检测（Xcode）仅在 macOS 主机上可用。",
        ["modules.canceled"] = "已取消。",
        ["modules.diagnostics"] = "诊断信息",
        ["modules.diagnostics.refresh"] = "刷新",

        // ── Projects page ──────────────────────────────────────────
        ["projects.title"] = "项目",
        ["projects.new"] = "新建项目",
        ["projects.add"] = "添加项目",
        ["projects.addFile"] = "添加 .xengine",
        ["projects.col.name"] = "名称 / 路径",
        ["projects.col.version"] = "引擎版本",
        ["projects.col.opened"] = "最近打开",
        ["projects.col.status"] = "状态",
        ["projects.filter.all"] = "全部项目",
        ["projects.filter.usable"] = "可用",
        ["projects.filter.missing"] = "失效",
        ["projects.neverOpened"] = "从未打开",
        ["projects.status.available"] = "可用",
        ["projects.status.missing"] = "失效",
        ["projects.status.incomplete"] = "元数据不完整",
        ["projects.action.openFolder"] = "打开所在文件夹",
        ["projects.action.open"] = "打开",
        ["projects.action.retry"] = "重试打开（换一个版本）",
        ["projects.action.repair"] = "修复路径…",
        ["projects.action.remove"] = "从列表移除",
        ["projects.noMatch.title"] = "没有匹配的项目",
        ["projects.noMatch.hint"] = "调整搜索词或筛选条件，或添加新项目。",
        ["projects.match.missing"] = "未安装此版本",
        ["projects.match.unknownVersion"] = "版本未知",
        ["projects.match.noInstallations"] = "未安装引擎",
        ["projects.empty.title"] = "还没有项目",
        ["projects.empty.hint"] = "点击“添加项目”导入现有项目，或通过“新建项目”创建。",

        // ── Dialogs ────────────────────────────────────────────────
        ["dialog.error.title"] = "XHub",
        ["dialog.pickFolder.title"] = "选择项目文件夹",
        ["dialog.pickFile.title"] = "选择 .xengine 项目文件",
        ["dialog.pickEditor.title"] = "选择 XEngine.Editor 可执行文件",
        ["dialog.xengine.title"] = "选择项目标识文件",
        ["dialog.xengine.message"] = "该文件夹包含多个 .xengine 文件，无法唯一识别项目。请选择要使用的一个：",
        ["dialog.invalid.title"] = "不是有效的 XEngine 项目",
        ["dialog.invalid.hint"] = "所选路径不存在，或缺少 Assets 文件夹。",
        ["dialog.repairClash.title"] = "无法修复路径",
        ["dialog.repairClash.hint"] = "新路径已被另一个项目记录占用，请先移除重复记录。",
        ["dialog.invalidEditor.title"] = "未找到可用的编辑器",
        ["dialog.invalidEditor.hint"] = "所选位置不包含可运行的 XEngine.Editor。",
        ["launch.error.noMatch.title"] = "没有匹配的引擎版本",
        ["launch.error.startFailed.title"] = "无法启动编辑器",
        ["launch.error.brokenInstall"] = "所选引擎版本不可用（缺少编辑器程序）。",
        ["launch.error.startFailed"] = "编辑器进程启动失败，请查看日志。",
        ["session.launching"] = "启动中…",
        ["session.ready"] = "已打开",
        ["session.failed"] = "打开失败",
        ["session.unconfirmed"] = "已启动（旧版本，无法确认加载）",

        // ── Installations page ─────────────────────────────────────
        ["installations.title"] = "引擎版本",
        ["installations.scan"] = "重新扫描",
        ["installations.locate"] = "手动定位…",
        ["installations.col.version"] = "版本",
        ["installations.col.path"] = "安装路径",
        ["installations.col.arch"] = "架构",
        ["installations.col.source"] = "来源",
        ["installations.col.status"] = "状态",
        ["installations.defaultBadge"] = "默认版本",
        ["installations.setDefault"] = "设为默认",
        ["installations.openFolder"] = "定位目录",
        ["installations.remove"] = "移除记录",
        ["installations.channel.preview"] = "预览",
        ["installations.channel.stable"] = "稳定",
        ["installations.channel.unknown"] = "未知",
        ["installations.source.hub"] = "XHub 下载",
        ["installations.source.installer"] = "安装器",
        ["installations.source.manual"] = "手动定位",
        ["installations.source.source"] = "源码构建",
        ["installations.status.ok"] = "可用",
        ["installations.status.broken"] = "损坏",
        ["installations.version.unknown"] = "版本未知",
        ["installations.empty.title"] = "没有已安装的引擎版本",
        ["installations.empty.hint"] = "点击“手动定位”登记现有安装，或等待后续版本的在线下载。",

        // ── Templates page ─────────────────────────────────────────
        ["templates.title"] = "模板",
        ["templates.use"] = "使用此模板",
        ["template.universal3d.name"] = "Universal 3D",
        ["template.universal3d.description"] = "标准 3D 工程模板：默认场景、通用渲染管线资源与基础目录结构。",
        ["templates.empty.hint"] = "安装引擎版本后即可使用 Universal 3D 模板创建项目。",

        // ── New-project wizard ─────────────────────────────────────
        ["wizard.title"] = "新建项目",
        ["wizard.template.label"] = "模板",
        ["wizard.engine.label"] = "引擎版本",
        ["wizard.name.label"] = "项目名称",
        ["wizard.name.watermark"] = "例如 MyGame",
        ["wizard.parent.label"] = "父目录",
        ["wizard.create"] = "创建项目",
        ["wizard.error.nameRequired"] = "请输入项目名称。",
        ["wizard.error.nameInvalid"] = "项目名称包含无效字符。",
        ["wizard.error.parentRequired"] = "请选择父目录。",
        ["wizard.error.parentMissing"] = "父目录不存在。",
        ["wizard.error.targetExists"] = "目标位置已存在同名且非空的目录。",
        ["wizard.error.noInstallation"] = "请选择可用的引擎版本。",
        ["wizard.error.canceled"] = "创建已取消。",
        ["wizard.error.createFailed"] = "项目创建失败。",

        // ── Installations page (N6 additions) ──────────────────────
        ["installations.installed"] = "已安装",
        ["installations.installable"] = "可安装",
        ["installations.refreshFeed"] = "刷新列表",
        ["installations.install"] = "安装",
        ["installations.uninstall"] = "卸载",
        ["install.queued.title"] = "已加入下载队列",
        ["install.queued.hint"] = "下载与安装进度见“下载任务”页。",
        ["uninstall.error.title"] = "无法卸载",
        ["uninstall.error.notHubManaged"] = "仅管理 XHub 下载安装的版本；此版本由外部安装，请使用其自带卸载程序。",
        ["uninstall.error.inUse"] = "该版本正被运行中的编辑器使用，请先关闭。",
        ["uninstall.error.outsideRoot"] = "安装目录不在 XHub 管理目录内，已拒绝删除。",
        ["uninstall.error.deleteFailed"] = "目录删除失败，请检查文件占用。",
        ["install.error.diskSpace"] = "磁盘空间不足，无法安装。",
        ["install.error.layout"] = "安装包布局不符合预期（缺少编辑器程序）。",
        ["install.error.capability"] = "安装包无法报告能力信息（--hub-info 无响应）。",
        ["install.error.protocolTooOld"] = "安装包要求的 Hub 协议版本高于当前 XHub。",
        ["install.error.failed"] = "安装失败。",

        // ── Downloads page ─────────────────────────────────────────
        ["downloads.title"] = "下载任务",
        ["downloads.pause"] = "暂停",
        ["downloads.resume"] = "继续",
        ["downloads.cancel"] = "取消",
        ["downloads.retry"] = "重试",
        ["downloads.log"] = "日志",
        ["downloads.log.title"] = "操作日志",
        ["downloads.log.empty"] = "暂无日志。",
        ["downloads.state.queued"] = "排队中",
        ["downloads.state.downloading"] = "下载中",
        ["downloads.state.paused"] = "已暂停",
        ["downloads.state.completed"] = "下载完成",
        ["downloads.state.failed"] = "失败",
        ["downloads.state.canceled"] = "已取消",
        ["downloads.state.installing"] = "安装中",
        ["downloads.state.done"] = "已安装",
        ["download.error.hashMismatch"] = "SHA-256 校验失败，已丢弃损坏的下载。",
        ["downloads.empty.title"] = "暂无下载任务",
        ["downloads.empty.hint"] = "从“引擎版本”页发起的下载会显示在这里。",

        // ── Settings page ──────────────────────────────────────────
        ["settings.title"] = "设置",
        ["settings.general"] = "常规",
        ["settings.language"] = "界面语言",
        ["settings.language.hint"] = "立即生效，无需重启。",
        ["settings.locations"] = "安装位置",
        ["settings.locations.editors"] = "编辑器",
        ["settings.locations.downloads"] = "下载缓存",
        ["settings.locations.browse"] = "浏览…",
        ["settings.locations.reset"] = "恢复默认",
        ["settings.locations.save"] = "保存位置",
        ["settings.locations.hint"] = "留空使用默认位置；已安装的编辑器不会移动。",
        ["settings.locations.restart"] = "已保存，重启 XHub 后生效。",
        ["settings.locations.current"] = "当前生效位置：",
        ["settings.locations.error.title"] = "位置无效",
        ["settings.locations.error.editors"] = "编辑器安装位置必须是绝对路径，且不能是盘符根目录。",
        ["settings.locations.error.downloads"] = "下载缓存位置必须是绝对路径，且不能是盘符根目录。",
        ["settings.about"] = "关于",
        ["settings.logs"] = "日志目录",
        ["settings.logs.hint"] = "在资源管理器中打开 XHub 日志文件夹。",

        // ── Status bar ─────────────────────────────────────────────
        ["status.ready"] = "就绪",
    };

    private static readonly Dictionary<string, string> English = new()
    {
        // ── Shell / navigation ─────────────────────────────────────
        ["app.name"] = "XHub",
        ["nav.projects"] = "Projects",
        ["nav.installations"] = "Engine Versions",
        ["nav.templates"] = "Templates",
        ["nav.downloads"] = "Downloads",
        ["nav.settings"] = "Settings",

        // ── Common ─────────────────────────────────────────────────
        ["common.open"] = "Open",
        ["common.cancel"] = "Cancel",
        ["common.close"] = "Close",
        ["common.ok"] = "OK",
        ["common.remove"] = "Remove",
        ["common.browse"] = "Browse…",
        ["common.search"] = "Search",
        ["common.language"] = "Language",
        ["common.system"] = "Follow System",

        // ── Modules / toolchains (N7) ──────────────────────────────
        ["installations.modules"] = "Manage Modules…",
        ["modules.title"] = "Manage Modules",
        ["modules.install"] = "Install",
        ["modules.platform.android"] = "Android",
        ["modules.platform.harmonyos"] = "HarmonyOS",
        ["modules.platform.ios"] = "iOS",
        ["modules.status.notDetected"] = "Not detected",
        ["modules.status.installing"] = "Installing…",
        ["modules.status.installed"] = "Installed",
        ["modules.status.failed"] = "Failed",
        ["modules.status.licenseRequired"] = "Read and accept the Android SDK license terms first",
        ["modules.android.license"] = "I have read and accept the Google Android SDK license terms (developer.android.com/studio/terms)",
        ["modules.android.licenseHint"] = "sdkmanager runs only after explicit license consent.",
        ["modules.harmonyos.importBundle"] = "Import Local Bundle…",
        ["modules.ios.hostLimit"] = "iOS toolchain detection (Xcode) is only available on macOS hosts.",
        ["modules.canceled"] = "Canceled.",
        ["modules.diagnostics"] = "Diagnostics",
        ["modules.diagnostics.refresh"] = "Refresh",

        // ── Projects page ──────────────────────────────────────────
        ["projects.title"] = "Projects",
        ["projects.new"] = "New Project",
        ["projects.add"] = "Add Project",
        ["projects.addFile"] = "Add .xengine",
        ["projects.col.name"] = "Name / Path",
        ["projects.col.version"] = "Engine Version",
        ["projects.col.opened"] = "Last Opened",
        ["projects.col.status"] = "Status",
        ["projects.filter.all"] = "All Projects",
        ["projects.filter.usable"] = "Usable",
        ["projects.filter.missing"] = "Missing",
        ["projects.neverOpened"] = "Never opened",
        ["projects.status.available"] = "Available",
        ["projects.status.missing"] = "Missing",
        ["projects.status.incomplete"] = "Metadata incomplete",
        ["projects.action.openFolder"] = "Open Containing Folder",
        ["projects.action.open"] = "Open",
        ["projects.action.retry"] = "Retry Open (pick another version)",
        ["projects.action.repair"] = "Repair Path…",
        ["projects.action.remove"] = "Remove from List",
        ["projects.noMatch.title"] = "No matching projects",
        ["projects.noMatch.hint"] = "Adjust the search term or filter, or add a new project.",
        ["projects.match.missing"] = "version not installed",
        ["projects.match.unknownVersion"] = "version unknown",
        ["projects.match.noInstallations"] = "no engine installed",
        ["projects.empty.title"] = "No projects yet",
        ["projects.empty.hint"] = "Use “Add Project” to import an existing project, or create one via “New Project”.",

        // ── Dialogs ────────────────────────────────────────────────
        ["dialog.error.title"] = "XHub",
        ["dialog.pickFolder.title"] = "Choose Project Folder",
        ["dialog.pickFile.title"] = "Choose a .xengine Project File",
        ["dialog.pickEditor.title"] = "Choose the XEngine.Editor Executable",
        ["dialog.xengine.title"] = "Choose Project Identity File",
        ["dialog.xengine.message"] = "This folder contains multiple .xengine files, so the project cannot be identified uniquely. Pick the one to use:",
        ["dialog.invalid.title"] = "Not a valid XEngine project",
        ["dialog.invalid.hint"] = "The selected path does not exist, or its Assets folder is missing.",
        ["dialog.repairClash.title"] = "Cannot repair path",
        ["dialog.repairClash.hint"] = "The new location is already registered by another project record. Remove the duplicate first.",
        ["dialog.invalidEditor.title"] = "No runnable editor found",
        ["dialog.invalidEditor.hint"] = "The selected location does not contain a runnable XEngine.Editor.",
        ["launch.error.noMatch.title"] = "No matching engine version",
        ["launch.error.startFailed.title"] = "Cannot start the editor",
        ["launch.error.brokenInstall"] = "The selected engine version is not usable (editor executable missing).",
        ["launch.error.startFailed"] = "Failed to start the editor process. See the log for details.",
        ["session.launching"] = "Launching…",
        ["session.ready"] = "Open",
        ["session.failed"] = "Open failed",
        ["session.unconfirmed"] = "Launched (legacy — load not confirmed)",

        // ── Installations page ─────────────────────────────────────
        ["installations.title"] = "Engine Versions",
        ["installations.scan"] = "Rescan",
        ["installations.locate"] = "Locate…",
        ["installations.col.version"] = "Version",
        ["installations.col.path"] = "Install Path",
        ["installations.col.arch"] = "Arch",
        ["installations.col.source"] = "Source",
        ["installations.col.status"] = "Status",
        ["installations.defaultBadge"] = "Default",
        ["installations.setDefault"] = "Set as Default",
        ["installations.openFolder"] = "Show in Folder",
        ["installations.remove"] = "Remove Record",
        ["installations.channel.preview"] = "Preview",
        ["installations.channel.stable"] = "Stable",
        ["installations.channel.unknown"] = "Unknown",
        ["installations.source.hub"] = "XHub Download",
        ["installations.source.installer"] = "Installer",
        ["installations.source.manual"] = "Manual",
        ["installations.source.source"] = "Source Build",
        ["installations.status.ok"] = "Available",
        ["installations.status.broken"] = "Broken",
        ["installations.version.unknown"] = "Version unknown",
        ["installations.empty.title"] = "No engine versions installed",
        ["installations.empty.hint"] = "Use “Locate” to register an existing installation; online downloads arrive in a later milestone.",

        // ── Templates page ─────────────────────────────────────────
        ["templates.title"] = "Templates",
        ["templates.use"] = "Use This Template",
        ["template.universal3d.name"] = "Universal 3D",
        ["template.universal3d.description"] = "The standard 3D project template: default scene, universal render pipeline assets, and the base folder layout.",
        ["templates.empty.hint"] = "Install an engine version to create projects from the Universal 3D template.",

        // ── New-project wizard ─────────────────────────────────────
        ["wizard.title"] = "New Project",
        ["wizard.template.label"] = "Template",
        ["wizard.engine.label"] = "Engine Version",
        ["wizard.name.label"] = "Project Name",
        ["wizard.name.watermark"] = "e.g. MyGame",
        ["wizard.parent.label"] = "Parent Folder",
        ["wizard.create"] = "Create Project",
        ["wizard.error.nameRequired"] = "Enter a project name.",
        ["wizard.error.nameInvalid"] = "The project name contains invalid characters.",
        ["wizard.error.parentRequired"] = "Choose a parent folder.",
        ["wizard.error.parentMissing"] = "The parent folder does not exist.",
        ["wizard.error.targetExists"] = "A non-empty directory with that name already exists.",
        ["wizard.error.noInstallation"] = "Select a usable engine version.",
        ["wizard.error.canceled"] = "Creation canceled.",
        ["wizard.error.createFailed"] = "Project creation failed.",

        // ── Installations page (N6 additions) ──────────────────────
        ["installations.installed"] = "Installed",
        ["installations.installable"] = "Installable",
        ["installations.refreshFeed"] = "Refresh List",
        ["installations.install"] = "Install",
        ["installations.uninstall"] = "Uninstall",
        ["install.queued.title"] = "Added to download queue",
        ["install.queued.hint"] = "Track progress on the Downloads page.",
        ["uninstall.error.title"] = "Cannot uninstall",
        ["uninstall.error.notHubManaged"] = "Only versions installed by XHub can be uninstalled here; use the vendor uninstaller for this one.",
        ["uninstall.error.inUse"] = "This version is in use by a running editor. Close it first.",
        ["uninstall.error.outsideRoot"] = "The install directory is outside the XHub management root; deletion refused.",
        ["uninstall.error.deleteFailed"] = "Directory deletion failed. Check for locked files.",
        ["install.error.diskSpace"] = "Not enough disk space to install.",
        ["install.error.layout"] = "Unexpected package layout (editor executable missing).",
        ["install.error.capability"] = "The package cannot report its capabilities (no --hub-info response).",
        ["install.error.protocolTooOld"] = "The package requires a newer hub protocol than this XHub.",
        ["install.error.failed"] = "Installation failed.",

        // ── Downloads page ─────────────────────────────────────────
        ["downloads.title"] = "Downloads",
        ["downloads.pause"] = "Pause",
        ["downloads.resume"] = "Resume",
        ["downloads.cancel"] = "Cancel",
        ["downloads.retry"] = "Retry",
        ["downloads.log"] = "Log",
        ["downloads.log.title"] = "Operation log",
        ["downloads.log.empty"] = "No log lines yet.",
        ["downloads.state.queued"] = "Queued",
        ["downloads.state.downloading"] = "Downloading",
        ["downloads.state.paused"] = "Paused",
        ["downloads.state.completed"] = "Downloaded",
        ["downloads.state.failed"] = "Failed",
        ["downloads.state.canceled"] = "Canceled",
        ["downloads.state.installing"] = "Installing",
        ["downloads.state.done"] = "Installed",
        ["download.error.hashMismatch"] = "SHA-256 mismatch — the corrupted download was discarded.",
        ["downloads.empty.title"] = "No download tasks",
        ["downloads.empty.hint"] = "Downloads started from the Engine Versions page appear here.",

        // ── Settings page ──────────────────────────────────────────
        ["settings.title"] = "Settings",
        ["settings.general"] = "General",
        ["settings.language"] = "Interface language",
        ["settings.language.hint"] = "Applies immediately — no restart needed.",
        ["settings.locations"] = "Install locations",
        ["settings.locations.editors"] = "Editors",
        ["settings.locations.downloads"] = "Download cache",
        ["settings.locations.browse"] = "Browse…",
        ["settings.locations.reset"] = "Default",
        ["settings.locations.save"] = "Save locations",
        ["settings.locations.hint"] = "Empty = default location; installed editors are never moved.",
        ["settings.locations.restart"] = "Saved — takes effect after restarting XHub.",
        ["settings.locations.current"] = "In effect now:",
        ["settings.locations.error.title"] = "Invalid location",
        ["settings.locations.error.editors"] = "The editors location must be an absolute path and not a drive root.",
        ["settings.locations.error.downloads"] = "The download cache location must be an absolute path and not a drive root.",
        ["settings.about"] = "About",
        ["settings.logs"] = "Log directory",
        ["settings.logs.hint"] = "Open the XHub log folder in the file manager.",

        // ── Status bar ─────────────────────────────────────────────
        ["status.ready"] = "Ready",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Catalog = new()
    {
        ["zh-CN"] = Chinese,
        ["en-US"] = English,
    };

    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _strings;

    /// <summary>Test seam: fresh instance not tied to the process-wide singleton.</summary>
    internal Loc() => _strings = ResolveDictionary(HubSettings.SystemLanguageCode);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string LanguageCode { get; private set; } = HubSettings.SystemLanguageCode;

    /// <summary>All keys across every dictionary (used by the localization parity test).</summary>
    public static IEnumerable<string> KnownKeys => Chinese.Keys;

    public static IReadOnlyList<string> KnownLanguages { get; } = ["zh-CN", "en-US"];

    public string this[string key] => _strings.TryGetValue(key, out string? value) ? value : key;

    /// <summary>Switches the active dictionary and refreshes every binding.</summary>
    public void SetLanguage(string languageCode)
    {
        Dictionary<string, string> next = ResolveDictionary(languageCode);
        if (ReferenceEquals(next, _strings) && languageCode == LanguageCode) return;
        LanguageCode = languageCode;
        _strings = next;

        // Empty property name = "all properties changed": every indexer binding re-evaluates.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public void Apply(HubLanguage preference) =>
        SetLanguage(preference switch
        {
            HubLanguage.SimplifiedChinese => "zh-CN",
            HubLanguage.English => "en-US",
            _ => HubSettings.SystemLanguageCode,
        });

    private static Dictionary<string, string> ResolveDictionary(string code)
        => Catalog.TryGetValue(code, out Dictionary<string, string>? dict) ? dict : English;
}
