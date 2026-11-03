using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;

namespace Sso.Runner.Messaging;

/// <summary>RabbitMQ of the shared infrastructure, vhost of Sso - the host's section "RabbitMq", the same keys.</summary>
// Set by the configuration binder.
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.Members)]
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required]
    public required string Host { get; init; }

    [Range(1, 65535)]
    public int Port { get; init; } = 5672;

    [Required]
    public string VirtualHost { get; init; } = "sso";

    [Required]
    public required string UserName { get; init; }

    /// <summary>A secret: environment variable RabbitMq__Password.</summary>
    [Required]
    public required string Password { get; init; }

    public Uri AmqpUri() =>
        new UriBuilder("amqp", Host, Port, Uri.EscapeDataString(VirtualHost))
        {
            UserName = Uri.EscapeDataString(UserName),
            Password = Uri.EscapeDataString(Password),
        }.Uri;
}
