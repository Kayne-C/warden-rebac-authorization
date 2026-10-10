# ADR 0001 — Revisions and consistency tokens

**Status:** Accepted

## Context

Authorization has a failure mode that ordinary caching does not: the "new enemy" problem. If access is revoked and then a document is shared, a check that reads the share from a fresh replica but the revocation from a stale one can grant access that should be gone. Checks need to read a **consistent snapshot**, and callers need a way to say "at least as new as my last write".

## Decision

- Every write transaction that changes anything creates one **revision** (a monotonically increasing integer). A relationship has `created_rev` and `deleted_rev`; it is visible at revision `R` iff `created_rev <= R` and (`deleted_rev` is null or `> R`). Nothing is overwritten, so any past revision can be read.
- A check runs entirely at **one revision**, so all of its reads agree with each other.
- A write returns a token (`r{revision}`). Reads choose a consistency level:
  - `minimize_latency` (default): the most recent head this node has seen, refreshed at most every `HeadCacheMs`. Cheap and cache friendly, may be a few milliseconds old.
  - `at_least_as_fresh(token)`: the cached head if it is already at or beyond the token, otherwise a fresh head. Read-your-writes without paying for a head lookup on every call.
  - `fully_consistent`: ask the database for the head now.
- Revisions are only allocated when something actually changed. Touching an existing relationship or deleting a missing one returns the current head.
- Caches are keyed by `(object, revision)`. A revision never changes, so cached data is never stale for the revision it is keyed by; "freshness" is purely a question of which revision a request picks.

## Consequences

- Revocation is visible to any request that presents a token from the revoking write, on any node (tested with two nodes and a deliberately stale head cache).
- A `minimize_latency` request can return a decision based on a slightly older revision. That is the documented trade-off for latency.
- Tokens are plain revisions: not signed, not bounded in time (see the README limits).
