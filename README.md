# AISharp (AI#) source preview

Experimental language source under Apache-2.0. The selected committed baseline
is `952b718a5161ebfab73601f1a6fdba1cba0ef6e3`. It includes the Windows C++ native
bootstrap compiler, AI# compiler sources used by self-host experiments, core
language files, managed compiler/runtime/CLI, samples and selected tests.
This is a development preview, not a production release. The active working
tree is not included. See [VERIFICATION.md](VERIFICATION.md) for current results:
managed build and sample validation pass; the main core suite has **331 passed,
9 failed and 6 explicitly skipped CLI gates**. Focused native checks pass **96
runtime outcomes and 16 exact diagnostics** across x86/x64 Release/Debug hosts
and normal/optimized generated output. Full language qualification is incomplete.

## Official token identity

The AISharp token is intended to help fund ongoing AISharp development.

Network: Solana mainnet.

Mint address:

```text
6VFDLF2gRAqv27PuPyg3oYZzbDJhFdJLS7znrecKpump
```

[Official AISharp token page](https://aisharp.io/token.html)

## Managed build and tests

Run from this repository root with .NET SDK 9.0.317 installed:

```powershell
dotnet build AISharp.sln -c Release -m:1 -p:UseSharedCompilation=false
dotnet run --project tests/AISharp.Tests/AISharp.Tests.csproj -c Release --no-build
dotnet run --project src/AISharp.Cli/AISharp.Cli.csproj -c Release --no-build -- samples/orders.aisharp --json
```

The test project is an executable runner. `dotnet test` does not execute that
runner. Some checks build temporary programs and perform local package dry-runs.
No credentials or external model service are needed for the sample validation.

## Native bootstrap verification

Windows, Python 3, CMake, Visual Studio 2022 Professional C++/Windows SDK tools,
and its bundled Ninja are required by the current verifier's tool discovery.
Use a fresh dedicated working directory and a separate report directory:

```powershell
python tests/AISharp.NativeCompiler.Tests/verify.py --work-root "$env:TEMP/AISharp-preview-native" --output "$env:TEMP/AISharp-preview-report" --fixtures tests/AISharp.NativeCompiler.Tests/preview-fixtures.json
```

The native path does not require .NET. It builds x86/x64 hosts and exercises
normal/optimized generated programs and source diagnostics. Add `--debug-host`
to use Debug hosts. The focused corpus covers 12 positive and four negative
programs; it is not the complete language or self-hosting verification matrix.
The broad default corpus currently stops on a diagnostic mismatch; see the
verification notes. Do not omit `--fixtures` when reproducing the passing preview check. Existing paths are not authorization to overwrite
another worker's files. Choose your own empty work directory.

## Scope and limitations

Full C# parity, complete compiler-enforced security guarantees, and complete
ownership/return-cleanup qualification are not claimed. Bootstrap and AI#
self-host paths have different coverage, including recovery/source mapping
limits. Contracts and policies have both compile-time and runtime boundaries.
Native programs execute with the caller's permissions. Successful compilation
is not a sandbox or proof that untrusted code is safe to execute.

Uncommitted development, Studio/VSIX, original Git history, internal documents,
work ledgers, agent transcripts, generated evidence, website and token-platform
work are excluded. Legacy machine-specific verification scripts are excluded;
this checkout provides the selected portable bootstrap verifier and fixtures.

See LICENSE, NOTICE and THIRD_PARTY_NOTICES.md. Existing Soligent attribution
is preserved. This preview does not grant licenses to external prerequisites.
