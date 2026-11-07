using Microsoft.Win32;

namespace RmlClient.Core.Game;

/// <summary>一个 Raft 安装。</summary>
public sealed record GameInstall(string GamePath, string Source)
{
    public string Executable => Path.Combine(GamePath, "Raft.exe");

    public string ModsFolder => Path.Combine(GamePath, "mods");

    public bool IsValid => GameInstallValidator.Validate(GamePath).IsValid;
}

public sealed record GameValidationResult(bool IsValid, IReadOnlyList<string> Problems)
{
    public static GameValidationResult Ok() => new(true, Array.Empty<string>());

    public static GameValidationResult Fail(params string[] problems) => new(false, problems);
}

public static class GameInstallValidator
{
    /// <summary>
    /// 与官方 <c>Main.IsGamePathValid</c> 等价，但给出具体原因而不是只返回 bool。
    /// </summary>
    public static GameValidationResult Validate(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return GameValidationResult.Fail("游戏路径为空。");
        }

        if (!Directory.Exists(gamePath))
        {
            return GameValidationResult.Fail($"目录不存在：{gamePath}");
        }

        var problems = new List<string>();

        var exe = Path.Combine(gamePath, "Raft.exe");
        if (!File.Exists(exe))
        {
            problems.Add("找不到 Raft.exe。");
        }

        var dataFolder = Path.Combine(gamePath, "Raft_Data");
        if (!Directory.Exists(dataFolder))
        {
            problems.Add("找不到 Raft_Data 目录。");
        }

        var assemblyCSharp = Path.Combine(dataFolder, "Managed", "Assembly-CSharp.dll");
        if (!File.Exists(assemblyCSharp))
        {
            problems.Add("找不到 Raft_Data\\Managed\\Assembly-CSharp.dll。");
        }

        return problems.Count == 0 ? GameValidationResult.Ok() : GameValidationResult.Fail(problems.ToArray());
    }
}

/// <summary>
/// 通过 Steam 的注册表与 libraryfolders.vdf 定位 Raft。
///
/// 官方只读 <c>HKLM\Software\Wow6432Node\Valve\Steam\InstallPath</c> 与
/// <c>BaseInstallFolder_1/2/3</c>（见 <c>Launcher\Main.cs:1021-1055</c>），
/// 在新版 Steam 上 <c>BaseInstallFolder_*</c> 已废弃，会导致多库用户找不到游戏。
/// 本实现改为解析 <c>steamapps\libraryfolders.vdf</c> 的 <c>path</c> 字段。
/// </summary>
public sealed class SteamLocator
{
    private readonly Func<string, bool> _directoryExists;

    public SteamLocator(Func<string, bool>? directoryExists = null) =>
        _directoryExists = directoryExists ?? Directory.Exists;

    public string? FindSteamInstallPath()
    {
        // 64 位视图
        var fromRegistry = ReadString(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")
                           ?? ReadString(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                           ?? ReadString(RegistryHive.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamPath");

        if (!string.IsNullOrWhiteSpace(fromRegistry) && _directoryExists(fromRegistry))
        {
            return fromRegistry;
        }

        foreach (var candidate in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                     @"D:\Steam",
                     @"D:\SteamLibrary",
                 })
        {
            if (_directoryExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>列出所有 Steam 库根目录（含主库）。</summary>
    public IReadOnlyList<string> EnumerateLibraryFolders()
    {
        var result = new List<string>();

        var steamPath = FindSteamInstallPath();
        if (steamPath is null)
        {
            return result;
        }

        result.Add(steamPath);

        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            return result;
        }

        try
        {
            foreach (var line in File.ReadLines(vdf))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = ExtractVdfString(trimmed);
                if (value is null)
                {
                    continue;
                }

                var normalized = value.Replace(@"\\", @"\");
                if (_directoryExists(normalized) &&
                    !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(normalized);
                }
            }
        }
        catch (IOException)
        {
            // libraryfolders.vdf 偶尔被 Steam 独占，读失败就只用主库。
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>在所有库里找 Raft。</summary>
    public IReadOnlyList<GameInstall> FindRaftInstallations()
    {
        var found = new List<GameInstall>();

        foreach (var library in EnumerateLibraryFolders())
        {
            var gamePath = Path.Combine(library, "steamapps", "common", "Raft");
            if (_directoryExists(gamePath) && GameInstallValidator.Validate(gamePath).IsValid)
            {
                found.Add(new GameInstall(gamePath, library));
            }
        }

        return found;
    }

    private static string? ReadString(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? ExtractVdfString(string line)
    {
        var firstQuote = line.IndexOf('"');
        if (firstQuote < 0)
        {
            return null;
        }

        var secondQuote = line.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0)
        {
            return null;
        }

        var thirdQuote = line.IndexOf('"', secondQuote + 1);
        if (thirdQuote < 0)
        {
            return null;
        }

        var fourthQuote = line.IndexOf('"', thirdQuote + 1);
        if (fourthQuote < 0)
        {
            return null;
        }

        return line[(thirdQuote + 1)..fourthQuote];
    }
}
