using System.Drawing.Drawing2D;
using ScreenGuard.Core;

namespace ScreenGuard.UI;

/// <summary>主控制面板：设置时长、查看剩余时间、查看管控状态。</summary>
public sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly Action<string> _log;

    private readonly ProcessGuard _guard;
    private readonly MediaController _media;
    private readonly KeyboardInterceptor _keyboard;
    private readonly WindowEnforcer _windowEnforcer;
    private readonly AllowedWindowPlacer _placer;

    private readonly Label _stateLabel = new();
    private readonly Label _remainingLabel = new();
    private readonly Label _detailLabel = new();
    private readonly Label _musicLabel = new();
    private readonly Label _guardLabel = new();
    private readonly NumericUpDown _workInput = new();
    private readonly NumericUpDown _restInput = new();
    private readonly CheckBox _autoStartBox = new();
    private readonly CheckBox _earlyExitBox = new();
    private readonly Button _startPauseButton = new();
    private readonly Button _restNowButton = new();
    private readonly Button _saveButton = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _tickTimer = new() { Interval = 1000 };

    private RestScreen? _restScreen;
    private DateTime _sessionStart;
    private TimeSpan _workSpan;
    private bool _running;
    private bool _resting;
    private bool _closingForExit;
    private HashSet<string> _musicDuringRest = new(StringComparer.OrdinalIgnoreCase);

    public MainForm(AppConfig config, Action<string> log)
    {
        _config = config;
        _log = log;

        _guard = new ProcessGuard(config, log);
        _guard.Reload();
        _media = new MediaController(config, _guard, log);
        _keyboard = new KeyboardInterceptor(log);
        _windowEnforcer = new WindowEnforcer(_guard, log);
        _placer = new AllowedWindowPlacer(_guard, log);

        BuildUi();
        BuildTray();
        LoadSettings();
        _tickTimer.Tick += (_, _) => OnTick();

        _sessionStart = DateTime.Now;
        _workSpan = TimeSpan.FromMinutes(_config.WorkMinutes);
        _running = true;
        _tickTimer.Start();

        _log($"[App] 启动完成，工作时长 {_config.WorkMinutes} 分钟，休息时长 {_config.RestMinutes} 分钟");
        _log($"[App] 部署位置校验: {(Deployment.IsValidLocation() ? "通过（E 盘）" : "失败")}");
    }

    private void BuildUi()
    {
        Text = "ScreenGuard 时长管家";
        ClientSize = new Size(560, 500);
        MinimumSize = new Size(560, 500);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(245, 247, 250);
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "ScreenGuard 时长管家",
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 34, 48),
            AutoSize = true,
            Location = new Point(28, 24),
        };

        _stateLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        _stateLabel.ForeColor = Color.FromArgb(52, 120, 220);
        _stateLabel.AutoSize = true;
        _stateLabel.Location = new Point(30, 74);

        _remainingLabel.Font = new Font("Consolas", 32F, FontStyle.Bold);
        _remainingLabel.ForeColor = Color.FromArgb(24, 30, 42);
        _remainingLabel.AutoSize = true;
        _remainingLabel.Location = new Point(28, 102);

        _detailLabel.AutoSize = false;
        _detailLabel.Size = new Size(505, 22);
        _detailLabel.Location = new Point(30, 164);
        _detailLabel.ForeColor = Color.FromArgb(110, 120, 140);

        _musicLabel.AutoSize = false;
        _musicLabel.Size = new Size(505, 22);
        _musicLabel.Location = new Point(30, 194);
        _musicLabel.ForeColor = Color.FromArgb(70, 150, 100);

        _guardLabel.AutoSize = false;
        _guardLabel.Size = new Size(505, 40);
        _guardLabel.Location = new Point(30, 224);
        _guardLabel.ForeColor = Color.FromArgb(130, 138, 155);

        // 设置区
        var settingsGroup = new GroupBox
        {
            Text = "设置",
            Location = new Point(26, 278),
            Size = new Size(508, 138),
            ForeColor = Color.FromArgb(90, 100, 120),
        };

        var workLabel = new Label { Text = "连续使用（分钟）", Location = new Point(22, 36), AutoSize = true };
        _workInput.Location = new Point(150, 32);
        _workInput.Size = new Size(96, 28);
        _workInput.Minimum = 1;
        _workInput.Maximum = 1440;

        var restLabel = new Label { Text = "休息时长（分钟）", Location = new Point(276, 36), AutoSize = true };
        _restInput.Location = new Point(390, 32);
        _restInput.Size = new Size(96, 28);
        _restInput.Minimum = 1;
        _restInput.Maximum = 240;

        _autoStartBox.Text = "开机自动启动";
        _autoStartBox.Location = new Point(22, 84);
        _autoStartBox.AutoSize = true;

        _earlyExitBox.Text = "允许提前结束休息";
        _earlyExitBox.Location = new Point(180, 84);
        _earlyExitBox.AutoSize = true;

        _saveButton.Text = "保存设置";
        _saveButton.Location = new Point(372, 80);
        _saveButton.Size = new Size(112, 30);
        _saveButton.Click += OnSaveClick;

        settingsGroup.Controls.AddRange(new Control[]
        {
            workLabel, _workInput, restLabel, _restInput,
            _autoStartBox, _earlyExitBox, _saveButton
        });

        // 操作区
        _startPauseButton.Text = "暂停计时";
        _startPauseButton.Location = new Point(26, 434);
        _startPauseButton.Size = new Size(158, 40);
        _startPauseButton.Click += (_, _) => ToggleRunning();

        _restNowButton.Text = "立即休息";
        _restNowButton.Location = new Point(200, 434);
        _restNowButton.Size = new Size(158, 40);
        _restNowButton.Click += (_, _) => EnterRest(true);

        var exitButton = new Button
        {
            Text = "退出",
            Location = new Point(374, 434),
            Size = new Size(158, 40),
        };
        exitButton.Click += (_, _) => ExitApplication();

        var protectedHint = new Label
        {
            Text = $"放行名单: DEVECO CODE · 全部编程类软件 · 音乐播放器　|　部署位置: E 盘",
            AutoSize = false,
            Size = new Size(505, 20),
            Location = new Point(30, 482),
            ForeColor = Color.FromArgb(140, 146, 160),
        };

        Controls.AddRange(new Control[]
        {
            title, _stateLabel, _remainingLabel, _detailLabel,
            _musicLabel, _guardLabel, settingsGroup,
            _startPauseButton, _restNowButton, exitButton, protectedHint
        });
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主面板", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; });
        menu.Items.Add("立即休息", null, (_, _) => EnterRest(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _tray.Icon = SystemIcons.Application;
        _tray.Text = "ScreenGuard 时长管家";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; };
    }

    private void LoadSettings()
    {
        _workInput.Value = Math.Clamp(_config.WorkMinutes, 1, 1440);
        _restInput.Value = Math.Clamp(_config.RestMinutes, 1, 240);
        _autoStartBox.Checked = _config.AutoStart;
        _earlyExitBox.Checked = _config.AllowEarlyExit;
    }

    private void OnTick()
    {
        if (_resting) return;

        var remain = _workSpan - (DateTime.Now - _sessionStart);
        if (remain <= TimeSpan.Zero)
        {
            EnterRest(false);
            return;
        }

        var mm = (int)remain.TotalMinutes;
        var ss = remain.Seconds.ToString("00");
        _remainingLabel.Text = $"{mm:D2}:{ss}";
        _detailLabel.Text = $"本次连续使用上限 {_config.WorkMinutes} 分钟　|　{( _running ? "计时中" : "已暂停")}";
        UpdateTrayText(remain);
    }

    private void UpdateTrayText(TimeSpan remain)
    {
        var mm = (int)remain.TotalMinutes;
        var ss = remain.Seconds.ToString("00");
        _tray.Text = $"ScreenGuard　剩余 {mm:D2}:{ss}";
    }

    private void ToggleRunning()
    {
        if (_resting) return;
        if (_running)
        {
            // 暂停：冻结已用时长
            _elapsedBeforePause = DateTime.Now - _sessionStart;
            _running = false;
        }
        else
        {
            // 恢复：以冻结的已用时长重设基准，暂停期间不计入
            _sessionStart = DateTime.Now - _elapsedBeforePause;
            _running = true;
        }
        _startPauseButton.Text = _running ? "暂停计时" : "继续计时";
        _log(_running ? "[App] 计时恢复" : "[App] 计时暂停");
    }

    private TimeSpan _elapsedBeforePause = TimeSpan.Zero;

    private TimeSpan Elapsed()
        => _running ? DateTime.Now - _sessionStart : _elapsedBeforePause;

    /// <summary>进入休息状态。</summary>
    private void EnterRest(bool manual)
    {
        if (_resting) return;
        _resting = true;
        _tickTimer.Stop();

        _log(manual ? "[App] 手动触发休息" : "[App] 达到使用时长上限，自动进入休息");

        try
        {
            // 1) 记录音乐播放状态（放行，不中断）
            _log("[App] 步骤1: 采样音乐状态");
            _musicDuringRest = _media.CaptureMusicPlaybackState();

            // 2) 快照既有浏览器进程（这些保留不杀，B 站视频所在浏览器必须存活）
            _log("[App] 步骤2: 快照既有浏览器");
            _guard.CapturePreExistingBrowsers();
            // 3) 暂停 B 站视频，但保留进程
            _log("[App] 步骤3: 暂停B站视频");
            _media.PauseBilibiliVideo();

            // 4) 最小化其他程序窗口（音乐与编程软件完全放行）
            _log("[App] 步骤4: 最小化其他窗口");
            _windowEnforcer.MinimizeAllExceptAllowed();

            // 5) 启用键盘拦截，封锁系统级逃逸快捷键
            _log("[App] 步骤5: 启用键盘拦截");
            _keyboard.Start();

            // 6) 启动全屏休息界面
            _log("[App] 步骤6: 创建休息界面");
            var total = TimeSpan.FromMinutes(_config.RestMinutes);
            var end = DateTime.Now.Add(total);
            _restEnd = end;
            _restScreen = new RestScreen(_config, ExitRestEarly, _log);
            _restScreen.BeginRest(end, total, BuildRestStatus());
            _log("[App] 步骤6完成: 休息界面已显示");

            // 7) 桌面歌词搬进休息界面 + 放行窗口置顶（保证音乐/编程软件可操作）
            _log("[App] 步骤7: 安置放行窗口与桌面歌词");
            var lyricsHwnd = _placer.FindLyricsWindow();
            if (lyricsHwnd != IntPtr.Zero)
            {
                _placer.MoveLyricsToHost(lyricsHwnd, _restScreen.LyricsHostRect);
            }
            else
            {
                _log("[Placer] 未找到桌面歌词窗口");
            }

            _restScreen.MusicCommandTarget = () => lyricsHwnd != IntPtr.Zero
                ? lyricsHwnd
                : _media.FindMusicCommandWindow();

            _placer.RaiseAllowedWindowsAbove(_restScreen.Handle);

            _stateLabel.Text = "休息中";
            _stateLabel.ForeColor = Color.FromArgb(220, 120, 60);
            _remainingLabel.Text = _config.RestMinutes.ToString("00") + ":00";
            _detailLabel.Text = $"强制休息 {_config.RestMinutes} 分钟，期间无法操作其他程序";
            Show();

            // 8) 休息期间持续执行管控
            _log("[App] 步骤8: 启动管控定时器");
            _restWatchTimer.Start();
            _focusGuardTimer.Start();
            _log("[App] 进入休息状态完成");
        }
        catch (Exception ex)
        {
            _log($"[Fatal] 进入休息状态时异常: {ex}");
            try { File.WriteAllText(Path.Combine(Deployment.DataRoot, "crash-rest.txt"), ex.ToString()); }
            catch { }
            ExitRestEarly();
        }
    }

    private string BuildRestStatus()
    {
        var parts = new List<string>();
        if (_musicDuringRest.Count > 0)
            parts.Add($"音乐运行中 · {string.Join(", ", _musicDuringRest)}（可自由操作，程序不干预）");
        else
            parts.Add("音乐未在运行 · 音乐播放器不受任何限制");
        parts.Add("浏览器已锁定");
        parts.Add("DEVECO CODE 及编程软件可正常使用");
        return string.Join("\r\n", parts);
    }

    private readonly System.Windows.Forms.Timer _restWatchTimer = new() { Interval = 2000 };

    /// <summary>前台激活拦截定时器：高频执行，确保用户无法抢占其他程序窗口。</summary>
    private readonly System.Windows.Forms.Timer _focusGuardTimer = new() { Interval = 250 };

    /// <summary>前台激活拦截：用户点任务栏/程序试图恢复被管控窗口时立即压回。</summary>
    private void OnFocusGuardTick()
    {
        if (!_resting) return;
        if (_restScreen == null || _restScreen.IsDisposed || !_restScreen.IsHandleCreated) return;

        // 休息界面必须在最顶层 —— 某些 UWP 宿主 / 模拟器窗口自带 WS_EX_TOPMOST，
        // 会浮到休息界面之上把内容挡住，必须反复压回去。
        _placer.KeepRestScreenOnTop(_restScreen.Handle);

        _windowEnforcer.SuppressForegroundActivation(_restScreen.Handle);
    }

    private void OnRestWatchTick()
    {
        if (!_resting) return;

        // 浏览器管控：仅压制窗口（不终止进程，避免误伤用户编辑中的网页与浏览器会话）
        _guard.CollectBrowsersToSuppress();

        // 窗口管控：隐藏新弹出的受管控窗口（模拟器错误框、UWP 宿主等）
        _windowEnforcer.EnforceSuppressed();

        // 歌词搬运：歌词窗可能稍晚才创建，持续重试
        if (_restScreen != null && !_restScreen.IsDisposed)
        {
            if (_placer.EnsureLyricsDocked(_restScreen.LyricsHostRect, out var lh))
                _log("[Placer] 桌面歌词已移入休息界面承载区");
            _restScreen.MusicCommandTarget = () => lh != IntPtr.Zero
                ? lh
                : _media.FindMusicCommandWindow();
        }

        // 音乐放行校验：只观测，不做任何恢复操作
        _media.EnsureMusicContinues(_musicDuringRest);

        // 主面板同步显示休息倒计时
        if (_restEnd.HasValue)
        {
            var remain = _restEnd.Value - DateTime.Now;
            if (remain > TimeSpan.Zero)
            {
                _remainingLabel.Text = $"{(int)remain.TotalMinutes:D2}:{remain.Seconds:D2}";
            }
        }

        if (_restEnd.HasValue && DateTime.Now >= _restEnd.Value)
            ExitRestEarly();
    }

    private DateTime? _restEnd;

    private void ExitRestEarly()
    {
        if (!_resting) return;
        _resting = false;
        _restWatchTimer.Stop();
        _focusGuardTimer.Stop();

        _keyboard.Stop();

        // 恢复被最小化的窗口
        _windowEnforcer.RestoreAll();

        // 还原放行窗口的位置与层级（歌词归位）
        _placer.RestoreAll();

        // 清除浏览器快照（下次休息重新快照）
        _guard.ClearBrowserSnapshot();

        if (_restScreen != null && !_restScreen.IsDisposed)
        {
            _restScreen.Close();
            _restScreen.Dispose();
        }
        _restScreen = null;

        // 恢复计数
        _sessionStart = DateTime.Now;
        _elapsedBeforePause = TimeSpan.Zero;
        _running = true;
        _tickTimer.Start();

        _stateLabel.Text = "工作中";
        _stateLabel.ForeColor = Color.FromArgb(52, 120, 220);
        _detailLabel.Text = "休息结束，继续计时";
        _log("[App] 休息结束，恢复正常工作状态");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _restWatchTimer.Tick += (_, _) => OnRestWatchTick();
        _restWatchTimer.Stop();
        _focusGuardTimer.Tick += (_, _) => OnFocusGuardTick();
        _focusGuardTimer.Stop();
        RefreshGuardStatus();
    }

    private void RefreshGuardStatus()
    {
        var alive = _guard.VerifyAllowedAlive();
        _guardLabel.Text = alive.Count > 0
            ? $"放行进程运行中（音乐 / 编程软件 / 白名单）: {string.Join(", ", alive)}"
            : "放行进程当前未运行（启动后自动恢复管控）";
        _musicLabel.Text = _musicDuringRest.Count > 0
            ? $"音乐放行中: {string.Join(", ", _musicDuringRest)}　（程序不干预，可自行开关）"
            : "";    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        _config.WorkMinutes = (int)_workInput.Value;
        _config.RestMinutes = (int)_restInput.Value;
        _config.AutoStart = _autoStartBox.Checked;
        _config.AllowEarlyExit = _earlyExitBox.Checked;
        _config.Normalize().Save();
        _guard.Reload();
        _workSpan = TimeSpan.FromMinutes(_config.WorkMinutes);
        _sessionStart = DateTime.Now;
        _elapsedBeforePause = TimeSpan.Zero;
        _log($"[App] 设置已保存: 工作 {_config.WorkMinutes} 分钟 / 休息 {_config.RestMinutes} 分钟");
        MessageBox.Show(this, "设置已保存", "ScreenGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closingForExit) { base.OnFormClosing(e); return; }

        // 关闭到托盘
        e.Cancel = true;
        Hide();
        _tray.Visible = true;
    }

    /// <summary>真正的退出流程：先恢复系统状态，再退出程序。</summary>
    private void ExitApplication()
    {
        _closingForExit = true;

        try
        {
            // 解除键盘拦截
            _keyboard.Stop();

            // 恢复被最小化的窗口，避免用户丢失窗口状态
            if (_resting)
            {
                _windowEnforcer.RestoreAll();
                _guard.ClearBrowserSnapshot();
            }
        }
        catch (Exception ex)
        {
            _log($"[App] 退出清理异常: {ex.Message}");
        }

        _tray.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closingForExit = true;
            _keyboard.Dispose();
            _tickTimer.Stop();
            _restWatchTimer.Stop();
            _focusGuardTimer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            if (_restScreen != null && !_restScreen.IsDisposed) _restScreen.Dispose();

            // 兜底：任何退出路径都要把被最小化的窗口恢复回来
            try { _windowEnforcer.RestoreAll(); } catch { }
        }
        base.Dispose(disposing);
    }
}
