# AI Patch Workflow Sample (PR-38)

This sample demonstrates the controlled AI-assisted patch proposal workflow
described in PR-23 (Patch Proposal Flow) and PR-24 (Policy Engine).

The sample runs **entirely offline**:

- Uses the in-process `FakeLocalModelRuntime` (no network, no remote model).
- Uses the default local-first policy.
- Never mutates the source file unless `--apply` is supplied.
- Always writes a provenance record so the proposal can be audited.

## Workflow

1. **Diagnostic** — `samples/ai-patch/buggy.aisharp` declares
   `fn discountFor` which always returns `0`. The sample compiles the file
   and includes any compiler diagnostics, then emits a synthetic AI-targeted
   diagnostic describing the logical issue to fix.
2. **Patch proposal** — a deterministic local "patch-proposer" model returns
   a JSON patch proposal containing a unified diff, rationale, suggested
   tests, risk level, and policy acknowledgements.
3. **Schema validation** — the runtime validates the JSON against the
   registered `AiSharpPatchProposal` schema before deserialising it, and the
   deserialised proposal goes through `PatchProposalValidator` for structural
   checks.
4. **Provenance** — a `PatchProposalProvenance` record (hash, file count,
   risk level, applied flag) is appended to
   `artifacts/samples/ai-patch/.aisharp/provenance/provenance.ndjson`
   using the same `FileProvenanceStore` other AI# commands use.
   No raw diff content is written into the provenance record.
5. **Apply (opt-in)** — if `--apply` is passed, the proposed unified diff is
   applied to a **copy** of the buggy source written under
   `artifacts/samples/ai-patch/applied/buggy.aisharp`. The original sample
   file is never modified.

## Run

```powershell
dotnet run --project src\AISharp.Cli\AISharp.Cli.csproj -- run-sample ai-patch --json
```

Optional flags:

| Flag        | Effect                                                                 |
| ----------- | ---------------------------------------------------------------------- |
| `--json`    | Emit the structured JSON payload (recommended for CI).                 |
| `--apply`   | Write the patched file into `artifacts/samples/ai-patch/applied/`.     |
| `--out <d>` | Override the artifact output directory (defaults to repo `artifacts`). |

## Expected output

When the workflow runs successfully the CLI exits with code `0` and emits a
JSON payload of the following shape (timestamps and elapsed time vary):

```json
{
  "command": "run-sample",
  "success": true,
  "diagnostics": [],
  "artifacts": [
    { "kind": "source", "path": "...\\samples\\ai-patch\\buggy.aisharp" },
    { "kind": "patch-proposal", "path": "...\\artifacts\\samples\\ai-patch\\patch-proposal.json" },
    { "kind": "provenance", "path": "...\\artifacts\\samples\\ai-patch\\.aisharp\\provenance\\provenance.ndjson" }
  ],
  "timing": { "elapsedMilliseconds": 42.0 },
  "aiPatch": {
    "sample": "ai-patch",
    "diagnostic": {
      "code": "AI4001",
      "severity": "info",
      "message": "discountFor always returns 0; VIP customers should receive a non-zero discount."
    },
    "proposal": {
      "rationale": "Return a 10 percent discount for VIP customers.",
      "riskLevel": "Low",
      "changedFiles": [ "samples/ai-patch/buggy.aisharp" ],
      "testsToRun": [ "dotnet run --project tests/AISharp.Tests/AISharp.Tests.csproj" ],
      "policyAcknowledgements": [
        "No secrets included in the diff.",
        "No network calls added."
      ],
      "proposalHash": "<sha256-hex>"
    },
    "schemaValidation": { "isValid": true, "errors": [] },
    "provenance": {
      "kind": "PatchProposal",
      "wasApplied": false,
      "changedFileCount": 1,
      "riskLevel": "Low"
    },
    "applied": false,
    "sourceMutated": false
  }
}
```

When `--apply` is supplied the payload also lists the applied file under
`artifacts` and sets `"applied": true`. `sourceMutated` stays `false` because
the original `samples/ai-patch/buggy.aisharp` is never overwritten.

If schema validation or structural validation fails (e.g. the fake model
returns an invalid payload), the CLI exits with a non-zero code and the
`diagnostics` array carries the failure messages.
