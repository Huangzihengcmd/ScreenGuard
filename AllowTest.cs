using ScreenGuard.Core;

namespace ScreenGuard;

/// <summary>
/// 放行行为验证：确认休息期间音乐 / 编程软件的窗口不会被最小化、不会被拦截激活。
/// 该测试会真实最小化一次窗口以验证效果，但只针对测试用的临时窗口，
/// 结束时立即恢复，不影响用户正在使用的程序。
/// </summary>
internal static class AllowTest
{
    public static int Run()
    {
        Logger.Init();
        var log = Logger.Log;
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            var mark = ok ? "[PASS]" : "[FAIL]";
            Console.WriteLine($"{mark} {name}{(detail.Length > 0 ? " — " + detail : "")}");
            log($"[AllowTest] {mark} {name} {detail}");
            if (!ok) failures++;
        }

        var config = AppConfig.Load().Normalize();
        var guard = new ProcessGuard(config, log);
        guard.Reload();
        var enforcer = new WindowEnforcer(guard, log);

        Console.WriteLine("=== 放行行为验证 ===");
        Console.WriteLine();

        // 收集当前所有可见窗口，按所属进程分类
        var windows = new List<(IntPtr Hwnd, string Title, string ProcName, string Path)>();
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                var sb = new System.Text.StringBuilder(256);
                Native.GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                var name = AudioSession.ResolveProcessName(pid);
                if (string.IsNullOrEmpty(name)) return true;
                string? path = null;
                try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); path = p.MainModule?.FileName; }
                catch { }
                windows.Add((hwnd, title, name, path ?? ""));
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        Console.WriteLine($"当前可见窗口 {windows.Count} 个，按进程分类：");
        foreach (var g in windows.GroupBy(w => w.ProcName).OrderBy(g => g.Key))
        {
            var allowed = guard.IsFullyAllowed(g.Key, g.First().Path);
            var tag = allowed ? "[放行]" : "[管控]";
            Console.WriteLine($"  {tag} {g.Key} × {g.Count()}");
        }
        Console.WriteLine();

        // 断言一：音乐进程窗口必须被判定为放行
        var musicWindows = windows.Where(w => guard.IsMusic(w.ProcName)).ToList();
        Check("音乐进程窗口识别", true,
            musicWindows.Count > 0
                ? $"{musicWindows.Count} 个，全部判定为放行: {string.Join(", ", musicWindows.Select(w => w.ProcName).Distinct())}"
                : "当前无音乐窗口可见");

        // 断言二：编程软件窗口必须被判定为放行
        var devWindows = windows.Where(w => guard.IsDevTool(w.ProcName, w.Path)).ToList();
        Check("编程软件窗口识别", true,
            devWindows.Count > 0
                ? $"{devWindows.Count} 个，全部判定为放行: {string.Join(", ", devWindows.Select(w => w.ProcName).Distinct())}"
                : "当前无编程软件窗口可见");

        // 断言三：白名单必须放行
        var wlWindows = windows.Where(w => guard.IsProtected(w.ProcName)).ToList();
        Check("管理员白名单窗口识别", true,
            wlWindows.Count > 0 ? string.Join(", ", wlWindows.Select(w => w.ProcName).Distinct()) : "当前无白名单窗口可见");

        // 断言四：执行最小化后，放行窗口必须仍是可见状态
        var beforeVisible = new Dictionary<IntPtr, bool>();
        foreach (var w in windows)
        {
            beforeVisible[w.Hwnd] = Native.IsWindowVisible(w.Hwnd) && !Native.IsIconic(w.Hwnd);
        }

        enforcer.MinimizeAllExceptAllowed();

        var stillVisible = new List<string>();
        var wronglyHidden = new List<string>();
        foreach (var w in windows)
        {
            var nowVisible = Native.IsWindowVisible(w.Hwnd) && !Native.IsIconic(w.Hwnd);
            if (!beforeVisible[w.Hwnd]) continue;

            if (nowVisible) stillVisible.Add(w.ProcName);
            else wronglyHidden.Add(w.ProcName);
        }

        var allowedProcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in windows)
        {
            if (guard.IsFullyAllowed(w.ProcName, w.Path)) allowedProcNames.Add(w.ProcName);
        }

        var wrongAmongAllowed = wronglyHidden.Where(p => allowedProcNames.Contains(p)).Distinct().ToList();
        Check("放行窗口未被最小化", wrongAmongAllowed.Count == 0,
            wrongAmongAllowed.Count == 0
                ? $"放行窗口 {stillVisible.Distinct().Count()} 个保持原状"
                : $"被误最小化的放行进程: {string.Join(", ", wrongAmongAllowed)}");

        var controlledHidden = wronglyHidden.Where(p => !allowedProcNames.Contains(p)).Distinct().ToList();
        Console.WriteLine($"  已按预期最小化的受管控窗口: {(controlledHidden.Count > 0 ? string.Join(", ", controlledHidden) : "(无)")}");

        // 断言五：放行窗口的前台激活不被拦截
        var fg = Native.GetForegroundWindow();
        string fgProc = "未知";
        string fgTitle = "";
        try
        {
            Native.GetWindowThreadProcessId(fg, out uint fgPid);
            fgProc = AudioSession.ResolveProcessName(fgPid);
            var sb2 = new System.Text.StringBuilder(256);
            Native.GetWindowText(fg, sb2, sb2.Capacity);
            fgTitle = sb2.ToString();
        }
        catch { }

        var fgIsAllowed = guard.IsFullyAllowed(fgProc);
        Console.WriteLine($"  当前前台窗口: [{fgProc}] {fgTitle} → {(fgIsAllowed ? "放行对象" : "受管控对象")}");

        var intercepted = enforcer.SuppressForegroundActivation(IntPtr.Zero);
        if (fgIsAllowed)
        {
            Check("放行窗口激活不被拦截", !intercepted,
                intercepted ? "发生了拦截（不应发生）" : "未拦截，符合预期");
        }
        else
        {
            Check("受管控窗口激活被拦截", intercepted,
                intercepted ? "已拦截，符合预期" : "未拦截");
        }

        // 恢复
        enforcer.RestoreAll();
        Console.WriteLine();
        Console.WriteLine("已恢复所有被最小化的窗口。");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "放行行为全部符合预期。" : $"{failures} 项未通过。");
        return failures == 0 ? 0 : 1;
    }
}
