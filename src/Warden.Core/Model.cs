namespace Warden;

public readonly record struct ObjectRef(string Type, string Id)
{
    public override string ToString() => $"{Type}:{Id}";

    public static ObjectRef Parse(string text)
    {
        var index = text.IndexOf(':');
        if (index <= 0 || index == text.Length - 1)
        {
            throw new FormatException($"'{text}' is not an object reference of the form type:id.");
        }

        return new ObjectRef(text[..index], text[(index + 1)..]);
    }
}

public readonly record struct SubjectRef(string Type, string Id, string? Relation = null)
{
    public const string Wildcard = "*";

    public bool IsWildcard => Id == Wildcard;

    public ObjectRef AsObject => new(Type, Id);

    public override string ToString() => Relation is null ? $"{Type}:{Id}" : $"{Type}:{Id}#{Relation}";

    public static SubjectRef Parse(string text)
    {
        var relation = (string?)null;
        var hash = text.IndexOf('#');
        if (hash >= 0)
        {
            relation = text[(hash + 1)..];
            text = text[..hash];
        }

        var obj = ObjectRef.Parse(text);
        return new SubjectRef(obj.Type, obj.Id, relation);
    }
}

public readonly record struct Relationship(ObjectRef Object, string Relation, SubjectRef Subject)
{
    public override string ToString() => $"{Object}#{Relation}@{Subject}";
}

public enum ChangeOp
{
    Touch,
    Delete,
}

public readonly record struct RelationshipUpdate(ChangeOp Op, Relationship Relationship);

public readonly record struct RelationshipChange(long Revision, ChangeOp Op, Relationship Relationship);

public sealed record RelationshipFilter(string? ObjectType = null, string? ObjectId = null, string? Relation = null, string? SubjectType = null, string? SubjectId = null, string? SubjectRelation = null);

public class WardenException(string message) : Exception(message);

public sealed class DepthExceededException(int maxDepth) : WardenException($"The check exceeded the maximum depth of {maxDepth}. The relationship graph is deeper than allowed or contains a very long chain.");

public sealed class UnknownNameException(string message) : WardenException(message);
