using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Warden.Server;

public sealed class WardenOptions
{
    public const string Section = "Warden";

    public string ConnectionString { get; set; } = string.Empty;

    public List<ApiKey> ApiKeys { get; set; } = [];

    public int HeadCacheMs { get; set; } = 100;

    public long CacheEntries { get; set; } = 500_000;

    public int MaxDepth { get; set; } = 32;

    public int MaxParallelism { get; set; } = 8;

    public int MaxUpdatesPerWrite { get; set; } = 1_000;
}

public sealed class ApiKey
{
    public string Key { get; set; } = string.Empty;

    public string[] Scopes { get; set; } = [];
}

public enum ConsistencyMode
{
    MinimizeLatency,
    AtLeastAsFresh,
    FullyConsistent,
}

public sealed record Consistency(ConsistencyMode Mode = ConsistencyMode.MinimizeLatency, string? Token = null)
{
    public static string TokenFor(long revision) => "r" + revision;

    public static bool TryParseToken(string? token, out long revision)
    {
        revision = 0;
        return token is { Length: > 1 } && token[0] == 'r' && long.TryParse(token.AsSpan(1), out revision) && revision >= 0;
    }
}

public sealed class HeadTracker(IRelationshipStore store, IOptions<WardenOptions> options)
{
    private long _head = -1;
    private long _checkedAt;

    public async ValueTask<long> ResolveAsync(Consistency consistency, CancellationToken ct)
    {
        switch (consistency.Mode)
        {
            case ConsistencyMode.FullyConsistent:
                return await RefreshAsync(ct).ConfigureAwait(false);

            case ConsistencyMode.AtLeastAsFresh:
                if (!Consistency.TryParseToken(consistency.Token, out var wanted))
                {
                    throw new WardenException("A valid consistency token is required for at_least_as_fresh.");
                }

                var cached = Volatile.Read(ref _head);
                return cached >= wanted ? cached : await RefreshAsync(ct).ConfigureAwait(false);

            default:
                var age = Stopwatch.GetElapsedTime(Volatile.Read(ref _checkedAt));
                var current = Volatile.Read(ref _head);
                return current >= 0 && age < TimeSpan.FromMilliseconds(options.Value.HeadCacheMs)
                    ? current
                    : await RefreshAsync(ct).ConfigureAwait(false);
        }
    }

    public void Observe(long revision)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _head);
            if (revision <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _head, revision, current) != current);
    }

    private async ValueTask<long> RefreshAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var head = await store.HeadRevisionAsync(ct).ConfigureAwait(false);
        Observe(head);
        Volatile.Write(ref _checkedAt, started);
        return Volatile.Read(ref _head);
    }
}

public sealed class CachingTupleReader(ITupleReader inner, IMemoryCache cache) : ITupleReader
{
    private long _hits;
    private long _misses;

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>>> ReadObjectAsync(ObjectRef obj, long revision, CancellationToken ct)
    {
        var key = (obj, revision);
        if (cache.TryGetValue(key, out IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>>? cached) && cached is not null)
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        Interlocked.Increment(ref _misses);
        var loaded = await inner.ReadObjectAsync(obj, revision, ct).ConfigureAwait(false);
        cache.Set(key, loaded, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(2) });
        return loaded;
    }

    public ValueTask<IReadOnlyList<ObjectRef>> ListObjectsAsync(string type, long revision, CancellationToken ct) => inner.ListObjectsAsync(type, revision, ct);

    public ValueTask<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, long revision, int limit, CancellationToken ct) => inner.ReadAsync(filter, revision, limit, ct);
}

public sealed class ActiveSchema(PostgresRelationshipStore store, CachingTupleReader reader, IOptions<WardenOptions> options)
{
    private sealed record State(long Version, string Definition, Schema Schema, Checker Checker);

    private State? _state;
    private long _checkedAt;

    public async Task<(long Version, string Definition)?> CurrentAsync(CancellationToken ct)
    {
        var state = await EnsureAsync(ct).ConfigureAwait(false);
        return state is null ? null : (state.Version, state.Definition);
    }

    public async Task<Schema?> SchemaAsync(CancellationToken ct) => (await EnsureAsync(ct).ConfigureAwait(false))?.Schema;

    public async Task<Checker?> CheckerAsync(CancellationToken ct) => (await EnsureAsync(ct).ConfigureAwait(false))?.Checker;

    public async Task<long> PublishAsync(string definition, CancellationToken ct)
    {
        var schema = Schema.Parse(definition);
        var version = await store.SaveSchemaAsync(definition, ct).ConfigureAwait(false);
        Volatile.Write(ref _state, Build(version, definition, schema));
        Volatile.Write(ref _checkedAt, Stopwatch.GetTimestamp());
        return version;
    }

    private async Task<State?> EnsureAsync(CancellationToken ct)
    {
        var cached = Volatile.Read(ref _state);
        if (cached is not null && Stopwatch.GetElapsedTime(Volatile.Read(ref _checkedAt)) < TimeSpan.FromSeconds(1))
        {
            return cached;
        }

        var started = Stopwatch.GetTimestamp();
        var latest = await store.LatestSchemaAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _checkedAt, started);
        if (latest is null)
        {
            return null;
        }

        var current = Volatile.Read(ref _state);
        if (current is not null && current.Version == latest.Value.Version)
        {
            return current;
        }

        var built = Build(latest.Value.Version, latest.Value.Definition, Schema.Parse(latest.Value.Definition));
        Volatile.Write(ref _state, built);
        return built;
    }

    private State Build(long version, string definition, Schema schema)
        => new(version, definition, schema, new Checker(schema, reader, new CheckerOptions { MaxDepth = options.Value.MaxDepth, MaxParallelism = options.Value.MaxParallelism }));
}
