# ADR 0004 — The schema language and its validation

**Status:** Accepted

- A small SpiceDB-like language: `definition`, `relation name: allowed | allowed`, `permission name = expression`. Allowed subjects are `type`, `type#relation` (a userset) or `type:*` (a wildcard).
- **Hand-written lexer and recursive-descent parser.** Precedence: `&` binds tighter than `+` and `-`; `+` and `-` are left-associative; parentheses override. Comments (`//`, `/* */`) are supported. Syntax errors carry the exact line and column and say what was expected and what was found.
- **Validation is a separate pass** that reports all semantic errors together: duplicate types, relations and permissions; a name that is both a relation and a permission; unknown types or relations in allowed subjects; references to unknown names; an arrow whose left side is not a relation, or whose right side does not exist on every type the left side can point to; and permissions defined in terms of themselves without going through data (`a = b`, `b = a`). Recursion **through data** (`parent->view`) is allowed because it is how hierarchies work.
- **Writes are validated against the schema:** a relationship must name an existing relation, may not target a permission, and its subject must be one of the allowed forms (a userset or wildcard only where the relation lists it). One invalid update rejects the whole request with every problem listed.
- Publishing an invalid schema returns 400 with `errors[{line, column, message}]` and leaves the active schema untouched.

## Consequences

- Authors see mistakes at publish time, not as wrong answers at check time.
- Changing the schema does not migrate existing data (README limit).
