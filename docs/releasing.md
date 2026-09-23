# Release Process

Servyx uses automated semantic versioning and release workflows to publish binaries and container images.

## Version Numbering

Versions are derived automatically from [conventional commits](https://www.conventionalcommits.org/) since the last `v*` tag, starting from `v0.1.0`:

| Commit Type | Version Bump | Example |
|---|---|---|
| `feat(scope):` | Minor (0.1.0 → 0.2.0) | `feat(server): add new control tier` |
| `fix(scope):` or `perf(scope):` | Patch (0.1.0 → 0.1.1) | `fix(api): resolve connection leak` |
| `BREAKING CHANGE:` footer or `!:` suffix | Major (0.1.0 → 1.0.0) | `feat!: overhaul control API` or `feat(api)!: remove legacy endpoint` |

Versioning is configured in [`GitVersion.yml`](../GitVersion.yml) (GitVersion 6, GitHubFlow mode, ContinuousDeployment on main).

## What Triggers a Release

Every push to `main` that passes all CI tests triggers an automated release:

1. A semantic version is computed from commits since the last tag
2. A git tag (`v0.1.0`, `v0.1.1`, etc.) is created
3. A GitHub Release is published
4. Platform binaries and Docker images are built and pushed
5. The process is **idempotent**: if a release tag already exists, it is skipped — reruns of the same commit do not create duplicate releases

## Commit Message Enforcement

Pull requests to `main` require conventional commit format:

- PR title must match the pattern (e.g., `feat: add backup scheduling`)
- Individual commit subjects must also match
- Enforced by [`.github/workflows/conventional-commits.yml`](.github/workflows/conventional-commits.yml)

Merge commits created during PR merge are not validated (the PR title validation is sufficient).

## Release Artifacts

Each release publishes:

### Platform Binaries

Two self-contained applications, compiled for 4 runtime identifiers (RIDs):

| App | Purpose | Linux x64 | Linux ARM64 | Windows x64 | macOS ARM64 |
|---|---|---|---|---|---|
| `servyx-web` | Main web dashboard | .tar.gz | .tar.gz | .zip | .zip |
| `servyx-mcp-stdio` | MCP server for local integration | .tar.gz | .tar.gz | .zip | .zip |

**Archive naming**: `servyx-{app}-{version}-{rid}.{ext}`  
Example: `servyx-web-0.1.0-linux-x64.tar.gz`

**macOS caveat**: Binaries are unsigned and not notarized. On first run, you may see a security warning. To allow execution:

```bash
xattr -d com.apple.quarantine ~/Downloads/servyx-web-0.1.0-osx-arm64.zip
```

### Checksum Verification

Every release includes a `SHA256SUMS.txt` file listing SHA256 hashes for all binaries:

```bash
sha256sum -c SHA256SUMS.txt
```

### Docker Image

A multi-architecture container image pushed to GitHub Container Registry (GHCR):

- **Repository**: `ghcr.io/jerrettdavis/servyx`
- **Tags**:
  - Exact version: `0.1.0`, `0.1.1`, etc.
  - Major.minor: `0.1`, `0.2`, etc.
  - Latest: `latest`
- **Architecture**: `linux/amd64` and `linux/arm64`
- **User**: Runs as a non-root user
- **Port**: `8080` (HTTP, no TLS)
- **Data volume**: `/app/servyx-data` — must be mounted to persist SQLite database and secrets

#### First-Time Setup: Make GHCR Package Public

When the package is first created, it is **private**. To allow pulling on other devices (NAS, ARM SBC, etc.), make it public once:

1. Go to your GitHub repo → **Packages** (top navigation)
2. Click the **servyx** package
3. Click **Package settings** (gear icon, right sidebar)
4. Scroll to "Danger zone" → **Change visibility** → **Make public** → **Change visibility**

You only need to do this once. Future pushes to the same package inherit public visibility.

#### Pulling the Image

```bash
docker pull ghcr.io/jerrettdavis/servyx:latest
```

If the package is still private, you'll need to authenticate:

```bash
docker login ghcr.io -u your-github-username -p your-github-token
```

#### Running the Container

Minimal example:

```bash
docker run -d \
  -p 8080:8080 \
  -v servyx-data:/app/servyx-data \
  ghcr.io/jerrettdavis/servyx:latest
```

This:
- Runs in the background (`-d`)
- Exposes port 8080 to `localhost:8080` (`-p 8080:8080`)
- Mounts a Docker volume at `/app/servyx-data` to persist the SQLite database and secrets keyring
- Uses the latest released image

**Access**: Open http://localhost:8080 in your browser once the container is running.

#### Data Persistence

The container stores SQLite database and secrets keyring in `/app/servyx-data`. This **must** be a volume mount (named volume or bind mount) or data will be lost when the container restarts:

```bash
# Named volume (recommended)
docker run -v servyx-data:/app/servyx-data ...

# Bind mount (alternative)
docker run -v /path/on/host:/app/servyx-data ...
```

#### Docker Socket Access (Optional)

If you want Servyx to manage Docker-based game servers on the host, you can bind-mount the Docker socket:

```bash
docker run \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v servyx-data:/app/servyx-data \
  ...
```

**Important**: The container runs as a non-root user. To use the Docker socket, add the container's user to the host's `docker` group:

```bash
docker run \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v servyx-data:/app/servyx-data \
  --group-add <host-docker-gid> \
  ...
```

Find the host's docker group ID:

```bash
getent group docker | cut -d: -f3
```

Then pass `--group-add <gid>` to `docker run` or `group_add: ["<gid>"]` in docker-compose.

See [`docker-compose.yml`](../docker-compose.yml) for a complete example with all options.

## See Also

- [Installation guide](user-guide/installation.md) — detailed operator setup
- [`docker-compose.yml`](../docker-compose.yml) — production-ready compose configuration
