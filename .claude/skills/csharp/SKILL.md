---
name: csharp
description: C# and .NET specialist skill router covering Blazor, WPF/MAUI, Unity/Godot C#, EF Core/SQL, testing, async performance, and error handling. Load specific files from references/ only when relevant to the task.
---

# C# / .NET Engineering Router

Apply strict YAGNI, self-documenting code (comments only for *why*), and C# / .NET best practices.

**Context Discipline:** Do NOT read all reference files upfront. Read ONLY the reference file(s) under `~/.claude/skills/csharp/references/` that match the active task:

- **Architecture, DI, Blazor/Desktop/Game decoupling, YAGNI boundaries:**
  Read `references/architecture.md`
- **SQL, PostgreSQL/SQL Server, EF Core queries, N+1, migrations, transactions:**
  Read `references/ef-core.md`
- **Unit & integration testing (xUnit, NUnit, MSTest, bUnit, Testcontainers, Engine-free game tests):**
  Read `references/testing.md`
- **Async/await, `CancellationToken`, `ValueTask`, `Span<T>`, GC allocations, frame-budget safety:**
  Read `references/async-perf.md`
- **Error handling, `Result` pattern, validation, `ProblemDetails`, logging:**
  Read `references/error-handling.md`
