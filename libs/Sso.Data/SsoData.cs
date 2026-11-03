using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sso.Data.Audit;

namespace Sso.Data;

public static class SsoData
{
    /// <summary>
    /// Npgsql's options of the model - the same for the host, the runner, the tests and dotnet ef. Without a data
    /// source (design time) only the model is built.
    /// </summary>
    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder options,
        DbDataSource? dataSource
    )
    {
        if (dataSource is null)
            options.UseNpgsql(o => o.MigrationsAssembly(typeof(SsoDbContext).Assembly.FullName));
        else
            options.UseNpgsql(
                dataSource,
                o => o.MigrationsAssembly(typeof(SsoDbContext).Assembly.FullName)
            );
        return options
            .UseSnakeCaseNamingConvention()
            // The schema is Liquibase's; a forgotten migration is caught by the tests, never at run time.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    /// <summary>
    /// The data source (no Kerberos: without GssEncryptionMode.Disable Npgsql probes GSS first and logs a missing
    /// libgssapi_krb5 in the aspnet image) and a pooled factory of <see cref="SsoDbContext"/> with the audit
    /// interceptor; the context itself is scoped. Needs <see cref="IActorAccessor"/> and <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddSsoData(
        this IServiceCollection services,
        string connectionString,
        string applicationName
    )
    {
        var dataSource = new NpgsqlDataSourceBuilder(connectionString)
        {
            ConnectionStringBuilder =
            {
                ApplicationName = applicationName,
                GssEncryptionMode = GssEncryptionMode.Disable,
            },
        }.Build();
        services.AddSingleton(dataSource);
        services.AddSingleton<AuditInterceptor>();
        services.AddDbContextFactory<SsoDbContext>(
            (sp, options) =>
                Configure(options, dataSource)
                    .AddInterceptors(sp.GetRequiredService<AuditInterceptor>())
        );
        return services;
    }
}
