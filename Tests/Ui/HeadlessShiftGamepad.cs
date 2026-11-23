namespace FH6Telemetry.Testing;

internal sealed class HeadlessShiftGamepad : IShiftGamepad
{
    private int _pressCount;
    public int PressCount => Volatile.Read(ref _pressCount);
    public ShiftGamepadReport LastPress { get; private set; }
    public bool Disposed { get; private set; }

    public void Submit(ShiftGamepadReport report)
    {
        if (report == default) return;
        LastPress = report;
        Interlocked.Increment(ref _pressCount);
    }

    public void Dispose() => Disposed = true;
}
