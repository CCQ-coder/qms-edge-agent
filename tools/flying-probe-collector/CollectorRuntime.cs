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
    private readonly Func<HostStatusSample> captureHostStatus;
    private readonly ManualResetEvent stop = new ManualResetEvent(false);
    private readonly object gate = new object();
    private Task worker;
    private Task hostWorker;
    private Task completion;
    private CollectorOwnership ownership;

    public CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending)
        : this(settings, log, onRecord, onPending, null)
    {
    }

    public CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending,
        Action<CollectorNoticeKind> onNotice)
        : this(settings, log, onRecord, onPending, onNotice, null)
    {
    }

    internal CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending,
        Action<CollectorNoticeKind> onNotice, Func<HostStatusSample> captureHostStatus)
    {
        this.settings = settings;
        this.log = log;
        this.onRecord = onRecord;
        this.onPending = onPending;
        this.onNotice = onNotice;
        this.captureHostStatus = captureHostStatus;
    }

    public bool IsRunning
    {
        get { lock (gate) return completion != null && !completion.IsCompleted; }
    }

    public Task Completion { get { lock (gate) return completion; } }

    public void Start()
    {
        lock (gate)
        {
            if (completion != null && !completion.IsCompleted) throw new InvalidOperationException("Collector is already running.");
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
                HostStatusSender hostSender = new HostStatusSender(settings.QmsBaseUrl, settings.GatewayToken,
                    settings.BearerToken, settings.RequestTimeoutSeconds, log);
                HostStatusSampler sampler = new HostStatusSampler();
                Func<HostStatusSample> capture = captureHostStatus ?? sampler.Capture;
                worker = Task.Factory.StartNew(delegate
                {
                    try
                    {
                        Log("Flying probe collector started. Source=" + settings.SourceRoot);
                        while (!stop.WaitOne(0))
                        {
                            bool scanError = false;
                            bool sendError = false;
                            try { scanner.ScanOnce(delegate { return stop.WaitOne(0); }); scanError = scanner.LastScanHadError; }
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
                hostWorker = Task.Factory.StartNew(delegate
                {
                    HostStatusRelay relay = null;
                    HostStatusWorker.Run(stop, TimeSpan.FromSeconds(60), delegate
                    {
                        try { hostSender.Send(capture()); }
                        catch (Exception error) { Log("Listener host status failed: " + error.GetType().Name); }
                        try
                        {
                            if (relay == null)
                                relay = new HostStatusRelay(settings.SourceRoot, settings.CheckpointFile + ".host-status.json",
                                    hostSender.Send, Log);
                            relay.ForwardOnce(delegate { return stop.WaitOne(0); });
                        }
                        catch (Exception error) { Log("Device host status relay failed: " + error.GetType().Name); }
                    }, Log);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                worker.ContinueWith(delegate { stop.Set(); });
                hostWorker.ContinueWith(delegate { stop.Set(); });
                completion = Task.Factory.ContinueWhenAll(new[] { worker, hostWorker }, delegate(Task[] tasks)
                {
                    foreach (Task task in tasks) task.Wait();
                });
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
            if (completion != null) return completion;
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

internal static class HostStatusWorker
{
    internal static void Run(WaitHandle stop, TimeSpan interval, Action tick, Action<string> log)
    {
        while (!stop.WaitOne(0))
        {
            try { tick(); }
            catch (Exception error) { log("Host status cycle failed: " + error.GetType().Name); }
            if (stop.WaitOne(interval)) return;
        }
    }
}
