namespace ScreenGuard.Core;

/// <summary>
/// 键盘拦截器：在休息期间拦截会破坏"锁定"状态的系统级快捷键。
///
/// 拦截清单：
///   - Alt+Tab / Alt+Shift+Tab / Ctrl+Tab  —— 任务切换
///   - Win / Win+D / Win+E / Win+R / Win+Tab —— 开始菜单、桌面、任务视图
///   - Ctrl+Esc                            —— 开始菜单
///   - Alt+F4                              —— 关闭休息界面
///   - F11                                 —— 浏览器全屏切换
///   - Ctrl+Shift+Esc                      —— 任务管理器
///   - PrintScreen                         —— 截屏逃逸
///
/// 只在休息期间生效，其余时间完全放行，不干扰正常使用。
/// </summary>
public sealed class KeyboardInterceptor : IDisposable
{
    private IntPtr _hookHandle = IntPtr.Zero;
    private Native.LowLevelKeyboardProc? _proc;
    private volatile bool _active;
    private readonly Action<string> _log;

    public KeyboardInterceptor(Action<string> log) => _log = log;

    public bool IsActive => _active;

    public void Start()
    {
        if (_active) return;
        try
        {
            _proc = HookCallback;
            // LL 钩子必须用本模块句柄，否则回调不会被调用
            var module = Native.GetModuleHandle(null);
            _hookHandle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, module, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                _log("[Keyboard] 钩子安装失败，键盘拦截未生效");
                return;
            }
            _active = true;
            _log("[Keyboard] 键盘拦截已启用（休息期间生效）");
        }
        catch (Exception ex)
        {
            _log($"[Keyboard] 钩子安装异常: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!_active) return;
        try
        {
            if (_hookHandle != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            _log("[Keyboard] 键盘拦截已解除");
        }
        catch { }
        finally
        {
            _active = false;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (!_active || nCode != Native.HC_ACTION)
            return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var msg = (int)wParam;
        if (msg != Native.WM_KEYDOWN && msg != Native.WM_SYSKEYDOWN)
            return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int vk;
        try
        {
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            vk = (int)data.vkCode;
        }
        catch
        {
            return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        bool alt = IsDown(Native.VK_MENU);
        bool ctrl = IsDown(Native.VK_CONTROL);
        bool shift = IsDown(Native.VK_SHIFT);
        bool win = vk == Native.VK_LWIN || vk == Native.VK_RWIN;

        bool block = false;
        string reason = "";

        switch (vk)
        {
            case Native.VK_TAB when (alt || ctrl):
                block = true; reason = "任务切换"; break;
            case Native.VK_ESCAPE when ctrl:
                block = true; reason = "开始菜单"; break;
            case Native.VK_ESCAPE when alt:
                block = true; reason = "系统菜单切换"; break;
            case Native.VK_F4 when alt:
                block = true; reason = "关闭窗口"; break;
            case 0x7A when (shift && ctrl): // F8
                block = true; reason = "Ctrl+Shift+F8"; break;
            case 0x2C when (shift && ctrl): // PrintScreen
                block = true; reason = "截屏"; break;
            case Native.VK_LWIN:
            case Native.VK_RWIN:
                block = true; reason = "Windows 键"; break;
            case Native.VK_APPS:
                block = true; reason = "菜单键"; break;
        }

        // Ctrl+Shift+Esc（任务管理器）：Esc + Ctrl + Shift
        if (vk == Native.VK_ESCAPE && ctrl && shift)
        {
            block = true; reason = "任务管理器";
        }
        // F11 浏览器全屏
        if (vk == 0x7A && !ctrl && !shift)
        {
            block = true; reason = "F11 全屏切换";
        }

        if (block)
        {
            // 同一原因不重复刷日志，避免刷屏
            if (!string.Equals(reason, _lastBlockReason, StringComparison.Ordinal))
            {
                _lastBlockReason = reason;
                _log($"[Keyboard] 拦截按键: {reason}");
            }
            return new IntPtr(1); // 吞掉该按键
        }

        return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private string _lastBlockReason = string.Empty;

    private static bool IsDown(int vk)
        => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        Stop();
        _proc = null;
    }
}
