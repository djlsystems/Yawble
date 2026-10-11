namespace Harness.Contracts;

public enum TriggerKind
{
    Cron,
    Every,
    Once,

    /// <summary>
    /// Fired by a message on the log rather than by a clock.
    ///
    /// A FOURTH VALUE ON THE EXISTING ENUM, not a second discriminator beside it. `schedules.kind`
    /// (now `triggers.kind`) already held cron/every/once; a separate "is this a schedule or an
    /// event" column would be two stores of one fact, and every existing row would have needed
    /// backfilling into it.
    /// </summary>
    Event,

    /// <summary>
    /// Polls a folder and publishes `file.changed` when it changes. It never delivers directly: the
    /// row also carries `event_type = file.changed`, so the event arm above delivers what this one
    /// publishes - filters, team pause, busy-skip, causation and the feed stay one code path.
    /// </summary>
    FolderChange,
}
