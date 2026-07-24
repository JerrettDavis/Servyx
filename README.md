# Servyx

Servyx is a self-hosted control panel that adopts your existing game servers and shows you the truth about them before it ever asks to change anything.

> **Status: pre-alpha, under active development.** APIs, schemas, and project layout may change without notice.

![The Servyx dashboard](docs/images/dashboard-overview.png)

## What it is

- **Adopts existing containers rather than owning them.** Servyx attaches to game servers you already run; it never creates one on your behalf or assumes control it wasn't given.
- **Read-only until you grant more.** Every mutating control is visible but locked until you explicitly enable writes, one server at a time.
- **Byte-exact config with drift detection.** Servyx compares your intent, the authoritative file, the rendered config, and the live server, and tells you the moment any two disagree.

## Who it's for

Anyone running a self-hosted game server (Palworld today, Minecraft next) who wants a single dashboard to see its state, configuration, and backups — without handing over destructive control before they're ready to grant it.

## Quickstart

```bash
dotnet restore
dotnet run --project src/Hosting/Servyx.AppHost
```

This launches the Aspire app host, which orchestrates the Servyx.Web dashboard and its dependencies.

## Documentation

### For operators

- [User guide](docs/user-guide/index.md) — start here
- [Installation](docs/user-guide/installation.md)
- [Connecting a host](docs/user-guide/connecting-a-host.md)
- [Adopting servers](docs/user-guide/adopting-servers.md)
- [Control tiers](docs/user-guide/control-tiers.md)
- [Configuration](docs/user-guide/configuration.md)
- [Secrets](docs/user-guide/secrets.md)
- [Backups and saves](docs/user-guide/backups-and-saves.md)
- [Console and logs](docs/user-guide/console-and-logs.md)
- [Troubleshooting](docs/user-guide/troubleshooting.md)

### For contributors

- [Architecture](docs/architecture.md)
- [Core abstractions](docs/abstractions.md)
- [The control plane](docs/control-plane.md)
- [Connectors](docs/connectors.md)
- [Game definition schema reference](docs/schema.md)
- [Roadmap](docs/roadmap.md)
- [Testing](docs/testing.md)
- [Example game definition: Palworld](definitions/palworld-docker.yaml)

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

## Testing

```bash
dotnet test
```

All projects use xUnit, NSubstitute, AwesomeAssertions, and TinyBDD; `Servyx.Web.Tests` additionally uses bunit for Blazor component testing.

## License

Servyx is licensed under the [MIT License](LICENSE).
