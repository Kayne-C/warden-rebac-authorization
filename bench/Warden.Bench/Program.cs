using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Warden;
using Warden.Server;

var options = Options.Parse(args);
Console.WriteLine($"mode={options.Mode} users={options.Users} groups={options.Groups} folders={options.Folders} docs={options.Docs} checks={options.Checks} concurrency={options.Concurrency}");

const string SchemaText = """
    definition user {}
    definition group { relation member: user | group#member }
    definition folder {
      relation parent: folder
      relation owner: user
      relation editor: user | group#member
      relation viewer: user | group#member | user:*
      permission edit = owner + editor + parent->edit
      permission view = edit + viewer + parent->view
    }
    definition document {
      relation parent: folder
      relation owner: user
      relation editor: user | group#member
      relation viewer: user | group#member | user:*
      relation banned: user
      permission edit = owner + editor + parent->edit
      permission view = (edit + viewer + parent->view) - banned
    }
    """;

var schema = Schema.Parse(SchemaText);
var random = new Random(options.Seed);
var updates = Generate();
Console.WriteLine($"generated {updates.Count} relationships (max folder depth {options.Depth})");

IRelationshipStore store;
NpgsqlDataSource? dataSource = null;
if (options.Mode == "memory")
{
    store = new InMemoryRelationshipStore();
}
else
{
    dataSource = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(options.Connection) { MaxPoolSize = 200 }.ConnectionString).Build();
    await using (var reset = dataSource.CreateCommand("DROP TABLE IF EXISTS relationships, revisions, schemas CASCADE"))
    {
        await reset.ExecuteNonQueryAsync();
    }

    await PostgresRelationshipStore.EnsureSchemaAsync(dataSource, default);
    store = new PostgresRelationshipStore(dataSource);
}

var loadWatch = Stopwatch.StartNew();
for (var i = 0; i < updates.Count; i += 10_000)
{
    await store.WriteAsync(updates.GetRange(i, Math.Min(10_000, updates.Count - i)), default);
}

loadWatch.Stop();
var head = await store.HeadRevisionAsync(default);
Console.WriteLine($"loaded in {loadWatch.Elapsed.TotalSeconds:0.0}s ({updates.Count / loadWatch.Elapsed.TotalSeconds:0} relationships/s), head revision {head}");

ITupleReader reader = store;
CachingTupleReader? cache = null;
if (options.Mode == "postgres-cached")
{
    cache = new CachingTupleReader(store, new MemoryCache(new MemoryCacheOptions { SizeLimit = 2_000_000 }));
    reader = cache;
}

var counting = new CountingReader(reader);
var checker = new Checker(schema, counting, new CheckerOptions { MaxDepth = options.Depth + 8, MaxParallelism = 8 });

var workload = new List<(ObjectRef, string, SubjectRef)>(options.Checks);
for (var i = 0; i < options.Checks; i++)
{
    var doc = random.Next(options.Docs);
    var user = random.Next(options.Users);
    workload.Add((new ObjectRef("document", $"d{doc}"), random.Next(5) == 0 ? "edit" : "view", new SubjectRef("user", $"u{user}")));
}

async Task RunAsync(int count, bool record, long[]? latencies, int[]? allowedCount)
{
    var index = -1;
    var workers = Enumerable.Range(0, options.Concurrency).Select(_ => Task.Run(async () =>
    {
        while (true)
        {
            var i = Interlocked.Increment(ref index);
            if (i >= count)
            {
                return;
            }

            var (obj, permission, subject) = workload[i];
            var started = Stopwatch.GetTimestamp();
            var allowed = await checker.CheckAsync(obj, permission, subject, head, default);
            if (record)
            {
                latencies![i] = Stopwatch.GetTimestamp() - started;
                if (allowed)
                {
                    Interlocked.Increment(ref allowedCount![0]);
                }
            }
        }
    })).ToArray();
    await Task.WhenAll(workers);
}

await RunAsync(Math.Min(5_000, options.Checks), false, null, null);
counting.Reset();

var latencyTicks = new long[options.Checks];
var allowedTotal = new int[1];
var watch = Stopwatch.StartNew();
await RunAsync(options.Checks, true, latencyTicks, allowedTotal);
watch.Stop();

Array.Sort(latencyTicks);
double Ms(double q) => latencyTicks[(int)Math.Min(latencyTicks.Length - 1, q * latencyTicks.Length)] * 1000.0 / Stopwatch.Frequency;
Console.WriteLine($"checks/s={options.Checks / watch.Elapsed.TotalSeconds:0}  latency ms p50={Ms(0.50):0.00} p95={Ms(0.95):0.00} p99={Ms(0.99):0.00} max={Ms(1):0.00}");
Console.WriteLine($"allowed={allowedTotal[0] * 100.0 / options.Checks:0.0}%  reads/check={counting.Reads / (double)options.Checks:0.0}" + (cache is null ? string.Empty : $"  cache hit rate={cache.Hits * 100.0 / Math.Max(1, cache.Hits + cache.Misses):0.0}%"));

if (dataSource is not null)
{
    await dataSource.DisposeAsync();
}

return 0;

List<RelationshipUpdate> Generate()
{
    var list = new List<RelationshipUpdate>();
    var seen = new HashSet<string>();

    void Add(string obj, string relation, string subject)
    {
        if (seen.Add($"{obj}#{relation}@{subject}"))
        {
            list.Add(new RelationshipUpdate(ChangeOp.Touch, new Relationship(ObjectRef.Parse(obj), relation, SubjectRef.Parse(subject))));
        }
    }

    for (var g = 0; g < options.Groups; g++)
    {
        for (var m = 0; m < 30; m++)
        {
            Add($"group:g{g}", "member", $"user:u{random.Next(options.Users)}");
        }

        if (g > 0 && random.Next(5) == 0)
        {
            Add($"group:g{g}", "member", $"group:g{random.Next(g)}#member");
        }
    }

    var depthOf = new int[options.Folders];
    for (var f = 0; f < options.Folders; f++)
    {
        if (f > 0)
        {
            var parent = random.Next(Math.Max(0, f - 50), f);
            if (depthOf[parent] + 1 <= options.Depth)
            {
                depthOf[f] = depthOf[parent] + 1;
                Add($"folder:f{f}", "parent", $"folder:f{parent}");
            }
        }

        if (random.Next(4) == 0)
        {
            Add($"folder:f{f}", "owner", $"user:u{random.Next(options.Users)}");
        }

        for (var n = random.Next(0, 6); n > 0; n--)
        {
            Add($"folder:f{f}", random.Next(3) == 0 ? "editor" : "viewer", random.Next(2) == 0 ? $"group:g{random.Next(options.Groups)}#member" : $"user:u{random.Next(options.Users)}");
        }
    }

    for (var d = 0; d < options.Docs; d++)
    {
        Add($"document:d{d}", "parent", $"folder:f{random.Next(options.Folders)}");
        if (random.Next(5) == 0)
        {
            Add($"document:d{d}", "owner", $"user:u{random.Next(options.Users)}");
        }

        if (random.Next(2) == 0)
        {
            Add($"document:d{d}", "viewer", random.Next(2) == 0 ? $"group:g{random.Next(options.Groups)}#member" : $"user:u{random.Next(options.Users)}");
        }

        if (random.Next(50) == 0)
        {
            Add($"document:d{d}", "banned", $"user:u{random.Next(options.Users)}");
        }
    }

    return list;
}

internal sealed class CountingReader(ITupleReader inner) : ITupleReader
{
    private long _reads;

    public long Reads => Interlocked.Read(ref _reads);

    public void Reset() => Interlocked.Exchange(ref _reads, 0);

    public ValueTask<IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>>> ReadObjectAsync(ObjectRef obj, long revision, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadObjectAsync(obj, revision, ct);
        }

    public ValueTask<IReadOnlyList<ObjectRef>> ListObjectsAsync(string type, long revision, CancellationToken ct) => inner.ListObjectsAsync(type, revision, ct);

    public ValueTask<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, long revision, int limit, CancellationToken ct) => inner.ReadAsync(filter, revision, limit, ct);
}

internal sealed record Options(string Mode, int Users, int Groups, int Folders, int Docs, int Depth, int Checks, int Concurrency, int Seed, string Connection)
{
    public static Options Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        return new Options(
            Get("mode", "memory"),
            int.Parse(Get("users", "10000")),
            int.Parse(Get("groups", "1000")),
            int.Parse(Get("folders", "10000")),
            int.Parse(Get("docs", "100000")),
            int.Parse(Get("depth", "8")),
            int.Parse(Get("checks", "100000")),
            int.Parse(Get("concurrency", "64")),
            int.Parse(Get("seed", "7")),
            Get("conn", "Host=localhost;Port=5434;Database=bench;Username=postgres;Password=bench"));
    }
}
