# C# Async, Concurrency & Allocation Hygiene

## 1. Async / Await Rules
- Never use `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` in async paths or UI/Blazor contexts (deadlock and thread-pool starvation risk).
- Avoid `async void` everywhere except top-level UI event handlers (and wrap those in explicit exception handling).
- Propagate `CancellationToken` through every async boundary (`HttpClient`, EF Core, `Channel`, `Task.Delay`).
- Use `ValueTask` only on genuinely hot paths that complete synchronously most of the time; never await a `ValueTask` multiple times.

## 2. Hot-Path & Game-Loop Allocations (Unity / Godot / Desktop)
- Avoid per-frame LINQ allocations (`Where().Select().ToList()`), `params` arrays, string concatenations, and closure captures inside `Update` / `_Process` / render loops.
- Prefer `Span<T>`, `ReadOnlySpan<T>`, `ArrayPool<T>`, pre-allocated buffers, and `CollectionsMarshal` where profiling shows GC pressure.
- Dispose unmanaged resources, `CancellationTokenSource`, streams, and event subscriptions deterministically (`using`, `IAsyncDisposable`).
