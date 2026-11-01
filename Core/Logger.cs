using System.Text;

namespace ScreenGuard.Core;

/// <summary>滚动日志：写入 E 盘 data 目录，同时输出到调试窗口。</summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string _logPath = string.Empty;

    public static void Init()
    {
        try
        {
            Deployment.EnsureDataRoot();
            _logPath = Path.Combine(Deployment.DataRoot,
                $"screenguard-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _logPath = string.Empty;
        }
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            System.Diagnostics.Debug.WriteLine(line);
        }
        catch { }

        lock (_lock)
        {
            if (string.IsNullOrEmpty(_logPath)) return;

            // 限流：同一消息在 5 秒内重复出现超过 20 次则折叠，
            // 防止异常场景下日志刷爆磁盘。
            var now = DateTime.Now;
            if (_recent.TryGetValue(message, out var last) && now - last < TimeSpan.FromSeconds(5))
            {
                _repeatCount[message] = _repeatCount.GetValueOrDefault(message) + 1;
                if (_repeatCount[message] > 20)
                {
                    if (_repeatCount[message] == 21)
                        Append(line + " (重复消息已折叠)");
                    return;
                }
            }
            else
            {
                _recent[message] = now;
                _repeatCount[message] = 0;
            }

            Append(line);
        }
    }

    private static readonly Dictionary<string, DateTime> _recent = new();
    private static readonly Dictionary<string, int> _repeatCount = new();

    private static void Append(string line)
    {
        try
        {
            File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}
