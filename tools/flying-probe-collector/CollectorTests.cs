using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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
            Run("RejectCorruptStores", RejectCorruptStores);
            Run("FailedWriteKeepsMemory", FailedWriteKeepsMemory);
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
