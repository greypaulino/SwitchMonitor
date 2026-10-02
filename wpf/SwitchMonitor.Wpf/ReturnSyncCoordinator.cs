namespace SwitchMonitor.Wpf;

// Intel/LG-specific return synchronization. Commands are never sent merely
// because a timer elapsed: loss of control, a matching reading, identity and
// a second matching reading are all required.
internal sealed class ReturnSyncCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Watch> watches = [];
    private readonly Action<string> report;
    private bool disposed;

    public ReturnSyncCoordinator(Action<string> report) => this.report = report;

    public void Arm(PhysicalMonitor monitor, int previous, int requested)
    {
        if (!monitor.IsLegacyLg || !monitor.HasIntelLegacyBackend) return;
        Cancel(monitor.Key);
        if (previous is not (17 or 18 or 15) || previous == requested) return;
        var watch = new Watch(monitor.Key, monitor.DisplayName, new ReturnSyncState(previous));
        lock (gate)
        {
            if (disposed) return;
            watches[watch.Key] = watch;
        }
        _ = ObserveAsync(watch);
    }

    public void Cancel(string key)
    {
        lock (gate)
        {
            if (!watches.Remove(key, out var watch)) return;
            watch.Token.Cancel();
        }
    }

    private bool IsCurrent(Watch watch)
    {
        lock (gate)
            return !disposed && watches.TryGetValue(watch.Key, out var current) &&
                ReferenceEquals(current, watch) && !watch.Token.IsCancellationRequested;
    }

    private async Task ObserveAsync(Watch watch)
    {
        try
        {
            while (IsCurrent(watch))
            {
                await Task.Delay(200, watch.Token.Token);
                int current = await Task.Run(() => ReadIntelInput(watch.DisplayName), watch.Token.Token);
                if (!IsCurrent(watch)) return;
                switch (watch.State.Observe(current))
                {
                    case ReturnSyncDecision.Wait: continue;
                    case ReturnSyncDecision.Cancel: Cancel(watch.Key); return;
                    case ReturnSyncDecision.Synchronize:
                        bool sent = await Task.Run(() => VerifyAndResend(watch), watch.Token.Token);
                        if (sent) report($"Resent {MonitorService.PortName(watch.State.PreviousInput)} to synchronize the LG menu.");
                        Cancel(watch.Key);
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Cancel(watch.Key);
            report("Return synchronization stopped: " + error.Message);
        }
        finally { watch.Token.Dispose(); }
    }

    private static int ReadIntelInput(string displayName)
    {
        try
        {
            var value = StaRunner.Run(() =>
            {
                using var intel = new IntelLegacyDdc(displayName);
                return intel.GetVcp(0x60).Current;
            });
            return value is 17 or 18 or 15 ? (int)value : 0;
        }
        catch { return 0; }
    }

    private bool VerifyAndResend(Watch watch)
    {
        var found = MonitorService.Enumerate();
        try
        {
            var matching = found.Where(m => m.Key == watch.Key &&
                string.Equals(m.DisplayName, watch.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                m.IsLegacyLg && m.HasIntelLegacyBackend).ToArray();
            if (matching.Length != 1 || !IsCurrent(watch)) return false;
            var monitor = matching[0];
            if (monitor.ReadInput().Current != watch.State.PreviousInput || !IsCurrent(watch)) return false;
            monitor.SetInput(watch.State.PreviousInput);
            return true;
        }
        finally { foreach (var monitor in found) monitor.Dispose(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var watch in watches.Values) watch.Token.Cancel();
            watches.Clear();
        }
    }

    private sealed record Watch(string Key, string DisplayName, ReturnSyncState State)
    {
        public CancellationTokenSource Token { get; } = new();
    }
}
