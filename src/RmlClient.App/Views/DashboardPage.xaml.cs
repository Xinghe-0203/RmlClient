using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RmlClient.App.Services;
using RmlClient.Core.Configuration;
using RmlClient.Core.Game;
using RmlClient.Core.Injection;
using RmlClient.Core.Updates;

namespace RmlClient.App.Views;

public sealed partial class DashboardPage : Page
{
    private AppServices _services = null!;
    private CoreUpdateStatus? _updateStatus;
    private ClientUpdateStatus? _clientUpdate;
    private bool _busy;

    public DashboardPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not AppServices services)
        {
            return;
        }

        _services = services;
        ApplyLocalization();
        RefreshEnvironment();
        await RefreshInjectionAsync();

        if (_services.Config.Client.CheckUpdatesOnStartup)
        {
            await CheckUpdatesAsync(silent: true);
            await CheckClientUpdatesAsync();
        }
    }

    private string L(string key) => _services.Localization[key];

    private void ApplyLocalization()
    {
        GreetingText.Text = "RAFT MOD LOADER";
        HeadlineText.Text = L("home.title");
        SubtitleText.Text = L("home.subtitle");
        PlayButtonText.Text = L("home.play");
        CheckUpdateText.Text = L("home.checkUpdates");
        GamePathHeader.Text = L("home.gamePath");
        InjectionHeader.Text = L("home.injection");
        DetectButton.Content = L("settings.detect");
        BrowseButton.Content = L("common.browse");
        RepairButton.Content = L("home.repair");
        RemoveInjectionButton.Content = L("home.removeInjection");
    }

    private void RefreshEnvironment()
    {
        var gamePath = _services.GamePath;
        var valid = GameInstallValidator.Validate(gamePath).IsValid;

        GamePathText.Text = valid
            ? gamePath
            : $"{L("home.gamePathMissing")}\n{gamePath}";

        GamePathText.Foreground = (Brush)Application.Current.Resources[
            valid ? "TextPrimaryBrush" : "WarningBrush"];

        CoreVersionText.Text = $"Core {_services.Config.CoreVersion}";

        var mods = _services.Mods;
        var entries = mods?.Scan() ?? Array.Empty<Core.Mods.ModEntry>();
        var enabled = entries.Count(m => m.IsEnabled);
        ModsCountText.Text = $"{enabled} / {entries.Count}";

        PlayButton.IsEnabled = valid;
    }

    private async Task RefreshInjectionAsync()
    {
        var gamePath = _services.GamePath;

        if (!GameInstallValidator.Validate(gamePath).IsValid)
        {
            InjectionStateText.Text = L("home.injection.none");
            InjectionDetailText.Text = L("home.gamePathMissing");
            InjectionStateText.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
            return;
        }

        var probe = await _services.GetInjector().ProbeAsync(gamePath);

        var (text, brushKey) = probe.State switch
        {
            InjectionState.Injected => (L("home.injection.json"), "SuccessBrush"),
            InjectionState.PartiallyInjected => (L("home.injection.partial"), "WarningBrush"),
            InjectionState.Corrupted => (L("home.injection.corrupted"), "DangerBrush"),
            _ => (L("home.injection.none"), "TextMutedBrush"),
        };

        InjectionText.Text = probe.State == InjectionState.NotInjected
            ? L("home.injection.none")
            : probe.Mode == InjectionMode.Doorstop
                ? L("home.injection.doorstop")
                : L("home.injection.json");

        InjectionStateText.Text = text;
        InjectionStateText.Foreground = (Brush)Application.Current.Resources[brushKey];
        InjectionDetailText.Text = probe.Details.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, probe.Details);
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var gamePath = _services.GamePath;
        if (!GameInstallValidator.Validate(gamePath).IsValid)
        {
            await ShowDialogAsync(L("common.error"), L("home.gamePathMissing"));
            return;
        }

        // 立刻给出反馈：按钮变灰、图标转圈、文案改「正在启动…」。
        // 启动链路（探测注入 → 可能修复 → 改写 globalgamemanagers → 起进程）
        // 可能要好几秒，没有反馈用户会以为没点上而反复点。
        SetBusy(true, L("home.launching"), keepPlayEnabled: true);
        ShowLaunching();

        // Steam 拉起游戏只要几十毫秒，「正在启动」会一闪而过。
        // 记下起点，稍后保证这个状态至少显示 LaunchingMinMs。
        var launchingSince = Environment.TickCount64;

        try
        {
            // 启动前确保注入到位，否则游戏起来也不会加载 mod。
            var probe = await _services.GetInjector().ProbeAsync(gamePath);
            if (!probe.IsHealthy)
            {
                _services.Logger.Warn("Launch", $"注入状态异常（{probe.State}），自动修复");
                var repair = await _services.GetInjector().RepairAsync(gamePath);
                if (!repair.Success)
                {
                    _services.Logger.Error("Launch", $"自动修复失败：{repair.Message}");
                }
            }

            // 跳过 Unity 启动画面需要改写 globalgamemanagers；失败不影响启动，
            // 所以只记日志不打断流程。
            var patch = _services.GameManagers.Apply(gamePath, _services.Config.SkipSplashScreen);
            if (patch.Changed || patch.BackedUp)
            {
                _services.Logger.Info("Launch", $"globalgamemanagers：{patch.Message}" +
                    (patch.BackedUp ? "（已备份原始文件）" : string.Empty));
            }

            var result = _services.GameLauncher.Start(
                gamePath,
                _services.Config.StartGameFromSteam,
                _services.Config.GameStartCommand);

            if (result.Success)
            {
                _services.Logger.Info("Launch", result.Message);
                await HoldLaunchingAsync(launchingSince);
                await ShowLaunchedAsync();
            }
            else
            {
                _services.Logger.Error("Launch", result.Message);
                await HoldLaunchingAsync(launchingSince);
                ShowLaunchFailed();
                await ShowDialogAsync(L("common.error"), result.Message);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Launch", "启动游戏失败", ex);
            await HoldLaunchingAsync(launchingSince);
            ShowLaunchFailed();
            await ShowDialogAsync(L("common.error"), ex.Message);
        }
        finally
        {
            // 成功或失败都保留 5 秒的可见状态，让用户确认点击生效了。
            // 这段时间内按钮保持禁用，避免重复拉起游戏进程。
            await Task.Delay(LaunchFeedbackMs);
            ResetLaunchButton();
            SetBusy(false);
        }
    }

    /// <summary>启动反馈的最短显示时长。</summary>
    private const int LaunchFeedbackMs = 5000;

    /// <summary>「正在启动…」状态的最短显示时长，避免一闪而过。</summary>
    private const int LaunchingMinMs = 1200;

    private static async Task HoldLaunchingAsync(long since)
    {
        var elapsed = Environment.TickCount64 - since;
        var remaining = LaunchingMinMs - (int)elapsed;
        if (remaining > 0)
        {
            await Task.Delay(remaining);
        }
    }

    private void ShowLaunching()
    {
        PlayIcon.Visibility = Visibility.Collapsed;
        PlaySpinner.Visibility = Visibility.Visible;
        PlaySpinner.IsActive = true;
        PlayButtonText.Text = L("home.launching");
        SetPlayButtonAccent(false);
        PlayScaleUp();
    }

    private async Task ShowLaunchedAsync()
    {
        PlaySpinner.IsActive = false;
        PlaySpinner.Visibility = Visibility.Collapsed;
        PlayIcon.Visibility = Visibility.Visible;
        PlayIcon.Text = "\uE73E"; // CheckMark
        PlayButtonText.Text = L("home.launched");
        if (App.MainWindowInstance is MainWindow window)
        {
            // 底部状态栏也要跟着改，否则按钮写「已启动」而状态栏还停在「正在启动…」。
            window.UpdateStatus(L("home.launched"), false);
        }

        PlayScaleUp();
        await Task.CompletedTask;
    }

    private void ShowLaunchFailed()
    {
        PlaySpinner.IsActive = false;
        PlaySpinner.Visibility = Visibility.Collapsed;
        PlayIcon.Visibility = Visibility.Visible;
        PlayIcon.Text = "\uE711"; // Cancel
        PlayButtonText.Text = L("home.launchFailed");
        SetPlayButtonAccent(false);
        if (App.MainWindowInstance is MainWindow window)
        {
            window.UpdateStatus(L("home.launchFailed"), false);
        }

        PlayScaleUp();
    }

    private void ResetLaunchButton()
    {
        PlaySpinner.IsActive = false;
        PlaySpinner.Visibility = Visibility.Collapsed;
        PlayIcon.Visibility = Visibility.Visible;
        PlayIcon.Text = "\uE768"; // Play
        PlayButtonText.Text = L("home.play");
        SetPlayButtonAccent(true);
    }

    /// <summary>
    /// 把主按钮的底色在「青蓝渐变」与「中性灰」之间切换。
    /// 模板里的 BackgroundBorder 用的是 AccentBrush（LinearGradientBrush），
    /// 直接换 Background 即可，不必再动样式。
    /// </summary>
    private void SetPlayButtonAccent(bool accent)
    {
        var border = FindTemplateChild<Border>(PlayButton, "BackgroundBorder");
        if (border is null)
        {
            return;
        }

        if (accent)
        {
            border.Background = (Brush)Application.Current.Resources["AccentBrush"];
            border.Opacity = 1.0;
            return;
        }

        // 变灰：用中性灰底 + 略降不透明度，白字仍然清晰可读。
        border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8));
        border.Opacity = 0.85;
    }

    private static T? FindTemplateChild<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name)
            {
                return element;
            }

            var nested = FindTemplateChild<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// 按下去「弹一下」：先缩到 0.96 再弹回 1.0。
    /// 只有按钮自己的 Compose 动画能做这种效果，Storyboard 改 Scale 会打架。
    /// </summary>
    private void PlayScaleUp()
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(PlayButton);
            var compositor = visual.Compositor;

            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0.0f, new System.Numerics.Vector3(1.0f, 1.0f, 1.0f));
            scale.InsertKeyFrame(0.35f, new System.Numerics.Vector3(0.96f, 0.96f, 1.0f));
            scale.InsertKeyFrame(1.0f, new System.Numerics.Vector3(1.0f, 1.0f, 1.0f));
            scale.Duration = TimeSpan.FromMilliseconds(320);

            visual.CenterPoint = new System.Numerics.Vector3(
                (float)PlayButton.ActualWidth / 2f,
                (float)PlayButton.ActualHeight / 2f,
                0f);

            visual.StartAnimation("Scale", scale);
        }
        catch (Exception)
        {
            // 动画失败不影响功能，忽略。
        }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e) =>
        await CheckUpdatesAsync(silent: false);

    private async Task CheckUpdatesAsync(bool silent)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, L("common.loading"));

        try
        {
            _updateStatus = await _services.CoreUpdater.CheckAsync(
                _services.Config.Branch,
                _services.Config.CoreVersion);

            if (_updateStatus.Error is { Length: > 0 } error)
            {
                _services.Logger.Warn("Update", $"检查更新失败：{error}");
                UpdateBanner.Visibility = Visibility.Collapsed;

                if (!silent)
                {
                    await ShowDialogAsync(L("common.warning"), error);
                }

                return;
            }

            if (_updateStatus.NeedsInstall)
            {
                UpdateBanner.Visibility = Visibility.Visible;
                UpdateTitle.Text = $"{L("home.updateAvailable")} — {_updateStatus.RemoteVersion}";
                UpdateChangelog.Text = string.IsNullOrWhiteSpace(_updateStatus.Changelog)
                    ? L("home.coreVersion")
                    : _updateStatus.Changelog.Trim();
            }
            else
            {
                UpdateBanner.Visibility = Visibility.Collapsed;

                if (!silent)
                {
                    await ShowDialogAsync(L("common.success"), L("home.upToDate"));
                }
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, L("common.loading"));
        ProgressCard.Visibility = Visibility.Visible;

        try
        {
            var coreProgress = new Progress<DownloadProgress>(p =>
            {
                ProgressBar.Value = p.Fraction * 100;
                ProgressDetail.Text = $"coremod  {p.HumanReadable}";
            });

            var assetsProgress = new Progress<DownloadProgress>(p =>
            {
                ProgressDetail.Text = $"rml.assets  {p.HumanReadable}";
            });

            var result = await _services.CoreUpdater.InstallAsync(
                _services.Config.Branch,
                coreProgress,
                assetsProgress);

            if (result.Success)
            {
                _services.Config.CoreVersion = result.Version ?? _services.Config.CoreVersion;
                _services.SaveConfig();
                _services.Logger.Info("Update", result.Message);
                UpdateBanner.Visibility = Visibility.Collapsed;
                RefreshEnvironment();
                await ShowDialogAsync(L("common.success"), result.Message);
            }
            else
            {
                _services.Logger.Error("Update", result.Message);
                await ShowDialogAsync(L("common.error"), result.Message);
            }
        }
        finally
        {
            ProgressCard.Visibility = Visibility.Collapsed;
            ProgressBar.Value = 0;
            SetBusy(false);
        }
    }

    // ===== 客户端自身更新（GitHub Releases）=====

    /// <summary>检查客户端是否有新版本；失败只记日志，不打扰用户。</summary>
    private async Task CheckClientUpdatesAsync()
    {
        try
        {
            var status = await _services.ClientUpdater.CheckAsync();

            if (status.Error is { Length: > 0 } error)
            {
                // 仓库还没建 Release、网络不通、账号异常都会走到这里。
                // 客户端更新是附加能力，失败不应该在首屏弹窗。
                _services.Logger.Warn("ClientUpdate", $"检查客户端更新失败：{error}");
                ClientUpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }

            _clientUpdate = status.UpdateAvailable ? status : null;

            if (!status.UpdateAvailable)
            {
                ClientUpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }

            ClientUpdateTitle.Text = string.Format(L("home.clientUpdate"), status.LatestVersion);
            ClientUpdateDetail.Text = string.IsNullOrWhiteSpace(status.Changelog)
                ? $"{L("home.clientUpdateDownload")} · {status.Installer?.Name}"
                : FirstLines(status.Changelog, 2);
            ClientUpdateBanner.Visibility = Visibility.Visible;

            _services.Logger.Info("ClientUpdate", $"发现客户端新版本 {status.LatestVersion}");
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("ClientUpdate", $"检查客户端更新异常：{ex.Message}");
            ClientUpdateBanner.Visibility = Visibility.Collapsed;
        }
    }

    private static string FirstLines(string text, int count)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(count);

        return string.Join(Environment.NewLine, lines);
    }

    private async void OnOpenClientReleaseClick(object sender, RoutedEventArgs e)
    {
        var url = _clientUpdate?.ReleaseUrl ?? ClientVersion.ReleasesUrl;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("ClientUpdate", $"打开更新页面失败：{ex.Message}");
        }

        await Task.CompletedTask;
    }

    private async void OnDownloadClientUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_clientUpdate?.Installer is not { } asset)
        {
            await ShowDialogAsync(L("common.warning"), L("home.clientUpdateUnavailable"));
            return;
        }

        if (_busy)
        {
            return;
        }

        SetBusy(true, L("home.clientUpdateDownloading"));
        ProgressCard.Visibility = Visibility.Visible;
        ProgressLabel.Text = L("home.clientUpdateDownloading");

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                ProgressBar.Value = p.Fraction * 100;
                ProgressDetail.Text = $"{asset.Name}  {p.HumanReadable}";
            });

            var path = await _services.ClientUpdater.DownloadInstallerAsync(asset, progress);

            if (path is null)
            {
                await ShowDialogAsync(L("common.error"), L("home.clientUpdateFailed"));
                return;
            }

            _services.Logger.Info("ClientUpdate", $"安装包已下载：{path}");

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L("common.success"),
                Content = new TextBlock
                {
                    Text = L("home.clientUpdateReady"),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = L("common.yes"),
                CloseButtonText = L("common.no"),
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ClientUpdater.LaunchInstaller(path);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("ClientUpdate", "下载客户端更新失败", ex);
            await ShowDialogAsync(L("common.error"), ex.Message);
        }
        finally
        {
            ProgressCard.Visibility = Visibility.Collapsed;
            ProgressBar.Value = 0;
            SetBusy(false);
        }
    }

    private async void OnDetectClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true, L("common.loading"));

        try
        {
            var found = await Task.Run(() => _services.SteamLocator.FindRaftInstallations());
            var install = found.FirstOrDefault(i => i.IsValid) ?? found.FirstOrDefault();

            if (install is null)
            {
                await ShowDialogAsync(L("common.warning"), L("home.gamePathMissing"));
                return;
            }

            _services.UpdateGamePath(install.GamePath);
            RefreshEnvironment();
            await RefreshInjectionAsync();

            _services.Logger.Info("Game", $"自动检测到游戏目录：{install.GamePath}（{install.Source}）");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var validation = GameInstallValidator.Validate(folder.Path);
        if (!validation.IsValid)
        {
            await ShowDialogAsync(L("common.warning"),
                $"{L("home.gamePathMissing")}\n\n{string.Join("\n", validation.Problems)}");
            return;
        }

        _services.UpdateGamePath(folder.Path);
        RefreshEnvironment();
        await RefreshInjectionAsync();
    }

    private async void OnRepairClick(object sender, RoutedEventArgs e)
    {
        if (!GameInstallValidator.Validate(_services.GamePath).IsValid)
        {
            await ShowDialogAsync(L("common.error"), L("home.gamePathMissing"));
            return;
        }

        SetBusy(true, L("common.loading"));

        try
        {
            var result = await _services.GetInjector().RepairAsync(_services.GamePath);

            if (result.Success)
            {
                _services.Logger.Info("Injection", result.Message);
            }
            else
            {
                _services.Logger.Error("Injection", result.Message);
            }

            await ShowDialogAsync(
                result.Success ? L("common.success") : L("common.error"),
                result.Message);

            await RefreshInjectionAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRemoveInjectionClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L("home.removeInjection"),
            Content = L("settings.restoreHint"),
            PrimaryButtonText = L("common.confirm"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true, L("common.loading"));

        try
        {
            var result = await _services.GetInjector().RemoveAsync(_services.GamePath);
            await ShowDialogAsync(
                result.Success ? L("common.success") : L("common.error"),
                result.Message);

            await RefreshInjectionAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? status = null, bool keepPlayEnabled = false)
    {
        _busy = busy;

        // 启动反馈期间按钮不设 IsEnabled=false：AccentButtonStyle 的禁用态本身带
        // 0.35 透明度，会和「已启动」的白色文字叠加成几乎看不见的浅色。
        // 重复点击由 _busy 拦住，视觉上的变灰交给 ShowLaunching 里的 Opacity 控制。
        PlayButton.IsEnabled = keepPlayEnabled
            ? GameInstallValidator.Validate(_services.GamePath).IsValid
            : !busy && GameInstallValidator.Validate(_services.GamePath).IsValid;
        CheckUpdateButton.IsEnabled = !busy;
        DetectButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        RepairButton.IsEnabled = !busy;
        RemoveInjectionButton.IsEnabled = !busy;

        if (App.MainWindowInstance is MainWindow window)
        {
            window.UpdateStatus(status, busy);
        }
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            CloseButtonText = L("common.close"),
        };

        await dialog.ShowAsync();
    }
}
