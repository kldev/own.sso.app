using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;

namespace Sso.Runner.Outbox;

/// <summary>The relay of messaging.outbox_messages. Bound from "Outbox" (Outbox__PollInterval, ...).</summary>
// Set by the configuration binder.
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>The wait after a pass that found nothing to hand on.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Rows per transaction.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; init; } = 100;

    /// <summary>A row this version cannot read is tried this often, then left for a person (last_error).</summary>
    [Range(1, 1000)]
    public int MaxAttempts { get; init; } = 10;

    /// <summary>Processed rows stay this long, then the relay deletes them.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "365.00:00:00")]
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
}
