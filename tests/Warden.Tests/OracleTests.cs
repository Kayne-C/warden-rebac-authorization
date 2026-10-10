using Xunit;
using static Warden.Tests.T;

namespace Warden.Tests;

public class OracleTests
{
    private sealed class Graph
    {
        public List<RelationshipUpdate> Updates { get; } = [];

        public List<string> Users { get; } = [];

        public List<string> Objects { get; } = [];
    }

    private static Graph Generate(int seed, bool allowCycles, bool withBans)
    {
        var random = new Random(seed);
        var graph = new Graph();
        var users = Enumerable.Range(0, 6).Select(i => $"user:u{i}").ToList();
        var groups = Enumerable.Range(0, 4).Select(i => $"group:g{i}").ToList();
        var folders = Enumerable.Range(0, 6).Select(i => $"folder:f{i}").ToList();
        var documents = Enumerable.Range(0, 10).Select(i => $"document:d{i}").ToList();
        graph.Users.AddRange(users);
        graph.Objects.AddRange(folders);
        graph.Objects.AddRange(documents);
        var seen = new HashSet<string>();

        void Add(string obj, string relation, string subject)
        {
            if (seen.Add($"{obj}#{relation}@{subject}"))
            {
                graph.Updates.Add(Touch(obj, relation, subject));
            }
        }

        string Subject(bool allowWildcard)
        {
            var roll = random.Next(10);
            if (roll < 6)
            {
                return users[random.Next(users.Count)];
            }

            if (roll < 9 || !allowWildcard)
            {
                return groups[random.Next(groups.Count)] + "#member";
            }

            return "user:*";
        }

        for (var i = 0; i < groups.Count; i++)
        {
            for (var n = random.Next(0, 4); n > 0; n--)
            {
                if (random.Next(4) == 0)
                {
                    var other = random.Next(groups.Count);
                    if (allowCycles || other > i)
                    {
                        Add(groups[i], "member", groups[other] + "#member");
                    }
                }
                else
                {
                    Add(groups[i], "member", users[random.Next(users.Count)]);
                }
            }
        }

        for (var i = 0; i < folders.Count; i++)
        {
            if (random.Next(3) > 0)
            {
                var parent = random.Next(folders.Count);
                if (allowCycles ? parent != i : parent < i)
                {
                    Add(folders[i], "parent", folders[parent]);
                }
            }

            if (random.Next(3) == 0)
            {
                Add(folders[i], "owner", users[random.Next(users.Count)]);
            }

            for (var n = random.Next(0, 3); n > 0; n--)
            {
                var relation = random.Next(2) == 0 ? "editor" : "viewer";
                Add(folders[i], relation, Subject(allowWildcard: relation == "viewer"));
            }
        }

        foreach (var document in documents)
        {
            if (random.Next(4) > 0)
            {
                Add(document, "parent", folders[random.Next(folders.Count)]);
            }

            if (random.Next(3) == 0)
            {
                Add(document, "owner", users[random.Next(users.Count)]);
            }

            for (var n = random.Next(0, 3); n > 0; n--)
            {
                var relation = random.Next(2) == 0 ? "editor" : "viewer";
                Add(document, relation, Subject(allowWildcard: relation == "viewer"));
            }

            if (withBans && random.Next(4) == 0)
            {
                Add(document, "banned", users[random.Next(users.Count)]);
            }
        }

        return graph;
    }

    private sealed class Oracle(Schema schema, IReadOnlyList<Relationship> relationships)
    {
        private readonly ILookup<(ObjectRef, string), SubjectRef> _subjects = relationships.ToLookup(r => (r.Object, r.Relation), r => r.Subject);

        public HashSet<(ObjectRef, string)> TruthFor(SubjectRef subject, IEnumerable<ObjectRef> objects)
        {
            var truth = new HashSet<(ObjectRef, string)>();
            var all = objects.ToList();
            for (var round = 0; round < 64; round++)
            {
                var changed = false;
                foreach (var obj in all)
                {
                    if (!schema.TryGetType(obj.Type, out var type))
                    {
                        continue;
                    }

                    foreach (var name in type.Relations.Keys.Concat(type.Permissions.Keys))
                    {
                        if (!truth.Contains((obj, name)) && Evaluate(obj, name, subject, truth))
                        {
                            truth.Add((obj, name));
                            changed = true;
                        }
                    }
                }

                if (!changed)
                {
                    break;
                }
            }

            return truth;
        }

        private bool Evaluate(ObjectRef obj, string name, SubjectRef subject, HashSet<(ObjectRef, string)> truth)
        {
            var type = schema.Types[obj.Type];
            if (type.Relations.ContainsKey(name))
            {
                foreach (var s in _subjects[(obj, name)])
                {
                    if (s.Relation is null)
                    {
                        if (s.Type == subject.Type && (s.Id == subject.Id || s.IsWildcard))
                        {
                            return true;
                        }
                    }
                    else if (truth.Contains((s.AsObject, s.Relation)))
                    {
                        return true;
                    }
                }

                return false;
            }

            return Eval(type.Permissions[name].Expression, obj, subject, truth);
        }

        private bool Eval(Expr expression, ObjectRef obj, SubjectRef subject, HashSet<(ObjectRef, string)> truth) => expression switch
        {
            Expr.Ref r => truth.Contains((obj, r.Name)),
            Expr.Arrow a => _subjects[(obj, a.Left)].Any(s => s.Relation is null && !s.IsWildcard && truth.Contains((s.AsObject, a.Right))),
            Expr.Union u => Eval(u.Left, obj, subject, truth) || Eval(u.Right, obj, subject, truth),
            Expr.Intersection i => Eval(i.Left, obj, subject, truth) && Eval(i.Right, obj, subject, truth),
            Expr.Exclusion e => Eval(e.Left, obj, subject, truth) && !Eval(e.Right, obj, subject, truth),
            _ => false,
        };
    }

    private static async Task AssertAgrees(string schemaText, Graph graph, string label)
    {
        var schema = Schema.Parse(schemaText);
        Assert.All(graph.Updates, u => Assert.Null(schema.ValidateRelationship(u.Relationship)));
        var store = new InMemoryRelationshipStore();
        await store.WriteAsync(graph.Updates, default);
        var head = await store.HeadRevisionAsync(default);
        var checker = new Checker(schema, store);
        var oracle = new Oracle(schema, graph.Updates.Select(u => u.Relationship).ToList());
        var objects = graph.Objects.Select(ObjectRef.Parse).Concat(graph.Updates.SelectMany(u => new[] { u.Relationship.Object })).Distinct().ToList();

        foreach (var user in graph.Users)
        {
            var subject = SubjectRef.Parse(user);
            var truth = oracle.TruthFor(subject, objects);
            var session = checker.CreateSession(subject, head);
            foreach (var obj in objects.Where(o => o.Type is "document" or "folder"))
            {
                foreach (var permission in schema.Types[obj.Type].Permissions.Keys)
                {
                    var actual = await session.CheckAsync(obj, permission, default);
                    Assert.True(
                        truth.Contains((obj, permission)) == actual,
                        $"{label}: {user} {permission} {obj}: oracle={truth.Contains((obj, permission))} checker={actual}");
                }
            }
        }
    }

    [Fact]
    public async Task On_graphs_with_cycles_the_union_schema_matches_a_bottom_up_oracle()
    {
        for (var seed = 1; seed <= 150; seed++)
        {
            await AssertAgrees(Schemas.UnionOnly, Generate(seed, allowCycles: true, withBans: false), $"cyclic seed {seed}");
        }
    }

    [Fact]
    public async Task With_exclusion_and_intersection_on_acyclic_graphs_the_checker_matches_the_oracle()
    {
        for (var seed = 1000; seed <= 1150; seed++)
        {
            await AssertAgrees(Schemas.Docs, Generate(seed, allowCycles: false, withBans: true), $"acyclic seed {seed}");
        }
    }

    [Fact]
    public async Task Sharing_one_session_across_many_objects_gives_the_same_answers_as_isolated_checks()
    {
        for (var seed = 2000; seed <= 2040; seed++)
        {
            var graph = Generate(seed, allowCycles: true, withBans: false);
            var schema = Schema.Parse(Schemas.UnionOnly);
            var store = new InMemoryRelationshipStore();
            await store.WriteAsync(graph.Updates, default);
            var head = await store.HeadRevisionAsync(default);
            var checker = new Checker(schema, store);

            foreach (var user in graph.Users.Take(3))
            {
                var shared = checker.CreateSession(SubjectRef.Parse(user), head);
                foreach (var obj in graph.Objects)
                {
                    var type = ObjectRef.Parse(obj).Type;
                    var isolated = await checker.CheckAsync(ObjectRef.Parse(obj), "view", SubjectRef.Parse(user), head, default);
                    Assert.Equal(isolated, await shared.CheckAsync(ObjectRef.Parse(obj), "view", default));
                    Assert.NotNull(type);
                }
            }
        }
    }
}
