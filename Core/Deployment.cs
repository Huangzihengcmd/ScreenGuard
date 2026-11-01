using System.Diagnostics;

namespace ScreenGuard.Core;

/// <summary>
/// 部署守卫：程序只允许运行在 E 盘，禁止安装/运行于 C 盘。
/// 启动时自检 exe 所在盘符，不合规直接拒绝启动。
/// </summary>
public static class Deployment
{
    /// <summary>允许的部署盘符。</summary>
    public const string RequiredDrive = "E";

    /// <summary>程序安装根目录（E:\ScreenGuard）。</summary>
    public static string InstallRoot { get; } = @"E:\ScreenGuard";

    /// <summary>所有可写数据（配置/日志）的根目录，始终位于 E 盘。</summary>
    public static string DataRoot { get; } = @"E:\ScreenGuard\data";

    /// <summary>当前 exe 是否位于合规盘符（E 盘）。</summary>
    public static bool IsValidLocation()
    {
        var exePath = GetExecutablePath();
        if (string.IsNullOrEmpty(exePath)) return false;
        return IsOnRequiredDrive(exePath);
    }

    public static bool IsOnRequiredDrive(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            return string.Equals(root.TrimEnd('\\'), RequiredDrive + ":", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string GetExecutablePath()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.MainModule?.FileName ?? Environment.ProcessPath ?? string.Empty;
        }
        catch
        {
            return Environment.ProcessPath ?? string.Empty;
        }
    }

    public static void EnsureDataRoot()
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
        }
        catch
        {
            // 目录创建失败时退回 exe 同级目录
        }
    }
}
