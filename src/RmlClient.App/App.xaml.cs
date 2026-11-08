using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RmlClient.App.Controls;
using RmlClient.App.Services;
using RmlClient.Core.Configuration;
using RmlClient.Core.Diagnostics;

namespace RmlClient.App;

public partial class App : Application
{
    private MainWindow? _window;

    public static AppServices Services { get; private set; } = null!;

    public static Window? MainWindowInstance => (Current as App)?._window;

    public App()
    {
        InitializeComponent();

        // 全局异常兜底：先把日志落盘，再让用户看到可读的提示，
        // 而不是 WinUI 默认的静默崩溃。
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Services?.Logger.Error("Crash", "未处理异常", e.ExceptionObject as Exception);
            }
            catch (Exception)
            {
                // 日志本身失败时无能为力。
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 已有实例在跑就直接退出，并把它的窗口提到前台。
        // 客户端驻留托盘，用户很可能从开始菜单再点一次 ——
        // 不加锁会出现多个托盘图标抢同一份 config.ini。
        if (!SingleInstance.TryAcquire())
        {
            Exit();
            return;
        }

        try
        {
            Services = new AppServices();
        }
        catch (Exception ex)
        {
            // 此时日志系统还没建起来，只能退回到 %TEMP% 里的纯文本记录。
            WriteBootFailure("初始化服务", ex);
            throw;
        }

        try
        {
            _window = new MainWindow(Services);
        }
        catch (Exception ex)
        {
            Services.Logger.Error("Crash", "主窗口创建失败", ex);
            WriteBootFailure("创建主窗口", ex);
            throw;
        }

        // 主题：先按配置改写资源画刷（StaticResource 解析出来是同一实例，
        // 改 Color 就能让全部引用处一起变色），再设根元素主题带动 WinUI 控件模板。
        // 注意 Application.RequestedTheme 只能在 App 构造函数里设置，
        // 此处改用窗口根元素的 ElementTheme，效果等价且不会抛 COMException。
        ThemeService.Apply(Services.Config.Client.Theme, Services.Config.Client.Accent);
        ThemeService.ApplyToWindow(_window, Services.Config.Client.Theme);

        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Services?.Logger.Error("Crash", "UI 线程未处理异常", e.Exception);

        // 用户开了「上传崩溃日志」才提示，否则只是安静记日志。
        // 上传必须经过预览确认，不能偷偷发。
        TryOfferCrashReport(e.Exception);

        // 标记已处理，避免进程直接消失；具体错误通过日志页展示。
        e.Handled = true;
    }

    /// <summary>
    /// 崩溃时若用户已授权上传，弹一个预览窗口让他确认。
    /// 全程不阻塞 UI 线程 —— 崩溃时最怕的就是又卡住。
    /// </summary>
    private void TryOfferCrashReport(Exception? exception)
    {
        try
        {
            if (Services?.Config.Client.UploadCrashLogs != true || _window is null)
            {
                return;
            }

            var entries = Services.Logger.Snapshot();
            var report = CrashReportBuilder.Build(entries, exception, "UI 线程未处理异常");

            _ = _window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // 窗口刚 Activate 时 Content 可能还没挂到树上，XamlRoot 为 null，
                    // 此时弹 ContentDialog 会抛「This element does not have a XamlRoot」。
                    // 等一小会儿再试，最多 10 次（约 2 秒）。
                    XamlRoot? xamlRoot = null;
                    for (var i = 0; i < 10 && xamlRoot is null; i++)
                    {
                        xamlRoot = _window.Content?.XamlRoot;
                        if (xamlRoot is null)
                        {
                            await Task.Delay(200);
                        }
                    }

                    if (xamlRoot is null)
                    {
                        Services.Logger.Warn("Crash", "窗口尚未就绪，跳过崩溃报告窗口。");
                        return;
                    }

                    await CrashReportDialog.ShowAsync(Services, report, xamlRoot);
                }
                catch (Exception ex)
                {
                    Services.Logger.Warn("Crash", $"崩溃报告窗口打开失败：{ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Services?.Logger.Warn("Crash", $"生成崩溃报告失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 日志系统可用之前的启动失败，只能写到 %TEMP% 里，
    /// 否则用户看到的就是「双击没反应」。
    /// </summary>
    private static void WriteBootFailure(string stage, Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "rmlclient-boot.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {stage}失败：{ex}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 连这个都写不了就没别的办法了。
        }
    }
}
