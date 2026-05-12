# STS2 Telemetry Mod

STS2 Telemetry is an unofficial Slay the Spire 2 mod that records structured
local telemetry for gameplay analysis and optional upload.

This public source export is intentionally limited to the privacy-reviewable
client-side code: the game mod, local inspector, updater helper, tests, and mod
release helpers. Production ingestion, reward generation, admin operations,
deployment, and anti-abuse logic are intentionally excluded from this public
repository.

## Included

| Path | Purpose |
| --- | --- |
| `src/Sts2Telemetry/` | Game mod telemetry recorder, background uploader, update client, and in-game status UI. |
| `src/Sts2Telemetry.Cli/` | Read-only local CLI for inspecting telemetry runs. |
| `src/Sts2Telemetry.Inspector/` | Telemetry validation and reporting library used by the CLI and tests. |
| `src/Sts2Telemetry.Updater/` | Mod update support. |
| `tests/` | Mod, uploader, recorder, release-path, inspector, and CLI tests. |
| `scripts/` | Mod release packaging, validation, and GitHub upload helpers. |

## Quick Verification

```bash
dotnet build src/Sts2Telemetry/Sts2Telemetry.csproj
dotnet run --project tests/Sts2Telemetry.Tests/Sts2Telemetry.Tests.csproj
dotnet run --project tests/Sts2Telemetry.Inspector.Tests/Sts2Telemetry.Inspector.Tests.csproj
```

## Data and Privacy Notes

Telemetry upload is enabled by default in the mod, with local settings and
status files under the telemetry data directory. The mod records structured
gameplay and diagnostic events, packages local JSONL segments, and scrubs native
save payloads before upload.

Collected data categories:

- Local gameplay telemetry JSONL.
- Scrubbed native save payloads from current run and history files.
- Run, segment, game version, mod version, and telemetry schema metadata.
- UTC record timestamps and local sequence numbers.
- Upload status and reward status needed to operate the telemetry pipeline.

Excluded data categories:

- Steam ID.
- OS username.
- Local filesystem paths.
- Raw native save local paths and local identity fields.
- Hardware fingerprint.
- IP-derived location.

## Source Boundary

Server-side ingestion, validation, reward generation, admin tooling, deployment,
and anti-abuse rules are private operational code. They are excluded so the
public mod source can prove what is collected without publishing server-side
abuse guidance.

## LinuxDo
https://linux.do
Thanks for participating！
