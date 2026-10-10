using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Warden.Server;
using Xunit;

namespace Warden.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("public.ecr.aws/docker/library/postgres:17")
        .WithCommand("-c", "max_connections=400")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, MaxPoolSize = 40 }.ConnectionString;
    }

    public async Task<(NpgsqlDataSource DataSource, PostgresRelationshipStore Store)> CreateStoreAsync()
    {
        var dataSource = NpgsqlDataSource.Create(await CreateDatabaseAsync());
        await PostgresRelationshipStore.EnsureSchemaAsync(dataSource, default);
        return (dataSource, new PostgresRelationshipStore(dataSource));
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class ApiNode : IAsyncDisposable
{
    public const string AdminKey = "test-admin-key-0000000001";
    public const string ReadKey = "test-read-key-00000000002";
    public const string WriteKey = "test-write-key-0000000003";

    private readonly WebApplication _app;
    private readonly HttpClient _http;

    private ApiNode(WebApplication app)
    {
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public static async Task<ApiNode> StartAsync(string connectionString, int headCacheMs = 100, int maxDepth = 32)
    {
        var app = WardenHost.Build([], b =>
        {
            b.Logging.ClearProviders();
            b.Logging.AddConsole();
            b.Logging.SetMinimumLevel(LogLevel.Error);
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Warden:ConnectionString"] = connectionString,
                ["Warden:HeadCacheMs"] = headCacheMs.ToString(),
                ["Warden:MaxDepth"] = maxDepth.ToString(),
                ["Warden:ApiKeys:0:Key"] = AdminKey,
                ["Warden:ApiKeys:0:Scopes:0"] = "admin",
                ["Warden:ApiKeys:1:Key"] = ReadKey,
                ["Warden:ApiKeys:1:Scopes:0"] = "read",
                ["Warden:ApiKeys:2:Key"] = WriteKey,
                ["Warden:ApiKeys:2:Scopes:0"] = "write",
                ["Warden:ApiKeys:2:Scopes:1"] = "read",
            });
        });
        await app.StartAsync();
        return new ApiNode(app);
    }

    public async Task<(int Status, JsonElement Body)> PostAsync(string path, object body, string? key = AdminKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return await SendAsync(request);
    }

    public async Task<(int Status, JsonElement Body)> GetAsync(string path, string? key = AdminKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return await SendAsync(request);
    }

    private async Task<(int Status, JsonElement Body)> SendAsync(HttpRequestMessage request)
    {
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var body = default(JsonElement);
        if (!string.IsNullOrWhiteSpace(text) && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            body = JsonDocument.Parse(text).RootElement.Clone();
        }
        return ((int)response.StatusCode, body);
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<string> WriteAsync(params (string Op, string Object, string Relation, string Subject)[] updates)
    {
        var (status, body) = await PostAsync("/v1/relationships/write", new { updates = updates.Select(u => new { op = u.Op, @object = u.Object, u.Relation, u.Subject }) });
        Assert.True(status == 200, body.ToString());
        return body.GetProperty("token").GetString()!;
    }

    public async Task<bool> CheckAsync(string obj, string permission, string subject, object? consistency = null, string? key = AdminKey)
    {
        var (status, body) = await PostAsync("/v1/check", new { @object = obj, permission, subject, consistency }, key);
        Assert.True(status == 200, body.ToString());
        return body.GetProperty("allowed").GetBoolean();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }
}
