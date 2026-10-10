using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Warden.Server;

public sealed record ConsistencyDto(string? Mode, string? Token)
{
    public Consistency ToConsistency() => new(
        Mode switch
        {
            null or "minimize_latency" => ConsistencyMode.MinimizeLatency,
            "at_least_as_fresh" => ConsistencyMode.AtLeastAsFresh,
            "fully_consistent" => ConsistencyMode.FullyConsistent,
            _ => throw new WardenException($"Unknown consistency mode '{Mode}'."),
        },
        Token);
}

public sealed record SchemaRequest(string Definition);

public sealed record UpdateDto(string Op, string Object, string Relation, string Subject);

public sealed record WriteRequest(List<UpdateDto> Updates);

public sealed record CheckRequest(string Object, string Permission, string Subject, ConsistencyDto? Consistency);

public sealed record ExpandRequest(string Object, string Permission, ConsistencyDto? Consistency);

public sealed record LookupRequest(string Type, string Permission, string Subject, int? Limit, ConsistencyDto? Consistency);

public sealed record ReadRequest(RelationshipFilter? Filter, int? Limit, ConsistencyDto? Consistency);

public static class WardenHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        var options = builder.Configuration.GetSection(WardenOptions.Section).Get<WardenOptions>() ?? new WardenOptions();
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException($"{WardenOptions.Section}:ConnectionString is required.");
        }

        if (options.ApiKeys.Count == 0 || options.ApiKeys.Any(k => k.Key.Length < 16))
        {
            throw new InvalidOperationException($"Configure {WardenOptions.Section}:ApiKeys with keys of at least 16 characters; the service does not start without authentication.");
        }

        builder.Services.Configure<WardenOptions>(builder.Configuration.GetSection(WardenOptions.Section));
        var connection = new NpgsqlConnectionStringBuilder(options.ConnectionString) { MaxPoolSize = Math.Max(40, new NpgsqlConnectionStringBuilder(options.ConnectionString).MaxPoolSize) };
        builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connection.ConnectionString).Build());
        builder.Services.AddSingleton<PostgresRelationshipStore>();
        builder.Services.AddSingleton<IRelationshipStore>(sp => sp.GetRequiredService<PostgresRelationshipStore>());
        builder.Services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions { SizeLimit = options.CacheEntries }));
        builder.Services.AddSingleton(sp => new CachingTupleReader(sp.GetRequiredService<PostgresRelationshipStore>(), sp.GetRequiredService<IMemoryCache>()));
        builder.Services.AddSingleton<HeadTracker>();
        builder.Services.AddSingleton<ActiveSchema>();
        builder.Services.AddHostedService<SchemaInstaller>();
        builder.Services.AddHealthChecks();
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (UnknownNameException ex)
            {
                await Problem(context, StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (DepthExceededException ex)
            {
                await Problem(context, StatusCodes.Status422UnprocessableEntity, ex.Message);
            }
            catch (WardenException ex)
            {
                await Problem(context, StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (FormatException ex)
            {
                await Problem(context, StatusCodes.Status400BadRequest, ex.Message);
            }
        });

        MapEndpoints(app, options);
        return app;
    }

    private static object Project(ExpandNode node) => new
    {
        kind = node.Kind,
        label = node.Label,
        subjects = node.Subjects.Select(s => s.ToString()),
        children = node.Children.Select(Project),
    };

    private static Task Problem(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }

    private static bool Authorized(HttpContext context, WardenOptions options, string scope)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        ApiKey? match = null;
        foreach (var key in options.ApiKeys)
        {
            var expected = Encoding.UTF8.GetBytes(key.Key);
            if (CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(presented),
                SHA256.HashData(expected)))
            {
                match = key;
            }
        }

        if (match is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        if (!match.Scopes.Contains(scope) && !match.Scopes.Contains("admin"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        return true;
    }

    private static void MapEndpoints(WebApplication app, WardenOptions options)
    {
        app.MapHealthChecks("/healthz");

        app.MapGet("/stats", (CachingTupleReader reader, HttpContext context) =>
            Authorized(context, options, "read")
                ? Results.Ok(new { cache_hits = reader.Hits, cache_misses = reader.Misses })
                : Results.StatusCode(context.Response.StatusCode));

        app.MapPost("/v1/schema", async (SchemaRequest request, ActiveSchema active, HttpContext context, CancellationToken ct) =>
        {
            if (!Authorized(context, options, "admin"))
            {
                return Results.StatusCode(context.Response.StatusCode);
            }

            try
            {
                var version = await active.PublishAsync(request.Definition, ct);
                return Results.Ok(new { version });
            }
            catch (SchemaException ex)
            {
                return Results.Json(new { errors = ex.Errors.Select(e => new { line = e.Line, column = e.Column, message = e.Message }) }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapGet("/v1/schema", async (ActiveSchema active, HttpContext context, CancellationToken ct) =>
        {
            if (!Authorized(context, options, "read"))
            {
                return Results.StatusCode(context.Response.StatusCode);
            }

            var current = await active.CurrentAsync(ct);
            return current is null ? Results.NotFound() : Results.Ok(new { version = current.Value.Version, definition = current.Value.Definition });
        });

        app.MapPost("/v1/relationships/write", async (WriteRequest request, ActiveSchema active, IRelationshipStore store, HeadTracker head, HttpContext context, CancellationToken ct) =>
        {
            if (!Authorized(context, options, "write"))
            {
                return Results.StatusCode(context.Response.StatusCode);
            }

            var schema = await active.SchemaAsync(ct);
            if (schema is null)
            {
                return Results.Json(new { error = "No schema has been written yet." }, statusCode: StatusCodes.Status409Conflict);
            }

            if (request.Updates is null || request.Updates.Count == 0 || request.Updates.Count > options.MaxUpdatesPerWrite)
            {
                return Results.Json(new { error = $"Send between 1 and {options.MaxUpdatesPerWrite} updates." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var updates = new List<RelationshipUpdate>();
            var problems = new List<string>();
            for (var i = 0; i < request.Updates.Count; i++)
            {
                var dto = request.Updates[i];
                try
                {
                    var op = dto.Op switch
                    {
                        "touch" => ChangeOp.Touch,
                        "delete" => ChangeOp.Delete,
                        _ => throw new FormatException($"Unknown op '{dto.Op}'; use touch or delete."),
                    };
                    var relationship = new Relationship(ObjectRef.Parse(dto.Object), dto.Relation, SubjectRef.Parse(dto.Subject));
                    var invalid = op == ChangeOp.Touch ? schema.ValidateRelationship(relationship) : null;
                    if (invalid is not null)
                    {
                        problems.Add($"updates[{i}]: {invalid}");
                    }
                    else
                    {
                        updates.Add(new RelationshipUpdate(op, relationship));
                    }
                }
                catch (FormatException ex)
                {
                    problems.Add($"updates[{i}]: {ex.Message}");
                }
            }

            if (problems.Count > 0)
            {
                return Results.Json(new { errors = problems }, statusCode: StatusCodes.Status400BadRequest);
            }

            var revision = await store.WriteAsync(updates, ct);
            head.Observe(revision);
            return Results.Ok(new { revision, token = Consistency.TokenFor(revision) });
        });

        app.MapPost("/v1/relationships/read", async (ReadRequest request, IRelationshipStore store, HeadTracker head, HttpContext context, CancellationToken ct) =>
        {
            if (!Authorized(context, options, "read"))
            {
                return Results.StatusCode(context.Response.StatusCode);
            }

            var revision = await head.ResolveAsync((request.Consistency ?? new ConsistencyDto(null, null)).ToConsistency(), ct);
            var found = await store.ReadAsync(request.Filter ?? new RelationshipFilter(), revision, Math.Clamp(request.Limit ?? 100, 1, 1000), ct);
            return Results.Ok(new
            {
                revision,
                relationships = found.Select(r => new { @object = r.Object.ToString(), relation = r.Relation, subject = r.Subject.ToString() }),
            });
        });

        app.MapPost("/v1/check", async (CheckRequest request, ActiveSchema active, HeadTracker head, HttpContext context, CancellationToken ct) =>
        {
            if (!Authorized(context, options, "read"))
            {
                return Results.StatusCode(context.Response.StatusCode);
            }

            var checker = await active.CheckerAsync(ct);
            if (checker is null)
            {
                return Results.Json(new { error = "No schema has been written yet." }, statusCode: StatusCodes.Status409Conflict);
            }

            var revision = await head.ResolveAsync((request.Consistency ?? new ConsistencyDto(null, null)).ToConsistency(), ct);
            var allowed = await checker.CheckAsync(ObjectRef.Parse(request.Object), request.Permission, SubjectRef.Parse(request.Subject), revision, ct);
            return Results.Ok(new { allowed, revision, token = Consistency.TokenFor(revision) });
        });

        app.MapPost("/v1/expand", async (ExpandRequest request, ActiveSchema active, HeadTracker head, HttpContext context, CancellationToken ct) =>
        {
            if (!Authorized(context, options, "read"))
            {
                return Results.StatusCode(context.Response.StatusCode);
            }

            var checker = await active.CheckerAsync(ct);
            if (checker is null)
            {
                return Results.Json(new { error = "No schema has been written yet." }, statusCode: StatusCodes.Status409Conflict);
            }

            var revision = await head.ResolveAsync((request.Consistency ?? new ConsistencyDto(null, null)).ToConsistency(), ct);
            var tree = await checker.ExpandAsync(ObjectRef.Parse(request.Object), request.Permission, revision, ct);
            return Results.Ok(new { revision, tree = Project(tree) });
        });

        app.MapPost("/v1/lookup/objects", async (LookupRequest request, ActiveSchema active, HeadTracker head, HttpContext context, CancellationToken ct) =>
        {
            if (!Authorized(context, options, "read"))
            {
                return Results.StatusCode(context.Response.StatusCode);
            }

            var checker = await active.CheckerAsync(ct);
            if (checker is null)
            {
                return Results.Json(new { error = "No schema has been written yet." }, statusCode: StatusCodes.Status409Conflict);
            }

            var revision = await head.ResolveAsync((request.Consistency ?? new ConsistencyDto(null, null)).ToConsistency(), ct);
            var objects = await checker.ListObjectsAsync(request.Type, request.Permission, SubjectRef.Parse(request.Subject), revision, Math.Clamp(request.Limit ?? 100, 1, 1000), ct);
            return Results.Ok(new { revision, objects = objects.Select(o => o.ToString()) });
        });

        app.MapGet("/v1/changes", async (long? after, int? limit, IRelationshipStore store, HttpContext context, CancellationToken ct) =>
        {
            if (!Authorized(context, options, "read"))
            {
                return Results.StatusCode(context.Response.StatusCode);
            }

            var changes = await store.ReadChangesAsync(after ?? 0, Math.Clamp(limit ?? 100, 1, 1000), ct);
            return Results.Ok(new
            {
                changes = changes.Select(c => new { revision = c.Revision, op = c.Op == ChangeOp.Touch ? "touch" : "delete", @object = c.Relationship.Object.ToString(), relation = c.Relationship.Relation, subject = c.Relationship.Subject.ToString() }),
            });
        });
    }
}

public sealed class SchemaInstaller(NpgsqlDataSource dataSource) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => PostgresRelationshipStore.EnsureSchemaAsync(dataSource, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
