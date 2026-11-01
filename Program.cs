using ScreenGuard.Core;
using ScreenGuard.UI;

namespace ScreenGuard;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // —— 自检模式：非交互验证核心能力，供部署后检查使用 ——
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run();

        // —— 端到端验证：真实进入休息并截图 ——
        if (args.Any(a => a.Equals("--e2e", StringComparison.OrdinalIgnoreCase)))
            return E2ETest.Run();

        // —— 真实界面截图：抓真实控件渲染结果 ——
        if (args.Any(a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase)))
            return ShotTest.Run(args);

        // —— 界面预览：渲染静态图用于核对布局，不进入全屏锁定 ——
        if (args.Any(a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase)))
            return Preview.Run(args);

        // —— 歌词搬运与置顶验证 ——
        if (args.Any(a => a.Equals("--placertest", StringComparison.OrdinalIgnoreCase)))
            return PlacerTest.Run();

        // —— 放行行为验证：确认音乐 / 编程软件不被干预 ——
        if (args.Any(a => a.Equals("--allowtest", StringComparison.OrdinalIgnoreCase)))
            return AllowTest.Run();

        // —— 休息流程测试模式：直接进入休息，输出异常堆栈 ——
        if (args.Any(a => a.Equals("--resttest", StringComparison.OrdinalIgnoreCase)))
            return RestTest.Run(args);

        // —— 部署守卫：仅允许在 E 盘运行，禁止 C 盘 ——
        var exePath = Deployment.GetExecutablePath();
        if (!Deployment.IsValidLocation())
        {
            MessageBox.Show(
                $"ScreenGuard 只能部署并运行在 {Deployment.RequiredDrive} 盘。\n\n" +
                $"当前路径: {exePath}\n\n" +
                $"请将程序移动到 {Deployment.InstallRoot} 后重新运行。",
                "部署位置校验失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }

        Logger.Init();
        Logger.Log($"=== ScreenGuard 启动 (PID {Environment.ProcessId}) ===");
        Logger.Log($"程序路径: {exePath}");

        // 单实例：避免重复启动导致双重计时
        using var mutex = new Mutex(true, @"Local\ScreenGuard.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Logger.Log("检测到已有实例运行，本次启动退出");
            // 已有实例在托盘，提示后退出（不阻塞）
            MessageBox.Show("ScreenGuard 已在运行，请查看系统托盘图标。", "ScreenGuard",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Logger.Log($"[Fatal] UI 线程异常: {e.Exception}");
            try { File.WriteAllText(Path.Combine(Deployment.DataRoot, "crash.txt"), e.Exception.ToString()); }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Log($"[Fatal] 未处理异常: {e.ExceptionObject}");
            try { File.WriteAllText(Path.Combine(Deployment.DataRoot, "crash-fatal.txt"), e.ExceptionObject?.ToString() ?? ""); }
            catch { }
        };

        var config = AppConfig.Load();
        Logger.Log($"配置加载完成: 工作 {config.WorkMinutes} 分钟 / 休息 {config.RestMinutes} 分钟");
        Logger.Log($"受保护进程(白名单): {string.Join(", ", config.ProtectedProcesses)}");

        try
        {
            Application.Run(new MainForm(config, Logger.Log));
        }
        catch (Exception ex)
        {
            Logger.Log($"[Fatal] 主循环异常: {ex}");
        }
        finally
        {
            Logger.Log("=== ScreenGuard 退出 ===");
        }

        return 0;
    }
}
