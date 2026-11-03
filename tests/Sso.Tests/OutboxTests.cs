using JetBrains.Annotations;
using Sso.Data.Identity;
using Sso.Data.Messaging;
using Sso.Messaging.Contracts;

namespace Sso.Tests;

/// <summary>A message only the tests publish.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized into the outbox
public sealed record TestPing(Guid Id);

/// <summary>
/// The transactional outbox (ADR 0010): a message is saved with its change and rolled back with it; the relay locks
/// pending rows so two relays never take the same one, marks the published ones processed and counts an attempt for
/// a type it does not know; the retention deletes processed rows only.
/// </summary>
[Trait("verify", "outbox")]
[Collection(ApiCollection.Name)]
public sealed class OutboxTests(ApiFixture api)
{
    private const string Type = "test-ping";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static OutboxTests() => OutboxMessageTypes.Register<TestPing>(Type);

    private string Host => api.ConnectionString("sso", "sso_api");

    private string Runner => api.ConnectionString("sso", "sso_runner");

    private static Person NewPerson()
    {
        var name = $"o.{Guid.NewGuid():N}"[..14];
        return new Person
        {
            Id = Guid.CreateVersion7(),
            Username = name,
            Email = $"{name}@test.example",
            DisplayName = "Outbox",
            GithubEmail = $"{name}@github.example",
        };
    }

    [Fact]
    public async Task A_message_is_saved_with_its_change_and_rolled_back_with_it()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = TestDb.Context(Host))
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            var person = NewPerson();
            db.People.Add(person);
            var message = Outbox.Enqueue(db, new TestPing(person.Id), now);
            await db.SaveChangesAsync(Ct);
            await tx.RollbackAsync(Ct);
            Assert.Equal(
                0L,
                await api.ScalarAsync<long>(
                    $"SELECT count(*) FROM messaging.outbox_messages WHERE id = '{message.Id}'"
                )
            );
            Assert.Equal(
                0L,
                await api.ScalarAsync<long>(
                    $"SELECT count(*) FROM identity.users WHERE id = '{person.Id}'"
                )
            );
        }

        await using (var db = TestDb.Context(Host))
        {
            var person = NewPerson();
            db.People.Add(person);
            var message = Outbox.Enqueue(db, new TestPing(person.Id), now);
            await db.SaveChangesAsync(Ct);
            Assert.Equal(
                Type,
                await api.ScalarAsync<string>(
                    $"SELECT message_type::text FROM messaging.outbox_messages WHERE id = '{message.Id}'"
                )
            );
            Assert.Equal(
                person.Id.ToString(),
                await api.ScalarAsync<string>(
                    $"SELECT payload->>'id' FROM messaging.outbox_messages WHERE id = '{message.Id}'"
                )
            );
        }
    }

    [Fact]
    public async Task Two_relays_never_take_the_same_message()
    {
        await using (var db = TestDb.Context(Host))
        {
            for (var i = 0; i < 4; i++)
                Outbox.Enqueue(db, new TestPing(Guid.CreateVersion7()), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        await using var first = TestDb.Context(Runner);
        await using var firstTx = await first.Database.BeginTransactionAsync(Ct);
        var a = await OutboxBatch.ClaimAsync(first, 2, 5, Ct);

        await using var second = TestDb.Context(Runner);
        await using var secondTx = await second.Database.BeginTransactionAsync(Ct);
        var b = await OutboxBatch.ClaimAsync(second, 100, 5, Ct);

        Assert.Equal(2, a.Count);
        Assert.NotEmpty(b);
        Assert.Empty(a.Select(m => m.Id).Intersect(b.Select(m => m.Id)));
        await secondTx.RollbackAsync(Ct);
        await firstTx.RollbackAsync(Ct);
    }

    [Fact]
    public async Task The_relay_marks_published_messages_processed_and_counts_an_unknown_type()
    {
        var unknown = Guid.CreateVersion7();
        await api.ExecuteAsync(
            $"INSERT INTO messaging.outbox_messages (id, message_type, payload) VALUES ('{unknown}', 'from-a-newer-version', '{{}}')"
        );
        await using (var db = TestDb.Context(Host))
        {
            Outbox.Enqueue(db, new TestPing(Guid.CreateVersion7()), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        var published = new List<object>();
        await using (var db = TestDb.Context(Runner))
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            var rows = await OutboxBatch.ClaimAsync(db, 1000, 5, Ct);
            await OutboxBatch.ProcessAsync(
                rows,
                DateTimeOffset.UtcNow,
                (_, message) =>
                {
                    published.Add(message);
                    return Task.CompletedTask;
                }
            );
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        }

        Assert.Contains(published, m => m is TestPing);
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM messaging.outbox_messages WHERE message_type = '{Type}' AND processed_at IS NULL"
            )
        );
        Assert.Equal(
            "Unknown message type from-a-newer-version.",
            await api.ScalarAsync<string>(
                $"SELECT last_error::text FROM messaging.outbox_messages WHERE id = '{unknown}' AND processed_at IS NULL AND attempts = 1"
            )
        );
    }

    [Fact]
    public async Task The_retention_deletes_only_processed_messages_past_it()
    {
        var old = Guid.CreateVersion7();
        var recent = Guid.CreateVersion7();
        var pending = Guid.CreateVersion7();
        await api.ExecuteAsync(
            $$"""
            INSERT INTO messaging.outbox_messages (id, message_type, payload, processed_at) VALUES
            ('{{old}}', '{{Type}}', '{}', now() - interval '30 days'),
            ('{{recent}}', '{{Type}}', '{}', now()),
            ('{{pending}}', '{{Type}}', '{}', NULL)
            """
        );
        await using var db = TestDb.Context(Runner);
        await OutboxBatch.DeleteProcessedAsync(db, DateTimeOffset.UtcNow.AddDays(-7), Ct);
        foreach (var (id, kept) in new[] { (old, false), (recent, true), (pending, true) })
            Assert.Equal(
                kept ? 1L : 0L,
                await api.ScalarAsync<long>(
                    $"SELECT count(*) FROM messaging.outbox_messages WHERE id = '{id}'"
                )
            );
    }
}
