using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Sso.Api.Messaging;

/// <summary>RabbitMQ of the shared infrastructure, vhost of Sso. Section "RabbitMq".</summary>
// Set by the configuration binder (BindConfiguration).
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

    /// <summary>A secret: environment variable RabbitMq__Password (appsettings.Development.json locally).</summary>
    [Required]
    public required string Password { get; init; }
}

public static class RabbitMqSetup
{
    public static WebApplicationBuilder AddRabbitMq(this WebApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<RabbitMqOptions>()
            .BindConfiguration(RabbitMqOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
            return new ConnectionFactory
            {
                HostName = o.Host,
                Port = o.Port,
                VirtualHost = o.VirtualHost,
                UserName = o.UserName,
                Password = o.Password,
                ClientProvidedName = "sso-api",
            };
        });
        return builder;
    }
}

/// <summary>Readiness: the broker accepts our login on our vhost. Messaging itself comes with the first module that needs it.</summary>
internal sealed class RabbitMqHealthCheck(ConnectionFactory factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var connection = await factory.CreateConnectionAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (BrokerUnreachableException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"RabbitMQ {factory.HostName}:{factory.Port} vhost '{factory.VirtualHost}' is not reachable.",
                ex
            );
        }
    }
}
