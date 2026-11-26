# Actor / Async Workflow Sample

Demonstrates the AI# actor profile and structured concurrency primitives that the
pilot acceptance suite (PR-44) exercises.

The sample declares:

- `BuildWorker` actor with two `state` members and two `async fn` handlers
  using `requires`/`ensures` contracts and declared `effects`.
- A free-standing `async fn` that returns a `Result<Artifact, Error>` with
  contracts and effects to exercise the async surface.
- A representative `policy`, two `capability` declarations, a record, a union
  error type, and a `test` block.

## Validate

```powershell
dotnet run --project src\AISharp.Cli\AISharp.Cli.csproj -- validate samples\actor-workflow\worker.aisharp --json
```

The validator should return `success: true` with no diagnostics.

## Pilot acceptance gate

The acceptance suite invokes this sample as the `actor-sample-validate` gate:

```powershell
dotnet run --project src\AISharp.Cli\AISharp.Cli.csproj -- acceptance --json
```

If the sample is missing or fails to validate, the gate fails and its
`detail` field points to the captured log under
`artifacts\acceptance\actor-sample-validate.log`.

## Limitations

Full actor runtime execution is not in scope for the pilot. This sample is
validated for syntax, name resolution, contracts, effects, and the static
shape of the actor handlers; runtime simulation will arrive in a later
milestone. Structured concurrency primitives (`taskgroup`/`spawn`/`all`)
are exercised by the parser/HIR tests in `tests/AISharp.Tests`.
