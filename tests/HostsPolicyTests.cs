using System.Text;
using RoutineHelper;

internal static class HostsPolicyTests
{
    public static void Run(string directory)
    {
        var hosts = Path.Combine(directory, "hosts");
        var oldBackup = Path.Combine(directory, "hosts.original.backup");
        File.WriteAllText(oldBackup, "오래된 백업: 읽거나 덮어쓰면 안 됨");
        var store = new HostsFileStore(hosts);
        var original = new byte[] { 0xef, 0xbb, 0xbf }.Concat(
            Encoding.UTF8.GetBytes("# 사용자 주석 한글\r\n127.0.0.1 internal.example\n# 혼합 줄바꿈\r")).ToArray();
        File.WriteAllBytes(hosts, original);
        Check(!store.Reconcile(false, []), "관리 구역 없는 시작은 변경 없음");
        Check(store.Reconcile(true, ["WWW.YouTube.com"]), "관리 구역 추가");
        var blocked = File.ReadAllBytes(hosts);
        Check(blocked.AsSpan(0, original.Length).SequenceEqual(original), "기존 BOM·바이트·줄바꿈 보존");
        var blockedLines = File.ReadAllLines(hosts);
        foreach (var domain in new[] { "www.youtube.com", "youtube.com" })
        {
            Check(blockedLines.Contains("127.0.0.1 " + domain), "www 및 원 도메인 IPv4 차단");
            Check(blockedLines.Contains("::1 " + domain), "www 및 원 도메인 IPv6 차단");
        }
        Check(!store.Reconcile(true, ["www.youtube.com", "youtube.com", "WWW.YOUTUBE.COM"]), "중복 제거 및 동일 적용 시 변경 없음");
        var added = Encoding.UTF8.GetBytes("# 차단 중 사용자 추가\n192.0.2.1 custom.example\n");
        File.WriteAllBytes(hosts, blocked.Concat(added).ToArray());
        store.Reconcile(true, ["other.example"]);
        Check(File.ReadAllBytes(hosts).AsSpan().StartsWith(original.Concat(added).ToArray()), "차단 갱신 시 구역 뒤 사용자 항목 보존");
        Check(!File.ReadAllLines(hosts).Contains("127.0.0.1 youtube.com"), "오래된 관리 항목 제거");
        new HostsFileStore(hosts).Reconcile(false, []);
        Check(File.ReadAllBytes(hosts).SequenceEqual(original.Concat(added)), "강제 종료 후 재시작 시 구역 밖 바이트 보존");

        // 정상 종료 후 수동 편집 → 재시작 → 다음 차단 → 종료 시 변경이 그대로 남는다.
        var edited = Encoding.UTF8.GetBytes("# 앱 종료 후 직접 수정\r\n192.0.2.2 edited.example\r\n");
        File.WriteAllBytes(hosts, edited);
        Check(!new HostsFileStore(hosts).Reconcile(false, []), "앱 재실행은 수동 변경 보존");
        store.Reconcile(true, ["m.youtube.com"]);
        Check(!File.ReadAllLines(hosts).Contains("127.0.0.1 youtube.com"), "www 이외 하위 도메인 임의 확장 안 함");
        store.Reconcile(true, []);
        Check(File.ReadAllBytes(hosts).SequenceEqual(edited), "빈 차단 목록은 구역만 정리");
        Check(File.ReadAllText(oldBackup) == "오래된 백업: 읽거나 덮어쓰면 안 됨", "이전 백업 무시 및 보존");
        Check(!Directory.GetFiles(directory, "*.required").Any(), "복구 상태 파일 생성 안 함");

        var managed = "# RoutineHelper BEGIN\r\n127.0.0.1 example.com\r\n::1 example.com\r\n# RoutineHelper END\r\n";
        var legacy = managed.Replace("RoutineHelper", "RoutineChecker");
        File.WriteAllText(hosts, "# 앞\n" + legacy + "# 중간\r\n" + managed + "# 뒤");
        store.Reconcile(false, []);
        Check(File.ReadAllText(hosts) == "# 앞\n# 중간\r\n# 뒤", "여러 구역과 이전 이름의 구역 제거");
        File.WriteAllText(hosts, "# RoutineHelper BEGIN 설명용 주석\n127.0.0.1 user.example\n");
        Check(!store.Reconcile(false, []), "표시 전체 줄이 일치해야 관리 구역으로 인식");

        foreach (var damaged in new[]
        {
            managed.Replace("# RoutineHelper BEGIN\r\n", ""),
            managed.Replace("# RoutineHelper END\r\n", ""),
            "# RoutineHelper END\n# RoutineHelper BEGIN\n",
            "# RoutineHelper BEGIN\n" + managed + "# RoutineHelper END\n",
            "# RoutineHelper BEGIN\n# RoutineChecker END\n",
            managed + "# RoutineHelper BEGIN\n# 사용자 내용\n"
        })
        {
            File.WriteAllText(hosts, damaged);
            Throws<HostsMarkerException>(() => store.Reconcile(false, []), "표시 손상 시 정리 거부");
            Throws<HostsMarkerException>(() => store.Reconcile(true, ["example.com"]), "표시 손상 시 차단 변경 거부");
            Check(File.ReadAllText(hosts) == damaged, "손상 시 완전한 구역도 포함하여 전체 파일 무변경");
        }
        var unmarked = managed.Replace("# RoutineHelper BEGIN\r\n", "").Replace("# RoutineHelper END\r\n", "");
        File.WriteAllText(hosts, unmarked);
        Check(!store.Reconcile(false, []), "양쪽 표시 삭제 후 남은 항목은 추측 삭제 안 함");
        store.Reconcile(true, ["other.example"]);
        store.Reconcile(false, []);
        Check(File.ReadAllText(hosts) == unmarked, "표시 없는 기존 차단 행은 보존");

        foreach (var baseline in new[]
        {
            Array.Empty<byte>(),
            new byte[] { 0xef, 0xbb, 0xbf },
            new byte[] { 0x23, 0x20, 0xb0, 0xa1, 0x0a }, // ANSI 바이트
            Encoding.UTF8.GetBytes("# 마지막 줄바꿈 없음")
        })
        {
            File.WriteAllBytes(hosts, baseline);
            store.Reconcile(true, ["example.com"]);
            Check(!store.Reconcile(true, ["example.com"]), "줄바꿈 없는 파일에서도 반복 차단 안정성");
            store.Reconcile(false, []);
            var needsNewline = baseline.Length > 3 && baseline[^1] != '\n';
            var expected = needsNewline ? baseline.Concat("\r\n"u8.ToArray()).ToArray() : baseline;
            Check(File.ReadAllBytes(hosts).SequenceEqual(expected), "빈 파일·BOM·ANSI·마지막 줄 내용 보존");
        }
        File.WriteAllBytes(hosts, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("# 사용자 내용")).ToArray());
        var unsupported = File.ReadAllBytes(hosts);
        Throws<IOException>(() => store.Reconcile(true, ["example.com"]), "지원하지 않는 인코딩 변경 거부");
        Check(File.ReadAllBytes(hosts).SequenceEqual(unsupported), "지원하지 않는 인코딩 파일 보존");
        File.WriteAllBytes(hosts, original);
        using (var locked = new FileStream(hosts, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Throws<IOException>(() => store.Reconcile(true, ["example.com"]), "다른 편집기가 잠근 파일 변경 거부");
        Check(File.ReadAllBytes(hosts).SequenceEqual(original), "접근 실패 시 기존 파일 보존");
        File.Delete(hosts);
        Check(!store.Reconcile(false, []), "삭제된 hosts 임의 복원 안 함");
        Throws<FileNotFoundException>(() => store.Reconcile(true, ["example.com"]), "삭제된 hosts 차단 생성 거부");
        Check(!File.Exists(hosts), "이전 백업으로 hosts 재생성 안 함");
        Check(!Directory.GetFiles(directory, ".routinehelper.*.tmp").Any(), "임시 교체 파일 정리");
    }

    private static void Check(bool result, string name)
    {
        if (!result) throw new Exception("hosts 검증 실패: " + name);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("hosts 검증 실패: " + name);
    }
}