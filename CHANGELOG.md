## [2.0.0] - 2026-09-05
### Bug Fixes
- Harden logout wait and drop redundant split-query hints
- Load difficulty profile on Windows, authenticate hub clients, expose /ready
- Harden CDN pool backoff, cancellation, and disposal
- Robust login lifecycle, reconnect supervisor, and queue races
- Reliable process lifecycle, schedules that fire, scoped hub broadcasts
- Harden mission, preset, account, update, and storage services
- Persist auth state, lock down ports, allow large uploads

### Features
- Password policy, API ProblemDetails, safer mod deletion, proxy networks
- Live player counts via Steam A2S query
- Blue Kepi design system - forensic Artoria retheme, dark-only de-Materialized MudBlazor, new kepi-C logo SVGs, staff-ribbon accents, CASTER_V1 spec + design docs
- Themed banner nav links in sidebar - skewed plates with kickers, Cinzel titles, brass rail + crimson hem on active, shine sweep, staggered deal-in
- Full Dress light mode + tailored nav banners - server-rendered mode class (no JS race), daylight token set, garments keep indigo, coat carries actions, de-skewed banner plates with stepped-hem corner
- Formalize dashboard - Inter + JetBrains Mono type system, dense grouped sidebar with instance and Settings deep-links, path-only breadcrumbs
- Default fresh installs to the Caster update channel (bluefield-creator/KAST)

### Maintenance
- Update changelog for v1.2.1 [skip ci]

## [1.2.1] - 2026-08-05
### Bug Fixes
- Restore mission download url round-trip
- Verify api keys after prefix lookup
- Guard progress broadcasts against async void
- Observe broadcast task failures
- Correct dispose pattern and drop finalizer
- Stabilize cdn pool lifecycle and waits
- Harden login state, timeouts and parallelism
- Dispose processes and parallelize sampling
- Prune instance lock cache
- Dedupe concurrent queue requests
- Parse log fields defensively
- Stop singleton process manager capturing scoped db
- Index hot columns and drop dead concurrency tokens
- Sync headless client rows from count
- Type status events with domain enums
- Sanitize all runtime event fields
- Evict completed install states
- Gate mission downloads and rate limit
- Make download actions async
- Marshal progress updates to circuit thread
- Harden hub auth, handlers and injection
- Guard event handlers against circuit crashes
- Defer update check off first render
- Reset restart counter on manual start
- Harden endpoint validation and status codes
- Rate limit login endpoint
- Avoid deadlock disposing queue registration

### Documentation
- Update badges and links to bluefield-creator/KAST
- Refactor README for CASTER branding and updated feature list
- Correct HTTP mission downloads description

### Maintenance
- Update changelog for v1.2.0 [skip ci]
- Update changelog [skip ci]
- Allow manual release dispatch

### Performance
- Fetch light snapshot for monitoring

### Refactoring
- Use TimeOnly for schedule times
- Dedupe install entry points
- Inject services instead of manual scopes

## [1.2.0] - 2026-06-20
### Bug Fixes
- Register IProcessManagerService in API test DI containers
- Add service stop+wait before file overwrite in Windows service mode

### Features
- Add mission hashing, HTTP downloads, process history, and performance tracking

### Maintenance
- Update changelog for v1.2.0 [skip ci]

## [1.1.3] - 2026-06-06
### Maintenance
- Update changelog for v1.1.2 [skip ci]

## [1.1.2] - 2026-06-06
### Maintenance
- Update changelog for v1.1.1 [skip ci]

## [1.1.1] - 2026-06-06
### Bug Fixes
- Generate changelog via action
- Honor parallel mod download limit

## [1.1.0] - 2026-06-06
### Bug Fixes
- Config file generation, disk write, and bidirectional UI sync
- Implement proper IDisposable pattern in SteamClientService (S3881)
- Pipeline hotfix for docker
- Unit tests
- Update permissions in release workflow
- Update Dockerfile to include CPM files
- Relative pathing & incorrect instanceID access
- Support internal self-connections
- Keep mod downloads running across navigation
- Harden discovery and mod download retries
- Resolve develop conflicts
- Pass all code quality warnings
- Resolve Arma import cancellation race
- Handle protected browser keyring failures
- Harden bulk queue recovery

### Features
- AST-based Arma 3 config parser with nested class support
- Wire up SignalR hubs to services for real-time broadcasting
- Add background metrics service for real-time SignalR broadcasting
- Add DB-persisted application settings with env var overrides
- Add process watchdog for crash recovery with restart policies
- Add scheduling background service for auto start/stop
- Implement DownloadAppAsync for Arma 3 DS install/update via SteamKit2
- Add dark/light theme toggle in header
- Enhance database queries and UI components for improved performance and usability
- Enhance server instance management with installation tracking and download progress
- Enhance download process with step tracking and parallel download settings
- Enhance process management with output handling and monitoring capabilities
- Update GitHub Actions workflows for CI/CD and add new features
- Add LastChecked and Comment properties to SteamMod model and update related migrations
- Enhance mod management by adding IsClientSide flag and updating related services and UI components
- Add ModsTab for managing server mods with drag-and-drop functionality
- Migration to CPM
- Enhance theme management with dark mode toggle and accent color options
- Add nightly build and release workflows for automated packaging and GitHub releases
- Add Unload All action to instance mods tab
- Update UX and monitoring flows
- Better campaign view
- Implement Output Sanitizer for improved error handling and logging
- Update OutputSanitizer to use content root path and add test for mods virtual path
- Sanitize executable path in StartInstanceAsync method
- Remove cancellation token from RunAsync calls in StartServerInstall and StartModInstall methods
- Add native app update flow
- Add Windows service management

### Maintenance
- Add GitHub Actions CI/CD pipeline and pin .NET SDK version
- Use .KAST_DATA/ as dev workdir for all data files
- Bump xunit.runner.visualstudio from 3.1.4 to 3.1.5 (#30)
- Bump QRCoder from 1.7.0 to 1.8.0 (#29)
- Bump MinVer from 5.0.0 to 7.0.0 (#28)
- Bump softprops/action-gh-release from 2 to 3 (#23)
- Bump coverlet.collector from 6.0.4 to 10.0.1 (#27)
- Bump github/codeql-action from 3 to 4 (#25)
- Bump crazy-max/ghaction-github-labeler from 5 to 6 (#24)
- Bump docker/build-push-action from 6 to 7 (#22)
- Bump Microsoft.AspNetCore.Cryptography.KeyDerivation and 8 others (#26)
- Bump action versions (#36)
- Sanitize paths
- Trigger caster nightly
- Skip SonarCloud when token is missing
- Add stable promotion workflow

### Other
- Merge native app update flow
- Sync remote caster

### Refactoring
- Code quality fixes

