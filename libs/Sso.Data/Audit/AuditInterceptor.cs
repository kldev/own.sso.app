using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Sso.Data.Audit;

/// <summary>
/// The audit log in the application (ADR 0009): before a save, every added, changed or deleted <see cref="IAudited"/>
/// entity gets a <see cref="ChangeLogEntry"/> in the same SaveChanges - so in the same transaction - with the actor
/// and the old and new row by column name, without the columns of <see cref="SsoDbContext.NotAudited"/>. Bulk
/// statements (ExecuteUpdate/ExecuteDelete) bypass it, so audited tables are changed through tracked entities only.
/// </summary>
public sealed class AuditInterceptor(IActorAccessor actor, TimeProvider time)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result
    )
    {
        if (eventData.Context is { } context)
            AddEntries(context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (eventData.Context is { } context)
            AddEntries(context);
        return ValueTask.FromResult(result);
    }

    private void AddEntries(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        var entries = context
            .ChangeTracker.Entries()
            .Where(e =>
                e
                    is {
                        Entity: IAudited,
                        State: EntityState.Added or EntityState.Modified or EntityState.Deleted,
                    }
            )
            .ToList();
        if (entries.Count == 0)
            return;
        var now = time.GetUtcNow();
        var actorId = actor.ActorId?.ToString();
        foreach (var entry in entries)
        {
            // An update that changes nothing but the row version is no change.
            if (entry.State == EntityState.Modified && !entry.Properties.Any(IsRealChange))
                continue;
            var table = entry.Metadata.GetTableName() ?? entry.Metadata.ShortName();
            context.Add(
                new ChangeLogEntry
                {
                    OccurredAt = now,
                    TableSchema = entry.Metadata.GetSchema() ?? "public",
                    TableName = table,
                    Operation = entry.State switch
                    {
                        EntityState.Added => "INSERT",
                        EntityState.Modified => "UPDATE",
                        _ => "DELETE",
                    },
                    ActorId = actorId,
                    OldRow = entry.State is EntityState.Modified or EntityState.Deleted
                        ? Row(entry, original: true)
                        : null,
                    NewRow = entry.State is EntityState.Added or EntityState.Modified
                        ? Row(entry, original: false)
                        : null,
                }
            );
        }
    }

    private static bool IsRealChange(PropertyEntry property) =>
        property.IsModified && !SsoDbContext.NotAudited.Contains(Column(property.Metadata));

    private static string Column(IProperty property) =>
        property.GetColumnName(
            StoreObjectIdentifier.Table(
                property.DeclaringType.GetTableName()!,
                property.DeclaringType.GetSchema()
            )
        ) ?? property.Name;

    private static JsonDocument Row(EntityEntry entry, bool original)
    {
        var row = new Dictionary<string, object?>();
        foreach (var property in entry.Properties)
        {
            var column = Column(property.Metadata);
            if (SsoDbContext.NotAudited.Contains(column))
                continue;
            row[column] = original ? property.OriginalValue : property.CurrentValue;
        }
        return JsonSerializer.SerializeToDocument(row);
    }
}
