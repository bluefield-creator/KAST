# CASTER v1 — Implementation Plan
**Project:** CASTER (Arma 3 Dedicated Server Manager v2)
**Organizations:** 3rd Shock Army × Bluefield
**License:** Proprietary / Enterprise Fork
**Predecessor:** KAST v1.1.3 (GPLv3, .NET 10, Blazor Server, SQLite, SteamKit2)

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [Architecture](#2-architecture)
3. [Technology Stack](#3-technology-stack)
4. [Repository Structure](#4-repository-structure)
5. [Database Design](#5-database-design)
6. [API Design — .NET 10 Backend](#6-api-design--net-10-backend)
7. [Download Sidecar — Rust + steamroom](#7-download-sidecar--rust--steamroom)
8. [Frontend — Next.js](#8-frontend--nextjs)
9. [Frontend ↔ Backend ↔ Sidecar: End-to-End Data Flow](#9-frontend--backend--sidecar-end-to-end-data-flow)
10. [Steam Authentication Flow](#10-steam-authentication-flow)
11. [Authentication & Authorization — OIDC + AD + RBAC](#11-authentication--authorization--oidc--ad--rbac)
12. [Real-Time Updates — SSE](#12-real-time-updates--sse)
13. [Enterprise Features — Audit Logging & Prometheus Metrics](#13-enterprise-features--audit-logging--prometheus-metrics)
14. [Deployment — Docker Compose](#14-deployment--docker-compose)
15. [Migration Strategy — KAST v1 → CASTER v1](#15-migration-strategy--kast-v1--caster-v1)
16. [Implementation Timeline](#16-implementation-timeline)
17. [Testing Strategy](#17-testing-strategy)
18. [Appendix: KAST v1 → CASTER Mapping](#18-appendix-kast-v1--caster-mapping)

---

## 1. Project Overview

### 1.1 What CASTER Does

CASTER is a web-based Arma 3 dedicated server manager. It provides a single control plane for:

- Installing and updating Arma 3 dedicated server files (stable, development, legacy, DLC branches)
- Discovering, downloading, updating, and verifying Steam Workshop mods
- Creating and managing multiple server instances with full configuration editing
- Assigning mods to instances with load-order control and client/server flags
- Launching server processes with headless client support and auto-restart watchdogs
- Real-time monitoring of host and instance CPU, memory, and console output
- Scheduling automatic start/stop per instance
- Enterprise authentication via OIDC (Keycloak/Authentik/Azure AD) + Windows AD Kerberos
- Role-based access control (Admin, Operator, Viewer)
- Audit logging of all administrative actions
- Prometheus metrics export for infrastructure monitoring

### 1.2 Design Principles

1. **Stateless API, stateful data** — The .NET backend stores no in-memory session state beyond auth cookies. All state lives in PostgreSQL. This enables horizontal scaling.
2. **Separation of concerns by protocol** — Steam's binary CM protocol and CDN chunk protocol live entirely in the Rust sidecar. The .NET backend never touches Steam wire formats.
3. **SSE over WebSocket** — Real-time updates use Server-Sent Events (SSE), eliminating the sticky-session requirement of SignalR/WebSocket and working natively with HTTP load balancers.
4. **Vertical slice modules** — Each business capability (Servers, Mods, Installations, Monitoring, Auth, Audit, Settings) is a self-contained module with its own endpoints, services, and DB queries. No cross-module service references except through published events.
5. **Library-first downloader** — The Rust sidecar uses `steamroom-client` as a library crate, not as a CLI child process. This gives programmatic control over auth, download progress, and cancellation.

---

## 2. Architecture

### 2.1 Service Topology

```
                    ┌──────────────────────────────────────────┐
                    │              nginx:alpine                │
                    │  :80 → cast-ui:3000 (Next.js)            │
                    │  :80/api/ → cast-api:8080 (.NET)         │
                    │  :80/api/sse/ → cast-api:8080 (SSE)      │
                    │  :80/metrics → cast-api:8080 (Prometheus)│
                    └────┬──────────────┬──────────────────────┘
                         │              │
              ┌──────────▼──┐    ┌──────▼──────────────┐
              │  cast-ui    │    │  cast-api (.NET 10)  │
              │  Next.js 15 │    │  Minimal API         │
              │  shadcn/ui  │    │                      │
              │  TanStack Q │    │  ┌────────────────┐  │
              │  Zustand    │    │  │ Vertical Slices│  │
              │             │    │  │ • Servers      │  │
              │  /servers   │    │  │ • Mods         │  │
              │  /mods      │    │  │ • Installations│  │
              │  /settings  │    │  │ • Monitoring   │  │
              │  /monitoring│    │  │ • Auth         │  │
              │  /login     │    │  │ • Audit        │  │
              └─────────────┘    │  │ • Settings     │  │
                                 │  └───────┬────────┘  │
                                 │          │            │
                                 │  EF Core + PostgreSQL  │
                                 │  gRPC → downloader     │
                                 │  SSE → cast-ui         │
                                 └──────────┬─────────────┘
                                            │ gRPC
                                ┌───────────▼─────────────┐
                                │  cast-downloader (Rust)  │
                                │  tonic gRPC server       │
                                │                          │
                                │  ┌────────────────────┐  │
                                │  │ steamroom-client   │  │
                                │  │ • CM connection    │  │
                                │  │ • Auth (QR/token/  │  │
                                │  │   credentials/anon)│  │
                                │  │ • PICS product info│  │
                                │  │ • Depot keys       │  │
                                │  │ • CDN server pool  │  │
                                │  │ • Manifest download│  │
                                │  │ • Chunk download   │  │
                                │  │ • Workshop download│  │
                                │  │ • Delta updates    │  │
                                │  └────────────────────┘  │
                                │                          │
                                │  steamroom (core crate)  │
                                │  • CM TCP/WebSocket      │
                                │  • Proto serialization   │
                                │  • AES/RSA crypto        │
                                │  • LZMA/zstd decompress  │
                                └──────────────────────────┘

                    ┌──────────────────┐
                    │  postgres:17     │
                    │  Persistent data │
                    └──────────────────┘

                    ┌──────────────────┐
                    │  prometheus       │
                    │  Metrics scraping │
                    └──────────────────┘
```

### 2.2 Network Boundaries

```
Service              Internal Port    Exposed via nginx    Protocol
─────────────────────────────────────────────────────────────────────
cast-ui              3000             :80/                 HTTP/1.1
cast-api             8080             :80/api/*            HTTP/1.1
cast-api (SSE)       8080             :80/api/sse/*        HTTP/1.1 (long-lived)
cast-api (metrics)   8080             :80/metrics          HTTP/1.1
cast-downloader      50051            (internal only)      gRPC (HTTP/2)
postgres             5432             (internal only)      PostgreSQL wire
prometheus           9090             :9090                HTTP/1.1
```

Key: nginx terminates browser traffic. cast-downloader and postgres are internal-only (no external port mapping). nginx does NOT proxy the gRPC connection — cast-api talks directly to cast-downloader within the Docker network.

### 2.3 Data Flow Patterns

**Pattern A: Request/Response (CRUD operations)**
```
Browser → nginx → cast-api → PostgreSQL → JSON response → nginx → Browser
```
Used for: server CRUD, mod CRUD, settings, user management, audit log queries.

**Pattern B: Long-Running Operation with Streaming Progress (downloads)**
```
Browser → nginx → cast-api
                       │
                       ├── INSERT download_task (PostgreSQL)
                       ├── gRPC: DownloadApp(stream) → cast-downloader
                       │       │
                       │       ├── Steam CM: connect, auth
                       │       ├── Steam PICS: resolve depots
                       │       ├── Steam CDN: download manifests
                       │       ├── Steam CDN: download chunks (streaming)
                       │       └──→ DownloadEvent stream
                       │
                       ├── UPDATE download_task.progress (PostgreSQL)
                       └── SSE: progress event → nginx → Browser
```
Used for: server installation, mod download, mod update, benchmark.

**Pattern C: Real-Time Push (monitoring, status changes)**
```
cast-api (BackgroundService)
  │
  ├── Poll process metrics / watchdog / schedule
  ├── WRITE monitoring snapshot (PostgreSQL)
  └── SSE: broadcast to all connected clients → nginx → Browser
```
Used for: host metrics, instance metrics, server status changes, Steam connection status.

**Pattern D: Steam Auth (interactive, multi-step)**
```
Browser → nginx → cast-api
                    │
                    ├── gRPC: BeginQrAuth(stream) → cast-downloader
                    │       │
                    │       ├── LoginBuilder::with_qr()
                    │       ├──→ AuthEvent::QrChallenge(url)
                    │       ├── SSE → Browser (render QR)
                    │       ├── ... user scans on phone ...
                    │       ├── steamroom polls Steam
                    │       ├──→ AuthEvent::LoggedIn(account, steam_id)
                    │       └── persist refresh token to disk
                    │
                    ├── UPDATE steam_auth_state (PostgreSQL)
                    └── SSE: auth_state → Browser
```
Used for: QR login, credential login, token refresh.

---

## 3. Technology Stack

### 3.1 Backend (.NET 10)

| Component           | Technology                                | Rationale                                  |
|---------------------|-------------------------------------------|--------------------------------------------|
| Runtime             | .NET 10 (net10.0)                         | Latest LTS, KAST v1 experience             |
| Web framework       | ASP.NET Core Minimal API                  | Lightweight, AOT-compatible, vertical slices|
| ORM                 | Entity Framework Core 10 + Npgsql         | Mature PostgreSQL provider, migrations     |
| Database            | PostgreSQL 17                             | Concurrent writes, JSONB, WAL archiving    |
| gRPC client         | Grpc.Net.Client                           | Native .NET gRPC, HTTP/2, streaming        |
| Auth (OIDC)         | Microsoft.AspNetCore.Authentication.OpenIdConnect | Built-in, battle-tested          |
| Auth (AD/Kerberos)  | Microsoft.AspNetCore.Authentication.Negotiate | Windows domain auth                  |
| Auth (API tokens)   | PBKDF2-hashed bearer tokens (prefix lookup) | Same as KAST v1, no external deps       |
| Real-time           | System.Threading.Channels + SSE middleware | Stateless, no sticky sessions needed     |
| Observability       | OpenTelemetry SDK + Prometheus exporter   | Standard enterprise stack                  |
| Logging             | Serilog + PostgreSQL sink                 | Structured logs, queryable via API         |
| Validation          | FluentValidation                          | Per-endpoint validators, clean errors      |
| Mapping             | Mapperly (source generator)               | Zero-reflection, AOT-compatible            |

### 3.2 Frontend (Next.js 15)

| Component           | Technology                                | Rationale                                  |
|---------------------|-------------------------------------------|--------------------------------------------|
| Framework           | Next.js 15 (App Router)                   | React Server Components, streaming, caching|
| Language            | TypeScript 5.x (strict mode)              | Type safety across full stack              |
| UI components       | shadcn/ui (Radix primitives + Tailwind)   | Accessible, customizable, copy-paste model |
| Styling             | Tailwind CSS 4                            | Utility-first, dark mode via class toggle  |
| Data fetching       | TanStack Query 5                          | Cache, refetch, optimistic updates         |
| Client state        | Zustand                                   | Lightweight stores for UI state            |
| Forms               | React Hook Form + Zod                     | Validation schema shared with API contracts|
| Real-time           | EventSource (native SSE) via custom hook  | No library needed, standard browser API    |
| Charts              | Recharts                                  | React-native charting for monitoring       |
| Code editor         | Monaco Editor                             | server.cfg / difficulty editing            |
| Toast notifications | Sonner                                    | Minimal toast library                      |

### 3.3 Download Sidecar (Rust)

| Component           | Technology                                | Rationale                                  |
|---------------------|-------------------------------------------|--------------------------------------------|
| Runtime             | Rust 1.88+ (edition 2024)                 | Zero-cost abstractions, no GC              |
| Async runtime       | Tokio                                     | Industry standard, multi-threaded          |
| gRPC server         | tonic 0.14                                | Native Rust gRPC, streaming support        |
| Steam protocol      | steamroom 0.2 (MIT/Apache-2.0)            | Battle-tested pure-Rust reimplementation   |
| Download orchestration | steamroom-client 0.2                    | DepotJob, CdnChunkFetcher, progress events |
| Crypto              | aes, rsa, sha1, hmac (via steamroom)      | Chunk decryption, filename decryption      |
| HTTP/2 CDN          | reqwest + h2 (via steamroom)              | Multiplexed chunk downloads                |
| Compression         | lzma-rs, zstd, flate2 (via steamroom)      | Valve LZMA, VZip, VZstd decompression      |
| Protobuf            | prost 0.14                                | gRPC + Steam wire format serialization     |
| Logging             | tracing + tracing-subscriber              | Structured, leveled, JSON output           |
| Health              | tonic-health                              | gRPC health check protocol                 |

### 3.4 Infrastructure

| Component           | Technology                                |
|---------------------|-------------------------------------------|
| Container runtime   | Docker 27+                                |
| Reverse proxy       | nginx:alpine                              |
| Database            | postgres:17-alpine                        |
| Metrics             | prom/prometheus + grafana/grafana (opt.)  |
| CI/CD               | GitHub Actions (inherited from KAST)      |

---

## 4. Repository Structure

```
CASTER/
├── CASTER_V1                        # This document
├── docker/
│   ├── docker-compose.yml           # Full stack
│   ├── docker-compose.dev.yml       # Dev overrides (hot-reload, exposed ports)
│   ├── nginx.conf                   # Reverse proxy config
│   ├── postgres/
│   │   └── init.sql                 # Initial DB setup, extensions
│   └── prometheus/
│       └── prometheus.yml           # Scrape config
│
├── src/
│   ├── backend/                     # .NET 10 solution
│   │   ├── Cast.slnx                # Solution file (new .slnx format)
│   │   ├── Directory.Build.props    # Shared build properties
│   │   ├── Directory.Packages.props # Central package management
│   │   ├── global.json              # .NET SDK version
│   │   │
│   │   ├── src/
│   │   │   ├── Cast.Core/           # Domain layer (models, interfaces, enums)
│   │   │   │   ├── Models/          # EF Core entity types
│   │   │   │   ├── Interfaces/      # Service contracts
│   │   │   │   ├── Enums/           # Enumeration types
│   │   │   │   ├── Events/          # Domain event records
│   │   │   │   └── Dtos/            # Data transfer objects (shared)
│   │   │   │
│   │   │   ├── Cast.Infrastructure/ # Persistence & external services
│   │   │   │   ├── Data/            # AppDbContext, migrations, interceptors
│   │   │   │   ├── Downloader/      # gRPC client to cast-downloader
│   │   │   │   ├── Auth/            # OIDC configuration, AD/Kerberos
│   │   │   │   ├── Audit/           # Audit log interceptor + service
│   │   │   │   ├── Metrics/         # Prometheus metric definitions
│   │   │   │   ├── Sse/             # SSE connection manager
│   │   │   │   ├── Steam/           # Steam Web API HTTP client (workshop metadata)
│   │   │   │   └── Services/        # Service implementations
│   │   │   │       ├── ServerService.cs
│   │   │   │       ├── ModService.cs
│   │   │   │       ├── InstallationService.cs
│   │   │   │       ├── MonitoringService.cs
│   │   │   │       ├── ContentOrchestrator.cs
│   │   │   │       ├── ...
│   │   │   │       └── Content/
│   │   │   │           ├── ServerInstaller.cs
│   │   │   │           ├── SteamModInstaller.cs
│   │   │   │           └── LocalModInstaller.cs
│   │   │   │
│   │   │   └── Cast.Api/            # ASP.NET Core Minimal API host
│   │   │       ├── Program.cs       # Bootstrap, middleware, DI
│   │   │       ├── Modules/         # Vertical slice endpoint groups
│   │   │       │   ├── ServersModule.cs
│   │   │       │   ├── ModsModule.cs
│   │   │       │   ├── InstallationsModule.cs
│   │   │       │   ├── MonitoringModule.cs
│   │   │       │   ├── AuthModule.cs
│   │   │       │   ├── AuditModule.cs
│   │   │       │   ├── SettingsModule.cs
│   │   │       │   ├── SseModule.cs
│   │   │       │   └── MetricsModule.cs
│   │   │       ├── BackgroundServices/
│   │   │       │   ├── MetricsCollectorService.cs
│   │   │       │   ├── ProcessWatchdogService.cs
│   │   │       │   ├── SchedulingService.cs
│   │   │       │   └── ModDownloadQueueService.cs
│   │   │       ├── Middleware/
│   │   │       │   ├── AuditMiddleware.cs
│   │   │       │   └── ExceptionHandlingMiddleware.cs
│   │   │       └── Auth/
│   │   │           ├── Policies.cs
│   │   │           └── RoleHandler.cs
│   │   │
│   │   └── tests/
│   │       ├── Cast.Api.Tests/       # Integration tests (TestHost)
│   │       ├── Cast.Core.Tests/      # Domain logic tests
│   │       └── Cast.Infrastructure.Tests/ # Service + DB tests
│   │
│   ├── frontend/                    # Next.js 15 application
│   │   ├── package.json
│   │   ├── tsconfig.json
│   │   ├── tailwind.config.ts
│   │   ├── components.json          # shadcn/ui config
│   │   ├── next.config.ts
│   │   │
│   │   ├── app/                     # Next.js App Router
│   │   │   ├── layout.tsx           # Root layout (providers, theme)
│   │   │   ├── page.tsx             # Dashboard (home)
│   │   │   ├── (auth)/              # Auth route group
│   │   │   │   ├── login/page.tsx
│   │   │   │   └── setup/page.tsx   # First-run admin creation
│   │   │   ├── servers/
│   │   │   │   ├── page.tsx         # Server list
│   │   │   │   ├── new/page.tsx     # Create server
│   │   │   │   └── [id]/page.tsx    # Edit server (tabbed)
│   │   │   ├── mods/
│   │   │   │   └── page.tsx         # Mod management (table/cards)
│   │   │   ├── installations/
│   │   │   │   └── page.tsx         # Shared core installations
│   │   │   ├── monitoring/
│   │   │   │   └── page.tsx         # Host + instance monitoring
│   │   │   └── settings/
│   │   │       └── page.tsx         # Application settings
│   │   │
│   │   ├── components/
│   │   │   ├── ui/                  # shadcn/ui primitives (generated)
│   │   │   ├── layout/              # App shell (sidebar, header, footer)
│   │   │   ├── servers/             # Server-specific components
│   │   │   ├── mods/                # Mod-specific components
│   │   │   ├── monitoring/          # Charts, metrics cards
│   │   │   ├── auth/                # Login form, OIDC buttons
│   │   │   └── shared/              # Reusable across modules
│   │   │
│   │   ├── hooks/                   # Custom React hooks
│   │   │   ├── use-sse.ts           # SSE subscription hook
│   │   │   ├── use-download-progress.ts
│   │   │   ├── use-steam-auth.ts    # Steam auth state + QR
│   │   │   └── use-server-status.ts
│   │   │
│   │   ├── lib/                     # Utilities
│   │   │   ├── api.ts               # API client (fetch wrapper)
│   │   │   ├── auth.ts              # Auth helpers (OIDC session)
│   │   │   ├── validators.ts        # Zod schemas (shared with API)
│   │   │   └── constants.ts
│   │   │
│   │   ├── stores/                  # Zustand stores
│   │   │   ├── theme-store.ts
│   │   │   ├── sidebar-store.ts
│   │   │   └── notifications-store.ts
│   │   │
│   │   └── public/                  # Static assets
│   │
│   └── downloader/                  # Rust sidecar
│       ├── Cargo.toml
│       ├── Cargo.lock
│       ├── build.rs                 # prost + tonic-build (compile proto)
│       ├── proto/
│       │   └── downloader.proto     # gRPC service definition (source of truth)
│       └── src/
│           ├── main.rs              # Entry: tokio runtime, tonic server, health
│           ├── server.rs            # tonic DownloadService implementation
│           ├── session.rs           # Steam session lifecycle + auth state machine
│           ├── download.rs          # App download: PICS → keys → CDN → DepotJob
│           ├── workshop.rs          # Workshop download pipeline
│           └── error.rs             # Domain error → tonic::Status mapping
│
└── docs/
    ├── architecture.md
    ├── api-spec.yaml                # OpenAPI 3.1 spec (auto-generated)
    └── steam-auth-flow.md           # Detailed Steam auth sequence diagrams
```

---

## 5. Database Design

### 5.1 PostgreSQL Configuration

```sql
-- Extensions
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pg_stat_statements";
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- Connection settings
ALTER SYSTEM SET max_connections = '50';
ALTER SYSTEM SET shared_buffers = '256MB';
ALTER SYSTEM SET wal_level = 'replica';          -- Enables WAL archiving for backups
ALTER SYSTEM SET archive_mode = 'on';
ALTER SYSTEM SET archive_command = '/bin/true';   -- Override in production
```

### 5.2 Schema — Core Entities

```sql
-- Users (local + OIDC-provisioned + system accounts)
CREATE TABLE users (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    username        VARCHAR(128) NOT NULL UNIQUE,
    display_name    VARCHAR(256),
    email           VARCHAR(256),
    password_hash   VARCHAR(512),           -- NULL for OIDC users
    auth_source     VARCHAR(32) NOT NULL,   -- 'local', 'oidc', 'system'
    oidc_subject    VARCHAR(256),           -- OIDC 'sub' claim
    oidc_provider   VARCHAR(64),            -- 'keycloak', 'authentik', 'azure_ad'
    role            VARCHAR(32) NOT NULL DEFAULT 'viewer',  -- 'admin', 'operator', 'viewer'
    avatar_url      VARCHAR(1024),
    last_login_at   TIMESTAMPTZ,
    is_disabled     BOOLEAN NOT NULL DEFAULT FALSE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_users_username ON users(username);
CREATE INDEX idx_users_oidc_subject ON users(oidc_subject, oidc_provider);

-- API keys
CREATE TABLE api_keys (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    key_prefix      VARCHAR(8) NOT NULL,     -- First 8 chars for lookup
    key_hash        VARCHAR(512) NOT NULL,   -- PBKDF2 hash
    label           VARCHAR(256),
    last_used_at    TIMESTAMPTZ,
    expires_at      TIMESTAMPTZ,
    is_revoked      BOOLEAN NOT NULL DEFAULT FALSE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_api_keys_prefix ON api_keys(key_prefix);

-- Arma 3 shared installations
CREATE TABLE installations (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name            VARCHAR(256) NOT NULL,
    app_id          INTEGER NOT NULL DEFAULT 233780,
    branch          VARCHAR(64) NOT NULL DEFAULT 'public',
    install_path    VARCHAR(1024) NOT NULL,
    installed_manifest_ids JSONB DEFAULT '{}',  -- { "depot_id": manifest_gid }
    install_status  VARCHAR(32) NOT NULL DEFAULT 'not_installed',
                                 -- 'not_installed', 'installing', 'installed',
                                 -- 'updating', 'validating', 'failed'
    last_installed_at TIMESTAMPTZ,
    last_checked_at TIMESTAMPTZ,
    size_on_disk    BIGINT DEFAULT 0,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Steam Workshop mods
CREATE TABLE mods (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workshop_id     BIGINT NOT NULL UNIQUE,
    title           VARCHAR(512),
    description     TEXT,
    author          VARCHAR(256),
    author_steam_id BIGINT,
    preview_url     VARCHAR(1024),
    file_size       BIGINT,
    consumer_app_id INTEGER,
    manifest_id     BIGINT,              -- Current installed manifest
    install_path    VARCHAR(1024),
    status          VARCHAR(32) NOT NULL DEFAULT 'not_installed',
                                 -- 'not_installed', 'downloading', 'installed',
                                 -- 'updating', 'validating', 'error'
    source          VARCHAR(32) NOT NULL DEFAULT 'workshop',
                                 -- 'workshop', 'local'
    is_client_side  BOOLEAN NOT NULL DEFAULT FALSE,
    comment         TEXT,
    last_checked_at TIMESTAMPTZ,
    installed_at    TIMESTAMPTZ,
    size_on_disk    BIGINT DEFAULT 0,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_mods_workshop_id ON mods(workshop_id);
CREATE INDEX idx_mods_status ON mods(status);

-- Server instances
CREATE TABLE server_instances (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name            VARCHAR(256) NOT NULL,
    port            INTEGER NOT NULL DEFAULT 2302,
    steam_query_port INTEGER NOT NULL DEFAULT 27016,
    installation_id  UUID REFERENCES installations(id) ON DELETE RESTRICT,
    install_path    VARCHAR(1024),
    server_config   TEXT,                 -- Raw server.cfg content
    basic_config    TEXT,                 -- Raw basic.cfg content
    arma_profile    TEXT,                 -- Raw Arma3Profile content
    additional_params TEXT,               -- Extra CLI arguments
    headless_client_count INTEGER NOT NULL DEFAULT 0,
    status          VARCHAR(32) NOT NULL DEFAULT 'stopped',
                                 -- 'stopped', 'starting', 'running',
                                 -- 'stopping', 'crashed', 'error'
    process_id      INTEGER,
    started_at      TIMESTAMPTZ,
    auto_start_time TIME,                 -- HH:mm
    auto_stop_time  TIME,                 -- HH:mm
    schedule_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    restart_policy  VARCHAR(32) NOT NULL DEFAULT 'on_crash',
                                 -- 'none', 'on_crash', 'always'
    max_restarts    INTEGER DEFAULT 3,
    -- Creator DLC toggles
    enable_contact  BOOLEAN NOT NULL DEFAULT FALSE,
    enable_gm       BOOLEAN NOT NULL DEFAULT FALSE,
    enable_csla     BOOLEAN NOT NULL DEFAULT FALSE,
    enable_ws       BOOLEAN NOT NULL DEFAULT FALSE,
    enable_spearhead BOOLEAN NOT NULL DEFAULT FALSE,
    enable_rf       BOOLEAN NOT NULL DEFAULT FALSE,
    enable_ef       BOOLEAN NOT NULL DEFAULT FALSE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_server_instances_status ON server_instances(status);

-- Server-to-mod assignment (many-to-many)
CREATE TABLE server_instance_mods (
    server_instance_id UUID NOT NULL REFERENCES server_instances(id) ON DELETE CASCADE,
    mod_id             UUID NOT NULL REFERENCES mods(id) ON DELETE CASCADE,
    sort_order         INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (server_instance_id, mod_id)
);

-- Download tasks (queued/active/completed downloads)
CREATE TABLE download_tasks (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    task_type       VARCHAR(32) NOT NULL,  -- 'app_install', 'app_update',
                                           -- 'mod_download', 'mod_update',
                                           -- 'benchmark'
    target_id       UUID,                  -- FK to installations.id or mods.id
    status          VARCHAR(32) NOT NULL DEFAULT 'queued',
                                 -- 'queued', 'running', 'completed',
                                 -- 'failed', 'cancelled'
    progress_pct    REAL DEFAULT 0,
    phase           VARCHAR(64),           -- Current download phase
    bytes_downloaded BIGINT DEFAULT 0,
    bytes_total     BIGINT,
    current_file    VARCHAR(512),
    error_message   TEXT,
    retry_count     INTEGER DEFAULT 0,
    max_retries     INTEGER DEFAULT 3,
    created_by      UUID REFERENCES users(id),
    queued_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ
);

CREATE INDEX idx_download_tasks_status ON download_tasks(status);
CREATE INDEX idx_download_tasks_target ON download_tasks(target_id, task_type);

-- Steam auth state (persistent session across sidecar restarts)
CREATE TABLE steam_auth_state (
    id              INTEGER PRIMARY KEY DEFAULT 1 CHECK (id = 1),  -- Singleton row
    is_authenticated BOOLEAN NOT NULL DEFAULT FALSE,
    account_name    VARCHAR(256),
    steam_id        BIGINT,
    avatar_url      VARCHAR(1024),
    refresh_token   TEXT,             -- Encrypted at rest
    last_login_at   TIMESTAMPTZ,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Application settings (singleton row)
CREATE TABLE settings (
    id              INTEGER PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    mods_directory          VARCHAR(1024),
    servers_directory       VARCHAR(1024),
    crash_reports_directory VARCHAR(1024),
    parallel_mod_downloads  INTEGER DEFAULT 4,
    parallel_chunk_downloads INTEGER DEFAULT 16,
    steam_app_api_key       VARCHAR(128),  -- For workshop metadata HTTP API
    update_channel          VARCHAR(32) DEFAULT 'stable',
    auto_update_check       BOOLEAN DEFAULT TRUE,
    telemetry_enabled       BOOLEAN DEFAULT FALSE,
    theme_accent_color      VARCHAR(8) DEFAULT '#4F46E5',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

### 5.3 Schema — Audit & Observability

```sql
-- Audit log (all administrative actions)
CREATE TABLE audit_log (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    actor_id        UUID REFERENCES users(id),
    actor_name      VARCHAR(256),
    action          VARCHAR(64) NOT NULL,    -- 'server.start', 'mod.delete', etc.
    resource_type   VARCHAR(64) NOT NULL,    -- 'server', 'mod', 'installation', etc.
    resource_id     UUID,
    resource_name   VARCHAR(512),
    old_values      JSONB,                   -- Previous state (for updates/deletes)
    new_values      JSONB,                   -- New state (for creates/updates)
    ip_address      INET,
    user_agent      VARCHAR(512),
    status          VARCHAR(16) NOT NULL DEFAULT 'success',  -- 'success', 'failure'
    error_message   TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_audit_log_actor ON audit_log(actor_id, created_at DESC);
CREATE INDEX idx_audit_log_resource ON audit_log(resource_type, resource_id);
CREATE INDEX idx_audit_log_action ON audit_log(action, created_at DESC);
CREATE INDEX idx_audit_log_created ON audit_log(created_at DESC);

-- Real-time monitoring snapshots (pruned to 24 hours)
CREATE TABLE monitoring_snapshots (
    id              BIGSERIAL PRIMARY KEY,
    timestamp       TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Host metrics
    host_cpu_pct    REAL,
    host_memory_used_bytes BIGINT,
    host_memory_total_bytes BIGINT,
    host_disk_read_bytes BIGINT,
    host_disk_write_bytes BIGINT,
    -- Per-instance metrics (stored as JSONB array for flexibility)
    instances       JSONB DEFAULT '[]'
);

CREATE INDEX idx_monitoring_ts ON monitoring_snapshots(timestamp DESC);

-- Auto-prune old snapshots (keep 24 hours at 5s intervals = ~17,280 rows)
-- Run via pg_cron or application-level cleanup
```

---

## 6. API Design — .NET 10 Backend

### 6.1 Project Structure Philosophy

The .NET backend follows **vertical slice architecture** — each business capability is a self-contained module with its own:

- Endpoint definitions (Minimal API route group)
- Request/response DTOs
- Validation rules
- Service implementation

Cross-cutting concerns (auth, audit, metrics, SSE broadcasting) are applied via middleware and endpoint filters, not base classes or global handlers.

### 6.2 Module: Servers

```
Module: Cast.Api/Modules/ServersModule.cs
Service: Cast.Infrastructure/Services/ServerService.cs
Entities: server_instances, server_instance_mods, headless_clients
```

```
GET    /api/servers                  List all servers (with status, mod count)
POST   /api/servers                  Create server
GET    /api/servers/{id}             Get server detail (includes mods, configs)
PUT    /api/servers/{id}             Update server
DELETE /api/servers/{id}             Delete server (stops if running, removes files)
POST   /api/servers/{id}/start       Start server process
POST   /api/servers/{id}/stop        Stop server process
POST   /api/servers/{id}/restart     Restart server process
GET    /api/servers/{id}/console     Get recent console output
GET    /api/servers/{id}/events      Get recent runtime events (RPT log)
PUT    /api/servers/{id}/mods        Set mod list (replaces all assignments)
PATCH  /api/servers/{id}/mods/reorder  Reorder mods
GET    /api/servers/{id}/missions    List missions
POST   /api/servers/{id}/missions    Upload mission file
DELETE /api/servers/{id}/missions/{name}  Delete mission
```

### 6.3 Module: Mods

```
GET    /api/mods                     List all mods (filterable by status, source)
POST   /api/mods                     Add mod by workshop ID or URL
GET    /api/mods/{id}                Get mod detail (manifests, size, metadata)
DELETE /api/mods/{id}                Delete mod (removes files)
POST   /api/mods/{id}/download       Queue download/update
POST   /api/mods/{id}/cancel         Cancel active download
POST   /api/mods/{id}/check-update   Check if newer manifest exists on Steam
POST   /api/mods/check-all           Check all workshop mods for updates
POST   /api/mods/update-all          Queue all outdated mods for download
POST   /api/mods/repair-all          Re-download all mods
POST   /api/mods/import-local        Import local mod folder
POST   /api/mods/import-preset       Import Arma 3 Launcher HTML preset
GET    /api/mods/{id}/workshop-info  Fetch latest workshop metadata from Steam
```

### 6.4 Module: Installations

```
GET    /api/installations            List shared installations
POST   /api/installations            Create installation
GET    /api/installations/{id}       Get installation detail
PUT    /api/installations/{id}       Update installation
DELETE /api/installations/{id}       Delete installation
POST   /api/installations/{id}/install   Install/update Arma 3 server files
POST   /api/installations/{id}/validate  Validate installed files
POST   /api/installations/{id}/cancel    Cancel active install
```

### 6.5 Module: Monitoring

```
GET    /api/monitoring/host          Current host metrics (CPU, RAM, disk)
GET    /api/monitoring/host/history  Historical host metrics (N data points)
GET    /api/monitoring/instances     Current instance metrics (per-server)
GET    /api/monitoring/downloads     Active download queue state
```

### 6.6 Module: Auth

```
POST   /api/auth/login               Local password login
GET    /api/auth/oidc/login           Initiate OIDC flow (redirect)
GET    /api/auth/oidc/callback        OIDC callback handler
POST   /api/auth/logout               End session
GET    /api/auth/me                    Current user info + permissions
POST   /api/auth/setup                First-time admin creation (no users exist)
GET    /api/auth/steam/status         Steam connection status
POST   /api/auth/steam/begin-qr       Start Steam QR login
POST   /api/auth/steam/begin-credentials  Start Steam credential login
POST   /api/auth/steam/submit-code    Submit Steam Guard code
POST   /api/auth/steam/logout         Disconnect Steam
GET    /api/auth/steam/profile        Current Steam profile (name, avatar)
PUT    /api/users/{id}                Update user (role, disable)
DELETE /api/users/{id}                Delete user
```

### 6.7 Module: Audit

```
GET    /api/audit                     Query audit log (paginated, filterable)
                                      ?actor_id= &action= &resource_type= &from= &to= &limit=
GET    /api/audit/{id}                Get single audit entry detail
```

### 6.8 Module: Settings

```
GET    /api/settings                  Get all settings
PUT    /api/settings                  Update settings
POST   /api/settings/api-keys         Create API key
DELETE /api/settings/api-keys/{id}    Revoke API key
GET    /api/settings/api-keys         List API keys
GET    /api/settings/crash-reports    List crash reports
DELETE /api/settings/crash-reports    Clear crash reports
```

### 6.9 Module: SSE (Server-Sent Events)

```
GET    /api/sse/events                Subscribe to real-time event stream
                                      ?topics=monitoring,downloads,steam,status
GET    /api/sse/events/{topic}        Subscribe to single topic
```

### 6.10 Endpoint Filters (Cross-Cutting)

Every mutating endpoint is decorated with:

```csharp
group.MapPost("/api/servers", CreateServer)
    .RequireAuthorization(Policies.Admin)
    .WithAuditLog(ResourceType.Server, action: "create")
    .WithMetrics("server_create", "count");
```

`WithAuditLog` is an endpoint filter that:
1. Captures the request body before execution
2. Captures the response body after execution
3. Extracts old/new values for updates
4. Writes a row to `audit_log`

`WithMetrics` increments a Prometheus counter for the given metric name.

### 6.11 Shared API Conventions

- All list endpoints support `?limit=50&offset=0` pagination
- All responses wrap in `{ "data": ..., "meta": { "total": N, "offset": N } }` for lists
- Errors return `{ "error": { "code": "...", "message": "...", "details": [...] } }`
- Timestamps are ISO 8601 with timezone: `2026-06-14T12:00:00Z`
- IDs are UUID v4 strings
- Enums are lowercase strings (e.g. `"running"`, not `1`)

---

## 7. Download Sidecar — Rust + steamroom

### 7.1 gRPC Service Contract

The complete proto file at `src/downloader/proto/downloader.proto`:

```protobuf
syntax = "proto3";
package caster.downloader.v1;

service DownloadService {
  // ── Health ──────────────────────────────────────────
  rpc Check(HealthCheckRequest) returns (HealthCheckResponse);

  // ── Session / Auth ──────────────────────────────────
  rpc GetStatus(GetStatusRequest) returns (StatusResponse);
  rpc BeginAnonymous(Empty) returns (stream AuthEvent);
  rpc BeginToken(TokenAuthRequest) returns (stream AuthEvent);
  rpc BeginCredentials(CredentialsAuthRequest) returns (stream AuthEvent);
  rpc BeginQr(Empty) returns (stream AuthEvent);
  rpc SubmitGuardCode(SubmitGuardCodeRequest) returns (SubmitGuardCodeResponse);
  rpc Logout(Empty) returns (Empty);

  // ── Downloads (server-streaming progress) ───────────
  rpc DownloadApp(DownloadAppRequest) returns (stream DownloadEvent);
  rpc DownloadWorkshopItem(DownloadWorkshopItemRequest) returns (stream DownloadEvent);
  rpc BenchmarkDownload(BenchmarkRequest) returns (stream BenchmarkEvent);

  // ── Control ─────────────────────────────────────────
  rpc Cancel(CancelRequest) returns (Empty);
}

// ── Common ────────────────────────────────────────────
message Empty {}

message GetStatusRequest {
  bool include_profile = 1;
}

message StatusResponse {
  string connection_state = 1;  // 'disconnected', 'connecting', 'connected'
  string auth_state = 2;        // 'anonymous', 'authenticated', 'in_progress'
  string auth_method = 3;       // 'none', 'anonymous', 'token', 'credentials', 'qr'
  optional string account_name = 4;
  optional uint64 steam_id = 5;
  optional string avatar_url = 6;
}

// ── Auth Messages ─────────────────────────────────────
message TokenAuthRequest {
  string account_name = 1;
  string refresh_token = 2;
  string device_name = 3;
}

message CredentialsAuthRequest {
  string account_name = 1;
  string password = 2;
  string device_name = 3;
}

message SubmitGuardCodeRequest {
  string code = 1;           // Email code, device code, or empty for device confirmation
}

message SubmitGuardCodeResponse {
  bool accepted = 1;
  optional string error = 2;
}

message AuthEvent {
  oneof event {
    QrChallenge qr_challenge = 1;
    GuardRequired guard_required = 2;
    LoggedIn logged_in = 3;
    AuthFailed failed = 4;
  }
}

message QrChallenge {
  string challenge_url = 1;    // URL to render as QR code
}

message GuardRequired {
  string guard_kind = 1;       // 'email_code', 'device_code', 'device_confirmation'
  optional string detail = 2;  // e.g., "Code sent to x***@example.com"
}

message LoggedIn {
  string account_name = 1;
  uint64 steam_id = 2;
  string refresh_token = 3;    // Persist this for future logins
  optional string avatar_url = 4;
}

message AuthFailed {
  string error = 1;
  bool retryable = 2;
}

// ── Download Messages ─────────────────────────────────
message DownloadAppRequest {
  uint32 app_id = 1;            // Steam AppID (e.g., 233780 for Arma 3 Server)
  string branch = 2;            // "public", "creatordlc", "legacy", etc.
  repeated uint32 depot_ids = 3;// Specific depots (empty = all for branch)
  string os_filter = 4;         // "" (all), "windows", "linux"
  string install_dir = 5;       // Absolute path (shared Docker volume)
  bool verify = 6;              // SHA-1 verify existing files
  uint32 max_parallel_chunks = 7;
}

message DownloadWorkshopItemRequest {
  uint64 published_file_id = 1;
  string install_dir = 2;
  bool verify = 3;
  uint32 max_parallel_chunks = 4;
}

message BenchmarkRequest {
  uint32 app_id = 1;            // App to benchmark against
  uint32 depot_id = 2;          // Depot with content to measure
  uint32 duration_secs = 3;
}

message DownloadEvent {
  oneof event {
    PhaseChanged phase = 1;
    FileStarted file_started = 2;
    FileCompleted file_completed = 3;
    FileSkipped file_skipped = 4;
    DepotProgress depot_progress = 5;
    DownloadCompleted completed = 6;
    DownloadFailed failed = 7;
  }
}

message PhaseChanged {
  string phase = 1;     // "cm_discovery", "connecting", "authenticating",
                        // "resolving_depots", "fetching_keys",
                        // "fetching_manifests", "downloading", "validating",
                        // "finalizing", "completed"
  string detail = 2;
}

message FileStarted {
  string filename = 1;
  uint64 size = 2;
}

message FileCompleted {
  string filename = 1;
}

message FileSkipped {
  string filename = 1;
  string reason = 2;  // "up_to_date", "filtered", "symlink"
}

message DepotProgress {
  uint64 completed_bytes = 1;
  uint64 total_bytes = 2;
  uint32 files_completed = 3;
  uint32 total_files = 4;
}

message DownloadCompleted {
  uint64 total_bytes = 1;
  uint32 total_files = 2;
  uint32 files_skipped = 3;
  string install_dir = 4;
  repeated DepotResult depots = 5;
}

message DepotResult {
  uint32 depot_id = 1;
  uint64 manifest_id = 2;
  uint64 size_bytes = 3;
}

message DownloadFailed {
  string error = 1;
  bool retryable = 2;
}

message CancelRequest {
  string download_id = 1;  // Empty = cancel all active downloads
}

message BenchmarkEvent {
  oneof event {
    BenchmarkProgress progress = 1;
    BenchmarkResult result = 2;
    BenchmarkError error = 3;
  }
}

message BenchmarkProgress {
  uint32 test_index = 1;
  string test_name = 2;
  uint32 elapsed_secs = 3;
  double speed_bytes_per_sec = 4;
  uint64 bytes_downloaded = 5;
}

message BenchmarkResult {
  double best_speed_bytes_per_sec = 1;
  uint32 recommended_chunks = 2;
}

message BenchmarkError {
  string error = 1;
}
```

### 7.2 Rust Internal Architecture

**State maintained by the sidecar:**

```
┌─────────────────────────────────────────┐
│              SessionManager              │
│                                         │
│  client: Mutex<Option<SteamClient<LoggedIn>>>
│  state:  Mutex<SessionState>             │
│  tokens: Mutex<Option<AuthTokens>>      │
│  active_auth: Mutex<Option<AuthFlow>>   │
│                                         │
│  Methods:                               │
│  • ensure_connected() → &SteamClient<>  │
│  • begin_anonymous()                    │
│  • begin_token(name, token)            │
│  • begin_credentials(name, pass)       │
│  • begin_qr()                           │
│  • submit_guard_code(code)             │
│  • logout()                             │
└──────────────┬──────────────────────────┘
               │
┌──────────────▼──────────────────────────┐
│           DownloadOrchestrator           │
│                                          │
│  active_downloads: HashMap<Id, CancelTx> │
│                                          │
│  Methods:                                │
│  • download_app(req) → Stream<Event>    │
│    1. PICSGetProductInfo(app_id)        │
│    2. GetDepotDecryptionKey(depots)     │
│    3. ContentServerDirectory → servers  │
│    4. GetManifestRequestCode(depots)    │
│    5. DownloadManifest(depot) per depot │
│    6. For each depot: DepotJob.download │
│    7. Forward DownloadEvent to gRPC     │
│                                          │
│  • download_workshop(req) → Stream      │
│    Same as above + PublishedFile lookup │
│                                          │
│  • cancel(id) → bool                    │
│    Drops cancel token for download      │
└──────────────────────────────────────────┘
```

**Cancel mechanism:**

Each active download gets a `tokio::sync::watch` channel. When `.cancel()` is called, the watch sender is dropped, which causes the download loop to detect cancellation and abort. The gRPC stream is terminated with `Status::cancelled`.

```rust
// In download.rs
pub struct ActiveDownload {
    pub cancel_tx: tokio::sync::watch::Sender<bool>,
    pub task: tokio::task::JoinHandle<()>,
}
```

**Recovery on sidecar restart:**

The sidecar is stateless except for the Steam refresh token, which is persisted to disk at `~/.caster/steam-token.json`. On startup:

1. Load saved refresh token from disk
2. If token exists → attempt `LoginBuilder::with_refresh_token()`
3. If token expired or invalid → fall back to disconnected state
4. .NET API can then trigger re-auth via QR or credentials gRPC call

### 7.3 Error Mapping (Rust → gRPC)

```
steamroom error                        → tonic::Status
────────────────────────────────────────────────────────────
LoginError::NoCmServers               → Unavailable("No Steam CM servers reachable")
LoginError::LogonFailed(eresult)      → PermissionDenied("Steam login failed: {eresult}")
LoginError::Transport(e)              → Unavailable("Steam connection lost: {e}")
SteamError::CdnStatus(429, ra)        → ResourceExhausted("CDN rate limited")
SteamError::CdnStatus(503, ra)        → Unavailable("CDN temporarily unavailable")
SteamError::Io(e)                     → Internal("File system error: {e}")
tokio::sync::watch::error::RecvError  → Cancelled("Download cancelled by user")  (→ Aborted in gRPC)
std::io::Error (disk full)            → ResourceExhausted("Disk full at {path}")
```

### 7.4 Dockerfile (Rust sidecar)

```dockerfile
# Stage 1: Build
FROM rust:1.88-alpine AS builder
RUN apk add --no-cache musl-dev protobuf-dev
WORKDIR /build
COPY Cargo.toml Cargo.lock ./
COPY build.rs .
COPY proto/ proto/
COPY src/ src/
RUN cargo build --release --locked

# Stage 2: Runtime
FROM alpine:3.21
RUN apk add --no-cache ca-certificates
COPY --from=builder /build/target/release/cast-downloader /usr/local/bin/
RUN mkdir -p /root/.caster /downloads
VOLUME /downloads
EXPOSE 50051
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD ["cast-downloader", "health"]
ENTRYPOINT ["cast-downloader"]
```

---

## 8. Frontend — Next.js

### 8.1 Page Routing

```
/                       Dashboard (home)
/login                  Login page (OIDC redirect + local login)
/setup                  First-time admin account creation
/servers                Server instance list
/servers/new            Create new server (tabbed form)
/servers/[id]           Edit existing server (tabbed form)
/servers/[id]/monitor   Real-time server monitoring view
/mods                   Mod management (table/card toggle)
/installations          Shared Arma 3 installations
/monitoring             Host-level monitoring dashboard
/settings               Application settings (accounts, steam, api-keys, service)
/settings/accounts      User management
/settings/steam         Steam account management
/settings/api-keys      API key management
/settings/audit-log     Audit log viewer
/about                  Version + system info
```

### 8.2 Key Components Per Page

**Dashboard (`/`)**
```
ServerStatusCard        Shows instance name, status, player count, uptime
ModUpdatesIndicator     Count of outdated mods, "Update All" button
RecentActivityList      Last N audit log entries
HostResourceWidget      CPU, RAM, Disk gauges (mini)
SteamConnectionBadge    Connected/Disconnected with account name
QuickActions            "New Server", "Add Mod", "Check Updates" buttons
FirstRunWizard          Only shown when no servers/mods exist
```

**Server Editor (`/servers/[id]`)**
```
ServerEditorLayout      Tab bar + content area
  GeneralTab            Name, ports, DLC toggles, headless clients, restart policy
  ConfigTab             server.cfg editor (Monaco Editor with syntax highlighting)
  BasicConfigTab        basic.cfg editor
  DifficultyTab         Arma3Profile difficulty editor
  ModsTab               Drag-and-drop mod assignment, load order, client/server flags
  MissionsTab           Mission file browser, upload, tagging
  InstallTab            Installation status, "Install/Reinstall" button,
                        MudStepper replacement: our own StepProgress component
  MonitorTab            Real-time console output, runtime events, CPU/memory chart
Share across tabs:
  UnsavedChangesGuard   Browser confirm() on navigation if form dirty
  ServerActionBar       Save, Save & Launch, Stop, Restart buttons
```

**Mods Page (`/mods`)**
```
ModDataTable            Sortable, filterable table with:
                        - Status badge (installed/downloading/outdated/error)
                        - Name, author, size, last updated
                        - Actions: Download, Update, Check Update, Delete
ModCardView             Alternative card view with thumbnails
ModDetailDialog         Full mod details (thumbnail, author, workshop link, manifests)
AddModDialog            Paste workshop URL or ID
ImportPresetDialog      Upload Arma 3 Launcher .html preset
ImportLocalDialog       Specify name + folder path
ModFilterBar            Filter by status, source, search by name
DownloadQueuePanel      Active + queued downloads with progress bars
BulkActions             Multi-select: Delete, Update Selected
```

**Monitoring (`/monitoring`)**
```
HostMetricsChart        CPU %, RAM GB, Disk I/O (Recharts area/line charts)
                          - Live updating (every 5 seconds via SSE)
                          - Time range picker: 5m, 15m, 1h, 6h, 24h
InstanceMetricsCards    One card per running server:
                        - CPU %, Memory MB, Players, Uptime
                        - Click to navigate to server monitor
```

**Settings (`/settings`)**
```
GeneralSettingsTab      Mods directory, servers directory, parallel downloads
AccountsTab             User list, add user, change role, disable user
                        (only visible to admins)
SteamTab                Steam sign-in (QR code display, credential form),
                        connection status, avatar + persona name
ApiKeysTab              Create/revoke API keys, show last used
ServiceTab              Start/stop Windows service (Windows only),
                        configure auto-start, recovery options
DiagnosticsTab          Crash reports list, system info, download benchmark
ThemeTab                Accent color picker, dark/light mode toggle
```

### 8.3 State Architecture

```
                    ┌─────────────────────────────┐
                    │       TanStack Query         │
                    │  (server state cache)        │
                    │                              │
                    │  useQuery('servers', fetch)  │
                    │  useMutation('server.create')│
                    │  useQuery('mods', fetch, {   │
                    │    filters: status, source   │
                    │  })                          │
                    └──────────┬───────────────────┘
                               │ invalidates on mutation
                    ┌──────────▼───────────────────┐
                    │        Zustand Stores         │
                    │  (client-only UI state)       │
                    │                               │
                    │  useThemeStore()              │
                    │  useSidebarStore()            │
                    │  useDownloadProgressStore()   │
                    │    SSSE → store update        │
                    │  useServerStatusStore()       │
                    │    SSE → store update          │
                    │  useSteamAuthStore()          │
                    │    SSE → store update          │
                    └───────────────────────────────┘
```

TanStack Query handles all CRUD data (servers, mods, installations, settings, audit log). It provides:
- Automatic cache invalidation after mutations
- Background refetch on window focus
- Optimistic updates for toggle actions
- Pagination/infinite scroll for large lists

Zustand stores handle real-time data pushed via SSE:
- `useDownloadProgressStore` — active download phase, percent, current file
- `useServerStatusStore` — per-server running/stopped/crashed, PID, uptime
- `useSteamAuthStore` — Steam connected/disconnected, account name, avatar
- `useMonitoringStore` — host CPU/RAM/Disk, instance metrics time series

### 8.4 Data Fetching Example

```typescript
// hooks/use-servers.ts
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';

export function useServers() {
  return useQuery({
    queryKey: ['servers'],
    queryFn: () => api.get('/api/servers'),
  });
}

export function useCreateServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (data: CreateServerDto) => api.post('/api/servers', data),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['servers'] }),
  });
}

export function useStartServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.post(`/api/servers/${id}/start`),
    onSuccess: (_, id) => {
      queryClient.invalidateQueries({ queryKey: ['servers'] });
      queryClient.invalidateQueries({ queryKey: ['servers', id] });
    },
  });
}
```

### 8.5 SSE Hook

```typescript
// hooks/use-sse.ts
import { useEffect, useCallback } from 'react';

type SseTopic = 'monitoring' | 'downloads' | 'steam' | 'status';

export function useSse(topics: SseTopic[], onEvent: (event: SseEvent) => void) {
  useEffect(() => {
    const topicStr = topics.join(',');
    const url = `/api/sse/events?topics=${topicStr}`;
    const es = new EventSource(url);

    for (const topic of topics) {
      es.addEventListener(topic, (e: MessageEvent) => {
        const data = JSON.parse(e.data);
        onEvent({ topic, data });
      });
    }

    es.onerror = () => {
      // EventSource auto-reconnects. Log if desired.
    };

    return () => es.close();
  }, [topics.join(','), onEvent]);
}
```

Connected to a Zustand store:

```typescript
// stores/download-progress-store.ts
import { create } from 'zustand';

interface DownloadState {
  tasks: Map<string, DownloadTask>;
  updateTask: (task: DownloadTask) => void;
  removeTask: (id: string) => void;
}

export const useDownloadProgressStore = create<DownloadState>((set) => ({
  tasks: new Map(),
  updateTask: (task) => set((s) => {
    const next = new Map(s.tasks);
    next.set(task.id, task);
    return { tasks: next };
  }),
  removeTask: (id) => set((s) => {
    const next = new Map(s.tasks);
    next.delete(id);
    return { tasks: next };
  }),
}));
```

---

## 9. Frontend ↔ Backend ↔ Sidecar: End-to-End Data Flow

### 9.1 Example: User Downloads a Workshop Mod

This is the most complex flow, touching all three tiers. We'll trace it step by step.

**Step 1: User adds a mod**
```
Browser (AddModDialog)
  │  User pastes "https://steamcommunity.com/sharedfiles/filedetails/?id=450814997"
  │
  ├── POST /api/mods { workshopId: 450814997 }
  │
  ▼
cast-api (ModsModule.CreateMod)
  │  1. Call Steam Web API: GET /ISteamRemoteStorage/GetPublishedFileDetails/v1/
  │     → Get title, author, size, preview URL, manifest_id
  │     (This is HTTP REST, NOT SteamKit2. Zero Steam protocol dependency.)
  │  2. INSERT INTO mods (...) VALUES (...)
  │  3. Return { id: "uuid", title: "CBA_A3", ... }
  ▼
Browser (ModsPage)
  │  TanStack Query invalidates ['mods'], mod appears in list with "Not Installed" badge
```

**Step 2: User clicks "Download"**
```
Browser (ModDataTable row action)
  │
  ├── POST /api/mods/{id}/download
  │
  ▼
cast-api (ModsModule.QueueDownload)
  │  1. UPDATE mods SET status = 'queued'
  │  2. INSERT INTO download_tasks (task_type='mod_download', target_id=modId, status='queued')
  │  3. Signal ModDownloadQueueService (via Channel<bool>)
  │  4. Return { taskId: "uuid", status: "queued" }
  │
  ▼
cast-api (ModDownloadQueueService background loop)
  │  1. Poll download_tasks WHERE status = 'queued' ORDER BY queued_at LIMIT parallel_mod_downloads
  │  2. For each task:
  │     a. UPDATE task SET status = 'running', started_at = now()
  │     b. UPDATE mods SET status = 'downloading'
  │     c. Broadcast SSE: { topic: 'downloads', event: 'queued', taskId }
  │     d. Call ContentOrchestrator.InstallModAsync(task)
  │
  ▼
cast-api (ContentOrchestrator)
  │  1. Acquire queue slot (SemaphoreSlim, max = parallel_mod_downloads)
  │  2. Call SteamModInstaller.InstallAsync(mod, installDir, progress, ct)
  │
  ▼
cast-api (SteamModInstaller)
  │  1. Call downloader.DownloadWorkshopItemAsync(workshopId, installDir, ct)
  │     → gRPC to cast-downloader
  │
  ▼
cast-downloader (DownloadService::download_workshop_item)
  │  1. session.ensure_connected() → anonymous or token login if needed
  │  2. Create (event_tx, event_rx) = mpsc::unbounded_channel()
  │  3. Spawn download task:
  │     a. Get CDN servers via ContentServerDirectory
  │     b. Get workshop item details → depot_id, manifest_id
  │     c. Get depot decryption key
  │     d. Get manifest request code
  │     e. Download manifest
  │     f. Create DepotJob with event_tx
  │     g. job.download(manifest, CdnChunkFetcher).await
  │  4. Forward events from event_rx → gRPC stream:
  │     while let Some(event) = event_rx.recv().await {
  │         tx.send(map_to_proto(event)).await?;
  │     }
  │
  ▼ gRPC stream (server → client)
  │  DownloadEvent { phase: "connecting" }
  │  DownloadEvent { phase: "authenticating" }
  │  DownloadEvent { phase: "fetching_manifests" }
  │  DownloadEvent { phase: "downloading" }
  │  DownloadEvent { depot_progress: { completed: 1048576, total: 52428800 } }
  │  DownloadEvent { file_started: { filename: "addons/cba_main.pbo", size: 1234567 } }
  │  DownloadEvent { file_completed: { filename: "addons/cba_main.pbo" } }
  │  ... (more files)
  │  DownloadEvent { phase: "validating" }
  │  DownloadEvent { completed: { total_bytes: 52428800, total_files: 15 } }
  │
  ▼ (stream ends, back in cast-api)
cast-api (SteamModInstaller)
  │  1. Calculate size_on_disk (walk install dir)
  │  2. Run post-download: set up symlinks for instance mod directories
  │  3. UPDATE mods SET status = 'installed', manifest_id = X, installed_at = now()
  │  4. UPDATE download_tasks SET status = 'completed', completed_at = now()
  │
  ▼ Broadcast via SSE to all connected browsers:
cast-api (SseConnectionManager)
  │  For each connected EventSource:
  │    Send: event: downloads
  │          data: {"taskId":"uuid","status":"completed","modId":"uuid"}
  │
  ▼
Browser (useDownloadProgressStore via useSse)
  │  Zustand store updates → task removed from active list
  │  TanStack Query auto-refetches ['mods'] → mod shows "Installed" badge
```

### 9.2 Example: User Installs Arma 3 Server

```
Browser (InstallTab, ServerEditor)
  │
  ├── POST /api/installations/{id}/install
  │
  ▼
cast-api (InstallationsModule.InstallServer)
  │  1. Verify installation exists, not already installing
  │  2. Create download_task (task_type='app_install')
  │  3. Call ServerInstaller.InstallAsync(installation)
  │
  ▼
cast-api (ServerInstaller)
  │  1. Call downloader.DownloadAppAsync(appId=233780, branch='public', ...)
  │     → gRPC streaming call
  │
  ▼
cast-downloader (DownloadService::download_app)
  │  1. PICSGetProductInfo(233780) → find depots for branch 'public'
  │     Returns: { 228990: "Arma 3 Server", 228991: "Arma 3 Server (Windows)", ... }
  │  2. Filter depots by os_filter (empty = all for Linux; "windows" for Windows)
  │  3. For each depot: GetDepotDecryptionKey()
  │  4. ContentServerDirectory → CDN server pool
  │  5. For each depot:
  │     a. GetManifestRequestCode(depot_id, app_id, manifest_gid)
  │     b. DownloadManifest(depot_id, manifest_gid, request_code, server, key)
  │     c. DepotJob::builder()
  │          .depot_id(depot_id)
  │          .depot_key(key)
  │          .install_dir(req.install_dir)
  │          .verify(req.verify)
  │          .event_sender(tx.clone())
  │          .build()
  │     d. job.download(&manifest, fetcher).await
  │  6. Stream events back (same pattern as workshop)
  │
  ▼ Back in cast-api:
  │  1. Mark installation.install_status = 'installed'
  │  2. Store manifest IDs in installation.installed_manifest_ids JSONB
  │  3. Broadcast SSE completion event
```

### 9.3 Example: Real-Time Monitoring

```
cast-api (MetricsCollectorService — BackgroundService, every 5 seconds)
  │
  ├── 1. Collect host metrics (System.Diagnostics.Process)
  │      - Total CPU %, per-core
  │      - Total RAM, available RAM
  │      - Disk I/O (read/write bytes since last sample)
  │
  ├── 2. For each running server instance:
  │      - Get process CPU % and working set
  │      - If process died unexpectedly → mark status 'crashed'
  │      - Read latest console output from tail buffer
  │
  ├── 3. INSERT INTO monitoring_snapshots (...)
  │
  ├── 4. Broadcast SSE to all connected clients:
  │      event: monitoring
  │      data: {
  │        "timestamp": "2026-06-14T12:00:05Z",
  │        "host": { "cpu": 23.5, "ram_used": 8589934592, "ram_total": 17179869184 },
  │        "instances": [
  │          { "id": "uuid", "name": "Main Server", "status": "running",
  │            "cpu": 12.3, "ram_mb": 2048, "players": 32, "uptime_secs": 3600 }
  │        ]
  │      }
  │
  ├── 5. ProcessWatchdogService (every 10 seconds, separate service)
  │      - Check each running instance's PID
  │      - If process died and restart_policy = 'on_crash':
  │        - Increment crash counter
  │        - If < max_restarts: restart server
  │        - Else: mark status 'crashed', create audit log entry
  │
  └── 6. SchedulingService (every 60 seconds, separate service)
         - Check each server's auto_start_time / auto_stop_time
         - If schedule_enabled and current time matches:
           - auto_start_time reached → start server (if stopped)
           - auto_stop_time reached → stop server (if running)
         - Broadcast SSE status changes
```

---

## 10. Steam Authentication Flow

### 10.1 Architecture Decision: Where Auth Lives

Steam authentication requires long-lived TCP/WebSocket connections to Steam's CM (Connection Manager) servers. These connections carry encrypted binary protobuf messages. This protocol knowledge lives entirely in `steamroom`, inside the Rust sidecar.

The .NET API does NOT speak Steam protocol. It acts as a relay:

```
Browser ←→ cast-api (.NET) ←→ cast-downloader (Rust) ←→ Steam CM/CDN
         HTTP/SSE                  gRPC                       TCP/WS
```

### 10.2 Anonymous Auth (Auto for Downloads)

```
Trigger: Any download request when no Steam session exists

.NET (SteamModInstaller)
  │  Calls gRPC: DownloadWorkshopItem(workshopId, ...)
  │
  ▼
Rust (download_workshop_item handler)
  │  1. session.ensure_connected()
  │  2. Check: is there a saved refresh token?
  │     YES → try LoginBuilder::with_refresh_token()
  │     NO  → LoginBuilder::anonymous()
  │  3. If anonymous succeeds → proceed with download
  │  4. If anonymous fails → return error, let .NET retry or prompt user
  │
  ▼ gRPC stream emits:
  │  DownloadEvent { phase_changed: { phase: "anonymous_login" } }
  │  DownloadEvent { phase_changed: { phase: "downloading" } }
  │  ... download events ...
```

Anonymous sessions are valid for downloading free content (Arma 3 server files, Workshop mods). The user never sees this authentication step — it happens transparently.

### 10.3 QR Code Login (User-Interactive)

```
Trigger: User opens Settings → Steam tab, clicks "Sign in with QR Code"

Browser (SteamTab)
  │  Shows empty QR code placeholder
  │  Calls: POST /api/auth/steam/begin-qr
  │
  ▼
.NET (AuthModule.BeginQrAuth)
  │  Calls gRPC: BeginQr(Empty) → returns stream<AuthEvent>
  │
  ▼
Rust (begin_qr handler)
  │  1. LoginBuilder::new()
  │       .device_name("CASTER")
  │       .with_qr()
  │  2. qr_flow.begin().await
  │  3. → AuthEvent::QrChallenge { challenge_url: "https://s.team/q/..." }
  │     Stream this back via gRPC
  │  4. qr_flow.wait_for_approval().await
  │     Blocks until user scans with Steam Mobile app
  │     (This takes anywhere from 5 seconds to 5 minutes)
  │  5. → AuthEvent::LoggedIn { account_name, steam_id, refresh_token }
  │  6. Persist refresh token to ~/.caster/steam-token.json
  │  7. Update session.client to SteamClient<LoggedIn>
  │
  ▼ gRPC stream (to .NET):
  │  AuthEvent { qr_challenge: { challenge_url: "https://s.team/q/..." } }
  │        ^
  │        │
  ▼        │
.NET (AuthModule)     │
  │  Receives QrChallenge                │
  │  ┌─────────────────────────────────┘
  │  │  Broadcast SSE event:
  │  │    event: steam
  │  │    data: { type: "qr_challenge", url: "https://s.team/q/..." }
  │  │
  │  │  Update steam_auth_state: auth_state = 'in_progress', method = 'qr'
  │  │
  │  │  ... wait for more events from gRPC stream ...
  │  │
  ▼  │
Browser (useSteamAuthStore via useSse)
  │  Receives SSE event
  │  → Renders QR code from challenge_url (using qrcode.js or similar)
  │  → Shows "Waiting for scan..." status
  │
  │  ... User opens Steam Mobile App, scans QR code, confirms ...
  │
  ▼ (back in Rust)
  │  qr_flow.wait_for_approval() completes
  │  → AuthEvent::LoggedIn { account_name: "Hayami", steam_id: 7656119..., refresh_token: "..." }
  │
  ▼ gRPC stream continues:
  │  AuthEvent { logged_in: { account_name: "Hayami", steam_id: 7656119..., ... } }
  │
  ▼
.NET (AuthModule)
  │  Receives LoggedIn event
  │  → UPDATE steam_auth_state SET is_authenticated = TRUE, account_name = 'Hayami', ...
  │  → Broadcast SSE: event: steam, data: { type: "logged_in", account_name: "Hayami" }
  │  → Create audit log entry: "steam.connect"
  │
  ▼
Browser
  │  SSE updates Zustand store
  │  → SteamTab shows "Connected as Hayami" with avatar
  │  → Dashboard badge changes from "Disconnected" to "Hayami"
  │  → All authenticated downloads now use this session
```

### 10.4 Credential + Steam Guard Login

```
Trigger: User enters username + password in SteamTab, clicks "Sign in"

Browser
  │  POST /api/auth/steam/begin-credentials { account_name, password }
  │
  ▼
.NET (AuthModule)
  │  Calls gRPC: BeginCredentials(CredentialsAuthRequest { account_name, password })
  │
  ▼
Rust (begin_credentials handler)
  │  1. LoginBuilder::new()
  │       .device_name("CASTER")
  │       .with_credentials(account_name, password)
  │  2. credential_flow.begin().await
  │  3. May return GuardRequired:
  │     → AuthEvent::GuardRequired { guard_kind: "email_code", detail: "Code sent to x***@gmail.com" }
  │     OR
  │     → AuthEvent::GuardRequired { guard_kind: "device_confirmation" }
  │
  │  If GuardRequired:
  │    Stream AuthEvent::GuardRequired to .NET
  │    Block waiting for SubmitGuardCode(code)
  │    → .NET forwards code from browser
  │    → credential_flow.submit_code(code)
  │    → If accepted: continue to LoggedIn
  │    → If rejected: AuthEvent::GuardRequired again or AuthEvent::Failed
  │
  ▼ Flow completes as with QR login:
  │  AuthEvent::LoggedIn { ... } with refresh token
  │  Persist token, update session
  │
  ▼
.NET → SSE → Browser → Show "Connected as {name}"
```

**Why 2FA is a separate gRPC call:** The gRPC `BeginCredentials` stream is already open. When `GuardRequired` is emitted, the Rust handler pauses internally (waiting on a `tokio::sync::oneshot` channel). The .NET API receives the `GuardRequired` event via the open stream, prompts the browser, and when the user submits the code, makes a separate unary `SubmitGuardCode` gRPC call. The Rust handler receives this via the oneshot channel, submits it to steamroom, and continues the stream.

```
Rust internal flow:
  ┌──────────────────────────────────────────────────┐
  │ begin_credentials handler                        │
  │                                                  │
  │  let (code_tx, code_rx) = oneshot::channel();    │
  │  session.active_auth = Some(code_tx);            │
  │                                                  │
  │  stream AuthEvent::GuardRequired via gRPC        │
  │                                                  │
  │  let code = code_rx.await;  // blocks            │
  │                │                                 │
  │                │  ← SubmitGuardCode gRPC call    │
  │                │    looks up code_tx from        │
  │                │    session.active_auth          │
  │                │    sends the code               │
  │                ▼                                 │
  │  credential_flow.submit_code(code)               │
  │  → success: stream AuthEvent::LoggedIn           │
  │  → failure: stream AuthEvent::GuardRequired again│
  └──────────────────────────────────────────────────┘
```

### 10.5 Token Persistence & Auto-Reconnect

```
┌────────────────────────────────────────────┐
│  ~/.caster/steam-token.json                │
│  {                                         │
│    "account_name": "Hayami",               │
│    "refresh_token": "eyAidHlwIjoiSldU...", │
│    "steam_id": 76561198000000000,           │
│    "last_used": "2026-06-14T12:00:00Z"     │
│  }                                         │
└────────────────────────────────────────────┘
```

On sidecar startup:
1. Try to load `steam-token.json`
2. If found and not expired → attempt `LoginBuilder::with_refresh_token()`
3. If successful → session is "authenticated" without user interaction
4. If expired/invalid → session is "disconnected", user must re-auth

On sidecar CM disconnect (network drop, Steam maintenance):
1. steamroom detects disconnection
2. Session state → "disconnected"
3. Send SSE event to .NET: `{ type: "steam_disconnected" }`
4. Auto-reconnect: wait 5s, try `with_refresh_token()` again
5. If fails after 3 attempts → stay disconnected, SSE: `{ type: "steam_auth_expired" }`
6. User must explicitly re-auth (QR or credentials)

### 10.6 Complete Steam Auth State Machine

```
                    ┌─────────────┐
                    │ DISCONNECTED │ ←── Start state (no token / invalid token)
                    └──────┬───────┘
                           │
              ┌────────────┼────────────┐
              │            │            │
         begin_qr()  begin_creds()  begin_anon()
              │            │            │
     ┌────────▼───┐ ┌─────▼──────┐ ┌───▼────────┐
     │ QR_PENDING │ │ CRED_PENDING│ │ ANONYMOUS   │
     │ (url sent) │ │ (code sent) │ │ (no user)   │
     └─────┬──────┘ └─────┬──────┘ └───┬─────────┘
           │              │            │
     user scans      user enters        login
           │         guard code          done
           │              │            │
     ┌─────▼──────────────▼──────┐     │
     │   AUTHENTICATING          │◄────┘
     │  (completing logon)       │
     └──────────┬────────────────┘
                │
        ┌───────▼────────┐
        │ AUTHENTICATED  │ ←── User logged in, token persisted
        │ (Hayami, ID)   │
        └───┬────────┬───┘
            │        │
       CM disconnect  logout()
            │        │
    ┌───────▼──┐ ┌──▼──────────┐
    │RECONNECT │ │ DISCONNECTED │
    │ (retry)  │ │ (manual re-  │
    │          │ │  auth needed)│
    └────┬─────┘ └──────────────┘
         │
    retry success → AUTHENTICATED
    retry fail ×3 → DISCONNECTED (auth_expired)
```

---

## 11. Authentication & Authorization — OIDC + AD + RBAC

### 11.1 Web Application Authentication

CASTER supports three authentication methods for the web UI:

| Method        | Provider                                          | Use Case                                |
|---------------|---------------------------------------------------|-----------------------------------------|
| **OIDC**      | Keycloak, Authentik, Azure AD, Google Workspace  | Primary enterprise auth                 |
| **Windows AD**| Active Directory (Kerberos/NTLM)                  | On-prem Windows environments            |
| **Local**     | Password hashed with PBKDF2                      | First-run setup, fallback               |

**Authentication flow (OIDC):**

```
Browser                          cast-api                      Identity Provider
  │                                 │                               │
  ├── GET /login                    │                               │
  │   ← Redirect to OIDC provider   │                               │
  │                                 │                               │
  ├──→ /realms/caster/protocol/openid-connect/auth?client_id=...  ──┤
  │   ← Login form                  │                               │
  │   Submit credentials            │                               │
  │                                 │                               │
  ├──→ /auth/oidc/callback?code=X  ←── Redirect with auth code ────┤
  │                                 │                               │
  │                  cast-api:                                       │
  │                  1. Exchange code for tokens (id_token + access_token)
  │                  2. Validate id_token (issuer, audience, nonce)
  │                  3. Extract claims: sub, preferred_username, email, groups
  │                  4. Check: user in allowed OIDC group? ("KAST Admins" or configurable)
  │                  5. If first login → auto-provision user row
  │                  6. Issue session cookie (HttpOnly, Secure, SameSite=Lax)
  │                  7. Redirect to / (dashboard)
  │                                 │                               │
  ├── GET / (with session cookie)   │                               │
  │   ← Dashboard (authenticated)   │                               │
```

**OIDC configuration (appsettings.json):**

```json
{
  "Auth": {
    "Mode": "Oidc",
    "Oidc": {
      "Authority": "https://auth.3rdshockarmy.com/realms/caster",
      "ClientId": "caster-web",
      "ClientSecret": "${OIDC_CLIENT_SECRET}",
      "ResponseType": "code",
      "Scopes": ["openid", "profile", "email", "groups"],
      "AllowedGroups": ["CASTER Admins", "CASTER Operators"],
      "RoleClaimType": "groups",
      "AdminGroup": "CASTER Admins",
      "OperatorGroup": "CASTER Operators",
      "AutoProvision": true
    },
    "WindowsAd": {
      "Enabled": false,
      "Domain": "3RDSHOCKARMY.local",
      "AllowedGroups": ["Domain Admins", "KAST Operators"]
    }
  }
}
```

### 11.2 RBAC — Role-Based Access Control

| Role       | Permissions                                                            |
|------------|------------------------------------------------------------------------|
| **Admin**  | Full access: CRUD servers/mods/installations, manage users, settings, audit log, API keys, Steam auth |
| **Operator** | Start/stop servers, manage mods (download/update/delete), view monitoring, view settings, view audit log |
| **Viewer** | Read-only: view servers, mods, monitoring, audit log. No mutations. |

**Implementation:**

```csharp
// Cast.Api/Auth/Policies.cs
public static class Policies
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";
    public const string AdminOrOperator = "admin_or_operator";

    public static void AddCastAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Admin, p => p.RequireRole("admin"));
            options.AddPolicy(Operator, p => p.RequireRole("admin", "operator"));
            options.AddPolicy(Viewer, p => p.RequireRole("admin", "operator", "viewer"));
            options.AddPolicy(AdminOrOperator, p => p.RequireRole("admin", "operator"));
        });
    }
}

// Applying to endpoints:
group.MapPost("/api/servers", CreateServer)
    .RequireAuthorization(Policies.AdminOrOperator);

group.MapDelete("/api/servers/{id}", DeleteServer)
    .RequireAuthorization(Policies.Admin);

group.MapGet("/api/servers", ListServers)
    .RequireAuthorization(Policies.Viewer);
```

**Role mapping from OIDC claims:**

```csharp
public class RoleHandler
{
    public static string MapRole(ClaimsPrincipal principal, IConfiguration config)
    {
        var groups = principal.FindAll("groups")
            .Select(c => c.Value).ToList();

        var adminGroup = config["Auth:Oidc:AdminGroup"] ?? "CASTER Admins";
        var operatorGroup = config["Auth:Oidc:OperatorGroup"] ?? "CASTER Operators";

        if (groups.Contains(adminGroup)) return "admin";
        if (groups.Contains(operatorGroup)) return "operator";
        return "viewer";
    }
}
```

### 11.3 API Key Authentication

Same as KAST v1 but stored in PostgreSQL instead of SQLite:

```
Authorization: Bearer kast_<base64_32_bytes>

Lookup: SELECT * FROM api_keys WHERE key_prefix = 'kast_XXXX' AND is_revoked = FALSE
Verify:  PBKDF2.Verify(full_key, stored_hash)
Scope:  Full access (same as the creating user's role)
```

---

## 12. Real-Time Updates — SSE

### 12.1 Why SSE Instead of SignalR/WebSocket

| Factor              | SignalR (v1)           | SSE (v2)                   |
|---------------------|------------------------|----------------------------|
| Transport           | WebSocket (with fallback) | HTTP/1.1 long-lived     |
| Browser support     | Requires JS library    | Native `EventSource` API   |
| Reconnection        | Built-in               | Built-in (browser)         |
| Sticky sessions     | Required               | Not required               |
| Load balancer       | Must support WS        | Standard HTTP proxy        |
| Binary data         | Yes                    | No (text only, fine for us)|
| Backend complexity  | Hub classes, groups    | Channel + middleware       |
| Direction           | Bidirectional          | Server→Client only         |

For CASTER's use case (server-to-client push of monitoring, progress, and status), SSE is the correct choice. Client-to-server communication (API calls) uses standard REST fetch.

### 12.2 SSE Backend Implementation

```csharp
// Cast.Infrastructure/Sse/SseConnectionManager.cs
public class SseConnectionManager
{
    private readonly ConcurrentDictionary<string, SseClient> _clients = new();

    public string AddClient(HttpContext context, string[] topics)
    {
        var clientId = Guid.NewGuid().ToString();
        var client = new SseClient
        {
            Id = clientId,
            Context = context,
            Topics = new HashSet<string>(topics),
            ConnectedAt = DateTimeOffset.UtcNow
        };
        _clients.TryAdd(clientId, client);
        return clientId;
    }

    public void RemoveClient(string clientId)
    {
        _clients.TryRemove(clientId, out _);
    }

    public async Task BroadcastAsync(string topic, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var deadClients = new List<string>();

        foreach (var (id, client) in _clients)
        {
            if (!client.Topics.Contains(topic)) continue;

            try
            {
                await client.Context.Response.WriteAsync(
                    $"event: {topic}\ndata: {json}\n\n");
                await client.Context.Response.Body.FlushAsync();
            }
            catch
            {
                deadClients.Add(id);
            }
        }

        foreach (var id in deadClients) RemoveClient(id);
    }
}

// SSE endpoint module:
// GET /api/sse/events?topics=monitoring,downloads,steam,status
group.MapGet("/api/sse/events", async (HttpContext context, SseConnectionManager mgr) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");
    context.Response.Headers.Append("X-Accel-Buffering", "no");  // Disable nginx buffering

    var topics = context.Request.Query["topics"].ToString()
        .Split(',', StringSplitOptions.RemoveEmptyEntries);

    var clientId = mgr.AddClient(context, topics);

    // Send initial heartbeat
    await context.Response.WriteAsync(": heartbeat\n\n");
    await context.Response.Body.FlushAsync();

    // Keep connection alive
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
    try
    {
        while (await timer.WaitForNextTickAsync(context.RequestAborted))
        {
            await context.Response.WriteAsync(": heartbeat\n\n");
            await context.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        mgr.RemoveClient(clientId);
    }
});
```

### 12.3 SSE Topics

| Topic        | Emitted By                    | Event Shape                                                | Frequency     |
|--------------|-------------------------------|------------------------------------------------------------|---------------|
| `monitoring` | MetricsCollectorService       | `{ host: {...}, instances: [...] }`                       | Every 5 sec   |
| `downloads`  | ModDownloadQueueService       | `{ taskId, modId, type, status, phase, pct, bytes, file }`| On change     |
| `steam`      | AuthModule (relay from gRPC)  | `{ type: "qr_challenge" / "logged_in" / "logged_out" / "expired" }` | On change |
| `status`     | ProcessWatchdogService        | `{ instanceId, oldStatus, newStatus, pid }`               | On change     |
| `audit`      | AuditMiddleware               | `{ id, action, resource, actor, timestamp }`              | On new entry  |
| `console`    | ServerConsoleLogTailer        | `{ instanceId, line, level, timestamp }`                  | Per line      |

### 12.4 nginx SSE Configuration

```nginx
# nginx.conf — Critical for SSE
location /api/sse/ {
    proxy_pass http://cast-api:8080;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

    # Disable buffering — SSE events must be delivered immediately
    proxy_buffering off;
    proxy_cache off;

    # Long-lived connection (24 hours)
    proxy_read_timeout 86400s;
    proxy_send_timeout 86400s;
}
```

**Key:** `proxy_buffering off` is essential. Without it, nginx buffers SSE events and delivers them in chunks, causing multi-second latency.

---

## 13. Enterprise Features — Audit Logging & Prometheus Metrics

### 13.1 Audit Logging

**What is audited:** Every mutating API call (create, update, delete, start, stop, download queue actions, settings changes, user management, Steam auth).

**How:**

```csharp
// Cast.Infrastructure/Audit/AuditEndpointFilter.cs
public class AuditEndpointFilter : IEndpointFilter
{
    private readonly string _resourceType;
    private readonly string _action;

    public AuditEndpointFilter(string resourceType, string action)
    {
        _resourceType = resourceType;
        _action = action;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var actor = httpContext.User;

        // Capture old state for updates/deletes
        object? oldValues = null;
        if (_action is "update" or "delete")
        {
            var id = httpContext.GetRouteValue("id") as string;
            if (id != null)
            {
                oldValues = await CaptureCurrentState(_resourceType, id);
            }
        }

        // Execute the actual endpoint
        var result = await next(context);

        // Extract response status
        var status = result is INestedHttpResult { Result: IStatusCodeHttpResult s }
            ? (s.StatusCode >= 200 && s.StatusCode < 300 ? "success" : "failure")
            : "success";

        // Write audit log
        await using var scope = httpContext.RequestServices
            .CreateAsyncScope();
        var auditService = scope.ServiceProvider
            .GetRequiredService<IAuditService>();

        await auditService.LogAsync(new AuditEntry
        {
            ActorId = actor.FindFirstValue(ClaimTypes.NameIdentifier),
            ActorName = actor.FindFirstValue("preferred_username") ?? actor.Identity?.Name,
            Action = $"{_resourceType}.{_action}",
            ResourceType = _resourceType,
            ResourceId = httpContext.GetRouteValue("id") as string,
            OldValues = oldValues,
            NewValues = CaptureRequestBody(context),
            IpAddress = httpContext.Connection.RemoteIpAddress,
            UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
            Status = status
        });

        return result;
    }
}

// Usage (extension method):
public static TBuilder WithAuditLog<TBuilder>(
    this TBuilder builder, string resourceType, string action)
    where TBuilder : IEndpointConventionBuilder
{
    builder.AddEndpointFilter(new AuditEndpointFilter(resourceType, action));
    return builder;
}
```

**Performance note:** Audit log writes are fire-and-forget (we don't block the response on them). A `Channel<AuditEntry>` + background `AuditWriterService` batches writes to PostgreSQL.

**Retention:** Audit logs are never auto-deleted. An admin can manually purge old entries via the Settings page.

### 13.2 Prometheus Metrics

**Endpoint:** `GET /metrics` (Prometheus text format)

```csharp
// Cast.Infrastructure/Metrics/CastMetrics.cs
public static class CastMetrics
{
    // Counters
    public static readonly Counter<int> ServerStarts = Metrics
        .CreateCounter("caster_server_starts_total", "Total server starts");

    public static readonly Counter<int> ServerCrashes = Metrics
        .CreateCounter("caster_server_crashes_total", "Total server crashes");

    public static readonly Counter<int> ModDownloads = Metrics
        .CreateCounter("caster_mod_downloads_total", "Total mod downloads");

    public static readonly Counter<int> ApiRequests = Metrics
        .CreateCounter("caster_api_requests_total", "Total API requests",
            new CounterConfig { LabelNames = ["method", "endpoint", "status_code"] });

    public static readonly Counter<int> AuditEntries = Metrics
        .CreateCounter("caster_audit_entries_total", "Total audit log entries");

    // Gauges
    public static readonly Gauge<int> ActiveServers = Metrics
        .CreateGauge("caster_active_servers", "Currently running server instances");

    public static readonly Gauge<int> ActiveDownloads = Metrics
        .CreateGauge("caster_active_downloads", "Currently active downloads");

    public static readonly Gauge<int> QueuedDownloads = Metrics
        .CreateGauge("caster_queued_downloads", "Queued downloads waiting");

    public static readonly Gauge<double> HostCpuPercent = Metrics
        .CreateGauge("caster_host_cpu_percent", "Host CPU usage %");

    public static readonly Gauge<long> HostMemoryBytes = Metrics
        .CreateGauge("caster_host_memory_bytes", "Host memory usage",
            new GaugeConfig { LabelNames = ["type"] });  // type = "used" or "total"

    public static readonly Gauge<int> DbConnections = Metrics
        .CreateGauge("caster_db_connections", "Active database connections");

    // Histograms
    public static readonly Histogram<double> ApiLatency = Metrics
        .CreateHistogram("caster_api_latency_seconds", "API request latency",
            new HistogramConfig
            {
                LabelNames = ["method", "endpoint"],
                Buckets = [0.01, 0.05, 0.1, 0.5, 1, 5, 10]
            });

    public static readonly Histogram<double> DownloadSpeed = Metrics
        .CreateHistogram("caster_download_speed_bytes_per_sec",
            "Download speed in bytes/second",
            new HistogramConfig
            {
                Buckets = [
                    1024 * 1024,        // 1 MB/s
                    5 * 1024 * 1024,    // 5 MB/s
                    10 * 1024 * 1024,   // 10 MB/s
                    25 * 1024 * 1024,   // 25 MB/s
                    50 * 1024 * 1024,   // 50 MB/s
                    100 * 1024 * 1024   // 100 MB/s
                ]
            });
}
```

**Registration in Program.cs:**
```csharp
app.MapGet("/metrics", async () =>
{
    var registry = Metrics.DefaultRegistry;
    using var stream = new MemoryStream();
    await registry.CollectAndExportAsTextAsync(stream);
    stream.Position = 0;
    var text = Encoding.UTF8.GetString(stream.ToArray());
    return Results.Text(text, "text/plain; version=0.0.4");
}).RequireAuthorization(Policies.Admin);
```

**Prometheus scrape config:**
```yaml
# docker/prometheus/prometheus.yml
global:
  scrape_interval: 15s

scrape_configs:
  - job_name: 'caster'
    static_configs:
      - targets: ['cast-api:8080']
    metrics_path: '/metrics'
```

---

## 14. Deployment — Docker Compose

### 14.1 Complete docker-compose.yml

```yaml
services:
  # ── Database ─────────────────────────────────────────
  postgres:
    image: postgres:17-alpine
    container_name: cast-postgres
    environment:
      POSTGRES_DB: caster
      POSTGRES_USER: caster
      POSTGRES_PASSWORD: ${DB_PASSWORD}
      POSTGRES_INITDB_ARGS: "--data-checksums"
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./docker/postgres/init.sql:/docker-entrypoint-initdb.d/init.sql:ro
    ports:
      - "5432:5432"  # Expose in dev, remove in production
    restart: unless-stopped
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U caster -d caster"]
      interval: 10s
      timeout: 5s
      retries: 5

  # ── API Backend ──────────────────────────────────────
  cast-api:
    build:
      context: ./src/backend
      dockerfile: Dockerfile
    container_name: cast-api
    depends_on:
      postgres:
        condition: service_healthy
      cast-downloader:
        condition: service_started
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: http://+:8080
      ConnectionStrings__Default: "Host=postgres;Port=5432;Database=caster;Username=caster;Password=${DB_PASSWORD}"
      Downloader__GrpcEndpoint: "http://cast-downloader:50051"
      Auth__Oidc__ClientSecret: ${OIDC_CLIENT_SECRET}
      Auth__Oidc__Authority: ${OIDC_AUTHORITY:-https://auth.example.com/realms/caster}
      Auth__Oidc__ClientId: ${OIDC_CLIENT_ID:-caster-web}
      Telemetry__Enabled: "${TELEMETRY_ENABLED:-false}"
    volumes:
      - cast-data:/app/data
      - cast-mods:/app/mods
      - cast-servers:/app/servers
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-sf", "http://localhost:8080/alive"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 15s

  # ── Download Sidecar ─────────────────────────────────
  cast-downloader:
    build:
      context: ./src/downloader
      dockerfile: Dockerfile
    container_name: cast-downloader
    environment:
      RUST_LOG: "info,cast_downloader=debug"
    volumes:
      - cast-data:/app/data
      - cast-mods:/app/mods
      - cast-servers:/app/servers
      - downloader-cache:/root/.caster
    ports:
      - "50051:50051"   # Expose in dev, remove in production
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "cast-downloader", "health"]
      interval: 30s
      timeout: 5s
      retries: 3

  # ── Frontend ─────────────────────────────────────────
  cast-ui:
    build:
      context: ./src/frontend
      dockerfile: Dockerfile
    container_name: cast-ui
    depends_on:
      cast-api:
        condition: service_healthy
    environment:
      API_BASE_URL: "http://cast-api:8080"
    ports:
      - "3000:3000"  # Expose in dev, remove in production
    restart: unless-stopped

  # ── Reverse Proxy ────────────────────────────────────
  nginx:
    image: nginx:alpine
    container_name: cast-nginx
    ports:
      - "80:80"
      - "443:443"  # If using TLS termination at nginx
    volumes:
      - ./docker/nginx.conf:/etc/nginx/nginx.conf:ro
    depends_on:
      cast-ui:
        condition: service_started
      cast-api:
        condition: service_healthy
    restart: unless-stopped

  # ── Metrics Stack (Optional) ─────────────────────────
  prometheus:
    image: prom/prometheus:latest
    container_name: cast-prometheus
    volumes:
      - ./docker/prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - prometheus-data:/prometheus
    command:
      - '--config.file=/etc/prometheus/prometheus.yml'
      - '--storage.tsdb.path=/prometheus'
      - '--web.console.libraries=/usr/share/prometheus/console_libraries'
      - '--web.console.templates=/usr/share/prometheus/consoles'
    ports:
      - "9090:9090"
    restart: unless-stopped

  grafana:
    image: grafana/grafana:latest
    container_name: cast-grafana
    environment:
      GF_SECURITY_ADMIN_PASSWORD: ${GRAFANA_PASSWORD:-admin}
    volumes:
      - grafana-data:/var/lib/grafana
    ports:
      - "3001:3000"
    depends_on:
      - prometheus
    restart: unless-stopped

volumes:
  pgdata:
  cast-data:
  cast-mods:
  cast-servers:
  downloader-cache:
  prometheus-data:
  grafana-data:
```

### 14.2 nginx Configuration (v2)

```nginx
# docker/nginx.conf
events {
    worker_connections 1024;
}

http {
    upstream cast-ui {
        server cast-ui:3000;
    }

    upstream cast-api {
        server cast-api:8080;
    }

    server {
        listen 80;
        server_name _;

        # Next.js frontend
        location / {
            proxy_pass http://cast-ui;
            proxy_http_version 1.1;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
        }

        # .NET API (REST)
        location /api/ {
            proxy_pass http://cast-api;
            proxy_http_version 1.1;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
        }

        # SSE (Server-Sent Events) — no buffering
        location /api/sse/ {
            proxy_pass http://cast-api;
            proxy_http_version 1.1;
            proxy_set_header Connection "";
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

            proxy_buffering off;
            proxy_cache off;
            proxy_read_timeout 86400s;
            proxy_send_timeout 86400s;
        }

        # Prometheus metrics
        location /metrics {
            proxy_pass http://cast-api;
            proxy_http_version 1.1;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
        }
    }
}
```

### 14.3 .NET Backend Dockerfile

```dockerfile
# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG APP_VERSION=0.1.0-local
WORKDIR /src

COPY Cast.slnx .
COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY src/Cast.Core/Cast.Core.csproj src/Cast.Core/
COPY src/Cast.Infrastructure/Cast.Infrastructure.csproj src/Cast.Infrastructure/
COPY src/Cast.Api/Cast.Api.csproj src/Cast.Api/

RUN dotnet restore Cast.slnx

COPY src/ src/
WORKDIR /src/src/Cast.Api
RUN dotnet publish -c Release -o /app/publish --no-restore \
    /p:MinVerVersionOverride=$APP_VERSION

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Arma 3 server runtime deps (needed because cast-api spawns server processes)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        lib32gcc-s1 \
        lib32stdc++6 \
        libpam0g && \
    rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/data /app/mods /app/servers

COPY --from=build /app/publish .
COPY docker/pam/cast /etc/pam.d/cast

ENV ASPNETCORE_URLS=http://+:8080
ENV Kast__ModsDirectory=/app/mods
ENV Kast__ServersDirectory=/app/servers

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
  CMD curl -sf http://localhost:8080/alive || exit 1

ENTRYPOINT ["dotnet", "Cast.Api.dll"]
```

### 14.4 Frontend Dockerfile

```dockerfile
# Stage 1: Dependencies
FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --production

# Stage 2: Build
FROM node:22-alpine AS builder
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm run build

# Stage 3: Runtime
FROM node:22-alpine AS runner
WORKDIR /app
ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1

RUN addgroup --system --gid 1001 nodejs
RUN adduser --system --uid 1001 nextjs

COPY --from=builder /app/public ./public
COPY --from=builder --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/.next/static ./.next/static

USER nextjs
EXPOSE 3000
ENV PORT=3000
CMD ["node", "server.js"]
```

---

## 15. Migration Strategy — KAST v1 → CASTER v1

### 15.1 Migration Phases

**Phase 0: Setup CASTER scaffolding (Week 1)**
- Create repository structure
- Set up PostgreSQL with initial schema
- Scaffold .NET solution with vertical slice modules (empty endpoints)
- Scaffold Next.js app with shadcn/ui
- Scaffold Rust sidecar with empty tonic server
- Docker Compose with all 6 services running health checks
- CI/CD pipeline (build, lint, test all three codebases)

**Phase 1: Database + API (Weeks 2–4)**
- Implement full PostgreSQL schema (section 5)
- Port all service logic from KAST.Infrastructure to Cast.Infrastructure:
  - ServerService, ModService, InstallationService, MonitoringService
  - SettingsService, UserAccountService, ModPresetService, MissionService
- Implement all API endpoint modules (section 6)
- Implement OIDC + AD authentication (section 11)
- Implement RBAC (section 11)
- Implement audit logging filter (section 13)
- Implement Prometheus metrics (section 13)
- Implement SSE connection manager (section 12)
- Write data migration script: SQLite → PostgreSQL
  - Export KAST data to JSON, import to CASTER PostgreSQL
  - Validate row counts + integrity

**Phase 2: Download sidecar (Weeks 5–6)**
- Implement full `caster-downloader` gRPC service (section 7)
- Implement .NET gRPC client adapter (`IDownloaderService` → `DownloaderGrpcAdapter`)
- Remove SteamKit2 from .NET (delete SteamClientService, CdnServerPool)
- Implement Steam auth relay flow (section 10)
- End-to-end test: install Arma 3 server via sidecar
- End-to-end test: download workshop mod via sidecar
- End-to-end test: QR login with real Steam account

**Phase 3: Frontend (Weeks 7–10)**
- Implement all pages and components (section 8)
- Implement TanStack Query hooks for all API calls
- Implement Zustand stores + SSE hooks (section 12)
- Implement Steam auth UI (QR display, credential form, status badge)
- Implement real-time monitoring dashboard (Recharts)
- Implement Monaco-based config editors
- Implement drag-and-drop mod assignment
- Implement download progress visualization
- Dark/light theme, responsive layout

**Phase 4: Integration + Cutover (Weeks 11–12)**
- Full integration test suite
- Performance testing (100+ mods, 10+ server instances)
- Load testing (5 concurrent users triggering downloads)
- Documentation (admin guide, deployment guide)
- KAST v1 data migration dry-run on production data
- Cutover: stop KAST v1, migrate data, start CASTER v1
- Monitoring: first 48 hours of production observation

### 15.2 Rollback Plan

KAST v1 data is never deleted during migration. The migration script is read-only on the KAST database. If CASTER v1 fails:
1. Stop all CASTER services
2. Start KAST v1 (it still has its original SQLite database)
3. Investigate CASTER issues
4. Fix and re-migrate

### 15.3 Data Migration Script (SQLite → PostgreSQL)

```csharp
// Cast.Infrastructure/Data/Migration/DataMigrator.cs
public class DataMigrator
{
    public async Task MigrateAsync(
        string sqliteConnectionString,
        string postgresConnectionString,
        CancellationToken ct)
    {
        // 1. Open SQLite connection (read-only)
        // 2. For each entity table:
        //    a. Read all rows from SQLite
        //    b. Transform: UUIDs (SQLite uses int IDs, CASTER uses UUIDs)
        //    c. Bulk insert into PostgreSQL
        //
        // Entity mapping:
        //   KastUser → users (int ID → UUID)
        //   KastSettings → settings (singleton row)
        //   ServerInstance → server_instances
        //     - Resolve ArmaInstallationId → installation_id
        //     - Flatten config content (JSON in v1 → TEXT columns in v2)
        //   SteamMod → mods
        //     - Resolve local paths
        //   ServerInstanceMod → server_instance_mods
        //     - Map old IDs to new UUIDs
        //   HeadlessClient → (embedded in server_instances.headless_client_count)
        //   DownloadTask → download_tasks
        //
        // Not migrated (rebuild):
        //   - Audit log (starts fresh)
        //   - Monitoring snapshots (starts fresh)
        //   - API keys (re-create)
        //   - Steam auth state (re-authenticate)
    }
}
```

---

## 16. Implementation Timeline

```
Week 1:  Phase 0 — Scaffolding
Week 2:  Phase 1 — Database schema + EF Core context + migrations
Week 3:  Phase 1 — Backend services (Servers, Mods, Installations)
Week 4:  Phase 1 — API endpoints + Auth (OIDC, RBAC) + Audit + Metrics + SSE
Week 5:  Phase 2 — Rust sidecar (auth, download_app)
Week 6:  Phase 2 — Rust sidecar (download_workshop, benchmark, cancel) + .NET adapter
Week 7:  Phase 3 — Frontend layout, dashboard, auth pages
Week 8:  Phase 3 — Server editor (all tabs), mods page
Week 9:  Phase 3 — Monitoring, installations, settings pages
Week 10: Phase 3 — Steam auth UI, real-time SSE integration, polish
Week 11: Phase 4 — Integration testing, bug fixing
Week 12: Phase 4 — Data migration, deployment, cutover

Total: 12 weeks (3 months), 1–2 full-time developers.
```

---

## 17. Testing Strategy

### 17.1 Test Layers

| Layer             | Framework       | Scope                                        |
|-------------------|-----------------|----------------------------------------------|
| Rust unit         | cargo test      | Download orchestrator, session manager, error mapping |
| Rust integration  | cargo test      | gRPC server with mock steamroom              |
| .NET unit         | xUnit + NSubstitute | Service logic, validators, DTO mapping   |
| .NET integration  | TestHost + Postgres (Testcontainers) | Full API pipeline with real DB |
| Frontend unit     | Vitest + Testing Library | Component rendering, hook behavior |
| Frontend E2E      | Playwright      | Critical user flows (login → create server → add mod → start) |
| Contract          | buf breaking    | Proto backward compatibility check           |

### 17.2 Critical Integration Tests

```
1. cast-downloader integration (with real Steam, needs Steam account)
   - Anonymous download of Spacewar (AppID 480) as smoke test
   - Workshop download of a small public mod
   - Cancel mid-download
   - QR login flow end-to-end
   - Credential + 2FA login end-to-end
   - Token refresh flow
   - CDN server failover (kill one CDN server, verify rotation)

2. cast-api integration (TestHost + Testcontainers Postgres)
   - Full CRUD for all resources
   - Auth: login, OIDC callback simulation, RBAC enforcement
   - Audit: verify entries written for each mutation
   - SSE: verify events delivered to connected client
   - Download queue: verify tasks progress through states

3. Frontend E2E (Playwright with mock API)
   - Dashboard loads with stats
   - Server creation + config editing
   - Mod add + download progress UI
   - Steam QR code display + auth flow
   - Dark/light theme toggle
   - Responsive breakpoints
```

---

## 18. Appendix: KAST v1 → CASTER Mapping

### 18.1 Concept Mapping

| KAST v1                                     | CASTER v1                                    |
|---------------------------------------------|----------------------------------------------|
| KAST.Core (models, interfaces, enums)       | Cast.Core (same, adapted for UUIDs + new fields) |
| KAST.Infrastructure/Services                | Cast.Infrastructure/Services (ported)         |
| KAST.Infrastructure/Data/KastDbContext       | Cast.Infrastructure/Data/CastDbContext (PostgreSQL) |
| KAST.Infrastructure/Steam/SteamClientService | cast-downloader (Rust)                        |
| KAST.Infrastructure/Steam/CdnServerPool      | steamroom::cdn::CdnServerPool (Rust)           |
| KAST.Infrastructure/Steam/SteamWebApiClient  | Cast.Infrastructure/Steam/SteamWebApiClient (kept, no SK2 dep) |
| KAST.UI/Program.cs (bootstrap)              | Cast.Api/Program.cs                           |
| KAST.UI/Api/KastApiEndpoints.cs             | Cast.Api/Modules/ (split by vertical slice)   |
| KAST.UI/Hubs/MonitoringHub.cs               | Cast.Infrastructure/Sse/SseConnectionManager  |
| KAST.UI/Hubs/DownloadHub.cs                 | Cast.Infrastructure/Sse/SseConnectionManager  |
| KAST.UI/Services/MetricsBackgroundService   | Cast.Api/BackgroundServices/MetricsCollectorService |
| KAST.UI/Services/ProcessWatchdogService     | Cast.Api/BackgroundServices/ProcessWatchdogService |
| KAST.UI/Services/SchedulingBackgroundService| Cast.Api/BackgroundServices/SchedulingService |
| KAST.UI/Components/Pages/ (Blazor)           | cast-frontend/app/ (Next.js)                  |
| KAST.UI/Components/Pages/Mods.razor          | cast-frontend/app/mods/page.tsx               |
| KAST.UI/Components/Pages/ServerEdit.razor    | cast-frontend/app/servers/[id]/page.tsx       |
| MudBlazor components                         | shadcn/ui components                           |
| Chart.js (via JS interop)                    | Recharts (React-native)                        |
| SignalR (WebSocket)                          | SSE (Server-Sent Events)                       |
| SQLite (single writer lock)                  | PostgreSQL 17 (concurrent writes)              |
| GPL v3                                       | Proprietary (v2 clean-room rewrite)            |

### 18.2 What Stays Exactly the Same

These components are proven, well-tested, and do not benefit from rewriting:

1. **Config parser/generator** (ServerConfigService — 767 lines of AST-based parser)
   - Ported as-is from `KAST.Infrastructure` to `Cast.Infrastructure`
   - No dependency on SteamKit2, SQLite, or Blazor

2. **Output sanitizer** (OutputSanitizer — 291 lines)
   - Same logic, adapted for UUID-based resource resolution

3. **Local mod installer** (LocalModInstaller)
   - ZIP extraction, directory size calculation — no Steam involvement

4. **Process manager** (ProcessManagerService)
   - Process spawn/kill/monitor — no dependency changes

5. **Crash report service** (CrashReportService)
   - Same file-based JSON crash reports

6. **Server console log tailer** (ServerConsoleLogTailer)
   - Same tail-f logic

7. **Arma RPT event detector** (ArmaRptEventDetector)
   - Same regex-based log parsing

### 18.3 What Is Fully Rewritten

1. **Frontend**: Blazor Server → Next.js App Router + shadcn/ui + TanStack Query
2. **Real-time**: SignalR → SSE
3. **Download engine**: SteamKit2 (C#) → steamroom (Rust) via gRPC
4. **Database**: SQLite → PostgreSQL (schema redesigned for UUIDs, JSONB, concurrent access)
5. **Auth**: Local-only + optional OIDC → OIDC-primary + AD + local fallback + RBAC
6. **API architecture**: Monolithic endpoint file → Vertical slice modules
7. **Audit/observability**: None → Audit log + Prometheus metrics

---

## License Notice

This plan is a **clean-room design** for a proprietary enterprise application. It references the architecture and feature set of KAST v1 (GPLv3) but specifies a complete reimplementation using different technologies, languages, frameworks, libraries, and architectural patterns. No KAST v1 source code will be copied or directly translated. All code will be written from scratch against this specification.

---

*Document version: 1.0*
*Last updated: 2026-06-14*
*Authors: 3rd Shock Army × Bluefield*
