<p align="center">
  <img src="src/KAST.UI/wwwroot/brand/logo-256.png" alt="CASTER logo" width="128" height="128" />
</p>

<h1 align="center">CASTER</h1>

<p align="center">
  <strong>Arma 3 server management from one web panel.</strong><br />
  Install servers and mods from Steam, configure instances, and run and monitor them from the browser.
</p>

<p align="center">
  <a href="https://github.com/bluefield-creator/KAST/releases/latest"><img alt="Release" src="https://img.shields.io/github/release/bluefield-creator/KAST.svg?style=flat-square" /></a>
  <a href="https://github.com/bluefield-creator/KAST/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/bluefield-creator/KAST/actions/workflows/ci.yml/badge.svg?branch=caster" /></a>
  <a href="https://github.com/bluefield-creator/KAST/actions/workflows/codeql-analysis.yml"><img alt="CodeQL" src="https://github.com/bluefield-creator/KAST/actions/workflows/codeql-analysis.yml/badge.svg" /></a>
  <a href="LICENSE.md"><img alt="License" src="https://img.shields.io/github/license/bluefield-creator/KAST.svg?style=flat-square" /></a>
</p>

---

## Table of Contents

- [About](#about)
- [Features](#features)
- [Quick Start](#quick-start)
- [Installation](#installation)
- [Configuration](#configuration)
- [Operations](#operations)
- [Versioning](#versioning)
- [Documentation](#documentation)
- [Support](#support)

---

## About

CASTER is a web panel for running Arma 3 dedicated servers. It started as a fork of [KAST](https://github.com/Foxlider/KAST), built for communities that need more than a single-admin tool:

- **Multi-user access** with OIDC and system-account sign-in, multiple administrators, and API keys
- **Native service installation** and an in-app update channel
- **Reliable Steam downloads** for server files and Workshop mods
- **Process supervision** with crash detection, restart policies, and orphan cleanup

---

## Features

<details>
<summary><strong>Authentication &amp; Security</strong></summary>

- Multiple administrator accounts with individual credentials
- OIDC / OpenID Connect, compatible with Authentik and other providers
- Three auth modes: Local-only, OIDC-only, or Local + OIDC fallback
- Group-claim access control (e.g. restrict to `KAST Admins`)
- System account sign-in: Windows local users, AD domain users, or Linux local users
- API keys with generate, revoke, and usage tracking
- First-run bootstrapping for an initial administrator or OIDC provisioning

</details>

<details>
<summary><strong>Deployment &amp; Operations</strong></summary>

- Windows Service: install, configure, start, stop, and remove
- Crash recovery with restart delay and failure reset window
- Auto-update from GitHub releases, staged and applied from the UI
- Stable and nightly update channels with configurable auto-check
- Service-aware updater that stops the Windows service before overwriting files
- Multi-arch Docker images tagged `latest`, `stable`, and `nightly`
- `/health` (web and database) and `/ready` (includes download queue status) endpoints
- Reverse proxy support (Caddy, nginx) with websocket proxying for Blazor Server
- Configurable trusted proxy IPs for forwarded headers

</details>

<details>
<summary><strong>Server Management</strong></summary>

- Multiple server profiles: create, edit, and delete instances
- Headless client support with configurable count and auto-launch
- Restart policies: None, On Crash, or Always, with max attempts
- Scheduled start and stop per instance (HH:mm)
- Process watchdog with crash detection and restart
- Process history for start, stop, restart, and crash events, with durations
- Performance tuning: hyper-threading toggle, max memory and CPU count overrides, ranking toggle
- Toggles for all 8 official Creator DLCs
- Custom launch parameters per instance (e.g. `-hugepages`)
- Live status indicators: Stopped, Starting, Running, Stopping, Crashed, Restarting

</details>

<details>
<summary><strong>Server Configuration</strong></summary>

- Structured `server.cfg` editor (hostname, password, max players, MOTD, voting, and more)
- `basic.cfg` editor (Bandwidth, MinBandwidth, MaxMsgSend, and other performance settings)
- Arma 3 profile editor with difficulty settings and raw-text fallback
- Raw config editing with bidirectional sync to structured fields
- Configs written to disk on save and launch
- Unsaved-changes guard on navigation

</details>

<details>
<summary><strong>Steam Integration</strong></summary>

- Install and update Arma 3 Dedicated Server (Stable, Development, DLCs, Legacy)
- Install, update, and manage Arma 3 Steam Workshop mods
- Steam login with username, password, and Steam Guard, or by QR code in the Steam mobile app
- Mod preset import from Arma 3 Launcher HTML exports
- Mod update check on application launch
- Steam profile display (avatar, persona name, SteamID) in settings

</details>

<details>
<summary><strong>Mod Management</strong></summary>

- Centralized mod page with table and card views
- Parallel downloads with an independent, configurable concurrency limit
- Durable download queue: queued and running downloads survive restarts
- Per-mod download, update, verify, and cancel actions
- Update All to batch-update outdated mods
- Per-instance mods with drag-and-drop load order and client/server flags
- Local mod import from folders with custom search paths
- Manifest tracking (`SteamManifestId` / `InstalledManifestId`) for reliable update detection
- Per-mod comment field
- Built-in speed benchmark to find optimal download parallelism

</details>

<details>
<summary><strong>Mission Management</strong></summary>

- Mission upload (PBO) via the web UI or API
- HTTP mission downloads as a faster, bandwidth-friendly alternative to Arma's built-in file transfer
- CRC32 / BZip2 integrity verification
- ETag and If-Modified-Since support (304 Not Modified)
- Byte-range requests for partial and resumable downloads
- Download logging: player name, Steam ID, server address, and user agent
- Tagging with create, delete, assign, and remove
- Search by text, tags, and map name
- Campaigns with add, remove, and reorder
- Named mission sets
- Bind a mission to a mod preset for easy deployment

</details>

<details>
<summary><strong>Monitoring &amp; Observability</strong></summary>

- Host dashboard with real-time CPU, memory, and disk charts
- Per-instance CPU, memory, and player count charts
- Live console tailing with color-coded output and log-level filtering
- RPT event detection: mission starts, Steam connections, admin activity, and crashes
- Server events panel with severity levels
- Detection of external (unmanaged) Arma processes, with the ability to kill them
- Real-time status and metrics pushed to the UI via SignalR
- Configurable metrics collection interval

</details>

<details>
<summary><strong>REST API</strong></summary>

| Endpoint | Purpose |
| --- | --- |
| `/api/servers` | Server instance CRUD |
| `/api/mods` | Mods, downloads, updates, and presets |
| `/api/missions` | Upload, download, search, tags, campaigns, and sets |
| `/api/monitoring` | Host and instance metrics |
| `/api/processes` | List running processes and kill by PID |
| `/api/settings` | Settings and API key management |
| `/api/downloads/state` | Authoritative download queue snapshot for UI recovery |

OpenAPI documentation with endpoint tags and Swagger support is included.

</details>

<details>
<summary><strong>UI / UX</strong></summary>

- Blazor Server web UI: works in any browser, no client install
- Dark mode by default, with light or "match system" per browser
- Command palette (`Ctrl+K`) to jump to pages or servers and start, stop, or restart them
- Self-hosted fonts and charts, so it works on air-gapped hosts
- Live status in navigation via SignalR
- Reconnect handling with state recovery

</details>

---

## Quick Start

1. Make sure you have a **Steam account that owns Arma 3** and a basic understanding of Arma 3 dedicated servers.
2. Download the latest release for your platform (see [Installation](#installation)).
3. Run the `KAST.UI` executable and open the panel in your browser.
4. Create the first administrator on first run, then configure your servers.

---

## Installation

CASTER ships as a self-contained single-file executable. No .NET installation is needed on the host.

### Stable releases

Download the latest build from the [Releases page](https://github.com/bluefield-creator/KAST/releases/latest):

| Platform | Archive |
| --- | --- |
| Linux x64 | `kast-linux-x64-v*.tar.gz` |
| Linux arm64 | `kast-linux-arm64-v*.tar.gz` |
| Windows x64 | `kast-win-x64-v*.zip` |
| Docker | `ghcr.io/bluefield-creator/kast:latest` or `:stable` |

Extract and run `KAST.UI`. On Linux you may need to run `chmod +x KAST.UI` first.

### Nightly builds

Automated builds from the `caster` branch are published as a rolling pre-release at [`releases/tag/nightly`](https://github.com/bluefield-creator/KAST/releases/tag/nightly). Download URLs are stable.

| Platform | File |
| --- | --- |
| Linux x64 | `kast-linux-x64-nightly.tar.gz` |
| Linux arm64 | `kast-linux-arm64-nightly.tar.gz` |
| Windows x64 | `kast-win-x64-nightly.zip` |
| Docker | `ghcr.io/bluefield-creator/kast:nightly` |

> [!WARNING]
> Nightly builds may be unstable. Use tagged releases for production.

### Docker (Compose)

```yaml
services:
  kast:
    image: ghcr.io/bluefield-creator/kast:latest
    ports:
      - "8080:8080"
    volumes:
      - kast-data:/app/data
volumes:
  kast-data:
```

---

## Configuration

### Authentication and OIDC

CASTER uses local administrator accounts by default. OpenID Connect can be enabled through configuration or environment variables.

| Mode | Behavior |
| --- | --- |
| `Local` | Local CASTER username and password sign-in only. |
| `Oidc` | OIDC sign-in only. The first allowed OIDC user bootstraps the first administrator. |
| `LocalAndOidc` | OIDC sign-in, with local administrator passwords kept as a fallback. |

Example Docker environment:

```yaml
environment:
  - Auth__Mode=LocalAndOidc
  - Auth__Oidc__Authority=https://auth.example.com/application/o/kast/
  - Auth__Oidc__ClientId=kast
  - Auth__Oidc__ClientSecret=replace-with-provider-secret
  - Auth__Oidc__DisplayName=OpenID Connect
  - Auth__Oidc__GroupClaim=groups
  - Auth__Oidc__NameClaim=preferred_username
  - Auth__Oidc__AllowedGroups__0=KAST Admins
```

#### Authentik setup

| Setting | Value |
| --- | --- |
| Redirect URI | `https://<kast-host>/auth/oidc/callback` |
| Logout / signed-out URI | `https://<kast-host>/auth/oidc/signed-out` |
| Scopes | `openid profile email` |
| Group claim | `groups` |
| Required group value | `KAST Admins` |

> Assigning a user to the Authentik application is not enough. The user must also belong to at least one allowed group in the group claim. CASTER links external accounts by OIDC issuer plus `sub`, so email or username changes do not break the link.

### System account sign-in

Existing administrators can enable system account sign-in from **Settings → Accounts**:

- **Windows:** local machine users or AD domain users.
- **Linux:** local Linux users on the running system.
- **Docker:** users inside the CASTER container, not the Docker host. Create or mount those container accounts before selecting them.

### Reverse proxy

Keep websocket proxying enabled for Blazor Server, and configure CASTER to trust only the proxy IPs that can reach it. For a local Caddy proxy the default trusted proxies are `127.0.0.1` and `::1`; override them with `ForwardedHeaders:KnownProxies` if the proxy runs elsewhere.

```caddyfile
panel.3rdshock.army {
    encode zstd gzip

    reverse_proxy 127.0.0.1:5000 {
        flush_interval -1
    }
}
```

---

## Operations

- **Health:** `/health` reports basic web and database health. `/ready` also reports the active mod download count.
- **Downloads:** bulk mod downloads are queued durably. If CASTER exits mid-download, it reconciles interrupted records on startup and resets mods left in `Downloading` or `Updating` so they can be retried safely.
- **Windows Service:** the **Settings → Service** tab installs CASTER as a service, sets startup mode, configures crash restart actions, and shows recent crash reports. Service changes require running CASTER as Administrator.
- **Other platforms:** use your supervisor for restarts, such as `systemd`, Docker restart policies, or your hosting platform's restart policy.

---

## Versioning

CASTER uses [MinVer](https://github.com/adamralph/minver) to derive the version from git tags at build time.

| Scenario | Version format |
| --- | --- |
| Tagged release `v1.2.3` | `1.2.3` |
| Nightly (`caster`) | `1.2.3-nightly.20260522.abc1234` |
| Local dev build | `1.2.3-alpha.0.5` |

To cut a stable release, run the **Promote Stable Release** workflow in GitHub Actions, or push an annotated `vX.Y.Z` tag from a commit reachable from `caster`. The pipeline runs tests, builds native binaries for all platforms, publishes checksums, pushes Docker tags, and creates a non-prerelease GitHub Release. Maintainer steps are in [CONTRIBUTING.md](CONTRIBUTING.md#release-process).

---

## Documentation

Full documentation is on the [GitHub Wiki](https://github.com/bluefield-creator/KAST/wiki).

| Document | Contents |
| --- | --- |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Branching, commits, development setup, release process |
| [CHANGELOG.md](CHANGELOG.md) | Generated release notes |
| [SECURITY.md](SECURITY.md) | Reporting vulnerabilities |
| [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) | Community standards |

---

## Support

- **Bugs and feature requests:** open an issue on the [GitHub repository](https://github.com/bluefield-creator/KAST/issues).
- **Security issues:** follow [SECURITY.md](SECURITY.md) and do not file them publicly.

---

<p align="center">
  <sub>Licensed under the terms in <a href="LICENSE.md">LICENSE.md</a>.</sub>
</p>
