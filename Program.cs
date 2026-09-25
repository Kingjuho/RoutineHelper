using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RoutineHelper;

internal static class Program
{
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

    internal static bool TryCleanupIfAppStopped(Action cleanup,
        string trayMutexName = @"Local\RoutineHelperTray", string legacyMutexName = @"Local\RoutineCheckerTray")
    {
        // 앱과 같은 실행 잠금을 정리가 끝날 때까지 유지해 시작·정리 사이의 경쟁을 막는다.
        using var current = new Mutex(true, trayMutexName, out var currentCreated);
        if (!currentCreated) return false;
        using var legacy = new Mutex(true, legacyMutexName, out var legacyCreated);
        if (!legacyCreated) return false;
        cleanup();
        return true;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--reconcile", StringComparer.OrdinalIgnoreCase))
            {
                TryCleanupIfAppStopped(() => HostsManager.Reconcile(false, []));
                return 0;
            }
            if (args.Contains("--clear", StringComparer.OrdinalIgnoreCase))
            {
                HostsManager.Reconcile(false, []);
                return 0;
            }

            using var mutex = new Mutex(true, @"Local\RoutineHelperTray", out var created);
            if (!created) return 0;
            using var previousVersionMutex = new Mutex(true, @"Local\RoutineCheckerTray", out var previousVersionClosed);
            if (!previousVersionClosed)
                throw new InvalidOperationException("실행 중인 이전 RoutineChecker를 종료한 뒤 RoutineHelper를 실행하세요.");
            // 설정을 읽기 전에 이전 실행의 관리 구역만 정리한다.
            try { HostsManager.Reconcile(false, []); }
            catch (HostsMarkerException ex)
            {
                AppLog.Write(ex.Message);
                MessageBox.Show(ex.Message, "RoutineHelper hosts 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            ApplicationConfiguration.Initialize();
            using var context = new RoutineContext(ConfigPath);
            Application.Run(context);
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Write($"오류: {ex}");
            if (!args.Contains("--reconcile", StringComparer.OrdinalIgnoreCase) &&
                !args.Contains("--clear", StringComparer.OrdinalIgnoreCase))
                MessageBox.Show(ex.Message, "RoutineHelper 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class RoutineContext : ApplicationContext
{
    private readonly string _configPath;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly string _reminderStatePath;
    private Config _config;
    private DateTime _configWriteTime;
    private DateTime _lastConfigCheck = DateTime.MinValue;
    private DateTime _lastHostsCheck = DateTime.MinValue;
    private DateTime _lastReminderDate = DateTime.MinValue;
    private bool? _lastActive;
    private bool _busy;
    private string? _hostsError;
    private SettingsForm? _settings;
    private readonly PomodoroSession _pomodoro = new();
    private readonly ToolStripMenuItem _pomodoroToggle;
    private readonly ToolStripMenuItem _pomodoroStatus;
    private string ReminderStatus => _config.ShutdownReminderEnabled ? _config.ShutdownReminder : "꺼짐";

    public RoutineContext(string configPath)
    {
        _configPath = configPath;
        _config = Config.Load(configPath);
        _configWriteTime = File.GetLastWriteTimeUtc(configPath);
        AppLog.Write($"설정 로드: {_configPath}; 종료 알림: {ReminderStatus}");
        _reminderStatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RoutineHelper", "last-reminder.txt");
        var previousReminderState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RoutineChecker", "last-reminder.txt");
        var reminderToRead = File.Exists(_reminderStatePath) ? _reminderStatePath : previousReminderState;
        if (File.Exists(reminderToRead) && DateTime.TryParseExact(File.ReadAllText(reminderToRead),
                "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var lastDate))
            _lastReminderDate = lastDate;

        var menu = new ContextMenuStrip();
        menu.Items.Add("메인 창 열기", null, (_, _) => OpenMainWindow());
        menu.Items.Add("설정 파일 열기", null, (_, _) => OpenConfiguration());
        menu.Items.Add("상태 보기", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());
        _pomodoroToggle = new ToolStripMenuItem("포모도로 기법 활성화", null, (_, _) => TogglePomodoro());
        _pomodoroStatus = new ToolStripMenuItem("포모도로 꺼짐 · 기존 시간표 적용") { Enabled = false };
        menu.Items.Add(_pomodoroToggle);
        menu.Items.Add(_pomodoroStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("새로고침", null, (_, _) => RefreshConfiguration());
        menu.Items.Add("종료", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = AppIcons.Tray,
            Text = "RoutineHelper",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenMainWindow();
        };
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();
    }

    private void Tick(string? notification = null)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var now = DateTime.Now;
            if ((now - _lastConfigCheck).Duration() >= TimeSpan.FromSeconds(5))
            {
                _lastConfigCheck = now;
                ReloadConfigIfChanged();
            }

            var pomodoroEvent = _pomodoro.Update();
            UpdatePomodoroDisplay();
            if ((pomodoroEvent & PomodoroEvent.BreakEndingSoon) != 0)
                notification = AddNotice(notification, $"휴식 종료까지 {(int)Math.Ceiling(_pomodoro.Remaining.TotalSeconds)}초 남았습니다. 집중이 시작되면 지정한 프로그램이 종료됩니다.");
            else if ((pomodoroEvent & PomodoroEvent.BreakStarted) != 0)
                notification = AddNotice(notification, "집중 시간이 끝났습니다. 5분간 휴식합니다.");
            else if ((pomodoroEvent & PomodoroEvent.FocusStarted) != 0)
                notification = AddNotice(notification, "휴식이 끝났습니다. 25분간 집중합니다.");
            var active = _pomodoro.ShouldBlock(_config.IsActive(now));
            if (_lastActive != active || (now - _lastHostsCheck).Duration() >= TimeSpan.FromMinutes(1))
            {
                try
                {
                    HostsManager.Reconcile(active, _config.BlockedSites);
                    _hostsError = null;
                    _lastHostsCheck = now;
                    _lastActive = active;
                }
                catch (Exception ex)
                {
                    AppLog.Write($"hosts 동기화 실패: {ex}");
                    if (_hostsError != ex.Message)
                        notification = AddNotice(notification, "사이트 차단 적용/해제 실패: " + ex.Message);
                    _hostsError = ex.Message;
                    _lastHostsCheck = now;
                    _lastActive = active;
                }
            }

            if (active) StopBlockedProcesses();
            if (_config.IsReminderDue(now, _lastReminderDate))
            {
                _lastReminderDate = now.Date;
                Directory.CreateDirectory(Path.GetDirectoryName(_reminderStatePath)!);
                File.WriteAllText(_reminderStatePath, now.ToString("yyyy-MM-dd"));
                notification = AddNotice(notification, "컴퓨터를 종료할 시간입니다.");
                AppLog.Write("종료 시간 알림 요청");
            }
            if (notification != null)
            {
                if (_hostsError != null && !notification.Contains(_hostsError))
                    notification = AddNotice(notification, "사이트 차단 적용/해제 실패: " + _hostsError);
                _icon.ShowBalloonTip(10000, "RoutineHelper", notification,
                    _hostsError is null ? ToolTipIcon.Info : ToolTipIcon.Warning);
                AppLog.Write(notification);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"실행 중 오류: {ex}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static string AddNotice(string? current, string message) => current == null ? message : current + "\n" + message;

    private string PomodoroStatus
    {
        get
        {
            if (!_pomodoro.Enabled) return "포모도로 꺼짐 · 기존 시간표 적용";
            var seconds = (int)Math.Ceiling(_pomodoro.Remaining.TotalSeconds);
            return $"포모도로 {(_pomodoro.Phase == PomodoroPhase.Focus ? "집중" : "휴식")} · {seconds / 60:00}:{seconds % 60:00} 남음";
        }
    }

    private void UpdatePomodoroDisplay()
    {
        _pomodoroToggle.Text = _pomodoro.Enabled ? "포모도로 기법 비활성화" : "포모도로 기법 활성화";
        _pomodoroStatus.Text = PomodoroStatus;
        _icon.Text = _pomodoro.Enabled ? "RoutineHelper | " + PomodoroStatus : "RoutineHelper";
    }

    private void TogglePomodoro()
    {
        try
        {
            var stopping = _pomodoro.Enabled;
            if (stopping) _pomodoro.Stop();
            else _pomodoro.Start();
            _lastHostsCheck = DateTime.MinValue;
            Tick(stopping ? "포모도로를 종료하고 기존 시간표로 복귀했습니다." : "포모도로를 시작합니다. 25분 집중 후 5분 휴식을 반복합니다.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"포모도로 전환 실패: {ex}");
            _icon.ShowBalloonTip(5000, "RoutineHelper", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ReloadConfigIfChanged()
    {
        var modified = File.GetLastWriteTimeUtc(_configPath);
        if (modified == _configWriteTime) return;
        try
        {
            LoadConfiguration();
        }
        catch (Exception ex)
        {
            AppLog.Write($"설정 다시 읽기 실패: {ex}");
        }
    }

    private void LoadConfiguration()
    {
        // 읽기와 검증에 성공한 설정만 교체한다.
        var config = Config.Load(_configPath);
        var modified = File.GetLastWriteTimeUtc(_configPath);
        // 파일 초기화에 성공한 뒤 메모리 상태와 설정을 함께 갱신한다.
        _lastReminderDate = ResetReminderIfTimeChanged(_config, config, _reminderStatePath, _lastReminderDate);
        _config = config;
        _configWriteTime = modified;
        _lastHostsCheck = DateTime.MinValue;
        AppLog.Write($"설정 다시 읽기: {_configPath}; 종료 알림: {ReminderStatus}");
    }

    internal static DateTime ResetReminderIfTimeChanged(Config previous, Config next, string statePath, DateTime lastReminderDate)
    {
        if (previous.ShutdownReminder == next.ShutdownReminder) return lastReminderDate;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        // 빈 파일을 남겨 이전 RoutineChecker 기록으로 되돌아가지 않게 한다.
        File.WriteAllText(statePath, string.Empty);
        return DateTime.MinValue;
    }

    private void OpenMainWindow()
    {
        try
        {
            if (_settings is null || _settings.IsDisposed)
            {
                _settings = new SettingsForm(_configPath, () =>
                {
                    LoadConfiguration();
                    Tick();
                    if (_hostsError != null) throw new IOException("사이트 차단 적용 실패: " + _hostsError);
                    return "저장하고 적용했습니다. 현재 차단: " + (_pomodoro.ShouldBlock(_config.IsActive(DateTime.Now)) ? "활성" : "비활성") + " · " + PomodoroStatus;
                });
                _settings.FormClosed += (_, _) => _settings = null;
                _settings.Show();
            }
            if (_settings.WindowState == FormWindowState.Minimized)
                _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Write($"메인 창 열기 실패: {ex}");
            MessageBox.Show(ex.Message, "RoutineHelper 설정 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenConfiguration()
    {
        try
        {
            var editor = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"))
            {
                UseShellExecute = true
            };
            editor.ArgumentList.Add(_configPath);
            Process.Start(editor)?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write($"설정 파일 열기 실패: {ex}");
            MessageBox.Show($"설정 파일을 열 수 없습니다.\n{_configPath}\n\n{ex.Message}",
                "RoutineHelper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshConfiguration()
    {
        try
        {
            LoadConfiguration();
            Tick("설정을 적용했습니다.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"새로고침 실패: {ex}");
            _icon.ShowBalloonTip(5000, "RoutineHelper", $"새로고침 실패: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void StopBlockedProcesses()
    {
        foreach (var name in _config.BlockedProcesses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var processName = Path.GetFileNameWithoutExtension(name);
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch (Exception ex)
            {
                AppLog.Write($"프로세스 목록 조회 실패 ({name}): {ex.Message}");
                continue;
            }
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        AppLog.Write($"프로세스 종료: {name} (PID {process.Id})");
                    }
                    catch (InvalidOperationException) { /* 조회 직후 종료된 프로세스 */ }
                    catch (Exception ex) { AppLog.Write($"프로세스 종료 실패 ({name}, PID {process.Id}): {ex.Message}"); }
                }
            }
        }
    }

    private void ShowStatus()
    {
        Tick();
        var active = _pomodoro.ShouldBlock(_config.IsActive(DateTime.Now));
        MessageBox.Show($"{PomodoroStatus}\n현재 차단: {(active ? "활성" : "비활성")}\n사이트: {_config.BlockedSites.Count}개\n프로세스: {_config.BlockedProcesses.Count}개\n종료 알림: {ReminderStatus}\n설정 파일: {_configPath}\n로그: {AppLog.PathName}",
            "RoutineHelper 상태", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void ExitThreadCore()
    {
        if (_settings is { IsDisposed: false } window)
        {
            window.Close();
            if (!window.IsDisposed) return;
        }
        try
        {
            HostsManager.Reconcile(false, []);
        }
        catch (HostsMarkerException ex)
        {
            AppLog.Write(ex.Message);
            MessageBox.Show(ex.Message + "\n앱은 종료되지만 남은 차단 항목은 자동 제거되지 않습니다.",
                "RoutineHelper hosts 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Write($"종료 전 사이트 차단 해제 실패: {ex}");
            MessageBox.Show($"사이트 차단을 해제하지 못했습니다. 앱을 계속 실행합니다.\n{ex.Message}",
                "RoutineHelper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _pomodoro.Stop();
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
