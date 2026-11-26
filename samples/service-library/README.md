# Service Library Sample

A realistic multi-file AI# package that exercises the production language
surface required by the **PR-37** acceptance criteria. The sample models a
small banking service library: account state, transfer settlement, and a
schema-constrained model call that summarises account activity.

## Files

| File | Purpose | AI# constructs demonstrated |
| --- | --- | --- |
| `aisharp.toml` | Package manifest read by `aisharp build` | manifest, sources, policy, build target |
| `accounts.aisharp` | Account records and pure helpers | records, unions (`AccountKind`, `AccountStatus`), `requires` / `ensures` contracts, `effects [time]`, `policy`, `test` |
| `transfers.aisharp` | Transfer proposal, approval, and settlement | records, union (`TransferStatus`), multi-clause contracts, derived fields, `test` |
| `summaries.aisharp` | Model-call returning a typed schema | `schema` declaration, `effects [model]`, fluent `model … .output<Schema>() … .run()` call, `policy`, `test` |

## What the sample demonstrates

* **Records** – `Account`, `LedgerEntry`, `Transfer`, `TransferReceipt`,
  `AccountSummary` are AI# record types that lower to `public sealed record`
  declarations.
* **Unions** – `AccountKind`, `AccountStatus`, and `TransferStatus` lower to
  abstract records with nested case records. Cases are constructed via
  type-annotated `let` bindings, e.g. `let approved: TransferStatus = Approved`.
* **Functions with contracts** – Every function has at least one `requires`,
  `ensures`, or `invariant` clause that lowers to runtime `AISharpContracts`
  guards.
* **Effects** – `effects [time]` and `effects [model]` are surfaced as
  `[AISharpEffectMetadataAttribute]` and runtime `AISharpEffects.Require`
  guards on the generated methods.
* **Policy** – Each AI# file declares a `policy` block with the production
  defaults (`classification = secret`, `inference = local_only`,
  `external_text = tainted`, `training = forbidden`).
* **Schemas and model calls** – `summaries.aisharp` declares a
  `schema AccountSummarySchema` and consumes it from a fluent model call
  (`model local "summarizer-v1" … .output<AccountSummarySchema>() … .run()`).
  The schema lowers to a record and the model-call expression lowers to a
  deterministic schema-typed default until the runtime model dispatcher is
  wired in.
* **Tests** – Each file ships inline `test "…" { expect … }` blocks that
  exercise the pure helpers.

## Build and validate

The sample is a first-class AI# *package*: a directory of `.aisharp` files
plus an `aisharp.toml` manifest. The CLI knows how to lower the whole package
to C# and invoke `dotnet build`:

```powershell
dotnet run --project src\AISharp.Cli\AISharp.Cli.csproj -- build samples\service-library --json
```

Successful output ends with a `build.exitCode` of `0` and emits one
`*.g.cs` artefact per source file together with a generated
`service-library.csproj` and its compiled assembly under
`artifacts\build\service-library-<timestamp>\`.

To run the AI# test suite (which also exercises the lowering / build
harness against this sample):

```powershell
dotnet run --project tests\AISharp.Tests\AISharp.Tests.csproj
```
