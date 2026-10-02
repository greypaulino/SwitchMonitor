namespace SwitchMonitor.Wpf;

internal enum ReturnSyncDecision { Wait, Cancel, Synchronize }

// A menu-resend is permitted only after this computer has lost DDC control
// and then reads the exact input it showed before switching away.
internal sealed class ReturnSyncState(int previousInput)
{
    private bool disconnected;
    public int PreviousInput { get; } = previousInput;

    public ReturnSyncDecision Observe(int currentInput)
    {
        if (currentInput == 0)
        {
            disconnected = true;
            return ReturnSyncDecision.Wait;
        }
        if (!disconnected) return ReturnSyncDecision.Wait;
        return currentInput == PreviousInput
            ? ReturnSyncDecision.Synchronize : ReturnSyncDecision.Cancel;
    }
}
