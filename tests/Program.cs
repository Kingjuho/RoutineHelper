using System.Text;
using RoutineHelper;
using System.Text.Json;
using System.Windows.Forms;
using System.Drawing;

var config = new Config
{
    Intervals =
    [
        new TimeInterval { Start = "11:30", End = "13:00" },
        new TimeInterval { Start = "23:00", End = "01:00" }
    ],
    BlockedSites = ["example.com"],
    BlockedProcesses = ["Game.exe"]
};
config.Validate();
Check(config.IsActive(new DateTime(2026, 9, 25, 11, 30, 0)), "구간 시작");
Check(!config.IsActive(new DateTime(2026, 9, 25, 13, 0, 0)), "구간 끝");
Check(config.IsActive(new DateTime(2026, 9, 25, 0, 30, 0)), "자정 넘김");
Check(!config.IsActive(new DateTime(2026, 9, 25, 2, 0, 0)), "자정 넘김 종료");

// 0=일요일인 Windows DayOfWeek와 JSON 숫자 요일의 매핑, 자정 경계를 검증한다.
var sunday = new DateTime(2026, 9, 20);
for (var selectedDay = 0; selectedDay < 7; selectedDay++)
{
    var weekly = new Config { Intervals = [new TimeInterval { Days = [selectedDay], Start = "11:30", End = "13:00" }] };
    weekly.Validate();
    for (var day = 0; day < 7; day++)
    {
        Check(weekly.IsActive(sunday.AddDays(day).AddHours(12)) == (day == selectedDay), "선택한 요일만 낮 차단");
        Check(!weekly.IsActive(sunday.AddDays(day).AddHours(13)), "요일별 종료 경계");
    }
    weekly.Intervals[0].Start = "23:00";
    weekly.Intervals[0].End = "01:00";
    var startDate = sunday.AddDays(selectedDay);
    Check(!weekly.IsActive(startDate.AddHours(0.5)), "이전 요일 구간은 선택되지 않음");
    Check(!weekly.IsActive(startDate.AddHours(22.99)), "자정 구간 시작 전");
    Check(weekly.IsActive(startDate.AddHours(23)), "자정 구간 시작 포함");
    Check(weekly.IsActive(startDate.AddDays(1)), "다음 날 자정 포함");
    Check(weekly.IsActive(startDate.AddDays(1).AddMinutes(59)), "다음 날까지 이어짐");
    Check(!weekly.IsActive(startDate.AddDays(1).AddHours(1)), "자정 구간 종료 제외");
    Check(!weekly.IsActive(startDate.AddDays(1).AddHours(23)), "다음 날 새 구간은 시작 안 함");
}
var legacy = Config.Parse("""{"intervals":[{"start":"11:30","end":"13:00"}]}""");
Check(legacy.Intervals[0].Days.SequenceEqual(Enumerable.Range(0, 7)), "기존 JSON 매일 호환");
foreach (var invalidDays in new[] { "[]", "[-1]", "[7]", "null", "[\"Mon\"]" })
    ExpectInvalidConfig(() => Config.Parse("{\"intervals\":[{\"days\":" + invalidDays + ",\"start\":\"11:30\",\"end\":\"13:00\"}]}"), "잘못된 요일 거부");
ExpectInvalidConfig(() => Config.Parse("""{"intervals":[null]}"""), "null 구간 거부");
var overlapping = new Config { Intervals =
[
    new TimeInterval { Days = [5], Start = "23:00", End = "01:00" },
    new TimeInterval { Days = [6], Start = "00:30", End = "02:00" }
] };
Check(overlapping.IsActive(new DateTime(2026, 9, 26, 1, 30, 0)), "겹친 구간 중 남은 구간 적용");
var testDirectory = Directory.CreateTempSubdirectory("RoutineHelper.Tests.");
try
{
    var configPath = Path.Combine(testDirectory.FullName, "config.json");
    var originalConfig = """{"intervals":[{"start":"11:30","end":"13:00"}],"blockedSites":[],"blockedProcesses":[],"shutdownReminder":"02:00"}""";
    File.WriteAllText(configPath, originalConfig);
    var editedConfig = Config.Load(configPath);
    editedConfig.Intervals[0].Days = [1, 2, 3, 4, 5];
    editedConfig.Save(configPath, originalConfig);
    var savedConfig = File.ReadAllText(configPath);
    Check(savedConfig.Contains("\"days\": [1, 2, 3, 4, 5]"), "요일 배열 한 줄 저장");
    using (var json = JsonDocument.Parse(savedConfig))
        Check(json.RootElement.GetProperty("intervals")[0].GetProperty("days").EnumerateArray()
            .All(day => day.ValueKind == JsonValueKind.Number), "요일은 JSON 숫자로 저장");
    Check(Config.Load(configPath).Intervals[0].Days.SequenceEqual([1, 2, 3, 4, 5]), "요일 저장 후 다시 읽기");
    editedConfig.Intervals[0].Days = [];
    ExpectInvalidConfig(() => editedConfig.Save(configPath, savedConfig), "저장 전 검증");
    Check(File.ReadAllText(configPath) == savedConfig, "유효하지 않은 설정은 파일 보존");
    editedConfig.Intervals[0].Days = [6];
    File.WriteAllText(configPath, originalConfig);
    ExpectIOException(() => editedConfig.Save(configPath, savedConfig), "외부 설정 변경 덮어쓰기 거부");
    Check(File.ReadAllText(configPath) == originalConfig, "외부 편집 내용 보존");
    Check(Directory.GetFiles(testDirectory.FullName, "*.tmp").Length == 0, "저장 실패 임시 파일 정리");

    // 실제 hosts나 차단 엔진을 호출하지 않고 설정 창의 편집→저장→재열기 경로를 검증한다.
    Exception? uiError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            var applied = 0;
            using (var form = new SettingsForm(configPath, () => { applied++; return "테스트 설정 저장 완료"; }))
            {
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                Application.DoEvents();
                var grid = (DataGridView)form.Controls.Find("Schedule", true).Single();
                Check(grid.Rows.Count == 1, "GUI 기존 시간표 로드");
                Check(Enumerable.Range(0, 7).All(day => grid.Rows[0].Cells[day + 2].Value is true), "GUI 기존 설정 매일 체크");
                grid.CurrentCell = grid.Rows[0].Cells[0];
                grid.Rows[0].Cells[2].Value = false;
                grid.Rows[0].Cells[8].Value = false;
                var startPicker = (DateTimePicker)form.Controls.Find("StartTime", true).Single();
                startPicker.Value = DateTime.Today.AddHours(10);
                ((Button)form.Controls.Find("AddInterval", true).Single()).PerformClick();
                Check(grid.Rows.Count == 2, "GUI 구간 추가");
                Check(startPicker.Value.TimeOfDay == new TimeSpan(11, 30, 0), "행 변경 시 시간 선택기도 변경");
                var endPicker = (DateTimePicker)form.Controls.Find("EndTime", true).Single();
                endPicker.Value = DateTime.Today.AddHours(14);
                Check((string?)grid.Rows[0].Cells[1].Value == "13:00" && (string?)grid.Rows[1].Cells[1].Value == "14:00", "선택한 행만 시각 수정");
                ((DateTimePicker)form.Controls.Find("ReminderTime", true).Single()).Value = DateTime.Today.AddHours(3);
                ((TextBox)form.Controls.Find("BlockedSites", true).Single()).Text = " example.com \r\n\r\nexample.com\r\nwww.example.com";
                ((TextBox)form.Controls.Find("BlockedProcesses", true).Single()).Text = "Game.exe";
                ((Button)form.Controls.Find("SaveSettings", true).Single()).PerformClick();
                var saved = Config.Load(configPath);
                Check(applied == 1, "GUI 저장 직후 적용 콜백");
                Check(saved.Intervals.Count == 2 && saved.Intervals[0].Days.SequenceEqual([1, 2, 3, 4, 5]), "GUI 체크 요일 저장");
                Check(saved.Intervals[0].Start == "10:00", "GUI 시간 선택기 저장");
                Check(saved.BlockedSites.SequenceEqual(["example.com", "www.example.com"]), "GUI 빈 줄 공백 중복 정리");
                Check(saved.BlockedProcesses.SequenceEqual(["Game.exe"]) && saved.ShutdownReminder == "03:00", "GUI 프로세스 및 알림 저장");
                var renderIndex = Array.IndexOf(args, "--render-settings");
                if (renderIndex >= 0)
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(args[renderIndex + 1]);
                    Console.WriteLine("설정 창 렌더링: " + args[renderIndex + 1]);
                    ((TabControl)form.Controls.Find("SettingsTabs", true).Single()).SelectedIndex = 1;
                    form.Size = form.MinimumSize;
                    Application.DoEvents();
                    using var targetsBitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(targetsBitmap, new Rectangle(0, 0, form.Width, form.Height));
                    targetsBitmap.Save(Path.ChangeExtension(args[renderIndex + 1], "targets.png"));
                }
                form.Close();
            }
            using var reopened = new SettingsForm(configPath, () => "적용");
            var restoredGrid = (DataGridView)reopened.Controls.Find("Schedule", true).Single();
            Check(restoredGrid.Rows.Count == 2 && restoredGrid.Rows[0].Cells[2].Value is false, "창 재열기 저장 내용 유지");
        }
        catch (Exception ex) { uiError = ex; }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (uiError != null) throw new Exception("GUI 검증 실패", uiError);
    HostsPolicyTests.Run(testDirectory.FullName);
}
finally
{
    // 이 테스트가 직접 생성한 임시 디렉터리만 제거한다.
    testDirectory.Delete(recursive: true);
}
Console.WriteLine("모든 검증 통과: 숫자 요일·자정 경계·설정 저장·GUI 편집 및 hosts 관리 구역 정리·사용자 내용 보존");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"검증 실패: {name}");
}

static void ExpectIOException(Action action, string name)
{
    try { action(); }
    catch (IOException) { return; }
    throw new Exception($"검증 실패: {name}");
}

static void ExpectInvalidConfig(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    catch (JsonException) { return; }
    throw new Exception($"검증 실패: {name}");
}