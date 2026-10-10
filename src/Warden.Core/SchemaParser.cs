using System.Text;

namespace Warden;

internal enum TokenKind
{
    Identifier,
    LeftBrace,
    RightBrace,
    LeftParen,
    RightParen,
    Colon,
    Pipe,
    Hash,
    Star,
    Equals,
    Plus,
    Minus,
    Ampersand,
    Arrow,
    End,
}

internal readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

internal sealed class Lexer(string text)
{
    private int _position;
    private int _line = 1;
    private int _column = 1;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipTrivia();
            if (_position >= text.Length)
            {
                tokens.Add(new Token(TokenKind.End, string.Empty, _line, _column));
                return tokens;
            }

            var c = text[_position];
            var line = _line;
            var column = _column;
            if (char.IsLetter(c) || c == '_')
            {
                var start = _position;
                while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_'))
                {
                    Advance();
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start.._position], line, column));
                continue;
            }

            if (c == '-' && _position + 1 < text.Length && text[_position + 1] == '>')
            {
                Advance();
                Advance();
                tokens.Add(new Token(TokenKind.Arrow, "->", line, column));
                continue;
            }

            var kind = c switch
            {
                '{' => TokenKind.LeftBrace,
                '}' => TokenKind.RightBrace,
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                ':' => TokenKind.Colon,
                '|' => TokenKind.Pipe,
                '#' => TokenKind.Hash,
                '*' => TokenKind.Star,
                '=' => TokenKind.Equals,
                '+' => TokenKind.Plus,
                '-' => TokenKind.Minus,
                '&' => TokenKind.Ampersand,
                _ => throw new SchemaException([new SchemaError(line, column, $"Unexpected character '{c}'.")]),
            };
            Advance();
            tokens.Add(new Token(kind, c.ToString(), line, column));
        }
    }

    private void Advance()
    {
        if (text[_position] == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }

        _position++;
    }

    private void SkipTrivia()
    {
        while (_position < text.Length)
        {
            var c = text[_position];
            if (char.IsWhiteSpace(c))
            {
                Advance();
            }
            else if (c == '/' && _position + 1 < text.Length && text[_position + 1] == '/')
            {
                while (_position < text.Length && text[_position] != '\n')
                {
                    Advance();
                }
            }
            else if (c == '/' && _position + 1 < text.Length && text[_position + 1] == '*')
            {
                var line = _line;
                var column = _column;
                Advance();
                Advance();
                while (_position + 1 < text.Length && !(text[_position] == '*' && text[_position + 1] == '/'))
                {
                    Advance();
                }

                if (_position + 1 >= text.Length)
                {
                    throw new SchemaException([new SchemaError(line, column, "Unterminated comment.")]);
                }

                Advance();
                Advance();
            }
            else
            {
                return;
            }
        }
    }
}

internal sealed record RawRelation(string Name, IReadOnlyList<AllowedSubject> Allowed, int Line, int Column);

internal sealed record RawPermission(string Name, Expr Expression, int Line, int Column);

internal sealed record RawType(string Name, List<RawRelation> Relations, List<RawPermission> Permissions, int Line, int Column);

public static class SchemaParser
{
    public static Schema Parse(string text)
    {
        var tokens = new Lexer(text).Tokenize();
        var types = new Parser(tokens).ParseSchema();
        return SchemaValidator.Build(types);
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;

        private Token Current => tokens[_index];

        public List<RawType> ParseSchema()
        {
            var types = new List<RawType>();
            while (Current.Kind != TokenKind.End)
            {
                types.Add(ParseType());
            }

            return types;
        }

        private RawType ParseType()
        {
            ExpectKeyword("definition");
            var name = Expect(TokenKind.Identifier, "a type name");
            Expect(TokenKind.LeftBrace, "'{'");
            var relations = new List<RawRelation>();
            var permissions = new List<RawPermission>();
            while (Current.Kind != TokenKind.RightBrace)
            {
                if (Current.Kind == TokenKind.Identifier && Current.Text == "relation")
                {
                    relations.Add(ParseRelation());
                }
                else if (Current.Kind == TokenKind.Identifier && Current.Text == "permission")
                {
                    permissions.Add(ParsePermission());
                }
                else
                {
                    throw Error(Current, $"Expected 'relation', 'permission' or '}}' but found {Describe(Current)}.");
                }
            }

            Expect(TokenKind.RightBrace, "'}'");
            return new RawType(name.Text, relations, permissions, name.Line, name.Column);
        }

        private RawRelation ParseRelation()
        {
            Advance();
            var name = Expect(TokenKind.Identifier, "a relation name");
            Expect(TokenKind.Colon, "':'");
            var allowed = new List<AllowedSubject> { ParseAllowed() };
            while (Current.Kind == TokenKind.Pipe)
            {
                Advance();
                allowed.Add(ParseAllowed());
            }

            return new RawRelation(name.Text, allowed, name.Line, name.Column);
        }

        private AllowedSubject ParseAllowed()
        {
            var type = Expect(TokenKind.Identifier, "a subject type");
            if (Current.Kind == TokenKind.Hash)
            {
                Advance();
                var relation = Expect(TokenKind.Identifier, "a relation name after '#'");
                return new AllowedSubject(type.Text, relation.Text, false);
            }

            if (Current.Kind == TokenKind.Colon)
            {
                Advance();
                Expect(TokenKind.Star, "'*' (only wildcard subjects use 'type:*')");
                return new AllowedSubject(type.Text, null, true);
            }

            return new AllowedSubject(type.Text, null, false);
        }

        private RawPermission ParsePermission()
        {
            Advance();
            var name = Expect(TokenKind.Identifier, "a permission name");
            Expect(TokenKind.Equals, "'='");
            var expression = ParseUnionOrExclusion();
            return new RawPermission(name.Text, expression, name.Line, name.Column);
        }

        private Expr ParseUnionOrExclusion()
        {
            var left = ParseIntersection();
            while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var op = Current.Kind;
                Advance();
                var right = ParseIntersection();
                left = op == TokenKind.Plus ? new Expr.Union(left, right) : new Expr.Exclusion(left, right);
            }

            return left;
        }

        private Expr ParseIntersection()
        {
            var left = ParseFactor();
            while (Current.Kind == TokenKind.Ampersand)
            {
                Advance();
                left = new Expr.Intersection(left, ParseFactor());
            }

            return left;
        }

        private Expr ParseFactor()
        {
            if (Current.Kind == TokenKind.LeftParen)
            {
                Advance();
                var inner = ParseUnionOrExclusion();
                Expect(TokenKind.RightParen, "')'");
                return inner;
            }

            var name = Expect(TokenKind.Identifier, "a relation or permission name");
            if (Current.Kind == TokenKind.Arrow)
            {
                Advance();
                var right = Expect(TokenKind.Identifier, "a name after '->'");
                return new Expr.Arrow(name.Text, right.Text);
            }

            return new Expr.Ref(name.Text);
        }

        private void Advance()
        {
            if (_index < tokens.Count - 1)
            {
                _index++;
            }
        }

        private Token Expect(TokenKind kind, string description)
        {
            if (Current.Kind != kind)
            {
                throw Error(Current, $"Expected {description} but found {Describe(Current)}.");
            }

            var token = Current;
            Advance();
            return token;
        }

        private void ExpectKeyword(string keyword)
        {
            if (Current.Kind != TokenKind.Identifier || Current.Text != keyword)
            {
                throw Error(Current, $"Expected '{keyword}' but found {Describe(Current)}.");
            }

            Advance();
        }

        private static string Describe(Token token) => token.Kind == TokenKind.End ? "the end of the schema" : $"'{token.Text}'";

        private static SchemaException Error(Token token, string message) => new([new SchemaError(token.Line, token.Column, message)]);
    }
}

internal static class SchemaValidator
{
    public static Schema Build(List<RawType> raw)
    {
        var errors = new List<SchemaError>();
        var typeNames = new Dictionary<string, RawType>(StringComparer.Ordinal);
        foreach (var type in raw)
        {
            if (!typeNames.TryAdd(type.Name, type))
            {
                errors.Add(new SchemaError(type.Line, type.Column, $"Type '{type.Name}' is defined more than once."));
            }
        }

        var definitions = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var type in typeNames.Values)
        {
            var relations = new Dictionary<string, RelationDefinition>(StringComparer.Ordinal);
            var permissions = new Dictionary<string, PermissionDefinition>(StringComparer.Ordinal);
            foreach (var relation in type.Relations)
            {
                if (!relations.TryAdd(relation.Name, new RelationDefinition(relation.Name, relation.Allowed)))
                {
                    errors.Add(new SchemaError(relation.Line, relation.Column, $"Relation '{type.Name}#{relation.Name}' is defined more than once."));
                }
            }

            foreach (var permission in type.Permissions)
            {
                if (relations.ContainsKey(permission.Name))
                {
                    errors.Add(new SchemaError(permission.Line, permission.Column, $"'{permission.Name}' on '{type.Name}' is both a relation and a permission."));
                }
                else if (!permissions.TryAdd(permission.Name, new PermissionDefinition(permission.Name, permission.Expression)))
                {
                    errors.Add(new SchemaError(permission.Line, permission.Column, $"Permission '{type.Name}#{permission.Name}' is defined more than once."));
                }
            }

            definitions[type.Name] = new TypeDefinition(type.Name, relations, permissions);
        }

        foreach (var type in typeNames.Values)
        {
            foreach (var relation in type.Relations)
            {
                foreach (var allowed in relation.Allowed)
                {
                    if (!definitions.TryGetValue(allowed.Type, out var target))
                    {
                        errors.Add(new SchemaError(relation.Line, relation.Column, $"Relation '{type.Name}#{relation.Name}' allows unknown type '{allowed.Type}'."));
                    }
                    else if (allowed.Relation != null && !target.Has(allowed.Relation))
                    {
                        errors.Add(new SchemaError(relation.Line, relation.Column, $"Relation '{type.Name}#{relation.Name}' allows '{allowed.Type}#{allowed.Relation}', which does not exist."));
                    }
                }
            }

            foreach (var permission in type.Permissions)
            {
                Validate(permission.Expression, definitions[type.Name], permission, definitions, errors);
            }
        }

        foreach (var definition in definitions.Values)
        {
            DetectReferenceCycles(definition, errors, typeNames[definition.Name]);
        }

        if (errors.Count > 0)
        {
            throw new SchemaException(errors.OrderBy(e => e.Line).ThenBy(e => e.Column).ToList());
        }

        return new Schema(definitions);
    }

    private static void Validate(Expr expression, TypeDefinition type, RawPermission owner, Dictionary<string, TypeDefinition> definitions, List<SchemaError> errors)
    {
        switch (expression)
        {
            case Expr.Ref reference:
                if (!type.Has(reference.Name))
                {
                    errors.Add(new SchemaError(owner.Line, owner.Column, $"Permission '{type.Name}#{owner.Name}' refers to '{reference.Name}', which is not a relation or permission of '{type.Name}'."));
                }

                break;
            case Expr.Arrow arrow:
                if (!type.Relations.TryGetValue(arrow.Left, out var left))
                {
                    errors.Add(new SchemaError(owner.Line, owner.Column, type.Permissions.ContainsKey(arrow.Left)
                        ? $"Permission '{type.Name}#{owner.Name}' uses '{arrow}', but the left side '{arrow.Left}' must be a relation, not a permission."
                        : $"Permission '{type.Name}#{owner.Name}' uses '{arrow}', but '{arrow.Left}' is not a relation of '{type.Name}'."));
                    break;
                }

                foreach (var allowed in left.Allowed.Where(a => a.Relation is null && !a.Wildcard))
                {
                    if (definitions.TryGetValue(allowed.Type, out var target) && !target.Has(arrow.Right))
                    {
                        errors.Add(new SchemaError(owner.Line, owner.Column, $"Permission '{type.Name}#{owner.Name}' uses '{arrow}', but '{allowed.Type}' has no relation or permission '{arrow.Right}'."));
                    }
                }

                break;
            case Expr.Union union:
                Validate(union.Left, type, owner, definitions, errors);
                Validate(union.Right, type, owner, definitions, errors);
                break;
            case Expr.Intersection intersection:
                Validate(intersection.Left, type, owner, definitions, errors);
                Validate(intersection.Right, type, owner, definitions, errors);
                break;
            case Expr.Exclusion exclusion:
                Validate(exclusion.Left, type, owner, definitions, errors);
                Validate(exclusion.Right, type, owner, definitions, errors);
                break;
        }
    }

    private static void DetectReferenceCycles(TypeDefinition type, List<SchemaError> errors, RawType raw)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var permission in type.Permissions.Keys)
        {
            Visit(permission, new Stack<string>());
        }

        void Visit(string name, Stack<string> stack)
        {
            if (!type.Permissions.TryGetValue(name, out var permission))
            {
                return;
            }

            if (state.TryGetValue(name, out var existing))
            {
                if (existing == 1)
                {
                    var origin = raw.Permissions.First(p => p.Name == name);
                    errors.Add(new SchemaError(origin.Line, origin.Column, $"Permission '{type.Name}#{name}' is defined in terms of itself ({string.Join(" -> ", stack.Reverse().Append(name))})."));
                }

                return;
            }

            state[name] = 1;
            stack.Push(name);
            foreach (var reference in References(permission.Expression))
            {
                Visit(reference, stack);
            }

            stack.Pop();
            state[name] = 2;
        }
    }

    private static IEnumerable<string> References(Expr expression) => expression switch
    {
        Expr.Ref reference => [reference.Name],
        Expr.Union union => References(union.Left).Concat(References(union.Right)),
        Expr.Intersection intersection => References(intersection.Left).Concat(References(intersection.Right)),
        Expr.Exclusion exclusion => References(exclusion.Left).Concat(References(exclusion.Right)),
        _ => [],
    };
}
