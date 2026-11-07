using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using RmlClient.App.Controls;
using RmlClient.App.Services;
using RmlClient.Core.Mods;
using RmlClient.Core.Translation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace RmlClient.App.Views;

public sealed partial class ModsPage : Page
{
    private AppServices _services = null!;
    private List<ModEntry> _all = new();
    private string _query = string.Empty;
    private bool _translating;

    public ModsPage()
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
        Reload();
        _ = TranslateTitlesAsync();
    }

    /// <summary>
    /// 把已安装模组的标题翻成目标语言，然后重建列表。
    ///
    /// 模组名多是英文（"Better Trees"、"Stack Boost"），中文界面下直接看很难懂；
    /// 译名落在 <c>cache\local-mod-titles.json</c>，第二次进页面直接命中。
    /// 翻译失败不影响列表 —— 回退显示原名。
    /// </summary>
    private async Task TranslateTitlesAsync()
    {
        if (_translating || !_services.Config.Client.AutoTranslate)
        {
            return;
        }

        var target = TranslationLanguage.Find(_services.Config.Client.TranslateTarget)
                     ?? TranslationLanguage.SystemDefault();

        var items = _all
            .Where(m => !string.IsNullOrWhiteSpace(m.DisplayName))
            .Select(m => (Key: TitleKey(m), Text: m.DisplayName))
            .ToList();

        if (items.Count == 0)
        {
            return;
        }

        if (_services.LocalModTitles.CurrentLanguage == target.Code &&
            _services.LocalModTitles.Snapshot().Count >= items.Count)
        {
            return;
        }

        _translating = true;

        try
        {
            await _services.LocalModTitles.EnsureAsync(items, target);
            RebuildList();
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("Translate", $"模组标题翻译失败：{ex.Message}");
        }
        finally
        {
            _translating = false;
        }
    }

    /// <summary>
    /// 标题翻译表的键：优先用市场 slug（同一 mod 换版本也能复用译名），
    /// 没有 updateUrl 的本地 mod 用文件名。
    /// </summary>
    private static string TitleKey(ModEntry entry) =>
        entry.MarketSlug is { Length: > 0 } slug ? slug : entry.FileName;

    /// <summary>列表里显示的标题：有译名用译名，否则用 modinfo.json 里的原名。</summary>
    private string TitleOf(ModEntry entry) =>
        _services.LocalModTitles.GetTitle(TitleKey(entry), entry.DisplayName);

    private string L(string key) => _services.Localization[key];

    private void ApplyLocalization()
    {
        TitleText.Text = L("mods.title");
        DropHintText.Text = L("mods.dropHint");
        OpenFolderButton.Content = L("mods.openFolder");
        RefreshButton.Content = L("common.refresh");
        EmptyTitle.Text = L("mods.empty");
        EmptyHint.Text = L("mods.emptyHint");
    }

    private void Reload()
    {
        _all = _services.Mods?.Scan().ToList() ?? new List<ModEntry>();
        RebuildList();
    }

    private void RebuildList()
    {
        var filtered = string.IsNullOrWhiteSpace(_query)
            ? _all
            : _all.Where(m =>
                    TitleOf(m).Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                    m.DisplayName.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                    (m.Info?.Author.Contains(_query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    m.FileName.Contains(_query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var enabled = _all.Count(m => m.IsEnabled);
        CountText.Text = $"{enabled} / {_all.Count}";

        ModsList.Items.Clear();

        if (filtered.Count == 0)
        {
            ModsList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = _all.Count == 0 ? L("mods.empty") : L("market.noResults");
            EmptyHint.Text = L("mods.emptyHint");
            return;
        }

        ModsList.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        foreach (var entry in filtered)
        {
            ModsList.Items.Add(BuildRow(entry));
        }
    }

    private UIElement BuildRow(ModEntry entry)
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Padding = new Thickness(16, 14, 16, 14),
            Opacity = entry.IsEnabled ? 1.0 : 0.62,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标
        var iconBorder = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["SurfaceAltBrush"],
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var iconImage = TryLoadIcon(entry);
        if (iconImage is not null)
        {
            iconBorder.Child = iconImage;
        }
        else
        {
            iconBorder.Child = new TextBlock
            {
                Text = "\uE8B7",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        // 文本
        var textStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = TitleOf(entry),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420,
        });

        if (entry.Info?.Version is { Length: > 0 } version)
        {
            titleRow.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["SurfaceAltBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = version,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                },
            });
        }

        textStack.Children.Add(titleRow);

        var subtitle = entry.ParseError is { Length: > 0 } error
            ? $"⚠ {error}"
            : string.Join(" · ", new[]
            {
                entry.Info?.Author is { Length: > 0 } a ? a : L("common.unknown"),
                FormatSize(entry.FileSize),
                entry.LastWriteTime.LocalDateTime.ToString("yyyy-MM-dd"),
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

        textStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources[
                entry.ParseError is { Length: > 0 } ? "DangerBrush" : "TextMutedBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        // 操作
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggle = new ToggleSwitch
        {
            IsOn = entry.IsEnabled,
            Style = (Style)Application.Current.Resources["CompactToggleSwitchStyle"],
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            Margin = new Thickness(0, 0, 4, 0),
        };

        toggle.Toggled += (_, _) =>
        {
            var result = _services.Mods!.SetEnabled(entry, toggle.IsOn);
            if (result.Success)
            {
                _services.Logger.Info("Mods", $"{entry.DisplayName} → {(toggle.IsOn ? "启用" : "禁用")}");
                Reload();
            }
            else
            {
                _services.Logger.Warn("Mods", result.Message);
                toggle.IsOn = entry.IsEnabled;
            }
        };

        actions.Children.Add(toggle);

        var uninstall = new Button
        {
            Style = (Style)Application.Current.Resources["IconButtonStyle"],
            Content = "\uE74D",
            Tag = entry,
        };
        ToolTipService.SetToolTip(uninstall, L("common.uninstall"));
        uninstall.Click += OnUninstallClick;
        actions.Children.Add(uninstall);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.Child = grid;

        // 点卡片看详情。ToggleSwitch 和卸载按钮会自己消化点击事件，
        // 不会冒泡到这里，所以不用担心误触。
        card.Tapped += async (_, _) => await ShowDetailsAsync(entry);
        ToolTipService.SetToolTip(card, L("mods.clickForDetails"));

        return card;
    }

    private async Task ShowDetailsAsync(ModEntry entry)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var model = ModDetailModel.FromLocal(
            entry,
            entry.IsEnabled ? L("common.enabled") : L("common.disabled"),
            TitleOf(entry));

        try
        {
            await ModDetailDialog.ShowAsync(_services, model, XamlRoot, view =>
            {
                // 本地模组：给一个「在资源管理器里定位」的按钮，比重新点开目录再找文件方便。
                var reveal = new Button
                {
                    Content = L("mods.revealFile"),
                    Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                reveal.Click += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{entry.FilePath}\"",
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        _services.Logger.Warn("Mods", $"打开文件位置失败：{ex.Message}");
                    }
                };

                view.Actions.Children.Add(reveal);
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Mods", $"打开 {entry.DisplayName} 详情失败", ex);
        }
    }

    private static Image? TryLoadIcon(ModEntry entry)
    {
        try
        {
            var bytes = RmodReader.TryReadImage(entry.FilePath, entry.Info?.Icon);
            if (bytes is null)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.SetSource(stream.AsRandomAccessStream());

            return new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModEntry entry })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L("common.uninstall"),
            Content = entry.DisplayName,
            PrimaryButtonText = L("common.confirm"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = _services.Mods!.Uninstall(entry);
        _services.Logger.Info("Mods", result.Message);
        Reload();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text?.Trim() ?? string.Empty;
        RebuildList();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = _services.Mods?.ModsFolder;
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
        {
            UseShellExecute = true,
        });
    }

    // ===== 拖入安装 =====

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = L("common.install");
        e.DragUIOverride.IsCaptionVisible = true;
        DropHint.BorderBrush = (Brush)Application.Current.Resources["AccentBorderBrush"];
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.BorderBrush = (Brush)Application.Current.Resources["BorderBrush"];

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var installed = 0;

            foreach (var item in items.OfType<StorageFile>())
            {
                if (!item.Name.EndsWith(Core.Configuration.RmlPaths.ModExtension,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = _services.Mods!.Install(item.Path);
                _services.Logger.Info("Mods", result.Message);

                if (result.Success)
                {
                    installed++;
                }
            }

            if (installed > 0)
            {
                Reload();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }
}
