using System.Globalization;

namespace RmlClient.Core.Translation;

/// <summary>
/// 一个翻译目标语言。三家免费接口的语言代码不一致，这里统一映射。
/// </summary>
/// <param name="Code">本客户端内部使用的稳定标识（BCP-47 风格）。</param>
/// <param name="BingCode">Bing 翻译用的代码，如 <c>zh-Hans</c>。</param>
/// <param name="MyMemoryCode">MyMemory 用的代码，如 <c>zh-CN</c>。</param>
/// <param name="NativeName">该语言自己的写法，用于下拉框。</param>
/// <param name="EnglishName">英文名，用于日志与回退。</param>
public sealed record TranslationLanguage(
    string Code,
    string BingCode,
    string MyMemoryCode,
    string NativeName,
    string EnglishName)
{
    /// <summary>下拉框显示文本，例如「简体中文」。</summary>
    public string DisplayName => NativeName;

    /// <summary>
    /// 可选的翻译目标语言。只收录 mod 社区里实际会出现的语种，
    /// 列表太长反而不好选。
    /// </summary>
    public static readonly IReadOnlyList<TranslationLanguage> All = new[]
    {
        new TranslationLanguage("zh-Hans", "zh-Hans", "zh-CN", "简体中文", "Chinese (Simplified)"),
        new TranslationLanguage("zh-Hant", "zh-Hant", "zh-TW", "繁體中文", "Chinese (Traditional)"),
        new TranslationLanguage("en", "en", "en", "English", "English"),
        new TranslationLanguage("ja", "ja", "ja", "日本語", "Japanese"),
        new TranslationLanguage("ko", "ko", "ko", "한국어", "Korean"),
        new TranslationLanguage("ru", "ru", "ru", "Русский", "Russian"),
        new TranslationLanguage("de", "de", "de", "Deutsch", "German"),
        new TranslationLanguage("fr", "fr", "fr", "Français", "French"),
        new TranslationLanguage("es", "es", "es", "Español", "Spanish"),
        new TranslationLanguage("pt", "pt", "pt", "Português", "Portuguese"),
        new TranslationLanguage("it", "it", "it", "Italiano", "Italian"),
        new TranslationLanguage("pl", "pl", "pl", "Polski", "Polish"),
        new TranslationLanguage("tr", "tr", "tr", "Türkçe", "Turkish"),
    };

    /// <summary>按内部代码查找，找不到返回 null。</summary>
    public static TranslationLanguage? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 把系统语言（<see cref="CultureInfo.InstalledUICulture"/>）映射到本表里的语言。
    ///
    /// Windows 上简体中文可能是 <c>zh-CN</c>、<c>zh-SG</c> 或 <c>zh-Hans-CN</c>，
    /// 繁体可能是 <c>zh-TW</c>、<c>zh-HK</c>、<c>zh-MO</c>，这里按书写系统区分。
    /// </summary>
    public static TranslationLanguage FromCulture(CultureInfo culture)
    {
        var name = culture.Name;

        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var traditional = name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                              || name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                              || name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                              || name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);

            return Find(traditional ? "zh-Hant" : "zh-Hans")!;
        }

        var twoLetter = culture.TwoLetterISOLanguageName;
        return Find(twoLetter) ?? Find("en")!;
    }

    /// <summary>系统默认的翻译目标语言。</summary>
    public static TranslationLanguage SystemDefault()
    {
        try
        {
            return FromCulture(CultureInfo.InstalledUICulture);
        }
        catch (Exception)
        {
            // 极端情况下（某些精简系统）拿不到区域信息，回退英文。
            return Find("en")!;
        }
    }
}
