using System.Drawing.Imaging;
using ScreenGuard.Core;
using ScreenGuard.UI;

namespace ScreenGuard;

/// <summary>
/// 真实界面截图：把休息界面在离屏位置渲染出来并抓图。
/// 与 --preview 的区别：--preview 是手绘模拟，本模式是真实控件渲染，
/// 因此能真实反映布局是否有重叠、控件是否被挤没。
///
/// 用法: ScreenGuard.exe --shot [输出路径]
/// </summary>
internal static class ShotTest
{
    public static int Run(string[] args)
    {
        Logger.Init();
        var outPath = "E:\\ScreenGuard\\data\\shot.png";
        var idx = Array.FindIndex(args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length) outPath = args[idx + 1];

        var config = AppConfig.Load().Normalize();
        config.AllowEarlyExit = true;

        // 按真实虚拟屏幕尺寸渲染
        var vs = SystemInformation.VirtualScreen;
        var width = vs.Width;
        var height = vs.Height;

        Console.WriteLine($"虚拟屏幕: {vs}");

        using var screen = new RestScreen(config, () => { }, Logger.Log);

        // 在离屏位置创建窗体并渲染，避免抢占用户屏幕
        screen.StartPosition = FormStartPosition.Manual;
        screen.Bounds = new Rectangle(-4000, 0, width, height);
        screen.Show();
        screen.BeginRest(DateTime.Now.AddMinutes(10), TimeSpan.FromMinutes(10),
            "音乐运行中 · EchoMusic（可自由操作，程序不干预）\r\n浏览器已锁定　｜　DEVECO CODE 及编程软件进程存活但不可操作");

        // 强制完成布局与重绘
        screen.Refresh();
        Application.DoEvents();
        Thread.Sleep(600);
        screen.Refresh();
        Application.DoEvents();

        // 抓取窗体内容 —— 用 DrawToBitmap 直接渲染控件树，
        // 不能用 CopyFromScreen：那抓的是屏幕实际像素，会把用户正在用的窗口拍进来。
        using var bmp = new Bitmap(width, height);
        screen.DrawToBitmap(bmp, new Rectangle(0, 0, width, height));

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        bmp.Save(outPath, ImageFormat.Png);

        Console.WriteLine($"真实界面截图已保存: {outPath}");

        // 报告各控件的实际位置，便于程序化核对重叠
        Console.WriteLine("--- 控件实际布局 ---");
        foreach (var (name, ctl) in new (string, Control)[]
        {
            ("标题", screen.GetType().GetField("_titleLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(screen) as Control),
            ("副标题", screen.GetType().GetField("_subtitleLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(screen) as Control),
            ("进度环", screen.GetType().GetField("_canvas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(screen) as Control),
            ("倒计时", screen.GetType().GetField("_countdownLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(screen) as Control),
            ("状态行", screen.GetType().GetField("_statusLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(screen) as Control),
            ("歌词说明", screen.GetType().GetField("_lyricsCaption", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(screen) as Control),
            ("音乐条", screen.GetType().GetField("_musicBar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(screen) as Control),
            ("提示", screen.GetType().GetField("_hintLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(screen) as Control),
        })
        {
            if (ctl == null) { Console.WriteLine($"{name}: (未找到)"); continue; }
            Console.WriteLine($"{name}: x={ctl.Left} y={ctl.Top} w={ctl.Width} h={ctl.Height} visible={ctl.Visible}");
        }

        screen.Close();
        return 0;
    }
}
