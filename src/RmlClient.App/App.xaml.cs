using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RmlClient.App.Services;
using RmlClient.Core.Configuration;

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

        // 主题：跟随设置里的 theme 键。
        // 注意 Application.RequestedTheme 只能在 App 构造函数里设置，
        // 此处改用窗口根元素的 ElementTheme 来切换，效果等价且不会抛 COMException。
        if (_window.Content is FrameworkElement root)
        {
            root.RequestedTheme = Services.Config.Client.Theme.Equals("light", StringComparison.OrdinalIgnoreCase)
                ? ElementTheme.Light
                : ElementTheme.Dark;
        }

        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Services?.Logger.Error("Crash", "UI 线程未处理异常", e.Exception);

        // 标记已处理，避免进程直接消失；具体错误通过日志页展示。
        e.Handled = true;
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
