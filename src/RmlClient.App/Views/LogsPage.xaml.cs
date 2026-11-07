using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RmlClient.App.Services;
using RmlClient.Core.Logging;

namespace RmlClient.App.Views;

public sealed partial class LogsPage : Page
{
    private AppServices _services = null!;
    private LogTailer? _tailer;
    private readonly List<string> _loaderLines = new();
    private bool _showLoader;
    private bool _autoScroll = true;

    public LogsPage()
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

        _services.Logger.EntryAdded += OnLogEntryAdded;

        // 跟随 HMLCore/AssemblyLoader 写的 hloader.log（游戏内的日志）。
        _tailer = new LogTailer(_services.Paths.HLoaderLog);
        _tailer.LineReceived += OnLoaderLine;
        _tailer.Start();

        UpdateTabStyles();
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        _services.Logger.EntryAdded -= OnLogEntryAdded;
        _tailer?.Dispose();
        _tailer = null;
    }

    private string L(string key) => _services.Localization[key];

    private void ApplyLocalization()
    {
        TitleText.Text = L("logs.title");
        ClientTab.Content = L("logs.client");
        LoaderTab.Content = L("logs.loader");
        AutoScrollCheck.Content = L("logs.autoScroll");
        ClearButton.Content = L("logs.clear");
        ExportButton.Content = L("logs.export");
        EmptyText.Text = L("logs.empty");
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            _showLoader = tag == "loader";
            UpdateTabStyles();
            Render();
        }
    }

    private void UpdateTabStyles()
    {
        StyleTab(ClientTab, !_showLoader);
        StyleTab(LoaderTab, _showLoader);
    }

    private void StyleTab(Button tab, bool active)
    {
        if (active)
        {
            tab.Background = (Brush)Application.Current.Resources["AccentBrush"];
            tab.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 4, 18, 26));
            tab.BorderThickness = new Thickness(0);
        }
        else
        {
            tab.Background = (Brush)Application.Current.Resources["SurfaceBrush"];
            tab.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            tab.BorderThickness = new Thickness(1);
            tab.BorderBrush = (Brush)Application.Current.Resources["BorderBrush"];
        }
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        if (_showLoader)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            Append(entry.Format());
        });
    }

    private void OnLoaderLine(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _loaderLines.Add(line);

            // 防止长时间挂机把内存吃满。
            if (_loaderLines.Count > 20000)
            {
                _loaderLines.RemoveRange(0, 5000);
            }

            if (_showLoader)
            {
                Append(line);
            }
        });
    }

    private void Append(string line)
    {
        var builder = new StringBuilder(LogText.Text);

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(line);

        // 总字符数上限，避免 TextBlock 布局卡死。
        if (builder.Length > 1_500_000)
        {
            var trimAt = builder.Length - 1_200_000;
            var newline = builder.ToString().IndexOf('\n', trimAt);
            builder.Remove(0, newline >= 0 ? newline + 1 : trimAt);
        }

        LogText.Text = builder.ToString();
        EmptyText.Visibility = Visibility.Collapsed;

        if (_autoScroll)
        {
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private void Render()
    {
        var lines = _showLoader
            ? _loaderLines
            : _services.Logger.Snapshot().Select(e => e.Format()).ToList();

        LogText.Text = string.Join('\n', lines);
        EmptyText.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_autoScroll)
        {
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private void OnAutoScrollChanged(object sender, RoutedEventArgs e) =>
        _autoScroll = AutoScrollCheck.IsChecked == true;

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_showLoader)
        {
            _loaderLines.Clear();
        }
        else
        {
            _services.Logger.Clear();
        }

        Render();
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = $"rml-logs-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("Text", new List<string> { ".log", ".txt" });

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var content = _showLoader
            ? string.Join('\n', _loaderLines)
            : string.Join('\n', _services.Logger.Snapshot().Select(x => x.Format()));

        await Windows.Storage.FileIO.WriteTextAsync(file, content);
    }
}
