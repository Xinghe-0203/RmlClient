using System.Text.Json.Nodes;
using RmlClient.Core.Configuration;
using RmlClient.Core.Injection;

namespace RmlClient.Core.Tests;

/// <summary>
/// JSON 注入的四态探测与往返改写。
///
/// 这些用例都在临时目录里伪造一个最小的游戏目录结构，
/// 不碰真实安装。注入器只依赖 Raft_Data 下的两个 JSON 与 Managed 目录，
/// 所以伪造这三个就足够覆盖全部逻辑。
/// </summary>
public class JsonInjectorTests
{
    private sealed class FakeGame : IDisposable
    {
        private readonly TempFolder _temp = new("rmlclient-game");

        public FakeGame()
        {
            Directory.CreateDirectory(Path.Combine(Root, "Raft_Data", "Managed"));

            File.WriteAllText(
                RuntimeJson,
                """{"root":[{"assemblyName":"UnityEngine","nameSpace":"UnityEngine","className":"X","methodName":"Y","loadTypes":1,"isUnityClass":true}]}""");

            File.WriteAllText(
                ScriptingJson,
                """{"names":["UnityEngine.dll","UnityEngine.AIModule.dll"],"types":[2,2]}""");
        }

        public string Root => _temp.Path;

        public string RuntimeJson => Path.Combine(Root, "Raft_Data", "RuntimeInitializeOnLoads.json");

        public string ScriptingJson => Path.Combine(Root, "Raft_Data", "ScriptingAssemblies.json");

        public string ManagedDll => Path.Combine(Root, "Raft_Data", "Managed", "RaftModLoader_AssemblyLoader.dll");

        /// <summary>造一个注入器，它用的 RmlPaths 指向临时数据目录，loader 也指向临时文件。</summary>
        public JsonInjector CreateInjector(string loaderSourcePath)
        {
            var paths = new RmlPaths(Path.Combine(Root, "appdata", "RaftModLoader"));
            Directory.CreateDirectory(paths.Binaries);
            File.Copy(loaderSourcePath, paths.AssemblyLoaderDll, overwrite: true);

            return new JsonInjector(paths, new InjectionBackup(paths), new LoaderProvisioner(paths));
        }

        public void Dispose() => _temp.Dispose();
    }

    /// <summary>
    /// 造一个假的 HMLCoreLibrary.dll。
    ///
    /// LoaderProvisioner 会校验长度与 SHA256，这里造的是「已存在但长度不符」的文件，
    /// 于是 Ensure() 会去解内嵌资源 —— 测试机上内嵌资源确实在，解出来就是真加载器。
    /// 为了让用例快且不依赖 8MB 解压，改用长度足够的假文件走「保留原文件」分支。
    /// </summary>
    private static string CreateFakeLoader(TempFolder temp)
    {
        var path = temp.Combine("fake-loader.dll");
        // 比 ExpectedLength 大，触发「已存在更新的加载器，保留原文件」。
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(LoaderProvisioner.ExpectedLength + 1);
        return path;
    }

    [Fact]
    public async Task Probe_NotInjected_OnCleanGame()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        var probe = await injector.ProbeAsync(game.Root);

        Assert.Equal(InjectionState.NotInjected, probe.State);
        Assert.False(probe.IsHealthy);
        Assert.Equal(InjectionMode.Json, probe.Mode);
    }

    [Fact]
    public async Task Apply_Then_ProbeReportsInjected()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        var result = await injector.ApplyAsync(game.Root);

        Assert.True(result.Success);
        Assert.True(File.Exists(game.ManagedDll));

        var probe = await injector.ProbeAsync(game.Root);
        Assert.Equal(InjectionState.Injected, probe.State);
        Assert.True(probe.IsHealthy);
    }

    [Fact]
    public async Task Apply_IsIdempotent()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        await injector.ApplyAsync(game.Root);
        await injector.ApplyAsync(game.Root);

        var runtime = JsonNode.Parse(File.ReadAllText(game.RuntimeJson))!["root"]!.AsArray();
        var scripting = JsonNode.Parse(File.ReadAllText(game.ScriptingJson))!;

        // 重复注入不能把条目插两遍。
        Assert.Single(runtime.Where(n => (string?)n?["assemblyName"] == "AssemblyLoader"));
        Assert.Single(scripting["names"]!.AsArray().Where(n => (string?)n == "RaftModLoader_AssemblyLoader.dll"));
    }

    [Fact]
    public async Task Apply_InsertsEntryAtFront()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        await injector.ApplyAsync(game.Root);

        var runtime = JsonNode.Parse(File.ReadAllText(game.RuntimeJson))!["root"]!.AsArray();
        Assert.Equal("AssemblyLoader", (string?)runtime[0]?["assemblyName"]);
        Assert.Equal("Entrypoint", (string?)runtime[0]?["className"]);

        var scripting = JsonNode.Parse(File.ReadAllText(game.ScriptingJson))!;
        Assert.Equal("RaftModLoader_AssemblyLoader.dll", (string?)scripting["names"]![0]);
        Assert.Equal(1, (int)scripting["types"]![0]!);
    }

    [Fact]
    public async Task Probe_PartiallyInjected_WhenOnlyDllPresent()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        // 只拷 DLL、不改 JSON —— 官方在拷贝中途崩溃就会留下这种状态。
        File.Copy(CreateFakeLoader(temp), game.ManagedDll);

        var probe = await injector.ProbeAsync(game.Root);

        Assert.Equal(InjectionState.PartiallyInjected, probe.State);
    }

    [Fact]
    public async Task Probe_PartiallyInjected_WhenJsonPresentButDllMissing()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        await injector.ApplyAsync(game.Root);
        File.Delete(game.ManagedDll);

        var probe = await injector.ProbeAsync(game.Root);

        Assert.Equal(InjectionState.PartiallyInjected, probe.State);
    }

    [Fact]
    public async Task Probe_Corrupted_WhenJsonUnparsable()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        await injector.ApplyAsync(game.Root);
        File.WriteAllText(game.RuntimeJson, "{ this is not json");

        var probe = await injector.ProbeAsync(game.Root);

        // 注入痕迹在、内容坏了 —— 应该提示「损坏」而不是「正常」。
        Assert.Equal(InjectionState.Corrupted, probe.State);
        Assert.False(probe.IsHealthy);
    }

    [Fact]
    public async Task Remove_CleansEverything()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        await injector.ApplyAsync(game.Root);
        var result = await injector.RemoveAsync(game.Root);

        Assert.True(result.Success);
        Assert.False(File.Exists(game.ManagedDll));

        var probe = await injector.ProbeAsync(game.Root);
        Assert.Equal(InjectionState.NotInjected, probe.State);
    }

    [Fact]
    public async Task Remove_KeepsUnrelatedScriptingTypesAligned()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        await injector.ApplyAsync(game.Root);
        await injector.RemoveAsync(game.Root);

        var doc = JsonNode.Parse(File.ReadAllText(game.ScriptingJson))!;
        var names = doc["names"]!.AsArray();
        var types = doc["types"]!.AsArray();

        // names 与 types 必须等长且一一对应 —— 早期实现固定删 types[0]，
        // 会把别的程序集的类型标志删错位。
        Assert.Equal(names.Count, types.Count);
        Assert.Equal(2, names.Count);
        Assert.Equal("UnityEngine.dll", (string?)names[0]);
        Assert.Equal(2, (int)types[0]!);
    }

    [Fact]
    public async Task Repair_FixesHalfBrokenInjection()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        await injector.ApplyAsync(game.Root);
        File.Delete(game.ManagedDll);

        var result = await injector.RepairAsync(game.Root);

        Assert.True(result.Success);
        var probe = await injector.ProbeAsync(game.Root);
        Assert.Equal(InjectionState.Injected, probe.State);
    }

    [Fact]
    public async Task Repair_SurvivesCorruptedJson()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        await injector.ApplyAsync(game.Root);
        File.WriteAllText(game.RuntimeJson, "{ broken");

        // 修复不该因为「已损坏」而崩溃（早期版本在这里抛
        // InvalidOperationException: The node already has a parent）。
        var result = await injector.RepairAsync(game.Root);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Apply_FailsWhenRuntimeJsonMissing()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        File.Delete(game.RuntimeJson);

        var result = await injector.ApplyAsync(game.Root);

        Assert.False(result.Success);
        Assert.Contains("RuntimeInitializeOnLoads.json", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_FailsWhenScriptingJsonMissing()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));

        File.Delete(game.ScriptingJson);

        var result = await injector.ApplyAsync(game.Root);

        Assert.False(result.Success);
        Assert.Contains("ScriptingAssemblies.json", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_BacksUpOriginalFiles()
    {
        using var temp = new TempFolder();
        using var game = new FakeGame();
        var injector = game.CreateInjector(CreateFakeLoader(temp));
        var original = File.ReadAllText(game.RuntimeJson);

        await injector.ApplyAsync(game.Root);

        // 备份是官方没有的能力：出问题要能一键还原到注入前。
        var backupDir = Path.Combine(game.Root, "appdata", "RaftModLoader", "cache", "backup", "injection");
        Assert.True(Directory.Exists(backupDir));
        Assert.True(File.Exists(Path.Combine(backupDir, "injection-manifest.json")));

        var backupFile = Path.Combine(backupDir, "Raft_Data_RuntimeInitializeOnLoads.json");
        Assert.True(File.Exists(backupFile));
        Assert.Equal(original, File.ReadAllText(backupFile));
    }
}
