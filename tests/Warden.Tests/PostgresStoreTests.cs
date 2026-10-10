using Xunit;
using static Warden.Tests.T;

namespace Warden.Tests;

[Collection("postgres")]
public class PostgresStoreTests(PostgresFixture pg)
{
    [Fact]
    public async Task Written_relationships_are_read_back_at_their_revision()
    {
        var (_, store) = await pg.CreateStoreAsync();

        var r1 = await store.WriteAsync([Touch("document:d", "viewer", "user:ayse"), Touch("document:d", "viewer", "group:eng#member")], default);
        var subjects = await store.ReadSubjectsAsync(O("document:d"), "viewer", r1, default);

        Assert.Equal(1, r1);
        Assert.Equal(["group:eng#member", "user:ayse"], subjects.Select(s => s.ToString()).Order());
        Assert.Empty(await store.ReadSubjectsAsync(O("document:d"), "viewer", 0, default));
    }

    [Fact]
    public async Task Touching_an_existing_relationship_or_deleting_a_missing_one_does_not_advance_the_revision()
    {
        var (_, store) = await pg.CreateStoreAsync();
        var r1 = await store.WriteAsync([Touch("document:d", "owner", "user:a")], default);

        var again = await store.WriteAsync([Touch("document:d", "owner", "user:a")], default);
        var missing = await store.WriteAsync([Delete("document:d", "owner", "user:zzz")], default);

        Assert.Equal(r1, again);
        Assert.Equal(r1, missing);
        Assert.Equal(r1, await store.HeadRevisionAsync(default));
    }

    [Fact]
    public async Task Deleting_and_re_adding_keeps_a_correct_history_for_every_revision()
    {
        var (_, store) = await pg.CreateStoreAsync();
        var r1 = await store.WriteAsync([Touch("document:d", "viewer", "user:a")], default);
        var r2 = await store.WriteAsync([Delete("document:d", "viewer", "user:a")], default);
        var r3 = await store.WriteAsync([Touch("document:d", "viewer", "user:a")], default);

        Assert.Single(await store.ReadSubjectsAsync(O("document:d"), "viewer", r1, default));
        Assert.Empty(await store.ReadSubjectsAsync(O("document:d"), "viewer", r2, default));
        Assert.Single(await store.ReadSubjectsAsync(O("document:d"), "viewer", r3, default));
    }

    [Fact]
    public async Task Order_inside_one_write_is_respected()
    {
        var (_, store) = await pg.CreateStoreAsync();

        var revision = await store.WriteAsync([Touch("document:d", "viewer", "user:a"), Delete("document:d", "viewer", "user:a"), Touch("document:d", "viewer", "user:b")], default);

        Assert.Equal(["user:b"], (await store.ReadSubjectsAsync(O("document:d"), "viewer", revision, default)).Select(s => s.ToString()));
    }

    [Fact]
    public async Task Many_relationships_in_one_write_share_a_single_revision()
    {
        var (_, store) = await pg.CreateStoreAsync();
        var updates = Enumerable.Range(0, 5_000).Select(i => Touch($"document:d{i}", "viewer", $"user:u{i % 50}")).ToList();

        var revision = await store.WriteAsync(updates, default);

        Assert.Equal(1, revision);
        Assert.Equal(5_000, (await store.ReadAsync(new RelationshipFilter(ObjectType: "document"), revision, 10_000, default)).Count);
    }

    [Fact]
    public async Task Concurrent_writers_get_distinct_gapless_revisions_and_every_snapshot_is_consistent()
    {
        var (_, store) = await pg.CreateStoreAsync();

        var revisions = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () => (long)await store.WriteAsync([Touch($"document:d{i}", "owner", $"user:u{i}")], default))));

        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), revisions.Order());
        for (var revision = 1; revision <= 40; revision++)
        {
            var visible = await store.ReadAsync(new RelationshipFilter(ObjectType: "document"), revision, 1_000, default);
            Assert.Equal(revision, visible.Count);
        }
    }

    [Fact]
    public async Task The_changes_feed_lists_touches_and_deletes_in_revision_order()
    {
        var (_, store) = await pg.CreateStoreAsync();
        await store.WriteAsync([Touch("document:d", "viewer", "user:a")], default);
        await store.WriteAsync([Touch("document:d", "viewer", "user:b"), Delete("document:d", "viewer", "user:a")], default);

        var all = await store.ReadChangesAsync(0, 100, default);
        var afterFirst = await store.ReadChangesAsync(1, 100, default);

        Assert.Equal(
            [(1L, ChangeOp.Touch, "user:a"), (2L, ChangeOp.Touch, "user:b"), (2L, ChangeOp.Delete, "user:a")],
            all.Select(c => (c.Revision, c.Op, c.Relationship.Subject.ToString())));
        Assert.Equal(2, afterFirst.Count);
    }

    [Fact]
    public async Task Filters_narrow_what_is_read()
    {
        var (_, store) = await pg.CreateStoreAsync();
        var revision = await store.WriteAsync(
        [
            Touch("document:a", "viewer", "user:x"),
            Touch("document:a", "owner", "user:y"),
            Touch("document:b", "viewer", "user:x"),
            Touch("folder:f", "viewer", "user:x"),
        ], default);

        Assert.Equal(2, (await store.ReadAsync(new RelationshipFilter(ObjectType: "document", Relation: "viewer"), revision, 100, default)).Count);
        Assert.Single(await store.ReadAsync(new RelationshipFilter(ObjectType: "document", ObjectId: "a", Relation: "owner"), revision, 100, default));
        Assert.Equal(3, (await store.ReadAsync(new RelationshipFilter(SubjectType: "user", SubjectId: "x"), revision, 100, default)).Count);
        Assert.Single(await store.ReadAsync(new RelationshipFilter(), revision, 1, default));
    }

    [Fact]
    public async Task The_checker_gives_the_same_answers_on_postgres_as_on_the_in_memory_store()
    {
        var (_, postgres) = await pg.CreateStoreAsync();
        var memory = new InMemoryRelationshipStore();
        var schema = Schema.Parse(Schemas.Docs);
        var updates = new List<RelationshipUpdate>
        {
            Touch("group:eng", "member", "user:ayse"),
            Touch("group:all", "member", "group:eng#member"),
            Touch("folder:root", "owner", "user:mehmet"),
            Touch("folder:team", "parent", "folder:root"),
            Touch("document:spec", "parent", "folder:team"),
            Touch("document:spec", "viewer", "group:all#member"),
            Touch("document:spec", "banned", "user:ayse"),
            Touch("document:open", "viewer", "user:*"),
            Touch("document:cycle", "parent", "folder:c1"),
            Touch("folder:c1", "parent", "folder:c2"),
            Touch("folder:c2", "parent", "folder:c1"),
        };
        var pr = await postgres.WriteAsync(updates, default);
        var mr = await memory.WriteAsync(updates, default);
        var onPostgres = new Checker(schema, postgres);
        var onMemory = new Checker(schema, memory);

        foreach (var obj in new[] { "document:spec", "document:open", "document:cycle", "folder:team" })
        {
            foreach (var permission in schema.Types[ObjectRef.Parse(obj).Type].Permissions.Keys)
            {
                foreach (var user in new[] { "user:ayse", "user:mehmet", "user:zeynep" })
                {
                    Assert.Equal(
                        await onMemory.CheckAsync(O(obj), permission, S(user), mr, default),
                        await onPostgres.CheckAsync(O(obj), permission, S(user), pr, default));
                }
            }
        }
    }
}
