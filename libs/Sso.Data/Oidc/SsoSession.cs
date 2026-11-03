using System.Net;

namespace Sso.Data.Oidc;

/// <summary>
/// A browser's SSO session (oidc.sessions): the cookie holds a random value, the table its hash. Each use moves
/// <see cref="IdleExpiresAt"/>, never past <see cref="AbsoluteExpiresAt"/> (14 days idle, 30 days at most - ADR 0011).
/// </summary>
public sealed class SsoSession
{
    public Guid Id { get; init; }

    public byte[] TokenHash { get; init; } = [];

    public Guid UserId { get; init; }

    public string Provider { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset IdleExpiresAt { get; set; }

    public DateTimeOffset AbsoluteExpiresAt { get; init; }

    public DateTimeOffset? EndedAt { get; set; }

    /// <summary><see cref="SessionEndReasons"/>.</summary>
    public string? EndReason { get; set; }

    public IPAddress? Ip { get; init; }

    public string? UserAgent { get; init; }
}

/// <summary>Why a session ended.</summary>
public static class SessionEndReasons
{
    public const string Idle = "idle";
    public const string Absolute = "absolute";
    public const string Admin = "admin";
    public const string UserInactive = "user_inactive";
    public const string Unlinked = "unlinked";

    public static readonly string[] All = [Idle, Absolute, Admin, UserInactive, Unlinked];
}
