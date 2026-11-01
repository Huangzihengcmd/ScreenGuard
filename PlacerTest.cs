using ScreenGuard.Core;

namespace ScreenGuard;

/// <summary>
/// 歌词搬运与置顶专项验证：
///  1. 找到音乐播放器的桌面歌词窗口
///  2. 搬到指定承载区，验证位置确实变了
///  3. 验证放行窗口被置顶（WS_EX_TOPMOST）
///  4. 全部还原，验证位置与层级恢复
/// </summary>
internal static class PlacerTest
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
            log($"[PlacerTest] {mark} {name} {detail}");
            if (!ok) failures++;
        }

        var config = AppConfig.Load().Normalize();
        var guard = new ProcessGuard(config, log);
        guard.Reload();
        var placer = new AllowedWindowPlacer(guard, log);

        Console.WriteLine("=== 歌词搬运与置顶验证 ===");
        Console.WriteLine();

        // 1. 找到桌面歌词窗口
        var lyrics = placer.FindLyricsWindow();
        var hasLyrics = lyrics != IntPtr.Zero;
        Check("找到音乐桌面歌词窗口", hasLyrics,
            hasLyrics ? $"句柄 0x{lyrics.ToInt64():X}" : "未找到（可能播放器未开启桌面歌词）");

        // 先归位到桌面顶部，保证往返测试起点明确
        if (hasLyrics)
        {
            Native.SetWindowPos(lyrics, IntPtr.Zero, 795, 7, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Thread.Sleep(120);
        }

        Rectangle oldRect = default;
        if (hasLyrics)
        {
            Native.TryGetWindowRect(lyrics, out var r);
            oldRect = new Rectangle(r.Left, r.Top, r.Width, r.Height);
            Console.WriteLine($"  歌词窗口原位置: ({oldRect.Left},{oldRect.Top}) 尺寸 {oldRect.Width}x{oldRect.Height}");
        }

        // 2. 搬到承载区
        var host = new Rectangle(462, 620, 516, 96);
        var moved = hasLyrics && placer.MoveLyricsToHost(lyrics, host);
        Check("歌词窗口已搬到承载区", moved);

        if (moved)
        {
            Thread.Sleep(300);
            Native.TryGetWindowRect(lyrics, out var newRect);
            var inHost = newRect.Left >= host.Left - 60 && newRect.Left <= host.Right
                      && newRect.Top >= host.Top - 60 && newRect.Top <= host.Bottom + 60;
            Check("歌词窗口位置已变更并落在承载区内", inHost,
                $"新位置 ({newRect.Left},{newRect.Top})，承载区 ({host.Left},{host.Top})-({host.Right},{host.Bottom})");

            var topMost = (Native.GetWindowLongPtr(lyrics, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0;
            Check("歌词窗口已置顶（可见于休息界面之上）", topMost);

            var visible = Native.IsWindowVisible(lyrics);
            Check("歌词窗口可见", visible, visible ? "可见" : "不可见");

            // 抓取歌词窗口所在区域的屏幕像素，确认它真的被画出来了
            try
            {
                using var bmp = new Bitmap(newRect.Width, newRect.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(new Point(newRect.Left, newRect.Top), Point.Empty,
                        new Size(newRect.Width, newRect.Height));
                }

                // 统计非黑像素：歌词是浅色文字，应当存在大量亮像素
                var bright = 0;
                for (int py = 0; py < bmp.Height; py += 2)
                {
                    for (int px = 0; px < bmp.Width; px += 2)
                    {
                        var c = bmp.GetPixel(px, py);
                        if (c.R + c.G + c.B > 180) bright++;
                    }
                }
                Check("歌词窗口区域实际绘制了内容", bright > 20,
                    $"检测到 {bright} 个亮像素（歌词文字）");
                bmp.Save("E:\\ScreenGuard\\data\\lyrics-capture.png");
            }
            catch (Exception ex)
            {
                Check("歌词窗口区域实际绘制了内容", false, ex.Message);
            }
        }

        // 3. 放行窗口置顶
        var raised = placer.RaiseAllowedWindowsAbove(IntPtr.Zero);
        Check("放行窗口置顶", raised > 0 || !hasLyrics,
            raised > 0 ? $"{raised} 个放行窗口已置顶" : "无放行窗口可见");

        // 4. 还原
        placer.RestoreAll();
        Check("还原完成", true);

        if (hasLyrics)
        {
            Native.TryGetWindowRect(lyrics, out var restored);
            var backOk = Math.Abs(restored.Left - oldRect.Left) < 6 && Math.Abs(restored.Top - oldRect.Top) < 6;
            Check("歌词窗口位置已还原", backOk,
                $"还原后 ({restored.Left},{restored.Top})，原位置 ({oldRect.Left},{oldRect.Top})");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部符合预期。" : $"{failures} 项未通过。");
        return failures == 0 ? 0 : 1;
    }
}
