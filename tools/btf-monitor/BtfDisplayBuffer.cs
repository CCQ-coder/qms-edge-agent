using System;
using System.Collections.Generic;

internal sealed class BtfDisplaySnapshot
{
    internal readonly BtfSnapshot Snapshot;
    internal readonly DateTime ReceivedUtc;

    internal BtfDisplaySnapshot(BtfSnapshot snapshot, DateTime receivedUtc)
    {
        Snapshot = snapshot;
        ReceivedUtc = receivedUtc;
    }
}

internal sealed class BtfDisplayBatch
{
    internal readonly Dictionary<string, List<BtfDisplaySnapshot>> Machines;
    internal readonly List<string> Logs;

    internal BtfDisplayBatch(Dictionary<string, List<BtfDisplaySnapshot>> machines, List<string> logs)
    {
        Machines = machines;
        Logs = logs;
    }
}

internal sealed class BtfDisplayBuffer
{
    private const int MaxPendingPerMachine = 200;
    private const int MaxPendingLogs = 1000;
    private readonly object sync = new object();
    private Dictionary<string, Queue<BtfDisplaySnapshot>> machines = NewMachines();
    private Queue<string> logs = new Queue<string>();

    internal void AddSnapshot(BtfSnapshot snapshot, DateTime receivedUtc)
    {
        if (snapshot == null) throw new ArgumentNullException("snapshot");
        lock (sync)
        {
            Queue<BtfDisplaySnapshot> pending;
            if (!machines.TryGetValue(snapshot.MachineName ?? "", out pending))
            {
                pending = new Queue<BtfDisplaySnapshot>();
                machines.Add(snapshot.MachineName ?? "", pending);
            }
            pending.Enqueue(new BtfDisplaySnapshot(snapshot, receivedUtc));
            if (pending.Count > MaxPendingPerMachine) pending.Dequeue();
        }
    }

    internal void AddLog(string line)
    {
        lock (sync)
        {
            logs.Enqueue(line);
            if (logs.Count > MaxPendingLogs) logs.Dequeue();
        }
    }

    internal BtfDisplayBatch Drain()
    {
        Dictionary<string, Queue<BtfDisplaySnapshot>> takenMachines;
        Queue<string> takenLogs;
        lock (sync)
        {
            takenMachines = machines;
            takenLogs = logs;
            machines = NewMachines();
            logs = new Queue<string>();
        }
        var batch = new Dictionary<string, List<BtfDisplaySnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Queue<BtfDisplaySnapshot>> item in takenMachines)
            batch.Add(item.Key, new List<BtfDisplaySnapshot>(item.Value));
        return new BtfDisplayBatch(batch, new List<string>(takenLogs));
    }

    internal void Clear()
    {
        lock (sync)
        {
            machines = NewMachines();
            logs = new Queue<string>();
        }
    }

    private static Dictionary<string, Queue<BtfDisplaySnapshot>> NewMachines()
    {
        return new Dictionary<string, Queue<BtfDisplaySnapshot>>(StringComparer.OrdinalIgnoreCase);
    }
}
