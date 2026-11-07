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

    public JsonInjector(RmlPaths paths, InjectionBackup backup)
    {
        _paths = paths;
        _backup = backup;
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
                var root = ParseRoot(runtimeJson);
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

        if (state == InjectionState.Injected && (runtimeBroken || scriptingBroken))
        {
            state = InjectionState.Corrupted;
        }

        return Task.FromResult(new InjectionProbeResult(state, Mode, details));
    }

    public async Task<InjectionResult> ApplyAsync(string gamePath, CancellationToken ct = default)
    {
        var sourceDll = _paths.AssemblyLoaderDll;
        if (!File.Exists(sourceDll))
        {
            return InjectionResult.Fail(
                "缺少 AssemblyLoader.dll，请先更新 core。",
                $"期望位置：{sourceDll}");
        }

        _backup.BackupFiles(gamePath, BackupTargets);

        var details = new List<string>();

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

        var runtimeRoot = ParseRoot(runtimeJson);
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
            WriteDocument(runtimeJson, new JsonObject { ["root"] = runtimeRoot });
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

        var scriptingDoc = ParseDocument(scriptingJson);
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
                var root = ParseRoot(runtimeJson);
                var removed = RemoveRuntimeEntries(root);
                if (removed > 0)
                {
                    WriteDocument(runtimeJson, new JsonObject { ["root"] = root });
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

    private static JsonArray ParseRoot(string path)
    {
        var doc = ParseDocument(path);
        if (doc["root"] is JsonArray root)
        {
            return root;
        }

        throw new JsonException("缺少 root 数组。");
    }

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

        if (doc["names"] is JsonArray names)
        {
            for (var i = names.Count - 1; i >= 0; i--)
            {
                if (string.Equals(names[i]?.GetValue<string>(), InjectedManagedFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    names.RemoveAt(i);
                    removed = true;
                }
            }
        }

        // types 与 names 平行，按相同下标移除。
        if (removed && doc["types"] is JsonArray types && types.Count > 0)
        {
            types.RemoveAt(0);
        }

        return removed;
    }
}
