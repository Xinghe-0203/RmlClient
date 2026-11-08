using RmlClient.Core.Configuration;

namespace RmlClient.Core.Tests;

public class IniFileTests
{
    [Fact]
    public void Parse_ReadsSectionsAndKeys()
    {
        var ini = IniFile.Parse("""
            [launcher]
            gamePath = D:\SteamLibrary\steamapps\common\Raft
            coreVersion = 2.8.10

            [client]
            theme = dark
            """);

        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Raft", ini.Get("launcher", "gamePath"));
        Assert.Equal("2.8.10", ini.Get("launcher", "coreVersion"));
        Assert.Equal("dark", ini.Get("client", "theme"));
    }

    [Fact]
    public void Parse_IgnoresCommentsAndBlankLines()
    {
        var ini = IniFile.Parse("""
            ; 这是注释
            # 这也是注释
            [launcher]

            gamePath = C:\Raft
            """);

        Assert.Equal(@"C:\Raft", ini.Get("launcher", "gamePath"));
        Assert.Single(ini.Sections);
    }

    [Fact]
    public void KeysAndSections_AreCaseInsensitive()
    {
        var ini = IniFile.Parse("[Launcher]\nGamePath = C:\\Raft\n");

        Assert.Equal(@"C:\Raft", ini.Get("LAUNCHER", "gamepath"));
    }

    [Fact]
    public void GetBool_AcceptsOfficialSpellings()
    {
        var ini = IniFile.Parse("""
            [launcher]
            a = True
            b = False
            c = 1
            d = 0
            e = yes
            f = no
            g = 乱写
            """);

        Assert.True(ini.GetBool("launcher", "a"));
        Assert.False(ini.GetBool("launcher", "b"));
        Assert.True(ini.GetBool("launcher", "c"));
        Assert.False(ini.GetBool("launcher", "d"));
        Assert.True(ini.GetBool("launcher", "e"));
        Assert.False(ini.GetBool("launcher", "f"));

        // 无法解析时回落到调用方给的默认值，而不是抛异常。
        Assert.True(ini.GetBool("launcher", "g", fallback: true));
        Assert.False(ini.GetBool("launcher", "g", fallback: false));
    }

    [Fact]
    public void Get_ReturnsFallbackForMissingKeyOrSection()
    {
        var ini = IniFile.Parse("[launcher]\nx = 1\n");

        Assert.Equal("fallback", ini.Get("launcher", "missing", "fallback"));
        Assert.Equal("fallback", ini.Get("nosuchsection", "x", "fallback"));
        Assert.Null(ini.Get("launcher", "missing"));
    }

    [Fact]
    public void GetInt_ParsesAndFallsBack()
    {
        var ini = IniFile.Parse("[client]\nlogBufferLines = 5000\nbad = abc\n");

        Assert.Equal(5000, ini.GetInt("client", "logBufferLines"));
        Assert.Equal(7, ini.GetInt("client", "bad", 7));
        Assert.Equal(7, ini.GetInt("client", "absent", 7));
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var original = IniFile.Parse("""
            [launcher]
            gamePath = C:\Raft
            agreeWithTOS = 1

            [client]
            theme = dark
            """);

        var reparsed = IniFile.Parse(original.Serialize());

        Assert.Equal(@"C:\Raft", reparsed.Get("launcher", "gamePath"));
        Assert.Equal("1", reparsed.Get("launcher", "agreeWithTOS"));
        Assert.Equal("dark", reparsed.Get("client", "theme"));
    }

    [Fact]
    public void Serialize_KeepsSectionAndKeyOrder()
    {
        var ini = new IniFile();
        ini.Set("b", "k2", "2");
        ini.Set("b", "k1", "1");
        ini.Set("a", "k", "x");

        var text = ini.Serialize();

        // 段顺序按首次出现，键顺序按写入顺序 —— 保证 diff 稳定。
        Assert.True(text.IndexOf("[b]", StringComparison.Ordinal) < text.IndexOf("[a]", StringComparison.Ordinal));
        Assert.True(text.IndexOf("k2", StringComparison.Ordinal) < text.IndexOf("k1", StringComparison.Ordinal));
    }

    [Fact]
    public void SetBool_WritesOfficialTrueFalseSpelling()
    {
        var ini = new IniFile();
        ini.Set("launcher", "skipSplashScreen", true);
        ini.Set("launcher", "startGameFromSteam", false);

        Assert.Equal("True", ini.Get("launcher", "skipSplashScreen"));
        Assert.Equal("False", ini.Get("launcher", "startGameFromSteam"));
    }

    [Fact]
    public void Remove_DropsKey()
    {
        var ini = IniFile.Parse("[client]\ntheme = dark\naccent = cyan\n");

        Assert.True(ini.Remove("client", "theme"));
        Assert.Null(ini.Get("client", "theme"));
        Assert.Equal("cyan", ini.Get("client", "accent"));
        Assert.False(ini.Remove("client", "theme"));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsOnDisk()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("config.ini");

        var ini = new IniFile();
        ini.Set("launcher", "gamePath", @"D:\SteamLibrary\steamapps\common\Raft");
        ini.Set("client", "language", "zh-CN");
        ini.Save(path);

        var loaded = IniFile.Load(path);

        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Raft", loaded.Get("launcher", "gamePath"));
        Assert.Equal("zh-CN", loaded.Get("client", "language"));
    }

    [Fact]
    public void Load_MissingFileReturnsEmptyIni()
    {
        using var temp = new TempFolder();

        var ini = IniFile.Load(temp.Combine("nope.ini"));

        Assert.Empty(ini.Sections);
    }

    [Fact]
    public void ConfigStore_PreservesUnknownSectionsAndKeys()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("config.ini");

        // 模拟官方 HMLCore.exe 写过、本客户端不认识的内容。
        File.WriteAllText(path, """
            [launcher]
            gamePath = C:\Raft
            someOfficialOnlyKey = keepme

            [futureSection]
            foo = bar
            """);

        var store = new ConfigStore(path);
        var config = store.Load();
        config.Client.Theme = "dark";
        store.Save(config);

        var reloaded = IniFile.Load(path);

        // 本客户端写的键要生效……
        Assert.Equal("dark", reloaded.Get("client", "theme"));
        Assert.Equal(@"C:\Raft", reloaded.Get("launcher", "gamePath"));

        // ……但不能抹掉别人写的东西，否则和官方混用会互相清配置。
        Assert.Equal("keepme", reloaded.Get("launcher", "someOfficialOnlyKey"));
        Assert.Equal("bar", reloaded.Get("futureSection", "foo"));
    }

    [Fact]
    public void LauncherConfig_RoundTripsThroughIni()
    {
        var config = new LauncherConfig
        {
            GamePath = @"D:\SteamLibrary\steamapps\common\Raft",
            CoreVersion = "2.8.10",
            AgreeWithTos = true,
            SkipSplashScreen = true,
            StartGameFromSteam = false,
            Branch = "indev",
            GameStartCommand = "steam://run/648800",
        };
        config.Client.Language = "en-US";
        config.Client.Theme = "dark";
        config.Client.Accent = "violet";
        config.Client.InjectionMode = InjectionMode.Doorstop;
        config.Client.CloseToTray = false;
        config.Client.MinimizeOnLaunch = false;
        config.Client.LogBufferLines = 1234;
        config.Client.TranslateTarget = "ja";

        var ini = new IniFile();
        config.WriteTo(ini);
        var back = LauncherConfig.FromIni(IniFile.Parse(ini.Serialize()));

        Assert.Equal(config.GamePath, back.GamePath);
        Assert.Equal("2.8.10", back.CoreVersion);
        Assert.True(back.AgreeWithTos);
        Assert.True(back.SkipSplashScreen);
        Assert.False(back.StartGameFromSteam);
        Assert.Equal("indev", back.Branch);
        Assert.Equal("en-US", back.Client.Language);
        Assert.Equal("dark", back.Client.Theme);
        Assert.Equal("violet", back.Client.Accent);
        Assert.Equal(InjectionMode.Doorstop, back.Client.InjectionMode);
        Assert.False(back.Client.CloseToTray);
        Assert.False(back.Client.MinimizeOnLaunch);
        Assert.Equal(1234, back.Client.LogBufferLines);
        Assert.Equal("ja", back.Client.TranslateTarget);
    }

    [Fact]
    public void LauncherConfig_UnknownInjectionModeFallsBackToJson()
    {
        var ini = IniFile.Parse("[client]\ninjectMode = 乱写\n");

        var config = LauncherConfig.FromIni(ini);

        Assert.Equal(InjectionMode.Json, config.Client.InjectionMode);
    }
}
