using Npgsql;
using NpgsqlTypes;

namespace Warden.Server;

public sealed class PostgresRelationshipStore(NpgsqlDataSource dataSource) : IRelationshipStore
{
    private const int WriterLockKey = 0x57415244;

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS revisions (
            rev        bigint PRIMARY KEY,
            created_at timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS relationships (
            id               bigserial PRIMARY KEY,
            object_type      text NOT NULL,
            object_id        text NOT NULL,
            relation         text NOT NULL,
            subject_type     text NOT NULL,
            subject_id       text NOT NULL,
            subject_relation text NOT NULL DEFAULT '',
            created_rev      bigint NOT NULL,
            deleted_rev      bigint NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS relationships_active_uq
            ON relationships (object_type, object_id, relation, subject_type, subject_id, subject_relation)
            WHERE deleted_rev IS NULL;
        CREATE INDEX IF NOT EXISTS relationships_object_idx ON relationships (object_type, object_id, relation, created_rev);
        CREATE INDEX IF NOT EXISTS relationships_subject_idx ON relationships (subject_type, subject_id, subject_relation);
        CREATE INDEX IF NOT EXISTS relationships_created_idx ON relationships (created_rev);
        CREATE INDEX IF NOT EXISTS relationships_deleted_idx ON relationships (deleted_rev) WHERE deleted_rev IS NOT NULL;

        CREATE TABLE IF NOT EXISTS schemas (
            version    bigserial PRIMARY KEY,
            definition text NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now()
        );
        """;

    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", (long)WriterLockKey + 1);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var ddl = new NpgsqlCommand(Ddl, conn, tx))
        {
            await ddl.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async ValueTask<long> HeadRevisionAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT COALESCE(max(rev), 0) FROM revisions");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async ValueTask<long> WriteAsync(IReadOnlyList<RelationshipUpdate> updates, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", (long)WriterLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        long head;
        await using (var headCmd = new NpgsqlCommand("SELECT COALESCE(max(rev), 0) FROM revisions", conn, tx))
        {
            head = Convert.ToInt64(await headCmd.ExecuteScalarAsync(ct));
        }

        var revision = head + 1;
        var changed = 0;
        var index = 0;
        while (index < updates.Count)
        {
            var op = updates[index].Op;
            var group = new List<Relationship>();
            while (index < updates.Count && updates[index].Op == op)
            {
                group.Add(updates[index].Relationship);
                index++;
            }

            changed += op == ChangeOp.Touch
                ? await InsertAsync(conn, tx, group, revision, ct)
                : await DeleteAsync(conn, tx, group, revision, ct);
        }

        if (changed == 0)
        {
            await tx.RollbackAsync(ct);
            return head;
        }

        await using (var rev = new NpgsqlCommand("INSERT INTO revisions (rev) VALUES (@r)", conn, tx))
        {
            rev.Parameters.AddWithValue("r", revision);
            await rev.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return revision;
    }

    private static NpgsqlCommand ArrayCommand(string sql, NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<Relationship> group, long revision)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("ot", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = group.Select(r => r.Object.Type).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("oi", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = group.Select(r => r.Object.Id).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("rl", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = group.Select(r => r.Relation).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = group.Select(r => r.Subject.Type).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("si", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = group.Select(r => r.Subject.Id).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("sr", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = group.Select(r => r.Subject.Relation ?? string.Empty).ToArray() });
        cmd.Parameters.AddWithValue("rev", revision);
        return cmd;
    }

    private static async Task<int> InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<Relationship> group, long revision, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO relationships (object_type, object_id, relation, subject_type, subject_id, subject_relation, created_rev)
            SELECT DISTINCT t.ot, t.oi, t.rl, t.st, t.si, t.sr, @rev
            FROM unnest(@ot, @oi, @rl, @st, @si, @sr) AS t(ot, oi, rl, st, si, sr)
            ON CONFLICT (object_type, object_id, relation, subject_type, subject_id, subject_relation) WHERE deleted_rev IS NULL DO NOTHING
            """;
        await using var cmd = ArrayCommand(sql, conn, tx, group, revision);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> DeleteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<Relationship> group, long revision, CancellationToken ct)
    {
        const string sql = """
            UPDATE relationships r SET deleted_rev = @rev
            FROM unnest(@ot, @oi, @rl, @st, @si, @sr) AS t(ot, oi, rl, st, si, sr)
            WHERE r.deleted_rev IS NULL
              AND r.object_type = t.ot AND r.object_id = t.oi AND r.relation = t.rl
              AND r.subject_type = t.st AND r.subject_id = t.si AND r.subject_relation = t.sr
            """;
        await using var cmd = ArrayCommand(sql, conn, tx, group, revision);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>>> ReadObjectAsync(ObjectRef obj, long revision, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT relation, subject_type, subject_id, subject_relation FROM relationships
            WHERE object_type = @t AND object_id = @i AND created_rev <= @rev AND (deleted_rev IS NULL OR deleted_rev > @rev)
            """);
        cmd.Parameters.AddWithValue("t", obj.Type);
        cmd.Parameters.AddWithValue("i", obj.Id);
        cmd.Parameters.AddWithValue("rev", revision);
        var result = new Dictionary<string, List<SubjectRef>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var relation = reader.GetString(0);
            var subjectRelation = reader.GetString(3);
            if (!result.TryGetValue(relation, out var list))
            {
                result[relation] = list = [];
            }

            list.Add(new SubjectRef(reader.GetString(1), reader.GetString(2), subjectRelation.Length == 0 ? null : subjectRelation));
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SubjectRef>)kv.Value);
    }

    public async ValueTask<IReadOnlyList<ObjectRef>> ListObjectsAsync(string type, long revision, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT DISTINCT object_id FROM relationships
            WHERE object_type = @t AND created_rev <= @rev AND (deleted_rev IS NULL OR deleted_rev > @rev)
            ORDER BY object_id
            """);
        cmd.Parameters.AddWithValue("t", type);
        cmd.Parameters.AddWithValue("rev", revision);
        var result = new List<ObjectRef>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ObjectRef(type, reader.GetString(0)));
        }

        return result;
    }

    public async ValueTask<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, long revision, int limit, CancellationToken ct)
    {
        var where = new List<string> { "created_rev <= @rev", "(deleted_rev IS NULL OR deleted_rev > @rev)" };
        await using var cmd = dataSource.CreateCommand(string.Empty);
        cmd.Parameters.AddWithValue("rev", revision);
        cmd.Parameters.AddWithValue("lim", limit);

        void Add(string column, string? value, string parameter)
        {
            if (value is not null)
            {
                where.Add($"{column} = @{parameter}");
                cmd.Parameters.AddWithValue(parameter, value);
            }
        }

        Add("object_type", filter.ObjectType, "ot");
        Add("object_id", filter.ObjectId, "oi");
        Add("relation", filter.Relation, "rl");
        Add("subject_type", filter.SubjectType, "st");
        Add("subject_id", filter.SubjectId, "si");
        Add("subject_relation", filter.SubjectRelation, "sr");
        cmd.CommandText = $"SELECT object_type, object_id, relation, subject_type, subject_id, subject_relation FROM relationships WHERE {string.Join(" AND ", where)} ORDER BY id LIMIT @lim";

        var result = new List<Relationship>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var subjectRelation = reader.GetString(5);
            result.Add(new Relationship(
                new ObjectRef(reader.GetString(0), reader.GetString(1)),
                reader.GetString(2),
                new SubjectRef(reader.GetString(3), reader.GetString(4), subjectRelation.Length == 0 ? null : subjectRelation)));
        }

        return result;
    }

    public async ValueTask<IReadOnlyList<RelationshipChange>> ReadChangesAsync(long afterRevision, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT rev, op, object_type, object_id, relation, subject_type, subject_id, subject_relation FROM (
                SELECT created_rev AS rev, 0 AS op, object_type, object_id, relation, subject_type, subject_id, subject_relation FROM relationships WHERE created_rev > @after
                UNION ALL
                SELECT deleted_rev AS rev, 1 AS op, object_type, object_id, relation, subject_type, subject_id, subject_relation FROM relationships WHERE deleted_rev > @after
            ) c ORDER BY rev, op, object_type, object_id, relation LIMIT @lim
            """);
        cmd.Parameters.AddWithValue("after", afterRevision);
        cmd.Parameters.AddWithValue("lim", limit);
        var result = new List<RelationshipChange>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var subjectRelation = reader.GetString(7);
            result.Add(new RelationshipChange(
                reader.GetInt64(0),
                reader.GetInt32(1) == 0 ? ChangeOp.Touch : ChangeOp.Delete,
                new Relationship(
                    new ObjectRef(reader.GetString(2), reader.GetString(3)),
                    reader.GetString(4),
                    new SubjectRef(reader.GetString(5), reader.GetString(6), subjectRelation.Length == 0 ? null : subjectRelation))));
        }

        return result;
    }

    public async Task<(long Version, string Definition)?> LatestSchemaAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT version, definition FROM schemas ORDER BY version DESC LIMIT 1");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    public async Task<long> SaveSchemaAsync(string definition, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("INSERT INTO schemas (definition) VALUES (@d) RETURNING version");
        cmd.Parameters.AddWithValue("d", definition);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }
}
