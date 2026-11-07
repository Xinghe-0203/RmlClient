using System.Globalization;

namespace RmlClient.App.Services;

/// <summary>
/// 中英双语。刻意不做 resw/PRI 资源文件：单文件自包含发布时 PRI 处理容易出岔子，
/// 而这里只需要几十条字符串，用字典最直接也最可预测。
/// </summary>
public sealed class Localization
{
    public const string ChineseSimplified = "zh-CN";
    public const string English = "en-US";

    private static readonly Dictionary<string, (string Zh, string En)> Strings = new()
    {
        // 导航
        ["nav.dashboard"] = ("启动", "Home"),
        ["nav.mods"] = ("模组", "Mods"),
        ["nav.market"] = ("市场", "Market"),
        ["nav.logs"] = ("日志", "Logs"),
        ["nav.settings"] = ("设置", "Settings"),

        // 通用
        ["common.refresh"] = ("刷新", "Refresh"),
        ["common.cancel"] = ("取消", "Cancel"),
        ["common.confirm"] = ("确定", "OK"),
        ["common.close"] = ("关闭", "Close"),
        ["common.retry"] = ("重试", "Retry"),
        ["common.browse"] = ("浏览…", "Browse…"),
        ["common.unknown"] = ("未知", "Unknown"),
        ["common.loading"] = ("加载中…", "Loading…"),
        ["common.enabled"] = ("已启用", "Enabled"),
        ["common.disabled"] = ("已禁用", "Disabled"),
        ["common.installed"] = ("已安装", "Installed"),
        ["common.notInstalled"] = ("未安装", "Not installed"),
        ["common.version"] = ("版本", "Version"),
        ["common.author"] = ("作者", "Author"),
        ["common.size"] = ("大小", "Size"),
        ["common.search"] = ("搜索", "Search"),
        ["common.all"] = ("全部", "All"),
        ["common.install"] = ("安装", "Install"),
        ["common.update"] = ("更新", "Update"),
        ["common.uninstall"] = ("卸载", "Uninstall"),
        ["common.enable"] = ("启用", "Enable"),
        ["common.disable"] = ("禁用", "Disable"),
        ["common.open"] = ("打开", "Open"),
        ["common.copy"] = ("复制", "Copy"),
        ["common.save"] = ("保存", "Save"),
        ["common.details"] = ("详情", "Details"),
        ["common.error"] = ("出错了", "Something went wrong"),
        ["common.success"] = ("完成", "Done"),
        ["common.warning"] = ("注意", "Warning"),
        ["common.yes"] = ("是", "Yes"),
        ["common.no"] = ("否", "No"),

        // 首页
        ["home.title"] = ("Raft Mod Loader", "Raft Mod Loader"),
        ["home.subtitle"] = ("一个更清晰的模组启动器", "A cleaner mod launcher"),
        ["home.play"] = ("启动游戏", "Play"),
        ["home.playing"] = ("游戏运行中", "Game running"),
        ["home.gamePath"] = ("游戏路径", "Game path"),
        ["home.gamePathMissing"] = ("未找到游戏，请先设置路径", "Game not found — set the path first"),
        ["home.coreVersion"] = ("Core 版本", "Core version"),
        ["home.injection"] = ("注入状态", "Injection"),
        ["home.injection.json"] = ("JSON 注入", "JSON injection"),
        ["home.injection.doorstop"] = ("Doorstop 注入", "Doorstop injection"),
        ["home.injection.none"] = ("未注入", "Not injected"),
        ["home.injection.partial"] = ("注入不完整", "Partially injected"),
        ["home.injection.corrupted"] = ("注入已损坏", "Injection corrupted"),
        ["home.inject"] = ("注入", "Inject"),
        ["home.repair"] = ("修复注入", "Repair"),
        ["home.removeInjection"] = ("移除注入", "Remove injection"),
        ["home.checkUpdates"] = ("检查更新", "Check for updates"),
        ["home.upToDate"] = ("已是最新", "Up to date"),
        ["home.updateAvailable"] = ("有新版本 {0}", "Version {0} available"),
        ["home.modsLoaded"] = ("{0} 个模组已启用", "{0} mods enabled"),
        ["home.quickActions"] = ("快捷操作", "Quick actions"),
        ["home.openFolder"] = ("打开目录", "Open folder"),
        ["home.openModsFolder"] = ("打开模组目录", "Open mods folder"),

        // 模组
        ["mods.title"] = ("模组", "Mods"),
        ["mods.count"] = ("共 {0} 个", "{0} total"),
        ["mods.dropHint"] = ("把 .rmod 文件拖到这里安装", "Drop .rmod files here to install"),
        ["mods.empty"] = ("还没有安装任何模组", "No mods installed yet"),
        ["mods.emptyHint"] = ("从市场安装，或把 .rmod 拖进窗口", "Install from the market, or drag a .rmod here"),
        ["mods.broken"] = ("无法解析", "Unreadable"),
        ["mods.installed"] = ("已安装 {0}", "Installed {0}"),
        ["mods.removed"] = ("已移除 {0}", "Removed {0}"),
        ["mods.toggled"] = ("{0} 已{1}", "{0} {1}"),
        ["mods.openFolder"] = ("打开模组目录", "Open mods folder"),
        ["mods.noGame"] = ("请先在设置里指定游戏路径", "Set the game path in Settings first"),

        // 市场
        ["market.title"] = ("模组市场", "Mod market"),
        ["market.searchPlaceholder"] = ("搜索模组、作者，支持中文…", "Search mods, authors, Chinese…"),
            ["market.translatingTitles"] = ("正在翻译模组名称…", "Translating mod names…"),
        ["market.fetching"] = ("正在获取市场数据…", "Fetching market data…"),
        ["market.offline"] = ("市场不可用，显示上次缓存", "Market unavailable — showing cache"),
        ["market.noResults"] = ("没有匹配的模组", "No matching mods"),
        ["market.downloads"] = ("{0} 次下载", "{0} downloads"),
        ["market.versions"] = ("版本历史", "Version history"),
        ["market.rollback"] = ("回滚到 {0}", "Roll back to {0}"),
        ["market.installVersion"] = ("安装 {0}", "Install {0}"),

        // 模组详情 / 翻译
        ["details.description"] = ("这个模组是做什么的", "What this mod does"),
        ["details.changelog"] = ("更新日志", "Changelog"),
        ["details.noDescription"] = ("作者没有填写描述", "The author left no description"),
        ["details.translateTo"] = ("翻译为", "Translate to"),
        ["details.original"] = ("原文", "Original"),
        ["details.translating"] = ("正在翻译…", "Translating…"),
        ["details.translatedBy"] = ("由 {0} 翻译", "Translated by {0}"),
        ["details.cached"] = ("已缓存", "cached"),
        ["details.translateFailed"] = ("翻译不可用，显示原文", "Translation unavailable — showing original"),
        ["details.skipped"] = ("已是目标语言", "Already in target language"),
        ["details.showOriginal"] = ("看原文", "Show original"),
        ["details.showTranslation"] = ("看译文", "Show translation"),
        ["details.notInMarket"] = ("本地模组，无市场信息", "Local mod — not in the market"),
            ["mods.clickForDetails"] = ("点击查看详情", "Click for details"),
            ["mods.revealFile"] = ("在文件夹中显示", "Show in folder"),

        // 日志
        ["logs.title"] = ("日志", "Logs"),        ["logs.client"] = ("客户端", "Client"),
        ["logs.loader"] = ("加载器", "Loader"),
        ["logs.autoScroll"] = ("自动滚动", "Auto-scroll"),
        ["logs.clear"] = ("清空", "Clear"),
        ["logs.export"] = ("导出", "Export"),
        ["logs.empty"] = ("暂无日志", "No log entries"),

        // 托盘
        ["tray.open"] = ("打开主界面", "Open"),
        ["tray.exit"] = ("退出", "Exit"),

        // 设置
        ["settings.title"] = ("设置", "Settings"),
        ["settings.general"] = ("通用", "General"),
        ["settings.language"] = ("语言", "Language"),
        ["settings.languageHint"] = ("切换界面语言", "Change interface language"),
        ["settings.branch"] = ("更新分支", "Update branch"),
        ["settings.branchHint"] = ("public 为稳定版；其它分支需服务器支持", "public is stable; other branches depend on the server"),
        ["settings.game"] = ("游戏", "Game"),
        ["settings.gamePath"] = ("游戏路径", "Game path"),
        ["settings.detect"] = ("自动检测", "Auto-detect"),
        ["settings.startViaSteam"] = ("通过 Steam 启动", "Launch through Steam"),
        ["settings.startViaSteamHint"] = ("由 Steam 处理 DRM 与云存档", "Steam handles DRM and cloud saves"),
        ["settings.skipSplash"] = ("跳过 Unity 启动画面", "Skip Unity splash screen"),
        ["settings.skipSplashHint"] = ("改写 globalgamemanagers（首次会备份）", "Patches globalgamemanagers (backed up on first use)"),
        ["settings.injection"] = ("注入", "Injection"),
        ["settings.injectionMode"] = ("注入方式", "Injection mode"),
        ["settings.injectionJson"] = ("JSON（推荐）", "JSON (recommended)"),
        ["settings.injectionDoorstop"] = ("Doorstop（兼容旧版）", "Doorstop (legacy)"),
        ["settings.injectionHint"] = ("JSON 方式不改动 exe，也不写 winhttp.dll", "JSON mode does not touch the exe or winhttp.dll"),
        ["settings.verify"] = ("校验文件", "Verify files"),
        ["settings.behavior"] = ("行为", "Behavior"),
        ["settings.closeToTray"] = ("关闭时最小化到托盘", "Minimize to tray on close"),
        ["settings.checkUpdates"] = ("启动时检查更新", "Check for updates on startup"),
        ["settings.uploadLogs"] = ("上传崩溃日志", "Upload crash logs"),
        ["settings.uploadLogsHint"] = ("默认关闭。开启后会把异常栈上传到第三方服务", "Off by default. Enabling uploads stack traces to a third party"),
        ["settings.advanced"] = ("高级", "Advanced"),
        ["settings.restore"] = ("从备份还原游戏文件", "Restore game files from backup"),
        ["settings.restoreHint"] = ("把注入前备份的原始文件写回", "Writes back the files backed up before injection"),
        ["settings.reset"] = ("重置所有设置", "Reset all settings"),
        ["settings.about"] = ("关于", "About"),
        ["settings.dataFolder"] = ("数据目录", "Data folder"),

        // 引导
        ["onboarding.welcome"] = ("欢迎使用 RML Client", "Welcome to RML Client"),
        ["onboarding.welcomeHint"] = ("这是 Raft Mod Loader 的新启动器，接管你已有的配置与模组。", "A new launcher for Raft Mod Loader that takes over your existing config and mods."),
        ["onboarding.step1"] = ("找到游戏", "Find your game"),
        ["onboarding.step1Hint"] = ("我们通过 Steam 库自动定位 Raft。", "We locate Raft through your Steam libraries."),
        ["onboarding.step2"] = ("安装 Core", "Install core"),
        ["onboarding.step2Hint"] = ("coremod 与资源文件会从官方服务器下载并校验。", "Coremod and assets are downloaded from the official server and verified."),
        ["onboarding.step3"] = ("注入加载器", "Inject loader"),
        ["onboarding.step3Hint"] = ("把 AssemblyLoader 挂进 Unity 的启动链。", "Hooks AssemblyLoader into Unity's startup chain."),
        ["onboarding.step4"] = ("准备就绪", "All set"),
        ["onboarding.step4Hint"] = ("现在可以启动游戏了。", "You can start the game now."),
        ["onboarding.next"] = ("下一步", "Next"),
        ["onboarding.back"] = ("上一步", "Back"),
        ["onboarding.finish"] = ("完成", "Finish"),
        ["onboarding.skip"] = ("跳过", "Skip"),
        ["onboarding.terms"] = ("我已阅读并同意服务条款", "I have read and agree to the terms of service"),
    };

    private string _language = ChineseSimplified;

    public event Action? LanguageChanged;

    public string Language
    {
        get => _language;
        set
        {
            if (string.Equals(_language, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _language = value;
            LanguageChanged?.Invoke();
        }
    }

    public bool IsChinese => _language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>取一条文案；缺失时回退到 key 本身，方便开发期发现漏翻。</summary>
    public string this[string key]
    {
        get
        {
            if (!Strings.TryGetValue(key, out var pair))
            {
                return key;
            }

            return IsChinese ? pair.Zh : pair.En;
        }
    }

    public string Format(string key, params object[] args)
    {
        var template = this[key];
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public static IReadOnlyList<(string Code, string Label)> AvailableLanguages { get; } = new[]
    {
        (ChineseSimplified, "简体中文"),
        (English, "English"),
    };
}
