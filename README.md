# Warden — A Relationship-Based Authorization Service (Zanzibar-style)

**English** · [Türkçe](README.tr.md)

"Can **this user** do **this** to **that object**?" answered from a graph of relationships (documents in folders, folders in folders, users in nested groups) instead of hard-coded role checks.
A small authorization service in the style of Google Zanzibar / SpiceDB, built on .NET 10 and PostgreSQL.

## What it does

| Area | Approach | Evidence |
|---|---|---|
| **Schema language** | A hand-written lexer and recursive-descent parser for definitions, relations (`user \| group#member \| user:*`) and permissions built from `+` (union), `&` (intersection), `-` (exclusion) and `->` (arrows through relations) | Tests for precedence, comments, exact line/column of syntax errors, and many kinds of semantic errors reported together (unknown types, bad arrows, permission cycles, duplicates, …) |
| **Check** | Recursive, parallel evaluation of the permission expression over the relationship graph with cycle cutting, a depth limit that is an **explicit error** (never a silent deny), and result memoization that is only applied when it is sound | A bottom-up fixpoint **oracle** written independently of the checker: 300+ random graphs (cyclic data for the union schema, acyclic data with exclusion and intersection) must agree on every user × object × permission |
| **Consistency** | Every write returns a revision token; reads can ask for `minimize_latency`, `at_least_as_fresh(token)` or `fully_consistent`. Reads are snapshots at a revision | Test: a write made on another node is invisible to a node serving a cached head until the token is presented; revoking access is visible to a fresh read immediately |
| **Storage** | PostgreSQL with full revision history (`created_rev` / `deleted_rev`), a partial unique index on live tuples, a single serialized writer so that revisions become visible in order | Tests: idempotent touches, delete / re-add history, ordering inside a write, 5,000 relationships in one revision, 40 concurrent writers → revisions 1..40 and a consistent snapshot at every one of them |
| **Reads in bulk** | All relations of an object are fetched in one query and shared inside a check session (and cached per revision) | Measured: round trips per check dropped from 34 to 12.8; PostgreSQL throughput 5.2x (see below) |
| **Also** | `Expand` (who has this permission and why), `ListObjects`, relationship filters, a changes feed, per-key scopes (`read`/`write`/`admin`) | API tests |
| **Fail-safe defaults** | The service does not start without API keys; unknown permission names are 400s; a too-deep graph is a 422 | Tests |

### Example schema

```
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
  relation viewer: user | group#member | user:*
  relation banned: user

  permission edit = owner + parent->edit
  permission view = (edit + viewer + parent->view) - banned
}
```

## Quick start

```bash
docker compose up -d --build      # PostgreSQL + 2 service nodes (5600, 5601), dev-only API keys
A='authorization: Bearer dev-admin-key-change-me-0001'
curl -X POST localhost:5600/v1/schema -H "$A" -H 'content-type: application/json' -d "$(jq -n --rawfile d examples/docs.schema '{definition:$d}')"

curl -X POST localhost:5600/v1/relationships/write -H "$A" -H 'content-type: application/json' -d '{"updates":[
  {"op":"touch","object":"group:eng","relation":"member","subject":"user:ayse"},
  {"op":"touch","object":"folder:root","relation":"owner","subject":"user:mehmet"},
  {"op":"touch","object":"document:spec","relation":"parent","subject":"folder:root"},
  {"op":"touch","object":"document:spec","relation":"viewer","subject":"group:eng#member"}]}'
# -> {"revision":1,"token":"r1"}

curl -X POST localhost:5601/v1/check -H "$A" -H 'content-type: application/json' \
  -d '{"object":"document:spec","permission":"view","subject":"user:ayse","consistency":{"mode":"at_least_as_fresh","token":"r1"}}'
# -> {"allowed":true,"revision":1,"token":"r1"}   (asked on the other node)
```

| Endpoint | Scope | Purpose |
|---|---|---|
| `POST /v1/schema`, `GET /v1/schema` | admin / read | Publish a schema (validated, versioned) and read the active one |
| `POST /v1/relationships/write` | write | Touch / delete relationships atomically; every update is validated against the schema; returns the token |
| `POST /v1/relationships/read` | read | Filtered read at a consistency level |
| `POST /v1/check` | read | One permission check |
| `POST /v1/expand` | read | The tree of subjects behind a permission |
| `POST /v1/lookup/objects` | read | Objects of a type a subject can use |
| `GET /v1/changes?after=` | read | Ordered touches and deletes after a revision |

## Measurements

AMD Ryzen 7 9800X3D, Windows, PostgreSQL 17 **in Docker with default settings on the same machine**, 887k relationships (30k users, 3k groups with nested groups, 30k folders in trees up to depth 8, 400k documents).
The workload is 64 concurrent checks of random document × user pairs, which is the **expensive case**: only 0.7% are allowed, so a denial has to explore every branch.

| Store | Checks/s | Latency p50 / p95 / p99 | Reads per check |
|---|---|---|---|
| In memory (algorithm only) | 74,000 | 0.06 / 1.0 / 2.5 ms | 12.8 |
| PostgreSQL, no cache | 1,604 | 38.6 / 73 / 87 ms | 12.8 |
| PostgreSQL + per-revision cache (as served) | 19,053 | 2.5 / 10.9 / 16.6 ms | 12.8 (92.7% cache hits) |

Before reading a whole object in one query the same workload needed 34 reads per check and ran at 310 / 4,279 checks/s on PostgreSQL without / with the cache.
Bulk loading 887k relationships took 13 s (≈67k relationships/s, in batches of 10,000).

**How to read this:** the client, the service logic and the database share one CPU, the cache is warm for the repeated hot objects of a random workload, and the data set is synthetic. These numbers show where the costs are
(round trips, not the algorithm), not what a production cluster would do. They were measured on a single node; multi-node scaling was not measured.

## Tests

```bash
dotnet test --solution Warden.slnx      # needs Docker (real PostgreSQL 17 via Testcontainers)
```

57 tests: parser and validator (including positions), the checker on small graphs (hierarchies, nested groups, wildcards, exclusion, intersection, cycles, depth limit, revisions, shared sessions),
the randomized oracle comparison, the PostgreSQL store (revisions, history, concurrency, changes feed, parity with the in-memory store), and the HTTP API (authentication and scopes, validation, consistency across two nodes, schema propagation).

## Architecture

```
client ─▶ HTTP API ─▶ HeadTracker (consistency → revision) ─▶ Checker ─▶ CachingTupleReader ─▶ PostgreSQL
                          │                                     │            (key: object, revision)       relationships
                          └ token = "r{revision}"               └ session: memo + one read per object      revisions, schemas
```

Decisions: [docs/adr/en](docs/adr/en).

## Known limits (an honest list)

- **No caveats / conditional permissions** (attribute-based conditions) and no expiring relationships.
- **`ListObjects` is a scan:** it checks every object of the type that has at least one relationship, with a shared session. There is no reverse index, so it is O(candidates) and not suitable for huge object sets.
- **Writes are serialized** by a single advisory lock so that revisions become visible in order. That is simple and correct, and it caps write throughput; PostgreSQL's transaction snapshots (as SpiceDB uses) would lift the cap.
- **History is never compacted.** Deleted relationships stay in the table so old revisions remain readable; there is no garbage collection.
- **Consistency tokens are plain revisions**, not clock-bounded and not signed. A token from a different deployment is not detected.
- **The changes feed is polling**, not a stream.
- **Schema changes are not migrations:** existing relationships are not re-validated when a new schema is published (checks simply ignore tuples the schema no longer allows to matter).
- **Authentication is static API keys** with three scopes; no per-object-type authorization, no tenant isolation.
- The depth limit (default 32) means very deep hierarchies need a higher setting and return 422 until then.
- Measurements are single-node and use a deny-heavy random workload; real traffic with repeated checks should cache better.
