using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenGuard.Core;

/// <summary>应用配置。持久化在 exe 同级 config.json。</summary>
public sealed class AppConfig
{
    /// <summary>连续使用时长（分钟），到达后强制休息。</summary>
    public int WorkMinutes { get; set; } = 45;

    /// <summary>休息时长（分钟）。</summary>
    public int RestMinutes { get; set; } = 10;

    /// <summary>是否启用自动启动。</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>休息界面是否允许提前结束（默认不允许，符合"强制"语义）。</summary>
    public bool AllowEarlyExit { get; set; } = false;

    /// <summary>受保护进程名（白名单），这些进程永不被终止/干扰。DEVECO CODE 在其中。</summary>
    public List<string> ProtectedProcesses { get; set; } = new()
    {
        "deveco.exe",
        "deveco",
    };

    /// <summary>受保护进程路径片段（命中即保护，优先级高于黑名单）。</summary>
    public List<string> ProtectedPathHints { get; set; } = new()
    {
        "@deveco",
        "deveco-code",
    };

    /// <summary>浏览器进程名，休息期间禁止启动/打开。</summary>
    public List<string> BrowserProcesses { get; set; } = new()
    {
        "msedge.exe",
        "chrome.exe",
        "firefox.exe",
        "iexplore.exe",
        "360se.exe",
        "360ee.exe",
        "360chrome.exe",
        "browser.exe",
        "qqbrowser.exe",
        "sogouexplorer.exe",
        "maxthon.exe",
        "opera.exe",
        "brave.exe",
        "vivaldi.exe",
        "quarkbrowser.exe",
        "liebao.exe",
        "ucbrowser.exe",
        "theworld.exe",
    };

    /// <summary>音乐播放器进程名，休息期间完全放行（不终止、不最小化、不拦截）。</summary>
    public List<string> MusicProcesses { get; set; } = new()
    {
        "EchoMusic.exe",
        "cloudmusic.exe",
        "qqmusic.exe",
        "qqmusicplayer.exe",
        "spotify.exe",
        "AIMP.exe",
        "foobar2000.exe",
        "kmplayer.exe",
        "PotPlayerMini64.exe",
        "PotPlayerMini.exe",
        "vlc.exe",
        "Kugou.exe",
        "KuGou.exe",
        "Kuwo.exe",
        "711.exe",
        "MusicBee.exe",
        "foobar.exe",
        "winamp.exe",
        "itunes.exe",
        "Rhythmbox.exe",
        "strawberry.exe",
        "Dopamine.exe",
        "tartube.exe",
    };

    /// <summary>
    /// 编程类软件进程名，休息期间完全放行 —— 绝不终止、绝不最小化、绝不干扰。
    /// 内置已覆盖 IDE / 编辑器 / 终端 / 构建工具 / 运行时 / 数据库 等，此处可自行补充。
    /// </summary>
    public List<string> DevProcesses { get; set; } = new()
    {
        "deveco.exe",
        "Code.exe",
        "idea64.exe",
        "pycharm64.exe",
        "webstorm64.exe",
        "goland64.exe",
        "rider64.exe",
        "devenv.exe",
        "studio64.exe",
        "notepad++.exe",
        "Sublime_text.exe",
        "nvim.exe",
        "node.exe",
        "python.exe",
        "java.exe",
        "javaw.exe",
        "dotnet.exe",
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "WindowsTerminal.exe",
        "hdc.exe",
        "editor_sdk.exe",
    };

    /// <summary>哔哩哔哩客户端进程名（独立客户端，非浏览器）。</summary>
    public List<string> BilibiliProcesses { get; set; } = new()
    {
        "bili.exe",
        "bilibili.exe",
        "BiliBili.exe",
        "哔哩哔哩.exe",
    };

    /// <summary>是否在休息期间隐藏桌面壁纸/图标（额外收敛视觉干扰）。</summary>
    public bool HideDesktopIcons { get; set; } = true;

    [JsonIgnore]
    public string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg != null) return cfg.Normalize();
            }
        }
        catch
        {
            // 配置损坏时回落到默认值
        }
        return new AppConfig().Normalize();
    }

    public void Save()
    {
        try
        {
            var path = ConfigPath;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 持久化失败不应中断运行
        }
    }

    /// <summary>修正非法值并确保关键白名单存在。</summary>
    public AppConfig Normalize()
    {
        WorkMinutes = Math.Clamp(WorkMinutes, 1, 24 * 60);
        RestMinutes = Math.Clamp(RestMinutes, 1, 24 * 60);

        // 统一进程名格式：Process.ProcessName 不含 ".exe" 后缀，
        // 因此所有名单在此统一剥离后缀并去重，避免比对失败。
        ProtectedProcesses = NormalizeNames(ProtectedProcesses);
        ProtectedPathHints = NormalizeNames(ProtectedPathHints);
        BrowserProcesses = NormalizeNames(BrowserProcesses);
        MusicProcesses = NormalizeNames(MusicProcesses);
        BilibiliProcesses = NormalizeNames(BilibiliProcesses);
        DevProcesses = NormalizeNames(DevProcesses);

        // DEVECO CODE 必须始终在白名单里，这是硬性要求。
        if (!ProtectedProcesses.Contains("deveco"))
            ProtectedProcesses.Add("deveco");
        if (!ProtectedPathHints.Any(p => p.Contains("deveco", StringComparison.OrdinalIgnoreCase)))
            ProtectedPathHints.Add("@deveco");

        return this;
    }

    /// <summary>剥离 .exe 后缀、去空、去重（忽略大小写）。</summary>
    private static List<string> NormalizeNames(List<string>? names)
    {
        var result = new List<string>();
        if (names == null) return result;

        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var n = raw.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                n = n[..^4];
            n = n.Trim();
            if (n.Length == 0) continue;
            if (!result.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)))
                result.Add(n);
        }
        return result;
    }
}
