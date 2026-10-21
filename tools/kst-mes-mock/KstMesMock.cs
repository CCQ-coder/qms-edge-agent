using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

internal static class KstMesMock
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 0) return KstMesMockCli.Run(args);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.Run(new MockForm()); }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "MES Mock 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }
}

internal sealed class MockForm : Form
{
    private sealed class MethodItem
    {
        internal readonly string Key;
        private readonly string label;
        internal MethodItem(string key, string text) { Key = key; label = text; }
        public override string ToString() { return label; }
    }

    private static readonly MethodItem[] KnownMethods = {
        new MethodItem("Status", "设备状态 /Status"),
        new MethodItem("OEE", "设备效率 /OEE"),
        new MethodItem("Alarm", "报警信息 /Alarm"),
        new MethodItem("ExposureResult", "曝光结果 /ExposureResult"),
        new MethodItem("HeartBeat", "心跳信息 /Heart"),
        new MethodItem("Mode", "设备模式 /Mode"),
        new MethodItem("Log", "代理日志 /Log")
    };

    private readonly string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_server_config.json");
    private readonly string deviceConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_server_device_config.json");
    private readonly string defaultsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_server_defaults.json");
    private readonly string mqttConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_mqtt_config.json");
    private readonly string sourceRoutesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_source_routes.json");
    private readonly KstMesServer server = new KstMesServer();
    private X509Certificate2 tlsCertificate;
    private KstMqttSettings mqttSettings;
    private KstMqttForwarder mqttForwarder;
    private Dictionary<string, string> templates;
    private Dictionary<string, Dictionary<string, string>> deviceTemplates;
    private string selectedMethod;
    private bool editorDirty;
    private bool restoringSelection;
    private TextBox urlBox, ipBox, portBox, responseEditor, requestDetails, responseDetails;
    private Button startButton, stopButton;
    private Label statusLabel, mqttStatusLabel;
    private ListBox methodsList;
    private DataGridView logGrid;

    internal MockForm() : this(Screen.FromPoint(Cursor.Position).WorkingArea.Size) { }

    internal MockForm(Size workAreaSize)
    {
        templates = KstConfig.Load(configPath);
        if (!templates.ContainsKey("Log")) templates.Add("Log", KstMesServer.DefaultLogResponse);
        deviceTemplates = KstConfig.LoadDeviceResponses(deviceConfigPath);
        mqttSettings = KstMqttSettings.Load(mqttConfigPath);
        Text = "科视光学 MES Mock";
        Font = new Font("Microsoft YaHei UI", 9F);
        MinimumSize = new Size(Math.Min(1000, workAreaSize.Width), Math.Min(700, workAreaSize.Height));
        Size = new Size(
            Math.Max(MinimumSize.Width, Math.Min(1400, workAreaSize.Width - 64)),
            Math.Max(MinimumSize.Height, Math.Min(820, workAreaSize.Height - 64)));
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        server.ExchangeReceived += OnExchangeReceived;
        server.RequestAccepted += OnRequestAccepted;
        server.LogWriteFailed += OnLogWriteFailed;
        FormClosing += OnFormClosing;
        UpdateUrl();
        if (methodsList.Items.Count > 0) methodsList.SelectedIndex = 0;
    }

    private void BuildUi()
    {
        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(20, 16, 20, 16);
        layout.ColumnCount = 1;
        layout.RowCount = 3;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 175));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        Controls.Add(layout);
        layout.Controls.Add(BuildServiceGroup(), 0, 0);
        layout.Controls.Add(BuildResponseGroup(), 0, 1);
        layout.Controls.Add(BuildLogGroup(), 0, 2);
    }

    private GroupBox BuildServiceGroup()
    {
        GroupBox group = Group("服务配置");
        TableLayoutPanel table = new TableLayoutPanel();
        table.Dock = DockStyle.Fill;
        table.Padding = new Padding(12, 10, 12, 8);
        table.ColumnCount = 2;
        table.RowCount = 3;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        group.Controls.Add(table);
        table.Controls.Add(Label("服务URL:"), 0, 0);
        urlBox = new TextBox();
        urlBox.Dock = DockStyle.Fill;
        urlBox.ReadOnly = true;
        urlBox.Margin = new Padding(0, 2, 0, 4);
        table.Controls.Add(urlBox, 1, 0);

        FlowLayoutPanel controls = new FlowLayoutPanel();
        controls.Dock = DockStyle.Fill;
        controls.WrapContents = false;
        controls.Controls.Add(Label("监听IP:"));
        ipBox = new TextBox();
        ipBox.Text = KstListenAddress.DefaultAddress();
        ipBox.Width = 215;
        ipBox.TextChanged += delegate { UpdateUrl(); };
        controls.Controls.Add(ipBox);
        controls.Controls.Add(Label("端口:"));
        portBox = new TextBox();
        portBox.Text = "4524";
        portBox.Width = 105;
        portBox.TextChanged += delegate { UpdateUrl(); };
        controls.Controls.Add(portBox);
        startButton = Button("启动服务", 115);
        startButton.Click += delegate { StartService(true); };
        controls.Controls.Add(startButton);
        stopButton = Button("停止服务", 115);
        stopButton.Enabled = false;
        stopButton.Click += delegate { StopService(); };
        controls.Controls.Add(stopButton);
        table.Controls.Add(controls, 1, 1);

        TableLayoutPanel statusRow = new TableLayoutPanel();
        statusRow.Dock = DockStyle.Fill;
        statusRow.ColumnCount = 4;
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        Button mqttButton = Button("S1 MQTT配置", 125);
        mqttButton.Click += delegate { EditMqttSettings(); };
        statusRow.Controls.Add(mqttButton, 0, 0);
        statusRow.Controls.Add(Label("状态:"), 1, 0);
        statusLabel = Label("已停止");
        statusLabel.AutoSize = false;
        statusLabel.AutoEllipsis = true;
        statusLabel.Dock = DockStyle.Fill;
        statusRow.Controls.Add(statusLabel, 2, 0);
        mqttStatusLabel = Label(mqttSettings.Enabled ? "S1 MQTT 待启动" : "S1 MQTT 未启用");
        mqttStatusLabel.AutoSize = false;
        mqttStatusLabel.AutoEllipsis = true;
        mqttStatusLabel.Dock = DockStyle.Fill;
        statusRow.Controls.Add(mqttStatusLabel, 3, 0);
        table.Controls.Add(statusRow, 1, 2);
        return group;
    }

    private GroupBox BuildResponseGroup()
    {
        GroupBox group = Group("接口响应配置");
        TableLayoutPanel split = new TableLayoutPanel();
        split.Dock = DockStyle.Fill;
        split.Padding = new Padding(12, 10, 12, 10);
        split.ColumnCount = 2;
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.Controls.Add(split);

        TableLayoutPanel left = new TableLayoutPanel();
        left.Dock = DockStyle.Fill;
        left.RowCount = 2;
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(Label("接口路径列表:"), 0, 0);
        methodsList = new ListBox();
        methodsList.Dock = DockStyle.Fill;
        methodsList.IntegralHeight = false;
        methodsList.SelectedIndexChanged += OnMethodChanged;
        foreach (MethodItem item in KnownMethods)
            if (templates.ContainsKey(item.Key)) methodsList.Items.Add(item);
        foreach (string key in templates.Keys)
        {
            bool known = false;
            foreach (MethodItem item in KnownMethods) if (item.Key == key) { known = true; break; }
            if (!known) methodsList.Items.Add(new MethodItem(key, key));
        }
        left.Controls.Add(methodsList, 0, 1);
        split.Controls.Add(left, 0, 0);

        TableLayoutPanel right = new TableLayoutPanel();
        right.Dock = DockStyle.Fill;
        right.Margin = new Padding(28, 0, 0, 0);
        right.RowCount = 3;
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        right.Controls.Add(Label("响应JSON编辑器:"), 0, 0);
        responseEditor = DetailBox(false);
        responseEditor.TextChanged += delegate { editorDirty = true; };
        right.Controls.Add(responseEditor, 0, 1);
        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Fill;
        buttons.Padding = new Padding(0, 7, 0, 0);
        Button saveButton = Button("保存修改", 115);
        saveButton.Click += delegate { SaveSelectedResponse(); };
        buttons.Controls.Add(saveButton);
        Button resetButton = Button("恢复默认", 115);
        resetButton.Click += delegate { RestoreSelectedResponse(); };
        buttons.Controls.Add(resetButton);
        right.Controls.Add(buttons, 0, 2);
        split.Controls.Add(right, 1, 0);
        return group;
    }

    private GroupBox BuildLogGroup()
    {
        GroupBox group = Group("请求响应记录");
        TableLayoutPanel body = new TableLayoutPanel();
        body.Dock = DockStyle.Fill;
        body.Padding = new Padding(12, 10, 12, 10);
        body.RowCount = 2;
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        group.Controls.Add(body);
        TableLayoutPanel columns = new TableLayoutPanel();
        columns.Dock = DockStyle.Fill;
        columns.ColumnCount = 3;
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        body.Controls.Add(columns, 0, 0);

        logGrid = new DataGridView();
        logGrid.Dock = DockStyle.Fill;
        logGrid.ReadOnly = true;
        logGrid.AllowUserToAddRows = false;
        logGrid.AllowUserToDeleteRows = false;
        logGrid.RowHeadersVisible = false;
        logGrid.MultiSelect = false;
        logGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        logGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        logGrid.Columns.Add("time", "时间");
        logGrid.Columns.Add("method", "接口路径");
        logGrid.Columns.Add("ip", "客户端IP");
        logGrid.Columns.Add("path", "请求路径");
        logGrid.SelectionChanged += OnLogSelected;
        columns.Controls.Add(logGrid, 0, 0);
        requestDetails = AddDetails(columns, 1, "请求详情:");
        responseDetails = AddDetails(columns, 2, "响应详情:");

        FlowLayoutPanel footer = new FlowLayoutPanel();
        footer.Dock = DockStyle.Fill;
        footer.FlowDirection = FlowDirection.RightToLeft;
        Button clearButton = Button("清空日志", 155);
        clearButton.Click += delegate { logGrid.Rows.Clear(); requestDetails.Clear(); responseDetails.Clear(); };
        footer.Controls.Add(clearButton);
        body.Controls.Add(footer, 0, 1);
        return group;
    }

    private static TextBox AddDetails(TableLayoutPanel columns, int index, string title)
    {
        TableLayoutPanel section = new TableLayoutPanel();
        section.Dock = DockStyle.Fill;
        section.Margin = new Padding(12, 0, 0, 0);
        section.RowCount = 2;
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        section.Controls.Add(Label(title), 0, 0);
        TextBox details = DetailBox(true);
        section.Controls.Add(details, 0, 1);
        columns.Controls.Add(section, index, 0);
        return details;
    }

    private static GroupBox Group(string title)
    {
        GroupBox group = new GroupBox();
        group.Text = title;
        group.Dock = DockStyle.Fill;
        group.Margin = new Padding(0, 0, 0, 10);
        return group;
    }

    private static Label Label(string text)
    {
        Label label = new Label();
        label.Text = text;
        label.AutoSize = true;
        label.Margin = new Padding(0, 5, 16, 0);
        return label;
    }

    private static Button Button(string title, int width)
    {
        Button button = new Button();
        button.Text = title;
        button.Width = width;
        button.Height = 32;
        button.Margin = new Padding(12, 0, 10, 0);
        return button;
    }

    private static TextBox DetailBox(bool readOnly)
    {
        TextBox box = new TextBox();
        box.Dock = DockStyle.Fill;
        box.Multiline = true;
        box.ScrollBars = ScrollBars.Both;
        box.WordWrap = false;
        box.ReadOnly = readOnly;
        box.Font = new Font("Consolas", 9F);
        return box;
    }

    private void UpdateUrl()
    {
        string address = KstListenAddress.DisplayAddress(ipBox.Text.Trim());
        urlBox.Text = address.Length == 0
            ? "请填写本机可用的监听 IP"
            : "https://" + address + ":" + portBox.Text.Trim() + "/{Status|OEE|Alarm|ExposureResult|Heart|Mode|Log}";
    }

    private bool StartService(bool showError)
    {
        int port;
        if (!int.TryParse(portBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
        {
            if (showError) MessageBox.Show(this, "端口必须在 1 至 65535 之间。", "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        string addressError = KstListenAddress.ValidationError(ipBox.Text.Trim());
        if (addressError != null)
        {
            statusLabel.Text = "启动失败：" + addressError;
            if (showError) MessageBox.Show(this, addressError, "无法启动服务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try
        {
            mqttSettings = KstMqttSettings.Load(mqttConfigPath);
            KstSourceRoutes sourceRoutes = KstSourceRoutes.Load(sourceRoutesPath);
            sourceRoutes.ValidateDeviceMappings(mqttSettings);
            server.ConfigureSourceRoutes(sourceRoutes.TrustedProxyIp, sourceRoutes.HostToCode,
                sourceRoutes.AllowedStaleAssets);
            mqttForwarder = new KstMqttForwarder(mqttSettings, OnMqttStatus, sourceRoutes);
            mqttForwarder.Start();
            tlsCertificate = KstTlsCertificate.LoadDefault();
            server.Start(ipBox.Text.Trim(), port, templates, deviceTemplates, tlsCertificate);
            ipBox.Enabled = false;
            portBox.Enabled = false;
            startButton.Enabled = false;
            stopButton.Enabled = true;
            statusLabel.Text = "运行中（TLS 1.2）";
            mqttStatusLabel.Text = mqttSettings.Enabled ? "S1 MQTT 运行中" : "S1 MQTT 未启用";
            return true;
        }
        catch (Exception error)
        {
            if (tlsCertificate != null) { tlsCertificate.Dispose(); tlsCertificate = null; }
            if (mqttForwarder != null) { mqttForwarder.Stop(); mqttForwarder = null; }
            statusLabel.Text = "启动失败：" + error.Message;
            if (showError) MessageBox.Show(this, error.Message, "无法启动服务", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void StopService()
    {
        server.Stop();
        if (mqttForwarder != null) { mqttForwarder.Stop(); mqttForwarder = null; }
        if (tlsCertificate != null) { tlsCertificate.Dispose(); tlsCertificate = null; }
        ipBox.Enabled = true;
        portBox.Enabled = true;
        startButton.Enabled = true;
        stopButton.Enabled = false;
        statusLabel.Text = "已停止";
        mqttStatusLabel.Text = mqttSettings.Enabled ? "S1 MQTT 已停止" : "S1 MQTT 未启用";
    }

    private void EditMqttSettings()
    {
        if (server.IsRunning)
        {
            MessageBox.Show(this, "请先停止服务，再修改 S1 MQTT 配置。", "服务正在运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using (KstMqttSettingsForm form = new KstMqttSettingsForm(mqttConfigPath, mqttSettings))
        {
            if (form.ShowDialog(this) != DialogResult.OK) return;
        }
        mqttSettings = KstMqttSettings.Load(mqttConfigPath);
        mqttStatusLabel.Text = mqttSettings.Enabled ? "S1 MQTT 待启动" : "S1 MQTT 未启用";
    }

    private void OnRequestAccepted(KstExchange item)
    {
        KstMqttForwarder current = mqttForwarder;
        if (current != null) current.Enqueue(item);
    }

    private void OnMqttStatus(string text)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) mqttStatusLabel.Text = text; }); }
        catch (InvalidOperationException) { }
    }

    private void OnMethodChanged(object sender, EventArgs args)
    {
        if (restoringSelection) return;
        MethodItem item = methodsList.SelectedItem as MethodItem;
        if (item == null || item.Key == selectedMethod) return;
        if (!ConfirmEditorChanges())
        {
            restoringSelection = true;
            foreach (MethodItem previous in methodsList.Items)
                if (previous.Key == selectedMethod) { methodsList.SelectedItem = previous; break; }
            restoringSelection = false;
            return;
        }
        selectedMethod = item.Key;
        responseEditor.Text = KstConfig.Pretty(templates[selectedMethod]);
        editorDirty = false;
    }

    private bool ConfirmEditorChanges()
    {
        if (!editorDirty || selectedMethod == null) return true;
        DialogResult answer = MessageBox.Show(this, "当前响应尚未保存。是否保存修改？", "未保存的修改", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return false;
        return answer != DialogResult.Yes || SaveSelectedResponse();
    }

    private bool SaveSelectedResponse()
    {
        if (selectedMethod == null) return true;
        try
        {
            KstConfig.ValidateResponse(responseEditor.Text);
            Dictionary<string, string> updated = new Dictionary<string, string>(templates, StringComparer.Ordinal);
            updated[selectedMethod] = responseEditor.Text.Trim();
            KstConfig.Save(configPath, updated);
            server.UpdateResponses(updated);
            templates = updated;
            editorDirty = false;
            return true;
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void RestoreSelectedResponse()
    {
        if (selectedMethod == null) return;
        try
        {
            Dictionary<string, string> defaults = KstConfig.Load(defaultsPath);
            string response;
            if (!defaults.TryGetValue(selectedMethod, out response)) throw new ArgumentException("默认配置中没有此方法。");
            responseEditor.Text = KstConfig.Pretty(response);
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "恢复默认失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OnExchangeReceived(KstExchange item)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed) return;
                int pinnedIndex = -1;
                if (logGrid.SelectedRows.Count != 0 && logGrid.SelectedRows[0].Index < logGrid.Rows.Count - 1)
                    pinnedIndex = logGrid.SelectedRows[0].Index;
                if (logGrid.Rows.Count >= 1000)
                {
                    logGrid.Rows.RemoveAt(0);
                    pinnedIndex--;
                }
                bool tlsHandshakeFailed = item.Transport == "TLS" && item.DecodedReceivedBytes == 0;
                int index = logGrid.Rows.Add(
                    item.Time.ToString("HH:mm:ss"),
                    tlsHandshakeFailed ? "TLS 握手" : item.Method,
                    item.ClientIp,
                    tlsHandshakeFailed ? "未进入 HTTP" : item.RequestPath);
                logGrid.Rows[index].Tag = item;
                logGrid.ClearSelection();
                logGrid.Rows[pinnedIndex >= 0 ? pinnedIndex : index].Selected = true;
            });
        }
        catch (InvalidOperationException) { }
    }

    private void OnLogWriteFailed(Exception error)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke((MethodInvoker)delegate
            {
                if (!IsDisposed && server.IsRunning) statusLabel.Text = "运行中（TLS 1.2，日志写入失败）";
            });
        }
        catch (InvalidOperationException) { }
    }

    private void OnLogSelected(object sender, EventArgs args)
    {
        if (logGrid.SelectedRows.Count == 0) return;
        KstExchange item = logGrid.SelectedRows[0].Tag as KstExchange;
        if (item == null) return;
        requestDetails.Text = item.Status >= 400
            ? KstDiagnosticFormatter.RequestDetails(item)
            : DisplayJson(item.RequestBody);
        responseDetails.Text = item.Transport == "TLS" && item.DecodedReceivedBytes == 0
            ? "TLS 握手失败（未进入 HTTP）" + Environment.NewLine + Environment.NewLine + item.Diagnostic
            : "HTTP " + item.Status
                + (item.Status >= 400 && !string.IsNullOrWhiteSpace(item.Diagnostic)
                    ? Environment.NewLine + "诊断信息: " + item.Diagnostic : "")
                + Environment.NewLine + DisplayJson(item.ResponseBody);
    }

    private static string DisplayJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        try { KstConfig.ValidateResponse(text); return KstConfig.Pretty(text); }
        catch (Exception) { return text; }
    }

    private void OnFormClosing(object sender, FormClosingEventArgs args)
    {
        if (!ConfirmEditorChanges()) { args.Cancel = true; return; }
        server.Stop();
        if (mqttForwarder != null) { mqttForwarder.Stop(); mqttForwarder = null; }
        if (tlsCertificate != null) { tlsCertificate.Dispose(); tlsCertificate = null; }
    }
}
