using System.Diagnostics;

namespace ScreenGuard.Core;

/// <summary>
/// 进程守卫：休息期间的管控策略。
///
/// 【策略原则 —— 按用户原意修订】
///  1. 音乐：完全放行。不终止、不最小化、不拦截激活、不暂停。用户想关就自己关，
///     关掉后播放器若自己在后台复活，那是播放器自身行为，程序不干预。
///  2. 编程类软件：完全放行。绝不终止、绝不最小化、绝不干扰其运行。
///  3. 管理员白名单（DEVECO CODE 等）：完全放行，优先级最高。
///  4. 浏览器：不再终止进程（避免误伤用户编辑中的网页、丢失会话），
///     改为"最小化窗口 + 拦截激活"，使其不可被操作。
/// </summary>
public sealed class ProcessGuard
{
    private readonly AppConfig _config;
    private readonly Action<string> _log;

    public ProcessGuard(AppConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    private HashSet<string> _protectedNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _browserNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _musicNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _bilibiliNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _devNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>剥离 .exe 后缀，与 Process.ProcessName 形态一致。</summary>
    internal static string StripExe(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    public void Reload()
    {
        _protectedNames = new HashSet<string>(_config.ProtectedProcesses.Select(StripExe), StringComparer.OrdinalIgnoreCase);
        _browserNames = new HashSet<string>(_config.BrowserProcesses.Select(StripExe), StringComparer.OrdinalIgnoreCase);
        _musicNames = new HashSet<string>(_config.MusicProcesses.Select(StripExe), StringComparer.OrdinalIgnoreCase);
        _bilibiliNames = new HashSet<string>(_config.BilibiliProcesses.Select(StripExe), StringComparer.OrdinalIgnoreCase);
        _devNames = new HashSet<string>(_config.DevProcesses.Select(StripExe), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>管理员白名单判定（优先级最高）。</summary>
    public bool IsProtected(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        return _protectedNames.Contains(StripExe(processName));
    }

    /// <summary>音乐进程判定 —— 放行对象。</summary>
    public bool IsMusic(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        return _musicNames.Contains(StripExe(processName));
    }

    /// <summary>编程类软件判定 —— 放行对象，绝不干扰。</summary>
    public bool IsDevTool(string? processName, string? exePath = null)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (_devNames.Contains(StripExe(processName))) return true;
        return DevToolDetector.IsDevProcess(processName, exePath);
    }

    public bool IsBrowser(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (IsDevTool(processName)) return false; // 编程软件即使名字像浏览器也不当浏览器处理
        return _browserNames.Contains(StripExe(processName));
    }

    public bool IsBilibiliClient(string? processName)
        => !string.IsNullOrEmpty(processName) && _bilibiliNames.Contains(StripExe(processName));

    /// <summary>
    /// 判断某进程是否应被完全放行（不受任何休息管控影响）。
    /// 放行顺序：管理员白名单 → 音乐 → 编程类软件。
    /// </summary>
    public bool IsFullyAllowed(string? processName, string? exePath = null)
    {
        if (IsProtected(processName)) return true;
        if (IsMusic(processName)) return true;
        if (IsDevTool(processName, exePath)) return true;
        return false;
    }

    /// <summary>
    /// 编程类软件 / 管理员白名单：进程必须活着，窗口保留，但**不可操作**。
    ///
    /// 【用户明确要求】"我是让他进程活着不能操作而已"
    /// —— 编译、构建等后台任务不能被打断，但人不能借机继续写代码。
    /// </summary>
    public bool IsAliveOnly(string? processName, string? exePath = null)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (IsMusic(processName)) return false;         // 音乐是完全放行，不是 alive-only
        if (IsProtected(processName)) return true;      // 管理员白名单
        return IsDevTool(processName, exePath);
    }

    /// <summary>
    /// 音乐进程：完全放行 —— 可听、可切歌、可调音量，窗口不最小化。
    /// </summary>
    public bool IsInteractiveAllowed(string? processName)
        => IsMusic(processName);

    /// <summary>
    /// 浏览器管控：不终止进程，仅返回需要最小化的浏览器进程名。
    /// 保留进程是为了不打断用户正在编辑的网页与浏览器会话。
    /// </summary>
    public List<string> CollectBrowsersToSuppress()
    {
        var list = new List<string>();
        foreach (var proc in SafeGetProcesses())
        {
            try
            {
                if (proc.HasExited) continue;
                var name = proc.ProcessName;
                if (!IsBrowser(name)) continue;
                if (IsFullyAllowed(name, SafePath(proc))) continue;
                if (!list.Contains(name)) list.Add(name);
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }
        return list;
    }

    /// <summary>
    /// 休息开始时快照已存在的浏览器进程。
    /// 【按用户原意修订】浏览器不再被终止，快照仅用于状态展示与后续日志记录。
    /// </summary>
    public void CapturePreExistingBrowsers()
    {
        _preExistingBrowserPids.Clear();
        try
        {
            foreach (var proc in SafeGetProcesses())
            {
                try
                {
                    if (proc.HasExited) continue;
                    if (IsBrowser(proc.ProcessName))
                        _preExistingBrowserPids.Add(proc.Id);
                }
                catch { }
                finally { try { proc.Dispose(); } catch { } }
            }
        }
        catch { }
        _log($"[ProcessGuard] 已快照 {_preExistingBrowserPids.Count} 个既有浏览器进程（保留进程，仅压制窗口）");
    }

    /// <summary>休息结束，清理快照状态。</summary>
    public void ClearBrowserSnapshot()
    {
        _preExistingBrowserPids.Clear();
    }

    private readonly HashSet<int> _preExistingBrowserPids = new();

    /// <summary>确认受保护 / 放行类进程仍然存活且未被干扰。</summary>
    public List<string> VerifyAllowedAlive()
    {
        var alive = new List<string>();
        foreach (var proc in SafeGetProcesses())
        {
            try
            {
                if (proc.HasExited) continue;
                var name = proc.ProcessName;
                if (IsFullyAllowed(name, SafePath(proc)))
                    alive.Add(name);
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }
        return alive.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>获取某进程的 exe 路径，失败返回 null。</summary>
    internal static string? SafePath(Process proc)
    {
        try { return proc.MainModule?.FileName; }
        catch { return null; }
    }

    public static Process[] SafeGetProcesses()
    {
        try { return Process.GetProcesses(); }
        catch { return Array.Empty<Process>(); }
    }
}
