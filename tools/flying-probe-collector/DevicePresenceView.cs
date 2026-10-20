using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal sealed class DevicePresenceView : UserControl
{
    private readonly Action<DeviceHosts> saveHosts;
    private readonly DataGridView grid = new DataGridView { Name = "HostsGrid", Dock = DockStyle.Fill,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox probeInterval = new TextBox { Name = "ProbeIntervalBox", Width = 45 };
    private readonly TextBox failureThreshold = new TextBox { Name = "FailureThresholdBox", Width = 45 };
    private readonly Button add = new Button { Name = "AddHostButton", Text = "添加设备", AutoSize = true };
    private readonly Button remove = new Button { Name = "RemoveHostButton", Text = "删除选中", AutoSize = true };
    private readonly Button save = new Button { Name = "SaveHostsButton", Text = "保存设备映射", AutoSize = true };
    private readonly Label status = new Label { Name = "HostConfigStatusLabel", Text = "主机名留空时状态为未确认，TXT 仍正常同步", AutoSize = true };
    private bool dirty;
    private bool loading;

    public bool HasUnsavedChanges { get { return dirty; } }

    public DevicePresenceView(DeviceHosts hosts, Action<DeviceHosts> saveHosts)
    {
        this.saveHosts = saveHosts;
        Dock = DockStyle.Fill;
        BuildLayout();
        loading = true;
        probeInterval.Text = hosts.ProbeIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        failureThreshold.Text = hosts.FailureThreshold.ToString(CultureInfo.InvariantCulture);
        foreach (HostTarget target in hosts.Targets) AddRow(target);
        loading = false;
        grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e)
        {
            if (loading || e.RowIndex < 0 || grid.Columns[e.ColumnIndex].ReadOnly) return;
            dirty = true;
            loading = true;
            try { ClearRuntimeStatus(grid.Rows[e.RowIndex]); }
            finally { loading = false; }
        };
        grid.RowsRemoved += delegate { if (!loading) dirty = true; };
        probeInterval.TextChanged += delegate { if (!loading) dirty = true; };
        failureThreshold.TextChanged += delegate { if (!loading) dirty = true; };
        add.Click += delegate { AddRow(new HostTarget { Method = "ICMP" }); dirty = true; };
        remove.Click += delegate
        {
            if (grid.CurrentRow != null) { grid.Rows.Remove(grid.CurrentRow); dirty = true; }
        };
        save.Click += delegate { Save(); };
    }

    public void ShowLoadError(string message)
    {
        status.Text = "映射读取失败，探测未确认；TXT 仍可同步: " + message;
    }

    public void SetEditable(bool enabled)
    {
        grid.ReadOnly = !enabled;
        add.Enabled = enabled;
        remove.Enabled = enabled;
        save.Enabled = enabled;
        probeInterval.Enabled = enabled;
        failureThreshold.Enabled = enabled;
    }

    public void WarnUnsaved()
    {
        status.Text = "设备映射未保存；TXT 可继续采集，探测仅使用已保存配置";
    }

    public void Update(PresenceReport report, PresenceSendResult result)
    {
        if (report == null) return;
        DataGridViewRow row = FindRow(report.MachineName);
        if (row == null)
        {
            loading = true;
            AddRow(new HostTarget { MachineName = report.MachineName, HostName = report.HostName, Method = "ICMP" });
            loading = false;
            row = FindRow(report.MachineName);
        }
        if (row == null) return;
        loading = true;
        try
        {
            row.Cells["ActiveHostName"].Value = string.IsNullOrWhiteSpace(report.HostName) ? "未配置" : report.HostName;
            bool sameHost = string.Equals(Convert.ToString(row.Cells["HostName"].Value).Trim(),
                (report.HostName ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            row.Cells["NetworkState"].Value = dirty || !sameHost ? "草稿未生效" :
                string.IsNullOrWhiteSpace(report.HostName) ? "待配置" : NetworkText(report.NetworkState);
            row.Cells["SourceState"].Value = SourceText(report.SourceState);
            row.Cells["LastProbeAt"].Value = report.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            row.Cells["LastTxtChangeAt"].Value = report.LastTxtChangeAt.HasValue
                ? report.LastTxtChangeAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "暂无";
            row.Cells["QmsReport"].Value = result == null ? "待上报" :
                result.Accepted ? "已上报" : "失败" + (result.HttpStatus > 0 ? " HTTP " + result.HttpStatus : "") +
                    (string.IsNullOrWhiteSpace(result.Error) ? "" : ": " + result.Error);
        }
        finally { loading = false; }
    }

    private void BuildLayout()
    {
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        Controls.Add(layout);
        FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        options.Controls.Add(new Label { Text = "探测间隔(秒)", AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
        options.Controls.Add(probeInterval);
        options.Controls.Add(new Label { Text = "连续失败次数", AutoSize = true, Margin = new Padding(14, 8, 4, 0) });
        options.Controls.Add(failureThreshold);
        options.Controls.Add(new Label { Text = "在线仅代表控制电脑网络可达；近 10 分钟 TXT 更新显示运行中", AutoSize = true,
            Margin = new Padding(20, 8, 4, 0) });
        layout.Controls.Add(options, 0, 0);
        AddTextColumn("MachineName", "设备目录", 90, false);
        AddTextColumn("HostName", "主机名 / 固定 IP", 140, false);
        AddTextColumn("ActiveHostName", "实际探测主机", 130, true);
        DataGridViewComboBoxColumn method = new DataGridViewComboBoxColumn { Name = "ProbeMethod", HeaderText = "方式",
            FlatStyle = FlatStyle.Flat, FillWeight = 60 };
        method.Items.AddRange("ICMP", "TCP");
        grid.Columns.Add(method);
        AddTextColumn("TcpPort", "TCP 端口", 70, false);
        AddTextColumn("NetworkState", "网络", 75, true);
        AddTextColumn("SourceState", "目录", 75, true);
        AddTextColumn("LastProbeAt", "最近探测", 130, true);
        AddTextColumn("LastTxtChangeAt", "最近 TXT 更新", 130, true);
        AddTextColumn("QmsReport", "QMS 状态上报", 150, true);
        grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
        layout.Controls.Add(grid, 0, 1);
        FlowLayoutPanel controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        controls.Controls.Add(add); controls.Controls.Add(remove); controls.Controls.Add(save);
        status.Margin = new Padding(14, 8, 0, 0);
        controls.Controls.Add(status);
        layout.Controls.Add(controls, 0, 2);
    }

    private void AddTextColumn(string name, string caption, float weight, bool readOnly)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = caption, FillWeight = weight, ReadOnly = readOnly });
    }

    private void AddRow(HostTarget target)
    {
        int index = grid.Rows.Add();
        DataGridViewRow row = grid.Rows[index];
        row.Cells["MachineName"].Value = target.MachineName ?? "";
        row.Cells["HostName"].Value = target.HostName ?? "";
        row.Cells["ActiveHostName"].Value = "尚未探测";
        row.Cells["ProbeMethod"].Value = string.IsNullOrWhiteSpace(target.Method) ? "ICMP" : target.Method.ToUpperInvariant();
        row.Cells["TcpPort"].Value = target.TcpPort == 0 ? "" : target.TcpPort.ToString(CultureInfo.InvariantCulture);
        row.Cells["NetworkState"].Value = string.IsNullOrWhiteSpace(target.HostName) ? "待配置" : "未确认";
        row.Cells["SourceState"].Value = "未确认";
        row.Cells["LastProbeAt"].Value = "暂无";
        row.Cells["LastTxtChangeAt"].Value = "暂无";
        row.Cells["QmsReport"].Value = "待上报";
    }

    private DataGridViewRow FindRow(string machine)
    {
        foreach (DataGridViewRow row in grid.Rows)
            if (string.Equals(Convert.ToString(row.Cells["MachineName"].Value), machine, StringComparison.OrdinalIgnoreCase))
                return row;
        return null;
    }

    private static void ClearRuntimeStatus(DataGridViewRow row)
    {
        row.Cells["ActiveHostName"].Value = "尚未探测";
        row.Cells["NetworkState"].Value = "草稿未生效";
        row.Cells["SourceState"].Value = "未确认";
        row.Cells["LastProbeAt"].Value = "暂无";
        row.Cells["LastTxtChangeAt"].Value = "暂无";
        row.Cells["QmsReport"].Value = "待上报";
    }

    private void Save()
    {
        try
        {
            grid.EndEdit();
            if (grid.IsCurrentCellInEditMode) grid.CurrentCell.DetachEditingControl();
            int interval, failures;
            if (!int.TryParse(probeInterval.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out interval))
                throw new ArgumentException("探测间隔必须为整数秒");
            if (interval < 1 || interval > 240)
                throw new ArgumentException("探测间隔必须为 1–240 秒");
            if (!int.TryParse(failureThreshold.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out failures))
                throw new ArgumentException("连续失败次数必须为整数");
            DeviceHosts hosts = new DeviceHosts { ProbeIntervalSeconds = interval, FailureThreshold = failures };
            foreach (DataGridViewRow row in grid.Rows)
            {
                string portText = Convert.ToString(row.Cells["TcpPort"].Value).Trim();
                int port = 0;
                if (portText.Length > 0 && !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                    throw new ArgumentException("TCP 端口必须为数字");
                hosts.Targets.Add(new HostTarget {
                    MachineName = Convert.ToString(row.Cells["MachineName"].Value).Trim(),
                    HostName = Convert.ToString(row.Cells["HostName"].Value).Trim(),
                    Method = Convert.ToString(row.Cells["ProbeMethod"].Value).Trim(),
                    TcpPort = port
                });
            }
            hosts.Validate();
            saveHosts(hosts);
            dirty = false;
            loading = true;
            foreach (DataGridViewRow row in grid.Rows)
            {
                row.Cells["ActiveHostName"].Value = "尚未探测";
                row.Cells["NetworkState"].Value = "待重启探测";
                row.Cells["QmsReport"].Value = "待上报";
            }
            loading = false;
            status.Text = "设备映射已保存；重启采集后生效";
        }
        catch (Exception error)
        {
            status.Text = "设备映射保存失败: " + (error.Message.IndexOf("port", StringComparison.OrdinalIgnoreCase) >= 0
                ? "TCP 端口需为 1–65535" : error.Message);
        }
    }

    private static string NetworkText(string state)
    {
        if (state == "ONLINE") return "在线";
        if (state == "OFFLINE") return "离线";
        return "未确认";
    }

    private static string SourceText(string state)
    {
        if (state == "READABLE") return "可读取";
        if (state == "UNREADABLE") return "不可读取";
        return "未确认";
    }
}
