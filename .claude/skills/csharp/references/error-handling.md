# C# Error Handling & Validation

## 1. Expected Domain Failures vs Exceptional Faults
- Represent expected domain/validation outcomes (not found, conflict, invalid state transition) with explicit `Result` / discriminated result types or typed error records—do not use exceptions for normal control flow.
- Reserve exceptions for truly unexpected infrastructure faults, contract violations (`ArgumentNullException.ThrowIfNull`), or unrecoverable invariants.
- Never swallow exceptions with empty `catch { }` blocks. If catching at a boundary, log structured context or rethrow cleanly (`throw;` rather than `throw ex;`).

## 2. API & UI Boundary Mapping
- **HTTP / Minimal APIs:** Map domain errors cleanly to `ProblemDetails` (`Results.Problem(...)` / RFC 9457) with appropriate status codes (`400`, `404`, `409`, `422`) and zero internal stack traces in production responses.
- **Blazor / Desktop / Games:** Surface actionable, user-safe failure states in ViewModels/UI state rather than crashing the circuit or frame loop; preserve technical details in structured logs (`ILogger` source-generated `[LoggerMessage]`).
