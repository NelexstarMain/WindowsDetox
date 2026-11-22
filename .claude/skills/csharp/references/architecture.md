# C# Architecture, YAGNI & UI/Engine Decoupling

## 1. Strict YAGNI & DI Discipline
- Do not create `IFooService` + `FooService` when only one implementation exists and it performs pure logic. Inject concrete types directly; introduce interfaces only at true I/O, clock/randomness, or external engine boundaries.
- Prefer Vertical Slices / feature folders over sprawling horizontal `Repositories/` + `Managers/` + `Helpers/` hierarchies unless the repository already enforces layered projects.
- Use `sealed` on concrete classes not designed for inheritance.

## 2. Blazor, WPF / MAUI & Game (Unity / Godot) Boundaries
- **Pure Domain Core:** Keep business rules, state machines, combat/economy math, and validation in plain C# classes (`NetStandard` / `.NET`) free of `MonoBehaviour`, `Node`, `Dispatcher`, or `NavigationManager`.
- **Blazor:** Keep `.razor` markup thin; avoid mutating shared scoped services across concurrent circuit renders; dispose timers/event subscriptions via `IDisposable` / `IAsyncDisposable`.
- **WPF / MAUI:** Bind ViewModels cleanly (`CommunityToolkit.Mvvm`); never perform blocking `.Result` / `.Wait()` on the UI thread; pass `CancellationToken` on navigation/unload.
- **Unity / Godot:** Separate frame-tick adapters (`Update`/`_Process`, `FixedUpdate`/`_PhysicsProcess`) from pure simulation state so unit tests run directly via `dotnet test` without launching the engine.
