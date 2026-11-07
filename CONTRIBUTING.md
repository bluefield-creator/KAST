# Contributing to KAST

Thank you for taking the time to contribute! This document explains how the project is structured, how to get started, and the rules we follow to keep history clean and releases automated.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Branching Strategy](#branching-strategy)
- [Commit Messages — Conventional Commits](#commit-messages--conventional-commits)
- [Versioning](#versioning)
- [Development Setup](#development-setup)
- [Submitting Changes](#submitting-changes)
  - [Issues](#issues)
  - [Pull Requests](#pull-requests)
- [Release Process](#release-process)
- [Getting Help](#getting-help)

---

## Code of Conduct

By participating in this project you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md). We hold ourselves and contributors to high standards of communication.

---

## Branching Strategy

| Branch | Purpose |
|---|---|
| `caster` | Default branch. Every push is tested and publishes the rolling `nightly` build. Stable releases are tagged from here. |
| `feat/<name>`, `fix/<name>` | Work branches. Branch from `caster`, PR back into `caster`. |

**Rules:**
- `caster` is protected. All changes go through a Pull Request.
- Branch prefixes mirror the Conventional Commit types and are used to label PRs.

---

## Commit Messages — Conventional Commits

All commits must follow the [Conventional Commits](https://www.conventionalcommits.org/) specification. This drives automatic changelog generation and semantic versioning.

### Format

```
<type>[optional scope]: <short description>

[optional body]

[optional footer(s)]
```

### Types

| Type | When to use | Version bump |
|---|---|---|
| `feat` | A new feature | Minor (`0.x.0`) |
| `fix` | A bug fix | Patch (`0.0.x`) |
| `perf` | Performance improvement | Patch |
| `refactor` | Code change that neither fixes a bug nor adds a feature | — |
| `docs` | Documentation only | — |
| `test` | Adding or updating tests | — |
| `chore` | Build process, dependencies, tooling | — |
| `ci` | CI/CD configuration | — |

Append `!` after the type for a **breaking change** — this bumps the Major version:
```
feat!: remove legacy mod import API
```

### Examples

```
feat(mods): add Steam Workshop search by collection
fix(steam): reconnect loop on session expiry
perf(db): index ServerInstance by status
docs: document API key creation flow
chore: bump SteamKit2 to 3.1.0
```

### PR Titles

When merging via Pull Request, use the PR **title** as the conventional commit message — GitHub uses it as the squash-merge commit message. Individual commits on the branch do not need to be conventional, but the PR title must be.

---

## Versioning

KAST uses [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`.

Versions are derived automatically from git tags using **MinVer**. You never set the version manually in any `.csproj`.

| Scenario | Version example |
|---|---|
| Tagged release `v1.2.3` | `1.2.3` |
| Nightly (`caster`) | `1.2.3-nightly.20260522.abc1234` |
| Local dev build | `1.2.3-alpha.0.5` |
| Docker | `ghcr.io/bluefield-creator/kast:1.2.3`, `:latest`, `:stable`, `:nightly` |

To see the current computed version locally:
```bash
dotnet build --configuration Release
# version is embedded in the assembly and shown in the app footer
```

---

## Development Setup

**Prerequisites:** .NET 10 SDK, Docker (optional).

```bash
git clone https://github.com/bluefield-creator/KAST.git
cd KAST
dotnet restore
dotnet build
dotnet test
```

**Run locally:**
```bash
dotnet run --project src/KAST.UI
```

**EF Core migrations** (if you change any entity):
```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add <MigrationName> \
  --project src/KAST.Infrastructure \
  --startup-project src/KAST.UI \
  --output-dir Data/Migrations
```

**Docker:**
```bash
docker compose up --build
```

---

## Submitting Changes

### Issues

- Search existing issues before opening a new one.
- Use the provided templates (bug report / feature request).
- For breaking or architectural changes, open an issue to discuss before writing code.

### Pull Requests

1. Fork the repository (external contributors) or create a branch on the upstream repo (collaborators).
2. Branch from `caster`:
   ```bash
   git checkout caster && git pull --ff-only
   git checkout -b fix/my-fix
   ```
3. Write your code. Keep commits focused; one logical change per commit.
4. Ensure all tests pass: `dotnet test`
5. Push and open a PR targeting `caster`.
6. **PR title must follow the Conventional Commits format** — it becomes the squash-merge commit message and feeds the changelog.

**PR checklist:**
- [ ] PR title follows Conventional Commits
- [ ] Tests added or updated for the changed behaviour
- [ ] No unrelated whitespace or refactoring mixed in
- [ ] EF migration included if entity models changed

---

## Release Process

Stable releases are tag-driven: a release is published when a tag matching
`vX.Y.Z` points to a commit reachable from `caster`. Nightly builds are separate
and use the rolling `nightly` tag. No manual changelog editing or version
bumping is required.

### Promote from GitHub Actions

1. Open **Actions** and run **Promote Stable Release**.
2. Set `version` to the stable version, for example `1.2.3`.
3. Leave `target_ref` as `caster` unless promoting a specific commit that is
   already reachable from `caster`.
4. Leave `publish_latest` enabled for normal stable releases.

The promotion workflow creates and pushes an annotated tag; the `Release`
workflow then runs the tests, builds native packages, pushes Docker images and
creates the GitHub Release.

### Promote from the command line

```bash
git fetch origin caster --tags
git checkout caster
git pull --ff-only origin caster
git tag -a v1.2.3 -m "KAST v1.2.3"
git push origin v1.2.3
```

CLI-created tags default to `make_latest: true`. To publish a stable release
without marking it as latest, add this line to the annotated tag message:

```text
Publish-Latest: false
```

### Expected outputs

- GitHub Release named `KAST vX.Y.Z` with generated notes.
- Linux x64, Linux ARM64 and Windows x64 archives, each with a `.sha256` file.
- GHCR Docker tags `X.Y.Z`, `latest` and `stable`.

---

## Getting Help

Join us on [Discord](https://discord.gg/2BUuZa3) and ask in the appropriate channel.
