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
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class CollectorTests
{
    private static int passed;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--host-status")
            {
                Run("CpuPercentFromCounters", CpuPercentFromCounters);
                if (Environment.OSVersion.Platform == PlatformID.Win32NT) Run("CaptureHostStatusOnWindows", CaptureHostStatusOnWindows);
                else Console.WriteLine("SKIP CaptureHostStatusOnWindows (requires Windows)");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--host-sender")
            {
                Run("HostStatusPost", HostStatusPost);
                Run("HostStatusPostFailures", HostStatusPostFailures);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--relay")
            {
                Run("RelayForwardsTwoDeviceIds", RelayForwardsTwoDeviceIds);
                Run("RelayDoesNotOverwriteListenerIdentity", RelayDoesNotOverwriteListenerIdentity);
                Run("RelayRejectsInvalidSnapshotAndContinues", RelayRejectsInvalidSnapshotAndContinues);
                Run("RelayDoesNotResendAfterRestart", RelayDoesNotResendAfterRestart);
                Run("RelayRetriesAfterSendFailure", RelayRetriesAfterSendFailure);
                Run("RelayFailureDoesNotStopTxtDelivery", RelayFailureDoesNotStopTxtDelivery);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--http")
            {
                Run("AcceptedPostRemovesEvent", AcceptedPostRemovesEvent);
                Run("RejectOneContinueNext", RejectOneContinueNext);
                Run("NetworkFailureRetains", NetworkFailureRetains);
                Run("RedirectRetainsEvent", RedirectRetainsEvent);
                Run("NoBearerWhenEmpty", NoBearerWhenEmpty);
                Run("SlowResponseHonorsOverallTimeout", SlowResponseHonorsOverallTimeout);
                Run("HostStatusPost", HostStatusPost);
                Run("HostStatusPostFailures", HostStatusPostFailures);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--runtime-host")
            {
                Run("HostWorkerRecoversAndStops", HostWorkerRecoversAndStops);
                Run("RuntimeHostFailureDoesNotBlockTxt", RuntimeHostFailureDoesNotBlockTxt);
                Run("StopWaitsForHostRequest", StopWaitsForHostRequest);
                Run("FatalTxtWorkerStopsHostWorker", FatalTxtWorkerStopsHostWorker);
                return 0;
            }
            Run("ParseSample", ParseSample);
            Run("RejectMalformed", RejectMalformed);
            Run("MapEvent", MapEvent);
            Run("CpuPercentFromCounters", CpuPercentFromCounters);
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) Run("CaptureHostStatusOnWindows", CaptureHostStatusOnWindows);
            else Console.WriteLine("SKIP CaptureHostStatusOnWindows (requires Windows)");
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
            Run("HostStatusPost", HostStatusPost);
            Run("HostStatusPostFailures", HostStatusPostFailures);
            Run("RelayForwardsTwoDeviceIds", RelayForwardsTwoDeviceIds);
            Run("RelayDoesNotOverwriteListenerIdentity", RelayDoesNotOverwriteListenerIdentity);
            Run("RelayRejectsInvalidSnapshotAndContinues", RelayRejectsInvalidSnapshotAndContinues);
            Run("RelayDoesNotResendAfterRestart", RelayDoesNotResendAfterRestart);
            Run("RelayRetriesAfterSendFailure", RelayRetriesAfterSendFailure);
            Run("RelayFailureDoesNotStopTxtDelivery", RelayFailureDoesNotStopTxtDelivery);
            Run("LoadSettingsRelativeToIni", LoadSettingsRelativeToIni);
            Run("HistorySurvivesRestart", HistorySurvivesRestart);
            Run("HistoryReadShowsUtcPlusEight", HistoryReadShowsUtcPlusEight);
            Run("MalformedHistoryLineIsReported", MalformedHistoryLineIsReported);
            Run("AcceptedWithoutHistoryStaysQueued", AcceptedWithoutHistoryStaysQueued);
            Run("HistoryNeverContainsTokens", HistoryNeverContainsTokens);
            Run("RuntimeStartStopReleasesLock", RuntimeStartStopReleasesLock);
            Run("HostWorkerRecoversAndStops", HostWorkerRecoversAndStops);
            Run("RuntimeHostFailureDoesNotBlockTxt", RuntimeHostFailureDoesNotBlockTxt);
            Run("StopWaitsForHostRequest", StopWaitsForHostRequest);
            Run("FatalTxtWorkerStopsHostWorker", FatalTxtWorkerStopsHostWorker);
            Run("StopDuringScanKeepsUnprocessedFiles", StopDuringScanKeepsUnprocessedFiles);
            Run("StopDoesNotSendNextPendingEvent", StopDoesNotSendNextPendingEvent);
            Run("SlowResponseHonorsOverallTimeout", SlowResponseHonorsOverallTimeout);
            Run("SettingsDraftCanOpenPlaceholder", SettingsDraftCanOpenPlaceholder);
            Run("SavePreservesOtherEntries", SavePreservesOtherEntries);
            Run("BuildReleaseLayout", BuildReleaseLayout);
            Run("GuiExeOpensWithPlaceholder", GuiExeOpensWithPlaceholder);
            Run("LauncherExplainsStartupFailure", LauncherExplainsStartupFailure);
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

    private static void CpuPercentFromCounters()
    {
        CpuTimes first = new CpuTimes(100, 200, 100);
        Assert(!HostStatusSampler.CalculateCpuPercent(null, first).HasValue, "first CPU sample has no rate");
        Assert(HostStatusSampler.CalculateCpuPercent(first, new CpuTimes(120, 260, 140)) == 80.0, "CPU busy delta");
        Assert(HostStatusSampler.CalculateCpuPercent(first, first) == null, "zero delta has no rate");
        Assert(HostStatusSampler.CalculateCpuPercent(first, new CpuTimes(90, 190, 90)) == null, "counter reset has no rate");
        Assert(HostStatusSampler.CalculateCpuPercent(first, new CpuTimes(170, 260, 140)) == 30.0, "idle included in kernel");
    }

    private static void CaptureHostStatusOnWindows()
    {
        HostStatusSample sample = new HostStatusSampler().Capture();
        Assert(!string.IsNullOrWhiteSpace(sample.HostName), "host name");
        Assert(sample.SampledAtUtc.Kind == DateTimeKind.Utc, "UTC sample time");
        Assert(sample.MemoryTotalBytes > 0 && sample.MemoryAvailableBytes <= sample.MemoryTotalBytes, "physical memory");
        Assert(sample.SystemDiskTotalBytes > 0 && sample.SystemDiskFreeBytes <= sample.SystemDiskTotalBytes, "system disk");
        Assert(sample.UptimeSeconds >= 0, "uptime");
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

    private static HostStatusSample ExampleHostStatus()
    {
        return new HostStatusSample("SCBGX", new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc),
            12.5, 17179869184, 8589934592, 512110190592, 171798691840, 3600);
    }

    private static void HostStatusPost()
    {
        using (HttpFixture server = HttpFixture.HostOnly(204))
        {
            HostStatusSender sender = new HostStatusSender(server.BaseUrl, "gateway-secret", "bearer-secret", 2,
                delegate(string message) { });
            Assert(sender.Send(ExampleHostStatus()), "host status accepted");
            server.WaitForRequests();
            CapturedRequest request = server.Requests[0];
            Assert(request.FirstLine == "POST /api/v1/gateway/host-status HTTP/1.1", "host status endpoint");
            Assert(request.Headers["X-Gateway-Token"] == "gateway-secret", "host gateway token");
            Assert(request.Headers["Authorization"] == "Bearer bearer-secret", "host bearer token");
            Dictionary<string, object> body = new JavaScriptSerializer().DeserializeObject(request.Body) as Dictionary<string, object>;
            Assert(Text(body, "hostName") == "SCBGX", "host name payload");
            Assert(DateTime.Parse(Text(body, "sampledAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal) == ExampleHostStatus().SampledAtUtc, "UTC sample time payload");
            Assert(Convert.ToDouble(body["cpuPercent"], CultureInfo.InvariantCulture) == 12.5, "CPU payload");
            Assert(Convert.ToInt64(body["memoryTotalBytes"], CultureInfo.InvariantCulture) == 17179869184, "memory bytes payload");
            Assert(Convert.ToInt64(body["systemDiskFreeBytes"], CultureInfo.InvariantCulture) == 171798691840, "disk bytes payload");
            Assert(Convert.ToInt64(body["uptimeSeconds"], CultureInfo.InvariantCulture) == 3600, "uptime seconds payload");
        }
    }

    private static void HostStatusPostFailures()
    {
        List<string> messages = new List<string>();
        using (HttpFixture login = new HttpFixture(200))
        using (HttpFixture server = HttpFixture.HostOnly(302))
        {
            server.RedirectLocation = login.BaseUrl + "/login";
            HostStatusSender sender = new HostStatusSender(server.BaseUrl, "gateway-secret", "", 2, messages.Add);
            Assert(!sender.Send(ExampleHostStatus()), "redirect is a failure");
            server.WaitForRequests();
            Assert(login.Requests.Count == 0, "host status does not follow redirect");
            Assert(!server.Requests[0].Headers.ContainsKey("Authorization"), "blank host bearer omitted");
        }
        HostStatusSender unreachable = new HostStatusSender("http://127.0.0.1:1", "gateway-secret", "bearer-secret", 1, messages.Add);
        Assert(!unreachable.Send(ExampleHostStatus()), "network failure is reported");
        Assert(messages.Count == 2 && !string.Join(" ", messages.ToArray()).Contains("gateway-secret")
            && !string.Join(" ", messages.ToArray()).Contains("bearer-secret"), "host failure logs hide credentials");
    }

    private static string StatusJson(string deviceId)
    {
        return "{\"schemaVersion\":1,\"deviceId\":\"" + deviceId + "\",\"sampledAt\":\"2026-10-22T10:00:00+00:00\"," +
            "\"cpuPercent\":12.5,\"memoryTotalBytes\":100,\"memoryAvailableBytes\":40," +
            "\"systemDiskTotalBytes\":1000,\"systemDiskFreeBytes\":500,\"uptimeSeconds\":60}";
    }

    private static void WriteStatus(string root, string deviceId, string content)
    {
        string directory = Path.Combine(root, deviceId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "host-status.json"), content, new UTF8Encoding(false));
    }

    private static void RelayForwardsTwoDeviceIds()
    {
        InTempDirectory(delegate(string directory)
        {
            string source = Path.Combine(directory, "source");
            WriteStatus(source, "FPT-01", StatusJson("FPT-01"));
            WriteStatus(source, "FPT-02", StatusJson("FPT-02"));
            List<string> names = new List<string>();
            new HostStatusRelay(source, Path.Combine(directory, "host-checkpoints.json"), delegate(HostStatusSample sample)
            {
                names.Add(sample.HostName);
                return true;
            }, delegate(string message) { }).ForwardOnce(delegate { return false; });
            names.Sort(StringComparer.Ordinal);
            Assert(names.Count == 2 && names[0] == "FPT-01" && names[1] == "FPT-02", "relay uses each configured device ID");
        });
    }

    private static void RelayDoesNotOverwriteListenerIdentity()
    {
        InTempDirectory(delegate(string directory)
        {
            string source = Path.Combine(directory, "source");
            WriteStatus(source, Environment.MachineName, StatusJson(Environment.MachineName));
            int sends = 0;
            new HostStatusRelay(source, Path.Combine(directory, "host-checkpoints.json"), delegate(HostStatusSample sample)
            { sends++; return true; }, delegate(string message) { }).ForwardOnce(delegate { return false; });
            Assert(sends == 0, "device snapshot cannot overwrite listener computer status");
        });
    }

    private static void RelayRejectsInvalidSnapshotAndContinues()
    {
        string valid = StatusJson("BAD");
        string[] invalid = {
            valid.Replace("\"schemaVersion\":1,", ""),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            valid.Replace("\"deviceId\":\"BAD\"", "\"deviceId\":\"OTHER\""),
            valid + new string(' ', 4096),
            valid.Replace("2026-10-22T10:00:00+00:00", "not-a-time"),
            valid.Replace("\"cpuPercent\":12.5", "\"cpuPercent\":101"),
            valid.Replace("\"memoryTotalBytes\":100", "\"memoryTotalBytes\":0"),
            valid.Replace("\"memoryAvailableBytes\":40", "\"memoryAvailableBytes\":101"),
            valid.Replace("\"systemDiskFreeBytes\":500", "\"systemDiskFreeBytes\":1001"),
            valid.Replace("\"uptimeSeconds\":60", "\"uptimeSeconds\":-1"),
            "{"
        };
        foreach (string payload in invalid)
        {
            InTempDirectory(delegate(string directory)
            {
                string source = Path.Combine(directory, "source");
                WriteStatus(source, "BAD", payload);
                WriteStatus(source, "GOOD", StatusJson("GOOD"));
                List<string> names = new List<string>();
                new HostStatusRelay(source, Path.Combine(directory, "host-checkpoints.json"), delegate(HostStatusSample sample)
                {
                    names.Add(sample.HostName);
                    return true;
                }, delegate(string message) { }).ForwardOnce(delegate { return false; });
                Assert(names.Count == 1 && names[0] == "GOOD", "bad snapshot must not block a good neighbor");
            });
        }
    }

    private static void RelayDoesNotResendAfterRestart()
    {
        InTempDirectory(delegate(string directory)
        {
            string source = Path.Combine(directory, "source");
            string checkpoint = Path.Combine(directory, "host-checkpoints.json");
            WriteStatus(source, "FPT-01", StatusJson("FPT-01"));
            int sends = 0;
            Func<HostStatusSample, bool> send = delegate(HostStatusSample sample) { sends++; return true; };
            new HostStatusRelay(source, checkpoint, send, delegate(string message) { }).ForwardOnce(delegate { return false; });
            new HostStatusRelay(source, checkpoint, send, delegate(string message) { }).ForwardOnce(delegate { return false; });
            Assert(sends == 1, "restarted relay skips an unchanged successful snapshot");
            WriteStatus(source, "FPT-01", StatusJson("FPT-01").Replace("10:00:00", "10:01:00"));
            new HostStatusRelay(source, checkpoint, send, delegate(string message) { }).ForwardOnce(delegate { return false; });
            Assert(sends == 2, "new sampledAt forwards a new snapshot");
        });
    }

    private static void RelayRetriesAfterSendFailure()
    {
        InTempDirectory(delegate(string directory)
        {
            string source = Path.Combine(directory, "source");
            WriteStatus(source, "FPT-01", StatusJson("FPT-01"));
            int attempts = 0;
            HostStatusRelay relay = new HostStatusRelay(source, Path.Combine(directory, "host-checkpoints.json"),
                delegate(HostStatusSample sample) { attempts++; return attempts > 1; }, delegate(string message) { });
            relay.ForwardOnce(delegate { return false; });
            relay.ForwardOnce(delegate { return false; });
            relay.ForwardOnce(delegate { return false; });
            Assert(attempts == 2, "failed send retries once then persists success");
        });
    }

    private static void RelayFailureDoesNotStopTxtDelivery()
    {
        InTempDirectory(delegate(string directory)
        {
            using (HttpFixture server = new HttpFixture(204))
            {
                server.HostStatusCode = 500;
                server.ExpectedHostRequests = 2;
                Settings settings = RuntimeSettings(directory, server.BaseUrl);
                WriteStatus(settings.SourceRoot, "FPT-01", StatusJson("FPT-01"));
                new PendingEventStore(settings.QueueFile).Enqueue(EventWithRecord("R-relay-independent"));
                CollectorRuntime runtime = new CollectorRuntime(settings, delegate(string message) { }, null, null, null,
                    ExampleHostStatus);
                runtime.Start();
                server.WaitForRequests();
                Assert(runtime.StopAsync().Wait(5000), "runtime stops after relay and TXT delivery");
                Assert(new PendingEventStore(settings.QueueFile).Snapshot().Count == 0,
                    "TXT event succeeds while relay HTTP returns 500");
                int deviceRequests = server.Requests.FindAll(delegate(CapturedRequest request)
                { return request.Body.Contains("\"hostName\":\"FPT-01\""); }).Count;
                Assert(deviceRequests == 1, "device status attempted separately");
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

    private static void HistorySurvivesRestart()
    {
        InTempDirectory(delegate(string directory)
        {
            SyncHistory history = new SyncHistory(directory, "gateway-secret", "bearer-secret");
            history.Append(SyncRecord.FromEvent("queued", EventWithRecord("R-1"), 0, ""));
            history.Append(SyncRecord.FromEvent("failed", EventWithRecord("R-1"), 500, "temporary"));
            history.Append(SyncRecord.FromEvent("success", EventWithRecord("R-1"), 204, "accepted"));
            int invalid;
            IList<SyncRecord> records = new SyncHistory(directory).ReadPage(DateTime.Now, 0, 20, out invalid);
            Assert(records.Count == 3 && invalid == 0, "history survives new instance");
            Assert(records[0].Stage == "success" && records[2].Stage == "queued", "newest history first");
        });
    }

    private static void HistoryReadShowsUtcPlusEight()
    {
        InTempDirectory(delegate(string directory)
        {
            SyncRecord record = SyncRecord.FromEvent("success", EventWithRecord("R-1"), 200, "accepted");
            record.Time = new DateTime(2026, 10, 15, 3, 53, 10, DateTimeKind.Utc);
            new SyncHistory(directory).Append(record);
            int invalid;
            IList<SyncRecord> rows = new SyncHistory(directory).ReadPage(new DateTime(2026, 10, 15), 0, 10, out invalid);
            Assert(rows.Count == 1 && invalid == 0, "saved UTC history row loads");
            Assert(rows[0].Time == new DateTime(2026, 10, 15, 11, 53, 10) &&
                rows[0].Time.Kind == DateTimeKind.Unspecified, "saved UTC history displays at UTC+8");
        });
    }

    private static void MalformedHistoryLineIsReported()
    {
        InTempDirectory(delegate(string directory)
        {
            SyncHistory history = new SyncHistory(directory);
            history.Append(SyncRecord.FromEvent("queued", EventWithRecord("R-1"), 0, ""));
            File.AppendAllText(Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"), "{broken\n");
            File.AppendAllText(Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"),
                "{\"Time\":\"bad\",\"Stage\":\"failed\",\"Tested\":1}\n" +
                "{\"Time\":\"2026-10-15T10:00:00\",\"Stage\":\"failed\",\"Tested\":2147483648}\n");
            int invalid;
            IList<SyncRecord> records = history.ReadPage(DateTime.Now, 0, 20, out invalid);
            Assert(records.Count == 1 && invalid == 3, "damaged typed rows are counted and valid row remains");
        });
    }

    private static void AcceptedWithoutHistoryStaysQueued()
    {
        InTempDirectory(delegate(string directory)
        {
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            queue.Enqueue(EventWithRecord("R-1"));
            using (HttpFixture server = new HttpFixture(204))
            {
                Action<SyncRecord> broken = delegate(SyncRecord record) { throw new IOException("history unavailable"); };
                AssertThrowsAny(delegate { new QmsSender(server.BaseUrl, "gateway-secret", "", 2, queue,
                    delegate(string message) { }, broken).SendPending(); }, "history failure is surfaced");
                server.WaitForRequests();
                Assert(queue.Snapshot().Count == 1, "accepted event remains queued when history fails");
            }
        });
    }

    private static void HistoryNeverContainsTokens()
    {
        InTempDirectory(delegate(string directory)
        {
            SyncHistory history = new SyncHistory(directory, "gateway-secret", "bearer-secret");
            history.Append(SyncRecord.FromEvent("failed", EventWithRecord("R-1"), 500,
                "gateway-secret bearer-secret " + new string('汉', 10000)));
            string raw = File.ReadAllText(Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"));
            Assert(!raw.Contains("gateway-secret") && !raw.Contains("bearer-secret"), "tokens redacted on disk");
            int invalid;
            SyncRecord record = history.ReadPage(DateTime.Now, 0, 1, out invalid)[0];
            Assert(Encoding.UTF8.GetByteCount(record.Response) <= 8192, "response summary bounded by UTF-8 bytes");
        });
    }

    private static void RuntimeStartStopReleasesLock()
    {
        InTempDirectory(delegate(string directory)
        {
            Settings settings = RuntimeSettings(directory, "http://127.0.0.1:1");
            CollectorRuntime runtime = new CollectorRuntime(settings, delegate(string line) { }, null, null);
            runtime.Start();
            Assert(runtime.IsRunning, "runtime starts");
            AssertThrowsAny(delegate { new CollectorOwnership(settings.QueueFile).Dispose(); }, "running collector owns storage");
            Assert(runtime.StopAsync().Wait(5000), "runtime stops promptly");
            using (new CollectorOwnership(settings.QueueFile)) { }
        });
    }

    private static void HostWorkerRecoversAndStops()
    {
        using (ManualResetEvent stop = new ManualResetEvent(false))
        using (ManualResetEvent repeated = new ManualResetEvent(false))
        {
            int ticks = 0;
            List<string> errors = new List<string>();
            Task task = Task.Factory.StartNew(delegate
            {
                HostStatusWorker.Run(stop, TimeSpan.FromMilliseconds(20), delegate
                {
                    if (Interlocked.Increment(ref ticks) == 1) throw new IOException("sample failed");
                    repeated.Set();
                }, errors.Add);
            });
            Assert(repeated.WaitOne(2000), "host worker retries after sampling error");
            stop.Set();
            Assert(task.Wait(2000), "host worker stops promptly");
            Assert(ticks >= 2 && errors.Count == 1 && errors[0].Contains("IOException"), "host worker logs safe error class");
        }
    }

    private static void RuntimeHostFailureDoesNotBlockTxt()
    {
        InTempDirectory(delegate(string directory)
        {
            using (HttpFixture server = new HttpFixture(204))
            {
                server.HostStatusCode = 500;
                server.ExpectedHostRequests = 1;
                Settings settings = RuntimeSettings(directory, server.BaseUrl);
                new PendingEventStore(settings.QueueFile).Enqueue(EventWithRecord("R-host-independent"));
                List<string> messages = new List<string>();
                CollectorRuntime runtime = new CollectorRuntime(settings, messages.Add, null, null, null,
                    ExampleHostStatus);
                runtime.Start();
                server.WaitForRequests();
                Assert(runtime.StopAsync().Wait(5000), "runtime stops after both requests");
                Assert(new PendingEventStore(settings.QueueFile).Snapshot().Count == 0, "TXT event succeeds after host HTTP failure");
                Assert(server.Requests.Exists(delegate(CapturedRequest request)
                { return request.FirstLine.Contains("/api/v1/gateway/host-status"); }), "host status posted immediately");
                Assert(messages.Exists(delegate(string message) { return message.Contains("Host status delivery failed: HTTP 500"); }),
                    "host failure logged separately");
            }
        });
    }

    private static void StopWaitsForHostRequest()
    {
        InTempDirectory(delegate(string directory)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using (ManualResetEvent connected = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            {
                Thread server = new Thread(delegate()
                {
                    try
                    {
                        using (TcpClient client = listener.AcceptTcpClient())
                        using (NetworkStream stream = client.GetStream())
                        {
                            connected.Set();
                            release.WaitOne(5000);
                            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            stream.Write(response, 0, response.Length);
                        }
                    }
                    catch (SocketException) { }
                    catch (IOException) { }
                });
                server.IsBackground = true;
                server.Start();
                try
                {
                    string url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                    CollectorRuntime runtime = new CollectorRuntime(RuntimeSettings(directory, url),
                        delegate(string message) { }, null, null, null, ExampleHostStatus);
                    runtime.Start();
                    Assert(connected.WaitOne(5000), "host request started");
                    Task stopping = runtime.StopAsync();
                    Assert(!stopping.IsCompleted, "stop waits for in-flight host request");
                    release.Set();
                    Assert(stopping.Wait(5000), "stop finishes after host request");
                }
                finally { release.Set(); listener.Stop(); server.Join(1000); }
            }
        });
    }

    private static void FatalTxtWorkerStopsHostWorker()
    {
        InTempDirectory(delegate(string directory)
        {
            Settings settings = RuntimeSettings(directory, "http://127.0.0.1:1");
            CollectorRuntime runtime = new CollectorRuntime(settings, delegate(string message) { }, null,
                delegate(int count) { throw new InvalidOperationException("display failed"); }, null, ExampleHostStatus);
            runtime.Start();
            try { runtime.Completion.Wait(5000); }
            catch (AggregateException) { }
            Assert(runtime.Completion.IsCompleted && runtime.Completion.IsFaulted, "fatal TXT worker ends whole runtime");
            using (new CollectorOwnership(settings.QueueFile)) { }
        });
    }

    private static void StopDuringScanKeepsUnprocessedFiles()
    {
        InTempDirectory(delegate(string directory)
        {
            Settings settings = RuntimeSettings(directory, "http://127.0.0.1:1");
            string device = Path.Combine(settings.SourceRoot, "FPT-01");
            Directory.CreateDirectory(device);
            for (int i = 0; i < 3; i++)
                File.WriteAllText(Path.Combine(device, "MAT-" + i.ToString(CultureInfo.InvariantCulture) + ".txt"),
                    "Test_Counters 2 2 0 01:27:09");
            CollectorRuntime runtime = null;
            using (ManualResetEvent firstQueued = new ManualResetEvent(false))
            {
                runtime = new CollectorRuntime(settings, delegate(string line) { }, delegate(SyncRecord record)
                {
                    if (record.Stage == "queued") { runtime.StopAsync(); firstQueued.Set(); }
                }, null);
                runtime.Start();
                Assert(firstQueued.WaitOne(5000), "first file queued before stop");
                Assert(runtime.StopAsync().Wait(5000), "stop completes during scan");
                Assert(new PendingEventStore(settings.QueueFile).Snapshot().Count == 1,
                    "scanner leaves other files for next run");
            }
        });
    }

    private static void StopDoesNotSendNextPendingEvent()
    {
        InTempDirectory(delegate(string directory)
        {
            string source = Path.Combine(directory, "source");
            Directory.CreateDirectory(source);
            using (HttpFixture server = new HttpFixture(204))
            {
                Settings settings = RuntimeSettings(directory, server.BaseUrl);
                settings.RequestTimeoutSeconds = 1;
                PendingEventStore queue = new PendingEventStore(settings.QueueFile);
                queue.Enqueue(EventWithRecord("R-1"));
                queue.Enqueue(EventWithRecord("R-2"));
                CollectorRuntime runtime = null;
                ManualResetEvent firstDone = new ManualResetEvent(false);
                runtime = new CollectorRuntime(settings, delegate(string line) { }, delegate(SyncRecord record)
                {
                    if (record.Stage == "success") { runtime.StopAsync(); firstDone.Set(); }
                }, null);
                runtime.Start();
                Assert(firstDone.WaitOne(5000), "first event sent before stop");
                Assert(runtime.StopAsync().Wait(5000), "runtime stopped");
                server.WaitForRequests();
                IList<Dictionary<string, object>> remaining = new PendingEventStore(settings.QueueFile).Snapshot();
                Assert(remaining.Count == 1 && Text(remaining[0], "recordNo") == "R-2", "second event retained");
            }
        });
    }

    private static void SlowResponseHonorsOverallTimeout()
    {
        InTempDirectory(delegate(string directory)
        {
            PendingEventStore queue = new PendingEventStore(Path.Combine(directory, "pending.json"));
            queue.Enqueue(EventWithRecord("R-slow"));
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Thread server = new Thread(delegate()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 30\r\nConnection: close\r\n\r\n");
                        stream.Write(headers, 0, headers.Length);
                        for (int i = 0; i < 30; i++)
                        {
                            stream.WriteByte((byte)'x');
                            Thread.Sleep(100);
                        }
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            });
            server.IsBackground = true; server.Start();
            try
            {
                string url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                Stopwatch timer = Stopwatch.StartNew();
                new QmsSender(url, "gateway-secret", "", 1, queue, delegate(string message) { }).SendPending();
                timer.Stop();
                Assert(timer.ElapsedMilliseconds < 2300, "whole response obeys request deadline");
                Assert(queue.Snapshot().Count == 1, "timed-out response stays queued");
            }
            finally { listener.Stop(); server.Join(5000); }
        });
    }

    private static Settings RuntimeSettings(string directory, string url)
    {
        string source = Path.Combine(directory, "source");
        Directory.CreateDirectory(source);
        string path = Path.Combine(directory, "runtime.ini");
        File.WriteAllText(path, "SourceRoot=" + source + "\r\nScanIntervalSeconds=1\r\nQmsBaseUrl=" + url +
            "\r\nGatewayToken=test-token\r\nQueueFile=.\\pending.json\r\nCheckpointFile=.\\checkpoint.json\r\nLogFile=.\\collector.log\r\n");
        return Settings.Load(path);
    }

    private static void SettingsDraftCanOpenPlaceholder()
    {
        InTempDirectory(delegate(string directory)
        {
            string path = Path.Combine(directory, "settings.ini");
            File.WriteAllText(path, "QmsBaseUrl=http://127.0.0.1:8080\r\nGatewayToken=REPLACE_WITH_QMS_GATEWAY_TOKEN\r\n");
            SettingsDocument draft = SettingsDocument.Load(path);
            Assert(draft.Get("GatewayToken", "") == "REPLACE_WITH_QMS_GATEWAY_TOKEN", "placeholder remains editable");
            AssertThrowsArgument(delegate { draft.Validate(); }, "placeholder rejected on validation");
        });
    }

    private static void SavePreservesOtherEntries()
    {
        InTempDirectory(delegate(string directory)
        {
            string path = Path.Combine(directory, "settings.ini");
            File.WriteAllText(path, "; site note\r\n[Custom]\r\nUnknown=kept\r\nQueueFile=.\\state\\pending.json\r\nGatewayToken=old\r\n");
            SettingsDocument draft = SettingsDocument.Load(path);
            draft.Set("GatewayToken", "new");
            draft.Set("QmsBaseUrl", "https://qms.example");
            draft.Save();
            string result = File.ReadAllText(path);
            Assert(result.Contains("Unknown=kept") && result.Contains("; site note"), "unknown lines remain");
            Assert(result.Contains("QueueFile=.\\state\\pending.json"), "storage path remains");
            Assert(Settings.Load(path).GatewayToken == "new", "edited value persists");
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
            Assert(File.Exists(Path.Combine(release, "FlyingProbeCollector.Console.exe")), "console EXE released alongside GUI");
            Assert(File.Exists(Path.Combine(release, "FlyingProbeCollector.ini")), "default INI released");
            Assert(File.Exists(Path.Combine(release, "start-collector.cmd")), "launcher released");
            Assert(File.Exists(Path.Combine(release, "README.md")), "operating guide released");
            string siteConfig = Path.Combine(release, "FlyingProbeCollector.ini");
            File.WriteAllText(siteConfig, "QmsBaseUrl=site-value\r\n");
            RunBuild(build, directory);
            Assert(File.ReadAllText(siteConfig) == "QmsBaseUrl=site-value\r\n", "rebuild preserves site INI");
        });
    }

    private static void GuiExeOpensWithPlaceholder()
    {
        InTempDirectory(delegate(string directory)
        {
            string source = Environment.GetEnvironmentVariable("FPT_TOOL_DIR");
            string target = Path.Combine(directory, "repo", "tools", "flying-probe-collector");
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            RunBuild(Path.Combine(target, "build.cmd"), directory);
            string release = Path.Combine(directory, "repo", "outputs", "flying-probe-collector");
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(release, "FlyingProbeCollector.exe"));
            start.WorkingDirectory = release;
            start.UseShellExecute = false;
            using (Process process = Process.Start(start))
            {
                try
                {
                    Assert(process.WaitForInputIdle(5000), "GUI message loop starts");
                    DateTime windowDeadline = DateTime.UtcNow.AddSeconds(5);
                    do { process.Refresh(); if (process.MainWindowHandle != IntPtr.Zero || process.HasExited) break; Thread.Sleep(50); }
                    while (DateTime.UtcNow < windowDeadline);
                    Assert(!process.HasExited && process.MainWindowHandle != IntPtr.Zero,
                        "placeholder shows a live window; exited=" + process.HasExited + " handle=" + process.MainWindowHandle +
                        (process.HasExited ? " code=" + process.ExitCode : ""));
                    process.CloseMainWindow();
                    Assert(process.WaitForExit(5000), "GUI closes normally");
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            }
        });
    }

    private static void LauncherExplainsStartupFailure()
    {
        InTempDirectory(delegate(string directory)
        {
            string source = Environment.GetEnvironmentVariable("FPT_TOOL_DIR");
            string target = Path.Combine(directory, "repo", "tools", "flying-probe-collector");
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            RunBuild(Path.Combine(target, "build.cmd"), directory);
            string release = Path.Combine(directory, "repo", "outputs", "flying-probe-collector");
            Assert(File.ReadAllText(Path.Combine(release, "start-collector.cmd")).Contains("FlyingProbeCollector.exe"),
                "launcher opens GUI executable");
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(release, "FlyingProbeCollector.Console.exe"));
            start.WorkingDirectory = release;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                Assert(process.WaitForExit(5000), "console exits when configuration is invalid");
                Assert(process.ExitCode != 0, "console retains startup failure exit code");
                Assert(error.Contains("GatewayToken must contain"), "configuration error is visible");
            }
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
            string exe = Path.Combine(release, "FlyingProbeCollector.Console.exe");
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
        public int HostStatusCode { get; set; }
        public int ExpectedHostRequests { get; set; }
        public List<CapturedRequest> Requests { get { return requests; } }

        public HttpFixture(params int[] statuses)
            : this(statuses, 204, 0)
        {
        }

        public static HttpFixture HostOnly(int status)
        {
            return new HttpFixture(new int[0], status, 1);
        }

        private HttpFixture(int[] statuses, int hostStatusCode, int expectedHostRequests)
        {
            this.statuses = statuses;
            HostStatusCode = hostStatusCode;
            ExpectedHostRequests = expectedHostRequests;
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
            int telemetryCount = requests.FindAll(delegate(CapturedRequest request)
            { return !request.FirstLine.Contains("/api/v1/gateway/host-status"); }).Count;
            int hostCount = requests.Count - telemetryCount;
            Assert(telemetryCount == statuses.Length && hostCount >= ExpectedHostRequests,
                "expected HTTP request count");
        }

        private void Run()
        {
            try
            {
                int telemetryIndex = 0;
                int hostCount = 0;
                while (telemetryIndex < statuses.Length || hostCount < ExpectedHostRequests)
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
                        bool host = request.FirstLine.Contains("/api/v1/gateway/host-status");
                        int status = host ? HostStatusCode : statuses[telemetryIndex];
                        if (host) hostCount++;
                        else telemetryIndex++;
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
