using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class LayoutTests
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (MockForm form = new MockForm())
            {
                KstMesServer server = Field<KstMesServer>(form, "server");
                try
                {
                    TextBox ipBox = Field<TextBox>(form, "ipBox");
                    TextBox portBox = Field<TextBox>(form, "portBox");
                    TextBox urlBox = Field<TextBox>(form, "urlBox");
                    Button startButton = Field<Button>(form, "startButton");
                    Button stopButton = Field<Button>(form, "stopButton");
                    Label statusLabel = Field<Label>(form, "statusLabel");
                    ListBox methods = Field<ListBox>(form, "methodsList");

                    string localAddress = KstListenAddress.DefaultAddress();
                    Assert(ipBox.Text == localAddress, "default listen IP comes from this computer's active network adapters");
                    Assert(portBox.Text == "4524", "default listen port is 4524");
                    Assert(localAddress.Length == 0 || urlBox.Text.StartsWith("https://" + localAddress + ":4524/", StringComparison.Ordinal),
                        "service URL advertises the active local address over TLS");
                    Assert(ipBox.Enabled && portBox.Enabled, "endpoint controls remain editable before manual start");
                    Assert(startButton.Enabled && !stopButton.Enabled, "only start is available before manual start");
                    Assert(statusLabel.Text == "已停止", "service stays stopped when the window opens");
                    Assert(server != null && !server.IsRunning, "service does not bind until the user starts it");
                    bool logShown = false;
                    foreach (object method in methods.Items)
                        if (method.ToString().Contains("/Log")) logShown = true;
                    Assert(logShown, "proxy /Log appears in the response editor list");

                    VerifyServiceControlsFit(form, startButton, stopButton, statusLabel);
                }
                finally
                {
                    if (server != null) server.Stop();
                }
            }
            VerifyMqttDialogCompact();
            VerifyTelemetryIntervalSettings();
            VerifyQueuePathMigration();
            VerifyInitialWindowFitsWorkArea();
            VerifyResponseDiagnosticVisible();
            Console.WriteLine("KST manual startup defaults passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }

    private static void VerifyInitialWindowFitsWorkArea()
    {
        ConstructorInfo sizingConstructor = typeof(MockForm).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Size) }, null);
        Assert(sizingConstructor != null, "window size can be calculated for the active screen work area");
        foreach (Size workArea in new[] { new Size(1536, 824), new Size(1366, 728), new Size(900, 650) })
        {
            using (MockForm form = (MockForm)sizingConstructor.Invoke(new object[] { workArea }))
            {
                Assert(form.Width <= Math.Min(1400, workArea.Width) && form.Height <= Math.Min(820, workArea.Height),
                    "initial window fits work area " + workArea);
                Assert(form.MinimumSize.Width <= workArea.Width && form.MinimumSize.Height <= workArea.Height,
                    "minimum window size fits work area " + workArea);
                Assert(form.Width >= form.MinimumSize.Width && form.Height >= form.MinimumSize.Height,
                    "initial window respects minimum size for " + workArea);
            }
        }
    }

    private static void VerifyMqttDialogCompact()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_mqtt_config.json");
        KstMqttSettings settings = KstMqttSettings.Load(path);
        VerifyMqttDialogCompact(path, settings, "one device");
        settings.AdditionalDevices.Add(new KstMqttDevice { SourceDeviceCode = "KST-002", DeviceNumber = "KST002" });
        VerifyMqttDialogCompact(path, settings, "two devices");
    }

    private static void VerifyResponseDiagnosticVisible()
    {
        using (MockForm form = new MockForm())
        {
            DataGridView grid = Field<DataGridView>(form, "logGrid");
            TextBox details = Field<TextBox>(form, "responseDetails");
            KstExchange failed = new KstExchange();
            failed.Status = 500;
            failed.Method = "HeartBeat";
            failed.RequestPath = "/Heart";
            failed.ResponseBody = "{\"success\":false,\"message\":\"Server error\"}";
            failed.Diagnostic = "System.IO.IOException: MQTT queue file cannot be written";
            int row = grid.Rows.Add("09:21:19", "HeartBeat", "192.168.1.10", "/Heart");
            grid.Rows[row].Tag = failed;
            grid.Rows[row].Selected = true;
            MethodInfo select = typeof(MockForm).GetMethod("OnLogSelected", BindingFlags.Instance | BindingFlags.NonPublic);
            select.Invoke(form, new object[] { null, EventArgs.Empty });
            Assert(details.Text.Contains("MQTT queue file cannot be written"),
                "HTTP 500 response details show the concrete local diagnostic");
        }
    }

    private static void VerifyMqttDialogCompact(string path, KstMqttSettings settings, string caseName)
    {
        using (KstMqttSettingsForm dialog = new KstMqttSettingsForm(path, settings))
        {
            TextBox queue = Field<TextBox>(dialog, "queue");
            TextBox telemetryInterval = Field<TextBox>(dialog, "telemetryInterval");
            Button save = FindButton(dialog, "保存");
            dialog.Show();
            Application.DoEvents();
            Assert(telemetryInterval != null && telemetryInterval.Text == settings.TelemetryIntervalSeconds.ToString(),
                "MQTT dialog shows the configured OEE/heart reporting interval for " + caseName);
            Assert(CompletelyVisible(telemetryInterval, dialog), "MQTT reporting interval fits for " + caseName);
            int gap = save.PointToScreen(Point.Empty).Y - queue.PointToScreen(new Point(0, queue.Height)).Y;
            Assert(gap >= 0 && gap <= 64, "MQTT dialog has no large gap below its last input for " + caseName + " (actual " + gap + "px)");
            Assert(CompletelyVisible(queue, dialog) && CompletelyVisible(save, dialog), "MQTT dialog fields fit for " + caseName);
            dialog.Hide();
        }
    }

    private static void VerifyTelemetryIntervalSettings()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mqtt-legacy-test.json");
        const string legacy = "{\"enabled\":true,\"host\":\"broker.example\",\"port\":1883,\"product_id\":\"17\",\"device_number\":\"KST001\",\"source_device_code\":\"KST-001\",\"user_id\":\"1\",\"username\":\"test-user\",\"password\":\"test-password\",\"additional_devices\":[{\"source_device_code\":\"KST-002\",\"device_number\":\"KST002\",\"username\":\"test-user-2\",\"password\":\"test-password-2\"}]}";
        try
        {
            File.WriteAllText(path, legacy);
            KstMqttSettings settings = KstMqttSettings.Load(path);
            Assert(settings.TelemetryIntervalSeconds == 60, "legacy MQTT config defaults to 60-second telemetry reporting");
            settings.TelemetryIntervalSeconds = 90;
            settings.Save(path);
            KstMqttSettings restored = KstMqttSettings.Load(path);
            Assert(restored.TelemetryIntervalSeconds == 90, "MQTT reporting interval survives config reload");
            Assert(restored.Password == "test-password" && restored.Username == "test-user", "primary MQTT credentials survive config save");
            Assert(restored.AdditionalDevices.Count == 1 && restored.AdditionalDevices[0].DeviceNumber == "KST002" &&
                restored.AdditionalDevices[0].Password == "test-password-2", "second device mapping and credentials survive config save");
            using (KstMqttSettingsForm dialog = new KstMqttSettingsForm(path, restored))
            {
                TextBox interval = Field<TextBox>(dialog, "telemetryInterval");
                dialog.Show();
                interval.Text = "120";
                FindButton(dialog, "保存").PerformClick();
                Assert(dialog.DialogResult == DialogResult.OK, "MQTT dialog saves the reporting interval");
            }
            KstMqttSettings fromDialog = KstMqttSettings.Load(path);
            Assert(fromDialog.TelemetryIntervalSeconds == 120, "MQTT dialog persists the edited reporting interval");
            Assert(fromDialog.Password == "test-password" && fromDialog.AdditionalDevices.Count == 1 &&
                fromDialog.AdditionalDevices[0].Password == "test-password-2", "MQTT dialog preserves both devices' credentials");
            bool rejected = false;
            restored.TelemetryIntervalSeconds = 3601;
            try { restored.Validate(); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "MQTT reporting interval above one hour is rejected");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void VerifyQueuePathMigration()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        string config = Path.Combine(directory, "mqtt-path-migration-config.json");
        string source = Path.Combine(directory, "mqtt-path-migration-source.json");
        string destination = Path.Combine(directory, "mqtt-path-migration-destination.json");
        string occupied = Path.Combine(directory, "mqtt-path-migration-occupied.json");
        string occupiedState = Path.Combine(directory, "mqtt-path-migration-occupied-state.json");
        string invalidConfig = Path.Combine(directory, "mqtt-path-migration-invalid-config");
        string failedDestination = Path.Combine(directory, "mqtt-path-migration-failed.json");
        string queueContent = "[{\"method\":\"HeartBeat\",\"device_code\":\"KST-001\",\"received_at\":\"2026-10-20T10:00:00\",\"body\":{\"HeartBeat\":1}}]";
        string stateContent = "{\"KST001-status\":\"WORK\"}";
        try
        {
            KstMqttSettings settings = KstMqttSettings.Load(Path.Combine(directory, "kst_mqtt_config.json"));
            settings.QueueFile = source;
            settings.Save(config);
            File.WriteAllText(source, queueContent);
            File.WriteAllText(source + ".rate-state.json", stateContent);

            using (KstMqttSettingsForm dialog = new KstMqttSettingsForm(config, KstMqttSettings.Load(config)))
            {
                Field<TextBox>(dialog, "queue").Text = destination;
                InvokeSaveSettingsCore(dialog);
            }
            Assert(File.ReadAllText(destination) == queueContent, "new MQTT queue path receives all pending messages");
            Assert(File.ReadAllText(destination + ".rate-state.json") == stateContent, "new MQTT queue path receives rate state");
            Assert(new KstMqttQueue(destination).Count == 1, "migrated MQTT queue remains readable");
            Assert(File.ReadAllText(source) == queueContent && File.ReadAllText(source + ".rate-state.json") == stateContent,
                "original queue and rate state remain as backup");
            Assert(KstMqttSettings.Load(config).QueueFile == destination, "MQTT config switches to the migrated queue path");

            File.WriteAllText(occupied, "existing queue");
            using (KstMqttSettingsForm dialog = new KstMqttSettingsForm(config, KstMqttSettings.Load(config)))
            {
                Field<TextBox>(dialog, "queue").Text = occupied;
                AssertSaveRejected(dialog, "occupied MQTT queue path is rejected");
            }
            Assert(File.ReadAllText(occupied) == "existing queue" && KstMqttSettings.Load(config).QueueFile == destination,
                "existing queue and config are unchanged after rejected migration");

            File.WriteAllText(occupiedState + ".rate-state.json", "existing rate state");
            using (KstMqttSettingsForm dialog = new KstMqttSettingsForm(config, KstMqttSettings.Load(config)))
            {
                Field<TextBox>(dialog, "queue").Text = occupiedState;
                AssertSaveRejected(dialog, "occupied MQTT rate-state path is rejected");
            }
            Assert(!File.Exists(occupiedState) && File.ReadAllText(occupiedState + ".rate-state.json") == "existing rate state",
                "existing rate state is not overwritten or paired with a new queue");

            Directory.CreateDirectory(invalidConfig);
            using (KstMqttSettingsForm dialog = new KstMqttSettingsForm(invalidConfig, KstMqttSettings.Load(config)))
            {
                Field<TextBox>(dialog, "queue").Text = failedDestination;
                AssertSaveRejected(dialog, "new queue files are removed when config saving fails");
            }
            Assert(!File.Exists(failedDestination) && !File.Exists(failedDestination + ".rate-state.json"),
                "failed config save cleans up both newly copied files");
            Assert(File.ReadAllText(destination) == queueContent && File.ReadAllText(destination + ".rate-state.json") == stateContent,
                "failed config save leaves old queue and state intact");
        }
        finally
        {
            foreach (string file in new[] { config, config + ".tmp", source, source + ".rate-state.json", destination,
                destination + ".rate-state.json", occupied, occupiedState + ".rate-state.json", failedDestination,
                failedDestination + ".rate-state.json", invalidConfig + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(invalidConfig)) Directory.Delete(invalidConfig);
        }
    }

    private static void AssertSaveRejected(KstMqttSettingsForm dialog, string message)
    {
        bool rejected = false;
        try { InvokeSaveSettingsCore(dialog); }
        catch (IOException) { rejected = true; }
        Assert(rejected, message);
    }

    private static void InvokeSaveSettingsCore(KstMqttSettingsForm dialog)
    {
        MethodInfo method = typeof(KstMqttSettingsForm).GetMethod("SaveSettingsCore", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method != null, "MQTT settings expose a testable save and queue migration operation");
        try { method.Invoke(dialog, new object[0]); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }

    private static void VerifyServiceControlsFit(MockForm form, Button startButton, Button stopButton, Label statusLabel)
    {
        Label mqttStatusLabel = Field<Label>(form, "mqttStatusLabel");
        GroupBox serviceGroup = FindGroup(form, "服务配置");
        Button mqttButton = FindButton(serviceGroup, "S1 MQTT配置");

        form.Show();
        foreach (Size size in new[] { new Size(900, 650), new Size(1000, 700), new Size(1560, 940) })
        {
            form.Size = size;
            statusLabel.Text = "启动失败：在其上下文中，该请求的地址无效。请检查当前计算机的监听地址。";
            mqttStatusLabel.Text = "S1 MQTT 待重试：Broker 连接失败；保留 123 条请求";
            Application.DoEvents();

            Assert(CompletelyVisible(startButton, serviceGroup), "start button fits at " + size);
            Assert(CompletelyVisible(stopButton, serviceGroup), "stop button fits at " + size);
            Assert(CompletelyVisible(mqttButton, serviceGroup), "MQTT settings button fits at " + size);
            Assert(!HasHorizontalScrollbar(serviceGroup), "service settings need no horizontal scrollbar at " + size);
        }
        form.Hide();
    }

    private static GroupBox FindGroup(Control parent, string title)
    {
        foreach (Control child in parent.Controls)
        {
            GroupBox group = child as GroupBox;
            if (group != null && group.Text == title) return group;
            GroupBox nested = FindGroup(child, title);
            if (nested != null) return nested;
        }
        return null;
    }

    private static Button FindButton(Control parent, string title)
    {
        foreach (Control child in parent.Controls)
        {
            Button button = child as Button;
            if (button != null && button.Text == title) return button;
            Button nested = FindButton(child, title);
            if (nested != null) return nested;
        }
        return null;
    }

    private static bool CompletelyVisible(Control control, Control ancestor)
    {
        Rectangle bounds = ancestor.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        return ancestor.ClientRectangle.Contains(bounds);
    }

    private static bool HasHorizontalScrollbar(Control control)
    {
        ScrollableControl scrollable = control as ScrollableControl;
        if (scrollable != null && scrollable.HorizontalScroll.Visible) return true;
        foreach (Control child in control.Controls)
            if (HasHorizontalScrollbar(child)) return true;
        return false;
    }

    private static T Field<T>(Control form, string name) where T : class
    {
        FieldInfo field = form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field == null ? null : field.GetValue(form) as T;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Failed: " + message);
    }
}
