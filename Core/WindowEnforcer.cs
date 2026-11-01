namespace ScreenGuard.Core;

/// <summary>
/// 休息期间的窗口管控器。
///
/// 【策略分层 —— 按用户原话落实】
///  · 编程类软件 / 管理员白名单："进程活着不能操作"
///      → 进程不终止、窗口不最小化（还在原位，能看见它在跑）
///      → 但抢不到焦点、点不动、键盘输入到不了它
///  · 音乐播放器：完全放行
///      → 桌面歌词搬进休息界面显示，底部控制条可切歌调音量
///      → 音乐窗口也不最小化，可自由开关
///  · 其余程序（浏览器、办公软件等）：休息期间最小化，使其不可被操作
///
/// 关键工程约束：最小化窗口后 Windows 会转移焦点，若不加限制地反复"最小化+拉回焦点"
/// 会形成死循环（焦点转移→拦截→最小化→焦点转移），表现为 CPU 飙升、界面卡死。
/// 因此：只处理尚未处理过的窗口句柄，并设置冷却时间。
/// </summary>
public sealed class WindowEnforcer
{
    private readonly ProcessGuard _guard;
    private readonly Action<string> _log;

    private readonly Dictionary<IntPtr, bool> _minimized = new();
    private readonly HashSet<IntPtr> _suppressedHandles = new();
    private readonly HashSet<IntPtr> _interceptedHandles = new();
    private DateTime _lastInterceptTime = DateTime.MinValue;

    public WindowEnforcer(ProcessGuard guard, Action<string> log)
    {
        _guard = guard;
        _log = log;
    }

    /// <summary>窗口所属进程的分类结果。</summary>
    private enum WindowClass { Managed, AliveOnly, InteractiveAllowed }

    private WindowClass Classify(IntPtr hwnd)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            var procName = AudioSession.ResolveProcessName(pid);
            if (string.IsNullOrEmpty(procName)) return WindowClass.Managed;

            var path = SafeExePath(pid);
            if (_guard.IsInteractiveAllowed(procName)) return WindowClass.InteractiveAllowed;
            if (_guard.IsAliveOnly(procName, path)) return WindowClass.AliveOnly;
            return WindowClass.Managed;
        }
        catch
        {
            return WindowClass.Managed;
        }
    }

    private static string? SafeExePath(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 休息开始：最小化"受管控"的窗口。
    /// 编程软件与音乐窗口一律保留在原位（不最小化）。
    /// </summary>
    public int MinimizeAllExceptAllowed()
    {
        var count = 0;

        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                if (IsOwnWindow(hwnd)) return true;

                // 编程软件（alive-only）与音乐（interactive）都不最小化
                if (Classify(hwnd) != WindowClass.Managed) return true;

                var sb = new System.Text.StringBuilder(256);
                Native.GetWindowText(hwnd, sb, sb.Capacity);
                if (string.IsNullOrWhiteSpace(sb.ToString())) return true;

                if (Native.IsIconic(hwnd)) return true;

                _suppressedHandles.Add(hwnd);
                Native.ShowWindow(hwnd, Native.SW_HIDE);
                _minimized[hwnd] = true;
                count++;
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        if (count > 0)
            _log($"[Window] 休息开始，已最小化 {count} 个受管控窗口（编程软件与音乐保留原位）");
        return count;
    }

    /// <summary>
    /// 休息期间持续执行：把新出现的受管控窗口**隐藏**（不是最小化）。
    ///
    /// 【为什么必须用隐藏】实测发现：模拟器 / UWP 宿主弹出的窗口（如错误对话框）
    /// 会持续重绘并被系统反复激活，最小化后它们仍会闪出来挡住休息界面，
    /// 而且会触发 z-order 争夺。隐藏（SW_HIDE）后：
    ///   · 窗口彻底不可见，界面不再被遮挡
    ///   · 进程照常运行（编译、模拟器后台任务不受影响）
    ///   · 休息结束时统一还原
    /// </summary>
    public void EnforceSuppressed()
    {
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                if (IsOwnWindow(hwnd)) return true;

                // 已处理过的窗口不再重复操作
                if (_suppressedHandles.Contains(hwnd)) return true;

                if (Classify(hwnd) != WindowClass.Managed) return true;

                // 空标题窗口也要隐藏（Nexus 等程序的前台窗口无标题）
                _suppressedHandles.Add(hwnd);
                Native.ShowWindow(hwnd, Native.SW_HIDE);
                _minimized[hwnd] = true;
            }
            catch { }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// 拦截前台激活 —— 这是"活着但不能操作"的实现核心。
    ///
    /// · 受管控窗口（浏览器等）→ 最小化 + 把休息界面拉回前台
    /// · 编程软件（alive-only）  → **不最小化**（进程与窗口保持原状），
    ///                              只把焦点抢回休息界面，用户点不动、键盘也进不去
    /// · 音乐（interactive）     → 完全放行，不抢焦点
    /// </summary>
    public bool SuppressForegroundActivation(IntPtr restScreenHandle)
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == restScreenHandle) return false;
        if (IsOwnWindow(foreground)) return false;

        // 冷却期内的重复激活放行，防止与系统焦点转移机制打架
        var now = DateTime.Now;
        if (now - _lastInterceptTime < TimeSpan.FromMilliseconds(1500))
            return false;

        try
        {
            var cls = Classify(foreground);

            // 音乐完全放行 —— 用户可以随意操作
            if (cls == WindowClass.InteractiveAllowed) return false;

            var sb = new System.Text.StringBuilder(256);
            Native.GetWindowText(foreground, sb, sb.Capacity);
            var title = sb.ToString();

            // 注意：不能因为标题为空就放行。
            // 实测 Nexus 等程序的前台窗口标题为空，若直接放行，
            // 用户就能在休息期间把这类窗口拉到前台操作。
            if (_interceptedHandles.Contains(foreground)) return false;

            _interceptedHandles.Add(foreground);
            _lastInterceptTime = now;

            var displayName = string.IsNullOrWhiteSpace(title) ? "(无标题窗口)" : Trim(title);

            if (cls == WindowClass.AliveOnly)
            {
                // 编程软件：窗口保持原状（不最小化、不隐藏），只是抢不回焦点
                // 这样编译/构建继续跑，但用户无法操作它
                Native.SetForegroundWindow(restScreenHandle);
                Native.BringWindowToTop(restScreenHandle);
                _log($"[Window] 编程窗口不可操作（进程保持运行）: {displayName}");
                return true;
            }

            // 受管控窗口：隐藏并抢回前台
            // 用隐藏而非最小化 —— 最小化的窗口在任务栏仍可见且可能被系统重新显示
            Native.ShowWindow(foreground, Native.SW_HIDE);
            _suppressedHandles.Add(foreground);
            _minimized[foreground] = true;

            Native.SetForegroundWindow(restScreenHandle);
            Native.BringWindowToTop(restScreenHandle);

            _log($"[Window] 拦截窗口激活并压回: {displayName}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>休息结束：恢复之前被隐藏/最小化的窗口。</summary>
    public void RestoreAll()
    {
        var restored = 0;
        foreach (var hwnd in _minimized.Keys.ToList())
        {
            try
            {
                if (!Native.IsWindow(hwnd)) continue;
                // 隐藏的窗口用 SW_SHOW 恢复可见（不需要 SW_RESTORE 的最大化/最小化状态还原）
                Native.ShowWindow(hwnd, Native.SW_SHOW);
                restored++;
            }
            catch { }
        }
        _minimized.Clear();
        _suppressedHandles.Clear();
        _interceptedHandles.Clear();
        _lastInterceptTime = DateTime.MinValue;
        if (restored > 0)
            _log($"[Window] 休息结束，已恢复 {restored} 个窗口");
    }

    private static string Trim(string s)
        => s.Length <= 50 ? s : s[..50] + "...";

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }
}
