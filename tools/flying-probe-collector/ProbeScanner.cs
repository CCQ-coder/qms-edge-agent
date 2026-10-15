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
    {
        this.root = root;
        this.pending = pending;
        this.checkpoints = checkpoints;
        this.log = log;
        this.onRecord = onRecord;
    }

    public void ScanOnce()
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root);
        }
        catch (IOException error)
        {
            Log("Source directory unavailable: " + error.Message);
            return;
        }
        catch (UnauthorizedAccessException error)
        {
            Log("Source directory inaccessible: " + error.Message);
            return;
        }

        foreach (string directory in directories)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (IOException error)
            {
                Log("Device directory unavailable: " + directory + ": " + error.Message);
                continue;
            }
            catch (UnauthorizedAccessException error)
            {
                Log("Device directory inaccessible: " + directory + ": " + error.Message);
                continue;
            }
            foreach (string file in files)
            {
                if (!string.Equals(Path.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase)) continue;
                try { ScanFile(directory, file); }
                catch (IOException error) { Log("File unavailable: " + file + ": " + error.Message); }
                catch (UnauthorizedAccessException error) { Log("File inaccessible: " + file + ": " + error.Message); }
                catch (FormatException error) { Log("Invalid counters: " + file + ": " + error.Message); }
            }
        }
    }

    private void ScanFile(string directory, string file)
    {
        FileInfo info = new FileInfo(file);
        info.Refresh();
        if (!info.Exists) return;
        FileStamp stamp = new FileStamp { Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
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
            catch (Exception error) { Log("History write failed for " + recordNo + ": " + error.GetType().Name); }
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
}
