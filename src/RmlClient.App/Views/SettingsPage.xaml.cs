using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RmlClient.App.Services;
using RmlClient.Core.Configuration;
using RmlClient.Core.Game;
using RmlClient.Core.Injection;

namespace RmlClient.App.Views;

public sealed partial class SettingsPage : Page
{
    private AppServices _services = null!;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not AppServices services)
        {
            return;
        }

        _services = services;
        ApplyLocalization();
        LoadValues();
    }

    private string L(string key) => _services.Localization[key];

    private void ApplyLocalization()
    {
        TitleText.Text = L("settings.title");

        GeneralHeader.Text = L("settings.general");
        LanguageLabel.Text = L("settings.language");
        LanguageHint.Text = L("settings.languageHint");
        BranchLabel.Text = L("settings.branch");
        BranchHint.Text = L("settings.branchHint");

        AppearanceHeader.Text = L("settings.appearance");
        ThemeLabel.Text = L("settings.theme");
        ThemeHint.Text = L("settings.themeHint");
        AccentLabel.Text = L("settings.accent");
        AccentHint.Text = L("settings.accentHint");

        GameHeader.Text = L("settings.game");
        GamePathLabel.Text = L("settings.gamePath");
        DetectButton.Content = L("settings.detect");
        BrowseButton.Content = L("common.browse");
        StartViaSteamLabel.Text = L("settings.startViaSteam");
        StartViaSteamHint.Text = L("settings.startViaSteamHint");
        SkipSplashLabel.Text = L("settings.skipSplash");
        SkipSplashHint.Text = L("settings.skipSplashHint");

        InjectionHeader.Text = L("settings.injection");
        JsonRadio.Content = L("settings.injectionJson");
        DoorstopRadio.Content = L("settings.injectionDoorstop");
        InjectionHint.Text = L("settings.injectionHint");
        VerifyButton.Content = L("settings.verify");
        RestoreButton.Content = L("home.removeInjection");

        BehaviorHeader.Text = L("settings.behavior");
        CloseToTrayLabel.Text = L("settings.closeToTray");
        CheckUpdatesLabel.Text = L("settings.checkUpdates");
        MinimizeOnLaunchLabel.Text = L("settings.minimizeOnLaunch");
        MinimizeOnLaunchHint.Text = L("settings.minimizeOnLaunchHint");
        UploadLogsLabel.Text = L("settings.uploadLogs");
        UploadLogsHint.Text = L("settings.uploadLogsHint");

        AdvancedHeader.Text = L("settings.advanced");
        DataFolderLabel.Text = L("settings.dataFolder");
        OpenDataButton.Content = L("home.openFolder");
        OpenLogsButton.Content = L("logs.title");
        RestoreLabel.Text = L("settings.restore");
        RestoreHint.Text = L("settings.restoreHint");
        RestoreBackupButton.Content = L("common.confirm");
        RestoreOfficialLabel.Text = L("settings.restoreOfficial");
        RestoreOfficialHint.Text = L("settings.restoreOfficialHint");
        RestoreOfficialButton.Content = L("settings.restoreOfficialAction");

        AboutHeader.Text = L("settings.about");
        AboutHint.Text = L("settings.aboutHint");
        AboutVersionText.Text = $"RML Client {RmlClient.Core.Updates.ClientVersion.Current}";

        // ToggleSwitch 自身没有可读文本，屏幕阅读器和 UI Automation 拿到的名字是空的。
        // 这里把旁边标签的文案补成无障碍名，顺带让自动化测试能按名字定位。
        AutomationProperties.SetName(StartViaSteamToggle, L("settings.startViaSteam"));
        AutomationProperties.SetName(SkipSplashToggle, L("settings.skipSplash"));
        AutomationProperties.SetName(CloseToTrayToggle, L("settings.closeToTray"));
        AutomationProperties.SetName(CheckUpdatesToggle, L("settings.checkUpdates"));
        AutomationProperties.SetName(MinimizeOnLaunchToggle, L("settings.minimizeOnLaunch"));
        AutomationProperties.SetName(UploadLogsToggle, L("settings.uploadLogs"));
    }

    private void LoadValues()
    {
        _loading = true;

        try
        {
            var config = _services.Config;

            // 语言
            LanguageCombo.Items.Clear();
            foreach (var (code, display) in Localization.AvailableLanguages)
            {
                LanguageCombo.Items.Add(new ComboBoxItem { Content = display, Tag = code });
            }

            SelectByTag(LanguageCombo, _services.Localization.Language);

            // 分支
            BranchCombo.Items.Clear();
            foreach (var branch in new[] { "public", "indev", "beta", "dev" })
            {
                BranchCombo.Items.Add(new ComboBoxItem { Content = branch, Tag = branch });
            }

            SelectByTag(BranchCombo, config.Branch);

            GamePathValue.Text = string.IsNullOrWhiteSpace(config.GamePath)
                ? L("home.gamePathMissing")
                : config.GamePath;

            StartViaSteamToggle.IsOn = config.StartGameFromSteam;
            SkipSplashToggle.IsOn = config.SkipSplashScreen;

            JsonRadio.IsChecked = config.Client.InjectionMode == InjectionMode.Json;
            DoorstopRadio.IsChecked = config.Client.InjectionMode == InjectionMode.Doorstop;

            CloseToTrayToggle.IsOn = config.Client.CloseToTray;
            CheckUpdatesToggle.IsOn = config.Client.CheckUpdatesOnStartup;
            MinimizeOnLaunchToggle.IsOn = config.Client.MinimizeOnLaunch;
            UploadLogsToggle.IsOn = config.Client.UploadCrashLogs;

            // 外观
            ThemeCombo.Items.Clear();
            ThemeCombo.Items.Add(new ComboBoxItem { Content = L("settings.themeLight"), Tag = ThemeService.Light });
            ThemeCombo.Items.Add(new ComboBoxItem { Content = L("settings.themeDark"), Tag = ThemeService.Dark });
            SelectByTag(ThemeCombo, ThemeService.IsDark(config.Client.Theme) ? ThemeService.Dark : ThemeService.Light);

            BuildAccentSwatches(config.Client.Accent);

            DataFolderValue.Text = _services.Paths.Root;
        }
        finally
        {
            _loading = false;
        }
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        _services.SaveConfig();
    }

    // ---------- 外观 ----------

    /// <summary>
    /// 画出强调色色块。选中的那块加一圈描边，方便一眼看出当前是哪个。
    /// 用 Button 而不是 Border：Button 自带 Invoke 模式，键盘 Tab + 回车也能选，
    /// 屏幕阅读器与自动化工具也点得中。
    /// </summary>
    private void BuildAccentSwatches(string? current)
    {
        AccentPanel.Children.Clear();

        var selected = string.IsNullOrWhiteSpace(current) ? "cyan" : current;

        foreach (var key in ThemeService.AccentKeys)
        {
            var (start, end) = ThemeService.Preview(key);
            var isSelected = string.Equals(key, selected, StringComparison.OrdinalIgnoreCase);

            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = start, Offset = 0 },
                        new GradientStop { Color = end, Offset = 1 },
                    },
                },
                BorderThickness = new Thickness(2),
                BorderBrush = isSelected
                    ? (Brush)Application.Current.Resources["TextPrimaryBrush"]
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Tag = key,
            };

            var name = L($"settings.accent{char.ToUpperInvariant(key[0])}{key[1..]}");
            AutomationProperties.SetName(swatch, name);
            AutomationProperties.SetAutomationId(swatch, $"settings.accent.{key}");
            ToolTipService.SetToolTip(swatch, name);

            swatch.Click += OnAccentSwatchClick;
            AccentPanel.Children.Add(swatch);
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string theme })
        {
            return;
        }

        if (string.Equals(_services.Config.Client.Theme, theme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _services.Config.Client.Theme = theme;
        Persist();
        ApplyAppearance();
    }

    private void OnAccentSwatchClick(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button { Tag: string accent })
        {
            return;
        }

        if (string.Equals(_services.Config.Client.Accent, accent, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _services.Config.Client.Accent = accent;
        Persist();
        ApplyAppearance();
        BuildAccentSwatches(accent);
    }

    /// <summary>把当前配置的主题与强调色立刻应用到运行中的窗口。</summary>
    private void ApplyAppearance()
    {
        var theme = _services.Config.Client.Theme;
        var accent = _services.Config.Client.Accent;

        // 改 RequestedTheme 会让控件模板重载，ComboBox 可能因此再抛一次
        // SelectionChanged（此时 SelectedItem 可能短暂停在第一项）。
        // 不挡住的话，刚选的深色会被自己"改回"浅色并存进配置。
        _loading = true;

        try
        {
            ThemeService.Apply(theme, accent);
            ThemeService.ApplyToWindow(App.MainWindowInstance, theme);

            if (App.MainWindowInstance is MainWindow window)
            {
                WindowHelper.SetTitleBarColors(window, ThemeService.IsDark(theme));
                window.ApplyBackdrop(ThemeService.IsDark(theme));
            }
        }
        finally
        {
            _loading = false;
        }

        _services.Logger.Info("Settings", $"外观切换为 {(ThemeService.IsDark(theme) ? "深色" : "浅色")} / {accent}");
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code })
        {
            return;
        }

        _services.Localization.Language = code;
        _services.Config.Client.Language = code;
        Persist();

        // 重新应用本页文案，其它页在下次进入时刷新。
        ApplyLocalization();
        LoadValues();
    }

    private void OnBranchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BranchCombo.SelectedItem is not ComboBoxItem { Tag: string branch })
        {
            return;
        }

        _services.Config.Branch = branch;
        Persist();
        _services.Logger.Info("Settings", $"分支切换为 {branch}");
    }

    private void OnStartViaSteamToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _services.Config.StartGameFromSteam = StartViaSteamToggle.IsOn;
        Persist();
    }

    private async void OnSkipSplashToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // Toggled 事件可能在 LoadValues 恢复开关状态后延迟触发，
        // 这时开关值与配置已经一致，再写一次会把用户刚做的选择覆盖回去。
        // 只有真的发生变化才继续。
        if (_services.Config.SkipSplashScreen == SkipSplashToggle.IsOn)
        {
            return;
        }

        _services.Config.SkipSplashScreen = SkipSplashToggle.IsOn;
        Persist();

        // 开关立即生效：改写 globalgamemanagers。失败只提示，不回滚开关
        // （配置已保存，下次启动会再试一次）。
        var gamePath = _services.GamePath;
        if (!GameInstallValidator.Validate(gamePath).IsValid)
        {
            return;
        }

        var result = await Task.Run(() =>
            _services.GameManagers.Apply(gamePath, _services.Config.SkipSplashScreen));

        _services.Logger.Info("Settings",
            $"跳过启动画面 = {_services.Config.SkipSplashScreen}：{result.Message}" +
            (result.BackedUp ? "（已备份原始文件）" : string.Empty));

        if (!result.Changed && !result.BackedUp &&
            !result.Message.Contains("已是目标状态", StringComparison.Ordinal))
        {
            await ShowDialogAsync(L("common.error"), result.Message);
        }
    }

    private async void OnInjectModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var mode = DoorstopRadio.IsChecked == true ? InjectionMode.Doorstop : InjectionMode.Json;
        if (_services.Config.Client.InjectionMode == mode)
        {
            return;
        }

        _services.Config.Client.InjectionMode = mode;
        Persist();

        // 切换模式需要重写游戏目录，先确认再执行。
        if (!GameInstallValidator.Validate(_services.GamePath).IsValid)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L("settings.injection"),
            Content = L("settings.injectionHint"),
            PrimaryButtonText = L("common.confirm"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // 先清掉另一种模式的产物，避免两种注入同时存在。
        IInjector other = mode == InjectionMode.Json
            ? _services.DoorstopInjector
            : _services.JsonInjector;
        await other.RemoveAsync(_services.GamePath);

        var result = await _services.GetInjector(mode).ApplyAsync(_services.GamePath);
        _services.Logger.Info("Injection", result.Message);

        await ShowDialogAsync(
            result.Success ? L("common.success") : L("common.error"),
            result.Message);
    }

    private void OnCloseToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _services.Config.Client.CloseToTray = CloseToTrayToggle.IsOn;
        Persist();

        if (App.MainWindowInstance is MainWindow window)
        {
            window.SetCloseToTray(CloseToTrayToggle.IsOn);
        }
    }

    private void OnCheckUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _services.Config.Client.CheckUpdatesOnStartup = CheckUpdatesToggle.IsOn;
        Persist();
    }

    private void OnMinimizeOnLaunchToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _services.Config.Client.MinimizeOnLaunch = MinimizeOnLaunchToggle.IsOn;
        Persist();
    }

    private void OnUploadLogsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _services.Config.Client.UploadCrashLogs = UploadLogsToggle.IsOn;
        Persist();
    }

    private async void OnDetectClick(object sender, RoutedEventArgs e)
    {
        var found = await Task.Run(() => _services.SteamLocator.FindRaftInstallations());
        var install = found.FirstOrDefault(i => i.IsValid) ?? found.FirstOrDefault();

        if (install is null)
        {
            await ShowDialogAsync(L("common.warning"), L("home.gamePathMissing"));
            return;
        }

        _services.UpdateGamePath(install.GamePath);
        LoadValues();
        _services.Logger.Info("Game", $"检测到游戏目录：{install.GamePath}");
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
        LoadValues();
    }

    private async void OnVerifyClick(object sender, RoutedEventArgs e)
    {
        var probe = await _services.GetInjector().ProbeAsync(_services.GamePath);
        var details = probe.Details.Count == 0
            ? probe.State.ToString()
            : string.Join("\n", probe.Details);

        await ShowDialogAsync($"{L("settings.verify")} — {probe.State}", details);
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var result = await _services.GetInjector().RemoveAsync(_services.GamePath);
        _services.Logger.Info("Injection", result.Message);

        await ShowDialogAsync(
            result.Success ? L("common.success") : L("common.error"),
            result.Message);
    }

    private void OnOpenDataClick(object sender, RoutedEventArgs e) => OpenFolder(_services.Paths.Root);

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => OpenFolder(_services.Paths.Logs);

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }

    private async void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (!_services.Backup.HasBackup)
        {
            await ShowDialogAsync(L("common.warning"), L("settings.restoreHint"));
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L("settings.restore"),
            Content = L("settings.restoreHint"),
            PrimaryButtonText = L("common.confirm"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var restored = _services.Backup.Restore(_services.GamePath);
            _services.Logger.Info("Injection", $"已还原 {restored.Count} 个文件");

            await ShowDialogAsync(L("common.success"),
                string.Join("\n", restored));
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Injection", "还原失败", ex);
            await ShowDialogAsync(L("common.error"), ex.Message);
        }
    }

    /// <summary>
    /// 一键还原为官方：清掉两种注入产物、还原 globalgamemanagers、删掉本客户端释放的加载器。
    /// 保留 mods / logs / config.ini，绝不删用户模组。
    /// </summary>
    private async void OnRestoreOfficialClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L("settings.restoreOfficial"),
            Content = new TextBlock
            {
                Text = L("settings.restoreOfficialHint"),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = L("common.confirm"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        RestoreOfficialButton.IsEnabled = false;

        try
        {
            var report = await _services.OfficialRestore.RestoreAsync(_services.GamePath);

            await ShowDialogAsync(
                report.Success ? L("common.success") : L("common.warning"),
                report.Summary);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Restore", "还原为官方失败", ex);
            await ShowDialogAsync(L("common.error"), ex.Message);
        }
        finally
        {
            RestoreOfficialButton.IsEnabled = true;
            LoadValues();
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
