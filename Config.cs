using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoutineHelper;

internal sealed class Config
{
    public List<TimeInterval> Intervals { get; set; } = [];
    public List<string> BlockedSites { get; set; } = [];
    public List<string> BlockedProcesses { get; set; } = [];
    public string ShutdownReminder { get; set; } = "02:00";
    public bool ShutdownReminderEnabled { get; set; } = true;

    public static Config Load(string path)
    {
        return Parse(File.ReadAllText(path));
    }

    public static Config Parse(string json)
    {
        var config = JsonSerializer.Deserialize<Config>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("설정 파일이 비어 있습니다.");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (Intervals is null || BlockedSites is null || BlockedProcesses is null)
            throw new InvalidDataException("설정 목록은 null일 수 없습니다.");
        foreach (var interval in Intervals)
        {
            if (interval is null)
                throw new InvalidDataException("시간 구간은 null일 수 없습니다.");
            if (interval.Days is null || interval.Days.Count == 0 || interval.Days.Any(day => day is < 0 or > 6))
                throw new InvalidDataException("각 시간 구간의 요일은 0~6 중 하나 이상 선택해야 합니다.");
            var start = ParseTime(interval.Start);
            var end = ParseTime(interval.End);
            if (start == end)
                throw new InvalidDataException("시작과 종료 시각이 같은 구간은 허용하지 않습니다.");
        }
        ParseTime(ShutdownReminder);

        foreach (var site in BlockedSites)
        {
            if (string.IsNullOrWhiteSpace(site) || site.Contains("//") || site.Contains('/') ||
                site.Any(char.IsWhiteSpace) || site.StartsWith('.') || site.EndsWith('.') ||
                site.Length > 253 || site.Split('.').Any(label => label.Length is < 1 or > 63 ||
                    label.StartsWith('-') || label.EndsWith('-') ||
                    label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
                throw new InvalidDataException($"유효하지 않은 도메인: {site}");
        }
        foreach (var process in BlockedProcesses)
        {
            if (string.IsNullOrWhiteSpace(process) || !process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                process.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
                throw new InvalidDataException($"실행 파일 이름만 입력하세요: {process}");
        }
    }

    public bool IsActive(DateTime now)
    {
        var time = now.TimeOfDay;
        return Intervals.Any(i =>
        {
            var start = ParseTime(i.Start);
            var end = ParseTime(i.End);
            var today = (int)now.DayOfWeek;
            if (start < end)
                return i.Days.Contains(today) && time >= start && time < end;
            // 자정을 넘는 구간은 시작한 날의 요일에 속한다.
            return (i.Days.Contains(today) && time >= start) ||
                   (i.Days.Contains((today + 6) % 7) && time < end);
        });
    }

    public bool IsReminderDue(DateTime now, DateTime lastReminderDate)
    {
        if (!ShutdownReminderEnabled || lastReminderDate.Date == now.Date) return false;
        var reminderTime = ParseTime(ShutdownReminder);
        return now.TimeOfDay >= reminderTime && now.TimeOfDay < reminderTime.Add(TimeSpan.FromMinutes(1));
    }

    public void Save(string path, string expectedContent)
    {
        Validate();
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }) + Environment.NewLine;
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // 창을 연 뒤 다른 편집기에서 바꾼 설정을 덮어쓰지 않는다.
            if (!File.Exists(path) || File.ReadAllText(path) != expectedContent)
                throw new IOException("다른 곳에서 설정 파일을 변경했습니다. '다시 불러오기' 후 편집해 주세요.");
            File.Replace(tempPath, path, null);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public static TimeSpan ParseTime(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToTimeSpan()
            : throw new InvalidDataException($"시각은 HH:mm 형식이어야 합니다: {value}");
}

internal sealed class TimeInterval
{
    // 이전 버전의 요일 없는 설정은 매일 적용한다.
    [JsonConverter(typeof(InlineDaysConverter))]
    public List<int> Days { get; set; } = [0, 1, 2, 3, 4, 5, 6];
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
}

// 다른 JSON 필드는 들여쓰기를 유지하고 요일 배열만 한 줄로 저장한다.
internal sealed class InlineDaysConverter : JsonConverter<List<int>>
{
    public override List<int>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<List<int>>(ref reader, options);

    public override void Write(Utf8JsonWriter writer, List<int> value, JsonSerializerOptions options) =>
        writer.WriteRawValue("[" + string.Join(", ", value.Select(day => day.ToString(CultureInfo.InvariantCulture))) + "]");
}