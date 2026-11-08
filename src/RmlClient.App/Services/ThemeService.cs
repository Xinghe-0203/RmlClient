using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RmlClient.App.Services;

/// <summary>
/// 明暗主题与强调色。
///
/// 全项目 XAML 用的是 <c>{StaticResource xxxBrush}</c>，它解析出来的是
/// **同一个画刷实例**，所以运行时改画刷的 <c>Color</c>（渐变改 GradientStop）
/// 就能让所有引用处一起变色 —— 不需要把 90 多处改成 ThemeResource，
/// 也不需要重建 ResourceDictionary。
///
/// 唯一改不到的是模板里的字面量颜色（目前只剩强调按钮上的白字、
/// 次级按钮悬停描边、市场页遮罩底色），深色下也仍然协调。
/// </summary>
public static class ThemeService
{
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>可选的强调色：键 → (起始色, 结束色)。</summary>
    private static readonly Dictionary<string, (string Start, string End)> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cyan"] = ("#06B6D4", "#0EA5E9"),
        ["violet"] = ("#8B5CF6", "#6366F1"),
        ["green"] = ("#10B981", "#059669"),
        ["amber"] = ("#F59E0B", "#EA580C"),
        ["rose"] = ("#F43F5E", "#E11D48"),
    };

    public static IReadOnlyList<string> AccentKeys { get; } = new[] { "cyan", "violet", "green", "amber", "rose" };

    public static bool IsDark(string? theme) =>
        string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase);

    /// <summary>取某个强调色的起止色，供设置页画色块预览。</summary>
    public static (Color Start, Color End) Preview(string? accent)
    {
        var (startHex, endHex) = Accents.TryGetValue(accent ?? string.Empty, out var pair)
            ? pair
            : Accents["cyan"];

        return (Parse(startHex), Parse(endHex));
    }

    /// <summary>把主题与强调色应用到当前资源上。</summary>
    public static void Apply(string? theme, string? accent)
    {
        var dark = IsDark(theme);

        // ---- 基础色板 ----
        if (dark)
        {
            SetBrush("BackgroundBrush", "#0B0F14");
            SetBrush("SurfaceBrush", "#121821");
            SetBrush("SurfaceAltBrush", "#161E2A");
            SetBrush("BorderBrush", "#1E2836");
            SetBrush("TextPrimaryBrush", "#E6EDF7");
            SetBrush("TextSecondaryBrush", "#94A3B8");
            SetBrush("TextMutedBrush", "#64748B");
            SetBrush("HoverBrush", "#1A2230");
            SetBrush("PressedBrush", "#0F1620");
            SetBrush("SelectedBrush", "#16202C");
            SetBrush("SidebarBrush", "#CC121821");

            SetGradient("HeroBrush", ("#141F2E", 0), ("#101720", 0.55), ("#121821", 1));
        }
        else
        {
            SetBrush("BackgroundBrush", "#F5F7FA");
            SetBrush("SurfaceBrush", "#FFFFFF");
            SetBrush("SurfaceAltBrush", "#F1F5F9");
            SetBrush("BorderBrush", "#E2E8F0");
            SetBrush("TextPrimaryBrush", "#0F172A");
            SetBrush("TextSecondaryBrush", "#475569");
            SetBrush("TextMutedBrush", "#94A3B8");
            SetBrush("HoverBrush", "#F1F5F9");
            SetBrush("PressedBrush", "#E2E8F0");
            SetBrush("SelectedBrush", "#E0F2FE");
            SetBrush("SidebarBrush", "#D9FFFFFF");

            SetGradient("HeroBrush", ("#ECFEFF", 0), ("#F0F9FF", 0.55), ("#FFFFFF", 1));
        }

        // ---- 强调色 ----
        var (startHex, endHex) = Accents.TryGetValue(accent ?? string.Empty, out var pair)
            ? pair
            : Accents["cyan"];

        var start = Parse(startHex);
        var end = Parse(endHex);

        SetGradient("AccentBrush", (startHex, 0), (endHex, 1));
        SetGradient("AccentHorizontalBrush", (startHex, 0), (endHex, 1));
        SetGradient("AccentBorderBrush", (Argb(0x80, start), 0), (Argb(0x33, end), 1));
        SetGradient("AccentGlowBrush", (Argb(0x40, start), 0), (Argb(0x00, end), 1));

        // WinUI 控件模板取这些键：开关、复选框、焦点框、进度条。
        SetColor("SystemAccentColor", start);
        SetColor("SystemAccentColorLight1", Lighten(start, 0.18));
        SetColor("SystemAccentColorLight2", Lighten(start, 0.32));
        SetColor("SystemAccentColorLight3", Lighten(start, 0.46));
        SetColor("SystemAccentColorDark1", Darken(start, 0.12));
        SetColor("SystemAccentColorDark2", Darken(start, 0.24));
        SetColor("SystemAccentColorDark3", Darken(start, 0.36));

        SetBrush("ToggleSwitchFillOn", start);
        SetBrush("ToggleSwitchFillOnPointerOver", end);
        SetBrush("ToggleSwitchFillOnPressed", Darken(start, 0.12));
        SetBrush("ToggleSwitchStrokeOn", start);
        SetBrush("ToggleSwitchStrokeOnPointerOver", end);
        SetBrush("ToggleSwitchStrokeOnPressed", Darken(start, 0.12));

        SetBrush("CheckBoxCheckBackgroundFillChecked", start);
        SetBrush("CheckBoxCheckBackgroundFillCheckedPointerOver", end);
        SetBrush("CheckBoxCheckBackgroundFillCheckedPressed", Darken(start, 0.12));
        SetBrush("CheckBoxCheckBackgroundStrokeChecked", start);
        SetBrush("CheckBoxCheckBackgroundStrokeCheckedPointerOver", end);
    }

    /// <summary>把主题作用到窗口根元素（ElementTheme 会带动 WinUI 控件模板换色）。</summary>
    public static void ApplyToWindow(Window? window, string? theme)
    {
        if (window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = IsDark(theme) ? ElementTheme.Dark : ElementTheme.Light;
        }
    }

    // ---------- 底层改写 ----------

    private static void SetBrush(string key, string hex) => SetBrush(key, Parse(hex));

    private static void SetBrush(string key, Color color)
    {
        if (Lookup(key) is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private static void SetColor(string key, Color color)
    {
        var dictionary = FindDictionary(key);
        if (dictionary is not null)
        {
            dictionary[key] = color;
        }
    }

    private static void SetGradient(string key, params (string Hex, double Offset)[] stops) =>
        SetGradient(key, stops.Select(s => (Parse(s.Hex), s.Offset)).ToArray());

    private static void SetGradient(string key, params (Color Color, double Offset)[] stops)
    {
        if (Lookup(key) is not LinearGradientBrush brush)
        {
            return;
        }

        for (var i = 0; i < stops.Length && i < brush.GradientStops.Count; i++)
        {
            brush.GradientStops[i].Color = stops[i].Color;
        }
    }

    /// <summary>
    /// 递归查找资源（含 MergedDictionaries）。
    /// <c>Application.Current.Resources.TryGetValue</c> 在部分版本上不穿合并字典，
    /// 这里自己走一遍更稳。
    /// </summary>
    private static object? Lookup(string key)
    {
        var dictionary = FindDictionary(key);
        return dictionary?[key];
    }

    private static ResourceDictionary? FindDictionary(string key) =>
        FindDictionary(Application.Current.Resources, key);

    private static ResourceDictionary? FindDictionary(ResourceDictionary dictionary, string key)
    {
        if (dictionary.ContainsKey(key))
        {
            return dictionary;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            var found = FindDictionary(merged, key);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // ---------- 颜色工具 ----------

    private static Color Parse(string hex)
    {
        var text = hex.TrimStart('#');

        return text.Length switch
        {
            6 => Color.FromArgb(
                0xFF,
                Convert.ToByte(text[..2], 16),
                Convert.ToByte(text.Substring(2, 2), 16),
                Convert.ToByte(text.Substring(4, 2), 16)),
            8 => Color.FromArgb(
                Convert.ToByte(text[..2], 16),
                Convert.ToByte(text.Substring(2, 2), 16),
                Convert.ToByte(text.Substring(4, 2), 16),
                Convert.ToByte(text.Substring(6, 2), 16)),
            _ => Color.FromArgb(0xFF, 0x06, 0xB6, 0xD4),
        };
    }

    private static Color Argb(byte alpha, Color color) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Lighten(Color color, double amount) => Color.FromArgb(
        color.A,
        (byte)Math.Clamp(color.R + (255 - color.R) * amount, 0, 255),
        (byte)Math.Clamp(color.G + (255 - color.G) * amount, 0, 255),
        (byte)Math.Clamp(color.B + (255 - color.B) * amount, 0, 255));

    private static Color Darken(Color color, double amount) => Color.FromArgb(
        color.A,
        (byte)Math.Clamp(color.R * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.G * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.B * (1 - amount), 0, 255));
}
