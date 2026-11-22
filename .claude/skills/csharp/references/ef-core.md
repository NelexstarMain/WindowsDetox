# EF Core & SQL Patterns

## 1. Query Hygiene
- Default read-only queries to `.AsNoTracking()` (or `AsNoTrackingWithIdentityResolution()` for cyclic graphs) so the change tracker does not retain unnecessary snapshots.
- Project directly into DTOs / `record` types via `.Select(...)` instead of loading entire entity graphs over the wire.
- Never load full tables into memory before filtering (`IEnumerable` vs `IQueryable`); keep predicates translatable to SQL.
- Watch for N+1 queries inside loops and `ToListAsync()` calls inside `foreach`. Use `.AsSplitQuery()` carefully when multiple collection `Include`s cause Cartesian explosion.

## 2. Concurrency, Transactions & DbContext Lifetime
- Keep `DbContext` short-lived (per request / per unit of work). In Blazor Server or desktop apps, inject `IDbContextFactory<AppDbContext>` and create a context per operation instead of holding a single long-lived `DbContext` across UI lifetime.
- Pass `CancellationToken` to all EF Core async methods (`ToListAsync`, `FirstOrDefaultAsync`, `SaveChangesAsync`).
- Use explicit transactions (`BeginTransactionAsync`) only when coordinating multiple `SaveChangesAsync` calls or raw SQL commands; single `SaveChangesAsync` is already atomic.

## 3. Migrations & Raw SQL
- Never hand-edit generated migration snapshots without understanding model drift.
- Parameterize every raw SQL query (`FromSqlInterpolated` / `ExecuteSqlInterpolatedAsync`); never use string concatenation with `FromSqlRaw`.
