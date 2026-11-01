using ScreenGuard.Core;
using ScreenGuard.UI;

namespace ScreenGuard;

/// <summary>
/// 休息流程专项测试：直接进入休息状态，逐步执行每个环节，
/// 捕获并打印异常堆栈，用于定位休息期崩溃。
/// 用法: ScreenGuard.exe --resttest [秒数]
/// </summary>
internal static class RestTest
{
    public static int Run(string[] args)
    {
        Logger.Init();
        var seconds = 20;
        var idx = Array.FindIndex(args, a => a.Equals("--resttest", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var s))
            seconds = s;

        var log = Logger.Log;
        var failures = 0;

        void Step(string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine($"[PASS] {name}");
                log($"[RestTest] [PASS] {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"[FAIL] {name}");
                Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"       {ex.StackTrace}");
                log($"[RestTest] [FAIL] {name} :: {ex}");
            }
        }

        var config = AppConfig.Load().Normalize();
        config.WorkMinutes = 1;
        config.RestMinutes = 2;
        config.AllowEarlyExit = true;

        var guard = new ProcessGuard(config, log);
        guard.Reload();
        var media = new MediaController(config, guard, log);
        var keyboard = new KeyboardInterceptor(log);
        var enforcer = new WindowEnforcer(guard, log);

        Console.WriteLine($"=== 休息流程专项测试 (休息时长 {seconds}s) ===");
        Console.WriteLine();

        Step("浏览器快照", () => guard.CapturePreExistingBrowsers());
        Step("音乐状态采样", () => media.CaptureMusicPlaybackState());
        Step("B站视频暂停", () => media.PauseBilibiliVideo());
        Step("窗口最小化", () => enforcer.MinimizeAllExceptAllowed());
        Step("键盘拦截启动", () => keyboard.Start());
        Step("休息界面创建", () =>
        {
            using var screen = new RestScreen(config, () => { }, log);
            var total = TimeSpan.FromSeconds(seconds);
            var end = DateTime.Now.Add(total);
            screen.BeginRest(end, total, "测试状态");
            Thread.Sleep(300);
            if (!screen.Visible) throw new InvalidOperationException("休息界面未显示");
        });
        Step("休息界面置顶", () =>
        {
            using var screen = new RestScreen(config, () => { }, log);
            var total = TimeSpan.FromSeconds(seconds);
            screen.BeginRest(DateTime.Now.Add(total), total, "测试状态");
            Thread.Sleep(200);
            screen.Refresh();
        });
        Step("前台激活拦截", () =>
        {
            using var screen = new RestScreen(config, () => { }, log);
            var total = TimeSpan.FromSeconds(seconds);
            screen.BeginRest(DateTime.Now.Add(total), total, "测试状态");
            Thread.Sleep(200);
            enforcer.SuppressForegroundActivation(screen.Handle);
        });
        Step("浏览器窗口压制扫描", () => guard.CollectBrowsersToSuppress());
        Step("窗口压制扫描", () => enforcer.EnforceSuppressed());
        Step("音乐放行校验", () => media.EnsureMusicContinues(new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        Step("键盘拦截停止", () => keyboard.Stop());
        Step("窗口恢复", () => enforcer.RestoreAll());
        Step("清除浏览器快照", () => guard.ClearBrowserSnapshot());

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "休息流程全部环节通过。"
            : $"{failures} 个环节失败。");
        return failures == 0 ? 0 : 1;
    }
}
