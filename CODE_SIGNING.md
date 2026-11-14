# Code signing policy

35Viewer is distributed for Windows only, as a ZIP file on GitHub Releases.

## Windows — SignPath Foundation (pending)

We are applying to the SignPath Foundation program.

Planned statement (required by the program, if approved):
"Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org)"

Status: Pending approval. Releases up to and including v0.2.0 are **not signed**.

### What will be signed

- The application's own executable and libraries (`K35Viewer.exe`, `K35Viewer.dll`) contained in the release ZIP
  (`K35Viewer-v<version>-win-x64.zip`) published on GitHub Releases.
- Third-party files bundled in the ZIP (the .NET runtime and NuGet package libraries) are distributed as provided by their publishers.

### Build and signing process

- Release artifacts are built from this public repository by GitHub Actions
  ([`.github/workflows/release.yml`](.github/workflows/release.yml)) when a version tag (`v*`) is pushed.
  The workflow runs the full test suite before building the artifacts.
- Only CI-built artifacts will be submitted to SignPath for signing. Artifacts built on a developer machine are never signed.
- The private key is held by SignPath (HSM-backed). This project does not store the private key.

### Team roles (single-maintainer project)

- Authors (commit access, can modify the repository without additional reviews):
  - <https://github.com/k355566687>
- Reviewers (review required for changes proposed by non-committers, e.g. pull requests):
  - <https://github.com/k355566687>
  - Policy: All external pull requests are reviewed by the maintainer before merge.
- Approvers (approve each signing request):
  - <https://github.com/k355566687>
  - Policy: Each signing request requires explicit approval by the maintainer.

## Distribution locations

- <https://github.com/k355566687/K35Viewer/releases>

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

35Viewer does not send any data over the network on its own: it has no telemetry and no automatic update check.
The only network access is the update feature in the settings window, and only when the user presses "Check for updates" or "Update":
it sends plain HTTPS requests to GitHub (`api.github.com` and `github.com`) to look for and download a newer release.
No user data is sent.
It only reads and writes files in the folders that the user registers (which may be network shares such as a NAS),
and stores its own data (a SQLite database, thumbnail cache and logs) locally in `%LOCALAPPDATA%\K35Viewer\`.
