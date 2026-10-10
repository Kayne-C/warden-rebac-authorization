using Xunit;
using static Warden.Tests.T;

namespace Warden.Tests;

public class CheckerTests
{
    [Fact]
    public async Task Direct_relations_grant_exactly_what_they_name()
    {
        var (checker, store) = await Build(Schemas.Docs, Touch("document:plan", "owner", "user:ayse"), Touch("document:plan", "viewer", "user:mehmet"));

        Assert.True(await Can(checker, store, "document:plan", "view", "user:ayse"));
        Assert.True(await Can(checker, store, "document:plan", "edit", "user:ayse"));
        Assert.True(await Can(checker, store, "document:plan", "view", "user:mehmet"));
        Assert.False(await Can(checker, store, "document:plan", "edit", "user:mehmet"));
        Assert.False(await Can(checker, store, "document:plan", "view", "user:zeynep"));
        Assert.False(await Can(checker, store, "document:other", "view", "user:ayse"));
    }

    [Fact]
    public async Task Group_membership_is_followed_including_nested_groups()
    {
        var (checker, store) = await Build(
            Schemas.Docs,
            Touch("group:eng", "member", "user:ayse"),
            Touch("group:all", "member", "group:eng#member"),
            Touch("document:plan", "viewer", "group:all#member"));

        Assert.True(await Can(checker, store, "document:plan", "view", "user:ayse"));
        Assert.False(await Can(checker, store, "document:plan", "view", "user:mehmet"));
    }

    [Fact]
    public async Task Permissions_flow_down_the_folder_hierarchy_through_arrows()
    {
        var (checker, store) = await Build(
            Schemas.Docs,
            Touch("folder:root", "owner", "user:ayse"),
            Touch("folder:team", "parent", "folder:root"),
            Touch("folder:project", "parent", "folder:team"),
            Touch("document:spec", "parent", "folder:project"),
            Touch("folder:team", "viewer", "user:mehmet"));

        Assert.True(await Can(checker, store, "document:spec", "edit", "user:ayse"));
        Assert.True(await Can(checker, store, "document:spec", "view", "user:mehmet"));
        Assert.False(await Can(checker, store, "document:spec", "edit", "user:mehmet"));
        Assert.False(await Can(checker, store, "folder:root", "view", "user:mehmet"));
    }

    [Fact]
    public async Task A_wildcard_viewer_matches_every_user_but_nothing_else()
    {
        var (checker, store) = await Build(Schemas.Docs, Touch("document:public", "viewer", "user:*"));

        Assert.True(await Can(checker, store, "document:public", "view", "user:anyone"));
        Assert.False(await Can(checker, store, "document:public", "edit", "user:anyone"));
        Assert.False(await Can(checker, store, "document:public", "view", "group:eng"));
    }

    [Fact]
    public async Task Exclusion_removes_a_subject_even_if_another_path_grants_access()
    {
        var (checker, store) = await Build(
            Schemas.Docs,
            Touch("folder:root", "owner", "user:ayse"),
            Touch("document:memo", "parent", "folder:root"),
            Touch("document:memo", "banned", "user:ayse"),
            Touch("document:memo", "viewer", "user:mehmet"));

        Assert.False(await Can(checker, store, "document:memo", "view", "user:ayse"));
        Assert.True(await Can(checker, store, "document:memo", "view", "user:mehmet"));
        Assert.True(await Can(checker, store, "document:memo", "edit", "user:ayse"));
    }

    [Fact]
    public async Task Intersection_needs_both_sides()
    {
        var (checker, store) = await Build(
            Schemas.Docs,
            Touch("document:memo", "owner", "user:ayse"),
            Touch("document:memo", "viewer", "user:ayse"),
            Touch("document:memo", "owner", "user:mehmet"),
            Touch("document:memo", "viewer", "user:zeynep"));

        Assert.True(await Can(checker, store, "document:memo", "share", "user:ayse"));
        Assert.False(await Can(checker, store, "document:memo", "share", "user:mehmet"));
        Assert.False(await Can(checker, store, "document:memo", "share", "user:zeynep"));
    }

    [Fact]
    public async Task Cycles_in_the_data_terminate_and_do_not_grant_access_by_themselves()
    {
        var (checker, store) = await Build(
            Schemas.Docs,
            Touch("folder:a", "parent", "folder:b"),
            Touch("folder:b", "parent", "folder:c"),
            Touch("folder:c", "parent", "folder:a"),
            Touch("group:g1", "member", "group:g2#member"),
            Touch("group:g2", "member", "group:g1#member"),
            Touch("document:d", "parent", "folder:a"),
            Touch("document:d", "viewer", "group:g1#member"));

        Assert.False(await Can(checker, store, "document:d", "view", "user:ayse"));

        await store.WriteAsync([Touch("folder:c", "owner", "user:ayse"), Touch("group:g2", "member", "user:mehmet")], default);

        Assert.True(await Can(checker, store, "document:d", "view", "user:ayse"));
        Assert.True(await Can(checker, store, "document:d", "view", "user:mehmet"));
        Assert.False(await Can(checker, store, "document:d", "view", "user:zeynep"));
    }

    [Fact]
    public async Task A_chain_longer_than_the_depth_limit_is_an_explicit_error_not_a_silent_deny()
    {
        var updates = new List<RelationshipUpdate> { Touch("folder:f0", "owner", "user:ayse") };
        for (var i = 1; i <= 40; i++)
        {
            updates.Add(Touch($"folder:f{i}", "parent", $"folder:f{i - 1}"));
        }

        var schema = Schema.Parse(Schemas.Docs);
        var store = new InMemoryRelationshipStore();
        await store.WriteAsync(updates, default);
        var head = await store.HeadRevisionAsync(default);

        var shallow = new Checker(schema, store, new CheckerOptions { MaxDepth = 10 });
        var deep = new Checker(schema, store, new CheckerOptions { MaxDepth = 100 });

        await Assert.ThrowsAsync<DepthExceededException>(() => shallow.CheckAsync(O("folder:f40"), "edit", S("user:ayse"), head, default));
        Assert.True(await deep.CheckAsync(O("folder:f40"), "edit", S("user:ayse"), head, default));
    }

    [Fact]
    public async Task Unknown_names_are_errors_not_denials()
    {
        var (checker, store) = await Build(Schemas.Docs);
        var head = await store.HeadRevisionAsync(default);

        await Assert.ThrowsAsync<UnknownNameException>(() => checker.CheckAsync(O("document:d"), "delete", S("user:a"), head, default));
        await Assert.ThrowsAsync<UnknownNameException>(() => checker.CheckAsync(O("spaceship:s"), "view", S("user:a"), head, default));
    }

    [Fact]
    public async Task Checks_at_an_older_revision_see_the_world_as_it_was()
    {
        var schema = Schema.Parse(Schemas.Docs);
        var store = new InMemoryRelationshipStore();
        var checker = new Checker(schema, store);
        var r1 = await store.WriteAsync([Touch("document:d", "viewer", "user:ayse")], default);
        var r2 = await store.WriteAsync([Touch("document:d", "viewer", "user:mehmet"), Delete("document:d", "viewer", "user:ayse")], default);

        Assert.True(await checker.CheckAsync(O("document:d"), "view", S("user:ayse"), r1, default));
        Assert.False(await checker.CheckAsync(O("document:d"), "view", S("user:mehmet"), r1, default));
        Assert.False(await checker.CheckAsync(O("document:d"), "view", S("user:ayse"), r2, default));
        Assert.True(await checker.CheckAsync(O("document:d"), "view", S("user:mehmet"), r2, default));
    }

    [Fact]
    public async Task A_shared_session_reads_each_relation_once_even_in_a_diamond()
    {
        var schema = Schema.Parse(Schemas.Docs);
        var inner = new InMemoryRelationshipStore();
        var counting = new CountingReader(inner);
        await inner.WriteAsync(
        [
            Touch("folder:top", "owner", "user:ayse"),
            Touch("folder:left", "parent", "folder:top"),
            Touch("folder:right", "parent", "folder:top"),
            Touch("folder:bottom", "parent", "folder:left"),
            Touch("folder:bottom", "parent", "folder:right"),
        ], default);
        var checker = new Checker(schema, counting);
        var head = await inner.HeadRevisionAsync(default);

        var session = checker.CreateSession(S("user:ayse"), head);
        Assert.True(await session.CheckAsync(O("folder:bottom"), "view", default));
        var afterFirst = counting.Reads;
        Assert.True(await session.CheckAsync(O("folder:bottom"), "view", default));

        Assert.Equal(afterFirst, counting.Reads);
    }

    [Fact]
    public async Task Expand_returns_the_tree_of_who_has_a_permission_and_why()
    {
        var (checker, store) = await Build(
            Schemas.Docs,
            Touch("folder:root", "owner", "user:ayse"),
            Touch("group:eng", "member", "user:mehmet"),
            Touch("document:d", "parent", "folder:root"),
            Touch("document:d", "editor", "group:eng#member"));

        var tree = await checker.ExpandAsync(O("document:d"), "edit", await store.HeadRevisionAsync(default), default);

        string Flatten(ExpandNode node) => string.Join(",", node.Subjects.Select(s => s.ToString()).Concat(node.Children.Select(Flatten)));
        var flat = Flatten(tree);
        Assert.Contains("user:ayse", flat);
        Assert.Contains("user:mehmet", flat);
        Assert.Equal("union", tree.Kind);
    }

    [Fact]
    public async Task List_objects_returns_exactly_the_objects_a_subject_can_use()
    {
        var (checker, store) = await Build(
            Schemas.Docs,
            Touch("folder:shared", "viewer", "user:ayse"),
            Touch("document:a", "parent", "folder:shared"),
            Touch("document:b", "owner", "user:ayse"),
            Touch("document:c", "owner", "user:mehmet"),
            Touch("document:d", "viewer", "user:*"));

        var visible = await checker.ListObjectsAsync("document", "view", S("user:ayse"), await store.HeadRevisionAsync(default), 100, default);
        var editable = await checker.ListObjectsAsync("document", "edit", S("user:ayse"), await store.HeadRevisionAsync(default), 100, default);

        Assert.Equal(["document:a", "document:b", "document:d"], visible.Select(o => o.ToString()));
        Assert.Equal(["document:b"], editable.Select(o => o.ToString()));
    }

    [Fact]
    public async Task Touching_twice_and_deleting_a_missing_relationship_do_not_create_revisions()
    {
        var store = new InMemoryRelationshipStore();

        var first = await store.WriteAsync([Touch("document:d", "owner", "user:a")], default);
        var again = await store.WriteAsync([Touch("document:d", "owner", "user:a")], default);
        var missing = await store.WriteAsync([Delete("document:d", "owner", "user:b")], default);

        Assert.Equal(first, again);
        Assert.Equal(first, missing);
    }

    private sealed class CountingReader(ITupleReader inner) : ITupleReader
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public ValueTask<IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>>> ReadObjectAsync(ObjectRef obj, long revision, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadObjectAsync(obj, revision, ct);
        }

        public ValueTask<IReadOnlyList<ObjectRef>> ListObjectsAsync(string type, long revision, CancellationToken ct) => inner.ListObjectsAsync(type, revision, ct);

        public ValueTask<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, long revision, int limit, CancellationToken ct) => inner.ReadAsync(filter, revision, limit, ct);
    }
}
