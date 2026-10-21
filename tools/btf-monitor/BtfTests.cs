using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class BtfTests
{
    private static int passed;

    [STAThread]
    private static void Main()
    {
        Run("SplitAndCombinedFrames", SplitAndCombinedFrames);
        Run("UndelimitedCommandsAndRecord", UndelimitedCommandsAndRecord);
        Run("SnapshotFields", SnapshotFields);
        Run("QueueSurvivesRestart", QueueSurvivesRestart);
        Run("QueueRecoversPartialTail", QueueRecoversPartialTail);
        Run("QmsReportInterval", QmsReportInterval);
        Run("S1ReportIntervalThrottlesEachSource", S1ReportIntervalThrottlesEachSource);
        Run("S1ReportIntervalPersists", S1ReportIntervalPersists);
        Run("SettingsRowsStayAligned", SettingsRowsStayAligned);
        Run("SettingsS1IntervalCanBeSaved", SettingsS1IntervalCanBeSaved);
        Run("S1UsesMappedDevice", S1UsesMappedDevice);
        Run("S1UsesPerDeviceProduct", S1UsesPerDeviceProduct);
        Run("S1AcceptsCncNameVariants", S1AcceptsCncNameVariants);
        Run("S1RoutesMultiWindowNamesToOneDevice", S1RoutesMultiWindowNamesToOneDevice);
        Run("HessianCallMatchesJava", HessianCallMatchesJava);
        Run("TcpToQmsAndS1", TcpToQmsAndS1);
        Run("NoPubackKeepsS1Queue", NoPubackKeepsS1Queue);
        Run("S1HeartbeatsWhileMachineActiveThenGoesOffline", S1HeartbeatsWhileMachineActiveThenGoesOffline);
        Run("S1ThrottledSnapshotsKeepConnectionOnline", S1ThrottledSnapshotsKeepConnectionOnline);
        Run("S1ReconnectsAfterBrokerClosesSocket", S1ReconnectsAfterBrokerClosesSocket);
        Console.WriteLine("PASS " + passed);
    }

    private static void Run(string name, Action test)
    {
        try { test(); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + name + ": " + error.GetType().Name + " " + error.Message);
            Console.Error.WriteLine(error.StackTrace);
            throw;
        }
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Equal(object expected, object actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual);
    }

    private static string Record()
    {
        string text = "2026-10-19(1)10:11: 4,25309.2,11658.3,20382,100.0,0,86649.3";
        return text.PadRight(80, ' ');
    }

    private static void SplitAndCombinedFrames()
    {
        List<string> commands = new List<string>();
        List<string> records = new List<string>();
        BtfStreamParser parser = new BtfStreamParser(commands.Add, records.Add);
        byte[] input = Encoding.ASCII.GetBytes("#Username:CNC-14\n#Filename:ABC.rou\n" + Record() + Record());
        parser.Feed(input, 0, 17);
        parser.Feed(input, 17, input.Length - 17);
        Equal(2, commands.Count);
        Equal("#Username:CNC-14", commands[0]);
        Equal(2, records.Count);
        Equal(Record(), records[1]);
    }

    private static void UndelimitedCommandsAndRecord()
    {
        List<string> commands = new List<string>();
        List<string> records = new List<string>();
        BtfStreamParser parser = new BtfStreamParser(commands.Add, records.Add);
        byte[] input = Encoding.ASCII.GetBytes("#Username:CNC-14#Filename:ABC.rou" + Record());
        parser.Feed(input, 0, 26);
        parser.Feed(input, 26, input.Length - 26);
        Equal(2, commands.Count);
        Equal("#Username:CNC-14", commands[0]);
        Equal("#Filename:ABC.rou", commands[1]);
        Equal(1, records.Count);
        Equal(Record(), records[0]);
    }

    private static void SnapshotFields()
    {
        BtfMachineState state = new BtfMachineState();
        state.ApplyCommand("#Username:CNC-14");
        state.ApplyCommand("#Filename:ABC.rou");
        state.ApplyCommand("#Operator:Zhang");
        state.ApplyCommand("#Spindles:4,F");
        BtfSnapshot snapshot = state.ApplyRecord(Record());
        Equal("CNC-14", snapshot.MachineName);
        Equal("ABC.rou", snapshot.ProgramName);
        Equal("Zhang", snapshot.OperatorName);
        Equal("1111", snapshot.SpindleStatus);
        Equal(4, snapshot.WorkCount);
        Equal(20382, snapshot.FinishedCount);
        Equal("IDLE", snapshot.State);
        Equal("2026-10-19 10:11:00", snapshot.DeviceTime);
    }

    private static void QueueSurvivesRestart()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-queue-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "pending.jsonl");
            using (BtfQueue first = new BtfQueue(path))
            {
                first.Enqueue("one");
                first.Enqueue("two");
                Equal("one", first.Peek());
                first.Acknowledge();
            }
            using (BtfQueue second = new BtfQueue(path)) Equal("two", second.Peek());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void QueueRecoversPartialTail()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-tail-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (BtfQueue first = new BtfQueue(directory)) first.Enqueue("one");
            string segment = Path.Combine(directory, "0000000001.bin");
            using (FileStream stream = new FileStream(segment, FileMode.Append, FileAccess.Write))
                stream.Write(new byte[] { 0x20, 0x00 }, 0, 2);
            using (BtfQueue recovered = new BtfQueue(directory))
            {
                Equal("one", recovered.Peek());
                recovered.Acknowledge();
                Equal(null, recovered.Peek());
                recovered.Enqueue("two");
                Equal("two", recovered.Peek());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void QmsReportInterval()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-interval-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            BtfSettings settings = new BtfSettings();
            settings.BaseDirectory = directory; settings.QmsEnabled = true; settings.QmsReportSeconds = 5;
            BtfMachineState state = new BtfMachineState(); state.ApplyCommand("#Username:CNC-14");
            BtfSnapshot snapshot = state.ApplyRecord(Record());
            using (BtfDelivery delivery = new BtfDelivery(settings, delegate(string value) { }))
            {
                delivery.Enqueue(snapshot);
                delivery.Enqueue(snapshot);
                snapshot.State = "ALAM";
                delivery.Enqueue(snapshot);
            }
            using (BtfQueue queue = new BtfQueue(settings.QmsQueuePath))
            {
                Equal("IDLE", BtfSnapshot.Deserialize(queue.Peek()).State);
                queue.Acknowledge();
                Equal("ALAM", BtfSnapshot.Deserialize(queue.Peek()).State);
                queue.Acknowledge();
                Equal(null, queue.Peek());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void S1ReportIntervalThrottlesEachSource()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-s1-interval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "BtfMonitor.json");
            BtfSettings settings = new BtfSettings();
            settings.S1Enabled = true; settings.S1ProductId = "19"; settings.S1UserId = "1";
            settings.S1Devices.Add(new BtfS1Device {
                SourceMachine = "CNC#22", DeviceNumber = "CNC22", Username = "u", Password = "p"
            });
            settings.Save(path);
            var serializer = new JavaScriptSerializer();
            var values = serializer.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            values["s1_report_seconds"] = 3600;
            File.WriteAllText(path, serializer.Serialize(values));
            settings = BtfSettings.Load(path);
            BtfSnapshot first = SnapshotFor("CNC-22_1");
            BtfSnapshot second = SnapshotFor("CNC-22_2");
            using (BtfDelivery delivery = new BtfDelivery(settings, delegate(string value) { }))
            {
                delivery.Enqueue(first);
                delivery.Enqueue(first);
                delivery.Enqueue(second);
                first.State = "ALAM";
                delivery.Enqueue(first);
                delivery.Enqueue(first);
            }
            using (BtfQueue queue = new BtfQueue(Path.Combine(settings.S1QueuePath, "CNC22")))
            {
                string item = queue.Peek();
                if (item == null || !item.Contains("CNC-22_1") || !item.Contains("/19/CNC22/property/post"))
                    throw new Exception("First window was not reported");
                queue.Acknowledge();
                item = queue.Peek();
                if (item == null || !item.Contains("CNC-22_2")) throw new Exception("Second window was throttled by the first");
                queue.Acknowledge();
                item = queue.Peek();
                if (item == null || !item.Contains("CNC-22_1") || !item.Contains("ALAM"))
                    throw new Exception("State change was not reported immediately");
                queue.Acknowledge();
                Equal(null, queue.Peek());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void S1ReportIntervalPersists()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-s1-setting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "BtfMonitor.json");
            BtfSettings settings = new BtfSettings();
            settings.Save(path);
            var serializer = new JavaScriptSerializer();
            var values = serializer.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            values["s1_report_seconds"] = 7;
            File.WriteAllText(path, serializer.Serialize(values));
            BtfSettings.Load(path).Save(path);
            values = serializer.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            Equal(7, Convert.ToInt32(values["s1_report_seconds"]));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void SettingsRowsStayAligned()
    {
        using (BtfSettingsForm form = new BtfSettingsForm("unused.json", new BtfSettings(), 0))
        {
            form.Opacity = 0; form.ShowInTaskbar = false; form.Show();
            TabControl tabs = SettingsTabs(form);
            for (int tab = 0; tab < 3; tab++)
            {
                tabs.SelectedIndex = tab; Application.DoEvents();
                TableLayoutPanel table = SettingsTable(tabs, tab);
                foreach (Control control in table.Controls)
                {
                    Label label = control as Label;
                    if (label == null) continue;
                    Control field = table.GetControlFromPosition(1, table.GetRow(label));
                    int delta = Math.Abs(label.Top + label.Height / 2 - field.Top - field.Height / 2);
                    if (delta > 20) throw new Exception(label.Text + " is " + delta + " px away from its field");
                }
            }
            form.Close();
        }
    }

    private static void SettingsS1IntervalCanBeSaved()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-ui-setting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "BtfMonitor.json");
            BtfSettings settings = new BtfSettings(); settings.Save(path);
            using (BtfSettingsForm form = new BtfSettingsForm(path, settings, 2))
            {
                form.Opacity = 0; form.ShowInTaskbar = false; form.Show();
                TabControl tabs = SettingsTabs(form);
                TableLayoutPanel table = SettingsTable(tabs, 2);
                Label label = null;
                foreach (Control control in table.Controls)
                    if (control is Label && control.Text == "上报间隔（秒）") label = (Label)control;
                if (label == null) throw new Exception("S1 MQTT report interval field is missing");
                TextBox field = table.GetControlFromPosition(1, table.GetRow(label)) as TextBox;
                if (field == null) throw new Exception("S1 MQTT report interval editor is missing");
                field.Text = "7";
                ((Button)form.AcceptButton).PerformClick();
                Equal(DialogResult.OK, form.DialogResult);
            }
            var values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            Equal(7, Convert.ToInt32(values["s1_report_seconds"]));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static TabControl SettingsTabs(Form form)
    {
        foreach (Control control in form.Controls)
            if (control is TabControl) return (TabControl)control;
        throw new Exception("Settings tabs are missing");
    }

    private static TableLayoutPanel SettingsTable(TabControl tabs, int index)
    {
        tabs.SelectedIndex = index; Application.DoEvents();
        foreach (Control control in tabs.TabPages[index].Controls)
            if (control is TableLayoutPanel) return (TableLayoutPanel)control;
        throw new Exception("Settings table is missing");
    }

    private static void S1UsesMappedDevice()
    {
        BtfMachineState state = new BtfMachineState();
        state.ApplyCommand("#Username:CNC-14");
        BtfSnapshot snapshot = state.ApplyRecord(Record());
        BtfS1Device device = new BtfS1Device { SourceMachine = "CNC-14", DeviceNumber = "BTF014" };
        BtfMqttMessage message = BtfS1.BuildSnapshot(snapshot, device, "17");
        Equal("/17/BTF014/property/post", message.Topic);
        object[] body = new JavaScriptSerializer().DeserializeObject(message.Payload) as object[];
        if (body == null || body.Length < 5) throw new Exception("S1 properties missing");
        Dictionary<string, object> first = body[0] as Dictionary<string, object>;
        Equal("btf_status", first["id"]);
        Equal("IDLE", first["value"]);
    }

    private static void S1UsesPerDeviceProduct()
    {
        BtfSettings settings = new BtfSettings();
        settings.S1Enabled = true;
        settings.S1ProductId = "17";
        settings.S1UserId = "9";
        BtfS1Device first = new BtfS1Device {
            SourceMachine = "CNC#01", DeviceNumber = "CNC01", Username = "u1", Password = "p1"
        };
        BtfS1Device second = new BtfS1Device {
            SourceMachine = "CNC#02", DeviceNumber = "CNC02", ProductId = "18", UserId = "10",
            Username = "u2", Password = "p2"
        };
        settings.S1Devices.Add(first);
        settings.S1Devices.Add(second);
        settings.Validate();
        Equal("/17/CNC01/property/post", BtfS1.BuildSnapshot(SnapshotFor("CNC#01"), first, settings.ProductIdFor(first)).Topic);
        Equal("/18/CNC02/property/post", BtfS1.BuildSnapshot(SnapshotFor("CNC#02"), second, settings.ProductIdFor(second)).Topic);
        Equal("S&CNC01&17&9", BtfMqttClient.ClientId(settings, first));
        Equal("S&CNC02&18&10", BtfMqttClient.ClientId(settings, second));
    }

    private static BtfSnapshot SnapshotFor(string name)
    {
        BtfMachineState state = new BtfMachineState();
        state.ApplyCommand("#Username:" + name);
        return state.ApplyRecord(Record());
    }

    private static void S1AcceptsCncNameVariants()
    {
        BtfSettings settings = new BtfSettings();
        settings.S1Enabled = true;
        settings.S1ProductId = "19";
        settings.S1UserId = "1";
        BtfS1Device device = new BtfS1Device {
            SourceMachine = "CNC#14", DeviceNumber = "CNC14", Username = "u", Password = "p"
        };
        settings.S1Devices.Add(device);
        settings.Validate();
        Equal(device, settings.FindS1Device("CNC#14"));
        Equal(device, settings.FindS1Device("CNC-14"));
        Equal(device, settings.FindS1Device("CNC14"));
        settings.S1Devices.Add(new BtfS1Device {
            SourceMachine = "CNC-14", DeviceNumber = "CNC15", Username = "u", Password = "p"
        });
        try { settings.Validate(); throw new Exception("Ambiguous alias accepted"); }
        catch (ArgumentException) { }
    }

    private static void S1RoutesMultiWindowNamesToOneDevice()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-multi-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            BtfSettings settings = new BtfSettings();
            settings.BaseDirectory = directory;
            settings.S1Enabled = true;
            settings.S1ProductId = "19";
            settings.S1UserId = "1";
            settings.S1Devices.Add(new BtfS1Device {
                SourceMachine = "CNC#22", DeviceNumber = "CNC22", Username = "u", Password = "p"
            });
            settings.Validate();
            using (BtfDelivery delivery = new BtfDelivery(settings, delegate(string value) { }))
            {
                delivery.Enqueue(SnapshotFor("CNC-22_1"));
                delivery.Enqueue(SnapshotFor("CNC-22_2"));
            }
            using (BtfQueue mapped = new BtfQueue(Path.Combine(settings.S1QueuePath, "CNC22")))
            {
                string first = mapped.Peek();
                if (first == null || !first.Contains("/19/CNC22/property/post") || !first.Contains("CNC-22_1"))
                    throw new Exception("CNC-22_1 was not routed to CNC22");
                mapped.Acknowledge();
                string second = mapped.Peek();
                if (second == null || !second.Contains("/19/CNC22/property/post") || !second.Contains("CNC-22_2"))
                    throw new Exception("CNC-22_2 was not routed to CNC22");
                mapped.Acknowledge();
                Equal(null, mapped.Peek());
            }
            using (BtfQueue unmapped = new BtfQueue(Path.Combine(settings.S1QueuePath, "unmapped")))
                Equal(null, unmapped.Peek());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void HessianCallMatchesJava()
    {
        byte[] actual = BtfHessianClient.EncodeCall("Monitor", "ABC");
        byte[] expected = new byte[] { 0x63, 0x01, 0x00, 0x6D, 0x00, 0x07,
            0x4D, 0x6F, 0x6E, 0x69, 0x74, 0x6F, 0x72, 0x53, 0x00, 0x03,
            0x41, 0x42, 0x43, 0x7A };
        Equal(BitConverter.ToString(expected), BitConverter.ToString(actual));
    }

    private static void TcpToQmsAndS1()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TcpListener qms = new TcpListener(IPAddress.Loopback, 0);
        TcpListener broker = new TcpListener(IPAddress.Loopback, 0);
        qms.Start(); broker.Start();
        string qmsBody = null, mqttTopic = null, mqttPayload = null;
        Exception qmsError = null, mqttError = null;
        ManualResetEvent qmsDone = new ManualResetEvent(false), mqttDone = new ManualResetEvent(false);
        ManualResetEvent qmsAcknowledged = new ManualResetEvent(false), mqttAcknowledged = new ManualResetEvent(false);
        Thread qmsThread = new Thread(delegate()
        {
            try
            {
                using (TcpClient client = qms.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] request = ReadHttpRequest(stream);
                    int headerEnd = Find(request, Encoding.ASCII.GetBytes("\r\n\r\n")) + 4;
                    byte[] body = new byte[request.Length - headerEnd];
                    Buffer.BlockCopy(request, headerEnd, body, 0, body.Length);
                    if (body[0] != 'c' || body[1] != 1) throw new Exception("Not Hessian 1");
                    qmsBody = Encoding.UTF8.GetString(body);
                    byte[] reply = new byte[] { 0x72, 0x01, 0x00, 0x53, 0x00, 0x02, 0x4F, 0x4B, 0x7A };
                    byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/x-hessian\r\nContent-Length: 9\r\nConnection: close\r\n\r\n");
                    stream.Write(headers, 0, headers.Length); stream.Write(reply, 0, reply.Length);
                }
            }
            catch (Exception error) { qmsError = error; }
            finally { qmsDone.Set(); }
        });
        Thread mqttThread = new Thread(delegate()
        {
            try
            {
                using (TcpClient client = broker.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    byte type; ReadMqtt(stream, out type);
                    Equal((byte)0x10, type);
                    byte[] connected = new byte[] { 0x20, 0x02, 0x00, 0x00 };
                    stream.Write(connected, 0, connected.Length);
                    byte[] publish = ReadMqtt(stream, out type);
                    Equal((byte)0x30, type);
                    int topicLength = publish[0] * 256 + publish[1];
                    mqttTopic = Encoding.UTF8.GetString(publish, 2, topicLength);
                    int packet = topicLength + 2;
                    byte[] ack = new byte[] { 0x40, 0x02, publish[packet], publish[packet + 1] };
                    mqttPayload = Encoding.UTF8.GetString(publish, packet + 2, publish.Length - packet - 2);
                    stream.Write(ack, 0, ack.Length);
                }
            }
            catch (Exception error) { mqttError = error; }
            finally { mqttDone.Set(); }
        });
        qmsThread.IsBackground = true; mqttThread.IsBackground = true;
        qmsThread.Start(); mqttThread.Start();
        try
        {
            TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); int listenPort = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            BtfSettings settings = new BtfSettings();
            settings.BaseDirectory = directory;
            settings.ListenHost = "127.0.0.1"; settings.ListenPort = listenPort;
            settings.QmsEnabled = true; settings.QmsUrl = "http://127.0.0.1:" + ((IPEndPoint)qms.LocalEndpoint).Port + "/hessian/monitor";
            settings.S1Enabled = true; settings.S1Host = "127.0.0.1";
            settings.S1Port = ((IPEndPoint)broker.LocalEndpoint).Port; settings.S1ProductId = "17"; settings.S1UserId = "9";
            settings.S1TimeoutSeconds = 3;
            settings.S1Devices.Add(new BtfS1Device { SourceMachine = "CNC-14", DeviceNumber = "BTF014", Username = "user", Password = "password" });
            settings.Validate();
            using (BtfServer server = new BtfServer(settings, delegate(BtfSnapshot value) { }, delegate(string value)
            {
                if (value == "QMS 发送成功") qmsAcknowledged.Set();
                if (value == "S1 BTF014 发送成功") mqttAcknowledged.Set();
            }))
            {
                server.Start();
                using (TcpClient machine = new TcpClient("127.0.0.1", listenPort))
                {
                    byte[] bytes = Encoding.ASCII.GetBytes("#Username:CNC-14\n#Filename:ABC.rou\n" + Record());
                    machine.GetStream().Write(bytes, 0, bytes.Length);
                    if (!qmsDone.WaitOne(6000) || !mqttDone.WaitOne(6000)
                        || !qmsAcknowledged.WaitOne(6000) || !mqttAcknowledged.WaitOne(6000))
                        throw new Exception("forwarding timed out");
                }
            }
            if (qmsError != null) throw qmsError;
            if (mqttError != null) throw mqttError;
            if (qmsBody == null || !qmsBody.Contains("CNC-14")) throw new Exception("QMS payload missing machine");
            Equal("/17/BTF014/property/post", mqttTopic);
            if (mqttPayload == null || !mqttPayload.Contains("btf_finished_count")) throw new Exception("S1 payload missing count");
            using (BtfQueue savedQms = new BtfQueue(Path.Combine(directory, "data", "pending-qms"))) Equal(null, savedQms.Peek());
            using (BtfQueue savedS1 = new BtfQueue(Path.Combine(directory, "data", "pending-s1", "BTF014"))) Equal(null, savedS1.Peek());
        }
        finally
        {
            qms.Stop(); broker.Stop();
            Directory.Delete(directory, true);
        }
    }

    private static byte[] ReadHttpRequest(NetworkStream stream)
    {
        stream.ReadTimeout = 5000;
        MemoryStream body = new MemoryStream();
        byte[] buffer = new byte[4096];
        int expected = -1;
        while (true)
        {
            int count = stream.Read(buffer, 0, buffer.Length);
            if (count <= 0) throw new EndOfStreamException();
            body.Write(buffer, 0, count);
            byte[] bytes = body.ToArray();
            int headerEnd = Find(bytes, Encoding.ASCII.GetBytes("\r\n\r\n"));
            if (headerEnd < 0) continue;
            if (expected < 0)
            {
                string headers = Encoding.ASCII.GetString(bytes, 0, headerEnd);
                foreach (string line in headers.Split('\n'))
                    if (line.TrimStart().StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        expected = int.Parse(line.Split(':')[1].Trim());
            }
            if (expected >= 0 && bytes.Length >= headerEnd + 4 + expected) return bytes;
        }
    }

    private static int Find(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool same = true;
            for (int j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { same = false; break; }
            if (same) return i;
        }
        return -1;
    }

    private static byte[] ReadMqtt(Stream stream, out byte type)
    {
        int first = stream.ReadByte(); if (first < 0) throw new EndOfStreamException();
        type = (byte)(first & 0xF0);
        int length = 0, multiplier = 1, digit;
        do { digit = stream.ReadByte(); if (digit < 0) throw new EndOfStreamException(); length += (digit & 127) * multiplier; multiplier *= 128; }
        while ((digit & 128) != 0);
        byte[] body = new byte[length];
        int offset = 0;
        while (offset < body.Length) { int count = stream.Read(body, offset, body.Length - offset); if (count <= 0) throw new EndOfStreamException(); offset += count; }
        return body;
    }

    private static void NoPubackKeepsS1Queue()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-noack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TcpListener broker = new TcpListener(IPAddress.Loopback, 0);
        broker.Start();
        ManualResetEvent published = new ManualResetEvent(false), failed = new ManualResetEvent(false);
        Thread brokerThread = new Thread(delegate()
        {
            try
            {
                using (TcpClient client = broker.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    byte type; ReadMqtt(stream, out type);
                    byte[] connected = new byte[] { 0x20, 0x02, 0x00, 0x00 };
                    stream.Write(connected, 0, connected.Length);
                    ReadMqtt(stream, out type);
                    published.Set();
                }
            }
            catch { published.Set(); }
        });
        brokerThread.IsBackground = true; brokerThread.Start();
        try
        {
            BtfSettings settings = new BtfSettings();
            settings.BaseDirectory = directory; settings.S1Enabled = true;
            settings.S1Host = "127.0.0.1"; settings.S1Port = ((IPEndPoint)broker.LocalEndpoint).Port;
            settings.S1ProductId = "17"; settings.S1UserId = "9";
            settings.S1TimeoutSeconds = 1; settings.S1RetrySeconds = 1;
            settings.S1Devices.Add(new BtfS1Device { SourceMachine = "CNC-14", DeviceNumber = "BTF014", Username = "user", Password = "password" });
            BtfMachineState state = new BtfMachineState(); state.ApplyCommand("#Username:CNC-14");
            BtfSnapshot snapshot = state.ApplyRecord(Record());
            using (BtfDelivery delivery = new BtfDelivery(settings, delegate(string value)
            { if (value.Contains("待重试")) failed.Set(); }))
            {
                delivery.Start(); delivery.Enqueue(snapshot);
                if (!published.WaitOne(4000) || !failed.WaitOne(4000)) throw new Exception("missing PUBACK was not observed");
            }
            using (BtfQueue queue = new BtfQueue(Path.Combine(directory, "data", "pending-s1", "BTF014")))
                if (queue.Peek() == null) throw new Exception("S1 queue was removed without PUBACK");
        }
        finally { broker.Stop(); Directory.Delete(directory, true); }
    }

    private static void S1HeartbeatsWhileMachineActiveThenGoesOffline()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-heartbeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TcpListener broker = new TcpListener(IPAddress.Loopback, 0);
        broker.Start();
        ManualResetEvent done = new ManualResetEvent(false);
        Exception brokerError = null;
        Thread brokerThread = new Thread(delegate()
        {
            try
            {
                using (TcpClient peer = broker.AcceptTcpClient())
                using (NetworkStream stream = peer.GetStream())
                {
                    stream.ReadTimeout = 15000;
                    byte type;
                    ReadMqtt(stream, out type);
                    Equal((byte)0x10, type);
                    byte[] connack = { 0x20, 0x02, 0x00, 0x00 };
                    stream.Write(connack, 0, connack.Length);
                    byte[] publish = ReadMqtt(stream, out type);
                    Equal((byte)0x30, type);
                    int topicLength = publish[0] * 256 + publish[1];
                    int packet = topicLength + 2;
                    byte[] puback = { 0x40, 0x02, publish[packet], publish[packet + 1] };
                    stream.Write(puback, 0, puback.Length);
                    byte[] pingresp = { 0xD0, 0x00 };
                    bool pinged = false;
                    do
                    {
                        ReadMqtt(stream, out type);
                        if (type == 0xC0)
                        {
                            pinged = true;
                            stream.Write(pingresp, 0, pingresp.Length);
                        }
                    } while (type == 0xC0);
                    if (!pinged) throw new Exception("MQTT PINGREQ was not sent");
                    Equal((byte)0xE0, type);
                }
            }
            catch (Exception error) { brokerError = error; }
            finally { done.Set(); }
        });
        brokerThread.IsBackground = true;
        brokerThread.Start();
        try
        {
            BtfSettings settings = new BtfSettings();
            settings.BaseDirectory = directory; settings.S1Enabled = true;
            settings.S1Host = "127.0.0.1"; settings.S1Port = ((IPEndPoint)broker.LocalEndpoint).Port;
            settings.S1ProductId = "19"; settings.S1UserId = "1";
            settings.S1KeepAliveSeconds = 5; settings.S1TimeoutSeconds = 2; settings.IdleSeconds = 1;
            settings.S1Devices.Add(new BtfS1Device { SourceMachine = "CNC#01", DeviceNumber = "CNC01", Username = "u", Password = "p" });
            using (BtfDelivery delivery = new BtfDelivery(settings, delegate(string value) { }))
            {
                delivery.Start();
                delivery.Enqueue(SnapshotFor("CNC#01"));
                if (!done.WaitOne(14000)) throw new Exception("MQTT heartbeat or idle disconnect timed out");
            }
            if (brokerError != null) throw brokerError;
        }
        finally { broker.Stop(); Directory.Delete(directory, true); done.Dispose(); }
    }

    private static void S1ThrottledSnapshotsKeepConnectionOnline()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "outputs", "btf-active-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TcpListener broker = new TcpListener(IPAddress.Loopback, 0);
        broker.Start();
        ManualResetEvent published = new ManualResetEvent(false);
        ManualResetEvent mayClose = new ManualResetEvent(false);
        ManualResetEvent done = new ManualResetEvent(false);
        Exception brokerError = null;
        Thread brokerThread = new Thread(delegate()
        {
            try
            {
                using (TcpClient peer = broker.AcceptTcpClient())
                using (NetworkStream stream = peer.GetStream())
                {
                    stream.ReadTimeout = 18000;
                    byte type;
                    ReadMqtt(stream, out type);
                    Equal((byte)0x10, type);
                    byte[] connack = { 0x20, 0x02, 0x00, 0x00 };
                    stream.Write(connack, 0, connack.Length);
                    byte[] payload = ReadMqtt(stream, out type);
                    Equal((byte)0x30, type);
                    int packet = 2 + payload[0] * 256 + payload[1];
                    byte[] puback = { 0x40, 0x02, payload[packet], payload[packet + 1] };
                    stream.Write(puback, 0, puback.Length);
                    published.Set();
                    while (true)
                    {
                        ReadMqtt(stream, out type);
                        if (type == 0xE0)
                        {
                            if (!mayClose.WaitOne(0)) throw new Exception("active machine was disconnected");
                            break;
                        }
                        Equal((byte)0xC0, type);
                        byte[] pingresp = { 0xD0, 0x00 };
                        stream.Write(pingresp, 0, pingresp.Length);
                    }
                }
            }
            catch (Exception error) { brokerError = error; }
            finally { done.Set(); }
        });
        brokerThread.IsBackground = true; brokerThread.Start();
        try
        {
            BtfSettings settings = new BtfSettings();
            settings.BaseDirectory = directory; settings.S1Enabled = true;
            settings.S1Host = "127.0.0.1"; settings.S1Port = ((IPEndPoint)broker.LocalEndpoint).Port;
            settings.S1ProductId = "19"; settings.S1UserId = "1";
            settings.S1KeepAliveSeconds = 5; settings.S1TimeoutSeconds = 2;
            settings.IdleSeconds = 1; settings.S1ReportSeconds = 3600;
            settings.S1Devices.Add(new BtfS1Device { SourceMachine = "CNC#22", DeviceNumber = "CNC22", Username = "u", Password = "p" });
            using (BtfDelivery delivery = new BtfDelivery(settings, delegate(string value) { }))
            {
                delivery.Start();
                delivery.Enqueue(SnapshotFor("CNC-22_1"));
                if (!published.WaitOne(5000)) throw new Exception("first S1 report timed out");
                for (int i = 0; i < 4; i++)
                {
                    Thread.Sleep(3000);
                    delivery.Enqueue(SnapshotFor("CNC-22_1"));
                }
                mayClose.Set();
            }
            if (!done.WaitOne(5000)) throw new Exception("MQTT disconnect timed out");
            if (brokerError != null) throw brokerError;
        }
        finally
        {
            broker.Stop(); Directory.Delete(directory, true);
            published.Dispose(); mayClose.Dispose(); done.Dispose();
        }
    }

    private static void S1ReconnectsAfterBrokerClosesSocket()
    {
        TcpListener broker = new TcpListener(IPAddress.Loopback, 0);
        broker.Start();
        ManualResetEvent reconnected = new ManualResetEvent(false);
        ManualResetEvent done = new ManualResetEvent(false);
        Exception brokerError = null;
        Thread brokerThread = new Thread(delegate()
        {
            try
            {
                using (TcpClient first = broker.AcceptTcpClient())
                using (NetworkStream stream = first.GetStream())
                {
                    stream.ReadTimeout = 8000;
                    byte type;
                    ReadMqtt(stream, out type); Equal((byte)0x10, type);
                    byte[] connack = { 0x20, 0x02, 0x00, 0x00 };
                    stream.Write(connack, 0, connack.Length);
                    byte[] publish = ReadMqtt(stream, out type); Equal((byte)0x30, type);
                    int packet = 2 + publish[0] * 256 + publish[1];
                    byte[] puback = { 0x40, 0x02, publish[packet], publish[packet + 1] };
                    stream.Write(puback, 0, puback.Length);
                    Thread.Sleep(500);
                }
                using (TcpClient second = broker.AcceptTcpClient())
                using (NetworkStream stream = second.GetStream())
                {
                    stream.ReadTimeout = 8000;
                    byte type;
                    ReadMqtt(stream, out type); Equal((byte)0x10, type);
                    byte[] connack = { 0x20, 0x02, 0x00, 0x00 };
                    stream.Write(connack, 0, connack.Length);
                    reconnected.Set();
                    ReadMqtt(stream, out type); Equal((byte)0xE0, type);
                }
            }
            catch (Exception error) { brokerError = error; }
            finally { done.Set(); }
        });
        brokerThread.IsBackground = true; brokerThread.Start();
        try
        {
            BtfSettings settings = new BtfSettings();
            settings.S1Host = "127.0.0.1"; settings.S1Port = ((IPEndPoint)broker.LocalEndpoint).Port;
            settings.S1ProductId = "19"; settings.S1UserId = "1";
            settings.S1KeepAliveSeconds = 5; settings.S1TimeoutSeconds = 2; settings.IdleSeconds = 1;
            BtfS1Device device = new BtfS1Device { SourceMachine = "CNC#01", DeviceNumber = "CNC01", Username = "u", Password = "p" };
            using (BtfMqttClient client = new BtfMqttClient(settings, device))
            {
                client.TouchSource();
                client.Publish(new BtfMqttMessage("/19/CNC01/property/post", "[]"));
                for (int i = 0; i < 18 && !reconnected.WaitOne(500); i++) client.TouchSource();
                if (!reconnected.WaitOne(0)) throw new Exception("S1 MQTT did not reconnect");
            }
            if (!done.WaitOne(5000)) throw new Exception("reconnected MQTT session did not close");
            if (brokerError != null) throw brokerError;
        }
        finally { broker.Stop(); reconnected.Dispose(); done.Dispose(); }
    }
}
