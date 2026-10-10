using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Warden.Tests;

[Collection("postgres")]
public class ApiTests(PostgresFixture pg)
{
    private static async Task<ApiNode> StartWithSchemaAsync(PostgresFixture pg, string? database = null, int headCacheMs = 100, int maxDepth = 32)
    {
        var node = await ApiNode.StartAsync(database ?? await pg.CreateDatabaseAsync(), headCacheMs, maxDepth);
        var (status, body) = await node.PostAsync("/v1/schema", new { definition = Schemas.Docs });
        Assert.True(status == 200, body.ToString());
        return node;
    }

    [Fact]
    public async Task Requests_need_a_valid_key_with_the_right_scope()
    {
        await using var node = await StartWithSchemaAsync(pg);

        Assert.Equal(401, (await node.PostAsync("/v1/check", new { @object = "document:d", permission = "view", subject = "user:a" }, key: null)).Status);
        Assert.Equal(401, (await node.PostAsync("/v1/check", new { @object = "document:d", permission = "view", subject = "user:a" }, key: "not-a-real-key-00000000")).Status);
        Assert.Equal(403, (await node.PostAsync("/v1/relationships/write", new { updates = new[] { new { op = "touch", @object = "document:d", relation = "owner", subject = "user:a" } } }, ApiNode.ReadKey)).Status);
        Assert.Equal(403, (await node.PostAsync("/v1/schema", new { definition = Schemas.Docs }, ApiNode.WriteKey)).Status);
        Assert.Equal(200, (await node.PostAsync("/v1/check", new { @object = "document:d", permission = "view", subject = "user:a" }, ApiNode.ReadKey)).Status);
    }

    [Fact]
    public async Task The_service_refuses_to_start_without_api_keys()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var app = Warden.Server.WardenHost.Build([], b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Warden:ConnectionString"] = "Host=localhost", ["Warden:ApiKeys:0:Key"] = "short" }));
            await app.DisposeAsync();
        });

        Assert.Contains("ApiKeys", exception.Message);
    }

    [Fact]
    public async Task An_invalid_schema_is_rejected_with_line_and_column()
    {
        await using var node = await ApiNode.StartAsync(await pg.CreateDatabaseAsync());

        var (status, body) = await node.PostAsync("/v1/schema", new { definition = "definition user {}\ndefinition doc {\n  relation r user\n}" });

        Assert.Equal(400, status);
        var error = body.GetProperty("errors")[0];
        Assert.Equal(3, error.GetProperty("line").GetInt32());
        Assert.Contains("':'", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Writing_relationships_needs_a_schema_and_validates_every_update()
    {
        await using var node = await ApiNode.StartAsync(await pg.CreateDatabaseAsync());
        var first = await node.PostAsync("/v1/relationships/write", new { updates = new[] { new { op = "touch", @object = "document:d", relation = "owner", subject = "user:a" } } });
        Assert.Equal(409, first.Status);

        await node.PostAsync("/v1/schema", new { definition = Schemas.Docs });
        var (status, body) = await node.PostAsync("/v1/relationships/write", new
        {
            updates = new object[]
            {
                new { op = "touch", @object = "document:d", relation = "owner", subject = "user:a" },
                new { op = "touch", @object = "document:d", relation = "view", subject = "user:a" },
                new { op = "touch", @object = "document:d", relation = "owner", subject = "group:g#member" },
                new { op = "explode", @object = "document:d", relation = "owner", subject = "user:a" },
                new { op = "touch", @object = "nonsense", relation = "owner", subject = "user:a" },
            },
        });

        Assert.Equal(400, status);
        Assert.Equal(4, body.GetProperty("errors").GetArrayLength());
        Assert.False(await node.CheckAsync("document:d", "view", "user:a"));
    }

    [Fact]
    public async Task A_full_flow_of_schema_write_check_expand_lookup_and_changes()
    {
        await using var node = await StartWithSchemaAsync(pg);

        var token = await node.WriteAsync(
            ("touch", "group:eng", "member", "user:ayse"),
            ("touch", "folder:root", "owner", "user:mehmet"),
            ("touch", "document:spec", "parent", "folder:root"),
            ("touch", "document:spec", "viewer", "group:eng#member"),
            ("touch", "document:memo", "owner", "user:ayse"));
        var fresh = new { mode = "at_least_as_fresh", token };

        Assert.True(await node.CheckAsync("document:spec", "view", "user:ayse", fresh));
        Assert.True(await node.CheckAsync("document:spec", "edit", "user:mehmet", fresh));
        Assert.False(await node.CheckAsync("document:spec", "edit", "user:ayse", fresh));

        var (_, lookup) = await node.PostAsync("/v1/lookup/objects", new { type = "document", permission = "view", subject = "user:ayse", consistency = fresh });
        Assert.Equal(["document:memo", "document:spec"], lookup.GetProperty("objects").EnumerateArray().Select(e => e.GetString()!));

        var (expandStatus, expand) = await node.PostAsync("/v1/expand", new { @object = "document:spec", permission = "edit", consistency = fresh });
        Assert.Equal(200, expandStatus);
        Assert.Contains("user:mehmet", expand.ToString());

        var (_, changes) = await node.GetAsync("/v1/changes?after=0");
        Assert.Equal(5, changes.GetProperty("changes").GetArrayLength());

        var (_, read) = await node.PostAsync("/v1/relationships/read", new { filter = new { object_type = "document", relation = "viewer" }, consistency = fresh });
        Assert.Single(read.GetProperty("relationships").EnumerateArray());
    }

    [Fact]
    public async Task A_write_on_another_node_is_visible_with_a_token_even_when_this_node_serves_a_cached_head()
    {
        var database = await pg.CreateDatabaseAsync();
        await using var reader = await StartWithSchemaAsync(pg, database, headCacheMs: 60_000);
        await using var writer = await ApiNode.StartAsync(database, headCacheMs: 60_000);

        Assert.False(await reader.CheckAsync("document:d", "view", "user:ayse"));
        var token = await writer.WriteAsync(("touch", "document:d", "viewer", "user:ayse"));

        Assert.False(await reader.CheckAsync("document:d", "view", "user:ayse"));
        Assert.True(await reader.CheckAsync("document:d", "view", "user:ayse", new { mode = "at_least_as_fresh", token }));
        Assert.True(await reader.CheckAsync("document:d", "view", "user:ayse"));
        Assert.True(await reader.CheckAsync("document:d", "view", "user:ayse", new { mode = "fully_consistent" }));
    }

    [Fact]
    public async Task Revoking_access_takes_effect_for_a_fresh_read_immediately()
    {
        await using var node = await StartWithSchemaAsync(pg, headCacheMs: 60_000);
        var granted = await node.WriteAsync(("touch", "document:d", "viewer", "user:ayse"));
        Assert.True(await node.CheckAsync("document:d", "view", "user:ayse", new { mode = "at_least_as_fresh", token = granted }));

        var revoked = await node.WriteAsync(("delete", "document:d", "viewer", "user:ayse"));

        Assert.False(await node.CheckAsync("document:d", "view", "user:ayse", new { mode = "at_least_as_fresh", token = revoked }));
        Assert.False(await node.CheckAsync("document:d", "view", "user:ayse"));
    }

    [Fact]
    public async Task A_new_schema_published_on_one_node_is_picked_up_by_another()
    {
        var database = await pg.CreateDatabaseAsync();
        await using var a = await StartWithSchemaAsync(pg, database);
        await using var b = await ApiNode.StartAsync(database);
        Assert.Equal(400, (await b.PostAsync("/v1/check", new { @object = "folder:f", permission = "audit", subject = "user:a" })).Status);

        var extended = Schemas.Docs.Replace("permission edit = owner + editor + parent->edit\n  permission view = edit", "permission audit = owner\n  permission edit = owner + editor + parent->edit\n  permission view = edit");
        Assert.Equal(200, (await a.PostAsync("/v1/schema", new { definition = extended })).Status);
        await Task.Delay(1_200);

        Assert.Equal(200, (await b.PostAsync("/v1/check", new { @object = "folder:f", permission = "audit", subject = "user:a" })).Status);
    }

    [Fact]
    public async Task Unknown_permissions_are_client_errors_and_too_deep_graphs_are_reported_explicitly()
    {
        await using var node = await StartWithSchemaAsync(pg, maxDepth: 8);
        var updates = new List<(string, string, string, string)> { ("touch", "folder:f0", "owner", "user:ayse") };
        for (var i = 1; i <= 20; i++)
        {
            updates.Add(("touch", $"folder:f{i}", "parent", $"folder:f{i - 1}"));
        }

        await node.WriteAsync([.. updates]);

        var (unknownStatus, unknown) = await node.PostAsync("/v1/check", new { @object = "document:d", permission = "delete", subject = "user:a" });
        var (deepStatus, deep) = await node.PostAsync("/v1/check", new { @object = "folder:f20", permission = "edit", subject = "user:ayse", consistency = new { mode = "fully_consistent" } });

        Assert.Equal(400, unknownStatus);
        Assert.Contains("not a relation or permission", unknown.GetProperty("error").GetString());
        Assert.Equal(422, deepStatus);
        Assert.Contains("maximum depth", deep.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Concurrent_checks_and_writes_never_produce_an_error()
    {
        await using var node = await StartWithSchemaAsync(pg);
        await node.WriteAsync(("touch", "folder:root", "owner", "user:ayse"), ("touch", "document:d", "parent", "folder:root"));

        var tasks = Enumerable.Range(0, 60).Select(async i =>
        {
            if (i % 5 == 0)
            {
                await node.WriteAsync(("touch", $"document:n{i}", "viewer", $"user:u{i}"));
                return true;
            }

            return await node.CheckAsync("document:d", "edit", "user:ayse", new { mode = "fully_consistent" });
        });

        Assert.All(await Task.WhenAll(tasks), result => Assert.True(result));
    }

    [Fact]
    public async Task Health_is_open_and_stats_report_cache_activity()
    {
        await using var node = await StartWithSchemaAsync(pg);
        await node.WriteAsync(("touch", "document:d", "viewer", "user:ayse"));
        await node.CheckAsync("document:d", "view", "user:ayse");
        await node.CheckAsync("document:d", "view", "user:ayse");

        Assert.Equal(200, (await node.GetAsync("/healthz", key: null)).Status);
        var (_, stats) = await node.GetAsync("/stats");
        Assert.True(stats.GetProperty("cache_hits").GetInt64() > 0 || stats.GetProperty("cache_misses").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Number, stats.GetProperty("cache_hits").ValueKind);
    }
}
