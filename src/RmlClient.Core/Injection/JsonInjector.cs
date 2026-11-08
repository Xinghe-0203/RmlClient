using System.Text.Json;
using System.Text.Json.Nodes;
using RmlClient.Core.Configuration;

namespace RmlClient.Core.Injection;

/// <summary>
/// JSON 注入器：把 AssemblyLoader 挂进 Unity 的启动链。
///
/// 手法完全照搬官方 <c>Launcher\UnityJSONAssemblyLoader.cs:38-78</c>：
///   1. 把内嵌的 AssemblyLoader.dll 拷成
///      <c>Raft_Data\Managed\RaftModLoader_AssemblyLoader.dll</c>
///   2. 在 <c>Raft_Data\RuntimeInitializeOnLoads.json</c> 的 root[0] 插入
///      <c>{assemblyName:"AssemblyLoader", nameSpace:"AssemblyLoader",
///        className:"Entrypoint", methodName:"Start", loadTypes:1, isUnityClass:false}</c>
///   3. 在 <c>Raft_Data\ScriptingAssemblies.json</c> 的 names[0] 插入 DLL 文件名、
///      types[0] 插入 1
///
/// 相对官方的改进：
///   - 改动前备份原始 JSON（官方不备份）
///   - 用 JSON DOM 解析而不是字符串拼接，容忍任意格式与原文件缩进
///   - 探测时能区分「没注入 / 注入一半 / 损坏」
///   - 幂等：重复 Apply 不会重复插入条目
/// </summary>
public sealed class JsonInjector : IInjector
{
    public const string InjectedAssemblyName = "AssemblyLoader";
    public const string InjectedNamespace = "AssemblyLoader";
    public const string InjectedClassName = "Entrypoint";
    public const string InjectedMethodName = "Start";
    public const string InjectedManagedFileName = "RaftModLoader_AssemblyLoader.dll";

    private static readonly string[] ManagedFiles = { InjectedManagedFileName };

    private static readonly string[] BackupTargets =
    {
        @"Raft_Data\RuntimeInitializeOnLoads.json",
        @"Raft_Data\ScriptingAssemblies.json",
        @"Raft_Data\Managed\" + InjectedManagedFileName,
    };

    private readonly RmlPaths _paths;
    private readonly InjectionBackup _backup;
    private readonly LoaderProvisioner _loader;

    public JsonInjector(RmlPaths paths, InjectionBackup backup, LoaderProvisioner? loader = null)
    {
        _paths = paths;
        _backup = backup;
        _loader = loader ?? new LoaderProvisioner(paths);
    }

    public InjectionMode Mode => InjectionMode.Json;

    public Task<InjectionProbeResult> ProbeAsync(string gamePath, CancellationToken ct = default)
    {
        var details = new List<string>();

        var managedDll = RmlPaths.GameInjectedLoaderDll(gamePath);
        var runtimeJson = RmlPaths.GameRuntimeInitializeOnLoadsJson(gamePath);
        var scriptingJson = RmlPaths.GameScriptingAssembliesJson(gamePath);

        var dllPresent = File.Exists(managedDll);
        details.Add(dllPresent ? $"已找到注入 DLL：{managedDll}" : $"缺少注入 DLL：{managedDll}");

        var runtimeEntry = false;
        var runtimeBroken = false;
        if (!File.Exists(runtimeJson))
        {
            details.Add($"缺少 {runtimeJson}");
        }
        else
        {
            try
            {
                var root = GetRoot(ParseRootDocument(runtimeJson));
                runtimeEntry = HasRuntimeEntry(root);
                details.Add(runtimeEntry
                    ? "RuntimeInitializeOnLoads.json 含 AssemblyLoader 启动项。"
                    : "RuntimeInitializeOnLoads.json 不含 AssemblyLoader 启动项。");
            }
            catch (JsonException ex)
            {
                runtimeBroken = true;
                details.Add($"RuntimeInitializeOnLoads.json 解析失败：{ex.Message}");
            }
        }

        var scriptingEntry = false;
        var scriptingBroken = false;
        if (!File.Exists(scriptingJson))
        {
            details.Add($"缺少 {scriptingJson}");
        }
        else
        {
            try
            {
                var doc = ParseDocument(scriptingJson);
                scriptingEntry = HasScriptingEntry(doc);
                details.Add(scriptingEntry
                    ? "ScriptingAssemblies.json 已登记 AssemblyLoader。"
                    : "ScriptingAssemblies.json 未登记 AssemblyLoader。");
            }
            catch (JsonException ex)
            {
                scriptingBroken = true;
                details.Add($"ScriptingAssemblies.json 解析失败：{ex.Message}");
            }
        }

        var state = (dllPresent, runtimeEntry, scriptingEntry) switch
        {
            (true, true, true) => InjectionState.Injected,
            (false, false, false) => InjectionState.NotInjected,
            _ => InjectionState.PartiallyInjected,
        };

        // 只要有注入痕迹、且某个 JSON 解析不了，就是「损坏」而不是「注入了一半」：
        // 后者可以靠 Repair 补齐，前者必须从备份还原，两者的修复动作完全不同。
        var hasTrace = dllPresent || runtimeEntry || scriptingEntry;
        if (hasTrace && (runtimeBroken || scriptingBroken))
        {
            state = InjectionState.Corrupted;
        }

        return Task.FromResult(new InjectionProbeResult(state, Mode, details));
    }

    public async Task<InjectionResult> ApplyAsync(string gamePath, CancellationToken ct = default)
    {
        // 先确保 binaries\HMLCoreLibrary.dll 在。全新安装的机器上它不存在
        // （官方启动器是在安装阶段释放的，本客户端没有那一步），
        // 这里自动从内置资源解出来，避免用户看到「缺少 AssemblyLoader.dll，请先更新 core。」
        var provision = _loader.Ensure();
        if (!provision.Success)
        {
            return InjectionResult.Fail(
                "缺少 AssemblyLoader.dll，请先更新 core。",
                $"{provision.Message}（期望位置：{provision.Path}）");
        }

        var sourceDll = _paths.AssemblyLoaderDll;
        if (!File.Exists(sourceDll))
        {
            return InjectionResult.Fail(
                "缺少 AssemblyLoader.dll，请先更新 core。",
                $"期望位置：{sourceDll}");
        }

        _backup.BackupFiles(gamePath, BackupTargets);

        var details = new List<string>();
        if (provision.Changed)
        {
            details.Add(provision.Message);
        }

        var managedTarget = RmlPaths.GameInjectedLoaderDll(gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(managedTarget)!);
        File.Copy(sourceDll, managedTarget, overwrite: true);
        details.Add($"已写入 {managedTarget}");

        var runtimeJson = RmlPaths.GameRuntimeInitializeOnLoadsJson(gamePath);
        if (!File.Exists(runtimeJson))
        {
            return InjectionResult.Fail(
                "找不到 RuntimeInitializeOnLoads.json，游戏可能不是受支持的版本。",
                runtimeJson);
        }

        // 注意：必须复用同一个 JsonNode 树写回。
        // 早期实现是 `new JsonObject { ["root"] = runtimeRoot }`，
        // 而 runtimeRoot 本身已经挂在解析出来的文档节点下，
        // 再塞进新对象会抛 InvalidOperationException: The node already has a parent，
        // 表现为「修复注入」直接崩溃。
        JsonNode runtimeDoc;
        try
        {
            runtimeDoc = ParseRootDocument(runtimeJson);
        }
        catch (JsonException ex)
        {
            // JSON 被写坏了就没法增量改。先试备份还原，还原不了就明确失败，
            // 绝不能让异常冒到 UI 线程（旧版在这里直接崩溃）。
            var restored = _backup.Restore(gamePath);
            if (restored.Count == 0)
            {
                return InjectionResult.Fail(
                    "RuntimeInitializeOnLoads.json 已损坏，且没有可用的备份。请在 Steam 里校验游戏文件完整性。",
                    $"解析失败：{ex.Message}");
            }

            details.AddRange(restored.Select(r => $"已从备份还原 {r}"));
            runtimeDoc = ParseRootDocument(runtimeJson);
        }

        var runtimeRoot = GetRoot(runtimeDoc);
        if (!HasRuntimeEntry(runtimeRoot))
        {
            var entry = new JsonObject
            {
                ["assemblyName"] = InjectedAssemblyName,
                ["nameSpace"] = InjectedNamespace,
                ["className"] = InjectedClassName,
                ["methodName"] = InjectedMethodName,
                ["loadTypes"] = 1,
                ["isUnityClass"] = false,
            };

            runtimeRoot.Insert(0, entry);
            WriteDocument(runtimeJson, runtimeDoc);
            details.Add("已在 RuntimeInitializeOnLoads.json 插入启动项。");
        }
        else
        {
            details.Add("RuntimeInitializeOnLoads.json 已有启动项，跳过。");
        }

        var scriptingJson = RmlPaths.GameScriptingAssembliesJson(gamePath);
        if (!File.Exists(scriptingJson))
        {
            return InjectionResult.Fail(
                "找不到 ScriptingAssemblies.json，游戏可能不是受支持的版本。",
                scriptingJson);
        }

        JsonNode scriptingDoc;
        try
        {
            scriptingDoc = ParseDocument(scriptingJson);
        }
        catch (JsonException ex)
        {
            var restored = _backup.Restore(gamePath);
            if (restored.Count == 0)
            {
                return InjectionResult.Fail(
                    "ScriptingAssemblies.json 已损坏，且没有可用的备份。请在 Steam 里校验游戏文件完整性。",
                    $"解析失败：{ex.Message}");
            }

            details.AddRange(restored.Select(r => $"已从备份还原 {r}"));
            scriptingDoc = ParseDocument(scriptingJson);
        }

        if (!HasScriptingEntry(scriptingDoc))
        {
            var names = scriptingDoc["names"] as JsonArray
                        ?? throw new InvalidOperationException("ScriptingAssemblies.json 缺少 names 数组。");
            var types = scriptingDoc["types"] as JsonArray
                        ?? throw new InvalidOperationException("ScriptingAssemblies.json 缺少 types 数组。");

            names.Insert(0, JsonValue.Create(InjectedManagedFileName));
            types.Insert(0, JsonValue.Create(1));
            WriteDocument(scriptingJson, scriptingDoc);
            details.Add("已在 ScriptingAssemblies.json 登记。");
        }
        else
        {
            details.Add("ScriptingAssemblies.json 已登记，跳过。");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return InjectionResult.Ok("JSON 注入完成。", details.ToArray());
    }

    public async Task<InjectionResult> RemoveAsync(string gamePath, CancellationToken ct = default)
    {
        var details = new List<string>();

        var managedTarget = RmlPaths.GameInjectedLoaderDll(gamePath);
        if (File.Exists(managedTarget))
        {
            File.Delete(managedTarget);
            details.Add($"已删除 {managedTarget}");
        }

        var runtimeJson = RmlPaths.GameRuntimeInitializeOnLoadsJson(gamePath);
        if (File.Exists(runtimeJson))
        {
            try
            {
                var runtimeDoc = ParseRootDocument(runtimeJson);
                var root = GetRoot(runtimeDoc);
                var removed = RemoveRuntimeEntries(root);
                if (removed > 0)
                {
                    WriteDocument(runtimeJson, runtimeDoc);
                    details.Add($"已从 RuntimeInitializeOnLoads.json 移除 {removed} 个启动项。");
                }
            }
            catch (JsonException ex)
            {
                details.Add($"RuntimeInitializeOnLoads.json 已损坏，未修改：{ex.Message}");
            }
        }

        var scriptingJson = RmlPaths.GameScriptingAssembliesJson(gamePath);
        if (File.Exists(scriptingJson))
        {
            try
            {
                var doc = ParseDocument(scriptingJson);
                if (RemoveScriptingEntry(doc))
                {
                    WriteDocument(scriptingJson, doc);
                    details.Add("已从 ScriptingAssemblies.json 注销。");
                }
            }
            catch (JsonException ex)
            {
                details.Add($"ScriptingAssemblies.json 已损坏，未修改：{ex.Message}");
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);

        return details.Count == 0
            ? InjectionResult.Ok("没有需要移除的注入痕迹。")
            : InjectionResult.Ok("JSON 注入已移除。", details.ToArray());
    }

    public async Task<InjectionResult> RepairAsync(string gamePath, CancellationToken ct = default)
    {
        // 修复 = 先移除残留再重新注入，保证状态自洽。
        var remove = await RemoveAsync(gamePath, ct).ConfigureAwait(false);
        if (!remove.Success)
        {
            return remove;
        }

        var apply = await ApplyAsync(gamePath, ct).ConfigureAwait(false);
        var details = remove.Details.Concat(apply.Details).ToArray();
        return new InjectionResult(apply.Success, apply.Success ? "注入已修复。" : apply.Message, details);
    }

    /// <summary>从备份还原游戏原始文件。这是官方没有的能力。</summary>
    public IReadOnlyList<string> RestoreFromBackup(string gamePath) => _backup.Restore(gamePath);

    private static JsonNode ParseRootDocument(string path)
    {
        var doc = ParseDocument(path);
        if (doc["root"] is JsonArray)
        {
            return doc;
        }

        throw new JsonException("缺少 root 数组。");
    }

    private static JsonArray GetRoot(JsonNode doc) => (JsonArray)doc["root"]!;

    private static JsonNode ParseDocument(string path)
    {
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text) ?? throw new JsonException("文件内容为空。");
    }

    private static void WriteDocument(string path, JsonNode node)
    {
        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(path, json);
    }

    private static bool HasRuntimeEntry(JsonArray root) =>
        root.Any(item => string.Equals(
            item?["assemblyName"]?.GetValue<string>(),
            InjectedAssemblyName,
            StringComparison.Ordinal));

    private static int RemoveRuntimeEntries(JsonArray root)
    {
        var removed = 0;
        for (var i = root.Count - 1; i >= 0; i--)
        {
            if (string.Equals(
                    root[i]?["assemblyName"]?.GetValue<string>(),
                    InjectedAssemblyName,
                    StringComparison.Ordinal))
            {
                root.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private static bool HasScriptingEntry(JsonNode doc)
    {
        if (doc["names"] is not JsonArray names)
        {
            return false;
        }

        return names.Any(n => string.Equals(
            n?.GetValue<string>(),
            InjectedManagedFileName,
            StringComparison.OrdinalIgnoreCase));
    }

    private static bool RemoveScriptingEntry(JsonNode doc)
    {
        var removed = false;
        var indexes = new List<int>();

        if (doc["names"] is JsonArray names)
        {
            for (var i = names.Count - 1; i >= 0; i--)
            {
                if (string.Equals(names[i]?.GetValue<string>(), InjectedManagedFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    names.RemoveAt(i);
                    indexes.Add(i);
                    removed = true;
                }
            }
        }

        // types 与 names 平行：必须按**相同下标**移除。
        // 早期实现固定删 types[0]，当条目不在首位时会把别的程序集类型标志删掉。
        if (doc["types"] is JsonArray types)
        {
            foreach (var index in indexes.OrderByDescending(i => i))
            {
                if (index < types.Count)
                {
                    types.RemoveAt(index);
                }
            }
        }

        return removed;
    }
}
