using ScreenGuard.Core;

namespace ScreenGuard.Core;

/// <summary>
/// 放行窗口安置器：让"被放行的程序"在休息期间依然可见、可操作。
///
/// 【用户需求】
///  "我只能听音乐，我又不可以放音乐" —— 光让音乐继续播不够，
///  用户要能操作音乐（切歌、暂停、调音量），也要能用编程软件。
///
/// 做法：
///  1. 音乐播放器的"桌面歌词"窗口 → 搬到休息界面的歌词承载区，
///     这样歌词直接显示在休息界面上（用户原话："把桌面歌词照搬上去就完事了"）。
///  2. 其余放行窗口（编程软件、音乐主窗口）→ 置顶到休息界面之上，
///     用户可以直接点击操作，不被全屏界面挡住。
///  3. 休息结束 → 全部还原到原位置与原层级。
/// </summary>
public sealed class AllowedWindowPlacer
{
    private readonly ProcessGuard _guard;
    private readonly Action<string> _log;

    // 记录被移动/置顶的窗口，休息结束还原
    private readonly Dictionary<IntPtr, WindowState> _moved = new();

    private sealed record WindowState(Rectangle Rect, bool WasTopMost, IntPtr Owner);

    public AllowedWindowPlacer(ProcessGuard guard, Action<string> log)
    {
        _guard = guard;
        _log = log;
    }

    /// <summary>
    /// 音乐播放器的桌面歌词窗口句柄。
    ///
    /// 判定采用两条独立线索，任一命中即认为找到了歌词窗：
    ///  1. 窗口标题含歌词特征词（Lyric / 歌词 / Lrc）
    ///  2. 形状特征：横向扁长（宽 ≥ 200 且高 ≤ 260）且体积不大
    /// 第 2 条能覆盖标题为空或自定义标题的歌词窗。
    /// </summary>
    public IntPtr FindLyricsWindow()
    {
        IntPtr best = IntPtr.Zero;
        var bestScore = int.MinValue;

        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd)) return true;

                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                var procName = AudioSession.ResolveProcessName(pid);
                if (string.IsNullOrEmpty(procName)) return true;
                if (!_guard.IsMusic(procName)) return true;

                if (!Native.TryGetWindowRect(hwnd, out var rect)) return true;
                var w = rect.Width;
                var h = rect.Height;
                if (w <= 0 || h <= 0) return true;

                var sb = new System.Text.StringBuilder(256);
                Native.GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();

                var area = (long)w * h;
                var score = int.MinValue;

                // 线索 1：标题含歌词特征
                if (title.Contains("Lyric", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("歌词", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("Lrc", StringComparison.OrdinalIgnoreCase))
                {
                    score = 1000;
                }
                // 线索 2：横向扁长的小浮窗
                else if (w >= 200 && h <= 260 && area <= 400000)
                {
                    score = 500;
                }

                if (score > int.MinValue && score > bestScore)
                {
                    bestScore = score;
                    best = hwnd;
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return best;
    }

    /// <summary>把桌面歌词窗口搬到休息界面的承载区。</summary>
    public bool MoveLyricsToHost(IntPtr lyricsWindow, Rectangle hostRect)
    {
        if (lyricsWindow == IntPtr.Zero || hostRect.Width <= 0) return false;

        try
        {
            if (!Native.TryGetWindowRect(lyricsWindow, out var oldRect)) return false;

            var oldTopMost = IsTopMost(lyricsWindow);
            RecordState(lyricsWindow, new WindowState(
                new Rectangle(oldRect.Left, oldRect.Top, oldRect.Width, oldRect.Height),
                oldTopMost, IntPtr.Zero));

            // 移到承载区，居中
            var x = hostRect.Left + (hostRect.Width - oldRect.Width) / 2;
            var y = hostRect.Top + (hostRect.Height - oldRect.Height) / 2;

            // 置顶到休息界面之上，使歌词可见
            Native.SetWindowPos(lyricsWindow, Native.HWND_TOPMOST, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);

            _log($"[Placer] 桌面歌词已移入休息界面承载区 ({x},{y})");
            return true;
        }
        catch (Exception ex)
        {
            _log($"[Placer] 移动桌面歌词失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把所有放行窗口（编程软件、音乐主窗口）置顶到休息界面之上，使其可点击操作。
    /// 只置顶，不最小化、不关闭。
    ///
    /// 【按用户原话修订】编程软件是"进程活着但不能操作"，
    /// 因此**不能**把它们置顶到休息界面之上 —— 置顶了用户就能点。
    /// 这里只处理音乐主窗口；编程软件由 WindowEnforcer 保持原位并抢焦点。
    /// </summary>
    public int RaiseAllowedWindowsAbove(IntPtr restScreenHandle)
    {
        var raised = 0;

        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                if (hwnd == restScreenHandle) return true;

                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                var procName = AudioSession.ResolveProcessName(pid);
                if (string.IsNullOrEmpty(procName)) return true;

                // 只置顶"音乐"这一类可交互放行对象；编程软件保持原位不可操作
                if (!_guard.IsInteractiveAllowed(procName)) return true;

                if (!_moved.ContainsKey(hwnd) && IsTopMost(hwnd)) return true;

                if (Native.TryGetWindowRect(hwnd, out var rect))
                {
                    RecordState(hwnd, new WindowState(
                        new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height),
                        IsTopMost(hwnd), IntPtr.Zero));
                }

                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                raised++;
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        if (raised > 0)
            _log($"[Placer] 已将 {raised} 个音乐窗口置顶（可自由操作）");
        return raised;
    }

    /// <summary>
    /// 确保休息界面压在所有窗口之上。
    ///
    /// 【必须做】实测发现：UWP 应用宿主（ApplicationFrameHost）、
    /// 模拟器、小程序宿主等窗口本身带 WS_EX_TOPMOST，会浮在休息界面之上
    /// 把内容全挡住。强制锁定的底线是"休息界面必须在最顶层"。
    /// </summary>
    public void KeepRestScreenOnTop(IntPtr restScreenHandle)
    {
        try
        {
            if (restScreenHandle == IntPtr.Zero) return;
            Native.SetWindowPos(restScreenHandle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        catch { }
    }

    /// <summary>休息结束：还原所有被移动/置顶的窗口。</summary>
    public void RestoreAll()
    {
        foreach (var (hwnd, state) in _moved)
        {
            try
            {
                if (!Native.IsWindow(hwnd)) continue;

                // 先把位置挪回去 —— 不带 SWP_NOZORDER，确保 SetWindowPos 真的生效
                Native.SetWindowPos(hwnd, IntPtr.Zero, state.Rect.Left, state.Rect.Top, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

                // 再恢复原有层级
                var insertAfter = state.WasTopMost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST;
                Native.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch { }
        }

        if (_moved.Count > 0)
            _log($"[Placer] 已还原 {_moved.Count} 个窗口的位置与层级");
        _moved.Clear();
    }

    /// <summary>
    /// 记录窗口的原始状态。
    /// 关键：只记第一次 —— 同一窗口可能被搬运和置顶两次处理，
    /// 若第二次覆盖记录，就会把"搬运后的位置"当成原位置，导致还原失败。
    /// </summary>
    private void RecordState(IntPtr hwnd, WindowState state)
    {
        if (!_moved.ContainsKey(hwnd))
            _moved[hwnd] = state;
    }

    /// <summary>
    /// 确保桌面歌词已搬进承载区。歌词窗可能稍晚才创建，因此可重复调用。
    /// </summary>
    /// <param name="hostRect">承载区（屏幕坐标）</param>
    /// <param name="alreadyMoved">已经搬运过的句柄，避免重复操作</param>
    /// <returns>是否发生了搬运</returns>
    public bool EnsureLyricsDocked(Rectangle hostRect, out IntPtr lyricsHwnd)
    {
        lyricsHwnd = IntPtr.Zero;

        var hwnd = FindLyricsWindow();
        if (hwnd == IntPtr.Zero) return false;

        lyricsHwnd = hwnd;

        // 已经在承载区且已置顶，无需再动
        if (Native.TryGetWindowRect(hwnd, out var rect))
        {
            var inHost = rect.Left >= hostRect.Left - 60 && rect.Left <= hostRect.Right
                      && rect.Top >= hostRect.Top - 60 && rect.Top <= hostRect.Bottom + 60;
            var topMost = (Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0;
            if (inHost && topMost) return false;
        }

        return MoveLyricsToHost(hwnd, hostRect);
    }

    private static bool IsTopMost(IntPtr hwnd)
    {
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        return (ex.ToInt64() & Native.WS_EX_TOPMOST) != 0;
    }
}
