using Xunit;

namespace Warden.Tests;

public class ParserTests
{
    [Fact]
    public void A_realistic_schema_is_parsed_into_types_relations_and_permissions()
    {
        var schema = Schema.Parse(Schemas.Docs);

        Assert.Equal(["document", "folder", "group", "user"], schema.Types.Keys.Order());
        var folder = schema.Types["folder"];
        Assert.Equal(["editor", "owner", "parent", "viewer"], folder.Relations.Keys.Order());
        Assert.Equal(["edit", "view"], folder.Permissions.Keys.Order());
        Assert.Equal(["user", "group#member", "user:*"], folder.Relations["viewer"].Allowed.Select(a => a.ToString()));
    }

    [Fact]
    public void Operators_have_the_documented_precedence_and_associativity()
    {
        var schema = Schema.Parse("""
            definition user {}
            definition doc {
              relation a: user
              relation b: user
              relation c: user
              permission p = a + b & c
              permission q = a - b - c
              permission r = (a + b) & c
            }
            """);

        Assert.Equal("(a + (b & c))", schema.Types["doc"].Permissions["p"].Expression.ToString());
        Assert.Equal("((a - b) - c)", schema.Types["doc"].Permissions["q"].Expression.ToString());
        Assert.Equal("((a + b) & c)", schema.Types["doc"].Permissions["r"].Expression.ToString());
    }

    [Fact]
    public void Arrows_comments_and_odd_whitespace_are_accepted()
    {
        var schema = Schema.Parse("""
            // line comment
            definition user {}
            /* block
               comment */
            definition folder { relation parent: folder
              relation owner: user permission view=owner+parent->view }
            """);

        Assert.Equal("(owner + parent->view)", schema.Types["folder"].Permissions["view"].Expression.ToString());
    }

    [Theory]
    [InlineData("definition user {", "end of the schema")]
    [InlineData("definition {}", "a type name")]
    [InlineData("definition user { relation : user }", "a relation name")]
    [InlineData("definition user { relation r user }", "':'")]
    [InlineData("definition user { permission p }", "'='")]
    [InlineData("definition user { permission p = (a + b }", "')'")]
    [InlineData("definition user { thing x }", "'relation', 'permission' or '}'")]
    [InlineData("user {}", "'definition'")]
    [InlineData("definition user { relation r: user:x }", "'*'")]
    public void Syntax_errors_point_at_the_problem(string text, string expected)
    {
        var exception = Assert.Throws<SchemaException>(() => Schema.Parse(text));

        var error = Assert.Single(exception.Errors);
        Assert.Contains(expected, error.Message);
        Assert.True(error.Line >= 1 && error.Column >= 1);
    }

    [Fact]
    public void Error_positions_are_line_and_column_accurate()
    {
        var exception = Assert.Throws<SchemaException>(() => Schema.Parse("definition user {}\ndefinition doc {\n  relation r user\n}"));

        var error = Assert.Single(exception.Errors);
        Assert.Equal(3, error.Line);
        Assert.Equal(14, error.Column);
    }

    [Fact]
    public void An_unterminated_comment_and_a_stray_character_are_reported()
    {
        Assert.Contains("Unterminated", Assert.Throws<SchemaException>(() => Schema.Parse("/* never closed")).Errors[0].Message);
        Assert.Contains("'@'", Assert.Throws<SchemaException>(() => Schema.Parse("definition a { @ }")).Errors[0].Message);
    }

    [Fact]
    public void Semantic_mistakes_are_all_reported_together()
    {
        var exception = Assert.Throws<SchemaException>(() => Schema.Parse("""
            definition user {}
            definition user {}
            definition doc {
              relation owner: ghost
              relation owner: user
              relation viewer: user#nothing
              permission view = owner + missing
              permission owner = viewer
              permission both = parent->view
            }
            """));

        var messages = string.Join("\n", exception.Errors.Select(e => e.Message));
        Assert.Contains("'user' is defined more than once", messages);
        Assert.Contains("unknown type 'ghost'", messages);
        Assert.Contains("'doc#owner' is defined more than once", messages);
        Assert.Contains("'user#nothing', which does not exist", messages);
        Assert.Contains("'missing', which is not a relation or permission", messages);
        Assert.Contains("both a relation and a permission", messages);
        Assert.Contains("'parent' is not a relation", messages);
    }

    [Fact]
    public void An_arrow_must_start_from_a_relation_and_end_at_something_that_exists()
    {
        var onPermission = Assert.Throws<SchemaException>(() => Schema.Parse("""
            definition user {}
            definition doc {
              relation owner: user
              permission p = owner
              permission q = p->x
            }
            """));
        var missingTarget = Assert.Throws<SchemaException>(() => Schema.Parse("""
            definition user {}
            definition folder { relation owner: user }
            definition doc {
              relation parent: folder
              permission view = parent->nothing
            }
            """));

        Assert.Contains("must be a relation, not a permission", onPermission.Errors[0].Message);
        Assert.Contains("'folder' has no relation or permission 'nothing'", missingTarget.Errors[0].Message);
    }

    [Fact]
    public void Permissions_defined_in_terms_of_themselves_are_rejected()
    {
        var exception = Assert.Throws<SchemaException>(() => Schema.Parse("""
            definition user {}
            definition doc {
              relation owner: user
              permission a = b + owner
              permission b = c
              permission c = a
            }
            """));

        Assert.Contains(exception.Errors, e => e.Message.Contains("in terms of itself"));
    }

    [Fact]
    public void Recursion_through_data_is_allowed()
    {
        var schema = Schema.Parse("""
            definition user {}
            definition folder {
              relation parent: folder
              relation viewer: user
              permission view = viewer + parent->view
            }
            """);

        Assert.True(schema.Types["folder"].Permissions.ContainsKey("view"));
    }

    [Fact]
    public void Written_relationships_are_validated_against_the_schema()
    {
        var schema = Schema.Parse(Schemas.Docs);

        Assert.Null(schema.ValidateRelationship(new Relationship(T.O("document:d"), "viewer", T.S("user:ayse"))));
        Assert.Null(schema.ValidateRelationship(new Relationship(T.O("document:d"), "viewer", T.S("group:eng#member"))));
        Assert.Null(schema.ValidateRelationship(new Relationship(T.O("document:d"), "viewer", T.S("user:*"))));
        Assert.Contains("Unknown relation", schema.ValidateRelationship(new Relationship(T.O("document:d"), "reader", T.S("user:ayse"))));
        Assert.Contains("is a permission", schema.ValidateRelationship(new Relationship(T.O("document:d"), "view", T.S("user:ayse"))));
        Assert.Contains("not allowed", schema.ValidateRelationship(new Relationship(T.O("document:d"), "owner", T.S("group:eng#member"))));
        Assert.Contains("not allowed", schema.ValidateRelationship(new Relationship(T.O("document:d"), "owner", T.S("user:*"))));
        Assert.Contains("Unknown object type", schema.ValidateRelationship(new Relationship(T.O("spaceship:s"), "owner", T.S("user:a"))));
        Assert.Contains("Unknown subject type", schema.ValidateRelationship(new Relationship(T.O("document:d"), "owner", T.S("alien:a"))));
    }

    [Fact]
    public void References_round_trip_through_text()
    {
        Assert.Equal(T.S("group:eng#member"), SubjectRef.Parse("group:eng#member"));
        Assert.Equal("document:42", T.O("document:42").ToString());
        Assert.Throws<FormatException>(() => ObjectRef.Parse("nonsense"));
        Assert.Throws<FormatException>(() => ObjectRef.Parse(":x"));
    }
}
