using System.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RmlClient.App.Services;
using RmlClient.Core.Configuration;
using RmlClient.Core.Logging;

namespace RmlClient.App.Views;

public sealed partial class LogsPage : Page
{
    /// <summary>一次最多渲染多少行。RichTextBlock 每行一个 Paragraph，行数太多布局会卡。</summary>
    private const int MaxRenderedLines = 3000;

    /// <summary>每份日志在内存里最多保留多少行。</summary>
    private const int MaxBufferedLines = 20000;

    private AppServices _services = null!;
    private LogTailer? _loaderTailer;
    private LogTailer? _gameTailer;

    private readonly List<LogLine> _clientLines = new();
    private readonly List<LogLine> _loaderLines = new();
    private readonly List<LogLine> _gameLines = new();

    private string _tab = "client";
    private bool _autoScroll = true;
    private LogLevel _minLevel = LogLevel.Trace;
    private string _query = string.Empty;
    private bool _loadingFilters;

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
        BuildLevelFilter();

        // 客户端已有的日志先灌进来，避免切页后历史丢失。
        _clientLines.AddRange(_services.Logger.Snapshot().Select(ToLine));

        _services.Logger.EntryAdded += OnLogEntryAdded;

        // 跟随 AssemblyLoader 写的 hloader.log（游戏内加载器的日志）。
        _loaderTailer = new LogTailer(_services.Paths.HLoaderLog);
        _loaderTailer.LineReceived += OnLoaderLine;
        _loaderTailer.Start();

        // 跟随 Unity 自己的 Player.log。它才是「游戏为什么崩了」的第一现场。
        // 首次回读 300 行：崩溃通常发生在刚启动时，只看新内容会错过现场。
        _gameTailer = new LogTailer(RmlPaths.GamePlayerLog, initialTailLines: 300);
        _gameTailer.LineReceived += OnGameLine;
        _gameTailer.Start();

        UpdateTabStyles();
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (_services is not null)
        {
            _services.Logger.EntryAdded -= OnLogEntryAdded;
        }

        _loaderTailer?.Dispose();
        _loaderTailer = null;

        _gameTailer?.Dispose();
        _gameTailer = null;
    }

    private string L(string key) => _services.Localization[key];

    private void ApplyLocalization()
    {
        TitleText.Text = L("logs.title");
        ClientTab.Content = L("logs.client");
        LoaderTab.Content = L("logs.loader");
        GameTab.Content = L("logs.game");
        AutoScrollCheck.Content = L("logs.autoScroll");
        ClearButton.Content = L("logs.clear");
        ExportButton.Content = L("logs.export");
        SearchBox.PlaceholderText = L("logs.searchPlaceholder");
        EmptyText.Text = L("logs.empty");
    }

    private void BuildLevelFilter()
    {
        _loadingFilters = true;

        LevelCombo.Items.Clear();
        LevelCombo.Items.Add(new ComboBoxItem { Content = L("logs.levelAll"), Tag = LogLevel.Trace });
        LevelCombo.Items.Add(new ComboBoxItem { Content = L("logs.levelWarn"), Tag = LogLevel.Warn });
        LevelCombo.Items.Add(new ComboBoxItem { Content = L("logs.levelError"), Tag = LogLevel.Error });
        LevelCombo.SelectedIndex = 0;

        _loadingFilters = false;
    }

    private static LogLine ToLine(LogEntry entry) => new(entry.Format(), entry.Level);

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            _tab = tag;
            UpdateTabStyles();
            Render();
        }
    }

    private void UpdateTabStyles()
    {
        StyleTab(ClientTab, _tab == "client");
        StyleTab(LoaderTab, _tab == "loader");
        StyleTab(GameTab, _tab == "game");
    }

    private void StyleTab(Button tab, bool active)
    {
        if (active)
        {
            tab.Background = (Brush)Application.Current.Resources["AccentBrush"];
            tab.Foreground = new SolidColorBrush(Colors.White);
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
        DispatcherQueue.TryEnqueue(() =>
        {
            _clientLines.Add(ToLine(entry));
            TrimBuffer(_clientLines);

            if (_tab == "client" && Passes(entry.Level, entry.Format()))
            {
                AppendToView(entry.Format(), entry.Level);
            }
        });
    }

    private void OnLoaderLine(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var level = GuessLevel(line);
            _loaderLines.Add(new LogLine(line, level));
            TrimBuffer(_loaderLines);

            if (_tab == "loader" && Passes(level, line))
            {
                AppendToView(line, level);
            }
        });
    }

    private void OnGameLine(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var level = GuessLevel(line);
            _gameLines.Add(new LogLine(line, level));
            TrimBuffer(_gameLines);

            if (_tab == "game" && Passes(level, line))
            {
                AppendToView(line, level);
            }
        });
    }

    /// <summary>
    /// hloader.log 和 Player.log 都是纯文本，没有结构化级别。
    /// 官方 HLogger 写警告/错误时加 <c>[Warning]</c> / <c>[Error]</c> 前缀，Unity 则写
    /// <c>Exception</c> / <c>NullReferenceException</c> 之类，这里按文本猜一个级别用于着色。
    /// </summary>
    private static LogLevel GuessLevel(string line)
    {
        if (line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error CS", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }

        if (line.Contains("[Warning]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("warning CS", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Warning:", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warn;
        }

        return LogLevel.Info;
    }

    private static void TrimBuffer(List<LogLine> buffer)
    {
        if (buffer.Count > MaxBufferedLines)
        {
            buffer.RemoveRange(0, buffer.Count - MaxBufferedLines);
        }
    }

    private bool Passes(LogLevel level, string text)
    {
        if (level < _minLevel)
        {
            return false;
        }

        return _query.Length == 0 ||
               text.Contains(_query, StringComparison.CurrentCultureIgnoreCase);
    }

    private List<LogLine> CurrentBuffer() => _tab switch
    {
        "loader" => _loaderLines,
        "game" => _gameLines,
        _ => _clientLines,
    };

    private Brush BrushFor(LogLevel level) => level switch
    {
        LogLevel.Error => (Brush)Application.Current.Resources["DangerBrush"],
        LogLevel.Warn => (Brush)Application.Current.Resources["WarningBrush"],
        LogLevel.Debug or LogLevel.Trace => (Brush)Application.Current.Resources["TextMutedBrush"],
        _ => (Brush)Application.Current.Resources["TextSecondaryBrush"],
    };

    /// <summary>增量追加一行（自动滚动时用，避免整块重建）。</summary>
    private void AppendToView(string text, LogLevel level)
    {
        LogRich.Blocks.Add(BuildParagraph(text, BrushFor(level)));
        EmptyText.Visibility = Visibility.Collapsed;

        // 视图里堆积太多行也会卡，超出上限就把最老的段落丢掉。
        while (LogRich.Blocks.Count > MaxRenderedLines)
        {
            LogRich.Blocks.RemoveAt(0);
        }

        if (_autoScroll)
        {
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private static Paragraph BuildParagraph(string text, Brush brush)
    {
        var run = new Run { Text = text, Foreground = brush };
        var paragraph = new Paragraph { LineHeight = 18 };
        paragraph.Inlines.Add(run);
        return paragraph;
    }

    private void Render()
    {
        var buffer = CurrentBuffer();
        var matched = buffer.Where(l => Passes(l.Level, l.Text)).ToList();
        var shown = matched.Count > MaxRenderedLines
            ? matched.Skip(matched.Count - MaxRenderedLines).ToList()
            : matched;

        LogRich.Blocks.Clear();

        foreach (var line in shown)
        {
            LogRich.Blocks.Add(BuildParagraph(line.Text, BrushFor(line.Level)));
        }

        if (_tab == "game" && buffer.Count == 0)
        {
            EmptyText.Text = L("logs.gameMissing");
        }
        else
        {
            EmptyText.Text = L("logs.empty");
        }

        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        FilterHint.Text = string.Format(L("logs.matches"), shown.Count, buffer.Count);

        if (_autoScroll && shown.Count > 0)
        {
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private void OnAutoScrollChanged(object sender, RoutedEventArgs e) =>
        _autoScroll = AutoScrollCheck.IsChecked == true;

    private void OnLevelFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilters)
        {
            return;
        }

        _minLevel = LevelCombo.SelectedItem is ComboBoxItem { Tag: LogLevel level }
            ? level
            : LogLevel.Trace;

        Render();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text?.Trim() ?? string.Empty;
        Render();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        switch (_tab)
        {
            case "loader":
                _loaderLines.Clear();
                break;
            case "game":
                _gameLines.Clear();
                break;
            default:
                _clientLines.Clear();
                _services.Logger.Clear();
                break;
        }

        Render();
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = $"rml-logs-{_tab}-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("Text", new List<string> { ".log", ".txt" });

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var builder = new StringBuilder();

        foreach (var line in CurrentBuffer())
        {
            builder.Append(line.Text).Append('\n');
        }

        await Windows.Storage.FileIO.WriteTextAsync(file, builder.ToString());
    }

    private readonly record struct LogLine(string Text, LogLevel Level);
}
