using RmlClient.App.ViewModels;
using RmlClient.Core.Configuration;
using RmlClient.Core.Diagnostics;
using RmlClient.Core.Game;
using RmlClient.Core.Injection;
using RmlClient.Core.Logging;
using RmlClient.Core.Market;
using RmlClient.Core.Mods;
using RmlClient.Core.Translation;
using RmlClient.Core.Updates;

namespace RmlClient.App.Services;

/// <summary>
/// 应用级服务容器。
///
/// 手工构造而不是上 DI 容器：依赖只有十来个，全是单例，
/// 显式 new 出来的顺序关系比反射注入更好读、也更好调试。
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly RmlHttpClient _http;

    public AppServices()
    {
        Paths = new RmlPaths();
        Paths.EnsureDirectories();

        Settings = new ConfigStore(Paths);
        Config = Settings.Load();

        Logger = new ClientLogger(Paths.ClientLog, Config.Client.LogBufferLines);
        Logger.Info("App", $"客户端启动，数据目录：{Paths.Root}");

        Localization = new Localization { Language = Config.Client.Language };

        _http = new RmlHttpClient();
        Api = new RmlApi(_http);
        CoreUpdater = new CoreUpdater(Paths, Api, _http);

        Backup = new InjectionBackup(Paths);
        JsonInjector = new JsonInjector(Paths, Backup);
        DoorstopInjector = new DoorstopInjector(Paths, Backup);

        GameLauncher = new GameLauncher(Paths, Logger);
        SteamLocator = new SteamLocator();
        GameManagers = new GameManagersPatcher(Path.Combine(Paths.CacheBackup, "ggm"));
        Translation = new TranslationService(Paths);
        MarketTitles = new TitleTranslator(Paths, Translation);
        LocalModTitles = new TitleTranslator(Paths, Translation, "local-mod-titles.json");
        ClientUpdater = new ClientUpdater(Paths);

        // 配置里已有有效游戏路径时立刻建好 mod / 市场服务，
        // 否则首屏「模组 0/0」、市场页空白，用户会以为程序没读到数据。
        if (GameInstallValidator.Validate(Config.GamePath).IsValid)
        {
            RebuildGameServices(Config.GamePath);
        }
    }

    public RmlPaths Paths { get; }

    public ConfigStore Settings { get; }

    public LauncherConfig Config { get; private set; }

    public ClientLogger Logger { get; }

    public Localization Localization { get; }

    public RmlApi Api { get; }

    public RmlHttpClient Http => _http;

    public CoreUpdater CoreUpdater { get; }

    public InjectionBackup Backup { get; }

    public JsonInjector JsonInjector { get; }

    public DoorstopInjector DoorstopInjector { get; }

    public GameLauncher GameLauncher { get; }

    public SteamLocator SteamLocator { get; }

    public GameManagersPatcher GameManagers { get; }

    public TranslationService Translation { get; }

    public TitleTranslator MarketTitles { get; }

    /// <summary>已安装模组的标题翻译表（键 = slug 或文件名）。</summary>
    public TitleTranslator LocalModTitles { get; }

    /// <summary>客户端自身的版本检查与安装包下载（GitHub Releases）。</summary>
    public ClientUpdater ClientUpdater { get; }

    public ModRepository? Mods { get; private set; }

    public MarketService? Market { get; private set; }

    /// <summary>游戏路径变化时重建依赖它的服务。</summary>
    public void UpdateGamePath(string gamePath)
    {
        Config.GamePath = gamePath;
        SaveConfig();
        RebuildGameServices(gamePath);

        Logger.Info("App", $"游戏路径：{gamePath}");
    }

    private void RebuildGameServices(string gamePath)
    {
        Mods = new ModRepository(RmlPaths.GameModsFolder(gamePath));
        Market = new MarketService(Paths, _http, Api, Mods);
    }

    public void SaveConfig()
    {
        Config.Client.Language = Localization.Language;
        Settings.Save(Config);
    }

    public IInjector GetInjector(InjectionMode? mode = null) =>
        (mode ?? Config.Client.InjectionMode) == InjectionMode.Doorstop
            ? DoorstopInjector
            : JsonInjector;

    /// <summary>当前有效的游戏路径（可能为空）。</summary>
    public string GamePath => Config.GamePath;

    public bool HasValidGame => GameInstallValidator.Validate(Config.GamePath).IsValid;

    public void Dispose()
    {
        Logger.Info("App", "客户端退出");
        Logger.Dispose();
        _http.Dispose();
    }
}
