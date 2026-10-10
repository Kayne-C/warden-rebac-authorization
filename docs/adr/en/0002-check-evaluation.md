# ADR 0002 — How a check is evaluated

**Status:** Accepted

## Decision

A permission is an expression tree (`Ref`, `Arrow`, `Union`, `Intersection`, `Exclusion`); a relation is a set of stored subjects. `Check(object, name, subject)` evaluates it recursively.

- **Relations:** a direct subject matches if type and id match, or if the stored subject is the wildcard `type:*`. Userset subjects (`group:eng#member`) are followed by evaluating that relation on that object for the same subject.
- **Arrows** (`parent->view`): for each object subject of `parent`, evaluate `view` on it. Targets that do not define the name are skipped (the schema validator already guarantees this cannot happen for allowed types).
- **Union** short-circuits on the first true branch and runs branches in parallel (bounded). **Intersection** and **exclusion** evaluate left first and only evaluate right when needed.
- **Cycles in data are normal** (group A contains group B contains group A). The path of `(object, name)` pairs being evaluated is tracked; meeting a key already on the path contributes **false**. This yields the least fixpoint for monotone schemas, which is what the oracle test verifies.
- **Memoization must be sound.** A result computed while a cycle was cut may depend on an ancestor that was still in progress, so it is only memoized when no unresolved cut points above it were involved. Every evaluation returns the shallowest cut depth it touched; a result is cached when that depth is not shallower than itself. True results are always safe (cuts only remove paths); a false produced through an exclusion whose right side relied on a cut is not (covered by the exclusion cases and the oracle tests).
- **Depth limit:** exceeding `MaxDepth` throws `DepthExceededException` (HTTP 422). A silent "deny" would be a security-relevant wrong answer, so it is an error.
- **Round trips:** all relations of an object are fetched in one read and shared across the whole session (`ConcurrentDictionary<object, Task<...>>`), including between parallel branches. This cut reads per check from 34 to 12.8 in the benchmark and made PostgreSQL 5x faster.
- A **session** (one subject, one revision) can answer many checks and share its memo and object reads; `ListObjects` uses it.

## Consequences

- Correctness is checked against an independent bottom-up implementation on hundreds of random graphs, including cyclic ones.
- Exclusion over data that contains cycles is not given a meaning beyond "cycle contributes false"; schemas that rely on that should be avoided.
- Parallel branches can read objects that a sibling would have made unnecessary; the shared object reads keep that cost low.
