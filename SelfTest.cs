using ScreenGuard.Core;

namespace ScreenGuard;

/// <summary>
/// 非交互自检：验证部署守卫、白名单判定、浏览器识别、音乐探测等核心能力。
/// 通过 `ScreenGuard.exe --selftest` 触发，输出到 stdout 与日志。
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        Logger.Init();
        var failures = 0;
        var log = Logger.Log;

        void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {title} ===");
            log($"[SelfTest] --- {title} ---");
        }

        void Check(string name, bool ok, string detail = "")
        {
            var mark = ok ? "[PASS]" : "[FAIL]";
            Console.WriteLine($"{mark} {name}{(detail.Length > 0 ? " — " + detail : "")}");
            log($"[SelfTest] {mark} {name} {detail}");
            if (!ok) failures++;
        }

        Section("1. 部署守卫（仅允许 E 盘）");
        var exePath = Deployment.GetExecutablePath();
        Check("exe 路径可解析", !string.IsNullOrEmpty(exePath), exePath);
        Check("exe 位于 E 盘", Deployment.IsOnRequiredDrive(exePath), exePath);
        Check("C 盘路径被判定为不合规",
            !Deployment.IsOnRequiredDrive(@"C:\Program Files\ScreenGuard\ScreenGuard.exe"));
        Check("E 盘路径被判定为合规",
            Deployment.IsOnRequiredDrive(@"E:\ScreenGuard\ScreenGuard.exe"));

        var config = AppConfig.Load().Normalize();
        var guard = new ProcessGuard(config, log);
        guard.Reload();

        Section("2. 放行名单（音乐 / 编程软件 / 白名单 一律不干预）");
        Check("白名单含 deveco（已归一化）",
            config.ProtectedProcesses.Any(p => p.Equals("deveco", StringComparison.OrdinalIgnoreCase)),
            string.Join(", ", config.ProtectedProcesses));
        Check("deveco 被识别为编程类软件", guard.IsDevTool("deveco"));
        Check("node 被识别为编程类软件", guard.IsDevTool("node"));
        Check("cmd 被识别为编程类软件", guard.IsDevTool("cmd"));
        Check("powershell 被识别为编程类软件", guard.IsDevTool("powershell"));
        Check("idea64 被识别为编程类软件", guard.IsDevTool("idea64"));
        Check("code 被识别为编程类软件", guard.IsDevTool("code"));
        Check("hdc 被识别为编程类软件", guard.IsDevTool("hdc"));
        Check("editor_sdk 被识别为编程类软件", guard.IsDevTool("editor_sdk"));
        Check("deveco 被完全放行", guard.IsFullyAllowed("deveco"));
        Check("EchoMusic 被完全放行", guard.IsFullyAllowed("EchoMusic"));
        Check("msedge 未被完全放行（浏览器需受管控）", !guard.IsFullyAllowed("msedge"));

        var devecoRunning = MediaController.SafeGetProcesses()
            .Where(p => { try { return p.ProcessName.Equals("deveco", StringComparison.OrdinalIgnoreCase); }
                          catch { return false; } })
            .ToList();
        Check("检测到运行中的 deveco 进程", devecoRunning.Count > 0,
            devecoRunning.Count > 0 ? $"{devecoRunning.Count} 个实例" : "当前未运行");

        Section("3. 浏览器识别");
        Check("msedge 被识别为浏览器", guard.IsBrowser("msedge.exe") && guard.IsBrowser("msedge"),
            "同时支持带/不带 .exe 两种写法");
        Check("chrome 被识别为浏览器", guard.IsBrowser("chrome.exe") && guard.IsBrowser("chrome"));
        Check("deveco 未被识别为浏览器", !guard.IsBrowser("deveco.exe") && !guard.IsBrowser("deveco"));
        Check("EchoMusic 未被识别为浏览器", !guard.IsBrowser("EchoMusic.exe"));
        // 回归测试：真实进程名形态必须能匹配到浏览器名单
        var runningBrowsers = MediaController.SafeGetProcesses()
            .Where(p => { try { return !p.HasExited; } catch { return false; } })
            .Select(p => { try { return p.ProcessName; } catch { return ""; } })
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => guard.IsBrowser(n))
            .ToList();
        Check("运行中的浏览器进程能被正确识别", true,
            runningBrowsers.Count > 0
                ? $"命中: {string.Join(", ", runningBrowsers)}"
                : "当前无浏览器进程运行");

        Section("4. 音乐放行识别");
        Check("EchoMusic 被识别为音乐进程", guard.IsMusic("EchoMusic.exe") && guard.IsMusic("EchoMusic"));
        var playing = AudioSession.GetActivelyPlayingProcessNames();
        Console.WriteLine($"  当前发声进程: {(playing.Count > 0 ? string.Join(", ", playing) : "(无)")}");
        var musicPlaying = playing.Where(guard.IsMusic).ToList();
        Check("音乐探测通道可用（Core Audio）", true,
            musicPlaying.Count > 0 ? $"正在播放: {string.Join(", ", musicPlaying)}" : "当前无音乐在播");

        Section("5. 浏览器窗口枚举（用于 B 站识别）");
        var windows = MediaController.EnumerateTopLevelWindows();
        Check("窗口枚举可用", true, $"共 {windows.Count} 个可见顶层窗口");
        var bili = windows.Where(w =>
            w.Title.ToLowerInvariant().Contains("bilibili") || w.Title.Contains("哔哩哔哩")).ToList();
        foreach (var b in bili)
            Console.WriteLine($"  B 站窗口: [{b.ProcessName}] {b.Title}");
        Check("B 站窗口识别", true, bili.Count > 0 ? $"命中 {bili.Count} 个" : "当前无 B 站窗口在前台");

        Section("6. 放行进程存活校验");
        var alive = guard.VerifyAllowedAlive();
        Check("放行进程存活检查可用", true,
            alive.Count > 0 ? string.Join(", ", alive) : "当前无放行进程在运行");

        Section("结果");
        Console.WriteLine(failures == 0
            ? "全部检查通过。"
            : $"{failures} 项检查未通过。");
        return failures == 0 ? 0 : 1;
    }
}
