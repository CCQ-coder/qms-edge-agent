using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

internal sealed class ProbeScanner
{
    private readonly string root;
    private readonly PendingEventStore pending;
    private readonly ScanCheckpointStore checkpoints;
    private readonly Action<string> log;
    private readonly Action<SyncRecord> onRecord;
    private readonly ScanSummary summary;
    public bool LastScanHadError { get; private set; }
    private readonly Dictionary<string, FileStamp> observed = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

    private struct FileStamp
    {
        public long Length;
        public DateTime LastWriteUtc;
    }

    public ProbeScanner(string root, PendingEventStore pending, ScanCheckpointStore checkpoints, Action<string> log)
        : this(root, pending, checkpoints, log, null)
    {
    }

    public ProbeScanner(string root, PendingEventStore pending, ScanCheckpointStore checkpoints, Action<string> log,
        Action<SyncRecord> onRecord)
        : this(root, pending, checkpoints, log, onRecord, null)
    {
    }

    public ProbeScanner(string root, PendingEventStore pending, ScanCheckpointStore checkpoints, Action<string> log,
        Action<SyncRecord> onRecord, ScanSummary summary)
    {
        this.root = root;
        this.pending = pending;
        this.checkpoints = checkpoints;
        this.log = log;
        this.onRecord = onRecord;
        this.summary = summary;
    }

    public void ScanOnce()
    {
        ScanOnce(delegate { return false; });
    }

    public void ScanOnce(Func<bool> shouldStop)
    {
        if (shouldStop == null) throw new ArgumentNullException("shouldStop");
        LastScanHadError = false;
        if (shouldStop()) return;
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root);
            if (summary != null)
            {
                List<string> present = new List<string>();
                foreach (string directory in directories) present.Add(Path.GetFileName(directory));
                summary.MarkRootReadable(DateTime.UtcNow, present);
            }
        }
        catch (IOException error)
        {
            if (summary != null) summary.MarkRootUnreadable(DateTime.UtcNow);
            Issue("Source directory unavailable: " + error.Message);
            return;
        }
        catch (UnauthorizedAccessException error)
        {
            if (summary != null) summary.MarkRootUnreadable(DateTime.UtcNow);
            Issue("Source directory inaccessible: " + error.Message);
            return;
        }

        foreach (string directory in directories)
        {
            if (shouldStop()) return;
            string machine = Path.GetFileName(directory);
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
                if (summary != null) summary.MarkReadable(machine, DateTime.UtcNow);
            }
            catch (IOException error)
            {
                if (summary != null) summary.MarkUnreadable(machine, DateTime.UtcNow);
                Issue("Device directory unavailable: " + directory + ": " + error.Message);
                continue;
            }
            catch (UnauthorizedAccessException error)
            {
                if (summary != null) summary.MarkUnreadable(machine, DateTime.UtcNow);
                Issue("Device directory inaccessible: " + directory + ": " + error.Message);
                continue;
            }
            foreach (string file in files)
            {
                if (shouldStop()) return;
                if (!string.Equals(Path.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase)) continue;
                try { ScanFile(directory, file); }
                catch (IOException error) { Issue("File unavailable: " + file + ": " + error.Message); }
                catch (UnauthorizedAccessException error) { Issue("File inaccessible: " + file + ": " + error.Message); }
                catch (FormatException error) { Issue("Invalid counters: " + file + ": " + error.Message); }
            }
        }
    }

    private void ScanFile(string directory, string file)
    {
        FileInfo info = new FileInfo(file);
        info.Refresh();
        if (!info.Exists) return;
        FileStamp stamp = new FileStamp { Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
        if (summary != null) summary.ObserveFile(Path.GetFileName(directory), Path.GetFileName(file),
            stamp.Length, stamp.LastWriteUtc, DateTime.UtcNow);
        FileStamp previous;
        bool seen = observed.TryGetValue(file, out previous);
        observed[file] = stamp;
        if (!seen || previous.Length != stamp.Length || previous.LastWriteUtc != stamp.LastWriteUtc) return;

        string content = File.ReadAllText(file, Encoding.UTF8);
        if (!MetadataUnchanged(info, stamp.Length, stamp.LastWriteUtc)) return;
        ProbeCounters counters = ProbeRecords.Parse(content);
        string machine = Path.GetFileName(directory);
        string material = Path.GetFileNameWithoutExtension(file);
        string relativePath = machine + "/" + Path.GetFileName(file);
        Dictionary<string, object> eventItem = ProbeRecords.CreateEvent(machine, material, relativePath, info.CreationTime, info.LastWriteTime, counters);
        string recordNo = (string)eventItem["recordNo"];
        if (checkpoints.Contains(relativePath, recordNo)) return;
        pending.Enqueue(eventItem);
        checkpoints.Record(relativePath, recordNo);
        if (onRecord != null)
        {
            try { onRecord(SyncRecord.FromEvent("queued", eventItem, 0, "")); }
            catch (Exception error) { Issue("History write failed for " + recordNo + ": " + error.GetType().Name); }
        }
        Log("Queued " + relativePath + " record=" + recordNo);
    }

    internal static bool MetadataUnchanged(FileInfo file, long length, DateTime writeUtc)
    {
        file.Refresh();
        return file.Exists && file.Length == length && file.LastWriteTimeUtc == writeUtc;
    }

    private void Log(string message)
    {
        if (log != null) log(message);
    }

    private void Issue(string message)
    {
        LastScanHadError = true;
        Log(message);
    }
}
