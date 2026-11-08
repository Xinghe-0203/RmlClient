using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using RmlClient.App.Controls;
using RmlClient.App.Services;
using RmlClient.Core.Market;
using RmlClient.Core.Mods;
using RmlClient.Core.Translation;
using RmlClient.Core.Updates;

namespace RmlClient.App.Views;

public sealed partial class MarketPage : Page
{
    private AppServices _services = null!;
    private string _query = string.Empty;
    private string _category = "all";
    private bool _busy;

    public MarketPage()
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

        var market = _services.Market;
        if (market is null)
        {
            ShowEmpty(L("mods.noGame"), string.Empty);
            return;
        }

        if (!market.HasData)
        {
            market.LoadFromDisk();
        }

        BuildCategories();
        RebuildGrid();

        if (!market.HasData)
        {
            await RefreshAsync();
        }
        else
        {
            StatusText.Text = $"{market.Mods.Count} mods · {market.LastFetchedAt.LocalDateTime:MM-dd HH:mm}";
            await EnsureTitlesTranslatedAsync();

            // 缓存过期就后台补一次。先渲染旧数据（用户能立刻看到内容），
            // 拉到新数据再重画，失败也不打断浏览。
            if (market.IsStale)
            {
                _ = RefreshInBackgroundAsync();
            }
        }
    }

    /// <summary>
    /// 后台静默刷新过期缓存。与 <see cref="RefreshAsync"/> 的区别：
    /// 不显示加载遮罩、不弹错误框，失败只记日志（旧数据仍然可用）。
    /// </summary>
    private async Task RefreshInBackgroundAsync()
    {
        var market = _services.Market;
        if (market is null || _busy)
        {
            return;
        }

        try
        {
            var count = await market.RefreshAsync();
            _services.Logger.Info("Market", $"缓存已过期，后台刷新 {count} 个 mod。");

            RebuildGrid();
            StatusText.Text = $"{market.Mods.Count} mods · {market.LastFetchedAt.LocalDateTime:MM-dd HH:mm}";
            await EnsureTitlesTranslatedAsync();
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("Market", $"后台刷新失败，继续使用旧缓存：{ex.Message}");
        }
    }

    /// <summary>
    /// 把市场标题翻成当前目标语言，然后刷新列表。
    ///
    /// 只翻「还没翻过的」：结果落在 <c>cache\market-titles.json</c>，
    /// 第二次进市场页直接命中，不再发请求。
    /// </summary>
    private async Task EnsureTitlesTranslatedAsync()
    {
        var market = _services.Market;

        if (market is null || !market.HasData)
        {
            return;
        }

        if (!_services.Config.Client.AutoTranslate)
        {
            return;
        }

        var target = TranslationLanguage.Find(_services.Config.Client.TranslateTarget)
                     ?? TranslationLanguage.SystemDefault();

        // 已经是这个语言并且翻全了，不用再走一遍。
        if (_services.MarketTitles.CurrentLanguage == target.Code &&
            _services.MarketTitles.Snapshot().Count >= market.Mods.Count)
        {
            return;
        }

        try
        {
            StatusText.Text = L("market.translatingTitles");

            await _services.MarketTitles.EnsureAsync(market.Mods, target);

            RebuildGrid();

            StatusText.Text = $"{market.Mods.Count} mods · {market.LastFetchedAt.LocalDateTime:MM-dd HH:mm}";
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("Translate", $"市场标题翻译失败：{ex.Message}");
            StatusText.Text = $"{market.Mods.Count} mods · {market.LastFetchedAt.LocalDateTime:MM-dd HH:mm}";
        }
    }

    private string L(string key) => _services.Localization[key];

    private void ApplyLocalization()
    {
        TitleText.Text = L("market.title");
        SearchBox.PlaceholderText = L("market.searchPlaceholder");
        RefreshButton.Content = L("common.refresh");
        EmptyRefreshButton.Content = L("common.refresh");
        LoadingText.Text = L("market.fetching");
    }

    private void BuildCategories()
    {
        CategoryPanel.Children.Clear();
        CategoryPanel.Children.Add(MakeChip("all", L("common.all")));

        foreach (var category in _services.Market?.Categories ?? Array.Empty<string>())
        {
            CategoryPanel.Children.Add(MakeChip(category, CategoryLabel(category)));
        }
    }

    private Button MakeChip(string key, string label)
    {
        var chip = new Button
        {
            Content = label,
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            FontSize = 12,
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(1),
            Tag = key,
        };

        ApplyChipStyle(chip, key == _category);

        chip.Click += (_, _) =>
        {
            _category = key;

            foreach (var child in CategoryPanel.Children.OfType<Button>())
            {
                ApplyChipStyle(child, (string)child.Tag == _category);
            }

            RebuildGrid();
        };

        return chip;
    }

    private static void ApplyChipStyle(Button chip, bool selected)
    {
        if (selected)
        {
            chip.Background = (Brush)Application.Current.Resources["AccentBrush"];
            chip.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 4, 18, 26));
            chip.BorderBrush = (Brush)Application.Current.Resources["AccentBorderBrush"];
        }
        else
        {
            chip.Background = (Brush)Application.Current.Resources["SurfaceBrush"];
            chip.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            chip.BorderBrush = (Brush)Application.Current.Resources["BorderBrush"];
        }
    }

    private string CategoryLabel(string category) => category.ToLowerInvariant() switch
    {
        "utility" => _services.Localization.IsChinese ? "实用" : "Utility",
        "content" => _services.Localization.IsChinese ? "内容" : "Content",
        "fun" => _services.Localization.IsChinese ? "娱乐" : "Fun",
        "optimization" => _services.Localization.IsChinese ? "优化" : "Optimization",
        _ => category,
    };

    private void RebuildGrid()
    {
        var market = _services.Market;
        if (market is null)
        {
            return;
        }

        var results = market.Search(_query, _category, MarketSort.Popular, _services.MarketTitles.Snapshot());
        var local = _services.Mods?.Scan() ?? Array.Empty<ModEntry>();

        ModsGrid.Items.Clear();

        if (results.Count == 0)
        {
            ShowEmpty(L("market.noResults"), market.HasData ? string.Empty : L("market.offline"));
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        ModsGrid.Visibility = Visibility.Visible;

        foreach (var mod in results)
        {
            ModsGrid.Items.Add(BuildCard(mod, market.GetLocalState(mod, local)));
        }
    }

    private void ShowEmpty(string text, string hint)
    {
        ModsGrid.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        EmptyText.Text = text;
        EmptyHint.Text = hint;
    }

    private UIElement BuildCard(MarketMod mod, (bool Installed, string? LocalVersion, bool Enabled) state)
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 8, 8),
            Width = 240,
            Height = 188,
        };

        var root = new Grid { Tag = mod };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 头部：图标 + 标题
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconHost = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["SurfaceAltBrush"],
            Margin = new Thickness(0, 0, 10, 0),
        };

        var icon = LoadRemoteImage(mod.IconImageUrl, 40, 40);
        iconHost.Child = icon is not null
            ? icon
            : new TextBlock
            {
                Text = "\uE719",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        header.Children.Add(iconHost);

        var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = _services.MarketTitles.GetTitle(mod),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });

        titleStack.Children.Add(new TextBlock
        {
            Text = $"{mod.Author} · v{mod.LatestVersion?.Version ?? "?"}",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });

        Grid.SetColumn(titleStack, 1);
        header.Children.Add(titleStack);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // 描述
        var description = new TextBlock
        {
            Text = StripMarkup(mod.Description),
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 3,
            Margin = new Thickness(0, 10, 0, 0),
        };

        Grid.SetRow(description, 1);
        root.Children.Add(description);

        // 底部：下载量 + 按钮
        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        footer.Children.Add(new TextBlock
        {
            Text = $"{FormatCount(mod.TotalDownloads)} ↓",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        var action = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 11.5,
            Content = state.Installed ? L("common.installed") : L("common.install"),
            IsEnabled = !state.Installed,
            Tag = mod,
        };

        action.Click += OnInstallClick;
        Grid.SetColumn(action, 1);
        footer.Children.Add(action);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        card.Child = root;
        return card;
    }

    private static Image? LoadRemoteImage(string? url, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            var absolute = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? url
                : "https://www.raftmodding.com" + (url.StartsWith('/') ? url : "/" + url);

            return new Image
            {
                Source = new BitmapImage(new Uri(absolute)),
                Stretch = Stretch.UniformToFill,
                Width = width,
                Height = height,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: MarketMod mod } button)
        {
            return;
        }

        var market = _services.Market;
        if (market is null)
        {
            return;
        }

        SetBusy(true, $"{L("common.install")} {mod.Title}");

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                LoadingText.Text = $"{mod.Title}  {p.HumanReadable}";
            });

            var result = await market.InstallAsync(mod, mod.LatestVersion, progress);

            _services.Logger.Info("Market", result.Message);

            if (result.Success)
            {
                button.Content = L("common.installed");
                button.IsEnabled = false;
                RebuildGrid();
            }
            else
            {
                await ShowDialogAsync(L("common.error"), result.Message);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Market", "安装失败", ex);
            await ShowDialogAsync(L("common.error"), ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Border { Child: Grid grid } ||
            grid.Tag is not MarketMod mod)
        {
            return;
        }

        await ShowDetailsAsync(mod);
    }

    private async Task ShowDetailsAsync(MarketMod mod)
    {
        var model = ModDetailModel.FromMarket(mod, L("details.notInMarket"), _services.MarketTitles.GetTitle(mod));

        try
        {
            await ModDetailDialog.ShowAsync(_services, model, XamlRoot, view =>
            {
                var install = new Button
                {
                    Content = L("common.install"),
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                    Height = 34,
                    Padding = new Thickness(18, 0, 18, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                install.Click += async (_, _) =>
                {
                    if (view.SelectedVersion?.MarketVersion is not { } version)
                    {
                        return;
                    }

                    SetBusy(true, $"{L("common.install")} {mod.Title} v{version.Version}");
                    var result = await _services.Market!.InstallAsync(mod, version);
                    SetBusy(false);

                    _services.Logger.Info("Market", result.Message);
                    await ShowDialogAsync(
                        result.Success ? L("common.success") : L("common.error"),
                        result.Message);
                    RebuildGrid();
                };

                view.Actions.Children.Add(install);
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Market", $"打开 {mod.Title} 详情失败", ex);
        }
    }

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, L("market.fetching"));

        try
        {
            var status = new Progress<string>(s => StatusText.Text = s);
            var count = await _services.Market!.RefreshAsync(status);

            BuildCategories();
            RebuildGrid();
            StatusText.Text = $"{count} mods · {DateTime.Now:MM-dd HH:mm}";

            // 刷新完再补翻译：新抓到的 mod 可能还没译名。
            await EnsureTitlesTranslatedAsync();
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("Market", $"刷新失败：{ex.Message}");
            StatusText.Text = L("market.offline");

            if (!_services.Market!.HasData)
            {
                ShowEmpty(L("market.offline"), ex.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text?.Trim() ?? string.Empty;
        RebuildGrid();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        LoadingOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !busy;

        if (message is not null)
        {
            LoadingText.Text = message;
        }
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = L("common.close"),
        };

        await dialog.ShowAsync();
    }

    private static string StripMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // 市场描述里混着 BBCode/HTML 片段，列表里直接显示会很脏。
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"\[[^\]]+\]", string.Empty);
        return stripped.Replace("&nbsp;", " ").Replace("&amp;", "&").Trim();
    }

    private static string FormatCount(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0.#}k",
        _ => count.ToString(),
    };
}
