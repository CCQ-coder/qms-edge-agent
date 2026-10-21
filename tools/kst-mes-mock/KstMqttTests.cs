using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class KstMqttTests
{
    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kst-mqtt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            FakeBroker broker = new FakeBroker(listener);
            Thread brokerThread = new Thread(new ThreadStart(broker.Run));
            brokerThread.IsBackground = true;
            brokerThread.Start();

            KstMqttSettings settings = new KstMqttSettings();
            settings.Enabled = true;
            settings.Host = "127.0.0.1";
            settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            settings.ProductId = "88";
            settings.DeviceNumber = "KST001";
            settings.UserId = "9";
            settings.Username = "device-user";
            settings.Password = "device-password";
            settings.KeepAliveSeconds = 30;
            settings.RequestTimeoutSeconds = 3;
            settings.RetryIntervalSeconds = 1;
            settings.QueueFile = Path.Combine(directory, "pending.json");
            SetTelemetryInterval(settings, 1);
            settings.Validate();
            TestOeePublishPrecision(settings);

            settings.DeviceNumber = "KST-001";
            bool invalidDeviceNumberRejected = false;
            try { settings.Validate(); }
            catch (ArgumentException) { invalidDeviceNumberRejected = true; }
            Assert(invalidDeviceNumberRejected, "S1 device number must reject a hyphen");
            settings.DeviceNumber = "KST001";

            KstExchange exchange = new KstExchange();
            exchange.Time = new DateTime(2026, 10, 17, 9, 41, 39);
            exchange.Method = "OEE";
            exchange.DeviceCode = "KST-001";
            exchange.RequestBody = "{\"function\":\"duty\",\"assets\":\"KST-001\",\"OEE\":0.0715467513303224,\"StartTime\":\"2026-10-17 08:00:00\",\"EndTime\":\"2026-10-17 20:00:00\",\"MachineTime\":\"2026-10-17 09:41:39\",\"UserName\":\"\",\"LotNO\":\"\",\"CBARCODE\":\"unknown\"}";

            KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
            forwarder.Start();
            forwarder.Enqueue(exchange);
            Assert(broker.Received.WaitOne(4000), "MQTT publish received");
            forwarder.Stop();
            brokerThread.Join(3000);
            if (broker.Error != null) throw broker.Error;
            Assert(broker.ClientId == "S&KST001&88&9", "client id");
            Assert(broker.Topic == "/88/KST001/property/post", "property topic");
            Assert(broker.Payload.Contains("\"id\":\"kst_oee\""), "OEE identifier");
            Assert(broker.Payload.Contains("\"value\":\"0.07\""), "OEE publish value has two decimal places");
            IList publishedItems = new JavaScriptSerializer().DeserializeObject(broker.Payload) as IList;
            Dictionary<string, object> publishedItem = publishedItems[0] as Dictionary<string, object>;
            Assert(Convert.ToString(publishedItem["remark"]).Contains("\"assets\":\"KST-001\""), "raw KST asset remains in remark");
            Assert(File.ReadAllText(settings.QueueFile, Encoding.UTF8) == "[]", "queue removed after PUBACK");

            KstExchange alarm = new KstExchange();
            alarm.Time = new DateTime(2026, 10, 17, 9, 39, 28);
            alarm.Method = "Alarm";
            alarm.DeviceCode = "KST-001";
            alarm.RequestBody = "{\"function\":\"alarm\",\"assets\":\"KST-001\",\"EventMessage\":\"正在中止线程。\",\"Level\":\"1\",\"ID\":\"202610170939285864\",\"MachineTime\":\"2026-10-17 09:39:28\"}";
            KstMqttMessage alarmMessage = KstMqttTemplate.Build(KstMqttQueueItem.FromExchange(alarm), settings);
            Assert(alarmMessage.Topic == "/88/KST001/event/post", "event topic");
            Assert(alarmMessage.Payload.Contains("正在中止线程。"), "UTF-8 alarm text");

            TestMultipleDevices(settings, directory);
            TestTrustedProxyRoutePersistsSecondDevice(directory);
            TestTrustedRouteQueueStartupGuard(directory);
            TestRetryAfterMissingPuback(settings, directory);
            TestFailedDeviceDoesNotBlockOtherDevice(settings, directory);
            TestQueueWriteFailureDoesNotAcknowledge(directory);
            TestHeartbeatFromBothDevicesCanBeQueued(directory);

            TestTelemetryRateBehavior(directory);

            Console.WriteLine("KST S1 MQTT forwarding passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception("Failed: " + label);
    }

    private static void TestOeePublishPrecision(KstMqttSettings settings)
    {
        KstExchange exchange = new KstExchange();
        exchange.Time = new DateTime(2026, 10, 20, 15, 26, 52);
        exchange.Method = "OEE";
        exchange.DeviceCode = "KST-002";
        exchange.RequestBody = "{\"assets\":\"KST-002\",\"OEE\":45.441587085902029,\"MachineTime\":\"2026-10-20 15:26:52\"}";

        KstMqttMessage message = KstMqttTemplate.Build(KstMqttQueueItem.FromExchange(exchange), settings, "KST002");
        IList items = new JavaScriptSerializer().DeserializeObject(message.Payload) as IList;
        Dictionary<string, object> model = items[0] as Dictionary<string, object>;
        string roundedValue = Convert.ToString(model["value"], CultureInfo.InvariantCulture);

        exchange.RequestBody = "{\"assets\":\"KST-002\",\"OEE\":45.4}";
        message = KstMqttTemplate.Build(KstMqttQueueItem.FromExchange(exchange), settings, "KST002");
        items = new JavaScriptSerializer().DeserializeObject(message.Payload) as IList;
        model = items[0] as Dictionary<string, object>;
        Assert(Convert.ToString(model["value"], CultureInfo.InvariantCulture) == "45.40", "OEE publish value retains two decimal places");
        Assert(roundedValue == "45.44", "OEE source precision rounds to two places for S1");
    }

    private static void TestTelemetryRateBehavior(string directory)
    {
        List<string> failures = new List<string>();
        CheckRateBehavior("default 60 seconds", delegate { TestDefaultTelemetryInterval(directory); }, failures);
        CheckRateBehavior("heartbeats coalesce per device", delegate { TestHeartbeatCoalescing(directory); }, failures);
        CheckRateBehavior("OEE coalesces per shift", delegate { TestOeeShiftCoalescing(directory); }, failures);
        CheckRateBehavior("unchanged status and mode", delegate { TestUnchangedStateDeduplication(directory); }, failures);
        CheckRateBehavior("alarm and exposure remain lossless", delegate { TestEventsRemainLossless(directory); }, failures);
        CheckRateBehavior("legacy queue migration and backup", delegate { TestLegacyQueueCompaction(directory); }, failures);
        CheckRateBehavior("new heartbeat survives in-flight PUBACK", delegate { TestHeartbeatArrivingDuringPuback(directory); }, failures);
        CheckRateBehavior("restart retains telemetry due time", delegate { TestRestartRetainsTelemetryDueTime(directory); }, failures);
        CheckRateBehavior("published state dedup survives restart", delegate { TestPublishedStateDeduplication(directory); }, failures);
        CheckRateBehavior("state return survives ambiguous PUBACK", delegate { TestStateReturnAfterMissingPuback(directory); }, failures);
        CheckRateBehavior("reconnect after broker idle close", delegate { TestReconnectAfterIdleBrokerClose(directory); }, failures);
        CheckRateBehavior("idle connection stays online with MQTT keepalive", delegate { TestIdleConnectionKeepAlive(directory); }, failures);
        CheckRateBehavior("failed idle ping reconnects without new data", delegate { TestFailedIdlePingReconnects(directory); }, failures);
        CheckRateBehavior("rejected CONNACK retries the same source", delegate { TestRejectedConnackRetries(directory); }, failures);
        CheckRateBehavior("successful publish resumes ping after maintenance failure", delegate { TestPublishResumesPingAfterMaintenanceFailure(directory); }, failures);
        CheckRateBehavior("first buffered heartbeat opens and maintains only its source connection", delegate { TestBufferedHeartbeatMaintainsSourceConnection(directory); }, failures);
        CheckRateBehavior("healthy idle connection is reused for a late publish", delegate { TestHealthyIdlePublishReusesConnection(directory); }, failures);
        CheckRateBehavior("active connection is pinged during another device backlog", delegate { TestPingWhileOtherDeviceBacklogDrains(directory); }, failures);
        CheckRateBehavior("active device pings while another broker handshake stalls", delegate { TestPingWhileOtherDeviceConnackStalls(directory); }, failures);
        CheckRateBehavior("one forwarder owns a durable queue", delegate { TestSingleForwarderQueueOwner(directory); }, failures);
        CheckRateBehavior("failed device does not delay another due device", delegate { TestFailedDeviceDoesNotDelayOtherDueDevice(directory); }, failures);
        CheckRateBehavior("stop waits for in-flight publisher before restart", delegate { TestStopWaitsForInFlightPublisher(directory); }, failures);
        if (failures.Count > 0) throw new Exception("Telemetry rate behavior failed: " + string.Join("; ", failures.ToArray()));
    }

    private static void CheckRateBehavior(string name, Action test, List<string> failures)
    {
        try { test(); Console.WriteLine("PASS: " + name); }
        catch (Exception error)
        {
            failures.Add(name + " (" + error.Message + ")");
            Console.Error.WriteLine("FAIL: " + name + " - " + error.Message);
        }
    }

    private static KstMqttSettings RateSettings(string directory, string fileName, int intervalSeconds)
    {
        KstMqttSettings settings = new KstMqttSettings();
        settings.Enabled = true;
        settings.Host = "127.0.0.1";
        settings.Port = 1883;
        settings.ProductId = "17";
        settings.DeviceNumber = "KST001";
        settings.SourceDeviceCode = "KST-001";
        settings.UserId = "1";
        settings.Username = "test-user";
        settings.Password = "test-password";
        settings.KeepAliveSeconds = 30;
        settings.RequestTimeoutSeconds = 3;
        settings.RetryIntervalSeconds = 1;
        settings.QueueFile = Path.Combine(directory, fileName);
        settings.AdditionalDevices.Add(new KstMqttDevice { SourceDeviceCode = "KST-002", DeviceNumber = "KST002" });
        SetTelemetryInterval(settings, intervalSeconds);
        settings.Validate();
        return settings;
    }

    private static void SetTelemetryInterval(KstMqttSettings settings, int seconds)
    {
        FieldInfo interval = typeof(KstMqttSettings).GetField("TelemetryIntervalSeconds", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (interval != null) interval.SetValue(settings, seconds);
    }

    private static string QueueDueTime(string path)
    {
        IList saved = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IList;
        Assert(saved != null && saved.Count == 1, "one saved telemetry item");
        Dictionary<string, object> item = saved[0] as Dictionary<string, object>;
        object due = null;
        Assert(item != null && item.TryGetValue("not_before_utc", out due), "telemetry due time is durable");
        DateTime parsed;
        Assert(DateTime.TryParse(Convert.ToString(due), out parsed), "saved telemetry due time is a valid timestamp");
        return Convert.ToString(due);
    }

    private static KstExchange RateExchange(string method, string deviceCode, string valueField, object value, string id)
    {
        Dictionary<string, object> body = new Dictionary<string, object>();
        body["ID"] = id;
        body["MachineNO"] = deviceCode;
        body["assets"] = deviceCode;
        body["MachineTime"] = "2026-10-20 10:00:00";
        body[valueField] = value;
        KstExchange exchange = new KstExchange();
        exchange.Time = new DateTime(2026, 10, 20, 10, 0, 0);
        exchange.Method = method;
        exchange.DeviceCode = deviceCode;
        exchange.RequestBody = new JavaScriptSerializer().Serialize(body);
        return exchange;
    }

    private static KstExchange RateOee(string deviceCode, double value, string start, string end, string id)
    {
        KstExchange exchange = RateExchange("OEE", deviceCode, "OEE", value, id);
        Dictionary<string, object> body = new JavaScriptSerializer().DeserializeObject(exchange.RequestBody) as Dictionary<string, object>;
        body["StartTime"] = start;
        body["EndTime"] = end;
        exchange.RequestBody = new JavaScriptSerializer().Serialize(body);
        return exchange;
    }

    private static KstMqttQueueItem FindQueued(KstMqttQueue queue, string method, string targetDevice, string shiftStart)
    {
        for (int index = 0; index < queue.Count; index++)
        {
            KstMqttQueueItem item = queue.Peek(index);
            if (item.Method != method || item.S1DeviceNumber != targetDevice) continue;
            object start;
            if (shiftStart != null && (!item.Body.TryGetValue("StartTime", out start) || Convert.ToString(start) != shiftStart)) continue;
            return item;
        }
        throw new Exception("Missing queued " + method + " for " + targetDevice + (shiftStart == null ? "" : " shift " + shiftStart));
    }

    private static void TestDefaultTelemetryInterval(string directory)
    {
        string path = Path.Combine(directory, "old-config-without-telemetry-interval.json");
        File.WriteAllText(path, "{\"enabled\":false,\"queue_file\":\"old-pending.json\"}", Encoding.UTF8);
        KstMqttSettings loaded = KstMqttSettings.Load(path);
        FieldInfo interval = typeof(KstMqttSettings).GetField("TelemetryIntervalSeconds", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(interval != null, "telemetry interval setting exists");
        Assert(Convert.ToInt32(interval.GetValue(loaded)) == 60, "old config defaults to 60 seconds");
        string savedPath = Path.Combine(directory, "saved-config-with-telemetry-interval.json");
        loaded.Save(savedPath);
        Dictionary<string, object> saved = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(savedPath, Encoding.UTF8)) as Dictionary<string, object>;
        Assert(saved.ContainsKey("telemetry_interval_seconds") && Convert.ToInt32(saved["telemetry_interval_seconds"]) == 60,
            "saved config contains the 60-second telemetry interval");
        SetTelemetryInterval(loaded, 120);
        loaded.Save(savedPath);
        Assert(Convert.ToInt32(interval.GetValue(KstMqttSettings.Load(savedPath))) == 120,
            "custom telemetry interval survives save and load");
        SetTelemetryInterval(loaded, 0);
        bool zeroRejected = false;
        try { loaded.Validate(); }
        catch (ArgumentException) { zeroRejected = true; }
        Assert(zeroRejected, "zero-second interval is rejected");
    }

    private static void TestHeartbeatCoalescing(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-heart-pending.json", 60);
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        forwarder.Enqueue(RateExchange("HeartBeat", "KST-001", "HeartBeat", 100, "first-1"));
        forwarder.Enqueue(RateExchange("HeartBeat", "KST-002", "HeartBeat", 200, "first-2"));
        forwarder.Enqueue(RateExchange("HeartBeat", "KST-001", "HeartBeat", 101, "latest-1"));
        forwarder.Enqueue(RateExchange("HeartBeat", "KST-002", "HeartBeat", 201, "latest-2"));
        KstMqttQueue queue = new KstMqttQueue(settings.QueueFile);
        Assert(queue.Count == 2, "one pending heartbeat per device, not four");
        Assert(Convert.ToInt32(FindQueued(queue, "HeartBeat", "KST001", null).Body["HeartBeat"]) == 101,
            "first device retains its latest heartbeat");
        Assert(Convert.ToInt32(FindQueued(queue, "HeartBeat", "KST002", null).Body["HeartBeat"]) == 201,
            "second device retains its latest heartbeat");
        forwarder.Stop();
    }

    private static void TestOeeShiftCoalescing(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-oee-pending.json", 60);
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        const string dayStart = "2026-10-20 08:00:00", dayEnd = "2026-10-20 20:00:00";
        const string nightStart = "2026-10-20 20:00:00", nightEnd = "2026-10-21 08:00:00";
        forwarder.Enqueue(RateOee("KST-001", 0.10, dayStart, dayEnd, "day-old"));
        forwarder.Enqueue(RateOee("KST-001", 0.20, nightStart, nightEnd, "night-old"));
        forwarder.Enqueue(RateOee("KST-002", 0.30, dayStart, dayEnd, "second-old"));
        forwarder.Enqueue(RateOee("KST-001", 0.11, dayStart, dayEnd, "day-latest"));
        forwarder.Enqueue(RateOee("KST-001", 0.21, nightStart, nightEnd, "night-latest"));
        forwarder.Enqueue(RateOee("KST-002", 0.31, dayStart, dayEnd, "second-latest"));
        KstMqttQueue queue = new KstMqttQueue(settings.QueueFile);
        Assert(queue.Count == 3, "one latest OEE per device and shift window");
        Assert(Convert.ToString(FindQueued(queue, "OEE", "KST001", dayStart).Body["ID"]) == "day-latest", "latest day shift OEE retained");
        Assert(Convert.ToString(FindQueued(queue, "OEE", "KST001", nightStart).Body["ID"]) == "night-latest", "latest night shift OEE retained");
        Assert(Convert.ToString(FindQueued(queue, "OEE", "KST002", dayStart).Body["ID"]) == "second-latest", "second device OEE stays isolated");
        forwarder.Stop();
    }

    private static void TestUnchangedStateDeduplication(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-state-pending.json", 60);
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "WORK", "status-old"));
        forwarder.Enqueue(RateExchange("Mode", "KST-001", "Mode", "AUTO", "mode-old"));
        forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "WORK", "status-new"));
        forwarder.Enqueue(RateExchange("Mode", "KST-001", "Mode", "AUTO", "mode-new"));
        KstMqttQueue queue = new KstMqttQueue(settings.QueueFile);
        Assert(queue.Count == 2, "unchanged Status and Mode do not add queue entries");
        forwarder.Stop();
    }

    private static void TestEventsRemainLossless(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-event-pending.json", 60);
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        forwarder.Enqueue(RateExchange("Alarm", "KST-001", "EventMessage", "alarm 1", "alarm-1"));
        forwarder.Enqueue(RateExchange("Alarm", "KST-001", "EventMessage", "alarm 2", "alarm-2"));
        forwarder.Enqueue(RateExchange("ExposureResult", "KST-001", "CRESULT", "OK", "exposure-1"));
        forwarder.Enqueue(RateExchange("ExposureResult", "KST-001", "CRESULT", "OK", "exposure-2"));
        KstMqttQueue queue = new KstMqttQueue(settings.QueueFile);
        Assert(queue.Count == 4, "all alarms and exposure results remain queued");
        Assert(Convert.ToString(queue.Peek(0).Body["ID"]) == "alarm-1" && Convert.ToString(queue.Peek(3).Body["ID"]) == "exposure-2",
            "event order and source identity retained");
        forwarder.Stop();
    }

    private static void TestLegacyQueueCompaction(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-legacy-pending.json", 60);
        KstMqttQueue original = new KstMqttQueue(settings.QueueFile);
        KstExchange[] exchanges = new[] {
            RateExchange("HeartBeat", "KST-001", "HeartBeat", 100, "old-heart"),
            RateExchange("Alarm", "KST-001", "EventMessage", "alarm", "alarm"),
            RateExchange("HeartBeat", "KST-001", "HeartBeat", 101, "latest-heart"),
            RateExchange("ExposureResult", "KST-001", "CRESULT", "OK", "exposure")
        };
        foreach (KstExchange exchange in exchanges)
        {
            KstMqttQueueItem item = KstMqttQueueItem.FromExchange(exchange);
            item.S1DeviceNumber = "KST001";
            original.Enqueue(item);
        }
        Assert(original.Count == 4, "legacy queue fixture contains four items");
        DateTime migrationStartedUtc = DateTime.UtcNow;
        KstMqttForwarder migrationOwner = new KstMqttForwarder(settings, null);
        KstMqttQueue migrated = new KstMqttQueue(settings.QueueFile);
        Assert(migrated.Count == 3, "legacy telemetry collapsed while both events remain");
        Assert(Convert.ToString(FindQueued(migrated, "HeartBeat", "KST001", null).Body["HeartBeat"]) == "101", "latest legacy heartbeat retained");
        int alarmIndex = -1, exposureIndex = -1;
        for (int index = 0; index < migrated.Count; index++)
        {
            if (migrated.Peek(index).Method == "Alarm") alarmIndex = index;
            if (migrated.Peek(index).Method == "ExposureResult") exposureIndex = index;
        }
        Assert(alarmIndex >= 0 && exposureIndex > alarmIndex, "migration preserves alarm and exposure order");
        IList rawMigrated = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(settings.QueueFile, Encoding.UTF8)) as IList;
        DateTimeOffset due = DateTimeOffset.MinValue;
        foreach (object raw in rawMigrated)
        {
            Dictionary<string, object> saved = raw as Dictionary<string, object>;
            if (Convert.ToString(saved["method"]) != "HeartBeat") continue;
            object rawDue;
            Assert(saved.TryGetValue("not_before_utc", out rawDue), "migrated heartbeat gets a due time");
            Assert(DateTimeOffset.TryParse(Convert.ToString(rawDue), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out due), "migrated heartbeat due time parses");
        }
        Assert(due.UtcDateTime > migrationStartedUtc.AddSeconds(40), "old telemetry backlog does not publish immediately after upgrade");
        string pattern = Path.GetFileName(settings.QueueFile) + ".before-rate-limit-*.json";
        string[] backups = Directory.GetFiles(directory, pattern);
        Assert(backups.Length == 1, "migration creates exactly one pre-rate-limit backup");
        KstMqttQueue backup = new KstMqttQueue(backups[0]);
        Assert(backup.Count == 4, "backup retains every original record");
        migrationOwner.Stop();
        KstMqttForwarder reopened = new KstMqttForwarder(settings, null);
        Assert(Directory.GetFiles(directory, pattern).Length == 1, "already migrated queue is not backed up again");
        reopened.Stop();
    }

    private static void TestHeartbeatArrivingDuringPuback(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-ack-pending.json", 1);
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        HeldAckBroker broker = new HeldAckBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        ManualResetEvent bothPublished = new ManualResetEvent(false);
        int successfulPublishes = 0;
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, delegate(string line)
        {
            if (line.Contains("发送成功") && Interlocked.Increment(ref successfulPublishes) == 2) bothPublished.Set();
        });
        try
        {
            forwarder.Enqueue(RateExchange("HeartBeat", "KST-001", "HeartBeat", 100, "first-heart"));
            forwarder.Start();
            Assert(broker.FirstPublished.WaitOne(5000), "first heartbeat reached broker");
            if (broker.Error != null) throw broker.Error;
            forwarder.Enqueue(RateExchange("HeartBeat", "KST-001", "HeartBeat", 101, "second-heart"));
            broker.ReleaseFirstAck.Set();
            Assert(broker.SecondPublished.WaitOne(6000), "new heartbeat published after old PUBACK");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.Payloads.Count == 2 && broker.Payloads[0].Contains("first-heart") && broker.Payloads[1].Contains("second-heart"),
                "both in-flight and newer samples reached broker");
            Assert(bothPublished.WaitOne(4000), "both MQTT PUBACKs completed");
            Assert(new KstMqttQueue(settings.QueueFile).Count == 0, "queue drained after both PUBACKs");
        }
        finally
        {
            broker.ReleaseFirstAck.Set();
            forwarder.Stop();
            listener.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestRestartRetainsTelemetryDueTime(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-restart-pending.json", 2);
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        FakeBroker broker = new FakeBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        KstMqttForwarder first = new KstMqttForwarder(settings, null);
        KstMqttForwarder restarted = null;
        try
        {
            first.Enqueue(RateExchange("HeartBeat", "KST-001", "HeartBeat", 100, "restart-heart"));
            string dueBefore = QueueDueTime(settings.QueueFile);
            first.Stop();
            restarted = new KstMqttForwarder(settings, null);
            Assert(QueueDueTime(settings.QueueFile) == dueBefore, "restart preserves original due time");
            restarted.Start();
            Assert(!broker.Received.WaitOne(300), "restart does not publish a buffered heartbeat immediately");
            Assert(broker.Received.WaitOne(5000), "buffered heartbeat publishes after its due time");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.Payload.Contains("restart-heart"), "restart publishes the buffered sample");
        }
        finally
        {
            if (restarted != null) restarted.Stop();
            first.Stop();
            listener.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestPublishedStateDeduplication(string directory)
    {
        TestPublishedStateDeduplication(directory, "Status", "WORK", "IDLE");
        TestPublishedStateDeduplication(directory, "Mode", "AUTO", "MANUAL");
    }

    private static void TestPublishedStateDeduplication(string directory, string method, string firstValue, string changedValue)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-published-" + method + ".json", 60);
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        FakeBroker broker = new FakeBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        StringBuilder reports = new StringBuilder();
        ManualResetEvent sent = new ManualResetEvent(false);
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, delegate(string line)
        {
            lock (reports) reports.AppendLine(line);
            if (line.Contains("发送成功")) sent.Set();
        });
        try
        {
            forwarder.Enqueue(RateExchange(method, "KST-001", method, firstValue, "first-" + method));
            forwarder.Start();
            Assert(broker.Received.WaitOne(4000), method + " initial change publishes promptly");
            if (broker.Error != null) throw broker.Error;
            bool drained = sent.WaitOne(4000) && new KstMqttQueue(settings.QueueFile).Count == 0;
            string status;
            lock (reports) status = reports.ToString();
            Assert(drained, method + " initial change is acknowledged; MQTT status: " + status);
        }
        finally
        {
            forwarder.Stop();
            listener.Stop();
            brokerThread.Join(3000);
        }

        KstMqttForwarder restarted = new KstMqttForwarder(settings, null);
        restarted.Enqueue(RateExchange(method, "KST-001", method, firstValue, "same-" + method));
        Assert(new KstMqttQueue(settings.QueueFile).Count == 0,
            method + " same value does not enqueue after PUBACK and process restart");
        restarted.Enqueue(RateExchange(method, "KST-001", method, changedValue, "changed-" + method));
        Assert(new KstMqttQueue(settings.QueueFile).Count == 1,
            method + " changed value still enqueues after restart");
        restarted.Stop();
    }

    private static void TestStateReturnAfterMissingPuback(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-state-ambiguous-ack.json", 60);
        KstMqttQueue queue = new KstMqttQueue(settings.QueueFile, settings);
        KstMqttQueueItem initialWork = KstMqttQueueItem.FromExchange(
            RateExchange("Status", "KST-001", "Status", "WORK", "initial-work"));
        initialWork.S1DeviceNumber = "KST001";
        Assert(queue.EnqueueManaged(initialWork), "initial WORK is queued");
        KstMqttQueueClaim confirmedWork = queue.ClaimDue(0, DateTime.UtcNow);
        Assert(confirmedWork != null, "initial WORK is available to publish");
        queue.Complete(confirmedWork, DateTime.UtcNow);
        Assert(queue.Count == 0, "initial WORK is confirmed and removed");

        KstMqttQueueItem stop = KstMqttQueueItem.FromExchange(
            RateExchange("Status", "KST-001", "Status", "STOP", "unconfirmed-stop"));
        stop.S1DeviceNumber = "KST001";
        Assert(queue.EnqueueManaged(stop), "STOP is queued");
        KstMqttQueueClaim unconfirmedStop = queue.ClaimDue(0, DateTime.UtcNow);
        Assert(unconfirmedStop != null, "STOP is in flight without PUBACK");

        KstMqttQueueItem returnedWork = KstMqttQueueItem.FromExchange(
            RateExchange("Status", "KST-001", "Status", "WORK", "returned-work"));
        returnedWork.S1DeviceNumber = "KST001";
        Assert(queue.EnqueueManaged(returnedWork), "return to WORK is queued while STOP is in flight");
        queue.Release(unconfirmedStop);
        Assert(queue.Count == 1, "ambiguous STOP ACK leaves one latest state pending");
        Assert(Convert.ToString(queue.Peek().Body["Status"]) == "WORK" &&
            Convert.ToString(queue.Peek().Body["ID"]) == "returned-work",
            "return to WORK remains pending because S1 may have received STOP");
        KstMqttQueue restored = new KstMqttQueue(settings.QueueFile, settings);
        Assert(restored.Count == 1 && Convert.ToString(restored.Peek().Body["Status"]) == "WORK",
            "return to WORK survives queue restart");
    }

    private static void TestReconnectAfterIdleBrokerClose(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-keepalive-pending.json", 1);
        settings.KeepAliveSeconds = 5;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        FakeBroker broker = new FakeBroker(listener, 2);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        KstMqttClient client = new KstMqttClient(settings, settings.DeviceByNumber("KST001"));
        try
        {
            KstMqttMessage first = KstMqttTemplate.Build(KstMqttQueueItem.FromExchange(
                RateExchange("HeartBeat", "KST-001", "HeartBeat", 100, "keepalive-first")), settings);
            client.Publish(first);
            Assert(broker.FirstPublishAcknowledged.WaitOne(3000), "broker acknowledged first publish before closing idle connection");
            if (broker.Error != null) throw broker.Error;
            // The broker has closed the socket; the next send occurs after 75% of the minimum keepalive.
            Assert(!broker.Received.WaitOne(4100), "second connection is not made before a new publish");
            KstMqttMessage second = KstMqttTemplate.Build(KstMqttQueueItem.FromExchange(
                RateExchange("HeartBeat", "KST-001", "HeartBeat", 101, "keepalive-second")), settings);
            client.Publish(second);
            Assert(broker.Received.WaitOne(3000), "second publish succeeds on a new connection");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.ClientIds.Count == 2, "stale MQTT connection was replaced");
            Assert(broker.Payload.Contains("keepalive-second"), "broker received the second heartbeat");
        }
        finally
        {
            client.Close();
            listener.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestIdleConnectionKeepAlive(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-idle-ping-pending.json", 1);
        settings.KeepAliveSeconds = 5;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        KeepAliveBroker broker = new KeepAliveBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        try
        {
            forwarder.Start();
            forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "WORK", "ping-first"));
            Assert(broker.FirstPublished.WaitOne(3000), "broker acknowledged the first publish");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.PingObserved.WaitOne(6000), "idle forwarder sends PINGREQ before five-second MQTT keepalive expires");
            if (broker.Error != null) throw broker.Error;
            forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "IDLE", "ping-second"));
            Assert(broker.SecondPublished.WaitOne(3000), "second publish uses the same MQTT connection after PINGRESP");
            if (broker.Error != null) throw broker.Error;
        }
        finally
        {
            forwarder.Stop();
            listener.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestFailedIdlePingReconnects(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-idle-reconnect-pending.json", 1);
        settings.KeepAliveSeconds = 5;
        settings.RetryIntervalSeconds = 1;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        DropPingBroker broker = new DropPingBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        try
        {
            forwarder.Start();
            forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "WORK", "reconnect-first"));
            Assert(broker.FirstPublished.WaitOne(3000), "broker acknowledged the one business publish");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.PingDropped.WaitOne(6000), "broker dropped the idle PINGREQ connection");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.Reconnected.WaitOne(5000), "forwarder reconnects the same device without any new business data");
            if (broker.Error != null) throw broker.Error;
        }
        finally
        {
            forwarder.Stop();
            listener.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestRejectedConnackRetries(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-rejected-connack.json", 60);
        settings.RetryIntervalSeconds = 1;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        FakeBroker broker = new FakeBroker(listener, 2, true, true);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        ManualResetEvent sent = new ManualResetEvent(false);
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, delegate(string message) {
            if (message.StartsWith("S1 MQTT 发送成功：", StringComparison.Ordinal)) sent.Set();
        });
        try
        {
            forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "WORK", "retry-after-connack-reject"));
            forwarder.Start();
            Assert(broker.FirstConnectionRejected.WaitOne(3000), "broker rejects KST001 CONNACK once");
            Assert(broker.Received.WaitOne(5000), "KST001 retries CONNECT and publishes after CONNACK rejection");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.ClientIds.Count == 2 && broker.ClientIds[0] == "S&KST001&17&1" &&
                broker.ClientIds[1] == "S&KST001&17&1", "retry uses the same MQTT client ID");
            Assert(broker.Topic == "/17/KST001/property/post", "retried state reaches the correct property topic");
            Assert(sent.WaitOne(3000), "forwarder completes the retry after PUBACK");
            Assert(new KstMqttQueue(settings.QueueFile).Count == 0, "retried state leaves the queue after PUBACK");
        }
        finally
        {
            forwarder.Stop();
            listener.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestPublishResumesPingAfterMaintenanceFailure(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-publish-after-maintenance-failure.json", 60);
        settings.KeepAliveSeconds = 5;
        settings.RetryIntervalSeconds = 30;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        RejectThenPingBroker broker = new RejectThenPingBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        ManualResetEvent maintenanceFailed = new ManualResetEvent(false);
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, delegate(string message) {
            if (message.StartsWith("S1 MQTT 保活或重连失败：设备 KST001", StringComparison.Ordinal)) maintenanceFailed.Set();
        });
        try
        {
            forwarder.Enqueue(RateExchange("HeartBeat", "KST-001", "HeartBeat", 100, "buffered-before-reject"));
            forwarder.Start();
            Assert(broker.Rejected.WaitOne(3000), "broker rejects the first maintenance CONNECT");
            Assert(maintenanceFailed.WaitOne(3000), "maintenance enters its 30-second retry after CONNACK rejection");
            forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "WORK", "publish-recovers-session"));
            Assert(broker.Published.WaitOne(3000), "due state publish establishes a healthy session during maintenance retry");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.PingObserved.WaitOne(6000), "healthy session pings despite earlier 30-second maintenance retry");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.PingDelayMilliseconds <= 4500, "PINGREQ occurs within the five-second keepalive budget");
            Assert(broker.PublishCount == 1, "buffered heartbeat is not emitted early to keep MQTT online");
        }
        finally
        {
            forwarder.Stop();
            broker.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestBufferedHeartbeatMaintainsSourceConnection(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-first-heart-connect.json", 60);
        settings.KeepAliveSeconds = 5;
        settings.RetryIntervalSeconds = 1;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IdleSourceBroker broker = new IdleSourceBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        try
        {
            forwarder.Enqueue(RateExchange("HeartBeat", "KST-001", "HeartBeat", 100, "first-heart-buffered"));
            Assert(new KstMqttQueue(settings.QueueFile).Count == 1, "first heartbeat is durably buffered for the 60-second interval");
            forwarder.Start();
            Assert(broker.Connected.WaitOne(3000), "KST001 connects promptly after an accepted heartbeat, before its publish is due");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.Pinged.WaitOne(6000), "KST001 receives an idle PINGREQ and responds without a property publish");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.Reconnected.WaitOne(6000), "KST001 reconnects after the broker closes its idle socket");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.ClientIds.Count == 2 && broker.ClientIds[0] == "S&KST001&17&1" &&
                broker.ClientIds[1] == "S&KST001&17&1", "silent KST002 never connects and the dropped KST001 reconnects");
            Assert(broker.PublishCount == 0, "connection maintenance does not bypass the 60-second telemetry rate limit");
            Assert(new KstMqttQueue(settings.QueueFile).Count == 1, "buffered heartbeat remains pending after connection maintenance");
        }
        finally
        {
            forwarder.Stop();
            broker.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestHealthyIdlePublishReusesConnection(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-late-healthy-publish.json", 60);
        settings.KeepAliveSeconds = 5;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        LatePublishBroker broker = new LatePublishBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        KstMqttClient client = new KstMqttClient(settings, settings.DeviceByNumber("KST001"));
        try
        {
            Assert(client.MaintainConnection(), "first MaintainConnection establishes MQTT session");
            Assert(broker.Connected.WaitOne(3000), "broker accepts the idle session");
            Thread.Sleep(4000); // Past 75% of the five-second keepalive, while the broker still holds the healthy socket.
            client.Publish(KstMqttTemplate.Build(KstMqttQueueItem.FromExchange(
                RateExchange("Status", "KST-001", "Status", "WORK", "late-healthy-publish")), settings));
            Assert(broker.Published.WaitOne(3000), "broker receives the late publish");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.PingCount == 1, "late publish refreshes the idle session with PINGREQ first");
            Assert(broker.ConnectCount == 1, "late publish reuses healthy session rather than disconnecting it");
        }
        finally
        {
            client.Close();
            broker.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestPingWhileOtherDeviceBacklogDrains(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-backlog-ping.json", 60);
        settings.KeepAliveSeconds = 5;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        BacklogBroker broker = new BacklogBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        try
        {
            forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "WORK", "backlog-first-device"));
            for (int i = 0; i < 16; i++)
                forwarder.Enqueue(RateExchange("Alarm", "KST-002", "EventMessage", "slow event", "backlog-second-" + i));
            forwarder.Start();
            Assert(broker.FirstPublished.WaitOne(4000), "first device is online before slow second-device event backlog");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.PingObserved.WaitOne(9000), "first device receives PINGREQ while the other device drains backlog");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.PingDelayMilliseconds <= 4500,
                "first device is pinged near its 3.75-second idle deadline rather than after the event backlog");
            Assert(broker.AllAcknowledged.WaitOne(9000), "all second-device events remain lossless");
            if (broker.Error != null) throw broker.Error;
        }
        finally
        {
            forwarder.Stop();
            broker.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestPingWhileOtherDeviceConnackStalls(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-stalled-connack-ping.json", 60);
        settings.KeepAliveSeconds = 5;
        settings.RequestTimeoutSeconds = 10;
        settings.RetryIntervalSeconds = 1;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        StalledConnackBroker broker = new StalledConnackBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        try
        {
            forwarder.Start();
            forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "WORK", "online-before-blackhole"));
            Assert(broker.FirstPublished.WaitOne(3000), "KST001 is connected and its initial state is acknowledged");
            if (broker.Error != null) throw broker.Error;
            forwarder.Enqueue(RateExchange("Alarm", "KST-002", "EventMessage", "stalled connection", "blackholed-second"));
            Assert(broker.SecondConnectStalled.WaitOne(3000), "KST002 reaches broker TCP but receives no CONNACK");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.PingObserved.WaitOne(5500), "KST001 PINGREQ is not blocked by KST002 ten-second CONNACK timeout");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.PingDelayMilliseconds <= 4500, "KST001 pings within its five-second keepalive budget");
        }
        finally
        {
            forwarder.Stop();
            broker.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestSingleForwarderQueueOwner(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-exclusive-pending.json", 60);
        KstMqttForwarder first = new KstMqttForwarder(settings, null);
        KstMqttForwarder unexpectedSecond = null;
        KstMqttForwarder afterStop = null;
        try
        {
            bool refused = false;
            try { unexpectedSecond = new KstMqttForwarder(settings, null); }
            catch (IOException) { refused = true; }
            catch (InvalidOperationException) { refused = true; }
            Assert(refused, "second Forwarder must reject concurrent ownership of the same queue");
            first.Stop();
            afterStop = new KstMqttForwarder(settings, null);
            Assert(afterStop != null, "queue can be reopened after owner stops");
        }
        finally
        {
            if (afterStop != null) afterStop.Stop();
            if (unexpectedSecond != null) unexpectedSecond.Stop();
            first.Stop();
        }
    }

    private static void TestFailedDeviceDoesNotDelayOtherDueDevice(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-device-fairness.json", 2);
        settings.RetryIntervalSeconds = 30;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        RejectPrimaryBroker broker = new RejectPrimaryBroker(listener, "S&KST001&17&1", "S&KST002&17&1");
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        try
        {
            forwarder.Enqueue(RateExchange("Status", "KST-001", "Status", "WORK", "first-device-fails"));
            forwarder.Enqueue(RateExchange("HeartBeat", "KST-002", "HeartBeat", 202, "second-device-due"));
            // Enqueue signals are pending before Start. Drain that startup wake so a failed
            // first flush must schedule the second device's due time itself.
            FieldInfo wakeField = typeof(KstMqttForwarder).GetField("wake", BindingFlags.Instance | BindingFlags.NonPublic);
            AutoResetEvent pendingWake = wakeField.GetValue(forwarder) as AutoResetEvent;
            Assert(pendingWake != null && pendingWake.WaitOne(0), "startup enqueue wake is drained for deterministic scheduling");
            forwarder.Start();
            Assert(broker.FirstConnectionRejected.WaitOne(3000), "first device connection is rejected");
            Assert(broker.SecondPublished.WaitOne(6000), "second device publishes near its two-second due time despite 30-second retry");
            if (broker.Error != null) throw broker.Error;
            Assert(broker.Topics.Contains("/17/KST002/property/post"), "second device heartbeat reached S1 broker");
        }
        finally
        {
            forwarder.Stop();
            broker.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestStopWaitsForInFlightPublisher(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "rate-stop-restart.json", 1);
        settings.RequestTimeoutSeconds = 10;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        HeldAckBroker broker = new HeldAckBroker(listener);
        Thread brokerThread = new Thread(new ThreadStart(broker.Run));
        brokerThread.IsBackground = true;
        brokerThread.Start();
        ManualResetEvent oldWorkerInReport = new ManualResetEvent(false);
        ManualResetEvent releaseOldWorker = new ManualResetEvent(false);
        ManualResetEvent stopReturned = new ManualResetEvent(false);
        KstMqttForwarder first = new KstMqttForwarder(settings, delegate(string line)
        {
            if (!line.Contains("待重试")) return;
            oldWorkerInReport.Set();
            releaseOldWorker.WaitOne(5000);
        });
        Thread stopper = null;
        KstMqttForwarder restarted = null;
        try
        {
            first.Enqueue(RateExchange("HeartBeat", "KST-001", "HeartBeat", 100, "in-flight-before-stop"));
            first.Start();
            Assert(broker.FirstPublished.WaitOne(5000), "old publisher reaches broker before missing PUBACK");
            stopper = new Thread(new ThreadStart(delegate { first.Stop(); stopReturned.Set(); }));
            stopper.IsBackground = true;
            stopper.Start();
            Assert(oldWorkerInReport.WaitOne(5000), "stopping aborts blocked publish and reaches worker callback");
            Assert(!stopReturned.WaitOne(100), "Stop does not return while old publisher thread is still active");
            releaseOldWorker.Set();
            Assert(stopReturned.WaitOne(5000), "Stop returns after old publisher exits");
            restarted = new KstMqttForwarder(settings, null);
            restarted.Enqueue(RateExchange("HeartBeat", "KST-001", "HeartBeat", 101, "new-after-stop"));
            KstMqttQueue persisted = new KstMqttQueue(settings.QueueFile);
            Assert(persisted.Count == 1 && Convert.ToString(persisted.Peek().Body["ID"]) == "new-after-stop",
                "new Forwarder keeps latest sample after old worker terminates");
        }
        finally
        {
            releaseOldWorker.Set();
            broker.ReleaseFirstAck.Set();
            if (stopper != null) stopper.Join(5000);
            if (restarted != null) restarted.Stop();
            first.Stop();
            listener.Stop();
            brokerThread.Join(3000);
        }
    }

    private static void TestMultipleDevices(KstMqttSettings settings, string directory)
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            settings.QueueFile = Path.Combine(directory, "multiple-pending.json");
            settings.SourceDeviceCode = "KST-001";
            settings.AdditionalDevices.Add(new KstMqttDevice {
                SourceDeviceCode = "KST-002", DeviceNumber = "KST002"
            });
            settings.Validate();

            string config = Path.Combine(directory, "multi-config.json");
            settings.Save(config);
            KstMqttSettings loaded = KstMqttSettings.Load(config);
            Assert(loaded.AdditionalDevices.Count == 1, "additional device survives save and load");
            Assert(loaded.SourceDeviceCode == "KST-001", "first source alias survives save and load");
            Assert(loaded.AdditionalDevices[0].DeviceNumber == "KST002", "second S1 device number survives save");

            FakeBroker broker = new FakeBroker(listener, 2);
            Thread brokerThread = new Thread(new ThreadStart(broker.Run));
            brokerThread.IsBackground = true;
            brokerThread.Start();
            KstMqttForwarder forwarder = new KstMqttForwarder(loaded, null);
            KstMqttForwarder restarted = null;
            try
            {
                KstExchange first = new KstExchange();
                first.Time = new DateTime(2026, 10, 17, 9, 41, 39);
                first.Method = "OEE";
                first.DeviceCode = "KST-001";
                first.RequestBody = "{\"assets\":\"KST-001\",\"OEE\":0.25}";
                KstExchange second = new KstExchange();
                second.Time = first.Time;
                second.Method = "Status";
                second.DeviceCode = "KST-002";
                second.RequestBody = "{\"assets\":\"KST-002\",\"Status\":\"WORK\"}";

                forwarder.Enqueue(first);
                forwarder.Enqueue(second);
                Assert(new KstMqttQueue(loaded.QueueFile).Count == 2, "both device messages persisted before publish");
                Assert(File.ReadAllText(loaded.QueueFile, Encoding.UTF8).Contains("\"s1_device_number\":\"KST002\""),
                    "second target fixed in durable queue");

                KstExchange unknown = new KstExchange();
                unknown.Time = first.Time;
                unknown.Method = "Status";
                unknown.DeviceCode = "KST-003";
                unknown.RequestBody = "{\"assets\":\"KST-003\",\"Status\":\"WORK\"}";
                bool unknownRejected = false;
                try { forwarder.Enqueue(unknown); }
                catch (InvalidOperationException) { unknownRejected = true; }
                Assert(unknownRejected, "unconfigured device rejected before acknowledgement");
                unknown.DeviceCode = "";
                unknown.RequestBody = "{\"Status\":\"WORK\"}";
                bool unlabelledRejected = false;
                try { forwarder.Enqueue(unknown); }
                catch (InvalidOperationException) { unlabelledRejected = true; }
                Assert(unlabelledRejected, "unlabelled request rejected when two devices configured");

                unknown.DeviceCode = "KST-001";
                unknown.RequestBody = "{\"MachineNO\":\"KST-001\",\"assets\":\"KST-002\",\"Status\":\"WORK\"}";
                bool conflictingIdentifiersRejected = false;
                try { forwarder.Enqueue(unknown); }
                catch (InvalidOperationException) { conflictingIdentifiersRejected = true; }
                Assert(conflictingIdentifiersRejected, "conflicting MachineNO and assets rejected in multi-device mode");
                Assert(new KstMqttQueue(loaded.QueueFile).Count == 2, "invalid messages not persisted");

                forwarder.Stop();
                restarted = new KstMqttForwarder(loaded, null);
                restarted.Start();
                Assert(broker.Received.WaitOne(5000), "both device publishes received");
            }
            finally { if (restarted != null) restarted.Stop(); forwarder.Stop(); }
            brokerThread.Join(3000);
            if (broker.Error != null) throw broker.Error;
            Assert(broker.ClientIds.Count == 2, "two MQTT connections");
            Assert(broker.ClientIds.Contains("S&KST001&88&9"), "first client id");
            Assert(broker.Topics.Contains("/88/KST001/property/post"), "first topic");
            Assert(broker.ClientIds.Contains("S&KST002&88&9"), "second client id");
            Assert(broker.Topics.Contains("/88/KST002/property/post"), "second topic");
            Assert(File.ReadAllText(loaded.QueueFile, Encoding.UTF8) == "[]", "two device queue removed after PUBACK");

            loaded.AdditionalDevices[0].Username = "second-user";
            loaded.AdditionalDevices[0].Password = "second-password";
            Assert(loaded.ResolveDevice("KST-002", false).Username == "second-user", "optional second username overrides common account");
            Assert(loaded.ResolveDevice("KST-002", false).Password == "second-password", "optional second password overrides common account");
        }
        finally { listener.Stop(); }
    }

    private static void TestTrustedProxyRoutePersistsSecondDevice(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "trusted-proxy-pending.json", 60);
        TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        KstMesServer server = new KstMesServer(Path.Combine(directory, "trusted-proxy-logs"));
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        try
        {
            MethodInfo configure = typeof(KstMesServer).GetMethod("ConfigureSourceRoutes",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                new[] { typeof(string), typeof(Dictionary<string, string>), typeof(Dictionary<string, string>) }, null);
            Assert(configure != null, "HTTP server supports trusted proxy source routing for MQTT integration");
            configure.Invoke(server, new object[] { "127.0.0.1",
                new Dictionary<string, string> { { "192.168.10.1:9000", "KST-001" }, { "192.168.10.1:9001", "KST-002" } },
                new Dictionary<string, string> { { "192.168.10.1:9001", "KST-001" } } });
            server.RequestAccepted += forwarder.Enqueue;
            server.Start("127.0.0.1", port, new Dictionary<string, string> {
                { "OEE", "{\"success\":true}" }, { "Status", "{\"success\":true}" },
                { "HeartBeat", "{\"success\":true}" }
            });

            Assert(ProxyHttpStatus(port, "/OEE", "192.168.10.1:9001",
                "{\"assets\":\"KST-001\",\"OEE\":0.25,\"StartTime\":\"2026-10-20 08:00:00\",\"EndTime\":\"2026-10-20 20:00:00\"}") == 200,
                "9001 OEE with stale assets is acknowledged after KST002 queue persistence");
            Assert(ProxyHttpStatus(port, "/Status", "192.168.10.1:9001",
                "{\"assets\":\"KST-001\",\"Status\":\"WORK\"}") == 200,
                "9001 Status with stale assets is acknowledged after KST002 queue persistence");
            Assert(ProxyHttpStatus(port, "/Heart", "192.168.10.1:9001",
                "{\"MachineNO\":\"KST-002\",\"assets\":\"KST-001\",\"HeartBeat\":13382}") == 200,
                "9001 Heart with mismatched MachineNO and stale assets is acknowledged after KST002 queue persistence");
            KstMqttQueue persisted = new KstMqttQueue(settings.QueueFile);
            Assert(persisted.Count == 3, "all three 9001 business requests persist in the durable queue");
            for (int i = 0; i < persisted.Count; i++)
            {
                KstMqttQueueItem item = persisted.Peek(i);
                Assert(item.S1DeviceNumber == "KST002", "9001 queue entry " + i + " targets KST002");
                Assert(item.RequestHost == "192.168.10.1:9001" && item.RoutingSource == "trusted_proxy_host"
                    && item.SourceClientIp == "127.0.0.1", "9001 queue entry " + i + " retains verified TCP source provenance");
            }

            string secondResult = "{\"function\":\"result\",\"assets\":\"KST-001\",\"CMACHINE_CODE\":\"KST-002\","
                + "\"CRESULT\":\"NG\",\"CITEM_NO\":\"board-2\",\"CSTART_TIME\":\"2026-10-20 14:04:21\","
                + "\"CEND_TIME\":\"2026-10-20 14:04:34\",\"MachineTime\":\"2026-10-20 14:31:00\"}";
            Assert(ProxyHttpStatus(port, "/Log", "192.168.10.1:9001", secondResult) == 200,
                "9001 result Log with stale assets is acknowledged after KST002 queue persistence");
            Assert(ProxyHttpStatus(port, "/Log", "192.168.10.1:9000",
                "{\"function\":\"result\",\"assets\":\"KST-001\",\"CMACHINE_CODE\":\"KST-001\",\"CRESULT\":\"OK\",\"CITEM_NO\":\"board-1\"}") == 200,
                "9000 result Log is acknowledged after KST001 queue persistence");
            Assert(ProxyHttpStatus(port, "/Log", "192.168.10.1:9001",
                "{\"function\":\"info\",\"CMACHINE_CODE\":\"KST-002\",\"CRESULT\":\"OK\"}") == 200,
                "non-result Log remains a local diagnostic only");
            Assert(ProxyHttpStatus(port, "/Log", "192.168.10.1:9001",
                "{\"function\":\"result\",\"CMACHINE_CODE\":\"KST-002\",\"CITEM_NO\":\"incomplete\"}") == 500,
                "result Log without CRESULT cannot falsely acknowledge failed S1 mapping");
            KstMqttQueue resultQueue = new KstMqttQueue(settings.QueueFile);
            Assert(resultQueue.Count == 5, "only two result Logs add exposure reports to the durable queue");
            KstMqttQueueItem queuedSecond = resultQueue.Peek(3);
            KstMqttQueueItem queuedFirst = resultQueue.Peek(4);
            Assert(queuedSecond.Method == "ExposureResult" && queuedSecond.DeviceCode == "KST-002"
                && queuedSecond.S1DeviceNumber == "KST002" && queuedSecond.RequestHost == "192.168.10.1:9001",
                "stale assets in 9001 result cannot redirect its exposure report to KST001");
            Assert(queuedFirst.Method == "ExposureResult" && queuedFirst.DeviceCode == "KST-001"
                && queuedFirst.S1DeviceNumber == "KST001", "9000 result belongs to KST001");
            KstMqttMessage resultMessage = KstMqttTemplate.Build(queuedSecond, settings, queuedSecond.S1DeviceNumber);
            IList resultModels = new JavaScriptSerializer().DeserializeObject(resultMessage.Payload) as IList;
            Dictionary<string, object> resultModel = resultModels[0] as Dictionary<string, object>;
            Assert(resultMessage.Topic == "/17/KST002/property/post" && Convert.ToString(resultModel["id"]) == "kst_exposure_result"
                && Convert.ToString(resultModel["value"]) == "NG", "result Log maps CRESULT into the second device S1 property");
            Assert(Convert.ToString(resultModel["remark"]).Contains("\"CITEM_NO\":\"board-2\"")
                && Convert.ToString(resultModel["remark"]).Contains("\"CSTART_TIME\":\"2026-10-20 14:04:21\"")
                && Convert.ToString(resultModel["remark"]).Contains("\"CEND_TIME\":\"2026-10-20 14:04:34\""),
                "result Log keeps exposure identifiers and times in S1 remark");
            TestSourceRoutes(directory, "trusted-proxy-route-config.json").ValidatePendingQueue(settings);
        }
        finally { server.Stop(); forwarder.Stop(); }
    }

    private static int ProxyHttpStatus(int port, string path, string host, string body)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
        request.Proxy = null;
        request.Host = host;
        request.Method = "POST";
        request.ContentType = "application/json; charset=utf-8";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        request.ContentLength = bytes.Length;
        using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
        try
        {
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) return (int)response.StatusCode;
        }
        catch (WebException error)
        {
            using (HttpWebResponse response = (HttpWebResponse)error.Response) return (int)response.StatusCode;
        }
    }

    private static void TestTrustedRouteQueueStartupGuard(string directory)
    {
        KstMqttSettings settings = RateSettings(directory, "source-route-startup-pending.json", 60);
        KstSourceRoutes routes = TestSourceRoutes(directory, "source-route-startup-config.json");
        MethodInfo validate = typeof(KstSourceRoutes).GetMethod("ValidatePendingQueue",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
            new[] { typeof(KstMqttSettings) }, null);
        Assert(validate != null, "trusted source routes provide a read-only pending-queue startup guard");

        Dictionary<string, object> legacy = PendingSourceRouteItem("KST-001", "KST001");
        string legacyBytes = WritePendingSourceRouteItem(settings.QueueFile, legacy);
        AssertPendingSourceRouteRejected(validate, routes, settings,
            "old pending item without source Host provenance blocks startup");
        Assert(File.ReadAllText(settings.QueueFile, Encoding.UTF8) == legacyBytes,
            "startup guard does not rewrite old pending data requiring audit");
        Assert(!File.Exists(settings.QueueFile + ".lock"),
            "legacy queue is rejected before forwarder acquires its queue lease");
        bool constructorRejected = false;
        try { new KstMqttForwarder(settings, delegate(string message) { }, routes); }
        catch (InvalidOperationException) { constructorRejected = true; }
        Assert(constructorRejected && File.ReadAllText(settings.QueueFile, Encoding.UTF8) == legacyBytes,
            "forwarder checks old entries under its queue lease before replay or migration");
        using (FileStream releasedLease = new FileStream(settings.QueueFile + ".lock",
            FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

        Dictionary<string, object> tagged = PendingSourceRouteItem("KST-002", "KST002");
        tagged["request_host"] = "192.168.10.1:9001";
        tagged["routing_source"] = "trusted_proxy_host";
        tagged["source_client_ip"] = "127.0.0.1";
        string taggedBytes = WritePendingSourceRouteItem(settings.QueueFile, tagged);
        validate.Invoke(routes, new object[] { settings });
        Assert(File.ReadAllText(settings.QueueFile, Encoding.UTF8) == taggedBytes,
            "valid tagged pending item survives a read-only restart check");
        KstMqttForwarder restarted = new KstMqttForwarder(settings, delegate(string message) { }, routes);
        restarted.Stop();
        Assert(File.ReadAllText(settings.QueueFile, Encoding.UTF8) == taggedBytes,
            "a routed pending item can restart without queue rewrites");

        tagged["request_host"] = "192.168.10.1:9000";
        string wrongHostBytes = WritePendingSourceRouteItem(settings.QueueFile, tagged);
        AssertPendingSourceRouteRejected(validate, routes, settings,
            "pending source Host and device code mismatch blocks startup");
        Assert(File.ReadAllText(settings.QueueFile, Encoding.UTF8) == wrongHostBytes,
            "Host mismatch remains untouched for manual audit");

        tagged["request_host"] = "192.168.10.1:9001";
        tagged["s1_device_number"] = "KST001";
        string wrongTargetBytes = WritePendingSourceRouteItem(settings.QueueFile, tagged);
        AssertPendingSourceRouteRejected(validate, routes, settings,
            "pending S1 target differing from the routed source blocks startup");
        Assert(File.ReadAllText(settings.QueueFile, Encoding.UTF8) == wrongTargetBytes,
            "target mismatch remains untouched for manual audit");

        tagged["s1_device_number"] = "KST002";
        tagged["device_code"] = "KST-001";
        string wrongCodeBytes = WritePendingSourceRouteItem(settings.QueueFile, tagged);
        AssertPendingSourceRouteRejected(validate, routes, settings,
            "pending device code differing from the routed Host blocks startup");
        Assert(File.ReadAllText(settings.QueueFile, Encoding.UTF8) == wrongCodeBytes,
            "device code mismatch remains untouched for manual audit");

        tagged["device_code"] = "KST-002";
        tagged["source_client_ip"] = "127.0.0.2";
        string wrongPeerBytes = WritePendingSourceRouteItem(settings.QueueFile, tagged);
        AssertPendingSourceRouteRejected(validate, routes, settings,
            "pending entry from an untrusted TCP peer blocks startup despite a trusted Host");
        Assert(File.ReadAllText(settings.QueueFile, Encoding.UTF8) == wrongPeerBytes,
            "source IP mismatch remains untouched for manual audit");
    }

    private static KstSourceRoutes TestSourceRoutes(string directory, string fileName)
    {
        string configPath = Path.Combine(directory, fileName);
        File.WriteAllText(configPath,
            "{\"trusted_proxy_ip\":\"127.0.0.1\",\"source_host_routes\":{\"192.168.10.1:9000\":\"KST-001\",\"192.168.10.1:9001\":\"KST-002\"},\"allowed_stale_assets\":{\"192.168.10.1:9001\":\"KST-001\"}}",
            new UTF8Encoding(false));
        return KstSourceRoutes.Load(configPath);
    }

    private static Dictionary<string, object> PendingSourceRouteItem(string code, string target)
    {
        return new Dictionary<string, object> {
            { "method", "Status" }, { "device_code", code }, { "s1_device_number", target },
            { "queue_id", "route-check-item" }, { "received_at", "2026-10-20T10:00:00.0000000+08:00" },
            { "body", new Dictionary<string, object> { { "assets", "KST-001" }, { "Status", "WORK" } } }
        };
    }

    private static string WritePendingSourceRouteItem(string path, Dictionary<string, object> item)
    {
        string content = new JavaScriptSerializer().Serialize(new object[] { item });
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return content;
    }

    private static void AssertPendingSourceRouteRejected(MethodInfo validate, KstSourceRoutes routes,
        KstMqttSettings settings, string label)
    {
        bool rejected = false;
        try { validate.Invoke(routes, new object[] { settings }); }
        catch (TargetInvocationException error)
        {
            if (error.InnerException is InvalidDataException || error.InnerException is InvalidOperationException)
                rejected = true;
            else throw;
        }
        Assert(rejected, label);
    }

    private static void TestRetryAfterMissingPuback(KstMqttSettings settings, string directory)
    {
        settings.QueueFile = Path.Combine(directory, "retry-pending.json");
        KstExchange exchange = new KstExchange();
        exchange.Time = new DateTime(2026, 10, 17, 9, 50, 0);
        exchange.Method = "Status";
        exchange.DeviceCode = "KST-002";
        exchange.RequestBody = "{\"assets\":\"KST-002\",\"Status\":\"WORK\"}";

        TcpListener failedListener = new TcpListener(IPAddress.Loopback, 0);
        failedListener.Start();
        try
        {
            settings.Port = ((IPEndPoint)failedListener.LocalEndpoint).Port;
            FakeBroker failedBroker = new FakeBroker(failedListener, 1, false);
            Thread failedThread = new Thread(new ThreadStart(failedBroker.Run));
            failedThread.IsBackground = true;
            failedThread.Start();
            KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
            try
            {
                forwarder.Enqueue(exchange);
                forwarder.Start();
                Assert(failedBroker.Received.WaitOne(4000), "broker received publish without PUBACK");
            }
            finally { forwarder.Stop(); }
            failedThread.Join(3000);
            if (failedBroker.Error != null) throw failedBroker.Error;
            Assert(new KstMqttQueue(settings.QueueFile).Count == 1, "second device message retained without PUBACK");
        }
        finally { failedListener.Stop(); }

        TcpListener recoveredListener = new TcpListener(IPAddress.Loopback, 0);
        recoveredListener.Start();
        try
        {
            settings.Port = ((IPEndPoint)recoveredListener.LocalEndpoint).Port;
            FakeBroker recoveredBroker = new FakeBroker(recoveredListener);
            Thread recoveredThread = new Thread(new ThreadStart(recoveredBroker.Run));
            recoveredThread.IsBackground = true;
            recoveredThread.Start();
            ManualResetEvent recoveredSent = new ManualResetEvent(false);
            KstMqttForwarder restarted = new KstMqttForwarder(settings, delegate(string message) {
                if (message.StartsWith("S1 MQTT 发送成功：", StringComparison.Ordinal)) recoveredSent.Set();
            });
            try
            {
                restarted.Start();
                Assert(recoveredBroker.Received.WaitOne(4000), "retained message retried after restart");
                Assert(recoveredSent.WaitOne(4000), "retried message completes after PUBACK before shutdown");
            }
            finally { restarted.Stop(); }
            recoveredThread.Join(3000);
            if (recoveredBroker.Error != null) throw recoveredBroker.Error;
            Assert(recoveredBroker.ClientId == "S&KST002&88&9", "retry uses second device client id");
            Assert(recoveredBroker.Topic == "/88/KST002/property/post", "retry uses second device topic");
            Assert(new KstMqttQueue(settings.QueueFile).Count == 0, "retried message removed after PUBACK");
        }
        finally { recoveredListener.Stop(); }
    }

    private static void TestFailedDeviceDoesNotBlockOtherDevice(KstMqttSettings settings, string directory)
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            settings.RetryIntervalSeconds = 30;
            settings.QueueFile = Path.Combine(directory, "independent-pending.json");
            RejectPrimaryBroker broker = new RejectPrimaryBroker(listener, "S&KST001&88&9", "S&KST002&88&9");
            Thread brokerThread = new Thread(new ThreadStart(broker.Run));
            brokerThread.IsBackground = true;
            brokerThread.Start();

            KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
            try
            {
                KstExchange first = new KstExchange();
                first.Time = DateTime.Now;
                first.Method = "Status";
                first.DeviceCode = "KST-001";
                first.RequestBody = "{\"assets\":\"KST-001\",\"Status\":\"FIRST\"}";
                KstExchange second = new KstExchange();
                second.Time = first.Time;
                second.Method = "Status";
                second.DeviceCode = "KST-001";
                second.RequestBody = "{\"assets\":\"KST-001\",\"Status\":\"SECOND\"}";
                KstExchange other = new KstExchange();
                other.Time = first.Time;
                other.Method = "Status";
                other.DeviceCode = "KST-002";
                other.RequestBody = "{\"assets\":\"KST-002\",\"Status\":\"OTHER\"}";
                forwarder.Enqueue(first);
                forwarder.Enqueue(second);
                forwarder.Enqueue(other);
                forwarder.Start();
                bool secondPublished = broker.SecondPublished.WaitOne(5000);
                if (broker.Error != null) throw broker.Error;
                Assert(secondPublished, "second device can publish while first login is rejected; clients="
                    + string.Join(",", broker.ClientIds.ToArray()) + "; topics=" + string.Join(",", broker.Topics.ToArray()));
            }
            finally { forwarder.Stop(); broker.Stop(); }
            brokerThread.Join(3000);
            if (broker.Error != null) throw broker.Error;
            Assert(broker.ClientIds.Count >= 2, "both active devices attempt to connect independently");
            Assert(broker.ClientIds.Contains("S&KST001&88&9") && broker.FirstConnectionRejected.WaitOne(0),
                "KST001 login rejected regardless of TCP arrival order");
            Assert(broker.ClientIds.Contains("S&KST002&88&9"), "KST002 connected independently");
            Assert(broker.Topics.Count == 1 && broker.Topics[0] == "/88/KST002/property/post", "second device published");
            KstMqttQueue pending = new KstMqttQueue(settings.QueueFile);
            Assert(pending.Count == 1, "failed device retains one coalesced state message");
            Assert(Convert.ToString(pending.Peek(0).Body["Status"]) == "SECOND", "latest failed state remains queued");
        }
        finally { listener.Stop(); settings.RetryIntervalSeconds = 1; }
    }

    private static void TestQueueWriteFailureDoesNotAcknowledge(string directory)
    {
        string blockedPath = Path.Combine(directory, "blocked-queue");
        Directory.CreateDirectory(blockedPath);
        KstMqttQueue queue = new KstMqttQueue(blockedPath);
        KstMqttQueueItem item = new KstMqttQueueItem();
        item.Method = "Status";
        item.DeviceCode = "KST-002";
        item.S1DeviceNumber = "KST002";
        item.ReceivedAt = DateTime.Now;
        item.Body = new Dictionary<string, object>();
        item.Body["Status"] = "WORK";
        bool rejected = false;
        try { queue.Enqueue(item); }
        catch (IOException) { rejected = true; }
        Assert(rejected, "queue write failure returned to request");
        Assert(queue.Count == 0, "failed queue write leaves no in-memory message");
    }

    private static void TestHeartbeatFromBothDevicesCanBeQueued(string directory)
    {
        KstMqttSettings settings = new KstMqttSettings();
        settings.Enabled = true;
        settings.Host = "127.0.0.1";
        settings.Port = 1883;
        settings.ProductId = "17";
        settings.DeviceNumber = "KST001";
        settings.SourceDeviceCode = "KST-001";
        settings.UserId = "1";
        settings.Username = "test";
        settings.Password = "test";
        settings.KeepAliveSeconds = 30;
        settings.RequestTimeoutSeconds = 10;
        settings.RetryIntervalSeconds = 10;
        settings.QueueFile = Path.Combine(directory, "heartbeat-pending.json");
        settings.AdditionalDevices.Add(new KstMqttDevice { SourceDeviceCode = "KST-002", DeviceNumber = "KST002" });
        settings.Validate();
        KstMqttForwarder forwarder = new KstMqttForwarder(settings, null);
        foreach (string code in new[] { "KST-001", "KST-002" })
        {
            KstExchange exchange = new KstExchange();
            exchange.Time = new DateTime(2026, 10, 20, 9, 21, 19);
            exchange.Method = "HeartBeat";
            exchange.DeviceCode = code;
            exchange.RequestBody = "{\"ID\":\"202610200844230461\",\"MachineNO\":\"" + code
                + "\",\"HeartBeat\":13382,\"MachineTime\":\"2026-10-20 09:21:19\",\"function\":\"heartbeat\",\"assets\":\"" + code + "\"}";
            forwarder.Enqueue(exchange);
        }
        KstMqttQueue queue = new KstMqttQueue(settings.QueueFile);
        Assert(queue.Count == 2, "both field heartbeats are durable before HTTP success");
        Assert(queue.Peek(0).S1DeviceNumber == "KST001" && queue.Peek(1).S1DeviceNumber == "KST002",
            "field heartbeat routes to each S1 device");
        Assert(KstMqttTemplate.Build(queue.Peek(0), settings, "KST001").Payload.Contains("\"id\":\"kst_heartbeat\""),
            "heartbeat template creates the S1 property payload");
        forwarder.Stop();
    }

    private sealed class IdleSourceBroker
    {
        private readonly TcpListener listener;
        private volatile bool stopping;
        private TcpClient activeSocket;
        internal readonly ManualResetEvent Connected = new ManualResetEvent(false);
        internal readonly ManualResetEvent Pinged = new ManualResetEvent(false);
        internal readonly ManualResetEvent Reconnected = new ManualResetEvent(false);
        internal readonly List<string> ClientIds = new List<string>();
        internal int PublishCount;
        internal Exception Error;

        internal IdleSourceBroker(TcpListener listener) { this.listener = listener; }

        internal void Run()
        {
            try
            {
                using (TcpClient first = listener.AcceptTcpClient())
                using (NetworkStream stream = first.GetStream())
                {
                    activeSocket = first;
                    stream.ReadTimeout = 8000;
                    ClientIds.Add(AcceptConnect(stream));
                    Connected.Set();
                    byte header;
                    byte[] packet = FakeBroker.ReadPacket(stream, out header);
                    if ((header & 0xF0) == 0x30) PublishCount++;
                    Assert(header == 0xC0 && packet.Length == 0, "buffered heartbeat sends a PINGREQ before any PUBLISH");
                    stream.Write(new byte[] { 0xD0, 0x00 }, 0, 2);
                    Pinged.Set();
                }

                using (TcpClient second = listener.AcceptTcpClient())
                using (NetworkStream stream = second.GetStream())
                {
                    activeSocket = second;
                    stream.ReadTimeout = 8000;
                    ClientIds.Add(AcceptConnect(stream));
                    Reconnected.Set();
                    while (!stopping)
                    {
                        if (!stream.DataAvailable) { Thread.Sleep(25); continue; }
                        byte header;
                        byte[] packet = FakeBroker.ReadPacket(stream, out header);
                        if ((header & 0xF0) == 0x30) PublishCount++;
                        if (header == 0xC0 && packet.Length == 0)
                            stream.Write(new byte[] { 0xD0, 0x00 }, 0, 2);
                        else if (header == 0xE0) break;
                        else Assert(false, "idle broker sees only PINGREQ or DISCONNECT after reconnect");
                    }
                }
            }
            catch (Exception error) { if (!stopping) Error = error; }
        }

        private static string AcceptConnect(NetworkStream stream)
        {
            byte header;
            byte[] connect = FakeBroker.ReadPacket(stream, out header);
            Assert(header == 0x10, "broker receives MQTT CONNECT");
            int index = 0;
            Assert(FakeBroker.ReadText(connect, ref index) == "MQTT", "CONNECT protocol is MQTT");
            index += 4;
            string clientId = FakeBroker.ReadText(connect, ref index);
            stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
            return clientId;
        }

        internal void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch { }
            try { if (activeSocket != null) activeSocket.Close(); } catch { }
        }
    }

    private sealed class LatePublishBroker
    {
        private readonly TcpListener listener;
        private volatile bool stopping;
        private TcpClient activeSocket;
        internal readonly ManualResetEvent Connected = new ManualResetEvent(false);
        internal readonly ManualResetEvent Published = new ManualResetEvent(false);
        internal int ConnectCount;
        internal int PingCount;
        internal Exception Error;

        internal LatePublishBroker(TcpListener listener) { this.listener = listener; }

        internal void Run()
        {
            try
            {
                using (TcpClient first = listener.AcceptTcpClient())
                using (NetworkStream firstStream = first.GetStream())
                {
                    activeSocket = first;
                    firstStream.ReadTimeout = 8000;
                    AcceptConnect(firstStream);
                    Connected.Set();
                    byte header;
                    byte[] packet = FakeBroker.ReadPacket(firstStream, out header);
                    if (header == 0xC0)
                    {
                        Assert(packet.Length == 0, "late publish sends a zero-length PINGREQ");
                        PingCount++;
                        firstStream.Write(new byte[] { 0xD0, 0x00 }, 0, 2);
                        packet = FakeBroker.ReadPacket(firstStream, out header);
                    }
                    if (header == 0xE0)
                    {
                        using (TcpClient second = listener.AcceptTcpClient())
                        using (NetworkStream secondStream = second.GetStream())
                        {
                            activeSocket = second;
                            secondStream.ReadTimeout = 8000;
                            AcceptConnect(secondStream);
                            AcknowledgePublish(secondStream);
                        }
                    }
                    else
                    {
                        Assert(header == 0x32, "healthy idle session receives QoS 1 PUBLISH");
                        AcknowledgePublish(firstStream, packet);
                    }
                    Published.Set();
                }
            }
            catch (Exception error) { if (!stopping) Error = error; Published.Set(); }
        }

        private void AcceptConnect(NetworkStream stream)
        {
            byte header;
            FakeBroker.ReadPacket(stream, out header);
            Assert(header == 0x10, "broker receives CONNECT before publish");
            ConnectCount++;
            stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
        }

        private static void AcknowledgePublish(NetworkStream stream)
        {
            byte header;
            byte[] packet = FakeBroker.ReadPacket(stream, out header);
            Assert(header == 0x32, "broker receives QoS 1 PUBLISH");
            AcknowledgePublish(stream, packet);
        }

        private static void AcknowledgePublish(NetworkStream stream, byte[] packet)
        {
            int index = 0;
            FakeBroker.ReadText(packet, ref index);
            int packetId = (packet[index++] << 8) | packet[index++];
            stream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
        }

        internal void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch { }
            try { if (activeSocket != null) activeSocket.Close(); } catch { }
        }
    }

    private sealed class RejectPrimaryBroker
    {
        private readonly TcpListener listener;
        private readonly string rejectedClientId;
        private readonly string acceptedClientId;
        private readonly List<TcpClient> sockets = new List<TcpClient>();
        private readonly List<Thread> handlers = new List<Thread>();
        private volatile bool stopping;
        internal readonly ManualResetEvent FirstConnectionRejected = new ManualResetEvent(false);
        internal readonly ManualResetEvent SecondPublished = new ManualResetEvent(false);
        internal readonly List<string> ClientIds = new List<string>();
        internal readonly List<string> Topics = new List<string>();
        internal Exception Error;

        internal RejectPrimaryBroker(TcpListener listener, string rejectedClientId, string acceptedClientId)
        {
            this.listener = listener;
            this.rejectedClientId = rejectedClientId;
            this.acceptedClientId = acceptedClientId;
        }

        internal void Run()
        {
            try
            {
                while (!stopping)
                {
                    TcpClient socket = listener.AcceptTcpClient();
                    Thread handler = new Thread(new ParameterizedThreadStart(delegate(object value) { Handle((TcpClient)value); }));
                    handler.IsBackground = true;
                    lock (sockets) { sockets.Add(socket); handlers.Add(handler); }
                    handler.Start(socket);
                }
            }
            catch (Exception error) { if (!stopping) Error = error; }
        }

        private void Handle(TcpClient socket)
        {
            try
            {
                using (socket)
                using (NetworkStream stream = socket.GetStream())
                {
                    stream.ReadTimeout = 5000;
                    byte header;
                    byte[] connect = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x10, "parallel broker receives MQTT CONNECT");
                    int index = 0;
                    FakeBroker.ReadText(connect, ref index);
                    index += 4;
                    string clientId = FakeBroker.ReadText(connect, ref index);
                    lock (ClientIds) ClientIds.Add(clientId);
                    if (clientId == rejectedClientId)
                    {
                        stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x05 }, 0, 4);
                        FirstConnectionRejected.Set();
                        return;
                    }
                    Assert(clientId == acceptedClientId, "other source is KST002");
                    stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
                    byte[] publish = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x32, "KST002 sends QoS 1 PUBLISH while KST001 is rejected");
                    index = 0;
                    string topic = FakeBroker.ReadText(publish, ref index);
                    int packetId = (publish[index++] << 8) | publish[index++];
                    lock (Topics) Topics.Add(topic);
                    stream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
                    SecondPublished.Set();
                }
            }
            catch (Exception error) { if (!stopping) Error = error; }
        }

        internal void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch { }
            lock (sockets) foreach (TcpClient socket in sockets) try { socket.Close(); } catch { }
            lock (sockets) foreach (Thread handler in handlers) handler.Join(3000);
        }
    }

    private sealed class RejectThenPingBroker
    {
        private readonly TcpListener listener;
        private volatile bool stopping;
        private TcpClient activeSocket;
        internal readonly ManualResetEvent Rejected = new ManualResetEvent(false);
        internal readonly ManualResetEvent Published = new ManualResetEvent(false);
        internal readonly ManualResetEvent PingObserved = new ManualResetEvent(false);
        internal long PingDelayMilliseconds;
        internal int PublishCount;
        internal Exception Error;

        internal RejectThenPingBroker(TcpListener listener) { this.listener = listener; }

        internal void Run()
        {
            try
            {
                using (TcpClient first = listener.AcceptTcpClient())
                using (NetworkStream stream = first.GetStream())
                {
                    activeSocket = first;
                    stream.ReadTimeout = 5000;
                    Assert(ReadClientId(stream) == "S&KST001&17&1", "first maintenance CONNECT belongs to KST001");
                    stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x05 }, 0, 4);
                    Rejected.Set();
                }
                using (TcpClient second = listener.AcceptTcpClient())
                using (NetworkStream stream = second.GetStream())
                {
                    activeSocket = second;
                    stream.ReadTimeout = 8000;
                    Assert(ReadClientId(stream) == "S&KST001&17&1", "publishing CONNECT reuses the same source identity");
                    stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
                    byte header;
                    byte[] publish = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x32, "healthy session receives the due state PUBLISH");
                    PublishCount++;
                    int index = 0;
                    FakeBroker.ReadText(publish, ref index);
                    int packetId = (publish[index++] << 8) | publish[index++];
                    stream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
                    DateTime publishAck = DateTime.UtcNow;
                    Published.Set();
                    byte[] ping = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0xC0 && ping.Length == 0, "session sends PINGREQ before its keepalive expires");
                    PingDelayMilliseconds = (long)(DateTime.UtcNow - publishAck).TotalMilliseconds;
                    stream.Write(new byte[] { 0xD0, 0x00 }, 0, 2);
                    PingObserved.Set();
                    while (!stopping) Thread.Sleep(25);
                }
            }
            catch (Exception error) { if (!stopping) Error = error; }
        }

        private static string ReadClientId(NetworkStream stream)
        {
            byte header;
            byte[] connect = FakeBroker.ReadPacket(stream, out header);
            Assert(header == 0x10, "broker receives MQTT CONNECT");
            int index = 0;
            FakeBroker.ReadText(connect, ref index);
            index += 4;
            return FakeBroker.ReadText(connect, ref index);
        }

        internal void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch { }
            try { if (activeSocket != null) activeSocket.Close(); } catch { }
        }
    }

    private sealed class StalledConnackBroker
    {
        private readonly TcpListener listener;
        private volatile bool stopping;
        private TcpClient firstSocket;
        private TcpClient secondSocket;
        private Thread firstHandler;
        internal readonly ManualResetEvent FirstPublished = new ManualResetEvent(false);
        internal readonly ManualResetEvent SecondConnectStalled = new ManualResetEvent(false);
        internal readonly ManualResetEvent PingObserved = new ManualResetEvent(false);
        internal long PingDelayMilliseconds;
        internal Exception Error;

        internal StalledConnackBroker(TcpListener listener) { this.listener = listener; }

        internal void Run()
        {
            try
            {
                firstSocket = listener.AcceptTcpClient();
                firstHandler = new Thread(new ThreadStart(HandleFirst));
                firstHandler.IsBackground = true;
                firstHandler.Start();
                secondSocket = listener.AcceptTcpClient();
                using (NetworkStream stream = secondSocket.GetStream())
                {
                    stream.ReadTimeout = 5000;
                    byte header;
                    byte[] connect = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x10, "second source begins MQTT CONNECT");
                    int index = 0;
                    FakeBroker.ReadText(connect, ref index);
                    index += 4;
                    Assert(FakeBroker.ReadText(connect, ref index) == "S&KST002&17&1", "blackholed CONNACK belongs to KST002");
                    SecondConnectStalled.Set();
                    while (!stopping) Thread.Sleep(25); // Deliberately withhold CONNACK.
                }
            }
            catch (Exception error) { if (!stopping) Error = error; }
        }

        private void HandleFirst()
        {
            try
            {
                using (NetworkStream stream = firstSocket.GetStream())
                {
                    stream.ReadTimeout = 10000;
                    byte header;
                    byte[] connect = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x10, "first source begins MQTT CONNECT");
                    int index = 0;
                    FakeBroker.ReadText(connect, ref index);
                    index += 4;
                    Assert(FakeBroker.ReadText(connect, ref index) == "S&KST001&17&1", "first active source is KST001");
                    stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
                    byte[] publish = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x32, "KST001 sends initial QoS 1 state");
                    index = 0;
                    FakeBroker.ReadText(publish, ref index);
                    int packetId = (publish[index++] << 8) | publish[index++];
                    stream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
                    DateTime firstAck = DateTime.UtcNow;
                    FirstPublished.Set();
                    byte[] ping = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0xC0 && ping.Length == 0, "KST001 sends PINGREQ while KST002 CONNACK stalls");
                    PingDelayMilliseconds = (long)(DateTime.UtcNow - firstAck).TotalMilliseconds;
                    stream.Write(new byte[] { 0xD0, 0x00 }, 0, 2);
                    PingObserved.Set();
                    while (!stopping) Thread.Sleep(25);
                }
            }
            catch (Exception error) { if (!stopping) Error = error; }
        }

        internal void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch { }
            try { if (firstSocket != null) firstSocket.Close(); } catch { }
            try { if (secondSocket != null) secondSocket.Close(); } catch { }
            if (firstHandler != null) firstHandler.Join(3000);
        }
    }

    private sealed class BacklogBroker
    {
        private readonly TcpListener listener;
        private readonly List<TcpClient> sockets = new List<TcpClient>();
        private readonly List<Thread> handlers = new List<Thread>();
        private volatile bool stopping;
        internal readonly ManualResetEvent FirstPublished = new ManualResetEvent(false);
        internal readonly ManualResetEvent PingObserved = new ManualResetEvent(false);
        internal readonly ManualResetEvent AllAcknowledged = new ManualResetEvent(false);
        internal long PingDelayMilliseconds;
        internal Exception Error;

        internal BacklogBroker(TcpListener listener) { this.listener = listener; }

        internal void Run()
        {
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    TcpClient socket = listener.AcceptTcpClient();
                    Thread handler = new Thread(new ParameterizedThreadStart(delegate(object value) { Handle((TcpClient)value); }));
                    handler.IsBackground = true;
                    lock (sockets) { sockets.Add(socket); handlers.Add(handler); }
                    handler.Start(socket);
                }
            }
            catch (Exception error) { if (!stopping) Error = error; }
        }

        private void Handle(TcpClient socket)
        {
            try
            {
                using (socket)
                using (NetworkStream stream = socket.GetStream())
                {
                    stream.ReadTimeout = 10000;
                    byte header;
                    byte[] connect = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x10, "backlog broker receives CONNECT");
                    int index = 0;
                    FakeBroker.ReadText(connect, ref index);
                    index += 4;
                    string clientId = FakeBroker.ReadText(connect, ref index);
                    stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
                    if (clientId == "S&KST001&17&1")
                    {
                        byte[] first = FakeBroker.ReadPacket(stream, out header);
                        Assert(header == 0x32, "first device publishes its state");
                        Acknowledge(stream, first);
                        DateTime firstAck = DateTime.UtcNow;
                        FirstPublished.Set();
                        byte[] ping = FakeBroker.ReadPacket(stream, out header);
                        Assert(header == 0xC0 && ping.Length == 0, "first device pings during second-device backlog");
                        PingDelayMilliseconds = (long)(DateTime.UtcNow - firstAck).TotalMilliseconds;
                        stream.Write(new byte[] { 0xD0, 0x00 }, 0, 2);
                        PingObserved.Set();
                        while (!stopping) Thread.Sleep(25);
                    }
                    else if (clientId == "S&KST002&17&1")
                    {
                        for (int count = 0; count < 16; count++)
                        {
                            byte[] publish = FakeBroker.ReadPacket(stream, out header);
                            Assert(header == 0x32, "second device sends a QoS 1 alarm");
                            Thread.Sleep(350);
                            Acknowledge(stream, publish);
                        }
                        AllAcknowledged.Set();
                        while (!stopping) Thread.Sleep(25);
                    }
                    else Assert(false, "backlog broker receives only configured MQTT client IDs");
                }
            }
            catch (Exception error) { if (!stopping) Error = error; }
        }

        private static void Acknowledge(NetworkStream stream, byte[] publish)
        {
            int index = 0;
            FakeBroker.ReadText(publish, ref index);
            int packetId = (publish[index++] << 8) | publish[index++];
            stream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
        }

        internal void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch { }
            lock (sockets) foreach (TcpClient socket in sockets) try { socket.Close(); } catch { }
            lock (sockets) foreach (Thread handler in handlers) handler.Join(3000);
        }
    }

    private sealed class KeepAliveBroker
    {
        private readonly TcpListener listener;
        internal readonly ManualResetEvent FirstPublished = new ManualResetEvent(false);
        internal readonly ManualResetEvent PingObserved = new ManualResetEvent(false);
        internal readonly ManualResetEvent SecondPublished = new ManualResetEvent(false);
        internal Exception Error;

        internal KeepAliveBroker(TcpListener listener) { this.listener = listener; }

        internal void Run()
        {
            try
            {
                using (TcpClient socket = listener.AcceptTcpClient())
                using (NetworkStream stream = socket.GetStream())
                {
                    stream.ReadTimeout = 7000;
                    byte header;
                    FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x10, "broker received CONNECT");
                    stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
                    byte[] first = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x32, "broker received first QoS 1 PUBLISH");
                    AcknowledgePublish(stream, first);
                    FirstPublished.Set();

                    byte[] ping = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0xC0 && ping.Length == 0, "broker received PINGREQ on the original connection");
                    stream.Write(new byte[] { 0xD0, 0x00 }, 0, 2);
                    PingObserved.Set();

                    byte[] second = FakeBroker.ReadPacket(stream, out header);
                    Assert(header == 0x32, "broker received second PUBLISH on the original connection");
                    AcknowledgePublish(stream, second);
                    SecondPublished.Set();
                }
            }
            catch (Exception error) { Error = error; }
        }

        private static void AcknowledgePublish(Stream stream, byte[] publish)
        {
            int index = 0;
            FakeBroker.ReadText(publish, ref index);
            int packetId = (publish[index++] << 8) | publish[index++];
            stream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
        }
    }

    private sealed class DropPingBroker
    {
        private readonly TcpListener listener;
        internal readonly ManualResetEvent FirstPublished = new ManualResetEvent(false);
        internal readonly ManualResetEvent PingDropped = new ManualResetEvent(false);
        internal readonly ManualResetEvent Reconnected = new ManualResetEvent(false);
        internal Exception Error;

        internal DropPingBroker(TcpListener listener) { this.listener = listener; }

        internal void Run()
        {
            try
            {
                string firstClientId;
                using (TcpClient firstSocket = listener.AcceptTcpClient())
                using (NetworkStream firstStream = firstSocket.GetStream())
                {
                    firstStream.ReadTimeout = 7000;
                    byte header;
                    byte[] connect = FakeBroker.ReadPacket(firstStream, out header);
                    Assert(header == 0x10, "broker received first CONNECT");
                    int connectIndex = 0;
                    FakeBroker.ReadText(connect, ref connectIndex);
                    connectIndex += 4;
                    firstClientId = FakeBroker.ReadText(connect, ref connectIndex);
                    firstStream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
                    byte[] publish = FakeBroker.ReadPacket(firstStream, out header);
                    Assert(header == 0x32, "broker received the only business PUBLISH");
                    int index = 0;
                    FakeBroker.ReadText(publish, ref index);
                    int packetId = (publish[index++] << 8) | publish[index++];
                    firstStream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
                    FirstPublished.Set();
                    byte[] ping = FakeBroker.ReadPacket(firstStream, out header);
                    Assert(header == 0xC0 && ping.Length == 0, "broker received idle PINGREQ");
                }
                PingDropped.Set();

                using (TcpClient secondSocket = listener.AcceptTcpClient())
                using (NetworkStream secondStream = secondSocket.GetStream())
                {
                    secondStream.ReadTimeout = 3000;
                    byte header;
                    byte[] reconnect = FakeBroker.ReadPacket(secondStream, out header);
                    Assert(header == 0x10, "broker received reconnect for the same device");
                    int connectIndex = 0;
                    FakeBroker.ReadText(reconnect, ref connectIndex);
                    connectIndex += 4;
                    Assert(FakeBroker.ReadText(reconnect, ref connectIndex) == firstClientId,
                        "reconnected MQTT client ID matches the first connection");
                    secondStream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
                    Reconnected.Set();
                }
            }
            catch (Exception error) { Error = error; }
        }
    }

    private sealed class HeldAckBroker
    {
        private readonly TcpListener listener;
        internal readonly ManualResetEvent FirstPublished = new ManualResetEvent(false);
        internal readonly ManualResetEvent ReleaseFirstAck = new ManualResetEvent(false);
        internal readonly ManualResetEvent SecondPublished = new ManualResetEvent(false);
        internal readonly List<string> Payloads = new List<string>();
        internal Exception Error;

        internal HeldAckBroker(TcpListener listener) { this.listener = listener; }

        internal void Run()
        {
            try
            {
                using (TcpClient socket = listener.AcceptTcpClient())
                using (NetworkStream stream = socket.GetStream())
                {
                    stream.ReadTimeout = 7000;
                    byte header;
                    FakeBroker.ReadPacket(stream, out header);
                    stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
                    for (int count = 0; count < 2; count++)
                    {
                        byte[] publish = FakeBroker.ReadPacket(stream, out header);
                        Assert((header & 0xF0) == 0x30, "broker received a publish packet");
                        int index = 0;
                        FakeBroker.ReadText(publish, ref index);
                        int packetId = (publish[index++] << 8) | publish[index++];
                        Payloads.Add(Encoding.UTF8.GetString(publish, index, publish.Length - index));
                        if (count == 0)
                        {
                            FirstPublished.Set();
                            if (!ReleaseFirstAck.WaitOne(5000)) throw new TimeoutException("test did not release first PUBACK");
                        }
                        stream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
                        if (count == 1) SecondPublished.Set();
                    }
                }
            }
            catch (Exception error)
            {
                Error = error;
                FirstPublished.Set();
                SecondPublished.Set();
            }
        }
    }

    private sealed class FakeBroker
    {
        private readonly TcpListener listener;
        internal readonly ManualResetEvent Received = new ManualResetEvent(false);
        internal readonly ManualResetEvent FirstPublishAcknowledged = new ManualResetEvent(false);
        internal readonly ManualResetEvent FirstConnectionRejected = new ManualResetEvent(false);
        internal string ClientId;
        internal string Topic;
        internal string Payload;
        internal readonly List<string> ClientIds = new List<string>();
        internal readonly List<string> Topics = new List<string>();
        internal Exception Error;
        private readonly int expectedCount;
        private readonly bool sendPuback;
        private readonly bool rejectFirstConnect;

        internal FakeBroker(TcpListener listener) : this(listener, 1) { }
        internal FakeBroker(TcpListener listener, int expectedCount) : this(listener, expectedCount, true) { }
        internal FakeBroker(TcpListener listener, int expectedCount, bool sendPuback)
            : this(listener, expectedCount, sendPuback, false) { }
        internal FakeBroker(TcpListener listener, int expectedCount, bool sendPuback, bool rejectFirstConnect)
        {
            this.listener = listener;
            this.expectedCount = expectedCount;
            this.sendPuback = sendPuback;
            this.rejectFirstConnect = rejectFirstConnect;
        }

        internal void Run()
        {
            try
            {
                for (int connection = 0; connection < expectedCount; connection++)
                {
                    using (TcpClient socket = listener.AcceptTcpClient())
                    using (NetworkStream stream = socket.GetStream())
                    {
                        byte header;
                        byte[] connect = ReadPacket(stream, out header);
                        int index = 0;
                        ReadText(connect, ref index);
                        index += 4;
                        ClientId = ReadText(connect, ref index);
                        ClientIds.Add(ClientId);
                        if (rejectFirstConnect && ClientId.Contains("&KST001&") && !FirstConnectionRejected.WaitOne(0))
                        {
                            stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x05 }, 0, 4);
                            FirstConnectionRejected.Set();
                            continue;
                        }
                        stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);
                        byte[] publish = ReadPacket(stream, out header);
                        index = 0;
                        Topic = ReadText(publish, ref index);
                        Topics.Add(Topic);
                        int packetId = (publish[index++] << 8) | publish[index++];
                        Payload = Encoding.UTF8.GetString(publish, index, publish.Length - index);
                        if (sendPuback) stream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
                        if (connection == 0 && sendPuback) FirstPublishAcknowledged.Set();
                    }
                }
                Received.Set();
            }
            catch (Exception error) { Error = error; Received.Set(); }
        }

        internal static byte[] ReadPacket(Stream stream, out byte header)
        {
            int first = stream.ReadByte();
            if (first < 0) throw new EndOfStreamException();
            header = (byte)first;
            int length = 0, multiplier = 1, digit;
            do
            {
                digit = stream.ReadByte();
                if (digit < 0) throw new EndOfStreamException();
                length += (digit & 127) * multiplier;
                multiplier *= 128;
            } while ((digit & 128) != 0);
            byte[] data = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int count = stream.Read(data, offset, length - offset);
                if (count <= 0) throw new EndOfStreamException();
                offset += count;
            }
            return data;
        }

        internal static string ReadText(byte[] data, ref int index)
        {
            int length = (data[index++] << 8) | data[index++];
            string value = Encoding.UTF8.GetString(data, index, length);
            index += length;
            return value;
        }
    }
}
