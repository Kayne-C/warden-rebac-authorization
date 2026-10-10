namespace Warden;

public interface ITupleReader
{
    ValueTask<IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>>> ReadObjectAsync(ObjectRef obj, long revision, CancellationToken ct);

    ValueTask<IReadOnlyList<ObjectRef>> ListObjectsAsync(string type, long revision, CancellationToken ct);

    ValueTask<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, long revision, int limit, CancellationToken ct);
}

public static class TupleReaderExtensions
{
    public static async ValueTask<IReadOnlyList<SubjectRef>> ReadSubjectsAsync(this ITupleReader reader, ObjectRef obj, string relation, long revision, CancellationToken ct)
    {
        var all = await reader.ReadObjectAsync(obj, revision, ct).ConfigureAwait(false);
        return all.TryGetValue(relation, out var subjects) ? subjects : [];
    }
}

public interface IRelationshipStore : ITupleReader
{
    ValueTask<long> HeadRevisionAsync(CancellationToken ct);

    ValueTask<long> WriteAsync(IReadOnlyList<RelationshipUpdate> updates, CancellationToken ct);

    ValueTask<IReadOnlyList<RelationshipChange>> ReadChangesAsync(long afterRevision, int limit, CancellationToken ct);
}

public sealed class InMemoryRelationshipStore : IRelationshipStore
{
    private sealed class Entry(Relationship relationship, long created)
    {
        public Relationship Relationship { get; } = relationship;

        public long Created { get; } = created;

        public long? Deleted { get; set; }

        public bool VisibleAt(long revision) => Created <= revision && (Deleted is null || Deleted > revision);
    }

    private readonly object _gate = new();
    private readonly List<Entry> _all = [];
    private readonly Dictionary<ObjectRef, Dictionary<string, List<Entry>>> _byObject = [];
    private readonly Dictionary<Relationship, Entry> _active = [];
    private long _head;

    public ValueTask<long> HeadRevisionAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_head);
        }
    }

    public ValueTask<long> WriteAsync(IReadOnlyList<RelationshipUpdate> updates, CancellationToken ct)
    {
        lock (_gate)
        {
            var revision = _head + 1;
            var changed = false;
            foreach (var update in updates)
            {
                if (update.Op == ChangeOp.Touch)
                {
                    if (_active.ContainsKey(update.Relationship))
                    {
                        continue;
                    }

                    var entry = new Entry(update.Relationship, revision);
                    _all.Add(entry);
                    _active[update.Relationship] = entry;
                    if (!_byObject.TryGetValue(update.Relationship.Object, out var relations))
                    {
                        _byObject[update.Relationship.Object] = relations = [];
                    }

                    if (!relations.TryGetValue(update.Relationship.Relation, out var list))
                    {
                        relations[update.Relationship.Relation] = list = [];
                    }

                    list.Add(entry);
                    changed = true;
                }
                else if (_active.Remove(update.Relationship, out var existing))
                {
                    existing.Deleted = revision;
                    changed = true;
                }
            }

            if (changed)
            {
                _head = revision;
            }

            return ValueTask.FromResult(_head);
        }
    }

    public ValueTask<IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>>> ReadObjectAsync(ObjectRef obj, long revision, CancellationToken ct)
    {
        lock (_gate)
        {
            var result = new Dictionary<string, IReadOnlyList<SubjectRef>>();
            if (_byObject.TryGetValue(obj, out var relations))
            {
                foreach (var (relation, list) in relations)
                {
                    var visible = list.Where(e => e.VisibleAt(revision)).Select(e => e.Relationship.Subject).ToList();
                    if (visible.Count > 0)
                    {
                        result[relation] = visible;
                    }
                }
            }

            return ValueTask.FromResult<IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>>>(result);
        }
    }

    public ValueTask<IReadOnlyList<ObjectRef>> ListObjectsAsync(string type, long revision, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<ObjectRef>>([.. _all
                .Where(e => e.Relationship.Object.Type == type && e.VisibleAt(revision))
                .Select(e => e.Relationship.Object)
                .Distinct()
                .OrderBy(o => o.Id, StringComparer.Ordinal)]);
        }
    }

    public ValueTask<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, long revision, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<Relationship>>([.. _all
                .Where(e => e.VisibleAt(revision) && Matches(filter, e.Relationship))
                .Take(limit)
                .Select(e => e.Relationship)]);
        }
    }

    public ValueTask<IReadOnlyList<RelationshipChange>> ReadChangesAsync(long afterRevision, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            var changes = _all.Where(e => e.Created > afterRevision).Select(e => new RelationshipChange(e.Created, ChangeOp.Touch, e.Relationship))
                .Concat(_all.Where(e => e.Deleted > afterRevision).Select(e => new RelationshipChange(e.Deleted!.Value, ChangeOp.Delete, e.Relationship)))
                .OrderBy(c => c.Revision)
                .Take(limit)
                .ToList();
            return ValueTask.FromResult<IReadOnlyList<RelationshipChange>>(changes);
        }
    }

    internal static bool Matches(RelationshipFilter filter, Relationship r)
        => (filter.ObjectType is null || r.Object.Type == filter.ObjectType)
           && (filter.ObjectId is null || r.Object.Id == filter.ObjectId)
           && (filter.Relation is null || r.Relation == filter.Relation)
           && (filter.SubjectType is null || r.Subject.Type == filter.SubjectType)
           && (filter.SubjectId is null || r.Subject.Id == filter.SubjectId)
           && (filter.SubjectRelation is null || r.Subject.Relation == filter.SubjectRelation);
}
