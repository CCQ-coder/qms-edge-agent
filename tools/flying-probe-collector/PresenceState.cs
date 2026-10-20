using System;
using System.Collections.Generic;

internal sealed class PresenceReport
{
    public string MachineName { get; set; }
    public string HostName { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string NetworkState { get; set; }
    public string SourceState { get; set; }
    public DateTimeOffset? LastTxtChangeAt { get; set; }
}

internal sealed class PresenceTracker
{
    private sealed class State
    {
        public string NetworkState = "UNKNOWN";
        public int Failures;
        public DateTime LastObservedUtc;
    }

    private readonly int failureThreshold;
    private readonly Dictionary<string, State> states = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

    public PresenceTracker(int failureThreshold)
    {
        if (failureThreshold < 1 || failureThreshold > 20) throw new ArgumentOutOfRangeException("failureThreshold");
        this.failureThreshold = failureThreshold;
    }

    public string Observe(string machine, bool? reachable, DateTime observedUtc)
    {
        State state;
        if (!states.TryGetValue(machine, out state))
        {
            state = new State();
            states[machine] = state;
        }
        if (state.LastObservedUtc != default(DateTime) && observedUtc < state.LastObservedUtc)
            return state.NetworkState;
        state.LastObservedUtc = observedUtc;
        if (!reachable.HasValue)
        {
            state.NetworkState = "UNKNOWN";
            state.Failures = 0;
        }
        else if (reachable.Value)
        {
            state.NetworkState = "ONLINE";
            state.Failures = 0;
        }
        else
        {
            state.Failures++;
            if (state.Failures >= failureThreshold) state.NetworkState = "OFFLINE";
        }
        return state.NetworkState;
    }
}
