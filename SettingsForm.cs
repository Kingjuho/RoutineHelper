using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace RoutineHelper;

internal sealed class SettingsForm : Form
{
    private readonly string _path;
    private readonly Func<string> _apply;
    private readonly DataGridView _schedule = new();
    private readonly DateTimePicker _start = TimePicker("StartTime");
    private readonly DateTimePicker _end = TimePicker("EndTime");
    private readonly DateTimePicker _reminder = TimePicker("ReminderTime");
    private readonly CheckBox _reminderEnabled = new()
    {
        Name = "ReminderEnabled", Text = "매일 종료 알림", AutoSize = true,
        Margin = new Padding(0, 5, 10, 5)
    };
    private readonly TextBox _sites = LinesBox("BlockedSites");
    private readonly TextBox _processes = LinesBox("BlockedProcesses");
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
    private string _snapshot = "";
    private bool _loading;
    private bool _selecting;
    private bool _dirty;

    public SettingsForm(string path, Func<string> apply)
    {
        _path = path;
        _apply = apply;
        Text = "RoutineHelper — 설정";
        Icon = AppIcons.Window;
        Font = new Font("맑은 고딕", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 650);
        MinimumSize = new Size(800, 620);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        root.Controls.Add(new Label
        {
            Text = "RoutineHelper", AutoSize = true,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold), Margin = new Padding(0, 0, 0, 16)
        }, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill, Name = "SettingsTabs" };
        var scheduleTab = new TabPage("요일 · 시간표") { Padding = new Padding(12), UseVisualStyleBackColor = true };
        var targetsTab = new TabPage("차단 대상") { Padding = new Padding(12), UseVisualStyleBackColor = true };
        tabs.TabPages.AddRange([scheduleTab, targetsTab]);
        root.Controls.Add(tabs, 0, 1);
        BuildSchedule(scheduleTab);
        BuildTargets(targetsTab);

        var reminderRow = FlowRow();
        reminderRow.Margin = new Padding(0, 12, 0, 4);
        reminderRow.Controls.Add(_reminderEnabled);
        reminderRow.Controls.Add(_reminder);
        reminderRow.Controls.Add(Caption("알림만 표시하며 PC를 자동 종료하지 않습니다."));
        root.Controls.Add(reminderRow, 0, 2);
        var pathLabel = new Label { Text = "설정 파일: " + path, AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 4) };
        root.Controls.Add(pathLabel, 0, 3);
        root.Controls.Add(_status, 0, 4);
        var buttons = FlowRow();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Margin = new Padding(0, 8, 0, 0);
        var save = Button("저장하고 적용", (_, _) => SaveChanges());
        save.Name = "SaveSettings";
        buttons.Controls.Add(save);
        buttons.Controls.Add(Button("다시 불러오기", (_, _) => ReloadFromDisk()));
        buttons.Controls.Add(Button("닫기", (_, _) => Close()));
        root.Controls.Add(buttons, 0, 5);
        AcceptButton = save;
        _reminderEnabled.CheckedChanged += (_, _) =>
        {
            _reminder.Enabled = _reminderEnabled.Checked;
            MarkDirty();
        };
        _reminder.ValueChanged += (_, _) => MarkDirty();
        _sites.TextChanged += (_, _) => MarkDirty();
        _processes.TextChanged += (_, _) => MarkDirty();
        FormClosing += (_, e) => e.Cancel = !ConfirmPendingChanges();
        LoadFromDisk();
    }

    private void BuildSchedule(Control parent)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        parent.Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = "요일을 체크하고, 행을 선택해 아래에서 시간을 조절하세요.\n자정을 넘는 구간은 시작 요일 기준입니다. 겹치는 구간은 함께 적용됩니다.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        _schedule.Name = "Schedule";
        _schedule.Dock = DockStyle.Fill;
        _schedule.AllowUserToAddRows = false;
        _schedule.AllowUserToDeleteRows = false;
        _schedule.AllowUserToResizeRows = false;
        _schedule.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _schedule.BackgroundColor = SystemColors.Window;
        _schedule.BorderStyle = BorderStyle.FixedSingle;
        _schedule.RowHeadersVisible = false;
        _schedule.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _schedule.MultiSelect = false;
        _schedule.RowTemplate.Height = 32;
        _schedule.ColumnHeadersHeight = 36;
        _schedule.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _schedule.Columns.Add(new DataGridViewTextBoxColumn { Name = "Start", HeaderText = "시작", ReadOnly = true, FillWeight = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
        _schedule.Columns.Add(new DataGridViewTextBoxColumn { Name = "End", HeaderText = "종료", ReadOnly = true, FillWeight = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
        string[] days = ["일", "월", "화", "수", "목", "금", "토"];
        for (var day = 0; day < days.Length; day++)
            _schedule.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Day" + day, HeaderText = days[day], FillWeight = 70, SortMode = DataGridViewColumnSortMode.NotSortable });
        _schedule.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_schedule.IsCurrentCellDirty) _schedule.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _schedule.CellValueChanged += (_, _) => MarkDirty();
        _schedule.CurrentCellChanged += (_, _) => SelectTimes();
        _schedule.DataError += (_, e) => { e.ThrowException = false; };
        layout.Controls.Add(_schedule, 0, 1);

        var times = FlowRow();
        times.Margin = new Padding(0, 10, 0, 0);
        times.Controls.Add(Caption("선택한 구간"));
        times.Controls.Add(_start);
        times.Controls.Add(Caption("~"));
        times.Controls.Add(_end);
        times.Controls.Add(Button("평일", (_, _) => SetDays([1, 2, 3, 4, 5])));
        times.Controls.Add(Button("주말", (_, _) => SetDays([0, 6])));
        times.Controls.Add(Button("매일", (_, _) => SetDays([0, 1, 2, 3, 4, 5, 6])));
        layout.Controls.Add(times, 0, 2);
        _start.ValueChanged += (_, _) => UpdateTimes();
        _end.ValueChanged += (_, _) => UpdateTimes();
        var actions = FlowRow();
        var add = Button("+ 시간 추가", (_, _) =>
        {
            var index = _schedule.Rows.Add("11:30", "13:00", true, true, true, true, true, true, true);
            _schedule.CurrentCell = _schedule.Rows[index].Cells[0];
            MarkDirty();
        });
        add.Name = "AddInterval";
        actions.Controls.Add(add);
        actions.Controls.Add(Button("선택 삭제", (_, _) =>
        {
            if (_schedule.CurrentRow is not { } row) return;
            _schedule.Rows.Remove(row);
            SelectTimes();
            MarkDirty();
        }));
        layout.Controls.Add(actions, 0, 3);
    }

    private void BuildTargets(Control parent)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "차단 사이트 · 한 줄에 도메인 하나\n예: www.youtube.com (youtube.com도 함께 차단)", AutoSize = true, Margin = new Padding(0, 0, 8, 10) }, 0, 0);
        layout.Controls.Add(new Label { Text = "차단 프로세스 · 한 줄에 파일 이름 하나\n예: GenshinImpact.exe (경로 제외)", AutoSize = true, Margin = new Padding(8, 0, 0, 10) }, 1, 0);
        _sites.Margin = new Padding(0, 0, 8, 0);
        _processes.Margin = new Padding(8, 0, 0, 0);
        layout.Controls.Add(_sites, 0, 1);
        layout.Controls.Add(_processes, 1, 1);
        parent.Controls.Add(layout);
    }

    private void SelectTimes()
    {
        _selecting = true;
        try
        {
            var row = _schedule.CurrentRow;
            _start.Enabled = _end.Enabled = row != null;
            if (row != null)
            {
                _start.Value = DateTime.Today.Add(Config.ParseTime((Convert.ToString(row.Cells[0].Value) ?? "")));
                _end.Value = DateTime.Today.Add(Config.ParseTime((Convert.ToString(row.Cells[1].Value) ?? "")));
            }
        }
        finally { _selecting = false; }
    }

    private void UpdateTimes()
    {
        if (_selecting || _loading || _schedule.CurrentRow is not { } row) return;
        row.Cells[0].Value = FormatTime(_start);
        row.Cells[1].Value = FormatTime(_end);
    }

    private void SetDays(int[] days)
    {
        if (_schedule.CurrentRow is not { } row) return;
        _schedule.EndEdit();
        for (var day = 0; day < 7; day++) row.Cells[day + 2].Value = days.Contains(day);
    }

    private void LoadFromDisk()
    {
        var snapshot = File.ReadAllText(_path);
        var config = Config.Parse(snapshot);
        _loading = true;
        try
        {
            _schedule.Rows.Clear();
            foreach (var interval in config.Intervals)
            {
                var values = new object[9];
                values[0] = interval.Start;
                values[1] = interval.End;
                for (var day = 0; day < 7; day++) values[day + 2] = interval.Days.Contains(day);
                _schedule.Rows.Add(values);
            }
            _sites.Lines = config.BlockedSites.ToArray();
            _processes.Lines = config.BlockedProcesses.ToArray();
            _reminder.Value = DateTime.Today.Add(Config.ParseTime(config.ShutdownReminder));
            _reminderEnabled.Checked = config.ShutdownReminderEnabled;
            _reminder.Enabled = _reminderEnabled.Checked;
            _snapshot = snapshot;
            _dirty = false;
            _status.Text = "설정을 불러왔습니다. 창을 닫아도 트레이에서 계속 실행됩니다.";
            _status.ForeColor = SystemColors.ControlText;
            SelectTimes();
        }
        finally { _loading = false; }
    }

    private Config ReadEditor()
    {
        _schedule.EndEdit();
        var config = new Config
        {
            BlockedSites = ReadLines(_sites),
            BlockedProcesses = ReadLines(_processes),
            ShutdownReminder = FormatTime(_reminder),
            ShutdownReminderEnabled = _reminderEnabled.Checked
        };
        foreach (DataGridViewRow row in _schedule.Rows)
        {
            var interval = new TimeInterval
            {
                Start = (Convert.ToString(row.Cells[0].Value) ?? ""),
                End = (Convert.ToString(row.Cells[1].Value) ?? ""),
                Days = Enumerable.Range(0, 7).Where(day => row.Cells[day + 2].Value is true).ToList()
            };
            if (interval.Days.Count == 0 || interval.Start == interval.End)
            {
                _schedule.CurrentCell = row.Cells[0];
                throw new InvalidDataException($"{row.Index + 1}번 구간: 요일을 하나 이상 선택하고 시작·종료 시각을 다르게 지정하세요.");
            }
            config.Intervals.Add(interval);
        }
        config.Validate();
        return config;
    }

    private bool SaveChanges()
    {
        try
        {
            ReadEditor().Save(_path, _snapshot);
            _snapshot = File.ReadAllText(_path);
            _dirty = false;
            try
            {
                _status.Text = _apply();
                _status.ForeColor = SystemColors.ControlText;
            }
            catch (Exception ex)
            {
                _status.Text = "저장 완료 · 적용 실패: " + ex.Message;
                _status.ForeColor = Color.Firebrick;
                MessageBox.Show(this, "파일은 저장했지만 적용하지 못했습니다. 트레이에서 새로고침해 주세요.\n" + ex.Message,
                    "RoutineHelper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "설정을 저장할 수 없습니다", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void ReloadFromDisk()
    {
        if (_dirty && MessageBox.Show(this, "저장하지 않은 변경을 버리고 파일을 다시 불러올까요?", "RoutineHelper",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { LoadFromDisk(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "설정을 불러올 수 없습니다", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private bool ConfirmPendingChanges()
    {
        if (!_dirty) return true;
        return MessageBox.Show(this, "변경한 설정을 저장하고 적용할까요?", "RoutineHelper",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question) switch
        {
            DialogResult.Yes => SaveChanges(),
            DialogResult.No => true,
            _ => false
        };
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        _status.Text = "저장하지 않은 변경이 있습니다.";
        _status.ForeColor = SystemColors.ControlText;
    }

    private static string FormatTime(DateTimePicker picker) => picker.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
    private static List<string> ReadLines(TextBox box) => box.Lines.Select(line => line.Trim())
        .Where(line => line.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static DateTimePicker TimePicker(string name) => new()
    {
        Name = name, Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 95
    };
    private static TextBox LinesBox(string name) => new()
    {
        Name = name, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill
    };
    private static FlowLayoutPanel FlowRow() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 5, 10, 5) };
    private static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(72, 30), Padding = new Padding(6, 0, 6, 0), Margin = new Padding(0, 0, 8, 4) };
        button.Click += click;
        return button;
    }
}