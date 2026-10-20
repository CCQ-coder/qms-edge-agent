using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal interface IHostProbe
{
    bool? Probe(HostTarget target, int timeoutMilliseconds, Func<bool> shouldStop);
}

internal sealed class NetworkProbe : IHostProbe
{
    private readonly Func<string, IPAddress[]> resolve;
    private readonly Func<IPAddress, HostTarget, int, bool> dial;

    public NetworkProbe() : this(Dns.GetHostAddresses, Dial) { }

    internal NetworkProbe(Func<string, IPAddress[]> resolve,
        Func<IPAddress, HostTarget, int, bool> dial)
    {
        this.resolve = resolve;
        this.dial = dial;
    }

    public bool? Probe(HostTarget target, int timeoutMilliseconds, Func<bool> shouldStop)
    {
        if (target == null || string.IsNullOrWhiteSpace(target.HostName) || shouldStop()) return null;
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            IPAddress[] addresses = resolve(target.HostName.Trim());
            if (shouldStop()) return null;
            foreach (IPAddress address in addresses)
            {
                int remaining = timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0) return false;
                if (dial(address, target, remaining)) return true;
                if (shouldStop()) return null;
            }
            return false;
        }
        catch (Exception error)
        {
            if (error is OutOfMemoryException) throw;
            return shouldStop() ? (bool?)null : false;
        }
    }

    private static bool Dial(IPAddress address, HostTarget target, int timeoutMilliseconds)
    {
        if (string.Equals(target.Method, "TCP", StringComparison.OrdinalIgnoreCase))
        {
            using (TcpClient client = new TcpClient(address.AddressFamily))
            {
                IAsyncResult connection = client.BeginConnect(address, target.TcpPort, null, null);
                try { return connection.AsyncWaitHandle.WaitOne(timeoutMilliseconds) && FinishConnect(client, connection); }
                finally { connection.AsyncWaitHandle.Close(); }
            }
        }
        using (Ping ping = new Ping())
            return ping.Send(address, timeoutMilliseconds).Status == IPStatus.Success;
    }

    private static bool FinishConnect(TcpClient client, IAsyncResult connection)
    {
        try { client.EndConnect(connection); return true; }
        catch (SocketException) { return false; }
    }
}

internal sealed class HostProbeCoordinator
{
    private readonly IHostProbe probe;
    private readonly int timeoutMilliseconds;
    private readonly int maxConcurrent;
    private readonly Dictionary<string, Task<bool?>> inFlight =
        new Dictionary<string, Task<bool?>>(StringComparer.OrdinalIgnoreCase);

    public HostProbeCoordinator(IHostProbe probe, int timeoutMilliseconds, int maxConcurrent)
    {
        this.probe = probe;
        this.timeoutMilliseconds = timeoutMilliseconds;
        this.maxConcurrent = maxConcurrent;
    }

    public IDictionary<string, bool?> Probe(IEnumerable<HostTarget> targets, Func<bool> shouldStop)
    {
        Dictionary<string, bool?> results = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        if (shouldStop()) return results;
        List<HostTarget> remaining = new List<HostTarget>(targets);
        while (remaining.Count > 0 && !shouldStop())
        {
            List<Task<bool?>> started = new List<Task<bool?>>();
            List<string> names = new List<string>();
            int active = 0;
            foreach (Task<bool?> prior in inFlight.Values) if (!prior.IsCompleted) active++;
            int capacity = Math.Max(0, maxConcurrent - active);
            for (int index = 0; index < remaining.Count && started.Count < capacity; )
            {
                HostTarget target = remaining[index];
                remaining.RemoveAt(index);
                if (string.IsNullOrWhiteSpace(target.HostName)) { results[target.MachineName] = null; continue; }
                Task<bool?> prior;
                if (inFlight.TryGetValue(target.MachineName, out prior))
                {
                    if (!prior.IsCompleted) { results[target.MachineName] = null; continue; }
                    inFlight.Remove(target.MachineName); // A result after its previous deadline is stale.
                }
                HostTarget current = target;
                Task<bool?> task = Task.Factory.StartNew(delegate
                {
                    try { return probe.Probe(current, timeoutMilliseconds, shouldStop); }
                    catch (Exception) { return (bool?)false; }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                inFlight[target.MachineName] = task;
                started.Add(task);
                names.Add(target.MachineName);
            }
            if (started.Count == 0)
            {
                foreach (HostTarget target in remaining)
                    results[target.MachineName] = null;
                break;
            }
            Task.WaitAll(started.ToArray(), timeoutMilliseconds);
            for (int index = 0; index < started.Count; index++)
            {
                Task<bool?> task = started[index];
                results[names[index]] = task.IsCompleted ? task.Result : (bool?)false;
                if (task.IsCompleted) inFlight.Remove(names[index]);
            }
        }
        return results;
    }
}
