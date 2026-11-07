using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RmlClient.App.Services;
using RmlClient.App.Views;
using RmlClient.Core.Game;

namespace RmlClient.App;

public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly List<(string Key, string Icon, Type PageType, Button Button)> _navItems = new();
    private bool _closeToTray;
    private bool _forceClose;
    private bool _onboarding;
    private int _selectedIndex = -1;
    private TrayIcon? _tray;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        Title = "Raft Mod Loader Client";

        var isDark = services.Config.Client.Theme.Equals("dark", StringComparison.OrdinalIgnoreCase);

        WindowHelper.Resize(this, 1100, 720);
        WindowHelper.SetMinSize(this, 900, 600);
        WindowHelper.CenterOnScreen(this);
        WindowHelper.SetTitleBarColors(this, dark: isDark);
        WindowHelper.UseCustomTitleBar(this, TitleBarGrid);

        _closeToTray = services.Config.Client.CloseToTray;

        // 首次启动（还没同意条款，或者根本没有可用的游戏目录）先进引导，
        // 引导完成后再切回常规布局。必须在建导航之前定下来，
        // 否则 BuildNavigation 会先把启动页塞进 Frame。
        _onboarding = services.Config.AgreeWithTos == false
            || !GameInstallValidator.Validate(services.GamePath).IsValid;

        BuildNavigation();
        UpdateStatus();

        if (_onboarding)
        {
            EnterOnboarding();
        }
        else
        {
            SelectNav(0);
        }

        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;

        // 第二个实例被启动时，它会 PostMessage 过来；这里把窗口叫回前台，
        // 让用户看起来就是「又打开了原来的窗口」。
        MinSizeHelper.ShowRequested = () =>
        {
            try
            {
                RestoreFromTray();
            }
            catch (Exception ex)
            {
                services.Logger.Warn("Window", $"响应重复启动失败：{ex.Message}");
            }
        };

        // 导航按钮的模板要等窗口真正布局完才套用，
        // 构造期调用 GoToState 会被静默忽略，所以加载完成后再重放一次选中态。
        // 注意：引导流程里 ContentFrame 装的是 OnboardingPage，
        // 这里不能再走 SelectNav，否则会把引导页顶掉。
        NavPanel.Loaded += (_, _) =>
        {
            if (!_onboarding && _selectedIndex >= 0)
            {
                SelectNav(_selectedIndex);
            }
        };

        // 语言切换后刷新导航文字
        _services.Localization.LanguageChanged += OnLanguageChanged;

        if (_closeToTray)
        {
            EnsureTray();
        }
    }

    private void BuildNavigation()
    {
        _navItems.Clear();
        NavPanel.Children.Clear();

        AddNav("nav.dashboard", "\uE80F", typeof(DashboardPage));
        AddNav("nav.mods", "\uE8B7", typeof(ModsPage));
        AddNav("nav.market", "\uE719", typeof(MarketPage));
        AddNav("nav.logs", "\uE9D9", typeof(LogsPage));
        AddNav("nav.settings", "\uE713", typeof(SettingsPage));
    }

    private void AddNav(string key, string icon, Type pageType)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBlock = new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 22,
        };
        Grid.SetColumn(iconBlock, 0);
        content.Children.Add(iconBlock);

        var label = new TextBlock
        {
            Text = _services.Localization[key],
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["NavItemButtonStyle"],
            Content = content,
            Tag = key,
        };

        // 导航按钮的内容是图标 + 文本的 Grid，没有可读文本，
        // 屏幕阅读器和 UI Automation 都拿不到名字，这里显式补上。
        AutomationProperties.SetName(button, label.Text);
        AutomationProperties.SetAutomationId(button, key);

        var index = _navItems.Count;
        button.Click += (_, _) => SelectNav(index);

        NavPanel.Children.Add(button);
        _navItems.Add((key, icon, pageType, button));
    }

    private void SelectNav(int index)
    {
        if (index < 0 || index >= _navItems.Count)
        {
            return;
        }

        _selectedIndex = index;

        for (var i = 0; i < _navItems.Count; i++)
        {
            var button = _navItems[i].Button;

            // 首次调用发生在窗口尚未布局时，模板还没套用，
            // GoToState 会静默失败（返回 false），必须先强制套用模板。
            button.ApplyTemplate();
            VisualStateManager.GoToState(button,
                i == index ? "Selected" : "Unselected", useTransitions: false);
        }

        var target = _navItems[index].PageType;
        if (ContentFrame.CurrentSourcePageType != target)
        {
            try
            {
                ContentFrame.Navigate(target, _services);
            }
            catch (Exception ex)
            {
                // 页面构造或 OnNavigatedTo 里抛异常时，Frame 会静默停在上一个页面，
                // 界面上只表现为「点了没反应」，必须留下证据。
                _services.Logger.Error("Navigation", $"导航到 {target.Name} 失败", ex);
            }
        }
    }

    public void NavigateTo(string navKey)
    {
        var index = _navItems.FindIndex(i => i.Key == navKey);
        if (index >= 0)
        {
            SelectNav(index);
        }
    }

    /// <summary>
    /// 进入引导流程：引导页占满整个窗口，侧边栏与导航一起收起来，
    /// 避免用户在还没配好环境时点进其它页面。
    /// </summary>
    private void EnterOnboarding()
    {
        _onboarding = true;
        Sidebar.Visibility = Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(0);
        ContentFrame.Navigate(typeof(OnboardingPage), _services);
    }

    /// <summary>引导完成后切回常规布局，落在启动页。</summary>
    public void FinishOnboarding()
    {
        _onboarding = false;
        Sidebar.Visibility = Visibility.Visible;
        SidebarColumn.Width = new GridLength(220);

        BuildNavigation();
        SelectNav(0);
    }

    /// <summary>侧边栏底部状态：就绪 / 检查中 / 游戏运行中。</summary>
    public void UpdateStatus(string? text = null, bool? isBusy = null)
    {
        StatusText.Text = text ?? (_services.Localization.IsChinese ? "就绪" : "Ready");

        var brushKey = isBusy == true ? "WarningBrush" : "SuccessBrush";
        if (isBusy == false && text is null)
        {
            brushKey = "TextMutedBrush";
        }

        if (Application.Current.Resources.TryGetValue(brushKey, out var brush) && brush is Brush b)
        {
            StatusDot.Fill = b;
        }
    }

    private void OnLanguageChanged()
    {
        UpdateBrandSubtitle();

        for (var i = 0; i < _navItems.Count; i++)
        {
            var (key, _, _, button) = _navItems[i];
            if (button.Content is Grid grid && grid.Children.Count > 1 &&
                grid.Children[1] is TextBlock label)
            {
                label.Text = _services.Localization[key];
            }
        }

        UpdateStatus();
    }

    /// <summary>
    /// 侧栏副标题显示当前更新分支。标题栏已经有产品名了，
    /// 这里再写一遍 "Raft Mod Loader" 只会显得重复。
    /// </summary>
    private void UpdateBrandSubtitle()
    {
        var branch = _services.Config.Branch;
        BrandSubtitle.Text = _services.Localization.IsChinese
            ? $"{branch} 分支"
            : $"{branch} branch";
    }

    // 最小化/最大化/关闭由系统标题栏按钮处理（见 MainWindow.xaml 注释），
    // 这里只保留关闭到托盘的拦截逻辑。

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_forceClose || !_closeToTray)
        {
            return;
        }

        // 关闭到托盘：取消关闭，隐藏窗口。托盘图标必须已经在跑，
        // 否则窗口一藏就再也叫不回来了。
        if (_tray is not { IsAvailable: true })
        {
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
    }

    public void SetCloseToTray(bool enabled)
    {
        _closeToTray = enabled;

        if (enabled)
        {
            EnsureTray();
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
        }
    }

    /// <summary>从托盘恢复窗口。</summary>
    public void RestoreFromTray()
    {
        AppWindow.Show();
        Activate();
        WindowHelper.BringToFront(this);
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void EnsureTray()
    {
        if (_tray is not null)
        {
            return;
        }

        var loc = _services.Localization;
        var tray = new TrayIcon(
            Environment.ProcessPath ?? string.Empty,
            "Raft Mod Loader",
            loc["tray.open"],
            loc["tray.exit"]);

        // 托盘回调在托盘线程上，必须切回 UI 线程再碰窗口。
        tray.OpenRequested += () => DispatcherQueue.TryEnqueue(RestoreFromTray);
        tray.ExitRequested += () => DispatcherQueue.TryEnqueue(ForceClose);
        tray.Start();

        _tray = tray;

        // Shell_NotifyIcon 是异步注册的，这里不阻塞等待，稍后再记一条实际状态。
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(600);
            _services.Logger.Info("Tray", $"托盘图标可用性：{tray.IsAvailable}");
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        MinSizeHelper.ShowRequested = null;
        _services.Localization.LanguageChanged -= OnLanguageChanged;
        _tray?.Dispose();
        _tray = null;
        _services.Dispose();
        SingleInstance.Release();
    }
}
