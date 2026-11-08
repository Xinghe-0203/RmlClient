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

    /// <summary>slug（或文件名）→ 更新检查结果，用于在卡片上画「有新版本」角标。</summary>
    private readonly Dictionary<string, ModUpdateResult> _updates = new(StringComparer.OrdinalIgnoreCase);

    private bool _checkingUpdates;

    /// <summary>当前检出的冲突，供提示条与详情弹窗使用。</summary>
    private IReadOnlyList<ModConflictDetector.ModConflict> _conflicts =
        Array.Empty<ModConflictDetector.ModConflict>();

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
        CheckUpdatesButton.Content = L("mods.checkUpdates");
        UpdateAllButton.Content = L("mods.updateAll");
        ConflictDetailsButton.Content = L("mods.conflictDetails");
        EmptyTitle.Text = L("mods.empty");
        EmptyHint.Text = L("mods.emptyHint");
        UpdateUpdateBanner();
        UpdateConflictBanner();
    }

    /// <summary>
    /// 更新提示条：只有查出有新版本时才显示。
    /// 检查失败（例如 mod 已下架）不算「有更新」，不打扰用户。
    /// </summary>
    private void UpdateUpdateBanner()
    {
        var pending = _updates.Values.Count(r => r.UpdateAvailable);

        if (pending == 0)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBanner.Visibility = Visibility.Visible;
        UpdateBannerText.Text = string.Format(L("mods.updateBanner"), pending);
    }

    private void Reload()
    {
        _all = _services.Mods?.Scan().ToList() ?? new List<ModEntry>();
        RefreshConflicts();
        RebuildList();
    }

    /// <summary>
    /// 重新检测冲突。检测要读每个包的文件条目，20 个 mod 约几十毫秒，
    /// 放在 UI 线程上可以接受；不异步是为了让 RebuildList 拿到的状态一定是最新的。
    /// </summary>
    private void RefreshConflicts()
    {
        try
        {
            _conflicts = ModConflictDetector.Detect(_all);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _conflicts = Array.Empty<ModConflictDetector.ModConflict>();
            _services.Logger.Warn("Mods", $"冲突检测失败：{ex.Message}");
        }
    }

    /// <summary>冲突提示条：只有检出冲突时才显示。</summary>
    private void UpdateConflictBanner()
    {
        if (_conflicts.Count == 0)
        {
            ConflictBanner.Visibility = Visibility.Collapsed;
            return;
        }

        ConflictBanner.Visibility = Visibility.Visible;
        ConflictBannerText.Text = string.Format(L("mods.conflictBanner"), _conflicts.Count);
    }

    private async void OnConflictDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_conflicts.Count == 0)
        {
            return;
        }

        var lines = _conflicts.Select(c => c.Kind == ModConflictDetector.ModConflictKind.DuplicateName
            ? string.Format(L("mods.conflictDuplicate"), c.Key, c.ModNames)
            : string.Format(L("mods.conflictFiles"), c.Key, c.ModNames));

        await ShowMessageAsync(L("mods.conflictTitle"), string.Join(Environment.NewLine + Environment.NewLine, lines));
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

        UpdateUpdateBanner();
        UpdateConflictBanner();

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

        // 有新版本时，在版本号后面挂一个绿色角标，写清新版本号。
        if (_updates.TryGetValue(TitleKey(entry), out var update) && update.UpdateAvailable)
        {
            titleRow.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["SuccessBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = string.Format(L("mods.newVersion"), update.LatestVersion),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
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

        // 只有查出新版本的 mod 才给「更新」按钮，避免每行都多一个按钮。
        if (_updates.TryGetValue(TitleKey(entry), out var rowUpdate) && rowUpdate.UpdateAvailable)
        {
            var updateButton = new Button
            {
                Content = L("mods.updateOne"),
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = entry,
            };

            ToolTipService.SetToolTip(
                updateButton,
                string.Format(L("mods.newVersion"), rowUpdate.LatestVersion));

            updateButton.Click += OnUpdateOneClick;
            actions.Children.Add(updateButton);
        }

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

    // ===== 更新检查 =====

    /// <summary>
    /// 逐个查 <c>modinfo.json</c> 里的 <c>updateUrl</c>（就是
    /// <c>raftmodding.com/api/v1/mods/&lt;slug&gt;/version.txt</c>），
    /// 和本地版本比出「有新版本」的那些。
    ///
    /// 这是显式动作：只在用户点「检查更新」时联网，不做后台轮询 ——
    /// 官方站点对密集请求不友好，而且用户没要求就不该偷偷发请求。
    /// </summary>
    private async void OnUpdateOneClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModEntry entry } button)
        {
            return;
        }

        var market = _services.Market;

        // 市场缓存只在市场页加载过；这里首次更新时补一次，否则 FindBySlug 查不到。
        market?.LoadFromDisk();

        var target = market?.FindBySlug(entry.MarketSlug);

        if (market is null || target is null)
        {
            await ShowMessageAsync(L("mods.updateOne"), L("mods.noUpdatable"));
            return;
        }

        button.IsEnabled = false;

        try
        {
            var wasEnabled = entry.IsEnabled;
            var install = await market.InstallAsync(target, target.LatestVersion);

            if (!install.Success)
            {
                _services.Logger.Warn("ModUpdate", $"{entry.DisplayName} 更新失败：{install.Message}");
                await ShowMessageAsync(L("mods.updateOne"), install.Message);
                return;
            }

            // 更新会覆盖同名文件，禁用状态要还原，否则更新后 mod 被悄悄启用。
            if (!wasEnabled)
            {
                var updated = _services.Mods?.Scan()
                    .FirstOrDefault(m => string.Equals(m.MarketSlug, entry.MarketSlug, StringComparison.OrdinalIgnoreCase));

                if (updated is not null && updated.IsEnabled)
                {
                    _services.Mods?.SetEnabled(updated, false);
                }
            }

            _updates.Remove(TitleKey(entry));
            _services.Logger.Info("ModUpdate", $"{entry.DisplayName} 已更新到 {target.LatestVersion?.Version}。");
            Reload();
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("ModUpdate", $"{entry.DisplayName} 更新失败：{ex.Message}");
            await ShowMessageAsync(L("mods.updateOne"), ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdates || _services.Mods is null)
        {
            return;
        }

        _checkingUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        UpdateAllButton.IsEnabled = false;

        var progress = new Progress<int>(done =>
        {
            // Progress<T> 是异步投递的，最后一次 Report 可能在检查结束后才到，
            // 那会把 RebuildList 写好的计数又覆盖成「正在检查…」，所以这里要守一道。
            if (!_checkingUpdates)
            {
                return;
            }

            // 总数取当前有 updateUrl 的条目，和 ModUpdateChecker 内部过滤条件一致。
            var total = _all.Count(m => m.Info?.UpdateUrl is { Length: > 0 });
            CountText.Text = string.Format(L("mods.checkingUpdates"), done, total);
        });

        try
        {
            var checker = new ModUpdateChecker(logger: _services.Logger);
            var results = await checker.CheckAsync(_all, progress);

            _updates.Clear();
            foreach (var result in results)
            {
                _updates[TitleKey(result.Mod)] = result;
            }

            var pending = results.Count(r => r.UpdateAvailable);
            var failed = results.Count(r => r.Error is not null);

            _services.Logger.Info(
                "ModUpdate",
                $"检查完成：{results.Count} 个可查，{pending} 个有更新，{failed} 个查询失败。");

            // 这里必须显式把进度文字改回来 —— RebuildList 会重设 CountText，
            // 但空列表/过滤分支可能提前 return，进度文字就会一直留在界面上。
            RebuildList();

            if (pending == 0)
            {
                await ShowMessageAsync(L("mods.checkUpdates"), L("mods.upToDate"));
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("ModUpdate", $"检查模组更新失败：{ex.Message}");
            await ShowMessageAsync(L("mods.checkUpdates"), ex.Message);
        }
        finally
        {
            _checkingUpdates = false;
            CheckUpdatesButton.IsEnabled = true;
            UpdateAllButton.IsEnabled = true;
            RebuildList();
        }    }

    /// <summary>把所有有新版本的 mod 依次更新（走市场下载，保留原启用状态）。</summary>
    private async void OnUpdateAllClick(object sender, RoutedEventArgs e)
    {
        var pending = _updates.Values.Where(r => r.UpdateAvailable).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var market = _services.Market;
        if (market is null)
        {
            await ShowMessageAsync(L("mods.updateAll"), L("mods.noUpdatable"));
            return;
        }

        // 市场缓存只在市场页加载过；这里首次更新时补一次，否则 FindBySlug 查不到。
        market.LoadFromDisk();

        if (!market.HasData)
        {
            await ShowMessageAsync(L("mods.updateAll"), L("mods.noUpdatable"));
            return;
        }

        UpdateAllButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;

        var ok = 0;
        var failed = 0;

        try
        {
            foreach (var result in pending)
            {
                var slug = result.Mod.MarketSlug;
                var target = market.FindBySlug(slug);

                if (target is null)
                {
                    // 市场缓存里没有（未刷新过或已下架），跳过而不是报错打断整批。
                    _services.Logger.Warn("ModUpdate", $"{result.Mod.DisplayName} 在市场里找不到，跳过。");
                    failed++;
                    continue;
                }

                var wasEnabled = result.Mod.IsEnabled;
                var install = await market.InstallAsync(target, target.LatestVersion);

                if (install.Success)
                {
                    ok++;
                    _services.Logger.Info("ModUpdate", $"{result.Mod.DisplayName} 已更新到 {target.LatestVersion?.Version}。");
                }
                else
                {
                    failed++;
                    _services.Logger.Warn("ModUpdate", $"{result.Mod.DisplayName} 更新失败：{install.Message}");
                }

                // Install 会覆盖同名文件，禁用状态需要还原，否则更新后 mod 会被悄悄启用。
                if (!wasEnabled)
                {
                    var updated = _services.Mods?.Scan()
                        .FirstOrDefault(m => string.Equals(m.MarketSlug, slug, StringComparison.OrdinalIgnoreCase));

                    if (updated is not null && updated.IsEnabled)
                    {
                        _services.Mods?.SetEnabled(updated, false);
                    }
                }
            }

            _updates.Clear();
            Reload();

            var summary = failed == 0
                ? string.Format(L("mods.installed"), ok)
                : $"{string.Format(L("mods.installed"), ok)} / {failed} ✗";

            await ShowMessageAsync(L("mods.updateAll"), summary);
        }
        finally
        {
            UpdateAllButton.IsEnabled = true;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot is null)
        {
            return;
        }

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
            DefaultButton = ContentDialogButton.Close,
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("Mods", $"对话框显示失败：{ex.Message}");
        }
    }

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
