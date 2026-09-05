namespace Gardener.Scheduler;

/// <summary>Frame-driven state machine states for one sweep, one bed at a time. Each Task_ file sets
/// the next state in its own final step (ICE/SealHunter SchedulerMain pattern); <see cref="SchedulerMain"/>
/// dispatches whichever state it finds once the task queue is empty.</summary>
public enum GardenerState
{
    Idle,
    Scanning,
    OpeningBed,
    Acting,
    ClosingMenu,
    PausedForPlayer,
    Done,
    Error,
}
