namespace Sso.Messaging.Contracts;

/// <summary>RabbitMQ queues (vhost sso): each background process listens only to its own.</summary>
public static class Queues
{
    /// <summary>What the runner does: the integration events between modules.</summary>
    public const string Runner = "sso-runner";
}
