# Servyx

> **Status: pre-alpha, under active development.** APIs, schemas, and project layout may change without notice.

Servyx is a free and open-source, self-hosted, pluggable multi-game server control panel. It manages game servers across local process, local Docker, remote SSH, and remote Docker targets through a single dashboard, with typed configuration schemas per game, monitoring, backups, and mod management.

## Solution layout

```
Servyx/
├── src/
│   ├── Core/
│   │   ├── Servyx.Domain/                 # Entities, value objects, domain events
│   │   └── Servyx.Application/            # Use cases, abstractions, DTOs (FluentValidation)
│   ├── Infrastructure/
│   │   ├── Servyx.Infrastructure/         # Shared infra, EF Core + SQLite persistence
│   │   ├── Servyx.Infrastructure.Docker/  # Docker transport (YamlDotNet)
│   │   └── Servyx.Infrastructure.Ssh/     # SSH transport
│   ├── Presentation/
│   │   └── Servyx.Web/                    # Blazor Server dashboard
│   └── Hosting/
│       ├── Servyx.AppHost/                # .NET Aspire app host / orchestration
│       └── Servyx.ServiceDefaults/        # Shared Aspire service defaults
├── tests/
│   ├── Core/                              # Servyx.Domain.Tests, Servyx.Application.Tests
│   ├── Infrastructure/                    # Servyx.Infrastructure(.Docker).Tests
│   └── Presentation/                      # Servyx.Web.Tests (bunit)
└── Servyx.sln
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (for the Docker-based server transport and Aspire container resources)

## Quickstart

```bash
dotnet restore
dotnet run --project src/Hosting/Servyx.AppHost
```

This launches the Aspire app host, which orchestrates the Servyx.Web dashboard and its dependencies.

## Testing

```bash
dotnet test
```

All projects use xUnit, NSubstitute, FluentAssertions, and TinyBDD; `Servyx.Web.Tests` additionally uses bunit for Blazor component testing.

## Documentation

- [Architecture](docs/architecture.md)
- [Roadmap](docs/roadmap.md)
- [Game definition schema reference](docs/schema.md)
- [Core abstractions](docs/abstractions.md)
- [Example game definition: Palworld](definitions/palworld-docker.yaml)

## License

Servyx is licensed under the [MIT License](LICENSE).
