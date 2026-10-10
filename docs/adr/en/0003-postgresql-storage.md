# ADR 0003 — PostgreSQL storage

**Status:** Accepted

- One table, `relationships`, with the full history: `(object_type, object_id, relation, subject_type, subject_id, subject_relation, created_rev, deleted_rev)`. A **partial unique index on live rows** (`WHERE deleted_rev IS NULL`) makes "touch" idempotent at the database level, and still lets a relationship be deleted and added again as a new row.
- Reading an object at a revision uses `(object_type, object_id, relation, created_rev)`; reading by subject and the changes feed have their own indexes.
- **Writes are serialized with one advisory lock**, and a revision row is inserted at the end of the transaction. Because writers cannot overlap, revision `N` is always committed before revision `N+1` starts, so a reader that sees head `N` is guaranteed to see everything up to `N`. (Allocating revisions from a sequence without the lock lets revision 5 commit before revision 4, and a reader at head 5 would silently miss 4.)
- Batches use `unnest` over arrays and run as one statement per run of identical operations, preserving the order of operations inside a request (touch, delete, touch of the same tuple behaves as written).
- The schema text is stored in a `schemas` table; nodes pick up a newer version within a second.
- Bulk load speed measured on one machine: about 67k relationships/s in batches of 10,000.

## Consequences

- Simple and verifiably correct; write throughput is bounded by the single writer.
- No garbage collection of old revisions (README limit).
- The in-memory store implements the same interface and semantics, which is what makes the PostgreSQL parity test and the oracle test possible.
