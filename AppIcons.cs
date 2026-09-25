using System.Drawing;
using System.Windows.Forms;

namespace RoutineHelper;

internal static class AppIcons
{
    // 공유 아이콘은 앱 수명 동안 유지하며 설정 창을 다시 열어도 재로딩하지 않는다.
    public static Icon Tray { get; } = Load(SystemInformation.SmallIconSize.Width);
    public static Icon Window { get; } = Load(32);

    private static Icon Load(int size)
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream("RoutineHelper.AppIcon")
            ?? throw new InvalidOperationException("RoutineHelper 아이콘 리소스를 찾을 수 없습니다.");
        using var icon = new Icon(stream, size, size);
        return (Icon)icon.Clone();
    }
}