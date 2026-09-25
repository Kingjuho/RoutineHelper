namespace RoutineHelper;

internal static class AppLog
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoutineHelper");

    public static string PathName => Path.Combine(DirectoryPath, "app.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* 로깅 실패가 차단 처리나 알림을 중단하지 않도록 한다. */ }
    }
}
