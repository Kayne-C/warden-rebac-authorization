using Xunit;

namespace Warden.Tests;

public static class Schemas
{
    public const string Docs = """
        definition user {}

        definition group {
          relation member: user | group#member
        }

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
          permission share = edit & viewer
        }
        """;

    public const string UnionOnly = """
        definition user {}

        definition group {
          relation member: user | group#member
        }

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

          permission edit = owner + editor + parent->edit
          permission view = edit + viewer + parent->view
        }
        """;
}

public static class T
{
    public static ObjectRef O(string text) => ObjectRef.Parse(text);

    public static SubjectRef S(string text) => SubjectRef.Parse(text);

    public static RelationshipUpdate Touch(string obj, string relation, string subject)
        => new(ChangeOp.Touch, new Relationship(ObjectRef.Parse(obj), relation, SubjectRef.Parse(subject)));

    public static RelationshipUpdate Delete(string obj, string relation, string subject)
        => new(ChangeOp.Delete, new Relationship(ObjectRef.Parse(obj), relation, SubjectRef.Parse(subject)));

    public static async Task<(Checker Checker, InMemoryRelationshipStore Store)> Build(string schemaText, params RelationshipUpdate[] updates)
    {
        var schema = Schema.Parse(schemaText);
        var store = new InMemoryRelationshipStore();
        foreach (var update in updates)
        {
            Assert.Null(schema.ValidateRelationship(update.Relationship));
        }

        await store.WriteAsync(updates, default);
        return (new Checker(schema, store), store);
    }

    public static async Task<bool> Can(Checker checker, InMemoryRelationshipStore store, string obj, string permission, string subject)
        => await checker.CheckAsync(ObjectRef.Parse(obj), permission, SubjectRef.Parse(subject), await store.HeadRevisionAsync(default), default);
}
