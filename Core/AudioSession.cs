using System.Runtime.InteropServices;

namespace ScreenGuard.Core;

/// <summary>
/// Core Audio (MMDeviceAPI) COM 互操作封装。
/// 用途：
///  1) 识别"当前正在发声"的进程 —— 用于判定休息前是否有背景音乐在播放（音乐放行）。
///  2) 校验 B 站视频是否已被真正暂停（音频会话变为非 Active）。
/// </summary>
internal static class AudioSession
{
    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    private enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrEndpointId, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("77BAA669-1B6F-4A3B-9EEB-899E9F38D3E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager 基类部分
        int GetAudioSessionControl(ref Guid audioSessionGuid, int flags, out IntPtr session);
        int GetSimpleAudioVolume(ref Guid audioSessionGuid, int flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        // IAudioSessionManager2 扩展部分
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string duckId, ref Guid sessionId, out IntPtr volume);
        int UnregisterAllDuckNotifications();
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA0125DEE6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionIndex, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl 基类部分
        int GetState(out int sessionState);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string sessionDisplayName);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string sessionDisplayName, ref Guid sessionGuid);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid sessionGuid);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid value);
        int RegisterAudioSessionNotification(IntPtr notification);
        int UnregisterAudioSessionNotification(IntPtr notification);
        // IAudioSessionControl2 扩展部分
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceIdentifier);
        int GetProcessId(out uint processId);
    }

    public sealed record AudioProcess(uint ProcessId, string ProcessName, bool IsActive);

    /// <summary>枚举当前所有音频会话对应的进程及其播放状态。</summary>
    public static List<AudioProcess> EnumerateAudioProcesses()
    {
        var result = new List<AudioProcess>();
        IntPtr enumeratorPtr = IntPtr.Zero;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;

        try
        {
            var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) != 0 || device == null)
                return result;

            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) != 0 || obj == null)
                return result;

            manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null)
                return result;

            if (sessions.GetCount(out var count) != 0) return result;

            for (var i = 0; i < count; i++)
            {
                try
                {
                    if (sessions.GetSession(i, out var session) != 0 || session == null) continue;

                    session.GetState(out var state);
                    session.GetProcessId(out var pid);
                    if (pid == 0) continue;

                    var name = ResolveProcessName(pid);
                    result.Add(new AudioProcess(pid, name, state == (int)AudioSessionState.Active));
                }
                catch
                {
                    // 单个会话读取失败不影响整体
                }
            }
        }
        catch
        {
            // 音频子系统不可用时返回空列表
        }
        finally
        {
            if (sessions != null) Marshal.ReleaseComObject(sessions);
            if (manager != null) Marshal.ReleaseComObject(manager);
            if (enumeratorPtr != IntPtr.Zero) Marshal.Release(enumeratorPtr);
        }

        return result;
    }

    /// <summary>获取指定进程集合中"正在发声"（音频会话 Active）的进程名集合。</summary>
    public static HashSet<string> GetActivelyPlayingProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in EnumerateAudioProcesses())
        {
            if (p.IsActive && !string.IsNullOrEmpty(p.ProcessName))
                set.Add(p.ProcessName);
        }
        return set;
    }

    public static string ResolveProcessName(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
