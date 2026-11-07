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

/// <summary>
/// 首次启动引导：找到游戏 → 安装 Core → 注入加载器 → 完成。
/// 任意一步都可以跳过，但没找到游戏时后续步骤不可用。
/// </summary>
public sealed partial class OnboardingPage : Page
{
    private const int StepCount = 4;

    private AppServices _services = null!;
    private readonly List<(Border Dot, TextBlock Label)> _stepRows = new();
    private int _step;
    private bool _busy;
    private bool _termsAccepted;

    public OnboardingPage()
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

        // 老用户已经同意过条款，不用再勾一次。
        _termsAccepted = _services.Config.AgreeWithTos;
        TermsCheck.IsChecked = _termsAccepted;

        BuildStepList();
        ApplyLocalization();
        ShowStep(0);
    }

    private string L(string key) => _services.Localization[key];

    private void ApplyLocalization()
    {
        BrandText.Text = "Raft Mod Loader";
        BackButton.Content = L("onboarding.back");
        DetectButton.Content = L("settings.detect");
        BrowseButton.Content = L("common.browse");
        InstallCoreButton.Content = L("common.install");
        InjectButton.Content = L("home.inject");
        TermsCheck.Content = L("onboarding.terms");

        for (var i = 0; i < _stepRows.Count; i++)
        {
            _stepRows[i].Label.Text = L($"onboarding.step{i + 1}");
        }

        UpdateChrome();
    }

    private void BuildStepList()
    {
        StepList.Children.Clear();
        _stepRows.Clear();

        for (var i = 0; i < StepCount; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = (Brush)Application.Current.Resources["SurfaceAltBrush"],
                BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                },
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            var label = new TextBlock
            {
                Text = L($"onboarding.step{i + 1}"),
                FontSize = 13,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            StepList.Children.Add(row);
            _stepRows.Add((dot, label));
        }
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, StepCount - 1);

        GameSection.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        CoreSection.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        InjectSection.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        DoneSection.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        TermsCheck.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        StepTitle.Text = L($"onboarding.step{_step + 1}");
        StepHint.Text = L($"onboarding.step{_step + 1}Hint");

        RefreshStepContent();
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var dots = new[] { Dot1, Dot2, Dot3, Dot4 };
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i].Fill = (Brush)Application.Current.Resources[
                i == _step ? "AccentBrush" : "BorderBrush"];
        }

        for (var i = 0; i < _stepRows.Count; i++)
        {
            var (dot, label) = _stepRows[i];
            var isCurrent = i == _step;
            var isDone = i < _step;

            dot.Background = (Brush)Application.Current.Resources[
                isCurrent ? "AccentBrush" : isDone ? "SelectedBrush" : "SurfaceAltBrush"];
            dot.BorderBrush = (Brush)Application.Current.Resources[
                isCurrent || isDone ? "AccentBorderBrush" : "BorderBrush"];

            if (dot.Child is TextBlock number)
            {
                number.Foreground = (Brush)Application.Current.Resources[
                    isCurrent ? "TextPrimaryBrush" : "TextMutedBrush"];
            }

            label.Foreground = (Brush)Application.Current.Resources[
                isCurrent ? "TextPrimaryBrush" : "TextMutedBrush"];
            label.FontWeight = isCurrent
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }

        BackButton.IsEnabled = !_busy && _step > 0;
        BackButton.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;

        NextButton.Content = _step == StepCount - 1 ? L("onboarding.finish") : L("onboarding.next");
        NextButton.IsEnabled = !_busy && (_step != StepCount - 1 || _termsAccepted);
    }

    private void RefreshStepContent()
    {
        var gamePath = _services.GamePath;
        var validation = GameInstallValidator.Validate(gamePath);

        if (_step == 0)
        {
            FoundGameLabel.Text = validation.IsValid ? L("home.gamePath") : L("home.gamePathMissing");
            FoundGamePath.Text = string.IsNullOrWhiteSpace(gamePath) ? "—" : gamePath;
            FoundGamePath.Foreground = (Brush)Application.Current.Resources[
                validation.IsValid ? "TextPrimaryBrush" : "WarningBrush"];
            return;
        }

        if (_step == 1)
        {
            var hasCore = File.Exists(_services.Paths.CoreModDll);
            var hasAssets = File.Exists(_services.Paths.AssetsFile);

            CoreStateText.Text = hasCore && hasAssets
                ? $"Core {_services.Config.CoreVersion}"
                : L("common.notInstalled");
            CoreStateText.Foreground = (Brush)Application.Current.Resources[
                hasCore && hasAssets ? "TextPrimaryBrush" : "TextSecondaryBrush"];

            CoreDetailText.Text = $"coremod.dll  {(hasCore ? L("common.installed") : L("common.notInstalled"))}"
                + $"    ·    rml.assets  {(hasAssets ? L("common.installed") : L("common.notInstalled"))}";

            InstallCoreButton.Content = hasCore && hasAssets ? L("common.refresh") : L("common.install");
            InstallCoreButton.IsEnabled = !_busy && validation.IsValid;
            return;
        }

        if (_step == 2)
        {
            _ = RefreshInjectionAsync();
            return;
        }

        // 最后一步：汇总
        DoneSummary.Text = $"{L("home.gamePath")}: {gamePath}{Environment.NewLine}"
            + $"Core: {_services.Config.CoreVersion}{Environment.NewLine}"
            + $"{L("settings.branch")}: {_services.Config.Branch}";
        DoneHint.Text = L("onboarding.step4Hint");
    }

    private async Task RefreshInjectionAsync()
    {
        var gamePath = _services.GamePath;

        if (!GameInstallValidator.Validate(gamePath).IsValid)
        {
            InjectStateText.Text = L("home.gamePathMissing");
            InjectDetailText.Text = string.Empty;
            InjectButton.IsEnabled = false;
            return;
        }

        var probe = await _services.GetInjector().ProbeAsync(gamePath);

        InjectStateText.Text = probe.State switch
        {
            InjectionState.Injected => L("home.injection.json"),
            InjectionState.PartiallyInjected => L("home.injection.partial"),
            InjectionState.Corrupted => L("home.injection.corrupted"),
            _ => L("home.injection.none"),
        };

        InjectStateText.Foreground = (Brush)Application.Current.Resources[probe.State switch
        {
            InjectionState.Injected => "SuccessBrush",
            InjectionState.PartiallyInjected => "WarningBrush",
            InjectionState.Corrupted => "DangerBrush",
            _ => "TextMutedBrush",
        }];

        InjectDetailText.Text = probe.Details.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, probe.Details);

        InjectButton.Content = probe.IsHealthy ? L("home.repair") : L("home.inject");
        InjectButton.IsEnabled = !_busy;
    }

    private async void OnDetectClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);

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
            _services.Logger.Info("Onboarding", $"自动检测到游戏目录：{install.GamePath}（{install.Source}）");
            RefreshStepContent();
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
                $"{L("home.gamePathMissing")}{Environment.NewLine}{Environment.NewLine}"
                + string.Join(Environment.NewLine, validation.Problems));
            return;
        }

        _services.UpdateGamePath(folder.Path);
        RefreshStepContent();
    }

    private async void OnInstallCoreClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        CoreProgress.Value = 0;

        try
        {
            var coreProgress = new Progress<DownloadProgress>(p =>
            {
                CoreProgress.Value = p.Fraction * 100;
                CoreDetailText.Text = $"coremod  {p.HumanReadable}";
            });

            var assetsProgress = new Progress<DownloadProgress>(p =>
            {
                CoreDetailText.Text = $"rml.assets  {p.HumanReadable}";
            });

            var result = await _services.CoreUpdater.InstallAsync(
                _services.Config.Branch,
                coreProgress,
                assetsProgress);

            if (result.Success)
            {
                _services.Config.CoreVersion = result.Version ?? _services.Config.CoreVersion;
                _services.SaveConfig();
                _services.Logger.Info("Onboarding", result.Message);
                CoreProgress.Value = 100;
            }
            else
            {
                _services.Logger.Error("Onboarding", result.Message);
                await ShowDialogAsync(L("common.error"), result.Message);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Onboarding", "安装 Core 失败", ex);
            await ShowDialogAsync(L("common.error"), ex.Message);
        }
        finally
        {
            SetBusy(false);
            RefreshStepContent();
        }
    }

    private async void OnInjectClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            var injector = _services.GetInjector();
            var probe = await injector.ProbeAsync(_services.GamePath);
            var result = probe.IsHealthy
                ? await injector.RepairAsync(_services.GamePath)
                : await injector.ApplyAsync(_services.GamePath);

            if (result.Success)
            {
                _services.Logger.Info("Onboarding", result.Message);
            }
            else
            {
                _services.Logger.Error("Onboarding", result.Message);
                await ShowDialogAsync(L("common.error"), result.Message);
            }
        }
        finally
        {
            SetBusy(false);
            await RefreshInjectionAsync();
        }
    }

    private void OnTermsChanged(object sender, RoutedEventArgs e)
    {
        _termsAccepted = TermsCheck.IsChecked == true;
        UpdateChrome();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step < StepCount - 1)
        {
            ShowStep(_step + 1);
            return;
        }

        // 完成：落盘同意标记，然后交给主窗口切到启动页。
        if (_termsAccepted && !_services.Config.AgreeWithTos)
        {
            _services.Config.AgreeWithTos = true;
            _services.SaveConfig();
        }

        _services.Logger.Info("Onboarding", "引导流程完成");

        if (App.MainWindowInstance is MainWindow window)
        {
            window.FinishOnboarding();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DetectButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        InstallCoreButton.IsEnabled = !busy && GameInstallValidator.Validate(_services.GamePath).IsValid;
        InjectButton.IsEnabled = !busy;

        UpdateChrome();
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
