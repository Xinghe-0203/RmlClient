using Windows.UI.ViewManagement;

namespace RmlClient.App.Services;

/// <summary>
/// 系统「减少动画」设置。
///
/// 设计文档要求所有动效遵循 <c>UISettings.AnimationsEnabled</c>：
/// 用户在辅助功能里关掉动画后，客户端不应再播放缩放/淡入淡出，
/// 否则对前庭敏感的用户很不友好。
/// </summary>
public static class MotionSettings
{
    private static bool? _cached;

    /// <summary>系统是否允许动画。读取失败时按「允许」处理。</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            if (_cached is { } cached)
            {
                return cached;
            }

            try
            {
                var settings = new UISettings();
                _cached = settings.AnimationsEnabled;
            }
            catch (Exception)
            {
                _cached = true;
            }

            return _cached.Value;
        }
    }

    /// <summary>用户在系统设置里改过之后可以调这个让下次重新读取。</summary>
    public static void Invalidate() => _cached = null;
}
