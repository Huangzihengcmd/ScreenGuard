using ScreenGuard.Core;
using ScreenGuard.UI;

namespace ScreenGuard;

/// <summary>
/// 界面预览：单独渲染休息界面并保存为 PNG，用于人工核对布局。
/// 用法: ScreenGuard.exe --preview [输出路径]
/// 该模式不进入全屏锁定，只在离屏位置渲染一张静态图。
/// </summary>
internal static class Preview
{
    public static int Run(string[] args)
    {
        Logger.Init();
        var outPath = "E:\\ScreenGuard\\data\\preview.png";
        var idx = Array.FindIndex(args, a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length) outPath = args[idx + 1];

        var config = AppConfig.Load().Normalize();
        config.AllowEarlyExit = true;

        var screen = SystemInformation.VirtualScreen;
        // 按 1920x1080 渲染
        var width = 1920;
        var height = 1080;

        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var scale = Math.Max(0.7, Math.Min(1.4, height / 1080.0));
            var contentW = (int)(1080 * scale);
            var cx = width / 2;

            var titleH = (int)(72 * scale);
            var gap1 = (int)(34 * scale);
            var subH = (int)(40 * scale);
            var gap2 = (int)(72 * scale);
            var ringH = (int)(270 * scale);
            var gap3 = (int)(46 * scale);
            var countH = (int)(116 * scale);
            var gap4 = (int)(40 * scale);
            var statusH = (int)(56 * scale);
            var gap5 = (int)(38 * scale);
            var lyricsH = (int)(92 * scale);
            var gap6 = (int)(16 * scale);
            var captionH = (int)(24 * scale);
            var gap7 = (int)(22 * scale);
            var barH = (int)(66 * scale);
            var gap8 = (int)(24 * scale);
            var hintH = (int)(28 * scale);

            var totalH = titleH + gap1 + subH + gap2 + ringH + gap3 + countH + gap4
                       + statusH + gap5 + lyricsH + gap6 + captionH + gap7 + barH + gap8 + hintH;
            var top = Math.Max((int)(24 * scale), (height - totalH) / 2);

            // 背景
            using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                       new Rectangle(0, 0, width, height),
                       Color.FromArgb(14, 16, 24), Color.FromArgb(30, 38, 60),
                       System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            {
                g.FillRectangle(bg, new Rectangle(0, 0, width, height));
            }

            void DrawText(string text, Font font, Color color, Rectangle rect)
            {
                using var f = font;
                TextRenderer.DrawText(g, text, f, rect, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            var y = top;
            DrawText("该休息一下了", new Font("Microsoft YaHei UI", (float)(46 * scale), FontStyle.Bold),
                Color.FromArgb(245, 247, 250), new Rectangle(cx - contentW / 2, y, contentW, titleH));
            y += titleH + gap1;

            DrawText("起来走两步，看看远处，让眼睛歇一会儿。", new Font("Microsoft YaHei UI", (float)(19 * scale)),
                Color.FromArgb(170, 182, 205), new Rectangle(cx - contentW / 2, y, contentW, subH));
            y += subH + gap2;

            // 进度环
            var ringSize = (int)(270 * scale);
            var ringRect = new Rectangle(cx - ringSize / 2, y, ringSize, ringSize);
            var glow = (int)(ringSize * 1.75);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddEllipse(ringRect.Left + ringSize / 2 - glow / 2,
                    ringRect.Top + ringSize / 2 - glow / 2, glow, glow);
                using var brush = new System.Drawing.Drawing2D.PathGradientBrush(path);
                brush.CenterColor = Color.FromArgb(52, 112, 180, 52);
                brush.SurroundColors = new Color[] { Color.FromArgb(0, 60, 120, 190) };
                g.FillPath(brush, path);
            }
            using (var pen = new Pen(Color.FromArgb(40, 50, 70), 8)) g.DrawEllipse(pen, ringRect);
            using (var pen = new Pen(Color.FromArgb(125, 205, 255), 8))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(pen, ringRect, -90, 360f * 0.62f);
            }
            y += ringH + gap3;

            DrawText("04:37", new Font("Consolas", (float)(92 * scale), FontStyle.Bold),
                Color.FromArgb(125, 205, 255), new Rectangle(cx - contentW / 2, y, contentW, countH));
            y += countH + gap4;

            DrawText("音乐运行中 · EchoMusic（可自由操作，程序不干预）\r\n浏览器已锁定　｜　DEVECO CODE 及编程软件可正常使用",
                new Font("Microsoft YaHei UI", (float)(16 * scale)), Color.FromArgb(152, 205, 178),
                new Rectangle(cx - contentW / 2, y, contentW, statusH));
            y += statusH + gap5;

            // 桌面歌词承载区
            var lyricsW = (int)(600 * scale);
            var lyricsRect = new Rectangle(cx - lyricsW / 2, y, lyricsW, lyricsH);
            using (var pen = new Pen(Color.FromArgb(55, 70, 100), 1))
            {
                g.DrawRectangle(pen, lyricsRect);
            }
            y += lyricsH + gap6;

            DrawText("桌面歌词显示区（音乐播放器歌词已移入此处）",
                new Font("Microsoft YaHei UI", (float)(13 * scale)), Color.FromArgb(130, 165, 205),
                new Rectangle(lyricsRect.Left, y, lyricsW, captionH));
            y += captionH + gap7;

            // 音乐控制条
            var barRect = new Rectangle(cx - contentW / 2, y, contentW, barH);
            using (var brush = new SolidBrush(Color.FromArgb(30, 36, 52)))
                g.FillRectangle(brush, barRect);

            var btnW = (int)(54 * scale);
            var btnH = (int)(42 * scale);
            var bx = barRect.Left + (int)(18 * scale);
            var by = barRect.Top + (int)(12 * scale);
            var labels = new[] { "⏮", "⏯", "⏭", "🔇", "🔉", "🔊" };
            foreach (var lb in labels)
            {
                using var brush = new SolidBrush(Color.FromArgb(46, 54, 74));
                g.FillRectangle(brush, new Rectangle(bx, by, btnW, btnH));
                DrawText(lb, new Font("Segoe UI Symbol", (float)(15 * scale)), Color.FromArgb(215, 228, 245),
                    new Rectangle(bx, by, btnW, btnH));
                bx += btnW + (int)(12 * scale);
            }
            y += barH + gap8;

            // 提示
            DrawText("按 Esc 可提前结束休息", new Font("Microsoft YaHei UI", (float)(14 * scale)),
                Color.FromArgb(118, 130, 152), new Rectangle(cx - contentW / 2, y, contentW, hintH));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"预览图已保存: {outPath}");
        Console.WriteLine($"画布尺寸: {width}x{height}");
        return 0;
    }
}
