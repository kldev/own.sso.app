using System.Text.Json;

namespace Sso.Data.Audit;

/// <summary>
/// One change of an audited table (audit.change_log), written by <see cref="AuditInterceptor"/> in the transaction of
/// the change. The API role may only insert (ADR 0009).
/// </summary>
public sealed class ChangeLogEntry
{
    public long Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>txid_current() - entries of one transaction belong together.</summary>
    public long TransactionId { get; init; }

    public string TableSchema { get; init; } = "";

    public string TableName { get; init; } = "";

    /// <summary>INSERT, UPDATE or DELETE.</summary>
    public string Operation { get; init; } = "";

    public string? ActorId { get; init; }

    /// <summary>session_user - which database role made the change (the database fills it in).</summary>
    public string? DbRole { get; init; }

    public JsonDocument? OldRow { get; init; }

    public JsonDocument? NewRow { get; init; }
}
