# C# Testing Practices (Auto-Detected Frameworks)

## 1. Detect Before Writing
Inspect existing `.csproj` files before picking packages:
- **xUnit / NUnit / MSTest:** Follow the existing framework, assertions (`FluentAssertions` / `Shouldly` / built-in `Assert`), and naming convention already in the repo. If creating a new library test project from scratch, default to **xUnit**.
- **Blazor:** Use `bUnit` (`TestContext`) when component rendering/event callbacks need verification; extract complex state logic into plain C# classes tested without `bUnit`.
- **Unity / Godot:** Keep domain/simulation tests in engine-agnostic C# test assemblies runnable via `dotnet test`; reserve `PlayMode` / `GdUnit` tests strictly for scene wiring and physics/input integration.
- **Database / EF Core:** Prefer real database containers (`Testcontainers` + `WebApplicationFactory`) over EF Core `InMemory` provider when testing SQL constraints, migrations, or raw SQL.

## 2. Isolation & Determinism
- Avoid `Thread.Sleep` / arbitrary `Task.Delay` in tests; inject `TimeProvider` (.NET 8+) or explicit clock delegates for time-dependent logic.
- Test behavior and public contracts rather than private methods or exact mock call sequences.
- Name tests clearly (`MethodOrBehavior_Condition_ExpectedOutcome`) and use Arrange-Act-Assert structure without noisy boilerplate comments.
