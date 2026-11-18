namespace StardewCompanion.Mod;

// Execution timeouts exclude time spent acknowledging an incidental story prompt.
// This clock never changes the native game clock.
internal sealed class ExecutionClock
{
    private long? _pausedAt;
    private long _pausedMilliseconds;
    internal long Now=>(_pausedAt??Environment.TickCount64)-_pausedMilliseconds;
    internal void Pause()=>_pausedAt??=Environment.TickCount64;
    internal void Resume()
    {
        if(_pausedAt is not {} started)return;
        _pausedMilliseconds+=Environment.TickCount64-started;
        _pausedAt=null;
    }
}
