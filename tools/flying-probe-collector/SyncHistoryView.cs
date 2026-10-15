using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class SyncHistoryView : UserControl
{
    private const int PageSize = 100;
    private readonly SyncHistory history;
    private readonly DateTimePicker dayPicker = new DateTimePicker { Name = "HistoryDayPicker", Format = DateTimePickerFormat.Short, Width = 130 };
    private readonly Button recent = new Button { Name = "RecentButton", Text = "最近记录", AutoSize = true };
    private readonly Button older = new Button { Name = "OlderButton", Text = "加载更早", AutoSize = true };
    private readonly Label notice = new Label { Name = "HistoryNoticeLabel", AutoSize = true, Text = "" };
    private readonly DataGridView grid = new DataGridView { Name = "HistoryGrid", Dock = DockStyle.Fill,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox request = new TextBox { Name = "RequestDetailBox", Dock = DockStyle.Fill, Multiline = true,
        ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly TextBox response = new TextBox { Name = "ResponseDetailBox", Dock = DockStyle.Fill, Multiline = true,
        ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private DateTime selectedDay;
    private int skip;
    private bool loading;
    private bool followRecent;
    private bool loadRunning;
    private bool reloadRequested;

    private sealed class PageResult
    {
        public IList<SyncRecord> Rows;
        public int Invalid;
    }

    public SyncHistoryView(SyncHistory history)
    {
        this.history = history;
        Dock = DockStyle.Fill;
        BuildLayout();
        dayPicker.ValueChanged += delegate { if (!loading) ShowDay(dayPicker.Value); };
        recent.Click += delegate { ShowRecent(); };
        older.Click += delegate { skip += PageSize; LoadPage(); };
        grid.SelectionChanged += delegate { ShowSelected(); };
        ShowRecent();
    }

    public void ShowDay(DateTime day)
    {
        followRecent = false;
        selectedDay = day.Date;
        skip = 0;
        loading = true;
        if (selectedDay >= dayPicker.MinDate && selectedDay <= dayPicker.MaxDate) dayPicker.Value = selectedDay;
        loading = false;
        LoadPage();
    }

    public void ShowRecent()
    {
        ShowDay(history.LatestDay());
        followRecent = true;
    }

    public void AddLive(SyncRecord record)
    {
        if (followRecent && record.Time.Date > selectedDay)
        {
            selectedDay = record.Time.Date;
            skip = 0;
            loading = true;
            dayPicker.Value = selectedDay;
            loading = false;
        }
        if (record.Time.Date == selectedDay && skip == 0) LoadPage();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LoadPage();
    }

    private void BuildLayout()
    {
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        FlowLayoutPanel filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        filters.Controls.Add(new Label { Text = "日期:", AutoSize = true, Margin = new Padding(4, 8, 4, 4) });
        filters.Controls.Add(dayPicker);
        filters.Controls.Add(recent);
        filters.Controls.Add(older);
        notice.Margin = new Padding(14, 8, 4, 4);
        filters.Controls.Add(notice);
        layout.Controls.Add(filters, 0, 0);

        string[] headings = { "时间", "设备", "料号", "已测", "好板", "坏板", "结果", "HTTP", "记录号" };
        foreach (string heading in headings) grid.Columns.Add(heading, heading);
        grid.Columns[0].FillWeight = 120;
        grid.Columns[1].FillWeight = 85;
        grid.Columns[2].FillWeight = 100;
        grid.Columns[8].FillWeight = 150;
        layout.Controls.Add(grid, 0, 1);

        TableLayoutPanel details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.Controls.Add(new Label { Text = "请求事件", Dock = DockStyle.Fill }, 0, 0);
        details.Controls.Add(new Label { Text = "响应 / 错误", Dock = DockStyle.Fill }, 1, 0);
        details.Controls.Add(request, 0, 1);
        details.Controls.Add(response, 1, 1);
        layout.Controls.Add(details, 0, 2);
        Controls.Add(layout);
    }

    private void LoadPage()
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (loadRunning) { reloadRequested = true; return; }
        loadRunning = true;
        DateTime day = selectedDay;
        int offset = skip;
        Task.Factory.StartNew(delegate
        {
            int invalid;
            return new PageResult { Rows = history.ReadPage(day, offset, PageSize, out invalid), Invalid = invalid };
        }).ContinueWith(delegate(Task<PageResult> task)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (IsDisposed) return;
                    loadRunning = false;
                    if (reloadRequested || day != selectedDay || offset != skip)
                    {
                        reloadRequested = false;
                        LoadPage();
                        return;
                    }
                    if (task.IsFaulted) { ShowReadError(task.Exception.GetBaseException()); return; }
                    ApplyPage(task.Result.Rows, task.Result.Invalid);
                });
            }
            catch (InvalidOperationException) { }
        });
    }

    private void ApplyPage(IList<SyncRecord> rows, int invalid)
    {
            grid.Rows.Clear();
            foreach (SyncRecord record in rows)
            {
                int index = grid.Rows.Add(record.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    record.Machine, record.Material, record.Tested, record.Good, record.Bad,
                    StageText(record.Stage), record.HttpStatus == 0 ? "" : record.HttpStatus.ToString(CultureInfo.InvariantCulture), record.RecordNo);
                grid.Rows[index].Tag = record;
            }
            older.Enabled = rows.Count == PageSize;
            notice.Text = "第 " + (skip / PageSize + 1).ToString(CultureInfo.InvariantCulture) + " 页" +
                (invalid > 0 ? " · 损坏记录 " + invalid.ToString(CultureInfo.InvariantCulture) + " 行" : "");
            if (grid.Rows.Count > 0) { grid.Rows[0].Selected = true; ShowSelected(); }
            else { request.Clear(); response.Clear(); }
    }

    private void ShowReadError(Exception error)
    {
        notice.Text = "读取历史失败: " + error.GetType().Name;
        grid.Rows.Clear(); request.Clear(); response.Clear();
    }

    private void ShowSelected()
    {
        if (grid.SelectedRows.Count == 0) return;
        SyncRecord record = grid.SelectedRows[0].Tag as SyncRecord;
        if (record == null) return;
        request.Text = record.Request ?? "";
        response.Text = "结果: " + StageText(record.Stage) + Environment.NewLine +
            "HTTP: " + (record.HttpStatus == 0 ? "—" : record.HttpStatus.ToString(CultureInfo.InvariantCulture)) + Environment.NewLine +
            "记录号: " + record.RecordNo + Environment.NewLine + (record.Response ?? "");
    }

    private static string StageText(string stage)
    {
        if (stage == "queued") return "已入队";
        if (stage == "success") return "同步成功";
        if (stage == "failed") return "同步失败";
        return stage;
    }
}
