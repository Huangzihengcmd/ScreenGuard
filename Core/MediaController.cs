using System.Diagnostics;

namespace ScreenGuard.Core;

/// <summary>
/// 媒体控制器：处理休息期间的 B 站视频暂停与音乐放行。
///
/// 关键点：
///  - B 站在浏览器（Edge）里播放时，无法直接"点击暂停按钮"而不干扰浏览器进程。
///    方案：向浏览器窗口发送 APPCOMMAND_MEDIA_PLAY_PAUSE 消息，这是 Windows 标准的
///    媒体控制命令，浏览器会把它交给当前正在播放的媒体会话，从而暂停视频，
///    但浏览器进程与页面完全保留（满足"保留进程不被关闭"）。
///  - 若 B 站是独立客户端进程，则直接向其窗口发送同样的媒体命令。
///  - 音乐放行：不对音乐进程做任何终止/静音操作，并在休息前记录哪些音乐进程在播，
///    休息期间持续校验其仍在发声，若被意外暂停则尝试恢复。
/// </summary>
public sealed class MediaController
{
    private readonly AppConfig _config;
    private readonly ProcessGuard _guard;
    private readonly Action<string> _log;

    public MediaController(AppConfig config, ProcessGuard guard, Action<string> log)
    {
        _config = config;
        _guard = guard;
        _log = log;
    }

    /// <summary>
    /// 休息开始时快照：音乐进程是否在运行。
    ///
    /// 【按用户原意修订】只做"观测"，绝不做任何干预：
    ///  - 不暂停音乐、不恢复播放、不静音、不终止
    ///  - 音乐窗口不最小化、不拦截激活（见 WindowEnforcer）
    ///  - 播放器关掉后自己在后台复活，是播放器自身行为，程序不干预。
    ///
    /// 判定采用双路：
    ///  1. 进程是否存活（主要依据 —— 可靠）
    ///  2. 是否有活跃音频会话（辅助 —— 部分播放器独占输出时可能探测不到）
    /// 只要进程在，就认定"音乐在运行"，避免误报"未在播放"。
    /// </summary>
    public HashSet<string> CaptureMusicPlaybackState()
    {
        try
        {
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proc in SafeGetProcesses())
            {
                try
                {
                    if (proc.HasExited) continue;
                    var name = proc.ProcessName;
                    if (_guard.IsMusic(name)) running.Add(name);
                }
                catch { }
                finally { try { proc.Dispose(); } catch { } }
            }

            var playing = AudioSession.GetActivelyPlayingProcessNames();
            var audible = new HashSet<string>(running.Where(playing.Contains), StringComparer.OrdinalIgnoreCase);

            if (running.Count > 0)
            {
                _log($"[Media] 音乐进程运行中: {string.Join(", ", running)}"
                     + (audible.Count > 0 ? $"（其中正在发声: {string.Join(", ", audible)}）" : "（未探测到音频会话，按运行中处理）")
                     + " —— 完全放行，程序不做任何干预");
            }
            else
            {
                _log("[Media] 当前无音乐进程运行");
            }
            return running;
        }
        catch (Exception ex)
        {
            _log($"[Media] 音乐状态采样失败: {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 暂停正在播放的 B 站视频，但保留其进程。
    /// 返回是否成功发出暂停指令。
    ///
    /// 判定依据（按可靠性排序）：
    ///  1. 浏览器进程存在活跃音频会话 —— 浏览器正在发声即说明有媒体在播放，
    ///     这与 B 站视频在哪个标签页无关，比窗口标题可靠（用户切到别的标签页时标题会变）。
    ///  2. 窗口标题含 bilibili / 哔哩哔哩 —— 精确命中当前前台标签页。
    /// 两条都命中时都发暂停指令，WM_APPCOMMAND 作用于浏览器当前的媒体会话。
    /// </summary>
    public bool PauseBilibiliVideo()
    {
        var handled = false;

        // 情况一：B 站独立客户端
        foreach (var proc in SafeGetProcesses())
        {
            try
            {
                var name = proc.ProcessName;
                if (!_guard.IsBilibiliClient(name)) continue;
                if (_guard.IsProtected(name)) continue;

                var hwnd = MainWindowHandleOf(proc);
                if (hwnd == IntPtr.Zero) continue;

                SendMediaPause(hwnd);
                _log($"[Media] 已向 B 站客户端发送暂停指令（进程保留）: {name} (PID {proc.Id})");
                handled = true;
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }

        // 情况二：浏览器中的 B 站
        var activeBrowserAudio = GetBrowserProcessesWithActiveAudio();
        foreach (var (hwnd, title, procName) in EnumerateTopLevelWindows())
        {
            try
            {
                if (!IsBrowserProcess(procName)) continue;

                // 音乐进程与编程软件不碰（用户可能用 IDE 内置浏览器看 B 站，也可能是音乐）
                if (_guard.IsMusic(procName) || _guard.IsDevTool(procName)) continue;
                var titleMatch = LooksLikeBilibili(title);
                var audioMatch = activeBrowserAudio.Contains(procName);

                if (!titleMatch && !audioMatch) continue;

                SendMediaPause(hwnd);
                _log($"[Media] 已向浏览器窗口发送暂停指令（进程保留）: {procName} | {Trim(title)}"
                     + $" [命中: {(titleMatch ? "标题" : "")}{(titleMatch && audioMatch ? "+" : "")}{(audioMatch ? "音频会话" : "")}]");
                handled = true;
            }
            catch { }
        }

        if (!handled)
            _log("[Media] 未检测到正在播放的 B 站视频");

        return handled;
    }

    /// <summary>获取当前存在活跃音频会话的浏览器进程名集合（说明浏览器正在发声）。</summary>
    private HashSet<string> GetBrowserProcessesWithActiveAudio()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in AudioSession.EnumerateAudioProcesses())
            {
                if (p.IsActive && IsBrowserProcess(p.ProcessName))
                    set.Add(p.ProcessName);
            }
        }
        catch (Exception ex)
        {
            _log($"[Media] 音频会话枚举失败: {ex.Message}");
        }
        return set;
    }

    /// <summary>
    /// 校验休息期间音乐仍在运行。
    ///
    /// 【按用户原意修订 —— 关键】
    /// 之前的实现会在检测到音频会话静默时"自动恢复播放"，这导致：
    ///  用户手动关闭音乐后，程序又把播放器唤起，表现为"关不掉 / 关了又复活"。
    /// 现在改为纯观测：只检查进程是否还活着，不做任何恢复动作。
    /// 用户想关就自己关，程序不管。
    /// </summary>
    public bool EnsureMusicContinues(HashSet<string> musicProcesses)
    {
        if (musicProcesses.Count == 0) return true;

        var aliveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in SafeGetProcesses())
        {
            try
            {
                if (!proc.HasExited) aliveNames.Add(proc.ProcessName);
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }

        var allAlive = true;
        foreach (var name in musicProcesses)
        {
            // 播放器已被用户关闭 —— 这是用户的意愿，程序尊重，不做任何恢复
            if (!aliveNames.Contains(name))
                _log($"[Media] 音乐进程 {name} 已被用户关闭，程序不做任何恢复操作");
            else
                allAlive = true;
        }
        return allAlive;
    }

    /// <summary>
    /// 查找可用于发送媒体控制命令的音乐窗口。
    /// 优先选桌面歌词窗（响应 APPCOMMAND 更积极），找不到则用主窗口。
    /// </summary>
    public IntPtr FindMusicCommandWindow()
    {
        IntPtr best = IntPtr.Zero;
        var bestArea = long.MaxValue;

        foreach (var (hwnd, title, procName) in EnumerateTopLevelWindows())
        {
            try
            {
                if (!_guard.IsMusic(procName)) continue;
                if (!Native.TryGetWindowRect(hwnd, out var rect)) continue;
                var area = (long)rect.Width * rect.Height;
                if (area > 0 && area < bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }
            }
            catch { }
        }
        return best;
    }

    private void SendMediaPause(IntPtr hwnd)
    {
        // WM_APPCOMMAND + APPCOMMAND_MEDIA_PLAY_PAUSE：标准媒体播放/暂停控制
        Native.SendMessage(hwnd, Native.WM_APPCOMMAND, IntPtr.Zero,
            new IntPtr(Native.APPCOMMAND_MEDIA_PLAY_PAUSE << 16));
    }

    private static bool LooksLikeBilibili(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        var t = title.ToLowerInvariant();
        return t.Contains("bilibili") || t.Contains("哔哩哔哩") || t.Contains("b站");
    }

    private bool IsBrowserProcess(string processName)
    {
        if (_guard.IsBrowser(processName)) return true;
        // Edge / Chrome 的进程名可能带变体
        return processName.StartsWith("msedge", StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith("chrome", StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith("firefox", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string s)
        => s.Length <= 60 ? s : s[..60] + "...";

    private static IntPtr MainWindowHandleOf(Process proc)
    {
        try { return proc.MainWindowHandle; }
        catch { return IntPtr.Zero; }
    }

    public static Process[] SafeGetProcesses()
    {
        try { return Process.GetProcesses(); }
        catch { return Array.Empty<Process>(); }
    }

    /// <summary>枚举所有可见的顶层窗口，返回 (句柄, 标题, 进程名)。</summary>
    public static List<(IntPtr Hwnd, string Title, string ProcessName)> EnumerateTopLevelWindows()
    {
        var list = new List<(IntPtr, string, string)>();
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd)) return true;

                var sb = new System.Text.StringBuilder(512);
                Native.GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                Native.GetWindowThreadProcessId(hwnd, out var pid);
                var name = AudioSession.ResolveProcessName(pid);
                if (string.IsNullOrEmpty(name)) return true;

                list.Add((hwnd, title, name));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
