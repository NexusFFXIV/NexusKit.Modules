# NexusKit.Modules

**Reusable feature modules built on [NexusKit](https://github.com/NexusFFXIV/NexusKit).**

[![CI](https://github.com/NexusFFXIV/NexusKit.Modules/actions/workflows/ci.yml/badge.svg)](https://github.com/NexusFFXIV/NexusKit.Modules/actions/workflows/ci.yml)
[![CodeQL](https://github.com/NexusFFXIV/NexusKit.Modules/actions/workflows/codeql.yml/badge.svg)](https://github.com/NexusFFXIV/NexusKit.Modules/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/NexusFFXIV/NexusKit.Modules?label=release&logo=github)](https://github.com/NexusFFXIV/NexusKit.Modules/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![NexusKit](https://img.shields.io/badge/NexusKit-%E2%89%A50.1.0-9D5BFF)](https://github.com/NexusFFXIV/NexusKit)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

## Overview

This repo holds the opt-in feature modules that sit on top of NexusKit. Each module is published as its own NuGet package so consumers pull only what they need. The modules cover the recurring high-level needs of a player-tracking-style FFXIV Dalamud plugin: tracking the local session, enriching player info via external sources, and bridging to other plugins via IPC.

Two architectural rules from the framework carry over here:

- **`InternalData` and `ExternalData` do not reference each other.** Their bridge lives in `PlayerEnrichment`.
- **`External/*` modules are standalone bricks**: a typed HttpClient (`FfxivCollect`), a Lodestone scraper (`Lodestone`), and a generic foreign-plugin-IPC adapter (`PluginBridge`). They have no Dalamud or InternalData dependencies and could be used by any consumer.

## Packages

| Package | What it provides |
|---|---|
| `NexusKit.Modules.InternalData` | Tracks the local player's session — player watcher, encounter tracker, history — using Dalamud framework events. |
| `NexusKit.Modules.ExternalData` | Aggregates Lodestone + FFXIVCollect external data into a unified read model with shared cache and refresh queue contracts. |
| `NexusKit.Modules.PlayerEnrichment` | Bridges InternalData and ExternalData via Lodestone-id resolution, the refresh queue, and shared UI hooks. |
| `NexusKit.Modules.FfxivCollect` | Typed HttpClient for the public FFXIVCollect API with response caching. |
| `NexusKit.Modules.Lodestone` | NetStone-based scraper for Lodestone character pages, with anti-throttling and DB-backed cache. |
| `NexusKit.Modules.PluginBridge` | Adapters that consume foreign Dalamud plugin IPCs (Visibility, PriceCheck, etc.) so consumers stay loosely coupled. |

## Install

Modules depend on the corresponding `NexusKit.*` framework packages, so configure both feeds. Create a `nuget.config` next to your `.sln`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="github-nexusffxiv" value="https://nuget.pkg.github.com/NexusFFXIV/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github-nexusffxiv>
      <add key="Username" value="<your-github-login>" />
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_PAT%" />
    </github-nexusffxiv>
  </packageSourceCredentials>
</configuration>
```

Create a [classic PAT](https://github.com/settings/tokens) with scope `read:packages`, store it as `GITHUB_PACKAGES_PAT`, then:

```powershell
dotnet add package NexusKit.Modules.PlayerEnrichment
# pulls all needed dependencies (Internal/ExternalData, Lodestone, FfxivCollect)
```

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using NexusKit.Modules.PlayerEnrichment;

services
    .AddNexusKitCore()
    .AddNexusKitPersistence()
    .AddNexusKitHosting()
    .AddNexusKitUi()
    .AddNexusKitGameData()
    .AddNexusKitPlayerEnrichment();   // pulls InternalData + ExternalData transitively
```

## Build from source

```powershell
git clone https://github.com/NexusFFXIV/NexusKit.Modules.git
cd NexusKit.Modules
# Restore needs your GITHUB_PACKAGES_PAT env var set; see Install section
dotnet build NexusKit.Modules.sln -c Release
```

The Dalamud-tied modules (`InternalData`, `PlayerEnrichment`) need a local Dalamud install. CI installs it from `https://goatcorp.github.io/dalamud-distrib/latest.zip`; locally, having XIVLauncher's dev hook at `%APPDATA%\XIVLauncher\addon\Hooks\dev\` is sufficient.

## Contributing

PRs welcome. Contributions are accepted under AGPL-3.0-only.

## License

[AGPL-3.0-only](LICENSE). NetStone (transitively depended on by `Lodestone`) is also AGPL — your derivative work must stay open under the same license.
