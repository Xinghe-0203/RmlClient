namespace RmlClient.Core.Configuration;

/// <summary>
/// RML 的目录约定，与官方 LauncherConfiguration 常量逐一对齐。
///
/// 官方来源：<c>Launcher\LauncherConfiguration.cs</c> 与 <c>Launcher\Main.cs:79-97</c>。
/// </summary>
public sealed class RmlPaths
{
    public const string GameSteamAppId = "648800";
    public const string GameName = "Raft";
    public const string GameMainExecutable = "Raft.exe";
    public const string GameDataFolderName = "Raft_Data";
    public const string ModLoaderName = "Raft Mod Loader";
    public const string ModLoaderSite = "raftmodding.com";
    public const string ModLoaderFolderName = "RaftModLoader";
    public const string ModExtension = ".rmod";
    public const string DisabledModExtension = ".rmod.disabled";
    public const string CoreModFileName = "coremod.dll";
    public const string AssetsFileName = "rml.assets";
    public const string UriScheme = "rmllauncher";

    public const string FastDlBaseUrl = "https://fastdl.raftmodding.com";
    public const string SiteBaseUrl = "https://www.raftmodding.com";

    public RmlPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ModLoaderFolderName);

        Binaries = Path.Combine(Root, "binaries");
        Cache = Path.Combine(Root, "cache");
        CacheMods = Path.Combine(Cache, "mods");
        CacheTextures = Path.Combine(Cache, "textures");
        CacheTemporary = Path.Combine(Cache, "temp");
        CacheBackup = Path.Combine(Cache, "backup");
        Logs = Path.Combine(Root, "logs");
        Resources = Path.Combine(Root, "resources");
    }

    public string Root { get; }

    public string Binaries { get; }

    public string Cache { get; }

    public string CacheMods { get; }

    public string CacheTextures { get; }

    public string CacheTemporary { get; }

    /// <summary>本客户端新增：注入前备份原始游戏文件。</summary>
    public string CacheBackup { get; }

    public string Logs { get; }

    public string Resources { get; }

    public string ConfigFile => Path.Combine(Root, "config.ini");

    public string PidFile => Path.Combine(Root, "pid.txt");

    public string SafeModeFile => Path.Combine(Root, "safe.mode");

    public string CoreModDll => Path.Combine(Binaries, CoreModFileName);

    public string AssemblyLoaderDll => Path.Combine(Binaries, "HMLCoreLibrary.dll");

    public string AssetsFile => Path.Combine(Root, AssetsFileName);

    public string HmlCoreExecutable => Path.Combine(Root, "HMLCore.exe");

    public string HLoaderLog => Path.Combine(Logs, "hloader.log");

    public string MarketCacheFile => Path.Combine(Cache, "market.json");

    public string ClientLog => Path.Combine(Logs, "rmlclient.log");

    public static string GameModsFolder(string gamePath) => Path.Combine(gamePath, "mods");

    public static string GameManagedFolder(string gamePath) =>
        Path.Combine(gamePath, GameDataFolderName, "Managed");

    public static string GameInjectedLoaderDll(string gamePath) =>
        Path.Combine(GameManagedFolder(gamePath), "RaftModLoader_AssemblyLoader.dll");

    public static string GameRuntimeInitializeOnLoadsJson(string gamePath) =>
        Path.Combine(gamePath, GameDataFolderName, "RuntimeInitializeOnLoads.json");

    public static string GameScriptingAssembliesJson(string gamePath) =>
        Path.Combine(gamePath, GameDataFolderName, "ScriptingAssemblies.json");

    public static string GameDoorstopConfig(string gamePath) =>
        Path.Combine(gamePath, "doorstop_config.ini");

    public static string GameWinHttpDll(string gamePath) => Path.Combine(gamePath, "winhttp.dll");

    public static string GameLoaderDll(string gamePath) => Path.Combine(gamePath, "RaftModLoader.dll");

    public static string GameGlobalGameManagers(string gamePath) =>
        Path.Combine(gamePath, GameDataFolderName, "globalgamemanagers");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Binaries);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(CacheMods);
        Directory.CreateDirectory(CacheTextures);
        Directory.CreateDirectory(CacheTemporary);
        Directory.CreateDirectory(CacheBackup);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Resources);
    }
}
