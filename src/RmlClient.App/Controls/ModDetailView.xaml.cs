using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RmlClient.App.Services;
using RmlClient.Core.Market;
using RmlClient.Core.Mods;
using RmlClient.Core.Translation;
using Windows.Storage.Streams;

namespace RmlClient.App.Controls;

/// <summary>
/// 模组详情面板：描述 + 更新日志 + 翻译。
///
/// 市场里的 mod 与本地 .rmod 都走这里 —— 两者字段名不同（市场是 JSON 的
/// <c>description</c>/<c>mod-versions</c>，本地是 <c>modinfo.json</c> 的
/// <c>description</c>/<c>version</c>），所以在 <see cref="ModDetailModel"/> 里先归一化。
///
/// 翻译是「按需 + 可切换」：
///   - 打开时若 <c>autoTranslate</c> 为真，自动翻成配置里的目标语言（默认跟随系统）
///   - 顶部下拉框随时换目标语言，换完立即重译（已翻过的走缓存，秒回）
///   - 状态栏显示「由 Bing 翻译 / 已缓存 / 翻译不可用，显示原文」
/// </summary>
public sealed partial class ModDetailView : UserControl
{
    private AppServices _services = null!;
    private ModDetailModel _model = null!;
    private TranslationLanguage _target = TranslationLanguage.SystemDefault();

    private string _originalDescription = string.Empty;
    private string _translatedDescription = string.Empty;

    private readonly Dictionary<string, (string Original, string Translated)> _changelogs = new();
    private string _currentVersionKey = string.Empty;

    private bool _loadingLanguages;

    public ModDetailView()
    {
        InitializeComponent();
    }

    /// <summary>调用方在 ShowAsync 之前把服务注入进来。</summary>
    public void Initialize(AppServices services, ModDetailModel model)
    {
        _services = services;
        _model = model;

        TitleText.Text = model.Title;
        SubtitleText.Text = model.Subtitle;
        TranslateLabel.Text = services.Localization["details.translateTo"];
        DescriptionHeader.Text = services.Localization["details.description"];
        ChangelogHeader.Text = services.Localization["details.changelog"];

        _originalDescription = model.Description;
        DescriptionText.Text = string.IsNullOrWhiteSpace(_originalDescription)
            ? services.Localization["details.noDescription"]
            : _originalDescription;

        LoadIcon();
        BuildVersionList();
        BuildLanguageList();
    }

    /// <summary>底部操作区，调用方往里塞「安装 / 回滚 / 打开目录」之类的按钮。</summary>
    public StackPanel Actions => ActionHost;

    /// <summary>当前在版本下拉框里选中的那一版；没有版本信息时为 null。</summary>
    public ModVersionEntry? SelectedVersion =>
        VersionCombo.SelectedItem is ComboBoxItem { Tag: ModVersionEntry version } ? version : null;

    private void LoadIcon()
    {
        var image = _model.LocalIconPath is { Length: > 0 } localPath
            ? TryLoadLocalIcon(localPath, _model.LocalIconEntry)
            : TryLoadRemoteIcon(_model.IconUrl);

        if (image is not null)
        {
            IconHost.Child = image;
        }
    }

    private static Image? TryLoadRemoteIcon(string? url)
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
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Image? TryLoadLocalIcon(string rmodPath, string? entryName)
    {
        try
        {
            var bytes = RmodReader.TryReadImage(rmodPath, entryName);

            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }

            stream.Seek(0);

            var bitmap = new BitmapImage();
            bitmap.SetSource(stream);

            return new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void BuildVersionList()
    {
        VersionCombo.Items.Clear();

        if (_model.Versions.Count == 0)
        {
            ChangelogSection.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var version in _model.Versions)
        {
            VersionCombo.Items.Add(new ComboBoxItem
            {
                Content = version.Label,
                Tag = version,
            });
        }

        VersionCombo.SelectedIndex = 0;
    }

    private void BuildLanguageList()
    {
        _loadingLanguages = true;

        LanguageCombo.Items.Clear();

        foreach (var language in TranslationLanguage.All)
        {
            LanguageCombo.Items.Add(new ComboBoxItem
            {
                Content = language.NativeName,
                Tag = language,
            });
        }

        // 配置里的目标语言为空表示「跟随系统」。
        var configured = TranslationLanguage.Find(_services.Config.Client.TranslateTarget);
        _target = configured ?? TranslationLanguage.SystemDefault();

        LanguageCombo.SelectedIndex = Math.Max(0, TranslationLanguage.All
            .ToList()
            .FindIndex(l => l.Code == _target.Code));

        _loadingLanguages = false;
    }

    /// <summary>打开详情后调用一次：按配置决定是否自动翻译。</summary>
    public async Task AutoTranslateAsync()
    {
        if (!_services.Config.Client.AutoTranslate)
        {
            return;
        }

        await TranslateAllAsync();
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLanguages ||
            LanguageCombo.SelectedItem is not ComboBoxItem { Tag: TranslationLanguage language })
        {
            return;
        }

        if (language.Code == _target.Code)
        {
            return;
        }

        _target = language;

        // 记住选择，下次打开详情直接用它。
        _services.Config.Client.TranslateTarget = language.Code;
        _services.SaveConfig();

        // 换了语言，之前缓存的译文不再适用，清空重翻。
        _changelogs.Clear();
        _translatedDescription = string.Empty;

        await TranslateAllAsync();
    }

    private async void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionCombo.SelectedItem is not ComboBoxItem { Tag: ModVersionEntry version })
        {
            return;
        }

        _currentVersionKey = version.Key;

        var changelog = version.Changelog;

        ChangelogText.Text = string.IsNullOrWhiteSpace(changelog)
            ? "—"
            : changelog;

        // 已经翻过这一版就直接用缓存，不重新请求。
        if (_changelogs.TryGetValue(version.Key, out var cached) &&
            !string.IsNullOrEmpty(cached.Translated))
        {
            ChangelogText.Text = cached.Translated;
            return;
        }

        if (string.IsNullOrWhiteSpace(changelog))
        {
            return;
        }

        await TranslateChangelogAsync(version);
    }

    private async Task TranslateAllAsync()
    {
        SetStatus(_services.Localization["details.translating"]);

        await TranslateDescriptionAsync();
        await TranslateCurrentChangelogAsync();
    }

    private async Task TranslateDescriptionAsync()
    {
        if (string.IsNullOrWhiteSpace(_originalDescription))
        {
            return;
        }

        var result = await _services.Translation.TranslateAsync(_originalDescription, _target);

        if (result.Skipped)
        {
            SetStatus(_services.Localization["details.skipped"]);
            return;
        }

        if (!result.Success)
        {
            SetStatus(_services.Localization["details.translateFailed"]);
            _services.Logger.Warn("Translate", $"描述翻译失败：{result.Error}");
            return;
        }

        _translatedDescription = result.Text;

        DescriptionText.Text = _translatedDescription;

        SetStatus(result.FromCache
            ? $"{string.Format(_services.Localization["details.translatedBy"], result.Provider)} · {_services.Localization["details.cached"]}"
            : string.Format(_services.Localization["details.translatedBy"], result.Provider));
    }

    private async Task TranslateCurrentChangelogAsync()
    {
        if (VersionCombo.SelectedItem is not ComboBoxItem { Tag: ModVersionEntry version })
        {
            return;
        }

        await TranslateChangelogAsync(version);
    }

    private async Task TranslateChangelogAsync(ModVersionEntry version)
    {
        if (string.IsNullOrWhiteSpace(version.Changelog))
        {
            return;
        }

        var result = await _services.Translation.TranslateAsync(version.Changelog, _target);

        if (result.Skipped)
        {
            _changelogs[version.Key] = (version.Changelog, version.Changelog);
            return;
        }

        if (!result.Success)
        {
            _services.Logger.Warn("Translate", $"{version.Label} 更新日志翻译失败：{result.Error}");
            return;
        }

        _changelogs[version.Key] = (version.Changelog, result.Text);

        // 只有当前选中的那一版才刷新界面（用户可能已经切到别的版本了）。
        if (_currentVersionKey == version.Key)
        {
            ChangelogText.Text = result.Text;
        }
    }

    private void SetStatus(string text) => TranslateStatus.Text = text;
}
