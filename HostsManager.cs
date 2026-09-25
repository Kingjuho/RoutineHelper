using System.Diagnostics;
using System.Text;

namespace RoutineHelper;

internal static class HostsManager
{
    private static readonly HostsFileStore Store = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts"));

    public static bool Reconcile(bool active, IReadOnlyList<string> sites)
    {
        using var mutex = new Mutex(false, @"Global\RoutineCheckerHosts");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                throw new IOException("hosts 파일 작업 잠금 획득에 실패했습니다.");
        }
        catch (AbandonedMutexException)
        {
            AppLog.Write("이전 hosts 작업이 비정상 종료되어 현재 파일의 관리 구역을 확인합니다.");
        }

        try
        {
            var changed = Store.Reconcile(active, sites);
            if (changed)
            {
                using var process = Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "ipconfig.exe"), "/flushdns")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (process is null || !process.WaitForExit(TimeSpan.FromSeconds(10)) || process.ExitCode != 0)
                    throw new IOException("hosts는 교체했지만 DNS 캐시 갱신에 실패했습니다.");
                AppLog.Write(active && sites.Count > 0 ? "hosts 관리 구역 차단 적용" : "hosts 관리 구역 제거 완료");
            }
            return changed;
        }
        finally { mutex.ReleaseMutex(); }
    }
}

internal sealed class HostsMarkerException(string message) : IOException(message);

// 실제 시스템 hosts 대신 임시 파일로 동일한 처리 경로를 검증할 수 있다.
internal sealed class HostsFileStore(string hostsPath)
{
    public bool Reconcile(bool active, IReadOnlyList<string> sites)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // 삭제된 파일을 오래된 백업으로 재생성하지 않는다.
            if (!File.Exists(hostsPath))
            {
                if (!active || sites.Count == 0) return false;
                throw new FileNotFoundException("hosts 파일이 없습니다. 파일을 확인한 뒤 새로고침하세요.", hostsPath);
            }
            var current = File.ReadAllBytes(hostsPath);
            var clean = RemoveManagedSections(current);
            var desired = active && sites.Count > 0 ? AppendSection(clean, sites) : clean;
            if (current.AsSpan().SequenceEqual(desired)) return false;
            if (TryReplace(current, desired)) return true;
            // 다른 편집기가 바꾼 내용을 다시 읽고 관리 구역만 재계산한다.
        }
        throw new IOException("다른 프로그램이 hosts를 변경하고 있습니다. 잠시 후 새로고침하세요.");
    }

    private static byte[] RemoveManagedSections(byte[] content)
    {
        // Latin1의 1:1 바이트 매핑으로 ANSI/UTF-8 주석도 재인코딩 없이 보존한다.
        // UTF-16/32를 바이트 단위로 처리하면 손상되므로 변경을 거부한다.
        if (content.Contains((byte)0) || content.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ||
            content.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            throw new IOException("hosts는 ANSI 또는 UTF-8 형식이어야 합니다. 파일을 변경하지 않았습니다.");
        using var result = new MemoryStream();
        var offset = content.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        result.Write(content.AsSpan(0, offset));
        string? section = null;
        while (offset < content.Length)
        {
            var lineStart = offset;
            while (offset < content.Length && content[offset] != '\r' && content[offset] != '\n') offset++;
            var text = Encoding.Latin1.GetString(content, lineStart, offset - lineStart).Trim(' ', '\t');
            if (offset < content.Length && content[offset] == '\r') offset++;
            if (offset < content.Length && content[offset] == '\n') offset++;
            var marker = text switch
            {
                "# RoutineHelper BEGIN" => (Name: "RoutineHelper", Begin: true),
                "# RoutineHelper END" => (Name: "RoutineHelper", Begin: false),
                "# RoutineChecker BEGIN" => (Name: "RoutineChecker", Begin: true),
                "# RoutineChecker END" => (Name: "RoutineChecker", Begin: false),
                _ => (Name: (string?)null, Begin: false)
            };
            if (marker.Name != null)
            {
                if (marker.Begin)
                {
                    if (section != null) throw InvalidMarkers();
                    section = marker.Name;
                }
                else
                {
                    if (section != marker.Name) throw InvalidMarkers();
                    section = null;
                }
            }
            else if (section == null)
                result.Write(content.AsSpan(lineStart, offset - lineStart));
        }
        if (section != null) throw InvalidMarkers();
        return result.ToArray();
    }

    private static HostsMarkerException InvalidMarkers() => new(
        "hosts의 RoutineHelper/RoutineChecker BEGIN·END 표시가 손상되었습니다. 파일을 변경하지 않았습니다. 표시와 남은 차단 항목을 직접 확인해 주세요.");

    private static byte[] AppendSection(byte[] clean, IReadOnlyList<string> sites)
    {
        using var result = new MemoryStream();
        result.Write(clean);
        var bomOnly = clean.AsSpan().SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf });
        if (clean.Length > 0 && !bomOnly && clean[^1] != '\n' && clean[^1] != '\r') result.Write("\r\n"u8);
        result.Write("# RoutineHelper BEGIN\r\n"u8);
        foreach (var domain in ExpandDomains(sites).Distinct(StringComparer.OrdinalIgnoreCase))
            result.Write(Encoding.ASCII.GetBytes($"127.0.0.1 {domain}\r\n::1 {domain}\r\n"));
        result.Write("# RoutineHelper END\r\n"u8);
        return result.ToArray();
    }

    private static IEnumerable<string> ExpandDomains(IReadOnlyList<string> sites)
    {
        foreach (var site in sites)
        {
            var domain = site.ToLowerInvariant();
            yield return domain;
            if (domain.StartsWith("www.", StringComparison.Ordinal) && domain.Length > 4)
                yield return domain[4..];
        }
    }

    private bool TryReplace(byte[] expected, byte[] desired)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(hostsPath))!;
        var temporary = Path.Combine(directory, $".routinehelper.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(desired);
                stream.Flush(flushToDisk: true);
            }
            // 최종 비교 직후 다른 프로그램이 파일을 교체하는 경쟁까지 완전히 막지는 못한다.
            // 저장 직전까지 발생한 외부 변경은 덮어쓰지 않고 재시도한다.
            if (!File.Exists(hostsPath) || !File.ReadAllBytes(hostsPath).AsSpan().SequenceEqual(expected)) return false;
            File.Replace(temporary, hostsPath, null);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}