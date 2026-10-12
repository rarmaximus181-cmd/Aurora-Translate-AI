# GitHub releases and updates

The official source and releases are hosted at https://github.com/rarmaximus181-cmd/Aurora-Translate-AI.

Aurora 2 checks the newest public release at startup. A newer mandatory release opens an update screen before the workspace. The app downloads the EXE, verifies its size, SHA-256 checksum and executable version, then restarts after installing it. Your voices, settings, themes and account profile stay in your Windows user profile.

Close Aurora and run the new EXE once if you are upgrading from 1.0 or 1.1; those older builds did not contain an updater. Once a required newer version is known, an offline restart still shows the update screen. If GitHub is unavailable before a required update is known, the workspace can open with an update-check status message.

## Publishing future changes

Commit and push source changes to the repository's main branch. The GitHub Actions workflow builds the Windows EXE, runs the conversation and update checks, creates a SHA-256 update manifest, and publishes a release. Versions are `2.0.<workflow run number>`, so each successful run produces a newer version. The release includes the EXE, a portable ZIP with guides/notices, the update manifest and native dependency sources. It uses GitHub's built-in workflow token; no token is embedded in the app.

Only the GitHub repository's owner and authorized maintainers can publish trusted updates. HTTPS and the release manifest provide download integrity; this is not an Authenticode signature. Review the source changes before pushing them to main, because the build publishes automatically.
