namespace Warden;

public sealed record AllowedSubject(string Type, string? Relation, bool Wildcard)
{
    public override string ToString() => Wildcard ? $"{Type}:*" : Relation is null ? Type : $"{Type}#{Relation}";
}

public sealed record RelationDefinition(string Name, IReadOnlyList<AllowedSubject> Allowed);

public abstract record Expr
{
    public sealed record Ref(string Name) : Expr
    {
        public override string ToString() => Name;
    }

    public sealed record Arrow(string Left, string Right) : Expr
    {
        public override string ToString() => $"{Left}->{Right}";
    }

    public sealed record Union(Expr Left, Expr Right) : Expr
    {
        public override string ToString() => $"({Left} + {Right})";
    }

    public sealed record Intersection(Expr Left, Expr Right) : Expr
    {
        public override string ToString() => $"({Left} & {Right})";
    }

    public sealed record Exclusion(Expr Left, Expr Right) : Expr
    {
        public override string ToString() => $"({Left} - {Right})";
    }
}

public sealed record PermissionDefinition(string Name, Expr Expression);

public sealed class TypeDefinition(string name, IReadOnlyDictionary<string, RelationDefinition> relations, IReadOnlyDictionary<string, PermissionDefinition> permissions)
{
    public string Name { get; } = name;

    public IReadOnlyDictionary<string, RelationDefinition> Relations { get; } = relations;

    public IReadOnlyDictionary<string, PermissionDefinition> Permissions { get; } = permissions;

    public bool Has(string name) => Relations.ContainsKey(name) || Permissions.ContainsKey(name);
}

public sealed class Schema
{
    public Schema(IReadOnlyDictionary<string, TypeDefinition> types)
    {
        Types = types;
    }

    public IReadOnlyDictionary<string, TypeDefinition> Types { get; }

    public bool TryGetType(string name, out TypeDefinition definition) => Types.TryGetValue(name, out definition!);

    public static Schema Parse(string text) => SchemaParser.Parse(text);

    public string? ValidateRelationship(Relationship relationship)
    {
        if (!Types.TryGetValue(relationship.Object.Type, out var type))
        {
            return $"Unknown object type '{relationship.Object.Type}'.";
        }

        if (!type.Relations.TryGetValue(relationship.Relation, out var relation))
        {
            return type.Permissions.ContainsKey(relationship.Relation)
                ? $"'{relationship.Relation}' on '{type.Name}' is a permission and cannot be written to."
                : $"Unknown relation '{relationship.Relation}' on '{type.Name}'.";
        }

        var subject = relationship.Subject;
        if (!Types.ContainsKey(subject.Type))
        {
            return $"Unknown subject type '{subject.Type}'.";
        }

        var allowed = relation.Allowed.Any(a =>
            a.Type == subject.Type
            && (subject.IsWildcard ? a.Wildcard : !a.Wildcard && a.Relation == subject.Relation));

        return allowed
            ? null
            : $"Subject '{subject}' is not allowed on '{type.Name}#{relationship.Relation}'. Allowed: {string.Join(" | ", relation.Allowed)}.";
    }
}

public sealed record SchemaError(int Line, int Column, string Message)
{
    public override string ToString() => $"({Line}:{Column}) {Message}";
}

public sealed class SchemaException(IReadOnlyList<SchemaError> errors) : WardenException(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<SchemaError> Errors { get; } = errors;
}
