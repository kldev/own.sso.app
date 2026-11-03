# libs

Code shared by the processes of Great SSO (the host `apps/Sso.Api`, the runner `apps/Sso.Runner`, the migrator and the
tests). Libraries reach the projects through `ProjectReference`, never through a package feed.

| Project | What it holds |
|---|---|
| `Sso.Data/` | the EF Core model - the source of the schema (ADR 0003): entities, configuration, migrations, the audit interceptor (ADR 0009), the outbox and its relay batch (ADR 0010) |
| `Sso.Messaging.Contracts/` | the names of the outbox messages and the runner's queue - plain types, no broker |
| `Sso.Observability/` | Serilog and OpenTelemetry (OTLP only with `OTEL_EXPORTER_OTLP_ENDPOINT`; Npgsql, HTTP), metrics of health checks and process limits |
| `Sso.Observability.AspNetCore/` | the same for ASP.NET Core: request instrumentation, one log line per request |

Package versions live in `Directory.Packages.props` (central package management). MimeKit pulls in a vulnerable
`System.Security.Cryptography.Pkcs` 10.0.0, pinned higher there (`CentralPackageTransitivePinningEnabled`).
