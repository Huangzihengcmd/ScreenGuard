using ScreenGuard.Core;
using ScreenGuard.UI;

namespace ScreenGuard;

/// <summary>
/// 端到端验证：真实进入休息状态 → 搬运歌词 → 截真实屏幕 → 检查歌词是否可见。
/// 休息时长很短（默认 25 秒），验证完自动退出。
/// 用法: ScreenGuard.exe --e2e
/// </summary>
internal static class E2ETest
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
            log($"[E2E] {mark} {name} {detail}");
            if (!ok) failures++;
        }

        var config = AppConfig.Load().Normalize();
        config.WorkMinutes = 1;
        config.RestMinutes = 1;
        config.AllowEarlyExit = true;

        var guard = new ProcessGuard(config, log);
        guard.Reload();
        var media = new MediaController(config, guard, log);
        var enforcer = new WindowEnforcer(guard, log);
        var placer = new AllowedWindowPlacer(guard, log);

        Console.WriteLine("=== 端到端验证（真实进入休息）===");
        Console.WriteLine();

        // 找歌词窗口
        var lyrics = placer.FindLyricsWindow();
        Check("找到音乐桌面歌词窗口", lyrics != IntPtr.Zero,
            lyrics != IntPtr.Zero ? $"句柄 0x{lyrics.ToInt64():X}" : "EchoMusic 未运行或无歌词窗");

        // 构造休息界面
        using var screen = new RestScreen(config, () => { }, log);
        screen.StartPosition = FormStartPosition.Manual;
        screen.Bounds = new Rectangle(0, 0,
            SystemInformation.VirtualScreen.Width, SystemInformation.VirtualScreen.Height);
        screen.Show();
        screen.BeginRest(DateTime.Now.AddSeconds(25), TimeSpan.FromSeconds(25),
            "音乐运行中 · EchoMusic（可自由操作，程序不干预）\r\n浏览器已锁定　｜　DEVECO CODE 及编程软件进程存活但不可操作");
        Application.DoEvents();
        Thread.Sleep(400);

        // 搬运歌词
        if (lyrics != IntPtr.Zero)
        {
            var moved = placer.MoveLyricsToHost(lyrics, screen.LyricsHostRect);
            Check("歌词窗口已搬运", moved,
                $"承载区 ({screen.LyricsHostRect.Left},{screen.LyricsHostRect.Top}) " +
                $"({screen.LyricsHostRect.Right},{screen.LyricsHostRect.Bottom})");
            Application.DoEvents();
            Thread.Sleep(500);
        }

        // 置顶音乐窗口
        var raised = placer.RaiseAllowedWindowsAbove(screen.Handle);
        Check("音乐窗口置顶", true, $"{raised} 个");

        // 执行真实管控：隐藏受管控窗口（模拟器错误框、UWP 宿主等）
        enforcer.MinimizeAllExceptAllowed();
        for (int i = 0; i < 6; i++)
        {
            enforcer.EnforceSuppressed();
            placer.KeepRestScreenOnTop(screen.Handle);
            Application.DoEvents();
            Thread.Sleep(300);
        }
        Check("已隐藏受管控窗口", true);

        Application.DoEvents();
        Thread.Sleep(800);

        // 抓真实屏幕
        var vs = SystemInformation.VirtualScreen;
        using (var bmp = new Bitmap(vs.Width, vs.Height))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(vs.Location, Point.Empty, new Size(vs.Width, vs.Height));
            }
            bmp.Save("E:\\ScreenGuard\\data\\e2e.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        Check("已截取真实屏幕", true, "data\\e2e.png");

        // 检查歌词承载区是否有内容（歌词是浅色文字）
        var host = screen.LyricsHostRect;
        using (var bmp = new Bitmap(vs.Width, vs.Height))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(vs.Location, Point.Empty, new Size(vs.Width, vs.Height));
            }
            var bright = 0;
            for (int py = host.Top; py < Math.Min(host.Bottom, vs.Height); py += 2)
            {
                for (int px = host.Left; px < Math.Min(host.Right, vs.Width); px += 2)
                {
                    var c = bmp.GetPixel(px, py);
                    if (c.R + c.G + c.B > 200) bright++;
                }
            }
            Check("歌词承载区显示出了歌词", bright > 10,
                $"承载区内检测到 {bright} 个亮像素");
        }

        // 清理
        placer.RestoreAll();
        screen.Close();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "端到端验证通过。" : $"{failures} 项未通过。");
        return failures == 0 ? 0 : 1;
    }
}
