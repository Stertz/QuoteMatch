# Releases, measured badges and client updates

The [verification workflow](../.github/workflows/verify.yml) tests the application, refreshes the README and builds update-capable releases for Windows, Linux and macOS. Publication and client installation are separate actions: publishing a release does not restart running clients.

## README badges

[Update-ReadmeBadges.ps1](../scripts/Update-ReadmeBadges.ps1) reads the repository's source of truth:

| Indicator | Source |
| --- | --- |
| SDK | `global.json` |
| .NET target | `Directory.Build.props` |
| Avalonia | `Directory.Packages.props` |
| Application coverage | Covered/valid source lines for all four measured assemblies in `summary.json` |
| Core coverage | The same measurement excluding Desktop |
| Build and latest release | GitHub Actions and Releases badges |

Coverage is an aggregate of actual covered lines, not an average of module percentages. The script requires all four modules and rejects invalid counts before changing the README. CI uses its Linux measurement after all three verification jobs pass. The generator replaces only the marked badge block; it does not rewrite the rest of the README.

The default-branch job commits the generated README using `GITHUB_TOKEN`. It checks that the branch still points at the measured commit and never force-pushes. A concurrent source update takes precedence. Bot pushes do not recursively create another release. Branch protection must permit this bot update; if direct writes are prohibited, adapt this job to create a documentation pull request.

For a local refresh after coverage:

```powershell
./scripts/Test-Coverage.ps1
./scripts/Update-ReadmeBadges.ps1
./scripts/Update-ReadmeBadges.ps1 -Check
```

These badges show the pinned versions actually used by this project. They do not claim that a package is the newest release available. Dependabot proposes dependency/action updates; the normal review and verification workflow applies before merging them.

Dependabot groups the Avalonia packages into one pull request. Update the core, Desktop, Fluent theme and Headless packages together so the application and desktop tests use the same framework version.

## Release pipeline

Every push and pull request runs synthetic tests, real SQLite checks, coverage enforcement, automation checks and formatting on Windows, Linux and macOS. A successful default-branch push or a manual run on that branch also builds all four native targets:

| Target | Distribution |
| --- | --- |
| `win-x64` | Per-user setup executable and complete portable ZIP |
| `linux-x64` | Executable AppImage |
| `osx-x64` | macOS Intel installer and portable app bundle |
| `osx-arm64` | macOS Apple Silicon installer and portable app bundle |

The numeric base is defined once in `Directory.Build.props`. [Get-ReleaseVersion.ps1](../scripts/Get-ReleaseVersion.ps1) adds the workflow run number minus one to its patch component, keeping a normal three-part version and a `v<version>` tag. With base `0.1.0`, runs 1, 2 and 42 produce `0.1.0`, `0.1.1` and `0.1.41`. A rerun retains its run number and cannot replace an already published release with different assets. Changing the base does not reset the workflow counter.

Each native job publishes the Desktop project with the same version/source commit, includes the .NET runtime and installs `vpk` using the Velopack version from central package management. [New-ReleasePackage.ps1](../scripts/New-ReleasePackage.ps1) creates the platform packages and embeds the Unreleased section from [CHANGELOG.md](../CHANGELOG.md). Maintain that section with user-visible changes before release.

[Test-ReleasePackage.ps1](../scripts/Test-ReleasePackage.ps1) verifies the update feed's package identity, version, file size and SHA-256; it also opens the payload to check its runtime configuration, source commit, channel and application files. Each target gets a checksum manifest. The publication job downloads and validates all four targets, rejects filename collisions, uploads everything to a draft and publishes the complete release together. Pull requests cannot publish releases or modify README badges.

No private documents, local databases, credentials or installation queries are release inputs. Packages come from the application's publish directory. Build/coverage artifacts remain separate from release assets.

## Update channels and hosting

Each target uses its own channel: `win-x64-stable`, `linux-x64-stable`, `osx-x64-stable` or `osx-arm64-stable`. The release includes the corresponding `releases.<channel>.json` and full `.nupkg`, as well as first-install downloads. Keeping a separate channel for every OS/architecture prevents a client from selecting another platform's package. See [Velopack channels](https://docs.velopack.io/packaging/channels).

Each package also includes an identical feed under its earlier `*-preview` name. This compatibility alias lets an older installed client discover the normal release; after installation it adopts the package's stable channel. The alias does not create another version or mark the GitHub release as a prerelease.

The client uses Velopack's GitHub source against the public repository recorded in its assembly metadata. CI derives this URL from `github.repository`; local builds use `RepositoryUrl` from `Directory.Build.props`. A fork's generated packages therefore use that fork's releases. Private feeds and access tokens are not configured by this implementation.

The pipeline publishes normal releases, marks them as the latest release on GitHub and the client excludes prereleases. There is no separate promotion step or user-selectable release channel.

## Client experience

1. Install a packaged release once. On Linux, make the AppImage executable; keep portable Windows folders and macOS bundles intact.
2. The footer always shows the running version. Packaged clients check once at startup, then every six hours while open. **Check for updates** can request another check.
3. A newer compatible version downloads in the background with progress and cancellation. The application keeps working; closing the update window does not cancel the download.
4. When downloaded, the footer announces that an update is ready. Open the version button to see the target version and packaged release notes.
5. **Install and restart** explicitly authorizes the restart. Other open work windows, active work, unsaved quotation/template/integration edits and pending/uncertain order journals block it with an actionable message.
6. The updater waits for graceful application exit, replaces the installed application and opens the new version. A downloaded update is detected again after reopening. Startup auto-application is disabled so a cached download alone cannot authorize installation.

Release notes are displayed as text, without executing HTML or embedded content. Connection/download failures remain local to the update panel and do not prevent quotation work. The SDK verifies downloaded package integrity before it is eligible for installation. These behaviors use the [Velopack C# API](https://docs.velopack.io/getting-started/csharp).

Source builds are intentionally not treated as installed releases and make no automatic feed requests. Copying only a new executable is not an update path: use the installer, complete portable package or AppImage produced by the pipeline. Velopack uses different [platform installation layouts](https://docs.velopack.io/category/operating-systems).

## Local data and recovery

SQLite, source documents, preferences and protected credentials remain in the application data directory, separately from installed binaries and update caches. Keep any `QUOTEMATCH_DATA_DIRECTORY` override outside the installation directory. Updating application files must not package or replace this data directory.

Existing migration backups still apply when the new version opens an older database. There is no automatic database downgrade or complete application/data rollback guarantee. Pending external operations remain journaled and must be reconciled before installing through the application.

## Verification and distribution limits

Public tests exercise the updater against a synthetic local feed, including application-identity rejection, real SDK download/checksum validation and downloaded-update recovery. Headless desktop tests cover progress/cancellation, explicit installation, offline failure, release notes and restart guards using the actual ViewModels/repositories. Automation scripts verify generated badges and the published payload contracts; Linux inspection extracts and checks the same AppImage stored in its full update package.

A passing package check does not establish a successful first installation or version-to-version restart on a native customer desktop. Those checks must be recorded independently on each OS/architecture after a release is available. The GitHub workflow must also execute in the hosted repository before its hosted behavior can be claimed as verified.

Signing credentials are not configured. Installers are unsigned and macOS packages are not notarized; operating-system trust policies can prevent installation or launch. Signing/notarization must be configured and tested before customer distribution. See [Velopack signing](https://docs.velopack.io/packaging/signing) and [Avalonia macOS deployment](https://docs.avaloniaui.net/docs/deployment/macos). A checksum detects changed bytes; it is not a publisher signature.

The project license remains pending as described in the [README](../README.md). Release automation does not choose a license or certify live ERP behavior.
