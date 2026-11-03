using System.ComponentModel.DataAnnotations;
using JasperFx;
using Sso.Messaging.Contracts;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace Sso.Runner.Messaging;

public static class MessagingSetup
{
    /// <summary>Wolverine's message storage: the schema wolverine, its tables from Liquibase.</summary>
    private const string Schema = "wolverine";

    /// <summary>
    /// Wolverine on RabbitMQ (ADR 0010): the relay publishes the outbox's messages to the runner's
    /// own queue, the handlers of this assembly take them from there. The inbox, outbox and dead letters live in
    /// PostgreSQL; their tables are Liquibase changesets, so Wolverine only checks them at start and creates nothing.
    /// </summary>
    public static WebApplicationBuilder AddRunnerMessaging(
        this WebApplicationBuilder builder,
        string connectionString
    )
    {
        var rabbitMq =
            builder.Configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>()
            ?? throw new InvalidOperationException($"{RabbitMqOptions.SectionName} is not set.");
        Validator.ValidateObject(rabbitMq, new ValidationContext(rabbitMq), true);

        builder.Host.UseWolverine(options =>
        {
            options.ApplicationAssembly = typeof(MessagingSetup).Assembly;
            options.ServiceName = "sso-runner";
            options.PersistMessagesWithPostgresql(connectionString, Schema);
            options.AutoBuildMessageStorageOnStartup = AutoCreate.None;

            // The queue belongs to the code that uses it: declared at start, in every environment.
            options.UseRabbitMq(rabbitMq.AmqpUri()).AutoProvision();
            // Every integration event of the outbox goes to the runner's own queue; its handlers change the module that
            // reacts, through EF Core (ADR 0010).
            options.PublishAllMessages().ToRabbitQueue(Queues.Runner).UseDurableOutbox();
            // One message at a time: the runner's work is small, and order keeps it predictable.
            options.ListenToRabbitQueue(Queues.Runner).UseDurableInbox().Sequential();
            // An exception is the infrastructure (the broker away, the database busy): try again later, then the dead
            // letters (wolverine.wolverine_dead_letters) for a person to look at.
            options
                .Policies.OnException<Exception>()
                .RetryWithCooldown(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromMinutes(1)
                )
                .Then.MoveToErrorQueue();
        });

        return builder;
    }
}
