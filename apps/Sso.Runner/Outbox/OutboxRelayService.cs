using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Sso.Runner.Outbox;

/// <summary>Runs the relay while the runner runs: batch after batch, a pause when nothing is pending.</summary>
// Created by the host (AddHostedService in OutboxSetup).
[UsedImplicitly]
internal sealed class OutboxRelayService(
    OutboxRelay relay,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelayService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await relay.RelayAsync(stoppingToken) > 0)
                    continue;
                await relay.DeleteProcessedAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The database is away or a batch failed: the rows stay pending, the next pass tries again.
                logger.LogError(exception, "Outbox relay pass failed");
            }

            await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
        }
    }
}

public static class OutboxSetup
{
    public static IServiceCollection AddOutboxRelay(this IServiceCollection services)
    {
        services
            .AddOptions<OutboxOptions>()
            .BindConfiguration(OutboxOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<OutboxRelay>();
        services.AddHostedService<OutboxRelayService>();
        return services;
    }
}
