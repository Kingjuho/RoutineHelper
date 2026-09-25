using System.Runtime.InteropServices;

namespace RoutineHelper;

internal enum PomodoroPhase { Off, Focus, Break }

[Flags]
internal enum PomodoroEvent { None = 0, FocusStarted = 1, BreakStarted = 2, BreakEndingSoon = 4 }

// 세션 상태는 메모리에만 존재하며 앱의 기존 1초 타이머에서 갱신한다.
internal sealed class PomodoroSession(Func<TimeSpan>? elapsedClock = null)
{
    private static readonly TimeSpan FocusDuration = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromMinutes(5);
    private readonly Func<TimeSpan> _clock = elapsedClock ?? ReadAwakeTime;
    private TimeSpan _started;
    private long _cycle;
    private long _warnedCycle = -1;

    public PomodoroPhase Phase { get; private set; }
    public TimeSpan Remaining { get; private set; }
    public bool Enabled => Phase != PomodoroPhase.Off;

    public void Start()
    {
        if (Enabled) return;
        _started = _clock();
        _cycle = 0;
        _warnedCycle = -1;
        Phase = PomodoroPhase.Focus;
        Remaining = FocusDuration;
    }

    public void Stop()
    {
        Phase = PomodoroPhase.Off;
        Remaining = TimeSpan.Zero;
        _started = TimeSpan.Zero;
        _cycle = 0;
        _warnedCycle = -1;
    }

    public bool ShouldBlock(bool scheduled) => Phase switch
    {
        PomodoroPhase.Focus => true,
        PomodoroPhase.Break => false,
        _ => scheduled
    };

    public PomodoroEvent Update()
    {
        if (!Enabled) return PomodoroEvent.None;
        var elapsed = _clock() - _started;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var cycleLength = FocusDuration.Ticks + BreakDuration.Ticks;
        var cycle = elapsed.Ticks / cycleLength;
        var position = TimeSpan.FromTicks(elapsed.Ticks % cycleLength);
        var phase = position < FocusDuration ? PomodoroPhase.Focus : PomodoroPhase.Break;
        var result = PomodoroEvent.None;
        if (phase != Phase || cycle != _cycle)
            result = phase == PomodoroPhase.Focus ? PomodoroEvent.FocusStarted : PomodoroEvent.BreakStarted;
        Phase = phase;
        _cycle = cycle;
        Remaining = phase == PomodoroPhase.Focus ? FocusDuration - position : FocusDuration + BreakDuration - position;
        // 지연된 틱에서도 현재 휴식의 예고만 보낸다. 이미 끝난 휴식 알림은 보내지 않는다.
        if (phase == PomodoroPhase.Break && Remaining <= TimeSpan.FromSeconds(30) && _warnedCycle != cycle)
        {
            _warnedCycle = cycle;
            result |= PomodoroEvent.BreakEndingSoon;
        }
        return result;
    }

    private static TimeSpan ReadAwakeTime()
    {
        // Windows 경과 시간: 시계 보정의 영향을 받지 않으며 절전·최대 절전 시간은 제외한다.
        // https://learn.microsoft.com/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttime
        if (!QueryUnbiasedInterruptTime(out var ticks))
            throw new InvalidOperationException("포모도로 경과 시간을 읽지 못했습니다.");
        return TimeSpan.FromTicks(checked((long)ticks));
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}