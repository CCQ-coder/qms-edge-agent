using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

internal enum CollectorNoticeKind { Healthy, ScanError, DeliveryError }

internal sealed class CollectorRuntime
{
    private readonly Settings settings;
    private readonly Action<string> log;
    private readonly Action<SyncRecord> onRecord;
    private readonly Action<int> onPending;
    private readonly Action<CollectorNoticeKind> onNotice;
    private readonly ManualResetEvent stop = new ManualResetEvent(false);
    private readonly object gate = new object();
    private Task worker;
    private CollectorOwnership ownership;

    public CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending)
        : this(settings, log, onRecord, onPending, null)
    {
    }

    public CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending,
        Action<CollectorNoticeKind> onNotice)
    {
        this.settings = settings;
        this.log = log;
        this.onRecord = onRecord;
        this.onPending = onPending;
        this.onNotice = onNotice;
    }

    public bool IsRunning
    {
        get { lock (gate) return worker != null && !worker.IsCompleted; }
    }

    public Task Completion { get { lock (gate) return worker; } }

    public void Start()
    {
        lock (gate)
        {
            if (worker != null && !worker.IsCompleted) throw new InvalidOperationException("Collector is already running.");
            stop.Reset();
            ownership = new CollectorOwnership(settings.QueueFile);
            try
            {
                PendingEventStore pending = new PendingEventStore(settings.QueueFile);
                ScanCheckpointStore checkpoints = new ScanCheckpointStore(settings.CheckpointFile);
                SyncHistory history = new SyncHistory(Path.Combine(Path.GetDirectoryName(settings.QueueFile), "history"),
                    settings.GatewayToken, settings.BearerToken);
                Action<SyncRecord> publish = delegate(SyncRecord record)
                {
                    history.Append(record);
                    if (onRecord != null)
                    {
                        try { onRecord(record); }
                        catch (Exception error) { Log("Display update failed: " + error.GetType().Name); }
                    }
                };
                ProbeScanner scanner = new ProbeScanner(settings.SourceRoot, pending, checkpoints, log, publish);
                QmsSender sender = new QmsSender(settings.QmsBaseUrl, settings.GatewayToken, settings.BearerToken,
                    settings.RequestTimeoutSeconds, pending, log, publish);
                worker = Task.Factory.StartNew(delegate
                {
                    try
                    {
                        Log("Flying probe collector started. Source=" + settings.SourceRoot);
                        while (!stop.WaitOne(0))
                        {
                            bool scanError = false;
                            bool sendError = false;
                            try { scanner.ScanOnce(); scanError = scanner.LastScanHadError; }
                            catch (Exception error) { scanError = true; Log("Scan failed: " + error.Message); }
                            if (scanError) Report(CollectorNoticeKind.ScanError);
                            try { sender.SendPending(delegate { return stop.WaitOne(0); }); sendError = sender.LastSendFailed; }
                            catch (Exception error) { sendError = true; Log("QMS delivery or history failed: " + error.GetType().Name); }
                            if (sendError) Report(CollectorNoticeKind.DeliveryError);
                            if (!scanError && !sendError) Report(CollectorNoticeKind.Healthy);
                            if (onPending != null) onPending(pending.Snapshot().Count);
                            stop.WaitOne(settings.ScanIntervalSeconds * 1000);
                        }
                        Log("Flying probe collector stopped.");
                    }
                    finally { ownership.Dispose(); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch
            {
                ownership.Dispose();
                ownership = null;
                throw;
            }
        }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            stop.Set();
            if (worker != null) return worker;
            TaskCompletionSource<object> done = new TaskCompletionSource<object>();
            done.SetResult(null);
            return done.Task;
        }
    }

    private void Log(string message)
    {
        if (log != null) log(message);
    }

    private void Report(CollectorNoticeKind kind)
    {
        if (onNotice != null) onNotice(kind);
    }
}
