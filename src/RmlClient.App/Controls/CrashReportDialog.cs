using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RmlClient.App.Services;
using RmlClient.Core.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace RmlClient.App.Controls;

/// <summary>
/// 崩溃报告预览 + 上传窗口。
///
/// 设计文档 §7 第 10 条：默认关闭、需显式同意、可预览内容。
/// 所以这里的顺序是「先给用户看全文，再问要不要上传」，而不是后台偷偷发。
/// </summary>
public static class CrashReportDialog
{
    public static async Task ShowAsync(AppServices services, string report, XamlRoot xamlRoot)
    {
        var L = services.Localization;

        var intro = new TextBlock
        {
            Text = L["crash.intro"],
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["MutedTextStyle"],
        };

        // 报告全文：等宽字体 + 可选中复制。
        //
        // 用 ScrollViewer + TextBlock 而不是只读 TextBox：
        // TextBox 在 ContentDialog 里高度被压时只渲染首行（实测），
        // 而崩溃报告最需要看的就是后面的堆栈。
        var reportText = new TextBlock
        {
            Text = report,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            LineHeight = 17,
        };

        var viewer = new ScrollViewer
        {
            Content = reportText,
            Height = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Background = (Brush)Application.Current.Resources["SurfaceAltBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
        };

        // 滚动条可见性是附加属性，C# 对象初始化器里写不了，只能构造后单独设。
        ScrollViewer.SetHorizontalScrollBarVisibility(viewer, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(viewer, ScrollBarVisibility.Auto);

        var status = new TextBlock
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        };

        var linkRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Visibility = Visibility.Collapsed,
        };

        var openButton = new Button
        {
            Content = L["crash.openLink"],
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12,
        };

        var copyButton = new Button
        {
            Content = L["crash.copyLink"],
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12,
        };

        linkRow.Children.Add(openButton);
        linkRow.Children.Add(copyButton);

        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 560,
            MaxWidth = 720,
            Children = { intro, viewer, status, linkRow },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = L["crash.title"],
            Content = content,
            PrimaryButtonText = L["crash.upload"],
            CloseButtonText = L["common.close"],

            // 不设默认按钮：回车不该把崩溃报告直接传出去。
            // 顺带避免 ContentDialog 把「关闭」画成一整块强调色。
            DefaultButton = ContentDialogButton.None,
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],

            // 跟着应用主题走，否则深色界面上会弹出一个白底对话框。
            RequestedTheme = ThemeService.IsDark(services.Config.Client.Theme)
                ? ElementTheme.Dark
                : ElementTheme.Light,
        };

        string? uploadedUrl = null;

        openButton.Click += (_, _) =>
        {
            if (uploadedUrl is null)
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uploadedUrl)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                services.Logger.Warn("Crash", $"打开报告链接失败：{ex.Message}");
            }
        };

        copyButton.Click += (_, _) =>
        {
            if (uploadedUrl is null)
            {
                return;
            }

            try
            {
                var package = new DataPackage();
                package.SetText(uploadedUrl);
                Clipboard.SetContent(package);
                status.Text = L["crash.copied"];
                status.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                services.Logger.Warn("Crash", $"复制报告链接失败：{ex.Message}");
            }
        };

        // 上传是异步的：点主按钮后先禁用，完成前不给用户重复点的机会。
        // 用 Deferral 让对话框保持打开，直到上传结束。
        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();

            try
            {
                var button = sender as ContentDialog;
                if (button is not null)
                {
                    button.IsPrimaryButtonEnabled = false;
                }

                status.Text = L["crash.uploading"];
                status.Visibility = Visibility.Visible;

                using var uploader = new CrashReportUploader();
                var result = await uploader.UploadAsync(report);

                if (result.Success && result.Url is not null)
                {
                    uploadedUrl = result.Url;
                    status.Text = $"{L["crash.uploaded"]}{Environment.NewLine}{result.Url}";
                    linkRow.Visibility = Visibility.Visible;

                    services.Logger.Info("Crash", $"崩溃报告已上传：{result.Url}");
                }
                else
                {
                    status.Text = $"{L["crash.uploadFailed"]}{Environment.NewLine}{result.Error}";
                    services.Logger.Warn("Crash", $"崩溃报告上传失败：{result.Error}");
                }

                if (button is not null)
                {
                    button.IsPrimaryButtonEnabled = true;
                }
            }
            finally
            {
                // 上传完成后不要自动关窗 —— 用户还要复制链接。
                args.Cancel = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }
}
