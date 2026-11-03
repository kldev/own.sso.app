namespace Sso.Data.Marketing;

/// <summary>An e-mail template per product and language (marketing.templates), edited by an SSO admin.</summary>
public sealed class Template : IAudited
{
    public Guid Id { get; init; }

    public string Code { get; init; } = "";

    /// <summary>A short product code (e.g. great-sso): "already got this product's e-mail" groups by it.</summary>
    public string Product { get; init; } = "";

    /// <summary>pl, en or de.</summary>
    public string Language { get; init; } = "";

    public string Name { get; init; } = "";

    public string Subject { get; set; } = "";

    public string Body { get; set; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint RowVersion { get; init; }
}

/// <summary>
/// A mailing (marketing.mailings): keeps the subject and body it went out with - a later change of the template does
/// not rewrite history.
/// </summary>
public sealed class Mailing : IAudited
{
    public Guid Id { get; init; }

    public Guid TemplateId { get; init; }

    public string Subject { get; init; } = "";

    public string Body { get; init; } = "";

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// One address of a mailing (marketing.recipients): pending -> sending -> sent, or back to pending after a failed
/// attempt and failed after the last one. The sender claims a row (pending -> sending) before it talks to SMTP, so a
/// row is sent once (ADR 0014).
/// </summary>
public sealed class Recipient
{
    public Guid Id { get; init; }

    public Guid MailingId { get; init; }

    public string Email { get; init; } = "";

    public string Status { get; init; } = RecipientStatuses.Pending;

    public int Attempts { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset? SentAt { get; init; }

    /// <summary>After a failed attempt the row waits before the next one.</summary>
    public DateTimeOffset NextAttemptAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public static class RecipientStatuses
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";

    public static readonly string[] All = [Pending, Sending, Sent, Failed];
}

public static class Languages
{
    public static readonly string[] All = ["pl", "en", "de"];
}
