# Source preview verification — November 26, 2026

Exact canonical baseline: `952b718a5161ebfab73601f1a6fdba1cba0ef6e3`.
This fresh snapshot excludes the original history and all uncommitted work.
Compiler/runtime implementation bodies are preserved. Snapshot changes concern
license/repository metadata, matching license tests, documentation, and native
verification portability/tool discovery; the focused corpus is new.

| Check | Actual result |
|---|---|
| Managed Release build | Passed; SDK 9.0.317; zero warnings/errors |
| Main core runner | 331 passed, 9 failed, 6 explicitly skipped CLI gates; exit 1 |
| Managed orders.aisharp validation | Passed; exit 0; JSON success true |
| Native Release host, x86/x64, normal/O2 | 48 runtime outcomes, 8 exact diagnostics; passed |
| Native Debug host, x86/x64, normal/O2 | 48 runtime outcomes, 8 exact diagnostics; passed |
| PE/CLR/target checks | Both host architectures verified; no CLR header; generated PE target and output hashes checked |
| Native external .NET requirement | Host/runtime checks use no .NET process or .NET PATH entry |
| Resolved external NuGet libraries | Zero in selected source projects |
| Full native corpus / self-host matrix | INCOMPLETE; not claimed qualified |

Managed reproduction (run from the root; SDK selection depends on it):

```powershell
dotnet build AISharp.sln -c Release -m:1 -p:UseSharedCompilation=false
$env:AISHARP_SKIP_CLI_GATE_TESTS = "1"
dotnet run --project tests/AISharp.Tests/AISharp.Tests.csproj -c Release --no-build
Remove-Item Env:AISHARP_SKIP_CLI_GATE_TESTS
```

The six deliberately skipped checks are the CLI release/acceptance/signing
integration gates, not six passing tests. Running the README's command without
that environment setting requests the full suite; no passing full-suite result
is claimed in this refresh. There was no real signing or software release.

Unresolved core failures:

- CLI validate command accepts single-source sample directory
- formatter formats representative constructs idempotently
- formatter output parses successfully
- CLI format stdout prints formatted output without writing
- name resolution resolves schema field types
- name resolution collects resolved symbol references
- HIR builder preserves model-call metadata
- CLI lower command persists source and lowered artifact hashes
- Policy engine honors runtime context decisions

The policy-context and provenance failures are security-relevant limitations.
Do not rely on those paths as complete security enforcement for untrusted code.
These defects were disclosed rather than changing active compiler development
inside a publication task.

The broad native verifier built both Release bootstrap hosts and completed its
x86 runtime cases, then stopped on `unsupported-passthrough`: it expected AI3100
at a member access while the pinned compiler returns AI3103 at the undeclared
receiver. That assertion remains unchanged. The passing preview command uses
an explicit focused fixture file; this is a narrower coverage claim, not a
replacement assertion or a claim that the broad corpus passed.

Native SDK discovery and an explicit owned `--work-root` were added because the
local developer environment omits SDK tools/headers/libraries. CMake paths are
normalized to forward slashes. No machine or user environment setting changed.
The verifier currently discovers Visual Studio 2022 Professional and Windows
SDK installations; other toolchain locations are not qualified.

The focused matrix does not requalify complete ownership/return cleanup,
AI# stage0/stage1/stage2 fixed-point behavior, recovery-source mapping, all
keywords or every debug-runtime path. The AI# self-host compiler source is
included for inspection, but its complete verification remains outstanding.

No Studio application, commercial IDE licensing change, original private
history, token platform, website, generated binaries or private audit evidence
is part of this source preview. All first-party licensing is Apache-2.0 with
preserved Soligent attribution. No independent reviewer was launched.
