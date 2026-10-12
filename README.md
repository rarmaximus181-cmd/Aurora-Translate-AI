# Aurora Translate AI

A Windows desktop workspace for translation, local AI conversation, and offline speech.

[Download the latest Windows release](https://github.com/rarmaximus181-cmd/Aurora-Translate-AI/releases/latest)

## What is included

- Translation across 50 selectable languages using free MyMemory online translation or a local Ollama model.
- A redesigned AI conversation workspace with model selection, task suggestions, response styles, optional reasoning, larger conversation context, and a bounded local calculator for arithmetic.
- Windows speech plus 12 downloadable Piper voice choices. Import your own Piper `.onnx` voice with its matching `.onnx.json` configuration.
- Four animated themes and custom PNG/GIF backgrounds.
- Discord sign-in and a cached profile image after one-time Discord application setup.
- An animated startup and GitHub update checks with verified downloads and required updates.
- An optional **Troll display** joke that rewrites the literal phrase “this is not allowed” on screen. It is visibly marked and does not change model prompts, original answers, conversation context, copying, or speech.

## Get started

Download the portable ZIP from Releases, extract it, and open `AuroraTranslate.exe`. Online translation needs internet and has MyMemory's anonymous daily allowance. Local AI uses [Ollama](https://ollama.com/download/windows) with an installed model. The app includes model download controls; `qwen3.5:4b` is the larger model option, while `qwen2.5:1.5b` is lighter. Model availability and speed depend on your computer.

Translation works as a guest. Discord login requires your registered Application ID, Public Client enabled, and the exact loopback redirect shown in Settings. See [Discord setup](DISCORD-SIGN-IN.md) and [custom voice instructions](CUSTOM-VOICES.md).

## Updates and releases

Version 2 and later check this repository's latest public release. New mandatory releases require an update before the workspace opens. The app checks the EXE's size, SHA-256 checksum, and version before installation. Earlier 1.0/1.1 EXEs need a manual upgrade once to gain the updater.

Every push to `main` runs the Windows build and isolated tests, then publishes a release numbered `2.0.<workflow run number>`. See [update and publishing details](UPDATES.md).

## Build and test

Use a Windows .NET 10 SDK:

```powershell
dotnet build AuroraTranslate.csproj -c Release
dotnet run --project Tests/ChatUpgradeChecks/ChatUpgradeChecks.csproj -c Release
dotnet run --project Tests/UpdateChecks/UpdateChecks.csproj -c Release
./Build/Prepare-Release.ps1 -Version 2.0.1
```

The conversation and update checks use local fixtures and mocked HTTP. Live model checks are available separately with the chat test runner's `--live` option and require Ollama.

## Data and components

Settings, copied backgrounds, optional translation history, voice models, and encrypted Discord sessions are stored under `%LOCALAPPDATA%\AuroraTranslate`. Chat text stays in memory for the session and is sent to the local Ollama service. Online translation sends text to MyMemory. No API key is bundled or requested for translation, speech, or local AI.

The speech engine's original native sources and notices are available in the release's native-source archive. Voice models have separate usage terms retained in their model cards. See [third-party notices](THIRD-PARTY-NOTICES.txt).
