namespace RoutineHelper;

internal static class ProcessProtection
{
    // 종료하면 앱 자체, Windows 세션 또는 기본 보안 기능이 중단될 수 있는 대상.
    // 모든 Windows 프로세스를 열거하는 목록은 아니며 일반 앱은 계속 차단할 수 있다.
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RoutineHelper.exe", "RoutineChecker.exe",
        "Idle.exe", "System.exe", "Registry.exe",
        "smss.exe", "csrss.exe", "wininit.exe", "services.exe",
        "lsass.exe", "LsaIso.exe", "svchost.exe", "winlogon.exe", "LogonUI.exe",
        "dwm.exe", "explorer.exe", "sihost.exe", "fontdrvhost.exe",
        "MsMpEng.exe", "SecurityHealthService.exe"
    };

    public static bool IsProtectedName(string executableName) => ProtectedNames.Contains(executableName);

    // EXE 이름이 바뀌어도 현재 프로세스는 PID로 보호한다.
    public static bool ShouldSkip(int processId, string executableName) =>
        processId == Environment.ProcessId || processId is 0 or 4 || IsProtectedName(executableName);
}
