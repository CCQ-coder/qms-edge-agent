using System;
using System.Collections.Generic;
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
    private readonly Action<PresenceReport, PresenceSendResult> onPresence;
    private readonly IHostProbe hostProbe;
    private readonly ManualResetEvent stop = new ManualResetEvent(false);
    private readonly object gate = new object();
    private Task worker;
    private CollectorOwnership ownership;
    private volatile bool scanFailed;
    private volatile bool deliveryFailed;

    public CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending)
        : this(settings, log, onRecord, onPending, null)
    {
    }

    public CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending,
        Action<CollectorNoticeKind> onNotice)
        : this(settings, log, onRecord, onPending, onNotice, null, null)
    {
    }

    public CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending,
        Action<CollectorNoticeKind> onNotice, Action<PresenceReport, PresenceSendResult> onPresence)
        : this(settings, log, onRecord, onPending, onNotice, onPresence, null)
    {
    }

    internal CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending,
        Action<CollectorNoticeKind> onNotice, Action<PresenceReport, PresenceSendResult> onPresence, IHostProbe hostProbe)
    {
        this.settings = settings;
        this.log = log;
        this.onRecord = onRecord;
        this.onPending = onPending;
        this.onNotice = onNotice;
        this.onPresence = onPresence;
        this.hostProbe = hostProbe ?? new NetworkProbe();
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
                ScanSummary summary = new ScanSummary();
                ProbeScanner scanner = new ProbeScanner(settings.SourceRoot, pending, checkpoints, log, publish, summary);
                QmsSender sender = new QmsSender(settings.QmsBaseUrl, settings.GatewayToken, settings.BearerToken,
                    settings.RequestTimeoutSeconds, pending, log, publish);
                Log("Flying probe collector started. Source=" + settings.SourceRoot);
                Task scanWorker = Task.Factory.StartNew(delegate
                {
                    while (!stop.WaitOne(0))
                    {
                        bool failed = false;
                        try { scanner.ScanOnce(delegate { return stop.WaitOne(0); }); failed = scanner.LastScanHadError; }
                        catch (Exception error) { failed = true; Log("Scan failed: " + error.Message); }
                        scanFailed = failed;
                        ReportCurrentNotice();
                        PublishPending(pending);
                        stop.WaitOne(settings.ScanIntervalSeconds * 1000);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task sendWorker = Task.Factory.StartNew(delegate
                {
                    while (!stop.WaitOne(0))
                    {
                        bool failed = false;
                        try { sender.SendPending(delegate { return stop.WaitOne(0); }); failed = sender.LastSendFailed; }
                        catch (Exception error) { failed = true; Log("QMS delivery or history failed: " + error.GetType().Name); }
                        deliveryFailed = failed;
                        ReportCurrentNotice();
                        PublishPending(pending);
                        stop.WaitOne(settings.ScanIntervalSeconds * 1000);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                PresenceSender presenceSender = new PresenceSender(settings.QmsBaseUrl, settings.GatewayToken,
                    settings.RequestTimeoutSeconds);
                HostProbeCoordinator coordinator = new HostProbeCoordinator(hostProbe, 3000, 4);
                Task presenceWorker = Task.Factory.StartNew(delegate
                {
                    PresenceTracker tracker = new PresenceTracker(3);
                    int threshold = 3;
                    HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, string> lastState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, string> lastError = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    while (!stop.WaitOne(0))
                    {
                        int interval = 60;
                        try
                        {
                            DeviceHosts hosts;
                            try { hosts = DeviceHosts.Load(settings.HostsFile); }
                            catch (InvalidDataException error)
                            {
                                Log("Host mapping unavailable: " + error.GetType().Name);
                                hosts = new DeviceHosts();
                            }
                            interval = hosts.ProbeIntervalSeconds;
                            if (hosts.FailureThreshold != threshold)
                            {
                                threshold = hosts.FailureThreshold;
                                tracker = new PresenceTracker(threshold);
                            }
                            IList<PresenceReport> reports = ProbePresence(hosts, summary, coordinator, tracker, known);
                            SendPresence(reports, presenceSender, lastState, lastError);
                        }
                        catch (Exception error) { Log("Presence cycle failed: " + error.GetType().Name); }
                        stop.WaitOne(interval * 1000);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                worker = Task.WhenAll(scanWorker, sendWorker, presenceWorker).ContinueWith(delegate(Task completed)
                {
                    try
                    {
                        Log("Flying probe collector stopped.");
                        if (completed.IsFaulted) throw completed.Exception;
                    }
                    finally { ownership.Dispose(); }
                }, TaskScheduler.Default);
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

    private void ReportCurrentNotice()
    {
        Report(scanFailed ? CollectorNoticeKind.ScanError :
            deliveryFailed ? CollectorNoticeKind.DeliveryError : CollectorNoticeKind.Healthy);
    }

    private void PublishPending(PendingEventStore pending)
    {
        if (onPending == null) return;
        try { onPending(pending.Snapshot().Count); }
        catch (Exception error) { Log("Pending count display failed: " + error.GetType().Name); }
    }

    private IList<PresenceReport> ProbePresence(DeviceHosts hosts, ScanSummary summary,
        HostProbeCoordinator coordinator, PresenceTracker tracker, HashSet<string> known)
    {
        Dictionary<string, HostTarget> targets = new Dictionary<string, HostTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (HostTarget target in hosts.Targets)
        {
            targets[target.MachineName] = target;
            known.Add(target.MachineName);
        }
        foreach (string machine in summary.Snapshot(DateTime.UtcNow).Keys) known.Add(machine);
        IDictionary<string, bool?> reachability = coordinator.Probe(targets.Values, delegate { return stop.WaitOne(0); });
        List<PresenceReport> reports = new List<PresenceReport>();
        foreach (string machine in known)
        {
            if (stop.WaitOne(0)) break;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SourceSnapshot source = summary.Get(machine, now.UtcDateTime);
            HostTarget target;
            targets.TryGetValue(machine, out target);
            bool? reachable;
            if (target == null || !reachability.TryGetValue(machine, out reachable)) reachable = null;
            string network = tracker.Observe(machine, reachable, now.UtcDateTime);
            reports.Add(new PresenceReport {
                MachineName = machine, HostName = target == null ? "" : target.HostName,
                ObservedAt = now, NetworkState = network, SourceState = source.SourceState,
                LastTxtChangeAt = source.LastTxtChangeUtc.HasValue && source.LastTxtChangeUtc.Value <= now.UtcDateTime
                    ? (DateTimeOffset?)new DateTimeOffset(DateTime.SpecifyKind(source.LastTxtChangeUtc.Value, DateTimeKind.Utc))
                    : null
            });
        }
        return reports;
    }

    private void SendPresence(IList<PresenceReport> reports, PresenceSender sender,
        Dictionary<string, string> lastState, Dictionary<string, string> lastError)
    {
        for (int start = 0; start < reports.Count && !stop.WaitOne(0); start += 4)
        {
            int count = Math.Min(4, reports.Count - start);
            Task<PresenceSendResult>[] tasks = new Task<PresenceSendResult>[count];
            for (int index = 0; index < count; index++)
            {
                PresenceReport current = reports[start + index];
                tasks[index] = Task.Factory.StartNew(delegate {
                    if (stop.WaitOne(0)) return new PresenceSendResult { Error = "Stopped" };
                    try { return sender.Send(current); }
                    catch (Exception error) {
                        if (error is OutOfMemoryException) throw;
                        return new PresenceSendResult { Error = error.GetType().Name };
                    }
                },
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            Task.WaitAll(tasks);
            if (stop.WaitOne(0)) return;
            for (int index = 0; index < count; index++)
            {
                PresenceReport report = reports[start + index];
                PresenceSendResult result = tasks[index].Result;
                string state = report.NetworkState + "/" + report.SourceState;
                string prior;
                if (!lastState.TryGetValue(report.MachineName, out prior) || prior != state)
                    Log("Presence " + report.MachineName + " " + state);
                lastState[report.MachineName] = state;
                string error = result.Accepted ? "" : (result.Error ?? "HTTP " + result.HttpStatus);
                if (!lastError.TryGetValue(report.MachineName, out prior) || prior != error)
                    if (error.Length > 0) Log("Presence report failed for " + report.MachineName + ": " + error);
                lastError[report.MachineName] = error;
                if (onPresence != null)
                {
                    try { onPresence(report, result); }
                    catch (Exception callbackError) { Log("Presence display failed: " + callbackError.GetType().Name); }
                }
            }
        }
    }
}
