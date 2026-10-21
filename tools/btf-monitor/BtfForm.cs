using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal sealed class BtfMainForm : Form
{
    private readonly string settingsPath;
    private readonly object logLock = new object();
    private readonly Dictionary<string, MachineView> machines = new Dictionary<string, MachineView>(StringComparer.OrdinalIgnoreCase);
    private readonly Panel machineArea = new Panel();
    private readonly TableLayoutPanel machineGrid = new TableLayoutPanel();
    private readonly TextBox logBox = new TextBox();
    private readonly Label status = new Label();
    private readonly Button pauseButton = new Button();
    private readonly Button startButton = new Button();
    private readonly Timer statusTimer = new Timer();
    private BtfSettings settings;
    private BtfServer server;
    private bool paused;

    internal BtfMainForm(string settingsPath)
    {
        this.settingsPath = settingsPath;
        Text = "标特福锣机监控";
        ClientSize = new Size(1100, 730);
        MinimumSize = new Size(760, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        Build();
        Shown += delegate { StartServer(); };
        FormClosing += delegate { StopServer(); };
        statusTimer.Interval = 1000;
        statusTimer.Tick += delegate { UpdateStatus(); };
        statusTimer.Start();
    }

    private void Build()
    {
        MenuStrip menu = new MenuStrip();
        ToolStripMenuItem setup = new ToolStripMenuItem("设置");
        setup.DropDownItems.Add("系统参数", null, delegate { OpenSettings(0); });
        setup.DropDownItems.Add("ERP / QMS 参数", null, delegate { OpenSettings(1); });
        setup.DropDownItems.Add("S1 MQTT 参数", null, delegate { OpenSettings(2); });
        menu.Items.Add(setup);
        MainMenuStrip = menu;

        machineArea.Dock = DockStyle.Fill;
        machineArea.AutoScroll = true;
        machineArea.Padding = new Padding(8);
        machineArea.Resize += delegate { if (settings != null) RebuildGrid(); };
        machineGrid.Dock = DockStyle.Top;
        machineGrid.ColumnCount = 2;
        machineGrid.RowCount = 2;
        machineGrid.Padding = new Padding(0);
        machineArea.Controls.Add(machineGrid);

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 205;
        bottom.Padding = new Padding(8);
        Panel buttons = new Panel();
        buttons.Dock = DockStyle.Right;
        buttons.Width = 145;
        pauseButton.Text = "暂停接收";
        pauseButton.SetBounds(15, 15, 115, 34);
        pauseButton.Click += delegate { TogglePause(); };
        Button clear = new Button();
        clear.Text = "清空显示";
        clear.SetBounds(15, 57, 115, 34);
        clear.Click += delegate { ClearDisplay(); };
        startButton.Text = "停止监听";
        startButton.SetBounds(15, 99, 115, 34);
        startButton.Click += delegate { if (server == null) StartServer(); else StopServer(); };
        buttons.Controls.Add(pauseButton);
        buttons.Controls.Add(clear);
        buttons.Controls.Add(startButton);
        logBox.Dock = DockStyle.Fill;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.Font = new Font("Consolas", 9F);
        bottom.Controls.Add(logBox);
        bottom.Controls.Add(buttons);

        status.Dock = DockStyle.Bottom;
        status.Height = 30;
        status.Padding = new Padding(8, 5, 0, 0);
        status.Text = "服务未启动";
        Controls.Add(machineArea);
        Controls.Add(bottom);
        Controls.Add(status);
        menu.Dock = DockStyle.Top;
        Controls.Add(menu);
    }

    private void StartServer()
    {
        if (server != null) return;
        try
        {
            settings = BtfSettings.Load(settingsPath);
            BtfServer next = new BtfServer(settings, ReceiveSnapshot, Report);
            try { next.Start(); }
            catch { next.Dispose(); throw; }
            server = next;
            paused = false;
            pauseButton.Text = "暂停接收";
            startButton.Text = "停止监听";
            RebuildGrid();
        }
        catch (Exception error)
        {
            Report("启动失败：" + error.Message);
            MessageBox.Show(this, error.Message, "标特福监听启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            startButton.Text = "启动监听";
        }
        UpdateStatus();
    }

    private void StopServer()
    {
        BtfServer active = server;
        server = null;
        if (active != null)
        {
            active.Dispose();
            Report("标特福监听已停止；待发队列保留。 ");
        }
        startButton.Text = "启动监听";
        UpdateStatus();
    }

    private void TogglePause()
    {
        if (server == null) return;
        paused = !paused;
        server.PauseReceive(paused);
        pauseButton.Text = paused ? "继续接收" : "暂停接收";
        UpdateStatus();
    }

    private void ClearDisplay()
    {
        logBox.Clear();
        foreach (MachineView view in machines.Values) { view.Lines.Clear(); view.Output.Clear(); }
    }

    private void OpenSettings(int tab)
    {
        try
        {
            BtfSettings editable = BtfSettings.Load(settingsPath);
            using (BtfSettingsForm dialog = new BtfSettingsForm(settingsPath, editable, tab))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
            }
            StopServer();
            StartServer();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ReceiveSnapshot(BtfSnapshot snapshot)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((MethodInvoker)delegate { ShowSnapshot(snapshot); }); }
        catch (InvalidOperationException) { }
    }

    private void ShowSnapshot(BtfSnapshot snapshot)
    {
        MachineView view;
        if (!machines.TryGetValue(snapshot.MachineName, out view))
        {
            view = new MachineView(snapshot.MachineName);
            machines.Add(snapshot.MachineName, view);
            RebuildGrid();
        }
        view.LastSeen = DateTime.Now;
        view.LastState = snapshot.State;
        view.Title.Text = snapshot.MachineName + "  ·  " + StateText(snapshot.State);
        string line = snapshot.DeviceTime + "  计数 " + snapshot.WorkCount + "，开机 " + FormatDuration(snapshot.BootSeconds)
            + "，加工 " + FormatDuration(snapshot.WorkSeconds) + "，完成数 " + snapshot.FinishedCount
            + "，进度 " + snapshot.ProgressPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            + "，锣程 " + snapshot.RouteLength.ToString("0.###", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(snapshot.ProgramName)) line += "，程序 " + snapshot.ProgramName;
        if (!string.IsNullOrWhiteSpace(snapshot.ScreenText)) line += "，报警 " + snapshot.ScreenText;
        view.Lines.Add(line);
        if (view.Lines.Count > 200) view.Lines.RemoveAt(0);
        view.Output.Lines = view.Lines.ToArray();
        view.Output.SelectionStart = view.Output.TextLength;
        view.Output.ScrollToCaret();
        UpdateStatus();
    }

    private void RebuildGrid()
    {
        int columns = settings == null ? 2 : settings.Columns;
        int visibleRows = settings == null ? 2 : settings.Rows;
        machineGrid.SuspendLayout();
        machineGrid.Controls.Clear();
        machineGrid.ColumnStyles.Clear();
        machineGrid.RowStyles.Clear();
        machineGrid.ColumnCount = columns;
        int rows = Math.Max(visibleRows, (machines.Count + columns - 1) / columns);
        machineGrid.RowCount = rows;
        machineGrid.Width = Math.Max(600, machineArea.ClientSize.Width - 22);
        machineGrid.Height = rows * 180;
        for (int column = 0; column < columns; column++) machineGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
        for (int row = 0; row < rows; row++) machineGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        int index = 0;
        foreach (MachineView view in machines.Values)
        {
            machineGrid.Controls.Add(view.Title, index % columns, index / columns);
            index++;
        }
        machineGrid.ResumeLayout();
    }

    private void UpdateStatus()
    {
        if (settings == null) { status.Text = "服务未启动"; return; }
        int online = 0;
        foreach (MachineView view in machines.Values)
        {
            if ((DateTime.Now - view.LastSeen).TotalSeconds < settings.IdleSeconds) online++;
            else view.Title.Text = view.MachineName + "  ·  离线";
        }
        status.Text = (server == null ? "● 已停止" : paused ? "● 暂停接收" : "● 正在监听")
            + "  " + settings.ListenHost + ":" + settings.ListenPort
            + "    设备 " + machines.Count + " / 在线 " + online
            + "    QMS " + (settings.QmsEnabled ? "已启用" : "未启用")
            + "    S1 MQTT " + (settings.S1Enabled ? "已启用" : "未启用");
    }

    private void Report(string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message;
        lock (logLock)
        {
            try
            {
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "monitor-" + DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"),
                    line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke((MethodInvoker)delegate
            {
                string[] lines = logBox.Lines;
                if (lines.Length > 1000)
                {
                    var recent = new List<string>(lines);
                    recent.RemoveRange(0, recent.Count - 800);
                    logBox.Lines = recent.ToArray();
                }
                logBox.AppendText(line + Environment.NewLine);
            });
        }
        catch (InvalidOperationException) { }
    }

    private static string StateText(string state)
    {
        if (state == "WORK") return "加工";
        if (state == "WAIT") return "等待";
        if (state == "STOP") return "停止";
        if (state == "ALAM") return "报警";
        return "空闲";
    }
    private static string FormatDuration(double seconds)
    {
        long total = (long)seconds;
        return (total / 3600).ToString("00") + ":" + ((total % 3600) / 60).ToString("00") + ":" + (total % 60).ToString("00");
    }

    private sealed class MachineView
    {
        internal readonly string MachineName;
        internal readonly GroupBox Title = new GroupBox();
        internal readonly TextBox Output = new TextBox();
        internal readonly List<string> Lines = new List<string>();
        internal DateTime LastSeen = DateTime.MinValue;
        internal string LastState = "OFFLINE";
        internal MachineView(string name)
        {
            MachineName = name;
            Title.Text = name;
            Title.Dock = DockStyle.Fill;
            Title.Padding = new Padding(5);
            Title.Margin = new Padding(4);
            Output.Dock = DockStyle.Fill;
            Output.Multiline = true;
            Output.ReadOnly = true;
            Output.ScrollBars = ScrollBars.Vertical;
            Output.Font = new Font("Consolas", 8.5F);
            Title.Controls.Add(Output);
        }
    }
}

internal sealed class BtfSettingsForm : Form
{
    private readonly string path;
    private readonly BtfSettings settings;
    private readonly TabControl tabs = new TabControl();
    private readonly Dictionary<string, TextBox> fields = new Dictionary<string, TextBox>();
    private readonly CheckBox qmsEnabled = new CheckBox();
    private readonly ComboBox qmsScheme = new ComboBox();
    private readonly CheckBox s1Enabled = new CheckBox();
    private readonly DataGridView devices = new DataGridView();

    internal BtfSettingsForm(string path, BtfSettings settings, int selectedTab)
    {
        this.path = path; this.settings = settings;
        Text = "标特福监听参数";
        ClientSize = new Size(940, 650);
        MinimumSize = new Size(780, 550);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        tabs.Dock = DockStyle.Fill;
        tabs.TabPages.Add(BuildSystem());
        tabs.TabPages.Add(BuildQms());
        tabs.TabPages.Add(BuildS1());
        tabs.SelectedIndex = selectedTab;
        Controls.Add(tabs);
        Panel buttons = new Panel(); buttons.Dock = DockStyle.Bottom; buttons.Height = 55;
        Button cancel = new Button(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(715, 10, 85, 32);
        Button save = new Button(); save.Text = "保存并应用"; save.SetBounds(810, 10, 100, 32);
        save.Click += delegate { Save(); };
        buttons.Controls.Add(cancel); buttons.Controls.Add(save);
        buttons.Resize += delegate { save.Left = buttons.ClientSize.Width - save.Width - 15; cancel.Left = save.Left - cancel.Width - 10; };
        Controls.Add(buttons);
        AcceptButton = save; CancelButton = cancel;
    }

    private TabPage BuildSystem()
    {
        TabPage page = new TabPage("系统参数");
        TableLayoutPanel table = NewTable();
        Add(table, "监听 IP", "host", settings.ListenHost);
        Add(table, "网络端口号", "port", settings.ListenPort.ToString(CultureInfo.InvariantCulture));
        Add(table, "显示列数", "columns", settings.Columns.ToString(CultureInfo.InvariantCulture));
        Add(table, "显示行数", "rows", settings.Rows.ToString(CultureInfo.InvariantCulture));
        Add(table, "记录保留天数", "retention", settings.RetentionDays.ToString(CultureInfo.InvariantCulture));
        Add(table, "空闲断线时间（秒）", "idle", settings.IdleSeconds.ToString(CultureInfo.InvariantCulture));
        page.Controls.Add(table); return page;
    }

    private TabPage BuildQms()
    {
        TabPage page = new TabPage("ERP / QMS 参数");
        TableLayoutPanel table = NewTable();
        qmsEnabled.Text = "启用 QMS 上报";
        qmsEnabled.Checked = settings.QmsEnabled;
        AddControl(table, "发送开关", qmsEnabled);
        Uri address;
        if (!Uri.TryCreate(settings.QmsUrl, UriKind.Absolute, out address)) address = new Uri("http://127.0.0.1:8080/hessian/monitor");
        qmsScheme.DropDownStyle = ComboBoxStyle.DropDownList;
        qmsScheme.Items.AddRange(new object[] { "http", "https" });
        qmsScheme.SelectedItem = address.Scheme;
        AddControl(table, "协议", qmsScheme);
        Add(table, "ERP 服务器 IP / 主机", "qmsHost", address.Host);
        Add(table, "ERP 网络端口号", "qmsPort", address.Port.ToString(CultureInfo.InvariantCulture));
        Add(table, "URL", "qmsPath", address.AbsolutePath);
        Add(table, "Method", "qmsMethod", settings.QmsMethod);
        Add(table, "上报间隔（秒）", "qmsInterval", settings.QmsReportSeconds.ToString(CultureInfo.InvariantCulture));
        Add(table, "失败重试间隔（秒）", "qmsRetry", settings.QmsRetrySeconds.ToString(CultureInfo.InvariantCulture));
        page.Controls.Add(table); return page;
    }

    private TabPage BuildS1()
    {
        TabPage page = new TabPage("S1 MQTT 参数");
        TableLayoutPanel table = NewTable();
        s1Enabled.Text = "启用 S1 MQTT 转发";
        s1Enabled.Checked = settings.S1Enabled;
        AddControl(table, "转发开关", s1Enabled);
        Add(table, "Broker 主机", "s1Host", settings.S1Host);
        Add(table, "Broker 端口", "s1Port", settings.S1Port.ToString(CultureInfo.InvariantCulture));
        Add(table, "默认产品 ID", "productId", settings.S1ProductId);
        Add(table, "默认用户 ID", "userId", settings.S1UserId);
        Add(table, "上报间隔（秒）", "s1Interval", settings.S1ReportSeconds.ToString(CultureInfo.InvariantCulture));
        Add(table, "重试间隔（秒）", "s1Retry", settings.S1RetrySeconds.ToString(CultureInfo.InvariantCulture));
        Add(table, "请求超时（秒）", "s1Timeout", settings.S1TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        Add(table, "保活时间（秒）", "s1KeepAlive", settings.S1KeepAliveSeconds.ToString(CultureInfo.InvariantCulture));
        devices.Dock = DockStyle.Fill;
        devices.AllowUserToAddRows = true;
        devices.AllowUserToDeleteRows = true;
        devices.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        devices.RowHeadersVisible = false;
        devices.Columns.Add("source", "现场设备名（#Username）");
        devices.Columns.Add("device", "S1 设备编号");
        devices.Columns.Add("product", "产品 ID");
        devices.Columns.Add("userId", "用户 ID");
        devices.Columns.Add("username", "MQTT 用户名");
        devices.Columns.Add("password", "MQTT 密码");
        devices.Columns[0].FillWeight = 24; devices.Columns[0].MinimumWidth = 210;
        devices.Columns[1].FillWeight = 16; devices.Columns[1].MinimumWidth = 115;
        devices.Columns[2].FillWeight = 11; devices.Columns[2].MinimumWidth = 85;
        devices.Columns[3].FillWeight = 11; devices.Columns[3].MinimumWidth = 85;
        devices.Columns[4].FillWeight = 19; devices.Columns[4].MinimumWidth = 130;
        devices.Columns[5].FillWeight = 19; devices.Columns[5].MinimumWidth = 130;
        foreach (BtfS1Device item in settings.S1Devices)
            devices.Rows.Add(item.SourceMachine, item.DeviceNumber, item.ProductId, item.UserId, item.Username, item.Password);
        Label hint = new Label(); hint.Text = "每台锣机对应一个 S1 设备；产品/用户 ID 留空时使用上方默认值。密码保存在本机配置文件中。";
        hint.Dock = DockStyle.Top; hint.Height = 32; hint.Padding = new Padding(12, 8, 0, 0);
        page.Controls.Add(devices); page.Controls.Add(hint); page.Controls.Add(table);
        return page;
    }

    private static TableLayoutPanel NewTable()
    {
        TableLayoutPanel table = new TableLayoutPanel();
        table.Dock = DockStyle.Top; table.Padding = new Padding(16);
        table.ColumnCount = 2;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }
    private void Add(TableLayoutPanel table, string label, string key, string value)
    {
        TextBox field = new TextBox(); field.Text = value; field.Dock = DockStyle.Fill;
        fields[key] = field; AddControl(table, label, field);
    }
    private static void AddControl(TableLayoutPanel table, string label, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Label title = new Label(); title.Text = label; title.Dock = DockStyle.Fill; title.TextAlign = ContentAlignment.MiddleLeft;
        control.Dock = DockStyle.Fill;
        CheckBox check = control as CheckBox;
        if (check != null) { check.AutoSize = false; check.TextAlign = ContentAlignment.MiddleLeft; }
        table.Controls.Add(title, 0, row); table.Controls.Add(control, 1, row);
        table.Height = table.Padding.Vertical + table.RowCount * 34;
    }
    private string Field(string key) { return fields[key].Text.Trim(); }
    private int Number(string key, string label)
    { int value; if (!int.TryParse(Field(key), out value)) throw new ArgumentException(label + "必须是整数。"); return value; }

    private void Save()
    {
        try
        {
            devices.EndEdit();
            settings.ListenHost = Field("host"); settings.ListenPort = Number("port", "监听端口");
            settings.Columns = Number("columns", "显示列数"); settings.Rows = Number("rows", "显示行数");
            settings.RetentionDays = Number("retention", "记录保留天数"); settings.IdleSeconds = Number("idle", "空闲断线时间");
            settings.QmsEnabled = qmsEnabled.Checked;
            string pathValue = Field("qmsPath");
            if (!pathValue.StartsWith("/")) pathValue = "/" + pathValue;
            string host = Field("qmsHost");
            settings.QmsUrl = new UriBuilder(Convert.ToString(qmsScheme.SelectedItem), host,
                Number("qmsPort", "ERP 网络端口"), pathValue).Uri.AbsoluteUri;
            settings.QmsMethod = Field("qmsMethod");
            settings.QmsReportSeconds = Number("qmsInterval", "QMS 上报间隔");
            settings.QmsRetrySeconds = Number("qmsRetry", "QMS 重试间隔");
            settings.S1Enabled = s1Enabled.Checked; settings.S1Host = Field("s1Host");
            settings.S1Port = Number("s1Port", "S1 端口"); settings.S1ProductId = Field("productId");
            settings.S1UserId = Field("userId"); settings.S1ReportSeconds = Number("s1Interval", "S1 上报间隔");
            settings.S1RetrySeconds = Number("s1Retry", "S1 重试间隔");
            settings.S1TimeoutSeconds = Number("s1Timeout", "S1 请求超时"); settings.S1KeepAliveSeconds = Number("s1KeepAlive", "S1 保活时间");
            settings.S1Devices.Clear();
            foreach (DataGridViewRow row in devices.Rows)
            {
                if (row.IsNewRow) continue;
                string source = Convert.ToString(row.Cells[0].Value) ?? "";
                if (string.IsNullOrWhiteSpace(source)) continue;
                settings.S1Devices.Add(new BtfS1Device {
                    SourceMachine = source.Trim(), DeviceNumber = (Convert.ToString(row.Cells[1].Value) ?? "").Trim(),
                    ProductId = (Convert.ToString(row.Cells[2].Value) ?? "").Trim(),
                    UserId = (Convert.ToString(row.Cells[3].Value) ?? "").Trim(),
                    Username = (Convert.ToString(row.Cells[4].Value) ?? "").Trim(), Password = Convert.ToString(row.Cells[5].Value) ?? ""
                });
            }
            settings.Save(path);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "配置无效", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
