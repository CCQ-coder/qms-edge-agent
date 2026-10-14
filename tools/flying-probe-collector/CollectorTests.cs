using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class CollectorTests
{
    private static int passed;

    private static int Main()
    {
        try
        {
            Run("ParseSample", ParseSample);
            Run("RejectMalformed", RejectMalformed);
            Run("MapEvent", MapEvent);
            Run("RecoverPending", RecoverPending);
            Run("RecoverCheckpoint", RecoverCheckpoint);
            Run("RecoverLargeStores", RecoverLargeStores);
            Run("RejectCorruptStores", RejectCorruptStores);
            Run("FailedWriteKeepsMemory", FailedWriteKeepsMemory);
            Run("FirstScanWaitsForStability", FirstScanWaitsForStability);
            Run("UpdatesCreateOneNewEvent", UpdatesCreateOneNewEvent);
            Run("IgnoreNestedAndNonTxt", IgnoreNestedAndNonTxt);
            Run("RetryInvalidOrChangingFile", RetryInvalidOrChangingFile);
            Run("MetadataChangedAfterRead", MetadataChangedAfterRead);
            Run("MissingRootKeepsState", MissingRootKeepsState);
            Run("AcceptedPostRemovesEvent", AcceptedPostRemovesEvent);
            Run("RejectOneContinueNext", RejectOneContinueNext);
            Run("NetworkFailureRetains", NetworkFailureRetains);
            Run("RedirectRetainsEvent", RedirectRetainsEvent);
            Run("NoBearerWhenEmpty", NoBearerWhenEmpty);
            Run("LoadSettingsRelativeToIni", LoadSettingsRelativeToIni);
            Run("BuildReleaseLayout", BuildReleaseLayout);
            Run("SecondLaunchFailsClearly", SecondLaunchFailsClearly);
            Run("ScanFailureStillAttemptsDelivery", ScanFailureStillAttemptsDelivery);
            Console.WriteLine("Passed " + passed.ToString(CultureInfo.InvariantCulture) + " test(s).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void ParseSample()
    {
        const string sample = "Pad_Net_Count 63 40\r\n" +
            "Panel_Layout 1 1 1 0 0\r\n" +
            "Test_Counters 2 2 0 01:27:09\r\n" +
            "Want_Ext_Resist_Test 0 30.00 5.00\r\n";
        ProbeCounters result = ProbeRecords.Parse(sample);
        Assert(result.Tested == 2, "tested boards");
        Assert(result.Good == 2, "good boards");
        Assert(result.Bad == 0, "bad boards");
        Assert(result.LastTestTime == "01:27:09", "last test time string");
        ProbeCounters inconsistent = ProbeRecords.Parse("Test_Counters 3 1 0 25:00:00");
        Assert(inconsistent.Tested == 3 && inconsistent.LastTestTime == "25:00:00", "no inferred equality or clock time");
    }

    private static void RejectMalformed()
    {
        string[] invalid = {
            "Panel_Layout 1 1 1 0 0",
            "Test_Counters 2 2 0",
            "Test_Counters 2 2 0 01:27:09 extra",
            "Test_Counters -1 2 0 01:27:09",
            "Test_Counters 2147483648 2 0 01:27:09",
            "Test_Counters 2 2 0 01:60:09",
            "Test_Counters 2 2 0 01:27:60",
            "Test_Counters_extra 2 2 0 01:27:09"
        };
        foreach (string value in invalid)
        {
            AssertThrowsFormat(delegate { ProbeRecords.Parse(value); }, value);
        }
    }

    private static void MapEvent()
    {
        DateTime created = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Local);
        DateTime modified = new DateTime(2026, 10, 14, 9, 10, 11, DateTimeKind.Local);
        ProbeCounters counters = ProbeRecords.Parse("Test_Counters 2 2 0 01:27:09");
        Dictionary<string, object> eventItem = ProbeRecords.CreateEvent("FPT-01", "MAT-1", "FPT-01/MAT-1.txt", created, modified, counters);
        Dictionary<string, object> attributes = (Dictionary<string, object>)eventItem["attributes"];
        Assert(Text(eventItem, "machineName") == "FPT-01", "machine name");
        Assert(Text(eventItem, "materialNo") == "MAT-1", "material number");
        Assert(Text(eventItem, "deviceTime") == "2026-10-14T09:10:11", "device time");
        Assert(Text(eventItem, "eventType") == "FLYING_PROBE_TEST_COUNTERS", "event type");
        Assert((int)eventItem["machiningCount"] == 2, "machining count");
        Assert(Text(attributes, "sourceProtocol") == "FLYING_PROBE_TXT_V1", "source protocol");
        Assert(Text(attributes, "startTime") == "2026-10-14T08:00:00", "start time");
        Assert(Text(attributes, "updatedAt") == "2026-10-14T09:10:11", "updated time");
        Assert((int)attributes["testedBoardCount"] == 2, "attribute tested");
        Assert((int)attributes["goodBoardCount"] == 2, "attribute good");
        Assert((int)attributes["badBoardCount"] == 0, "attribute bad");
        Assert(Text(attributes, "lastTestTime") == "01:27:09", "attribute last test time");
        Assert(Text(attributes, "sourceFile") == "FPT-01/MAT-1.txt", "relative source path");
        string recordNo = Text(eventItem, "recordNo");
        Assert(recordNo != null && recordNo.StartsWith("FPT-", StringComparison.Ordinal) && recordNo.Length <= 160, "record number shape");
        Assert(recordNo == Text(ProbeRecords.CreateEvent("FPT-01", "MAT-1", "FPT-01/MAT-1.txt", created, modified, counters), "recordNo"), "stable record number");
        Assert(recordNo != Text(ProbeRecords.CreateEvent("FPT-01", "MAT-1", "FPT-01/MAT-1.txt", created, modified.AddTicks(1), counters), "recordNo"), "timestamp changes record number");
        Assert(recordNo != Text(ProbeRecords.CreateEvent("FPT-01", "MAT-1", "FPT-01/MAT-1.txt", created, modified, ProbeRecords.Parse("Test_Counters 3 2 1 01:27:09")), "recordNo"), "counter changes record number");
    }

    private static string Text(Dictionary<string, object> value, string key)
    {
        object item;
        return value.TryGetValue(key, out item) ? item as string : null;
    }

    private static void RecoverPending()
    {
        InTempDirectory(delegate(string directory)
        {
            string path = Path.Combine(directory, "pending.json");
            PendingEventStore store = new PendingEventStore(path);
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["recordNo"] = "R-1";
            item["machineName"] = "FPT-01";
            Assert(store.Enqueue(item), "first enqueue");
            Assert(!store.Enqueue(item), "duplicate not enqueued");
            PendingEventStore recovered = new PendingEventStore(path);
            Assert(recovered.Snapshot().Count == 1, "pending survives restart");
            Assert(Text(recovered.Snapshot()[0], "machineName") == "FPT-01", "pending payload survives restart");
            recovered.Remove("R-1");
            Assert(new PendingEventStore(path).Snapshot().Count == 0, "removal survives restart");
        });
    }

    private static void RecoverCheckpoint()
    {
        InTempDirectory(delegate(string directory)
        {
            string path = Path.Combine(directory, "scan.json");
            ScanCheckpointStore store = new ScanCheckpointStore(path);
            store.Record("FPT-01/MAT-1.txt", "R-1");
            ScanCheckpointStore recovered = new ScanCheckpointStore(path);
            Assert(recovered.Contains("FPT-01/MAT-1.txt", "R-1"), "checkpoint survives restart");
            Assert(!recovered.Contains("FPT-01/MAT-1.txt", "R-2"), "new snapshot not checkpointed");
        });
    }

    private static void RecoverLargeStores()
    {
        InTempDirectory(delegate(string directory)
        {
            string queuePath = Path.Combine(directory, "pending.json");
            string statePath = Path.Combine(directory, "scan.json");
            string longValue = new string('X', 2100000);
            PendingEventStore queue = new PendingEventStore(queuePath);
            Dictionary<string, object> item = EventWithRecord("R-large");
            item["largeAttribute"] = longValue;
            queue.Enqueue(item);
            Assert(new PendingEventStore(queuePath).Snapshot().Count == 1, "large queue survives restart");
            ScanCheckpointStore state = new ScanCheckpointStore(statePath);
            state.Record(longValue, "R-large");
            Assert(new ScanCheckpointStore(statePath).Contains(longValue, "R-large"), "large checkpoint survives restart");
        });
    }


    private static void RejectCorruptStores()
    {
        InTempDirectory(delegate(string directory)
        {
            string queuePath = Path.Combine(directory, "pending.json");
            string scanPath = Path.Combine(directory, "scan.json");
            File.WriteAllText(queuePath, "{not json");
            File.WriteAllText(scanPath, "{not json");
            AssertThrowsInvalidData(delegate { new PendingEventStore(queuePath); }, "corrupt pending queue");
            AssertThrowsInvalidData(delegate { new ScanCheckpointStore(scanPath); }, "corrupt scan state");
            Assert(File.ReadAllText(queuePath) == "{not json", "pending queue not erased");
            Assert(File.ReadAllText(scanPath) == "{not json", "scan state not erased");
        });
    }

    private static void FailedWriteKeepsMemory()
    {
        InTempDirectory(delegate(string directory)
        {
            string queuePath = Path.Combine(directory, "queue-as-directory");
            string scanPath = Path.Combine(directory, "scan-as-directory");
            Directory.CreateDirectory(queuePath);
            Directory.CreateDirectory(scanPath);
            PendingEventStore queue = new PendingEventStore(queuePath);
            ScanCheckpointStore checkpoints = new ScanCheckpointStore(scanPath);
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["recordNo"] = "R-1";
            AssertThrowsAny(delegate { queue.Enqueue(item); }, "queue write failure");
            AssertThrowsAny(delegate { checkpoints.Record("FPT-01/MAT-1.txt", "R-1"); }, "checkpoint write failure");
            Assert(queue.Snapshot().Count == 0, "failed enqueue leaves memory unchanged");
            Assert(!checkpoints.Contains("FPT-01/MAT-1.txt", "R-1"), "failed checkpoint leaves memory unchanged");
        });
    }

    private static void FirstScanWaitsForStability()
    {
        InTempDirectory(delegate(string directory)
        {
            string root = Path.Combine(directory, "source");
            string device = Path.Combine(root, "FPT-01");
            Directory.CreateDirectory(device);
            string file = Path.Combine(device, "MAT-1.txt");
            File.WriteAllText(file, "Test_Counters 2 2 0 01:27:09");
            DateTime created = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Local);
            DateTime updated = new DateTime(2026, 10, 14, 9, 10, 11, DateTimeKind.Local);
            File.SetCreationTime(file, created);
            File.SetLastWriteTime(file, updated);
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            ScanCheckpointStore state = new ScanCheckpointStore(Path.Combine(directory, "scan.json"));
            ProbeScanner scanner = new ProbeScanner(root, queue, state, delegate(string message) { });
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 0, "first observation is not queued");
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 1, "stable file queued once");
            Dictionary<string, object> item = queue.Snapshot()[0];
            Assert(Text(item, "machineName") == "FPT-01", "directory maps to machine");
            Assert(Text(item, "materialNo") == "MAT-1", "stem maps to material");
            Assert(Text(item, "deviceTime") == "2026-10-14T09:10:11", "modification time maps to device time");
            Dictionary<string, object> attributes = (Dictionary<string, object>)item["attributes"];
            Assert(Text(attributes, "startTime") == "2026-10-14T08:00:00", "creation time maps to start time");
            Assert(state.Contains("FPT-01/MAT-1.txt", Text(item, "recordNo")), "checkpoint after queue");
        });
    }

    private static void UpdatesCreateOneNewEvent()
    {
        InTempDirectory(delegate(string directory)
        {
            string root = Path.Combine(directory, "source");
            string device = Path.Combine(root, "FPT-01");
            Directory.CreateDirectory(device);
            string file = Path.Combine(device, "MAT-1.txt");
            File.WriteAllText(file, "Test_Counters 2 2 0 01:27:09");
            File.SetLastWriteTime(file, new DateTime(2026, 10, 14, 9, 10, 11));
            string queuePath = Path.Combine(directory, "pending.json");
            string statePath = Path.Combine(directory, "scan.json");
            PendingEventStore queue = new PendingEventStore(queuePath);
            ProbeScanner scanner = new ProbeScanner(root, queue, new ScanCheckpointStore(statePath), delegate(string message) { });
            scanner.ScanOnce();
            scanner.ScanOnce();
            string firstRecord = Text(queue.Snapshot()[0], "recordNo");
            File.WriteAllText(file, "Test_Counters 3 2 1 01:28:00");
            File.SetLastWriteTime(file, new DateTime(2026, 10, 14, 9, 12, 0));
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 1, "changed file must stabilize");
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 2, "updated snapshot queued once");
            Assert(firstRecord != Text(queue.Snapshot()[1], "recordNo"), "updated snapshot has new record number");
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 2, "unchanged file not requeued");
            ProbeScanner restarted = new ProbeScanner(root, new PendingEventStore(queuePath), new ScanCheckpointStore(statePath), delegate(string message) { });
            restarted.ScanOnce();
            restarted.ScanOnce();
            Assert(new PendingEventStore(queuePath).Snapshot().Count == 2, "restart does not requeue current snapshot");
        });
    }

    private static void IgnoreNestedAndNonTxt()
    {
        InTempDirectory(delegate(string directory)
        {
            string root = Path.Combine(directory, "source");
            string device = Path.Combine(root, "FPT-01");
            string nested = Path.Combine(device, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(root, "root.txt"), "Test_Counters 9 9 0 01:00:00");
            File.WriteAllText(Path.Combine(device, "notes.csv"), "Test_Counters 9 9 0 01:00:00");
            File.WriteAllText(Path.Combine(nested, "nested.txt"), "Test_Counters 9 9 0 01:00:00");
            File.WriteAllText(Path.Combine(device, "MAT-1.TXT"), "Test_Counters 2 2 0 01:27:09");
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            ProbeScanner scanner = new ProbeScanner(root, queue, new ScanCheckpointStore(Path.Combine(directory, "scan.json")), delegate(string message) { });
            scanner.ScanOnce();
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 1, "only immediate TXT under a device directory");
            Assert(Text(queue.Snapshot()[0], "materialNo") == "MAT-1", "uppercase TXT extension accepted");
        });
    }

    private static void RetryInvalidOrChangingFile()
    {
        InTempDirectory(delegate(string directory)
        {
            string root = Path.Combine(directory, "source");
            string device = Path.Combine(root, "FPT-01");
            Directory.CreateDirectory(device);
            string file = Path.Combine(device, "MAT-1.txt");
            File.WriteAllText(file, "Test_Counters 2 2 0");
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            ProbeScanner scanner = new ProbeScanner(root, queue, new ScanCheckpointStore(Path.Combine(directory, "scan.json")), delegate(string message) { });
            scanner.ScanOnce();
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 0, "invalid file not queued");
            File.WriteAllText(file, "Test_Counters 2 2 0 01:27:09");
            File.SetLastWriteTime(file, DateTime.Now.AddMinutes(2));
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 0, "change between scans not queued");
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 1, "corrected stable file retried");
        });
    }

    private static void MetadataChangedAfterRead()
    {
        InTempDirectory(delegate(string directory)
        {
            string file = Path.Combine(directory, "MAT-1.txt");
            File.WriteAllText(file, "Test_Counters 2 2 0 01:27:09");
            FileInfo info = new FileInfo(file);
            long oldLength = info.Length;
            DateTime oldWriteUtc = info.LastWriteTimeUtc;
            File.AppendAllText(file, "\r\nWant_Ext_Resist_Test 0 30.00 5.00");
            Assert(!ProbeScanner.MetadataUnchanged(info, oldLength, oldWriteUtc), "post-read metadata change detected");
        });
    }

    private static void MissingRootKeepsState()
    {
        InTempDirectory(delegate(string directory)
        {
            string root = Path.Combine(directory, "missing-source");
            string queuePath = Path.Combine(directory, "pending.json");
            string statePath = Path.Combine(directory, "scan.json");
            PendingEventStore queue = new PendingEventStore(queuePath);
            Dictionary<string, object> old = new Dictionary<string, object>();
            old["recordNo"] = "R-old";
            queue.Enqueue(old);
            ScanCheckpointStore state = new ScanCheckpointStore(statePath);
            state.Record("FPT-old/OLD.txt", "R-old");
            ProbeScanner scanner = new ProbeScanner(root, queue, state, delegate(string message) { });
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 1 && state.Contains("FPT-old/OLD.txt", "R-old"), "missing share preserves state");
            string device = Path.Combine(root, "FPT-01");
            Directory.CreateDirectory(device);
            File.WriteAllText(Path.Combine(device, "MAT-1.txt"), "Test_Counters 2 2 0 01:27:09");
            scanner.ScanOnce();
            scanner.ScanOnce();
            Assert(queue.Snapshot().Count == 2, "scanning resumes after source returns");
        });
    }

    private static void AcceptedPostRemovesEvent()
    {
        InTempDirectory(delegate(string directory)
        {
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            queue.Enqueue(ProbeRecords.CreateEvent("FPT-01", "MAT-1", "FPT-01/MAT-1.txt",
                new DateTime(2026, 10, 14, 8, 0, 0), new DateTime(2026, 10, 14, 9, 10, 11),
                ProbeRecords.Parse("Test_Counters 2 2 0 01:27:09")));
            using (HttpFixture server = new HttpFixture(204))
            {
                List<string> messages = new List<string>();
                new QmsSender(server.BaseUrl, "gateway-secret", "bearer-secret", 2, queue, messages.Add).SendPending();
                if (queue.Snapshot().Count != 0) throw new Exception("QMS sender did not clear accepted event: " + string.Join(" | ", messages.ToArray()));
                server.WaitForRequests();
                Assert(queue.Snapshot().Count == 0, "HTTP 204 removes event");
                CapturedRequest request = server.Requests[0];
                Assert(request.FirstLine == "POST /api/v1/equipment/telemetry HTTP/1.1", "telemetry endpoint and method");
                Assert(request.Headers["X-Gateway-Token"] == "gateway-secret", "gateway token header");
                Assert(request.Headers["Authorization"] == "Bearer bearer-secret", "bearer header");
                Dictionary<string, object> payload = new JavaScriptSerializer().DeserializeObject(request.Body) as Dictionary<string, object>;
                IList events = payload == null ? null : payload["events"] as IList;
                Assert(events != null && events.Count == 1, "one event in wrapper");
                Dictionary<string, object> item = events[0] as Dictionary<string, object>;
                Assert(item != null && Text(item, "machineName") == "FPT-01" && (int)item["machiningCount"] == 2, "QMS event payload");
            }
        });
    }

    private static void RejectOneContinueNext()
    {
        InTempDirectory(delegate(string directory)
        {
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            queue.Enqueue(EventWithRecord("R-1"));
            queue.Enqueue(EventWithRecord("R-2"));
            using (HttpFixture server = new HttpFixture(400, 200))
            {
                new QmsSender(server.BaseUrl, "gateway-secret", "", 2, queue, delegate(string message) { }).SendPending();
                server.WaitForRequests();
                Assert(server.Requests.Count == 2, "second event sent after first rejected");
                Assert(queue.Snapshot().Count == 1 && Text(queue.Snapshot()[0], "recordNo") == "R-1", "only rejected event retained");
            }
        });
    }

    private static void NetworkFailureRetains()
    {
        InTempDirectory(delegate(string directory)
        {
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            queue.Enqueue(EventWithRecord("R-1"));
            List<string> messages = new List<string>();
            new QmsSender("http://127.0.0.1:1", "gateway-secret", "bearer-secret", 1, queue, messages.Add).SendPending();
            Assert(queue.Snapshot().Count == 1, "unreachable QMS retains event");
            Assert(messages.Count > 0 && !messages[0].Contains("gateway-secret") && !messages[0].Contains("bearer-secret"), "failure log hides tokens");
        });
    }

    private static void RedirectRetainsEvent()
    {
        InTempDirectory(delegate(string directory)
        {
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            queue.Enqueue(EventWithRecord("R-redirect"));
            using (HttpFixture login = new HttpFixture(200))
            using (HttpFixture endpoint = new HttpFixture(302))
            {
                endpoint.RedirectLocation = login.BaseUrl + "/login";
                new QmsSender(endpoint.BaseUrl, "gateway-secret", "", 2, queue, delegate(string message) { }).SendPending();
                endpoint.WaitForRequests();
                Assert(queue.Snapshot().Count == 1, "redirected POST remains pending");
                Assert(login.Requests.Count == 0, "collector does not follow redirect to login page");
            }
        });
    }

    private static void NoBearerWhenEmpty()
    {
        InTempDirectory(delegate(string directory)
        {
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            queue.Enqueue(EventWithRecord("R-1"));
            using (HttpFixture server = new HttpFixture(200))
            {
                new QmsSender(server.BaseUrl, "gateway-secret", "", 2, queue, delegate(string message) { }).SendPending();
                server.WaitForRequests();
                Assert(!server.Requests[0].Headers.ContainsKey("Authorization"), "blank bearer omits Authorization");
            }
        });
    }

    private static Dictionary<string, object> EventWithRecord(string recordNo)
    {
        Dictionary<string, object> item = new Dictionary<string, object>();
        item["recordNo"] = recordNo;
        item["machineName"] = "FPT-01";
        item["deviceTime"] = "2026-10-14T09:10:11";
        item["eventType"] = "FLYING_PROBE_TEST_COUNTERS";
        return item;
    }

    private static void LoadSettingsRelativeToIni()
    {
        InTempDirectory(delegate(string directory)
        {
            string configDir = Path.Combine(directory, "config");
            string otherDir = Path.Combine(directory, "other");
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(otherDir);
            string config = Path.Combine(configDir, "FlyingProbeCollector.ini");
            string valid = "ScanIntervalSeconds=15\r\nQmsBaseUrl=http://127.0.0.1:8080\r\n" +
                "GatewayToken=test-token\r\nBearerToken=\r\nRequestTimeoutSeconds=5\r\n" +
                "QueueFile=.\\state\\pending.json\r\nCheckpointFile=.\\state\\scan.json\r\nLogFile=.\\logs\\collector.log\r\n";
            File.WriteAllText(config, valid);
            string prior = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = otherDir;
                Settings settings = Settings.Load(config);
                Assert(settings.SourceRoot == @"\\Scbgx\数字化\飞针机", "default UNC source root");
                Assert(settings.ScanIntervalSeconds == 15 && settings.RequestTimeoutSeconds == 5, "interval and timeout settings");
                Assert(settings.QueueFile == Path.GetFullPath(Path.Combine(configDir, "state", "pending.json")), "queue path relative to INI");
                Assert(settings.CheckpointFile == Path.GetFullPath(Path.Combine(configDir, "state", "scan.json")), "checkpoint path relative to INI");
                Assert(settings.LogFile == Path.GetFullPath(Path.Combine(configDir, "logs", "collector.log")), "log path relative to INI");
                File.WriteAllText(config, valid.Replace("GatewayToken=test-token", "GatewayToken=REPLACE_WITH_QMS_GATEWAY_TOKEN"));
                AssertThrowsArgument(delegate { Settings.Load(config); }, "placeholder gateway token rejected");
                File.WriteAllText(config, valid.Replace("QmsBaseUrl=http://127.0.0.1:8080", "QmsBaseUrl="));
                AssertThrowsArgument(delegate { Settings.Load(config); }, "missing QMS URL rejected");
            }
            finally { Environment.CurrentDirectory = prior; }
        });
    }

    private static void BuildReleaseLayout()
    {
        InTempDirectory(delegate(string directory)
        {
            string source = Environment.GetEnvironmentVariable("FPT_TOOL_DIR");
            Assert(!string.IsNullOrEmpty(source), "test runner provides tool source path");
            string target = Path.Combine(directory, "repo", "tools", "flying-probe-collector");
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
            string build = Path.Combine(target, "build.cmd");
            string release = Path.Combine(directory, "repo", "outputs", "flying-probe-collector");
            RunBuild(build, directory);
            Assert(File.Exists(Path.Combine(release, "FlyingProbeCollector.exe")), "independent EXE released");
            Assert(File.Exists(Path.Combine(release, "FlyingProbeCollector.ini")), "default INI released");
            Assert(File.Exists(Path.Combine(release, "start-collector.cmd")), "launcher released");
            Assert(File.Exists(Path.Combine(release, "README.md")), "operating guide released");
            string siteConfig = Path.Combine(release, "FlyingProbeCollector.ini");
            File.WriteAllText(siteConfig, "QmsBaseUrl=site-value\r\n");
            RunBuild(build, directory);
            Assert(File.ReadAllText(siteConfig) == "QmsBaseUrl=site-value\r\n", "rebuild preserves site INI");
        });
    }

    private static void SecondLaunchFailsClearly()
    {
        InTempDirectory(delegate(string directory)
        {
            string source = Environment.GetEnvironmentVariable("FPT_TOOL_DIR");
            string target = Path.Combine(directory, "repo", "tools", "flying-probe-collector");
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            RunBuild(Path.Combine(target, "build.cmd"), directory);
            string release = Path.Combine(directory, "repo", "outputs", "flying-probe-collector");
            string input = Path.Combine(directory, "source");
            Directory.CreateDirectory(input);
            File.WriteAllText(Path.Combine(release, "FlyingProbeCollector.ini"),
                "SourceRoot=" + input + "\r\nScanIntervalSeconds=1\r\nQmsBaseUrl=http://127.0.0.1:1\r\n" +
                "GatewayToken=test-token\r\nQueueFile=.\\data\\pending.json\r\n" +
                "CheckpointFile=.\\data\\scan.json\r\nLogFile=.\\logs\\collector.log\r\n");
            string exe = Path.Combine(release, "FlyingProbeCollector.exe");
            ProcessStartInfo start = new ProcessStartInfo(exe);
            start.WorkingDirectory = release;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using (Process first = Process.Start(start))
            {
                try
                {
                    string log = Path.Combine(release, "logs", "collector.log");
                    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!File.Exists(log) && DateTime.UtcNow < deadline) Thread.Sleep(50);
                    Assert(File.Exists(log) && !first.HasExited, "first collector owns storage");
                    ProcessStartInfo secondStart = new ProcessStartInfo(exe);
                    secondStart.WorkingDirectory = release;
                    secondStart.UseShellExecute = false;
                    secondStart.CreateNoWindow = true;
                    secondStart.RedirectStandardError = true;
                    using (Process second = Process.Start(secondStart))
                    {
                        try
                        {
                            Assert(second.WaitForExit(1500), "second collector exits promptly");
                            Assert(second.ExitCode != 0, "second collector fails");
                            Assert(second.StandardError.ReadToEnd().Contains("already in use"), "second collector explains state ownership");
                        }
                        finally { if (!second.HasExited) { second.Kill(); second.WaitForExit(5000); } }
                    }
                }
                finally { if (!first.HasExited) { first.Kill(); first.WaitForExit(5000); } }
            }
        });
    }

    private static void ScanFailureStillAttemptsDelivery()
    {
        MethodInfo cycle = typeof(Program).GetMethod("RunCycle", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(cycle != null, "collector exposes a testable cycle boundary");
        int attempts = 0;
        List<string> messages = new List<string>();
        Action scan = delegate { throw new IOException("sample scan failure"); };
        Action send = delegate { attempts++; };
        cycle.Invoke(null, new object[] { scan, send, (Action<string>)messages.Add });
        Assert(attempts == 1, "pending delivery runs after scan failure");
        Assert(messages.Count == 1 && messages[0].Contains("sample scan failure"), "scan failure logged");
    }

    private static void RunBuild(string build, string workingDirectory)
    {
        ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/c \"" + build + "\"");
        start.WorkingDirectory = workingDirectory;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using (Process process = Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            Assert(process.WaitForExit(30000), "build completes in 30 seconds");
            Assert(process.ExitCode == 0, "build exit 0: " + output + error);
        }
    }

    private static void AssertThrowsArgument(Action action, string description)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Expected ArgumentException: " + description);
    }

    private sealed class CapturedRequest
    {
        public string FirstLine;
        public Dictionary<string, string> Headers;
        public string Body;
    }

    private sealed class HttpFixture : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread thread;
        private readonly int[] statuses;
        private readonly List<CapturedRequest> requests = new List<CapturedRequest>();
        private Exception failure;
        public string BaseUrl { get; private set; }
        public string RedirectLocation { get; set; }
        public List<CapturedRequest> Requests { get { return requests; } }

        public HttpFixture(params int[] statuses)
        {
            this.statuses = statuses;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Start();
        }

        public void WaitForRequests()
        {
            if (!thread.Join(5000)) throw new Exception("HTTP fixture timed out; captured=" + requests.Count.ToString(CultureInfo.InvariantCulture) +
                "; failure=" + (failure == null ? "none" : failure.ToString()));
            if (failure != null) throw new Exception("HTTP fixture failed", failure);
            Assert(requests.Count == statuses.Length, "expected HTTP request count");
        }

        private void Run()
        {
            try
            {
                foreach (int status in statuses)
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        client.ReceiveTimeout = 5000;
                        stream.ReadTimeout = 5000;
                        StreamReader reader = new StreamReader(stream, Encoding.UTF8);
                        CapturedRequest request = new CapturedRequest();
                        request.FirstLine = reader.ReadLine();
                        request.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string line;
                        while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                        {
                            int split = line.IndexOf(':');
                            if (split > 0) request.Headers[line.Substring(0, split)] = line.Substring(split + 1).Trim();
                        }
                        string lengthText;
                        int length = request.Headers.TryGetValue("Content-Length", out lengthText) ?
                            int.Parse(lengthText, CultureInfo.InvariantCulture) : 0;
                        char[] body = new char[length];
                        int read = 0;
                        while (read < length)
                        {
                            int count = reader.Read(body, read, length - read);
                            if (count == 0) throw new IOException("HTTP request ended before body.");
                            read += count;
                        }
                        request.Body = new string(body);
                        requests.Add(request);
                        string location = status >= 300 && status < 400 && RedirectLocation != null ?
                            "Location: " + RedirectLocation + "\r\n" : "";
                        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) +
                            " Result\r\n" + location + "Content-Length: 0\r\nConnection: close\r\n\r\n");
                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch (Exception error) { failure = error; }
        }

        public void Dispose()
        {
            listener.Stop();
            thread.Join(1000);
        }
    }

    private static void InTempDirectory(Action<string> test)
    {
        string root = Environment.GetEnvironmentVariable("FPT_TEST_ROOT");
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        string directory = Path.Combine(root, "fpt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, true); }
    }

    private static void AssertThrowsInvalidData(Action action, string description)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected InvalidDataException: " + description);
    }

    private static void AssertThrowsAny(Action action, string description)
    {
        try { action(); }
        catch (Exception) { return; }
        throw new Exception("Expected failure: " + description);
    }

    private static void AssertThrowsFormat(Action action, string description)
    {
        try
        {
            action();
        }
        catch (FormatException)
        {
            return;
        }
        throw new Exception("Expected FormatException: " + description);
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new Exception("Failed: " + description);
    }
}
