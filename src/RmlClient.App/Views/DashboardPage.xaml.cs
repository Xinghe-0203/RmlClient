using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        SetBusy(true, L("home.playing"));

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
            }
            else
            {
                _services.Logger.Error("Launch", result.Message);
                await ShowDialogAsync(L("common.error"), result.Message);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Launch", "启动游戏失败", ex);
            await ShowDialogAsync(L("common.error"), ex.Message);
        }
        finally
        {
            SetBusy(false);
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

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        PlayButton.IsEnabled = !busy && GameInstallValidator.Validate(_services.GamePath).IsValid;
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
