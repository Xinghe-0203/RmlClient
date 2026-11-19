# Raft Mod Loader Client

一个 Raft 模组加载器启动器：WinUI 3 + Windows App SDK，中英双语，浅色/深色主题。

它复用 Raft Mod Loader 官方已安装的注入链路（`coremod.dll` + `AssemblyLoader` + JSON 注入 / Doorstop），只重做界面、更新、模组管理与日志，因此可以和官方启动器共用 `%APPDATA%\RaftModLoader` 目录，装完即可接管已有的模组。

---

## 功能

| 模块 | 说明 |
| --- | --- |
| 启动 | 一键启动游戏；启动前自动校验并修复注入；可选经 Steam 启动（走 DRM + 云存档）或直接启动 `Raft.exe`。点击后按钮立刻变灰、转圈并显示「正在启动…」，成功后显示「已启动」并保持 5 秒，避免重复点击 |
| 更新 | 按分支（public / indev）拉 `client.php` 清单，SHA512 校验后原子替换 `coremod.dll` 与 `rml.assets` |
| 模组 | 扫描 `mods\*.rmod`（含 `.lnk` 指向的文件夹 mod），启用/禁用、拖入安装、卸载到回收站、显示真实图标与版本；可一键检查全部模组是否有新版本（读各 mod 的 `version.txt`），逐条或全部更新，更新后保持原有的启用/禁用状态；自动检出重名与文件重叠冲突 |
| 市场 | 拉取 raftmodding.com 全量 mod 列表，本地搜索/分类筛选/排序，一键安装，支持版本回滚；缓存 1 小时 TTL，过期后后台静默刷新 |
| 详情 | 点任意模组（市场或本地）弹窗看「这个模组是做什么的」、更新日志、版本列表 |
| 翻译 | 描述与更新日志自动译成系统默认语言，可随时切换目标语言（13 种）；市场标题、已安装模组列表与搜索也支持中文 |
| 日志 | 客户端日志（环形缓冲 + 文件轮转）、`hloader.log` 与游戏 `Player.log` 三路实时跟随；按级别着色（INFO 灰 / WARN 黄 / ERROR 红），可按级别过滤与全文搜索，可清空、导出 |
| 设置 | 语言、分支、外观（浅色/深色 + 5 种强调色）、游戏路径、注入方式、托盘、启动后收进托盘、崩溃日志上传、跳过 Unity 启动画面、还原为官方启动器 |
| 引导 | 首次启动 4 步向导：定位游戏 → 安装 Core → 注入加载器 → 同意条款并完成 |
| 托盘 | 关闭到托盘，托盘菜单可打开主界面 / 退出；客户端为**单实例**，重复启动会把已有窗口唤到前台 |
| 自更新 | 查本仓库 GitHub Releases 的最新版本，发现新版时首页提示，可一键下载安装包并运行 |

---

## 设计上的加固点

启动器要下载并执行远端文件，因此下面这些环节都按「不信任输入」处理：

| # | 常见问题 | 本客户端 |
| --- | --- | --- |
| 1 | 用字符串前缀判断下载地址来源，`https://fastdl.raftmodding.com.evil.tld/x.exe` 这类地址也能通过 | `UrlGuard` 校验 scheme + 默认端口 + **精确主机名** + IDN 全 ASCII |
| 2 | 自动跟随重定向，最终落点不再校验 | `AllowAutoRedirect = false`，手动跟随且**每一跳重新过白名单** |
| 3 | 只校验扩展名，不校验来源域名 | 每个下载地址都过 `UrlGuard` |
| 4 | 校验失败后直接覆盖目标文件，可能留下半个文件 | 先落 `.part`，校验 SHA512 通过后**原子替换** |
| 5 | 文件不存在时先取长度，异常导致提示永不出现 | 先判存在再取长度 |
| 6 | 缺少 hash 时静默跳过校验 | 无 hash 时明确拒绝安装 |
| 7 | 自定义 URI scheme 的参数未做校验 | slug 走正则 `^(?!\d+$)[a-z0-9\-._]+$` 且长度 ≤ 64 |
| 8 | 下载回调不检查错误，失败也置完成标志 | 异常路径明确返回失败 |
| 9 | 用极短间隔忙等轮询下载完成 | `TaskCompletionSource` / 顺序 await |
| 10 | 异常路径缺少超时与失败分支，界面可能永久等待 | 每步都有超时与失败分支 |
| 11 | 卸载时不清文件关联与 URI scheme | 卸载时清理注入产物并提示是否删除用户数据 |
| 12 | 改写游戏文件前不备份 | 首次改写前备份到 `cache\backup\` |

HTTP 栈严格校验证书，不使用任何全局关闭 TLS 校验的兼容路径。

---

## 翻译

详情弹窗里的描述与更新日志会自动翻译，目标语言默认跟随系统，可在弹窗内随时切换。

| 项 | 说明 |
| --- | --- |
| 主通道 | **Bing 网页版翻译**（`www.bing.com/ttranslatev3`）—— 国内可直连，译文质量较好 |
| 备用通道 | **MyMemory**（`api.mymemory.translated.net`），单次上限 500 字符，主通道失败时兜底 |
| 已排除 | Google 系（国内连不上）、百度（需 cookie 签名）、有道（302 / 返回 HTML）、LibreTranslate 公共实例（全部无响应）、SimplyTranslate（译文为空） |
| 语言 | 简体中文、繁體中文、English、日本語、한국어、Русский、Deutsch、Français、Español、Português、Italiano、Polski、Türkçe |
| 缓存 | `cache\translation.json`（正文，按 SHA256 键控）、`cache\market-titles.json`（市场标题）、`cache\local-mod-titles.json`（已安装模组标题） |
| 免翻判定 | 原文已经是目标语言（CJK / 韩文占比判定）时直接跳过，不发请求 |

实现要点：

- Bing 需要先 `GET /translator` 拿 cookie 并正则提取 `IG` / `IID` / `params_AbusePreventionHelper`（key + token），`fromLang` 必须写 `auto-detect`；单次文本上限约 900 字符，故按 800 切分。
- 长文本按换行 → 句末标点 → 空格 → 硬切的优先级分块，块间串行请求。
- 批量接口 `TranslateBatchAsync` 把多个短标题用 `\n` 拼成一次请求，返回行数对不上就退回逐条（不猜对应关系）。
- 翻译失败**永不抛异常**：返回原文并在状态栏标注「翻译不可用，显示原文」，不影响模组安装等主流程。

---

## 目录结构

```
RML/Client/
├── RmlClient.sln
├── src/
│   ├── RmlClient.Core/            # 无 UI 的业务核心（net9.0-windows）
│   │   ├── Assets/                # classdata.tpk、HMLCoreLibrary.dll.gz、winhttp.dll、doorstop_config.ini（内嵌资源）
│   │   ├── Configuration/         # IniFile、RmlPaths、LauncherConfig
│   │   ├── Diagnostics/           # HashUtil、UrlGuard、OfficialRestore、CrashReport
│   │   ├── Game/                  # SteamLocator、GameLauncher、GameManagersPatcher
│   │   ├── Injection/             # IInjector、JsonInjector、DoorstopInjector、InjectionBackup、LoaderProvisioner
│   │   ├── Logging/               # ClientLogger、LogTailer
│   │   ├── Market/                # MarketMod、MarketService、TitleTranslator
│   │   ├── Mods/                  # RmodReader、ModRepository、ModUpdateChecker、ModConflictDetector、ShortcutResolver
│   │   ├── Translation/           # TranslationService、Bing/MyMemory provider、TranslationLanguage
│   │   └── Updates/               # Manifests、RmlHttpClient、CoreUpdater、ClientUpdater、ClientVersion
│   └── RmlClient.App/             # WinUI 3 界面
│       ├── Views/                 # Dashboard / Mods / Market / Logs / Settings / Onboarding
│       ├── Controls/              # ModDetailView、ModDetailDialog、ModDetailModel、CrashReportDialog
│       ├── Themes/                # Colors.xaml、Controls.xaml
│       ├── Services/              # AppServices、Localization、WindowHelper、TrayIcon、MinSizeHelper、SingleInstance、ThemeService、MotionSettings
│       ├── Assets/app.ico         # 7 个尺寸（16–256）
│       └── app.manifest           # asInvoker，PerMonitorV2
├── tests/RmlClient.Core.Tests/    # xUnit，139 个用例
├── installer/RmlClient.iss        # Inno Setup 脚本
├── installer/ChineseSimplified.isl
├── tools/                         # 自动化脚本（纯 ASCII）
│   ├── uia.ps1                    # 按 AutomationId 点导航 + PrintWindow 截图
│   ├── click.ps1                  # 按名字/AutomationId 点击任意控件
│   ├── clickitem.ps1              # 真实鼠标点击列表项（GridView 不响应 Invoke）
│   ├── combo.ps1                  # 展开 ComboBox 并从桌面根选中弹出项
│   ├── search.ps1                 # 往搜索框输入文本并截图
│   ├── shot2.ps1                  # 最小化也能正确抓图的窗口截图
│   └── wizard.ps1                 # 驱动 Inno Setup 的 Win32 向导
├── docs/design.md                 # 设计文档
└── docs/screenshots/              # 实机截图
```

源码规模：65 个 `.cs` / `.xaml` 文件，约 15,500 行；测试 139 个用例。

---

## 测试

```powershell
dotnet test tests\RmlClient.Core.Tests\RmlClient.Core.Tests.csproj -c Release -p:Platform=x64
```

覆盖：INI 往返与未知键保留、URL 白名单（含 `fastdl.raftmodding.com.evil.tld` 等 10 种绕过用例）、SHA512、版本比较、`.rmod` 解析与 Zip-Slip、mod 安装/启用/禁用、JSON 注入四态与修复、模组冲突检测、崩溃报告脱敏。

---

## 构建

前置：.NET SDK 9、Windows App SDK 1.8（NuGet 自动还原）。

```powershell
# 调试运行（必须带 -r win-x64 -p:Platform=x64，否则 WindowsAppSDKSelfContained 报错）
dotnet build src\RmlClient.App\RmlClient.App.csproj -c Release -r win-x64 -p:Platform=x64

# 自包含文件夹发布（供安装包使用）
dotnet publish src\RmlClient.App\RmlClient.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o publish\app
```

> `WindowsAppSDKSelfContained` 要求显式架构，`dotnet build -c Release` 单独用会报
> `WindowsAppSDKSelfContained requires a supported Windows architecture`。

## 打包安装程序

需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)（`winget install JRSoftware.InnoSetup`，默认装到 `%LOCALAPPDATA%\Programs\Inno Setup 6`）：

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\RmlClient.iss
```

产物 `dist\RmlClient-Setup-1.0.2.exe`（约 66 MB，`lzma2/max` 固实压缩；未压缩的 `publish\app` 约 219 MB）。

安装行为：默认按用户安装到 `%LOCALAPPDATA%\Programs\Raft Mod Loader Client`，无需管理员；卸载时**默认保留** `%APPDATA%\RaftModLoader`（该目录与官方启动器共用，误删会丢全部 mod），可选清理。

---

## 与官方数据的兼容性

本客户端**接管**官方目录 `%APPDATA%\RaftModLoader`，不另建一套：

| 文件/目录 | 用途 | 谁写 |
| --- | --- | --- |
| `config.ini` | 官方 7 键 + 新增 `[client]` 段 7 键 | 双方，未知键保留 |
| `binaries\coremod.dll` | 核心 mod | 本客户端更新 |
| `binaries\HMLCoreLibrary.dll` | AssemblyLoader | **本客户端内嵌并自动释放**（见下）；已存在更高版本时保留原文件 |
| `rml.assets` | 资源包（约 47 MB） | 本客户端更新 |
| `pid.txt` | 启动握手（AssemblyLoader 读后立即清空） | 本客户端写 |
| `cache\backup\` | 注入备份 + `globalgamemanagers` 原始副本 | 本客户端 |
| `logs\rmlclient.log` | 本客户端日志 | 本客户端 |

游戏目录侧的注入产物与官方一致：`Raft_Data\Managed\RaftModLoader_AssemblyLoader.dll`、`Raft_Data\RuntimeInitializeOnLoads.json` 首条 `AssemblyLoader/Entrypoint/Start` 项、`Raft_Data\ScriptingAssemblies.json` 登记。

### 内置加载器

官方启动器在安装阶段会把内嵌的 `AssemblyLoader.dll` 释放到 `binaries\HMLCoreLibrary.dll`；`fastdl.raftmodding.com` 上并没有这个文件的下载端点（只有 `client.php` 给出的 `coremod.dll` 与 `rml.assets`），所以只装本客户端、从没跑过官方启动器的机器会卡在「缺少 AssemblyLoader.dll，请先更新 core」。

本客户端把该文件以 gzip 形式（22.85 MB → 8.58 MB）嵌入 `RmlClient.Core.dll`，由 `LoaderProvisioner` 在启动时与每次注入前自动释放：

| 项 | 说明 |
| --- | --- |
| 资源 | `src\RmlClient.Core\Assets\HMLCoreLibrary.dll.gz`（LogicalName `RmlClient.Core.HMLCoreLibrary.dll.gz`） |
| 期望值 | 长度 `22851072`、SHA256 `4A2880F3…EC8B99`，释放后逐一校验，不符则删除临时文件并报错 |
| 已有文件 | 长度相同 → 视为完好直接跳过；**长度更大 → 认为是更新的官方版本，保留不覆盖**；更小或缺失 → 重新释放 |
| 写入方式 | 先解到 `.tmp` 再 `File.Move(overwrite: true)`，避免中途失败留下半个 DLL |
| doorstop 模式 | `winhttp.dll`（26,112 B）与 `doorstop_config.ini` 同样内嵌；`resources\` 目录下若放了同名文件则优先用用户的 |

---

## 已知限制

- 安装包约 66 MB、自包含发布约 219 MB（Windows App SDK 自包含 + .NET 运行时），未做 trimming（WinUI 对 trimming 支持有限）。
- 仍无 Authenticode 签名：hash 校验只能证明「与服务器同一次响应一致」，不能证明来源，这一点受限于官方分发方式。
- 市场数据是全量 JSON（约 917 KB），服务端 `page`/`limit`/`search` 参数均不生效，只能本地筛选。
- 只支持 Windows x64；Unity 类定义包固定为 `2021.3.45f2` 对应的 `classdata.tpk`，游戏大版本升级后需要换包。
- 翻译依赖 Bing 网页版接口（非公开 API，字段名与 token 机制可能随时变化）；接口不可用时自动回落到 MyMemory，两者都失败则显示原文。译文由机器翻译产生，仅作参考。
- 自更新走 GitHub Releases API（`api.github.com`，国内可能需代理）。检查失败时只在日志留 WARN，不影响其它功能；仓库不可达时首页不会出现更新提示条。
- 崩溃报告上传到 dpaste.com（公开服务，报告保留 7 天）。上传前必定展示完整预览，且用户名 / 本机路径已替换为占位符；服务不可用时只提示失败，不影响主流程。

## 外观

| 项 | 说明 |
| --- | --- |
| 主题 | 浅色 / 深色，设置页切换后立即生效并写入 `config.ini` 的 `theme` 键 |
| 强调色 | 青蓝 / 紫罗兰 / 翠绿 / 琥珀 / 玫红，写入 `accent` 键 |
| 实现方式 | XAML 全部用 `{StaticResource xxxBrush}`，解析出来是同一画刷实例，运行时改 `Color`（渐变改 `GradientStops`）就能让所有引用处一起变色，无需把 90 多处改成 `ThemeResource` |
| 窗口背景 | 优先 Mica（`MicaBackdrop`），不可用时回落纯色；侧栏半透明形成亚克力观感 |
| 动效 | 遵循系统「减少动画」设置（`UISettings.AnimationsEnabled`），关闭时不播放按钮缩放动画 |

## 日志

| 项 | 说明 |
| --- | --- |
| 三路来源 | 客户端 `logs\rmlclient.log`、加载器 `logs\hloader.log`、游戏 `%USERPROFILE%\AppData\LocalLow\Redbeet Interactive\Raft\Player.log` |
| 着色 | INFO 灰、WARN 黄、ERROR 红（RichTextBlock 逐行 `Run`，不是整块单色） |
| 过滤 | 级别（全部 / 警告以上 / 仅错误）+ 全文搜索，实时生效 |
| 性能 | 只渲染最后 3000 行，缓冲上限 20000 行；游戏日志打开时回读最后 300 行（崩溃现场在旧内容里） |

## 冲突检测

`modinfo.json` 的 `excludedFiles` 是**打包时排除**规则（过滤 `.csproj` 之类的文件），与运行时资源冲突无关，因此不作为冲突判据。实际使用两个判据：

| 判据 | 说明 |
| --- | --- |
| 重名 | 两个 mod 的 `modinfo.json` 里 `name` 相同（加载器按 name 建索引，重名会顶掉一个） |
| 文件重叠 | 两个包内含同名文件（Unity 资源与程序集加载不看 mod 边界） |
| 忽略项 | `modinfo.json`、图片（`.png`/`.jpg`/`.ico`…）、源码与工程文件（`.cs`/`.csproj`/`.sln`…）、文档与元数据（`.md`/`.txt`/`.json`/`.xml`/`.pdb`）不计入冲突，否则同一套 API 的多个 mod 会大量误报 |

## 还原为官方

设置 → 高级 → 「还原为官方启动器」：移除两种注入模式的全部产物（先探测再移除，本来没注入的记为跳过）、把改写的 `globalgamemanagers` 从备份还原、删除 `binaries\HMLCoreLibrary.dll`（官方下次运行会重新释放）。**模组、日志与配置一律保留。**

## 自更新

| 项 | 说明 |
| --- | --- |
| 版本来源 | `ClientVersion.Current`（编译期常量），发版时同步改这里 |
| 检查接口 | `GET https://api.github.com/repos/Xinghe-0203/RmlClient/releases/latest`，取 `tag_name` 比对 |
| 安装包选择 | Release 资产里优先取名字含 `Setup` 且以 `.exe` 结尾的，没有则取第一个 `.exe` |
| 完整性 | 资产若带 `digest`（`sha256:...`，GitHub 新版本会提供）则校验 SHA256，否则只比对文件长度 |
| 下载地址 | 只允许 `github.com`、`objects.githubusercontent.com`、`release-assets.githubusercontent.com` |
| 失败降级 | 检查失败只记 `ClientUpdate` WARN，首页不显示提示条，不弹窗 |

> 发版流程：改 `ClientVersion.Current` → 重新构建 → 打包 → 建 tag（如 `v1.0.1`）→ 创建 Release → 上传 `RmlClient-Setup-x.y.z.exe`。客户端下次启动即可看到更新提示。

---

## 模组更新检查

| 项 | 说明 |
| --- | --- |
| 数据来源 | 每个 mod 的 `modinfo.json` 里的 `updateUrl`，形如 `https://www.raftmodding.com/api/v1/mods/<slug>/version.txt`，返回纯文本版本号 |
| 版本比对 | 去掉 `v`/`V` 前缀后按 `Version` 比较；**任一侧解析失败一律判定为「无更新」**，宁可漏报也不误报，避免让用户白下载 |
| 请求节奏 | 顺序请求，每次间隔 250ms；官方站点对密集请求不友好 |
| 异常处理 | 404 或返回 JSON → 「该 mod 已不在市场中」；其它非 2xx → 显示状态码；空内容 → 「服务器返回空内容」 |
| 更新方式 | 走市场下载（`?ignoreVirusScan=true` + 白名单 + SHA256），落盘为 `modinstaller.<slug>.rmod`；**若原本是禁用状态，更新后仍保持禁用** |
| 已知上游问题 | 个别 mod 的 `version.txt` 已声明新版本，但市场分发的包内 `modinfo.json` 仍是旧版本（如 Raft_Cost 的 `version.txt` 写 1.1.0 而包内是 1.0），表现为「更新后仍提示有更新」。属上游数据不一致，客户端无法修正 |

> 与官方启动器的差异：官方在检查模组更新时不校验 TLS 证书，本实现走正常证书校验。
