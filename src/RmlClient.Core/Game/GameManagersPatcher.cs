using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace RmlClient.Core.Game;

/// <summary>
/// 改写 <c>Raft_Data\globalgamemanagers</c> 里的 PlayerSettings / AudioManager。
///
/// 官方（<c>Launcher\GGMEditor.cs</c>）做了两件事：
///   1. <c>m_ShowUnitySplashScreen</c> 取反于 <c>skipSplashScreen</c> 配置
///   2. 若 <c>m_DisableAudio</c> 为 true 就改回 false（否则 mod 的自定义音频不生效）
///
/// 本实现的改进：
///   - 第一次改写前把原文件备份到 <c>%APPDATA%\RaftModLoader\cache\backup\ggm</c>，
///     官方是完全不备份直接覆写的，一旦改坏只能靠 Steam 校验恢复。
///   - 只在字段真的需要变化时才写盘，避免每次启动都动游戏文件。
///   - 任何异常都吞掉并返回说明，不让「跳过启动画面」这种可选功能拖垮启动流程。
/// </summary>
public sealed class GameManagersPatcher
{
    private const string ClassDataResourceName = "RmlClient.Core.classdata.tpk";
    private const string SplashFieldName = "m_ShowUnitySplashScreen";
    private const string DisableAudioFieldName = "m_DisableAudio";

    private readonly string _backupDirectory;

    public GameManagersPatcher(string backupDirectory)
    {
        _backupDirectory = backupDirectory;
    }

    public sealed record PatchResult(bool Changed, bool BackedUp, string Message);

    /// <summary>
    /// 把 <c>globalgamemanagers</c> 调整到与设置一致。
    /// </summary>
    /// <param name="gamePath">游戏根目录。</param>
    /// <param name="skipSplash">true 表示跳过 Unity 启动画面。</param>
    public PatchResult Apply(string gamePath, bool skipSplash)
    {
        var ggmPath = Configuration.RmlPaths.GameGlobalGameManagers(gamePath);
        if (!File.Exists(ggmPath))
        {
            return new PatchResult(false, false, $"找不到 {ggmPath}");
        }

        try
        {
            return PatchCore(ggmPath, skipSplash);
        }
        catch (Exception ex)
        {
            return new PatchResult(false, false, $"改写 globalgamemanagers 失败：{ex.Message}");
        }
    }

    private PatchResult PatchCore(string ggmPath, bool skipSplash)
    {
        using var classData = OpenClassData();
        if (classData is null)
        {
            return new PatchResult(false, false, "缺少 classdata.tpk，无法解析 Unity 资源");
        }

        var manager = new AssetsManager();
        manager.LoadClassPackage(classData);

        // 整份读进内存再解析：写回时用的是同一个 path，直接用 FileStream 会锁住文件。
        var bytes = File.ReadAllBytes(ggmPath);
        using var source = new MemoryStream(bytes);
        var instance = manager.LoadAssetsFile(source, ggmPath, false, null);
        manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);

        var changed = false;

        // PlayerSettings：m_ShowUnitySplashScreen 与「跳过启动画面」相反。
        var playerSettings = instance.file.GetAssetsOfType(AssetClassID.PlayerSettings);
        if (playerSettings.Count > 0)
        {
            var field = manager.GetBaseField(instance, playerSettings[0], AssetReadFlags.None);
            var splash = field[SplashFieldName];
            if (splash is not null && !splash.IsDummy)
            {
                var desired = !skipSplash;
                if (splash.AsBool != desired)
                {
                    splash.AsBool = desired;
                    playerSettings[0].SetNewData(field);
                    changed = true;
                }
            }
        }

        // AudioManager：官方强制把 m_DisableAudio 改回 false，否则 mod 的自定义音频不播放。
        var audioManager = instance.file.GetAssetsOfType(AssetClassID.AudioManager);
        if (audioManager.Count > 0)
        {
            var field = manager.GetBaseField(instance, audioManager[0], AssetReadFlags.None);
            var disableAudio = field[DisableAudioFieldName];
            if (disableAudio is not null && !disableAudio.IsDummy && disableAudio.AsBool)
            {
                disableAudio.AsBool = false;
                audioManager[0].SetNewData(field);
                changed = true;
            }
        }

        if (!changed)
        {
            return new PatchResult(false, false, "启动画面与音频设置已是目标状态，无需改写");
        }

        var backedUp = BackupOnce(ggmPath, bytes);

        // 先写临时文件再替换，避免写到一半崩溃留下半个资源文件。
        var tempPath = ggmPath + ".rmlclient.tmp";
        using (var writer = new AssetsFileWriter(tempPath))
        {
            instance.file.Write(writer, 0L);
        }

        File.Move(tempPath, ggmPath, overwrite: true);

        return new PatchResult(true, backedUp, "已更新 globalgamemanagers");
    }

    private static Stream? OpenClassData()
    {
        var assembly = typeof(GameManagersPatcher).Assembly;
        var stream = assembly.GetManifestResourceStream(ClassDataResourceName);
        if (stream is not null)
        {
            return stream;
        }

        // 回退：发布目录里如果有同名文件也认。
        var sideCar = Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
        return File.Exists(sideCar) ? File.OpenRead(sideCar) : null;
    }

    /// <summary>只在第一次改写前备份，后续改写不覆盖备份（备份永远是原始文件）。</summary>
    private bool BackupOnce(string ggmPath, byte[] original)
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            var backupPath = Path.Combine(_backupDirectory, "globalgamemanagers.original");
            if (File.Exists(backupPath))
            {
                return false;
            }

            File.WriteAllBytes(backupPath, original);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
