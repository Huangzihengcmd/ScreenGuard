using System.Drawing.Drawing2D;
using ScreenGuard.Core;

namespace ScreenGuard.UI;

/// <summary>
/// 休息锁定界面：覆盖整个虚拟屏幕的全屏置顶窗口。
///
/// 布局要点（用户明确要求"再铺开一点"）：
///  - 每个元素独占一个独立区块，区块之间留大间距，互不重叠
///  - 进度环、倒计时数字、状态文字三者完全分离，各占各的行
///  - 底部提供音乐控制条 + 桌面歌词承载区
///
/// 渲染要点：
///  - 使用双缓冲 Panel，杜绝重绘闪烁
///  - OnPaintBackground 正常绘制背景，不留空实现
///  - 置顶调用不带 SWP_SHOWWINDOW，避免反复 ShowWindow 造成闪烁
/// </summary>
public sealed class RestScreen : Form
{
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Label _countdownLabel;
    private readonly Label _statusLabel;
    private readonly Label _hintLabel;
    private readonly Label _lyricsCaption;
    private readonly BreathingPanel _canvas;
    private readonly Panel _musicBar;
    private readonly Button _btnPrev;
    private readonly Button _btnPlayPause;
    private readonly Button _btnNext;
    private readonly Button _btnMute;
    private readonly Button _btnVolDown;
    private readonly Button _btnVolUp;

    private readonly System.Windows.Forms.Timer _topmostTimer;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private readonly AppConfig _config;
    private readonly Action _onExitRequested;
    private readonly Action<string> _log;

    private DateTime _restEnd;
    private TimeSpan _totalRest;
    private bool _closing;
    private double _progressRatio;

    public RestScreen(AppConfig config, Action onExitRequested, Action<string> log)
    {
        _config = config;
        _onExitRequested = onExitRequested;
        _log = log;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        KeyPreview = true;
        BackColor = Color.FromArgb(16, 18, 26);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        var screen = SystemInformation.VirtualScreen;
        Bounds = new Rectangle(screen.Left, screen.Top, screen.Width, screen.Height);

        _canvas = new BreathingPanel();
        // 关键：不能用 Dock = Fill。
        // WinForms 的 Dock 布局按 z-order 反序处理，Dock=Fill 的画布会先吃掉整个客户区，
        // 导致后添加的音乐控制条被挤成 0 尺寸（控件整个消失）。
        // 这里改为绝对定位，画布铺满客户区，其余控件作为它的子控件绝对定位。
        _canvas.Dock = DockStyle.None;
        _canvas.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);

        _titleLabel = MakeLabel(46, FontStyle.Bold, Color.FromArgb(245, 247, 250));
        _subtitleLabel = MakeLabel(19, FontStyle.Regular, Color.FromArgb(170, 182, 205));
        _countdownLabel = MakeLabel(92, FontStyle.Bold, Color.FromArgb(125, 205, 255));
        _statusLabel = MakeLabel(16, FontStyle.Regular, Color.FromArgb(152, 205, 178));
        _hintLabel = MakeLabel(14, FontStyle.Regular, Color.FromArgb(118, 130, 152));
        _lyricsCaption = MakeLabel(13, FontStyle.Regular, Color.FromArgb(130, 165, 205));

        // 音乐控制条
        _musicBar = new Panel
        {
            BackColor = Color.FromArgb(30, 36, 52),
            Padding = new Padding(14, 10, 14, 10),
        };

        _btnPrev = MakeMusicButton("⏮", "上一首");
        _btnPlayPause = MakeMusicButton("⏯", "播放 / 暂停");
        _btnNext = MakeMusicButton("⏭", "下一首");
        _btnMute = MakeMusicButton("🔇", "静音");
        _btnVolDown = MakeMusicButton("🔉", "音量减");
        _btnVolUp = MakeMusicButton("🔊", "音量加");

        _btnPrev.Click += (_, _) => SendMusicCommand(Native.APPCOMMAND_MEDIA_PREVIOSTRACK, "上一首");
        _btnPlayPause.Click += (_, _) => SendMusicCommand(Native.APPCOMMAND_MEDIA_PLAY_PAUSE, "播放/暂停");
        _btnNext.Click += (_, _) => SendMusicCommand(Native.APPCOMMAND_MEDIA_NEXTTRACK, "下一首");
        _btnMute.Click += (_, _) => SendMusicCommand(Native.APPCOMMAND_MEDIA_VOLUME_MUTE, "静音切换");
        _btnVolDown.Click += (_, _) => SendMusicCommand(Native.APPCOMMAND_MEDIA_VOLUME_DOWN, "音量减");
        _btnVolUp.Click += (_, _) => SendMusicCommand(Native.APPCOMMAND_MEDIA_VOLUME_UP, "音量加");

        _musicBar.Controls.AddRange(new Control[]
        {
            _btnPrev, _btnPlayPause, _btnNext, _btnMute, _btnVolDown, _btnVolUp
        });

        // 全部作为画布的子控件，绝对定位，互不参与 Dock 计算
        _canvas.Controls.AddRange(new Control[]
        {
            _titleLabel, _subtitleLabel, _countdownLabel, _statusLabel,
            _hintLabel, _lyricsCaption, _musicBar
        });
        Controls.Add(_canvas);

        Resize += (_, _) => SyncCanvasSize();

        // 频率不能太慢：其他窗口（UWP 宿主、模拟器等）可能带 WS_EX_TOPMOST，
        // 会在两次置顶之间浮上来盖住界面
        _topmostTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _topmostTimer.Tick += (_, _) => EnforceTopmost();

        _tickTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _tickTimer.Tick += (_, _) => UpdateCountdown();

        KeyDown += OnKeyDown;
        FormClosing += OnFormClosing;
    }

    private static Label MakeLabel(int size, FontStyle style, Color color)
        => new()
        {
            Font = new Font("Microsoft YaHei UI", size, style),
            ForeColor = color,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            BackColor = Color.Transparent,
        };

    private static Button MakeMusicButton(string text, string tip)
        => new()
        {
            Text = text,
            Font = new Font("Segoe UI Symbol", 15F),
            ForeColor = Color.FromArgb(215, 228, 245),
            BackColor = Color.FromArgb(46, 54, 74),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(56, 44),
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 6, 0),
            TabStop = false,
        };

    /// <summary>桌面歌词承载区在屏幕上的矩形（供外部放置歌词窗口）。</summary>
    public Rectangle LyricsHostRect { get; private set; }

    public void BeginRest(DateTime endTime, TimeSpan total, string statusText)
    {
        _restEnd = endTime;
        _totalRest = total;
        _statusLabel.Text = statusText;

        _titleLabel.Text = "该休息一下了";
        _subtitleLabel.Text = "起来走两步，看看远处，让眼睛歇一会儿。";
        _hintLabel.Text = _config.AllowEarlyExit
            ? "按 Esc 可提前结束休息"
            : "休息结束前无法关闭本界面";

        SyncCanvasSize();
        Layout();

        Show();
        _canvas.StartAnimation();
        EnforceTopmost();
        _topmostTimer.Start();
        _tickTimer.Start();
        UpdateCountdown();
    }

    /// <summary>画布尺寸随窗口同步（因为不用 Dock，必须手动维护）。</summary>
    private void SyncCanvasSize()
    {
        if (_canvas == null) return;
        var cr = ClientRectangle;
        if (_canvas.Bounds == cr) return;
        _canvas.Bounds = cr;
        _canvas.Invalidate();
    }

    /// <summary>按屏幕尺寸等比布局，各区块分离、间距宽松。</summary>
    private void Layout()
    {
        var area = ClientRectangle;
        var cx = area.Width / 2;
        var scale = Math.Max(0.7, Math.Min(1.4, area.Height / 1080.0));
        var contentW = (int)(1080 * scale);

        // 各区块高度与间距
        // 注意：倒计时字号 76pt，其单行高度约 122px，因此标签高度必须 ≥ 130px，
        // 否则数字会被上下裁掉（这是之前"09:55 只显示一半"的原因）。
        var titleH = (int)(70 * scale);
        var gap1 = (int)(30 * scale);      // 标题 → 副标题
        var subH = (int)(38 * scale);
        var gap2 = (int)(66 * scale);      // 副标题 → 进度环
        var ringH = (int)(256 * scale);
        var gap3 = (int)(40 * scale);      // 进度环 → 倒计时
        var countH = (int)(132 * scale);
        var gap4 = (int)(36 * scale);      // 倒计时 → 状态
        var statusH = (int)(60 * scale);   // 状态区容纳两行
        var gap5 = (int)(34 * scale);      // 状态 → 歌词框
        var lyricsH = (int)(96 * scale);
        var gap6 = (int)(14 * scale);      // 歌词框 → 说明文字
        var captionH = (int)(24 * scale);
        var gap7 = (int)(20 * scale);      // 说明文字 → 控制条
        var barH = (int)(64 * scale);
        var gap8 = (int)(22 * scale);      // 控制条 → 提示
        var hintH = (int)(26 * scale);

        var totalH = titleH + gap1 + subH + gap2 + ringH + gap3 + countH + gap4
                   + statusH + gap5 + lyricsH + gap6 + captionH + gap7 + barH + gap8 + hintH;

        // 整块垂直居中
        var top = Math.Max((int)(24 * scale), (area.Height - totalH) / 2);

        var y = top;
        _titleLabel.SetBounds(cx - contentW / 2, y, contentW, titleH);
        y += titleH + gap1;

        _subtitleLabel.SetBounds(cx - contentW / 2, y, contentW, subH);
        y += subH + gap2;

        // 进度环独占一整行
        _canvas.RingRect = new Rectangle(cx - (int)(128 * scale), y, (int)(256 * scale), (int)(256 * scale));
        y += ringH + gap3;

        // 倒计时数字独占一行（与环、状态、歌词区完全分离）
        _countdownLabel.SetBounds(cx - contentW / 2, y, contentW, countH);
        _countdownLabel.Font = new Font("Consolas", (float)(76 * scale), FontStyle.Bold);
        y += countH + gap4;

        // 状态文字独占一块（两行）
        _statusLabel.SetBounds(cx - contentW / 2, y, contentW, statusH);
        y += statusH + gap5;

        // 桌面歌词承载区 —— 边框由画布绘制，歌词窗口会被搬进这个位置
        var lyricsW = (int)(620 * scale);
        LyricsHostRect = new Rectangle(cx - lyricsW / 2, y, lyricsW, lyricsH);
        _canvas.LyricsHostRect = LyricsHostRect;
        y += lyricsH + gap6;

        _lyricsCaption.SetBounds(LyricsHostRect.Left, y, lyricsW, captionH);
        _lyricsCaption.Text = "桌面歌词显示区（音乐播放器歌词已移入此处）";
        y += captionH + gap7;

        // 音乐控制条
        _musicBar.SetBounds(cx - contentW / 2, y, contentW, barH);
        _musicBar.Padding = new Padding((int)(16 * scale), (int)(12 * scale), (int)(16 * scale), (int)(12 * scale));

        var btnW = (int)(52 * scale);
        var btnH = (int)(40 * scale);
        var btnY = (int)(12 * scale);
        var bx = cx - contentW / 2 + (int)(18 * scale);
        foreach (var b in new[] { _btnPrev, _btnPlayPause, _btnNext, _btnMute, _btnVolDown, _btnVolUp })
        {
            b.SetBounds(bx, btnY, btnW, btnH);
            b.Font = new Font("Segoe UI Symbol", (float)(15F * scale));
            bx += btnW + (int)(12 * scale);
        }
        y += barH + gap8;

        // 提示文字
        _hintLabel.SetBounds(cx - contentW / 2, y, contentW, hintH);
    }

    /// <summary>供主界面放置音乐控制命令的目标窗口。</summary>
    public Func<IntPtr> MusicCommandTarget { get; set; } = () => IntPtr.Zero;

    private void SendMusicCommand(int appCommand, string what)
    {
        try
        {
            var hwnd = MusicCommandTarget();
            if (hwnd == IntPtr.Zero)
            {
                _log($"[RestScreen] 未找到音乐窗口，{what} 指令未发送");
                return;
            }
            Native.SendMessage(hwnd, Native.WM_APPCOMMAND, IntPtr.Zero, new IntPtr(appCommand << 16));
            _log($"[RestScreen] 已发送音乐指令: {what}");
        }
        catch (Exception ex)
        {
            _log($"[RestScreen] 音乐指令发送失败({what}): {ex.Message}");
        }
    }

    private void UpdateCountdown()
    {
        var remain = _restEnd - DateTime.Now;
        if (remain <= TimeSpan.Zero)
        {
            _tickTimer.Stop();
            _topmostTimer.Stop();
            _canvas.StopAnimation();
            Close();
            return;
        }

        var mm = remain.Minutes.ToString("00");
        var ss = remain.Seconds.ToString("00");
        _countdownLabel.Text = $"{mm}:{ss}";

        var total = _totalRest.TotalSeconds;
        _progressRatio = total <= 0 ? 0 : Math.Clamp(remain.TotalSeconds / total, 0, 1);
        _canvas.ProgressRatio = _progressRatio;
        _canvas.InvalidateRing();
    }

    private void EnforceTopmost()
    {
        try
        {
            if (IsDisposed) return;

            var screen = SystemInformation.VirtualScreen;
            if (Bounds != new Rectangle(screen.Left, screen.Top, screen.Width, screen.Height))
                Bounds = new Rectangle(screen.Left, screen.Top, screen.Width, screen.Height);

            // 注意：不带 SWP_SHOWWINDOW —— 那会反复触发 ShowWindow 导致界面闪烁
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            // 从任务栏图标 Alt+Tab 也切不走
            var ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
        }
        catch (Exception ex)
        {
            _log($"[RestScreen] 置顶失败: {ex.Message}");
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _config.AllowEarlyExit)
        {
            _onExitRequested();
        }
        e.Handled = true;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;

        if (!_config.AllowEarlyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _log("[RestScreen] 拦截关闭请求（强制休息模式）");
            return;
        }

        _closing = true;
        _topmostTimer.Stop();
        _tickTimer.Stop();
        _canvas.StopAnimation();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Alt | Keys.F4)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

/// <summary>
/// 双缓冲画布：负责背景、进度环与呼吸动画。
/// 继承 Panel 并显式开启 OptimizedDoubleBuffer，避免重绘闪烁。
/// </summary>
internal sealed class BreathingPanel : Panel
{
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 60 };
    private double _ratio;

    public BreathingPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(16, 18, 26);
        _animTimer.Tick += (_, _) => InvalidateRing();
    }

    public double ProgressRatio
    {
        get => _ratio;
        set => _ratio = value;
    }

    /// <summary>进度环所在矩形（屏幕坐标换算后绘制）。</summary>
    public Rectangle RingRect { get; set; } = new();

    /// <summary>桌面歌词承载区（画布坐标），会绘制边框提示。</summary>
    public Rectangle LyricsHostRect { get; set; } = new();

    public void StartAnimation() => _animTimer.Start();
    public void StopAnimation() => _animTimer.Stop();

    /// <summary>仅重绘进度环区域，避免整屏重绘。</summary>
    public void InvalidateRing()
    {
        if (RingRect.Width <= 0) return;
        var local = new Rectangle(
            RingRect.Left - Parent.ClientRectangle.Left - 30,
            RingRect.Top - Parent.ClientRectangle.Top - 30,
            RingRect.Width + 60, RingRect.Height + 60);
        Invalidate(local);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 正常绘制背景 —— 空实现会导致重绘闪烁
        using var brush = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(14, 16, 24),
            Color.FromArgb(30, 38, 60),
            LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // —— 桌面歌词承载区边框 ——
        // 必须在真程序里画出来，否则用户看不到歌词该出现在哪
        if (LyricsHostRect.Width > 0)
        {
            // LyricsHostRect 是屏幕坐标，画布坐标系需减去偏移
            var host = new Rectangle(
                LyricsHostRect.Left - Parent?.ClientRectangle.Left ?? LyricsHostRect.Left,
                LyricsHostRect.Top - Parent?.ClientRectangle.Top ?? LyricsHostRect.Top,
                LyricsHostRect.Width, LyricsHostRect.Height);

            using var pen = new Pen(Color.FromArgb(52, 68, 96), 1);
            using var fill = new SolidBrush(Color.FromArgb(22, 26, 38));
            g.FillRectangle(fill, host);
            g.DrawRectangle(pen, host);
        }

        if (RingRect.Width <= 0) return;

        var ring = new Rectangle(
            RingRect.Left - Parent.ClientRectangle.Left,
            RingRect.Top - Parent.ClientRectangle.Top,
            RingRect.Width, RingRect.Height);

        // 外圈柔光
        var glow = (int)(ring.Width * 1.75);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(
                ring.Left + ring.Width / 2 - glow / 2,
                ring.Top + ring.Height / 2 - glow / 2,
                glow, glow);
            using var brush = new PathGradientBrush(path);
            brush.CenterColor = Color.FromArgb(52, 112, 180, 52);
            brush.SurroundColors = new Color[] { Color.FromArgb(0, 60, 120, 190) };
            g.FillPath(brush, path);
        }

        // 底圈
        using (var pen = new Pen(Color.FromArgb(40, 50, 70), 8))
        {
            g.DrawEllipse(pen, ring);
        }

        // 进度弧
        if (_ratio > 0)
        {
            using var arcPen = new Pen(Color.FromArgb(125, 205, 255), 8);
            arcPen.StartCap = LineCap.Round;
            arcPen.EndCap = LineCap.Round;
            g.DrawArc(arcPen, ring, -90, (float)(360 * _ratio));
        }

        // 环心呼吸点
        var t = DateTime.Now.Millisecond / 1000.0;
        var pulse = 1.0 + 0.14 * Math.Sin(t * Math.PI * 2);
        var dot = (int)(16 * pulse);
        var cx = ring.Left + ring.Width / 2;
        var cy = ring.Top + ring.Height / 2;
        using (var brush = new SolidBrush(Color.FromArgb(125, 205, 255)))
        {
            g.FillEllipse(brush, cx - dot / 2, cy - dot / 2, dot, dot);
        }
    }
}
