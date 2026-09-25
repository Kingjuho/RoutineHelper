using RoutineHelper;

internal static class PomodoroTests
{
    public static void Run(string directory)
    {
        var elapsed = TimeSpan.FromHours(7);
        var origin = elapsed;
        var session = new PomodoroSession(() => elapsed);
        Check(!session.Enabled && !session.ShouldBlock(false) && session.ShouldBlock(true), "꺼짐은 시간표 적용");
        session.Start();
        Check(session.Phase == PomodoroPhase.Focus && session.Remaining == TimeSpan.FromMinutes(25), "25분 집중으로 시작");
        Check(session.ShouldBlock(false) && session.ShouldBlock(true), "집중은 시간표보다 우선 차단");
        elapsed += TimeSpan.FromMinutes(10);
        session.Update();
        session.Start();
        Check(session.Remaining == TimeSpan.FromMinutes(15), "이미 활성화된 세션의 중복 시작은 시간 초기화 안 함");
        elapsed = origin + TimeSpan.FromMinutes(25) - TimeSpan.FromMilliseconds(1);
        Check(session.Update() == PomodoroEvent.None && session.Phase == PomodoroPhase.Focus, "집중 종료 직전");
        elapsed = origin + TimeSpan.FromMinutes(25);
        Check(session.Update() == PomodoroEvent.BreakStarted, "25분 경계에서 휴식 전환");
        Check(session.Remaining == TimeSpan.FromMinutes(5) && !session.ShouldBlock(true) && !session.ShouldBlock(false), "휴식은 시간표보다 우선 해제");
        elapsed = origin + TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(29);
        Check(session.Update() == PomodoroEvent.None, "휴식 종료 31초 전에는 예고 없음");
        elapsed += TimeSpan.FromSeconds(1);
        Check(session.Update() == PomodoroEvent.BreakEndingSoon && session.Remaining == TimeSpan.FromSeconds(30), "휴식 종료 30초 전 예고");
        Check(session.Update() == PomodoroEvent.None, "같은 틱 반복 시 예고 중복 없음");
        elapsed += TimeSpan.FromSeconds(10);
        Check(session.Update() == PomodoroEvent.None, "같은 휴식에서 예고 한 번");
        var remainingBeforeSleep = session.Remaining;
        // 절전 시 Windows awake clock 값은 증가하지 않는다.
        Check(session.Update() == PomodoroEvent.None && session.Remaining == remainingBeforeSleep, "절전 동안 경과 시간이 멈추면 남은 시간 유지");
        elapsed = origin + TimeSpan.FromMinutes(30);
        Check(session.Update() == PomodoroEvent.FocusStarted && session.Remaining == TimeSpan.FromMinutes(25), "휴식 종료 후 다음 집중 반복");
        elapsed = origin + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(35);
        var delayed = session.Update();
        Check((delayed & PomodoroEvent.BreakEndingSoon) != 0 && session.Remaining == TimeSpan.FromSeconds(25), "다음 휴식의 30초 경계를 조금 늦게 확인해도 한 번 예고");
        Check(session.Update() == PomodoroEvent.None, "다음 휴식에서도 중복 예고 없음");
        elapsed = origin + TimeSpan.FromHours(20);
        Check(session.Update() == PomodoroEvent.FocusStarted && session.Remaining == TimeSpan.FromMinutes(25), "오랫동안 틱이 지연돼도 과거 알림 없이 현재 구간 복구");
        session.Stop();
        Check(!session.Enabled && session.Remaining == TimeSpan.Zero && session.Update() == PomodoroEvent.None, "비활성화 시 세션 상태 초기화");
        Check(session.ShouldBlock(true) && !session.ShouldBlock(false), "비활성화 후 현재 시간표로 복귀");
        session.Start();
        Check(session.Remaining == TimeSpan.FromMinutes(25), "재활성화하면 새로운 25분");
        elapsed += TimeSpan.FromMinutes(29.5);
        Check((session.Update() & PomodoroEvent.BreakEndingSoon) != 0, "새 세션의 예고 상태도 초기화");
        session.Stop();
        session.Start();
        elapsed += TimeSpan.FromMinutes(30);
        Check(session.Update() == PomodoroEvent.FocusStarted, "휴식 전체를 놓친 경우 오래된 30초 예고를 보내지 않음");

        // 실제 Windows 경과 시간 API가 대상 시스템에서 로드되는지 확인한다.
        var nativeClockSession = new PomodoroSession();
        nativeClockSession.Start();
        nativeClockSession.Update();
        Check(nativeClockSession.Enabled && nativeClockSession.Remaining > TimeSpan.Zero, "Windows awake clock 호출");
        nativeClockSession.Stop();

        var hosts = Path.Combine(directory, "pomodoro.hosts");
        const string personal = "# 사용자 항목\r\n192.0.2.1 personal.example\r\n";
        File.WriteAllText(hosts, personal);
        var store = new HostsFileStore(hosts);
        session.Stop();
        session.Start();
        var focusStart = elapsed;
        store.Reconcile(session.ShouldBlock(false), ["www.example.com"]);
        Check(File.ReadAllText(hosts).Contains("127.0.0.1 example.com"), "시간표 밖 집중 시 실제 hosts 구역 생성");
        var blocked = File.ReadAllText(hosts);
        var mutexName = @"Local\RoutineHelper.Tests." + Guid.NewGuid().ToString("N");
        var legacyName = mutexName + ".Legacy";
        using (var appRunning = new Mutex(true, mutexName))
        {
            Check(!RoutineHelper.Program.TryCleanupIfAppStopped(() => store.Reconcile(false, []), mutexName, legacyName), "트레이 앱 실행 중 예약 정리 건너뜀");
            Check(File.ReadAllText(hosts) == blocked, "예약 정리가 포모도로 차단을 풀지 않음");
        }
        elapsed = focusStart + TimeSpan.FromMinutes(25);
        session.Update();
        store.Reconcile(session.ShouldBlock(true), ["www.example.com"]);
        Check(File.ReadAllText(hosts) == personal, "시간표 차단 구간에도 포모도로 휴식은 hosts 해제");
        var reminder = new Config { ShutdownReminder = "02:00", ShutdownReminderEnabled = true };
        Check(reminder.IsReminderDue(new DateTime(2026, 9, 26, 2, 0, 0), DateTime.MinValue), "휴식 중에도 종료 알림 조건 독립");
        session.Stop();
        store.Reconcile(session.ShouldBlock(true), ["www.example.com"]);
        Check(File.ReadAllText(hosts).Contains("# RoutineHelper BEGIN"), "휴식 중 비활성화하면 활성 시간표로 복귀");
        using (var legacyApp = new Mutex(true, legacyName))
            Check(!RoutineHelper.Program.TryCleanupIfAppStopped(() => throw new Exception("호출되면 안 됨"), mutexName, legacyName), "이전 이름의 앱 실행 중에도 정리 안 함");
        Check(RoutineHelper.Program.TryCleanupIfAppStopped(() => store.Reconcile(false, []), mutexName, legacyName), "앱이 꺼졌다면 시간표와 무관하게 남은 구역 정리");
        Check(File.ReadAllText(hosts) == personal, "예약 정리 후 사용자 내용 보존");
        Check(RoutineHelper.Program.TryCleanupIfAppStopped(() =>
        {
            Check(!RoutineHelper.Program.TryCleanupIfAppStopped(() => throw new Exception("중복 정리"), mutexName, legacyName), "예약 정리 작업 중복 방지");
        }, mutexName, legacyName), "정리 실행 잠금 재사용 가능");
    }

    private static void Check(bool result, string name)
    {
        if (!result) throw new Exception("포모도로 검증 실패: " + name);
    }
}