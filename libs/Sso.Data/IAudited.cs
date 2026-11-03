namespace Sso.Data;

/// <summary>
/// An entity whose every insert, update and delete is written to audit.change_log in the same transaction, with the
/// actor and the old and new row (ADR 0009). Properties listed in <see cref="SsoDbContext.NotAudited"/> (secrets) are
/// left out.
/// </summary>
public interface IAudited;

/// <summary>Who acts in the current request or job - written with every audit entry and event. Null without one.</summary>
public interface IActorAccessor
{
    Guid? ActorId { get; }
}

/// <summary>No actor: background work of the runner, which changes no audited table.</summary>
public sealed class NoActor : IActorAccessor
{
    public Guid? ActorId => null;
}
