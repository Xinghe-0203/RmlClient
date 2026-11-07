# RML Client 设计文档

> 一个替代 `HMLCore.exe` 的 Raft Mod Loader 启动器外壳。复用现有注入链（coremod.dll + AssemblyLoader + JSON 注入 / doorstop），重做 UI、交互、更新、日志、mod 市场。

- 状态：设计待确认
- 日期：2026-11-07
- 前置产物：对官方 `RMLLauncher.exe` 的逆向分析（不在本仓库内）

---

## 1. 目标与非目标

### 目标

1. 一个**好看**的启动器：深色 + 青蓝、云母/亚克力、大圆角卡片、左侧图标导航、有过渡动画。
2. 覆盖原版全部必要功能，并修掉逆向中发现的缺陷。
3. 真正能跑：能启动 Raft、能加载现有 19 个 mod、能自动更新 coremod。
4. 自包含单文件发布，双击即用，不要求用户装 .NET 或 Windows App Runtime。

### 非目标

- 不重写 AssemblyLoader / coremod（22MB + 943KB 的运行时注入器，重写不现实也不必要）。
- 不实现 RaftModding 的社区功能（服务器列表、好友、聊天）。
- 不做游戏内 UI（那是 coremod 的职责，本项目只做启动器外壳）。
- 不修改游戏 `Raft.exe` / `Assembly-CSharp.dll`。

---

## 2. 与现有 RML 的关系

### 目录接管

客户端**直接读写** `%APPDATA%\RaftModLoader`，与官方 HMLCore.exe 共用同一份数据。官方 exe 保留在磁盘上但不再由本客户端启动。

| 路径 | 用途 | 本客户端 |
|---|---|---|
| `%APPDATA%\RaftModLoader\config.ini` | 配置 | 读写 |
| `%APPDATA%\RaftModLoader\binaries\coremod.dll` | 运行时 coremod | 读写（更新） |
| `%APPDATA%\RaftModLoader\binaries\HMLCoreLibrary.dll` | AssemblyLoader | 读写（更新） |
| `%APPDATA%\RaftModLoader\rml.assets` | 资源包 | 读写（更新） |
| `%APPDATA%\RaftModLoader\cache\` | 缓存 | 读写 |
| `%APPDATA%\RaftModLoader\logs\` | 日志 | 读 |
| `<gamePath>\mods\*.rmod` | mod 文件 | 读写 |
| `<gamePath>\Raft_Data\Managed\RaftModLoader_AssemblyLoader.dll` | JSON 注入产物 | 读写 |
| `<gamePath>\Raft_Data\RuntimeInitializeOnLoads.json` | JSON 注入点 | 读写（备份后改） |
| `<gamePath>\Raft_Data\ScriptingAssemblies.json` | JSON 注入点 | 读写（备份后改） |
| `<gamePath>\doorstop_config.ini` / `winhttp.dll` / `RaftModLoader.dll` | doorstop 注入产物 | 读写（可选模式） |

### config.ini 契约

保持与官方完全一致的格式与键名，因为 coremod 与 AssemblyLoader 都会读它：

```ini
[launcher]
gamePath = <Steam 库>\steamapps\common\Raft
coreVersion = 2.8.10
agreeWithTOS = 1
skipSplashScreen = False
startGameFromSteam = True
branch = public
gameStartCommand = steam://run/648800
```

新增键（官方读不到会忽略，安全）：

```ini
[client]
language = zh-CN
theme = dark
accent = cyan
injectMode = json
closeToTray = True
```

### 注入模式

两种都实现，默认 JSON（当前机器上生效的模式），设置里可切。

- **JSON 注入**：拷 `RaftModLoader_AssemblyLoader.dll` 到 `Raft_Data\Managed\`，在 `RuntimeInitializeOnLoads.json` 的 `root[0]` 插入 `AssemblyLoader/Entrypoint/Start`，在 `ScriptingAssemblies.json` 的 `names[0]` 插入 DLL 名、`types[0]` 插入 `1`。**改动前必须备份原始 JSON**（官方实现不备份，这是缺陷）。
- **doorstop 注入**：拷 `winhttp.dll`、写 `doorstop_config.ini`、拷 `RaftModLoader.dll` 到游戏根目录。`ignore_disable_switch` 默认 false（现代版），兼容旧版 Raft 时设 true。

---

## 3. 技术栈

| 层 | 选型 | 理由 |
|---|---|---|
| UI | **WinUI 3 / Windows App SDK 1.8** | 用户指定。Fluent 控件开箱可用，云母/亚克力/圆角原生支持。 |
| 语言/运行时 | C# 13 / .NET 9 | 与现有 RML 逻辑同语言，能直接照搬逆向出来的算法。 |
| MVVM | CommunityToolkit.Mvvm 8.x | `[ObservableProperty]` / `[RelayCommand]` 源生成器，样板代码最少。 |
| 打包 | `WindowsAppSDKSelfContained=true` + `PublishSingleFile` | 自包含单文件，用户零依赖。 |
| 配置 | 手写 INI 读写 | 必须与官方 config.ini 字节级兼容，不用第三方库。 |
| HTTP | `HttpClient` + `SocketsHttpHandler` | 显式设置 `SslOptions`，修掉官方的 TLS 缺陷。 |
| JSON | `System.Text.Json` 源生成 | mod 市场 917KB 响应需要快速解析。 |
| 压缩 | `System.IO.Compression` | 读 `.rmod`（ZIP 格式）里的 `modinfo.json`。 |

**已验证**：WinUI 3 在本机可编译可运行（`_smoke` 工程，281KB exe，窗口成功创建）。注意必须显式 `-r win-x64 -p:Platform=x64`，否则 `WindowsAppSDKSelfContained requires a supported Windows architecture`。

---

## 4. 视觉设计

### 设计语言

- **底色**：`#0B0F14`（近黑蓝）主背景，`#121821` 卡片，`#1A2230` 悬浮态。
- **主色**：`#22D3EE`（青蓝），渐变到 `#0EA5E9`。用于主按钮、进度条、选中态、焦点环。
- **辅助色**：成功 `#34D399`，警告 `#FBBF24`，危险 `#F87171`。
- **文字**：主 `#E6EDF3`，次 `#8B98A9`，禁用 `#4A5568`。
- **圆角**：卡片 12px，按钮 8px，输入框 6px，头像/图标底 10px。
- **间距**：8 的倍数（8/16/24/32）。
- **字体**：`Segoe UI Variable Display`（标题）/ `Segoe UI Variable Text`（正文），回退 `Microsoft YaHei UI`。
- **材质**：窗口背景 Mica（`MicaBackdrop`），侧边栏 Acrylic（`DesktopAcrylicBackdrop`），内容区不透明以保证文字对比度。

### 布局

```
┌──────────────────────────────────────────────────────────┐
│  ●  ●  ●   (自绘标题栏，可拖动，右侧最小化/关闭)          │
├────────┬─────────────────────────────────────────────────┤
│  Logo  │                                                 │
│        │                                                 │
│ ▣ 主页 │        内容区（NavigationView + Frame）          │
│ ▤ Mods │                                                 │
│ ◈ 市场 │                                                 │
│ ▦ 日志 │                                                 │
│ ⚙ 设置 │                                                 │
│        │                                                 │
│        │                                                 │
│ v1.0.0 │                                                 │
└────────┴─────────────────────────────────────────────────┘
```

- 窗口 1100×720，最小 900×600，无系统边框（`ExtendsContentIntoTitleBar`）。
- 左侧导航 220px 固定宽，图标 + 文字，选中项左侧 3px 青色竖条 + 背景微亮。
- 内容区滚动，最大宽度 960px 居中。

### 关键页面

**主页**

```
┌────────────────────────────────────────────────────────┐
│  ┌──────────────────────────────────────────────────┐  │
│  │  Raft Mod Loader                                 │  │
│  │  Core 2.8.10 · 已是最新 · 分支 public            │  │
│  │                                                  │  │
│  │        [   ▶  启动游戏  ]                        │  │
│  │                                                  │  │
│  │  ☑ 跳过启动画面   ☑ 通过 Steam 启动             │  │
│  └──────────────────────────────────────────────────┘  │
│                                                        │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐                  │
│  │ 19      │ │ 2.8.10  │ │ JSON    │                  │
│  │ 已装 Mod│ │ Core版本│ │ 注入模式│                  │
│  └─────────┘ └─────────┘ └─────────┘                  │
│                                                        │
│  最近日志                                              │
│  ┌──────────────────────────────────────────────────┐  │
│  │ [18:42:11] ModLoader initialized                 │  │
│  │ [18:42:12] Loading 19 mods...                    │  │
│  └──────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────┘
```

主按钮 220×56，青色渐变，hover 时上浮 2px + 阴影增强，点击有 0.95 缩放回弹。

**Mods 页**：虚拟化列表，每行 = 图标 48×48 + 名称/作者/版本 + 启用开关 + 更多菜单（卸载/打开所在文件夹/查看更新）。顶部有搜索框 + 筛选（全部/已启用/有更新）。支持拖入 `.rmod` 文件安装，拖入时整页显示青色虚线边框。

**市场页**：首次进入拉 `/api/v1/mods`（917KB 全量），本地缓存 1 小时。顶部搜索框做本地过滤（API 不支持搜索）。卡片网格 3 列，每张卡 = banner 图 + 标题 + 作者 + 分类徽章 + 下载量 + 安装按钮。

**日志页**：实时 tail `%APPDATA%\RaftModLoader\logs\hloader.log` 与游戏日志，等宽字体，按级别着色（INFO 灰 / WARN 黄 / ERROR 红），底部有「自动滚动」开关和「清空」。

**设置页**：分组卡片 —— 游戏（路径选择器 + 自动探测）、更新（分支下拉 + 手动检查）、注入（模式切换 + 修复/还原按钮）、外观（主题/强调色/语言）、关于。

### 动效

- 页面切换：`Frame.Navigate` 默认 `EntranceNavigationTransitionInfo`（内容淡入 + 轻微上移）。
- 列表项进入：`RepositionThemeTransition`，错峰 20ms。
- 开关/按钮：`PointerOver` 状态 150ms 缓动。
- 进度条：下载时青色流光。
- 启动时：Logo 从 0.8 缩放到 1.0 + 淡入，300ms `CubicEase`。

**约束**：所有动效遵循系统「减少动画」设置（读 `UISettings.AnimationsEnabled`）。

---

## 5. 架构

```
RmlClient.sln
├─ src/RmlClient.App/            WinUI 3 应用（视图 + ViewModel + DI 装配）
│   ├─ App.xaml(.cs)
│   ├─ Views/                    HomePage / ModsPage / MarketPage / LogsPage / SettingsPage / OnboardingWindow
│   ├─ ViewModels/
│   ├─ Controls/                 ModCard / StatTile / LogViewer / PathPicker
│   ├─ Themes/                   Colors.xaml / Styles.xaml / Brushes.xaml
│   └─ Assets/
├─ src/RmlClient.Core/           纯 .NET 类库，无 UI 依赖，可单测
│   ├─ Configuration/            ConfigFile (INI) / LauncherConfig
│   ├─ Game/                     SteamLocator / GameInstall / ProcessLauncher
│   ├─ Injection/                IInjector / JsonInjector / DoorstopInjector / InjectionState
│   ├─ Mods/                     RmodReader / ModScanner / ModInstaller / ModState
│   ├─ Updates/                  CoreUpdater / ManifestClient / HashVerifier
│   ├─ Market/                   MarketClient / MarketCache / ModMetadata
│   ├─ Logging/                  LogTailer / LogEntry / LogLevel
│   └─ Diagnostics/              PathProbe / EnvironmentReport
└─ tests/RmlClient.Core.Tests/   xUnit
```

### 依赖方向

`App → Core`，Core 不反向依赖 App。Core 里不出现 `Microsoft.UI.*`。

### 关键接口

```csharp
public interface IInjector
{
    InjectionMode Mode { get; }
    Task<InjectionState> ProbeAsync(GameInstall game, CancellationToken ct);
    Task<InjectionResult> ApplyAsync(GameInstall game, CancellationToken ct);
    Task<InjectionResult> RemoveAsync(GameInstall game, CancellationToken ct);
    Task<InjectionResult> RepairAsync(GameInstall game, CancellationToken ct);
}

public interface IHashVerifier
{
    Task<string> ComputeSha512Async(string path, CancellationToken ct);
    bool Verify(string expected, string actual);
}
```

`ProbeAsync` 必须返回**四态**：`NotInjected` / `Injected` / `PartiallyInjected` / `Corrupted`。官方的实现只有「文件在不在」，无法诊断半损坏状态。

---

## 6. 功能规格

### 6.1 首次启动引导

首次启动（`agreeWithTOS != 1` 或 gamePath 无效）进入引导窗口，4 步：

1. **欢迎**：Logo + 一句话说明。
2. **选择游戏**：自动探测 Steam 库（读 `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath` + `libraryfolders.vdf` 的所有 `path`），列出找到的 Raft 安装；也可手动浏览。校验：目录下存在 `Raft.exe` 与 `Raft_Data\Managed\Assembly-CSharp.dll`。
3. **选择分支**：public / indev，显示各自当前 core 版本。
4. **同意条款**：勾选后写 `agreeWithTOS=1`，然后下载 coremod + assets 并注入。

进度用一条分步进度条（探测 → 下载 coremod → 下载 assets → 注入 → 完成），每步有独立状态图标。

### 6.2 主页与启动

- 启动前检查链：游戏路径有效 → coremod 存在且 hash 匹配 → 注入状态正常 → 游戏未在运行。任一不满足，主按钮变为「修复并启动」并说明原因。
- 启动方式二选一：`Process.Start("<gamePath>\Raft.exe")` 或 `Process.Start("steam://run/648800")`（读 `startGameFromSteam`）。
- 启动后写 `pid.txt`（AssemblyLoader 的 PID 握手需要），客户端最小化到托盘。
- **修掉官方缺陷**：官方 `-DEBUG` 且无 exe 时会永久卡在 "Checking for launcher updates..."，本客户端所有异步操作都有超时与取消，UI 永不无限等待。

### 6.3 Core 更新

- `GET https://fastdl.raftmodding.com/data/client.php?branch=<branch>`，15s 超时。
- 解析 `version / coremod_url / assets_url / coremod_hash / assets_hash / rawChangelog / fullChangelogUrl`。
- **安全加固**（相对官方）：
  1. `coremod_url` / `assets_url` 必须 `https` 且 host 属于白名单 `{fastdl.raftmodding.com, www.raftmodding.com}`，用 `Uri.Host` 精确比较，**不用字符串前缀**（官方 `StartsWith` 可被 `fastdl.raftmodding.com.evil.tld` 绕过）。
  2. 下载到临时文件，校验 SHA512 通过后才原子替换（官方直接覆盖，失败会留下半个文件）。
  3. 下载超时 + 取消 + 断点信息（失败可重试，不用从头）。
  4. 显示进度与速率，可暂停/取消。
- 校验失败：删除临时文件，保留旧版本，明确提示「校验失败，已保留现有 core」。

### 6.4 Mod 管理

- 扫描 `<gamePath>\mods\*.rmod`（含 `.lnk`）。用 `ZipArchive` 读每个包的 `modinfo.json`，取 `name / author / version / description / icon / banner / updateUrl`。
- **启用/禁用**：官方通过把文件重命名来实现（coremod 只认 `*.rmod`）。本客户端用同样的机制：禁用 = 重命名为 `<name>.rmod.disabled`。**但必须先验证 coremod 确实只扫 `*.rmod`**（`ModManagerPage.cs:121,581` 已确认：`x.Name.ToLower().EndsWith(".rmod")`），所以 `.rmod.disabled` 不会被加载。这点与官方 UI 行为一致。
- **拖入安装**：接受 `.rmod` 文件，校验 ZIP 结构 + `modinfo.json` 存在 + slug 合法（`^(?!\d+$)[a-z0-9\-._]+$`，≤64），然后复制到 mods 目录。**不信任包内文件名**（官方 `ModInstaller.cs:212` 直接用 JSON 里的文件名索引字典会抛异常，本客户端只按固定名 `modinfo.json` 取）。
- **卸载**：移到回收站（`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` + `RecycleOption.SendToRecycleBin`），不是直接删。
- **冲突检测**：比较两个 mod 的 `modinfo.json` 中 `excludedFiles` 与已知的资源路径重叠；同时按 `name` 判重。

### 6.5 Mod 市场

- `GET https://www.raftmodding.com/api/v1/mods` → 917KB 全量 JSON 数组（**已实测：不支持 page/limit/search/category 参数，全部返回同一份**）。
- 客户端本地缓存到 `%APPDATA%\RaftModLoader\cache\market.json`，1 小时 TTL。
- 搜索/分类/排序全部本地做。
- 单个 mod 详情：`GET /api/v1/mods/<id>`（**注意用 `id` 不是 slug**，如 `morestorages` 而非 `more-storages`）。
- 安装：`https://www.raftmodding.com<downloadUrl>`，下载到临时文件 → 校验（API 未提供 hash，只能校验 ZIP 结构与 modinfo.json）→ 移到 mods 目录。
- 版本回滚：详情页列出 `mod-versions[]`，可下任一历史版本。

### 6.6 日志

- tail `%APPDATA%\RaftModLoader\logs\hloader.log`，用 `FileStream` + `FileShare.ReadWrite` 打开，`FileSystemWatcher` 监听变更。
- 也读游戏日志 `<gamePath>\Player.log`（Unity 默认输出）。
- 行内解析 `[HH:mm:ss]` 与 `[ERROR]/[WARN]/[Info]` 前缀着色。
- 内存环形缓冲 5000 行，避免长时间运行吃内存。

### 6.7 设置与托盘

- 托盘图标（`TaskbarIcon` 用 Win32 `Shell_NotifyIcon`，WinUI 3 无内置）：显示/退出。
- 关闭窗口默认最小化到托盘（可关）。
- 设置项持久化到 `config.ini` 的 `[client]` 段。

---

## 7. 相对官方的修复清单

| # | 官方问题 | 本客户端 |
|---|---|---|
| 1 | 更新 URL 用 `StartsWith` 前缀比较 | `Uri.Host` 精确白名单 |
| 2 | coremod/assets 下载完全无域名校验 | 同上 |
| 3 | 直接覆盖目标文件，失败留半个文件 | 临时文件 + 原子替换 |
| 4 | 下载回调不检查 `e.Error` | 每步检查并上报 |
| 5 | `while(...) await Task.Delay(1)` 忙等 | `Task.WhenAll` |
| 6 | 无超时的 `DownloadStringTaskAsync` | 全链路 `CancellationToken` + 超时 |
| 7 | JSON 注入不备份原文件 | 改前备份到 `cache\backup\`，可一键还原 |
| 8 | 卸载不清 `.rmod` 关联与 `rmllauncher` scheme | 完整清理 |
| 9 | 禁用 mod 靠改名但 UI 不提示 | 明确状态 + 可撤销 |
| 10 | 崩溃日志自动传 pastebin（含本地路径/用户名） | 默认关闭，需显式同意，可预览内容 |
| 11 | `FileInfo.Length` 在文件不存在时抛异常 | 先 `File.Exists` 再取长度 |
| 12 | 无 UAC 提权但写游戏目录可能失败无提示 | 写失败时明确提示需要管理员权限并给出指引 |

---

## 8. 测试策略

- **Core 层单测**（xUnit）：INI 读写往返、hash 校验、ZIP modinfo 解析、URI 白名单（含 `fastdl.raftmodding.com.evil.tld` 这类绕过用例）、注入状态四态判定、配置迁移。
- **集成测试**（手动，本机真实环境）：探测 `<Steam 库>\steamapps\common\Raft`、扫描 mod 目录、切换注入模式、实际启动游戏。
- **UI 冒烟**：每页能加载、不崩、暗色下对比度达标。
- 不做自动化 UI 测试（WinUI 3 的 UI 自动化成本过高，收益低）。

---

## 9. 风险

| 风险 | 影响 | 缓解 |
|---|---|---|
| WinUI 3 自包含单文件体积大（150-200MB） | 分发不便 | 接受；或提供框架依赖版作为备选 |
| WinUI 3 在部分机器上 XAML 加载失败 | 无法启动 | 发布前在干净 VM 验证；保留 WPF 备选方案 |
| coremod 只认 `*.rmod`，改名禁用可能与游戏内 UI 状态不同步 | mod 状态显示不一致 | 客户端为唯一权威，启动前重写状态 |
| 接管 `%APPDATA%\RaftModLoader` 与官方 exe 冲突 | 用户混用导致状态错乱 | 首次接管时检测并提示；提供「还原为官方」按钮 |
| 市场 API 无 hash，无法校验下载完整性 | 中间人可替换 mod | 至少校验 HTTPS 证书（不禁用）、ZIP 结构、modinfo 一致性；提示用户风险 |

---

## 10. 里程碑

1. **M1 骨架**：解决方案 + Core 库 + WinUI 外壳 + 导航 + 主题（可运行的空壳，5 页可切换）。
2. **M2 环境**：Steam 探测、游戏校验、config.ini 读写、注入探测（只读）。
3. **M3 更新链**：ManifestClient + CoreUpdater + hash 校验 + 进度 UI。
4. **M4 Mod 管理**：扫描、解析、启用禁用、拖入安装、卸载。
5. **M5 注入与启动**：JSON/doorstop 双模式写入与还原、启动游戏、托盘。
6. **M6 市场**：全量拉取、本地缓存、搜索筛选、安装、版本回滚。
7. **M7 打磨**：引导页、日志页、设置页、动效、中英双语、打包发布。
