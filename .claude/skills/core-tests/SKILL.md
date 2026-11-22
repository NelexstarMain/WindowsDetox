---
name: core-tests
description: Pragmatic TDD workflow (Red-Green-Refactor), automatic test framework detection across .NET/Blazor/Unity/Godot/Python, and verification rules for the smart Stop TDD gate. Load when writing tests, fixing bugs, or verifying code changes.
user-invocable: true
---

# Core Testing & Pragmatic TDD

## 1. Auto-Detect Repository Test Stack
Never guess or install a duplicate test framework. Inspect `.csproj`, `pyproject.toml`, or `package.json` first:
- **.NET / C#:** Detect `xunit`, `NUnit`, `MSTest`, `bUnit` (Blazor), `Verify`, `Testcontainers`, or `WebApplicationFactory`. If creating a brand-new .NET test project with no existing standard, default to **xUnit**.
- **Unity / Godot C#:** Detect `EditMode`/`PlayMode` assemblies or `GdUnit4`/`GoDotTest`. Keep pure domain/simulation logic in engine-free C# assemblies so core logic runs fast via `dotnet test`.
- **Python:** Detect `pytest` (`uv run pytest`, `poetry run pytest`) or stdlib `unittest`.
- **TypeScript / JS:** Detect `vitest`, `node --test`, `tsx --test`, or `jest` (`npm`/`pnpm`/`yarn`/`bun`).
- **Other languages / custom repos:** Detect `cargo test`, `go test`, `ctest`, `make test`, or custom scripts declared in `.claude/hygiene.json`.

## 2. Red-Green-Refactor Discipline
- **Mandatory Red-first:** Business logic, domain calculations, state machines, parsers, and **every bug fix** (write a minimal failing test reproducing the bug before fixing production code).
- **Pragmatic verification:** Pure UI markup, styling, or thin wiring may be verified via build checks (`dotnet build`), render smoke tests (`bUnit`), or integration checks.
- **Keep tests fast & deterministic:** Inject `TimeProvider` / clock delegates instead of `Thread.Sleep` / `Task.Delay`; avoid EF Core `InMemory` when SQL constraints matter.

## 3. Working With the Smart `Stop` TDD Gate
- Any edit outside ignored doc/asset patterns (`*.md`, `.claude/**`, `docs/**`, `*.txt`, `.gitignore`, `*.png/jpg/svg/ico`, `LICENSE`) sets `code_dirty = true`.
- Running a recognized test/build command (`dotnet test`, `dotnet build`, `pytest`, `npm`/`pnpm`/`yarn`/`bun test`, `npx vitest/jest`, `cargo test`, `go test`, `make test`, or `extra_test_commands` in `.claude/hygiene.json`) satisfies the gate:
  - **Green:** clears `code_dirty` (`TESTS:OK`).
  - **Conscious TDD Red:** if you run the new failing test *after* your last edit, `Stop` allows returning control so you can discuss or proceed to Green (`TESTS:RED`).
- **Rule:** Never conclude a coding turn without running verification *after* the final production-code edit.
