using System.Collections.Concurrent;

namespace Warden;

public sealed class CheckerOptions
{
    public int MaxDepth { get; set; } = 32;

    public int MaxParallelism { get; set; } = 8;
}

public sealed record ExpandNode(string Kind, string Label, IReadOnlyList<ExpandNode> Children, IReadOnlyList<SubjectRef> Subjects);

public sealed class Checker(Schema schema, ITupleReader reader, CheckerOptions? options = null)
{
    private readonly CheckerOptions _options = options ?? new CheckerOptions();

    internal ITupleReader Reader { get; } = reader;

    private readonly record struct Result(bool Value, int Cut)
    {
        public const int NoCut = int.MaxValue;

        public static Result True => new(true, NoCut);

        public static Result False => new(false, NoCut);
    }

    private sealed class PathNode((ObjectRef Object, string Name) key, int depth, PathNode? parent)
    {
        public (ObjectRef Object, string Name) Key { get; } = key;

        public int Depth { get; } = depth;

        public PathNode? Parent { get; } = parent;
    }

    public sealed class Session
    {
        internal Session(Checker checker, SubjectRef subject, long revision)
        {
            Checker = checker;
            Subject = subject;
            Revision = revision;
        }

        internal Checker Checker { get; }

        internal SubjectRef Subject { get; }

        internal long Revision { get; }

        internal ConcurrentDictionary<(ObjectRef, string), bool> Memo { get; } = new();

        private ConcurrentDictionary<ObjectRef, Task<IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>>>> Objects { get; } = new();

        internal async ValueTask<IReadOnlyList<SubjectRef>> SubjectsAsync(ObjectRef obj, string relation, CancellationToken ct)
        {
            var task = Objects.GetOrAdd(obj, o => Checker.Reader.ReadObjectAsync(o, Revision, CancellationToken.None).AsTask());
            var all = await task.WaitAsync(ct).ConfigureAwait(false);
            return all.TryGetValue(relation, out var subjects) ? subjects : [];
        }

        public async Task<bool> CheckAsync(ObjectRef obj, string name, CancellationToken ct)
        {
            Checker.EnsureKnown(obj, name);
            return (await Checker.EvalAsync(obj, name, null, this, ct).ConfigureAwait(false)).Value;
        }
    }

    public Session CreateSession(SubjectRef subject, long revision) => new(this, subject, revision);

    public Task<bool> CheckAsync(ObjectRef obj, string name, SubjectRef subject, long revision, CancellationToken ct)
        => CreateSession(subject, revision).CheckAsync(obj, name, ct);

    public async Task<IReadOnlyList<ObjectRef>> ListObjectsAsync(string type, string permission, SubjectRef subject, long revision, int limit, CancellationToken ct)
    {
        if (!schema.TryGetType(type, out var definition) || !definition.Has(permission))
        {
            throw new UnknownNameException($"'{type}#{permission}' is not defined in the schema.");
        }

        var candidates = await Reader.ListObjectsAsync(type, revision, ct).ConfigureAwait(false);
        var session = CreateSession(subject, revision);
        var allowed = new ConcurrentBag<ObjectRef>();
        using var gate = new SemaphoreSlim(_options.MaxParallelism * 2);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = candidates.Select(async candidate =>
        {
            await gate.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                if (allowed.Count >= limit)
                {
                    return;
                }

                if (await session.CheckAsync(candidate, permission, stop.Token).ConfigureAwait(false))
                {
                    allowed.Add(candidate);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. allowed.OrderBy(o => o.Id, StringComparer.Ordinal).Take(limit)];
    }

    public async Task<ExpandNode> ExpandAsync(ObjectRef obj, string name, long revision, CancellationToken ct)
    {
        EnsureKnown(obj, name);
        return await ExpandAsync(obj, name, null, revision, ct).ConfigureAwait(false);
    }

    internal void EnsureKnown(ObjectRef obj, string name)
    {
        if (!schema.TryGetType(obj.Type, out var type))
        {
            throw new UnknownNameException($"Unknown object type '{obj.Type}'.");
        }

        if (!type.Has(name))
        {
            throw new UnknownNameException($"'{name}' is not a relation or permission of '{obj.Type}'.");
        }
    }

    private async Task<Result> EvalAsync(ObjectRef obj, string name, PathNode? path, Session session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var depth = path is null ? 0 : path.Depth + 1;
        if (depth > _options.MaxDepth)
        {
            throw new DepthExceededException(_options.MaxDepth);
        }

        var key = (obj, name);
        if (session.Memo.TryGetValue(key, out var known))
        {
            return known ? Result.True : Result.False;
        }

        for (var p = path; p != null; p = p.Parent)
        {
            if (p.Key == key)
            {
                return new Result(false, p.Depth);
            }
        }

        if (!schema.TryGetType(obj.Type, out var type))
        {
            return Result.False;
        }

        var node = new PathNode(key, depth, path);
        Result result;
        if (type.Relations.ContainsKey(name))
        {
            result = await EvalRelationAsync(obj, name, node, session, ct).ConfigureAwait(false);
        }
        else if (type.Permissions.TryGetValue(name, out var permission))
        {
            result = await EvalExprAsync(obj, permission.Expression, node, session, ct).ConfigureAwait(false);
        }
        else
        {
            return Result.False;
        }

        if (result.Cut >= depth)
        {
            session.Memo[key] = result.Value;
            return result.Value ? Result.True : Result.False;
        }

        return result;
    }

    private async Task<Result> EvalRelationAsync(ObjectRef obj, string relation, PathNode node, Session session, CancellationToken ct)
    {
        var subject = session.Subject;
        var subjects = await session.SubjectsAsync(obj, relation, ct).ConfigureAwait(false);
        var usersets = new List<SubjectRef>();
        foreach (var s in subjects)
        {
            if (s.Relation is null)
            {
                if (subject.Relation is null && s.Type == subject.Type && (s.Id == subject.Id || s.IsWildcard))
                {
                    return Result.True;
                }
            }
            else if (s == subject)
            {
                return Result.True;
            }
            else
            {
                usersets.Add(s);
            }
        }

        if (usersets.Count == 0)
        {
            return Result.False;
        }

        return await AnyAsync(
            usersets.Select<SubjectRef, Func<CancellationToken, Task<Result>>>(s => token => EvalAsync(s.AsObject, s.Relation!, node, session, token)).ToList(),
            ct).ConfigureAwait(false);
    }

    private async Task<Result> EvalExprAsync(ObjectRef obj, Expr expression, PathNode node, Session session, CancellationToken ct)
    {
        switch (expression)
        {
            case Expr.Ref reference:
                return await EvalAsync(obj, reference.Name, node, session, ct).ConfigureAwait(false);

            case Expr.Arrow arrow:
            {
                var subjects = await session.SubjectsAsync(obj, arrow.Left, ct).ConfigureAwait(false);
                var branches = new List<Func<CancellationToken, Task<Result>>>();
                foreach (var s in subjects)
                {
                    if (s.Relation is not null || s.IsWildcard || !schema.TryGetType(s.Type, out var target) || !target.Has(arrow.Right))
                    {
                        continue;
                    }

                    var next = s.AsObject;
                    branches.Add(token => EvalAsync(next, arrow.Right, node, session, token));
                }

                return await AnyAsync(branches, ct).ConfigureAwait(false);
            }

            case Expr.Union union:
                return await AnyAsync(
                    [token => EvalExprAsync(obj, union.Left, node, session, token), token => EvalExprAsync(obj, union.Right, node, session, token)],
                    ct).ConfigureAwait(false);

            case Expr.Intersection intersection:
            {
                var left = await EvalExprAsync(obj, intersection.Left, node, session, ct).ConfigureAwait(false);
                if (!left.Value)
                {
                    return left;
                }

                var right = await EvalExprAsync(obj, intersection.Right, node, session, ct).ConfigureAwait(false);
                return right.Value ? Result.True : right;
            }

            case Expr.Exclusion exclusion:
            {
                var left = await EvalExprAsync(obj, exclusion.Left, node, session, ct).ConfigureAwait(false);
                if (!left.Value)
                {
                    return left;
                }

                var right = await EvalExprAsync(obj, exclusion.Right, node, session, ct).ConfigureAwait(false);
                return right.Value ? Result.False : new Result(true, right.Cut);
            }

            default:
                return Result.False;
        }
    }

    private async Task<Result> AnyAsync(IReadOnlyList<Func<CancellationToken, Task<Result>>> branches, CancellationToken ct)
    {
        if (branches.Count == 0)
        {
            return Result.False;
        }

        if (branches.Count == 1)
        {
            return await branches[0](ct).ConfigureAwait(false);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<Result>>();
        var next = 0;
        var cut = Result.NoCut;
        try
        {
            while (next < branches.Count || pending.Count > 0)
            {
                while (next < branches.Count && pending.Count < _options.MaxParallelism)
                {
                    pending.Add(branches[next++](stop.Token));
                }

                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);
                var result = await finished.ConfigureAwait(false);
                if (result.Value)
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                    await DrainAsync(pending).ConfigureAwait(false);
                    return Result.True;
                }

                cut = Math.Min(cut, result.Cut);
            }

            return new Result(false, cut);
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await DrainAsync(pending).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DrainAsync(List<Task<Result>> pending)
    {
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task<ExpandNode> ExpandAsync(ObjectRef obj, string name, PathNode? path, long revision, CancellationToken ct)
    {
        var depth = path is null ? 0 : path.Depth + 1;
        var label = $"{obj}#{name}";
        if (depth > _options.MaxDepth)
        {
            throw new DepthExceededException(_options.MaxDepth);
        }

        var key = (obj, name);
        for (var p = path; p != null; p = p.Parent)
        {
            if (p.Key == key)
            {
                return new ExpandNode("cycle", label, [], []);
            }
        }

        if (!schema.TryGetType(obj.Type, out var type))
        {
            return new ExpandNode("leaf", label, [], []);
        }

        var node = new PathNode(key, depth, path);
        if (type.Relations.ContainsKey(name))
        {
            var subjects = await Reader.ReadSubjectsAsync(obj, name, revision, ct).ConfigureAwait(false);
            var children = new List<ExpandNode>();
            foreach (var userset in subjects.Where(s => s.Relation is not null))
            {
                children.Add(await ExpandAsync(userset.AsObject, userset.Relation!, node, revision, ct).ConfigureAwait(false));
            }

            return new ExpandNode("leaf", label, children, [.. subjects.Where(s => s.Relation is null)]);
        }

        if (type.Permissions.TryGetValue(name, out var permission))
        {
            return await ExpandExprAsync(obj, permission.Expression, label, node, revision, ct).ConfigureAwait(false);
        }

        return new ExpandNode("leaf", label, [], []);
    }

    private async Task<ExpandNode> ExpandExprAsync(ObjectRef obj, Expr expression, string label, PathNode node, long revision, CancellationToken ct)
    {
        switch (expression)
        {
            case Expr.Ref reference:
                return await ExpandAsync(obj, reference.Name, node, revision, ct).ConfigureAwait(false);
            case Expr.Arrow arrow:
            {
                var subjects = await Reader.ReadSubjectsAsync(obj, arrow.Left, revision, ct).ConfigureAwait(false);
                var children = new List<ExpandNode>();
                foreach (var s in subjects.Where(s => s.Relation is null && !s.IsWildcard && schema.TryGetType(s.Type, out var t) && t.Has(arrow.Right)))
                {
                    children.Add(await ExpandAsync(s.AsObject, arrow.Right, node, revision, ct).ConfigureAwait(false));
                }

                return new ExpandNode("union", $"{label} ({arrow})", children, []);
            }

            case Expr.Union union:
                return new ExpandNode("union", label, [await ExpandExprAsync(obj, union.Left, label, node, revision, ct).ConfigureAwait(false), await ExpandExprAsync(obj, union.Right, label, node, revision, ct).ConfigureAwait(false)], []);
            case Expr.Intersection intersection:
                return new ExpandNode("intersection", label, [await ExpandExprAsync(obj, intersection.Left, label, node, revision, ct).ConfigureAwait(false), await ExpandExprAsync(obj, intersection.Right, label, node, revision, ct).ConfigureAwait(false)], []);
            case Expr.Exclusion exclusion:
                return new ExpandNode("exclusion", label, [await ExpandExprAsync(obj, exclusion.Left, label, node, revision, ct).ConfigureAwait(false), await ExpandExprAsync(obj, exclusion.Right, label, node, revision, ct).ConfigureAwait(false)], []);
            default:
                return new ExpandNode("leaf", label, [], []);
        }
    }
}
