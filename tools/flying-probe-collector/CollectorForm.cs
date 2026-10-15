using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class CollectorForm : Form
{
    private readonly string configPath;
    private SettingsDocument document;
    private CollectorRuntime runtime;
    private SyncHistoryView historyView;
    private GroupBox historyGroup;
    private readonly TextBox source = new TextBox { Name = "SourceRootBox" };
    private readonly TextBox url = new TextBox { Name = "QmsBaseUrlBox" };
    private readonly TextBox gateway = new TextBox { Name = "GatewayTokenBox", UseSystemPasswordChar = true };
    private readonly TextBox bearer = new TextBox { Name = "BearerTokenBox", UseSystemPasswordChar = true };
    private readonly TextBox interval = new TextBox { Name = "ScanIntervalBox" };
    private readonly Button save = new Button { Name = "SaveButton", Text = "保存配置", AutoSize = true };
    private readonly Button start = new Button { Name = "StartButton", Text = "启动采集", AutoSize = true };
    private readonly Button stop = new Button { Name = "StopButton", Text = "停止采集", AutoSize = true };
    private readonly Label status = new Label { Name = "StatusLabel", AutoSize = true, Text = "待配置" };
    private readonly Label pending = new Label { Text = "待发送: 0", AutoSize = true };
    private readonly Label lastSuccess = new Label { Text = "最近成功: 暂无", AutoSize = true };
    private readonly Label lastError = new Label { Text = "最近错误: 暂无", AutoSize = true };
    private bool closingReady;
    private bool stopping;

    public CollectorForm(string configPath)
    {
        this.configPath = Path.GetFullPath(configPath);
        Text = "飞针机数据采集与 QMS 同步";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1200, 780);
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildLayout();
        document = SettingsDocument.Load(this.configPath);
        source.Text = document.Get("SourceRoot", @"\\Scbgx\数字化\飞针机");
        url.Text = document.Get("QmsBaseUrl", "");
        gateway.Text = document.Get("GatewayToken", "");
        bearer.Text = document.Get("BearerToken", "");
        interval.Text = document.Get("ScanIntervalSeconds", "30");
        string queuePath;
        try
        {
            string configured = document.Get("QueueFile", @".\data\pending-events.json");
            queuePath = Path.GetFullPath(Path.IsPathRooted(configured) ? configured :
                Path.Combine(Path.GetDirectoryName(this.configPath), configured));
        }
        catch (Exception) { queuePath = Path.Combine(Path.GetDirectoryName(this.configPath), "data", "pending-events.json"); }
        historyView = new SyncHistoryView(new SyncHistory(Path.Combine(Path.GetDirectoryName(queuePath), "history")));
        historyGroup.Controls.Add(historyView);
        save.Click += delegate { SaveConfiguration(); };
        start.Click += delegate { StartCollector(); };
        stop.Click += delegate { StopCollector(); };
        stop.Enabled = false;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try { document.Validate(); StartCollector(); }
        catch (Exception error) { status.Text = "待配置: " + error.Message; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closingReady && runtime != null && runtime.IsRunning)
        {
            e.Cancel = true;
            if (!stopping)
            {
                stopping = true;
                status.Text = "正在停止，等待当前请求完成…";
                runtime.StopAsync().ContinueWith(delegate(Task task)
                {
                    Post(delegate { closingReady = true; Close(); });
                });
            }
        }
        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 264));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        GroupBox config = new GroupBox { Text = "采集配置", Dock = DockStyle.Fill, Padding = new Padding(10) };
        TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddSetting(grid, 0, "采集目录", source);
        AddSetting(grid, 1, "QMS 地址", url);
        AddSetting(grid, 2, "网关令牌", gateway);
        AddSetting(grid, 3, "Bearer Token", bearer);
        AddSetting(grid, 4, "扫描间隔(秒)", interval);
        FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        buttons.Controls.Add(save); buttons.Controls.Add(start); buttons.Controls.Add(stop);
        grid.Controls.Add(buttons, 1, 5);
        config.Controls.Add(grid);
        layout.Controls.Add(config, 0, 0);

        GroupBox summary = new GroupBox { Text = "同步概况", Dock = DockStyle.Fill, Padding = new Padding(10) };
        FlowLayoutPanel summaryFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        foreach (Label label in new Label[] { status, pending, lastSuccess, lastError })
        { label.Margin = new Padding(12, 8, 28, 8); summaryFlow.Controls.Add(label); }
        summary.Controls.Add(summaryFlow);
        layout.Controls.Add(summary, 0, 1);

        historyGroup = new GroupBox { Text = "同步记录", Dock = DockStyle.Fill, Padding = new Padding(10) };
        layout.Controls.Add(historyGroup, 0, 2);
    }

    private static void AddSetting(TableLayoutPanel grid, int row, string caption, TextBox box)
    {
        grid.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        box.Dock = DockStyle.Fill;
        grid.Controls.Add(box, 1, row);
    }

    private void SaveConfiguration()
    {
        try
        {
            document.Set("SourceRoot", source.Text.Trim());
            document.Set("QmsBaseUrl", url.Text.Trim());
            document.Set("GatewayToken", gateway.Text.Trim());
            document.Set("BearerToken", bearer.Text.Trim());
            document.Set("ScanIntervalSeconds", interval.Text.Trim());
            document.Save();
            status.Text = "配置已保存，可启动采集";
            lastError.Text = "最近错误: 暂无";
        }
        catch (Exception error) { status.Text = "待配置: " + error.Message; }
    }

    private void StartCollector()
    {
        if (runtime != null && runtime.IsRunning) return;
        try
        {
            Settings settings = document.Validate();
            if (settings.QmsBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            runtime = new CollectorRuntime(settings, delegate(string message) { WriteLog(settings, message); },
                delegate(SyncRecord record)
                {
                    Post(delegate
                    {
                        historyView.AddLive(record);
                        if (record.Stage == "success") lastSuccess.Text = "最近成功: " + record.Time.ToString("yyyy-MM-dd HH:mm:ss");
                        if (record.Stage == "failed") lastError.Text = "最近错误: " + record.Time.ToString("HH:mm:ss") + " HTTP " + record.HttpStatus;
                    });
                }, delegate(int count) { Post(delegate { pending.Text = "待发送: " + count.ToString(CultureInfo.InvariantCulture); }); });
            runtime.Start();
            ToggleInputs(false);
            status.Text = "运行中";
        }
        catch (Exception error) { status.Text = "待配置: " + error.Message; lastError.Text = "最近错误: " + error.GetType().Name; }
    }

    private void StopCollector()
    {
        if (runtime == null || !runtime.IsRunning || stopping) return;
        stopping = true;
        status.Text = "正在停止，等待当前请求完成…";
        runtime.StopAsync().ContinueWith(delegate(Task task)
        {
            Post(delegate
            {
                stopping = false;
                ToggleInputs(true);
                status.Text = task.IsFaulted ? "停止失败: " + task.Exception.GetBaseException().Message : "已停止";
            });
        });
    }

    private void ToggleInputs(bool enabled)
    {
        foreach (TextBox box in new TextBox[] { source, url, gateway, bearer, interval }) box.Enabled = enabled;
        save.Enabled = enabled; start.Enabled = enabled; stop.Enabled = !enabled;
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private static void WriteLog(Settings settings, string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message;
        Directory.CreateDirectory(Path.GetDirectoryName(settings.LogFile));
        File.AppendAllText(settings.LogFile, line + Environment.NewLine, Encoding.UTF8);
    }
}
