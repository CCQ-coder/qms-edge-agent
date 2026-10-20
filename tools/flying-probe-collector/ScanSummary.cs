using System;
using System.Collections.Generic;

internal sealed class SourceSnapshot
{
    public string MachineName { get; set; }
    public string SourceState { get; set; }
    public DateTime? LastScanSuccessUtc { get; set; }
    public DateTime? LastTxtChangeUtc { get; set; }
}

internal sealed class ScanSummary
{
    private sealed class MachineObservation
    {
        public DateTime? LastReadableUtc;
        public DateTime? LastUnreadableUtc;
        public DateTime? LastTxtChangeUtc;
    }

    private struct FileObservation
    {
        public long Length;
        public DateTime LastWriteUtc;
    }

    private readonly object gate = new object();
    private readonly Dictionary<string, MachineObservation> machines =
        new Dictionary<string, MachineObservation>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileObservation> files =
        new Dictionary<string, FileObservation>(StringComparer.OrdinalIgnoreCase);
    private DateTime? lastRootReadableUtc;
    private DateTime? lastRootUnreadableUtc;

    public void MarkRootReadable(DateTime observedUtc, IEnumerable<string> presentMachines = null)
    {
        lock (gate)
        {
            lastRootReadableUtc = observedUtc;
            if (presentMachines == null) return;
            HashSet<string> present = new HashSet<string>(presentMachines, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, MachineObservation> machine in machines)
                if (!present.Contains(machine.Key)) machine.Value.LastUnreadableUtc = observedUtc;
        }
    }

    public void MarkRootUnreadable(DateTime observedUtc)
    {
        lock (gate) lastRootUnreadableUtc = observedUtc;
    }

    public void MarkReadable(string machine, DateTime observedUtc)
    {
        lock (gate) GetOrCreate(machine).LastReadableUtc = observedUtc;
    }

    public void MarkUnreadable(string machine, DateTime observedUtc)
    {
        lock (gate) GetOrCreate(machine).LastUnreadableUtc = observedUtc;
    }

    public void ObserveFile(string machine, string file, long length, DateTime lastWriteUtc, DateTime observedUtc)
    {
        lock (gate)
        {
            MachineObservation machineState = GetOrCreate(machine);
            string key = machine + "/" + file;
            FileObservation previous;
            if (files.TryGetValue(key, out previous) &&
                (previous.Length != length || previous.LastWriteUtc != lastWriteUtc))
                machineState.LastTxtChangeUtc = observedUtc;
            files[key] = new FileObservation { Length = length, LastWriteUtc = lastWriteUtc };
        }
    }

    public SourceSnapshot Get(string machine, DateTime nowUtc)
    {
        lock (gate)
        {
            MachineObservation state;
            machines.TryGetValue(machine, out state);
            string source = "UNKNOWN";
            DateTime? lastSuccess = state == null ? null : state.LastReadableUtc;
            if (IsRecent(lastRootUnreadableUtc, nowUtc) &&
                (!lastRootReadableUtc.HasValue || lastRootUnreadableUtc >= lastRootReadableUtc))
                source = "UNREADABLE";
            else if (state != null)
            {
                if (IsRecent(state.LastUnreadableUtc, nowUtc) &&
                    (!state.LastReadableUtc.HasValue || state.LastUnreadableUtc >= state.LastReadableUtc))
                    source = "UNREADABLE";
                else if (IsRecent(state.LastReadableUtc, nowUtc) && IsRecent(lastRootReadableUtc, nowUtc))
                    source = "READABLE";
            }
            return new SourceSnapshot {
                MachineName = machine,
                SourceState = source,
                LastScanSuccessUtc = lastSuccess,
                LastTxtChangeUtc = state == null ? null : state.LastTxtChangeUtc
            };
        }
    }

    public IDictionary<string, SourceSnapshot> Snapshot(DateTime nowUtc)
    {
        lock (gate)
        {
            Dictionary<string, SourceSnapshot> result =
                new Dictionary<string, SourceSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (string machine in machines.Keys) result[machine] = Get(machine, nowUtc);
            return result;
        }
    }

    private MachineObservation GetOrCreate(string machine)
    {
        MachineObservation state;
        if (!machines.TryGetValue(machine, out state))
        {
            state = new MachineObservation();
            machines[machine] = state;
        }
        return state;
    }

    private static bool IsRecent(DateTime? when, DateTime nowUtc)
    {
        return when.HasValue && when.Value <= nowUtc && (nowUtc - when.Value).TotalSeconds <= 300;
    }
}
