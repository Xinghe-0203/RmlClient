# Raft Mod Loader Client

一个替代官方 `HMLCore.exe` 的 Raft 模组加载器启动器：WinUI 3 + Windows App SDK，浅色主题，中英双语。

复用官方已安装的注入链路（`coremod.dll` + `AssemblyLoader` + JSON 注入 / Doorstop），只重做界面、更新、模组管理与日志，因此可以和官方启动器共用 `%APPDATA%\RaftModLoader` 目录，装完即可直接接管现有的mod。

---

## 功能

| 模块 | 说明 |
| --- | --- |
| 启动 | 一键启动游戏；启动前自动校验并修复注入；可选经 Steam 启动（走 DRM + 云存档）或直接启动 `Raft.exe` |
| 更新 | 按分支（public / indev）拉 `client.php` 清单，SHA512 校验后原子替换 `coremod.dll` 与 `rml.assets` |
| 模组 | 扫描 `mods\*.rmod`，启用/禁用（改名为 `.rmod.disabled`）、拖入安装、卸载到回收站、显示真实图标与版本 |
| 市场 | 拉取 raftmodding.com 全量 mod 列表（约 385 个），本地搜索/分类筛选/排序，一键安装，支持版本回滚 |
| 详情 | 点任意模组（市场或本地）弹窗看「这个模组是做什么的」、更新日志、版本列表 |
| 翻译 | 描述与更新日志自动译成系统默认语言，可随时切换目标语言（13 种）；市场标题、已安装模组列表与搜索也支持中文 |
| 日志 | 客户端日志（环形缓冲 + 文件轮转）与 `hloader.log` 实时跟随，可清空、导出 |
| 设置 | 语言、分支、游戏路径、注入方式、托盘、崩溃日志上传、跳过 Unity 启动画面 |
| 引导 | 首次启动 4 步向导：定位游戏 → 安装 Core → 注入加载器 → 同意条款并完成 |
| 托盘 | 关闭到托盘，托盘菜单可打开主界面 / 退出 |
| 自更新 | 查本仓库 GitHub Releases 的最新版本，发现新版时首页提示，可一键下载安装包并运行 |

---

## 相对官方启动器的改进

官方 `RMLLauncher.exe`（513 KB，.NET Framework 4.8，无签名）逆向后的主要问题，以及本客户端的处理：

| # | 官方问题 | 本客户端 |
| --- | --- | --- |
| 1 | 更新 URL 白名单用 `url.StartsWith("https://fastdl.raftmodding.com")`，`https://fastdl.raftmodding.com.evil.tld/x.exe` 也能通过 | `UrlGuard` 校验 scheme + 默认端口 + **精确主机名** + IDN 全 ASCII |
| 2 | `WebClient` 默认跟随重定向，重定向后的最终地址不再校验 | `AllowAutoRedirect = false`，手动跟随且**每一跳重新过白名单** |
| 3 | 递归删除 `:Zone.Identifier` 清除 MOTW，抑制 SmartScreen | 保留（mod 正常加载所需），但只对下载产物做，不递归整目录 |
| 4 | 下载后 `Process.Start` 直接执行，仅比对同一响应内的 hash | 校验 SHA512 后**原子替换**，仍无独立签名（受限于官方分发方式） |
| 5 | coremod / assets 下载只校验扩展名 `.dll` / `.assets`，**无域名校验** | 两个 URL 都过 `UrlGuard` |
| 6 | `if (!File.Exists(text) && new FileInfo(text).Length > 10)` 条件写反，提示框永不弹出 | 正确的前置检查 |
| 7 | mod hash 为 null 时静默跳过校验 | 无 hash 时明确拒绝安装 |
| 8 | `rmllauncher://installmod/<slug>` 未做本地 slug 校验 | slug 走正则 `^(?!\d+$)[a-z0-9\-._]+$` 且长度 ≤ 64 |
| 9 | 下载回调不检查 `e.Error`，失败也置完成标志 | 异常路径明确返回失败 |
| 10 | `while (!a \|\| !b) await Task.Delay(1)` 1ms 忙等 | 用 `TaskCompletionSource` / 顺序 await |
| 11 | 无 `-DEBUG` 且无本地 exe 时界面永久卡在 "Checking for launcher updates..." | 每步都有超时与失败分支 |
| 12 | 卸载不清 `.rmod` 文件关联、`rmllauncher` URI scheme、`binaries\*.dll` | 卸载时清理注入产物并提示是否删除用户数据 |
| 13 | 改写 `globalgamemanagers` **完全不备份** | 首次改写前备份到 `cache\backup\ggm\globalgamemanagers.original` |

另外，AssemblyLoader / coremod 里 `HttpCertHandler.ValidateCertificate => true` 全局关闭了 TLS 校验（4 处调用），本客户端**不复用**该路径，自己的 HTTP 栈严格校验证书。

---

## 翻译

详情弹窗里的描述与更新日志会自动翻译，目标语言默认跟随系统，可在弹窗内随时切换。

| 项 | 说明 |
| --- | --- |
| 主通道 | **Bing 网页版翻译**（`www.bing.com/ttranslatev3`）—— 实测国内可直连，质量最好 |
| 备用通道 | **MyMemory**（`api.mymemory.translated.net`），单次上限 500 字符，主通道失败时兜底 |
| 已排除 | Google 系（国内连不上）、百度（需 cookie 签名）、有道（302 / 返回 HTML）、LibreTranslate 公共实例（全部无响应）、SimplyTranslate（译文为空） |
| 语言 | 简体中文、繁體中文、English、日本語、한국어、Русский、Deutsch、Français、Español、Português、Italiano、Polski、Türkçe |
| 缓存 | `cache\translation.json`（正文，按 SHA256 键控）、`cache\market-titles.json`（市场标题）、`cache\local-mod-titles.json`（已安装模组标题） |
| 免翻判定 | 原文已经是目标语言（CJK / 韩文占比判定）时直接跳过，不发请求 |

实现要点：

- Bing 需要先 `GET /translator` 拿 cookie 并正则提取 `IG` / `IID` / `params_AbusePreventionHelper`（key + token），`fromLang` 必须写 `auto-detect`；单次文本上限实测 900 字符，故按 800 切分。
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
│   │   ├── Assets/classdata.tpk   # Unity 类定义包（内嵌资源）
│   │   ├── Configuration/         # IniFile、RmlPaths、LauncherConfig
│   │   ├── Diagnostics/           # HashUtil、UrlGuard
│   │   ├── Game/                  # SteamLocator、GameLauncher、GameManagersPatcher
│   │   ├── Injection/             # IInjector、JsonInjector、DoorstopInjector、InjectionBackup
│   │   ├── Logging/               # ClientLogger、LogTailer
│   │   ├── Market/                # MarketMod、MarketService、TitleTranslator
│   │   ├── Mods/                  # RmodReader、ModRepository
│   │   ├── Translation/           # TranslationService、Bing/MyMemory provider、TranslationLanguage
│   │   └── Updates/               # Manifests、RmlHttpClient、CoreUpdater
│   └── RmlClient.App/             # WinUI 3 界面
│       ├── Views/                 # Dashboard / Mods / Market / Logs / Settings / Onboarding
│       ├── Controls/              # ModDetailView、ModDetailDialog、ModDetailModel
│       ├── Themes/                # Colors.xaml、Controls.xaml
│       ├── Services/              # AppServices、Localization、WindowHelper、TrayIcon、MinSizeHelper
│       ├── Assets/app.ico         # 7 个尺寸（16–256）
│       └── app.manifest           # asInvoker，PerMonitorV2
├── installer/RmlClient.iss        # Inno Setup 脚本
├── installer/ChineseSimplified.isl
├── tools/                         # 自动化脚本（纯 ASCII）
│   ├── uia.ps1                    # 按 AutomationId 点导航 + PrintWindow 截图
│   ├── click.ps1                  # 按名字/AutomationId 点击任意控件
│   ├── clickitem.ps1              # 真实鼠标点击列表项（GridView 不响应 Invoke）
│   ├── combo.ps1                  # 展开 ComboBox 并从桌面根选中弹出项
│   ├── search.ps1                 # 往搜索框输入文本并截图
│   └── wizard.ps1                 # 驱动 Inno Setup 的 Win32 向导
├── docs/design.md                 # 设计文档
└── docs/screenshots/              # 实机截图
```

源码规模：53 个 `.cs` / `.xaml` 文件，约 11,300 行。

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

产物 `dist\RmlClient-Setup-1.0.0.exe`（约 58 MB，`lzma2/max` 固实压缩；未压缩的 `publish\app` 是 210 MB）。

安装行为：默认按用户安装到 `%LOCALAPPDATA%\Programs\Raft Mod Loader Client`，无需管理员；卸载时**默认保留** `%APPDATA%\RaftModLoader`（该目录与官方启动器共用，误删会丢全部 mod），可选清理。

---

## 与官方数据的兼容性

本客户端**接管**官方目录 `%APPDATA%\RaftModLoader`，不另建一套：

| 文件/目录 | 用途 | 谁写 |
| --- | --- | --- |
| `config.ini` | 官方 7 键 + 新增 `[client]` 段 7 键 | 双方，未知键保留 |
| `binaries\coremod.dll` | 核心 mod | 本客户端更新 |
| `binaries\HMLCoreLibrary.dll` | AssemblyLoader | 官方安装时写入 |
| `rml.assets` | 资源包（约 47 MB） | 本客户端更新 |
| `pid.txt` | 启动握手（AssemblyLoader 读后立即清空） | 本客户端写 |
| `cache\backup\` | 注入备份 + `globalgamemanagers` 原始副本 | 本客户端 |
| `logs\rmlclient.log` | 本客户端日志 | 本客户端 |

游戏目录侧的注入产物与官方一致：`Raft_Data\Managed\RaftModLoader_AssemblyLoader.dll`、`Raft_Data\RuntimeInitializeOnLoads.json` 首条 `AssemblyLoader/Entrypoint/Start` 项、`Raft_Data\ScriptingAssemblies.json` 登记。

---

## 已知限制

- 安装包 58 MB、自包含发布 210 MB（Windows App SDK 自包含 + .NET 运行时），未做 trimming（WinUI 对 trimming 支持有限）。
- 仍无 Authenticode 签名：hash 校验只能证明「与服务器同一次响应一致」，不能证明来源，这一点受限于官方分发方式。
- 市场数据是全量 917 KB JSON，服务端 `page`/`limit`/`search` 参数全部无效，只能本地筛选。
- 只支持 Windows x64；Unity 类定义包固定为 `2021.3.45f2` 对应的 `classdata.tpk`，游戏大版本升级后需要换包。
- 翻译依赖 Bing 网页版接口（非公开 API，字段名与 token 机制可能随时变化）；接口不可用时自动回落到 MyMemory，两者都失败则显示原文。译文由机器翻译产生，仅作参考。
- 自更新走 GitHub Releases API（`api.github.com`，国内可能需代理）。检查失败时只在日志留 WARN，不影响其它功能；仓库不可达时首页不会出现更新提示条。

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
