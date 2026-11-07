namespace RmlClient.Core.Configuration;

/// <summary>注入方式。</summary>
public enum InjectionMode
{
    /// <summary>改写 Raft_Data 下的两个 JSON + 拷 AssemblyLoader.dll。当前机器上生效的模式。</summary>
    Json = 0,

    /// <summary>winhttp.dll 代理 + doorstop_config.ini。兼容旧版 Raft。</summary>
    Doorstop = 1,
}

/// <summary>客户端自有的配置，落在 config.ini 的 [client] 段。</summary>
public sealed class ClientSettings
{
    public string Language { get; set; } = "zh-CN";

    public string Theme { get; set; } = "light";

    public string Accent { get; set; } = "cyan";

    public InjectionMode InjectionMode { get; set; } = InjectionMode.Json;

    public bool CloseToTray { get; set; } = true;

    /// <summary>崩溃/异常日志上传。官方默认自动上传，本客户端默认关闭。</summary>
    public bool UploadCrashLogs { get; set; }

    public bool CheckUpdatesOnStartup { get; set; } = true;

    public int LogBufferLines { get; set; } = 5000;

    /// <summary>
    /// 模组详情页的翻译目标语言。空字符串表示「跟随系统」。
    /// </summary>
    public string TranslateTarget { get; set; } = string.Empty;

    /// <summary>打开模组详情时自动翻译描述与更新日志。</summary>
    public bool AutoTranslate { get; set; } = true;
}

/// <summary>
/// 完整配置：官方 [launcher] 段 + 客户端 [client] 段。
///
/// 官方 [launcher] 段的键名与语义必须逐字保持，因为 coremod 与 AssemblyLoader
/// 都会读取它（见 <c>HLoader.GetLauncherConfiguration()</c>）。
/// </summary>
public sealed class LauncherConfig
{
    public const string LauncherSection = "launcher";
    public const string ClientSection = "client";

    public string GamePath { get; set; } = string.Empty;

    public string CoreVersion { get; set; } = "?";

    public bool AgreeWithTos { get; set; }

    public bool SkipSplashScreen { get; set; }

    public bool StartGameFromSteam { get; set; } = true;

    public string Branch { get; set; } = "public";

    public string GameStartCommand { get; set; } = "steam://run/648800";

    public ClientSettings Client { get; set; } = new();

    public static LauncherConfig FromIni(IniFile ini)
    {
        var config = new LauncherConfig
        {
            GamePath = ini.Get(LauncherSection, "gamePath", string.Empty) ?? string.Empty,
            CoreVersion = ini.Get(LauncherSection, "coreVersion", "?") ?? "?",
            AgreeWithTos = ini.GetInt(LauncherSection, "agreeWithTOS") == 1,
            SkipSplashScreen = ini.GetBool(LauncherSection, "skipSplashScreen"),
            StartGameFromSteam = ini.GetBool(LauncherSection, "startGameFromSteam", fallback: true),
            Branch = ini.Get(LauncherSection, "branch", "public") ?? "public",
            GameStartCommand = ini.Get(LauncherSection, "gameStartCommand", "steam://run/648800")
                               ?? "steam://run/648800",
        };

        var client = config.Client;
        client.Language = ini.Get(ClientSection, "language", client.Language) ?? client.Language;
        client.Theme = ini.Get(ClientSection, "theme", client.Theme) ?? client.Theme;
        client.Accent = ini.Get(ClientSection, "accent", client.Accent) ?? client.Accent;
        client.CloseToTray = ini.GetBool(ClientSection, "closeToTray", client.CloseToTray);
        client.UploadCrashLogs = ini.GetBool(ClientSection, "uploadCrashLogs", client.UploadCrashLogs);
        client.CheckUpdatesOnStartup = ini.GetBool(ClientSection, "checkUpdatesOnStartup", client.CheckUpdatesOnStartup);
        client.LogBufferLines = ini.GetInt(ClientSection, "logBufferLines", client.LogBufferLines);
        client.TranslateTarget = ini.Get(ClientSection, "translateTarget", string.Empty) ?? string.Empty;
        client.AutoTranslate = ini.GetBool(ClientSection, "autoTranslate", client.AutoTranslate);

        var mode = ini.Get(ClientSection, "injectMode");
        client.InjectionMode = mode?.Equals("doorstop", StringComparison.OrdinalIgnoreCase) == true
            ? InjectionMode.Doorstop
            : InjectionMode.Json;

        return config;
    }

    public void WriteTo(IniFile ini)
    {
        ini.Set(LauncherSection, "gamePath", GamePath);
        ini.Set(LauncherSection, "coreVersion", CoreVersion);
        ini.Set(LauncherSection, "agreeWithTOS", AgreeWithTos ? "1" : "0");
        ini.Set(LauncherSection, "skipSplashScreen", SkipSplashScreen);
        ini.Set(LauncherSection, "startGameFromSteam", StartGameFromSteam);
        ini.Set(LauncherSection, "branch", Branch);
        ini.Set(LauncherSection, "gameStartCommand", GameStartCommand);

        ini.Set(ClientSection, "language", Client.Language);
        ini.Set(ClientSection, "theme", Client.Theme);
        ini.Set(ClientSection, "accent", Client.Accent);
        ini.Set(ClientSection, "injectMode",
            Client.InjectionMode == InjectionMode.Doorstop ? "doorstop" : "json");
        ini.Set(ClientSection, "closeToTray", Client.CloseToTray);
        ini.Set(ClientSection, "uploadCrashLogs", Client.UploadCrashLogs);
        ini.Set(ClientSection, "checkUpdatesOnStartup", Client.CheckUpdatesOnStartup);
        ini.Set(ClientSection, "logBufferLines", Client.LogBufferLines);
        ini.Set(ClientSection, "translateTarget", Client.TranslateTarget);
        ini.Set(ClientSection, "autoTranslate", Client.AutoTranslate);
    }
}

/// <summary>配置文件读写入口。</summary>
public sealed class ConfigStore
{
    private readonly string _path;

    public ConfigStore(string path) => _path = path;

    public ConfigStore(RmlPaths paths) : this(paths.ConfigFile)
    {
    }

    public string Path => _path;

    public LauncherConfig Load()
    {
        var ini = IniFile.Load(_path);
        return LauncherConfig.FromIni(ini);
    }

    /// <summary>
    /// 保存时保留文件中已存在但本客户端不认识的段与键，
    /// 避免与官方 HMLCore.exe 混用时互相抹掉对方写的配置。
    /// </summary>
    public void Save(LauncherConfig config)
    {
        var ini = IniFile.Load(_path);
        config.WriteTo(ini);
        ini.Save(_path);
    }
}
