using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RmlClient.App.Services;

namespace RmlClient.App.Controls;

/// <summary>
/// 把 <see cref="ModDetailView"/> 装进 ContentDialog 弹出来。
///
/// 市场和本地模组页都调这里，区别只在 <c>configure</c> 里塞什么按钮。
/// </summary>
public static class ModDetailDialog
{
    /// <param name="xamlRoot">调用页面的 XamlRoot —— ContentDialog 在桌面端必须显式指定。</param>
    /// <param name="configure">可选：给详情面板底部塞操作按钮。</param>
    public static async Task ShowAsync(
        AppServices services,
        ModDetailModel model,
        XamlRoot xamlRoot,
        Action<ModDetailView>? configure = null)
    {
        var view = new ModDetailView();
        view.Initialize(services, model);
        configure?.Invoke(view);

        // 面板本身已经是 ScrollViewer，这里只约束宽高，避免在 4K 屏上铺满全屏。
        var host = new Grid
        {
            MinWidth = 520,
            MaxWidth = 620,
            MaxHeight = 540,
            Children = { view },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Content = host,
            CloseButtonText = services.Localization["common.close"],
            DefaultButton = ContentDialogButton.Close,
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
        };

        // 先把对话框弹出来，再在后台开始翻译 —— 否则网络慢的时候用户会看到
        // 一个卡住的空白窗口，等 20 秒才出现内容。
        var showTask = dialog.ShowAsync().AsTask();

        try
        {
            await view.AutoTranslateAsync();
        }
        catch (Exception ex)
        {
            // 翻译是锦上添花，失败不该影响看详情。
            services.Logger.Warn("Translate", $"自动翻译失败：{ex.Message}");
        }

        await showTask;
    }
}
