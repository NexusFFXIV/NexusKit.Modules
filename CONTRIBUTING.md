# Contributing to NexusKit.Modules

Thanks for considering a contribution. This doc covers the workflow; the release process lives in [RELEASING.md](RELEASING.md).

This repo holds the **opt-in feature modules** built on top of [NexusKit](https://github.com/NexusFFXIV/NexusKit). All changes here must remain compatible with the public NexusKit API of the version range declared in the module csprojs.

## Branch & PR workflow

All changes go through a Pull Request. Direct pushes to `main` are blocked by branch protection.

1. **Branch off `main`** with a descriptive name:
   ```powershell
   git checkout main; git pull
   git checkout -b <scope>/<short-summary>
   ```
   Suggested scopes: `feat/`, `fix/`, `chore/`, `docs/`, `refactor/`, `test/`. Example: `fix/lodestone-handle-504`.

2. **Commit** with clear imperative messages:
   ```
   fix(Lodestone): retry with backoff on transient 504
   ```

3. **Push the branch and open a PR**:
   ```powershell
   git push -u origin <branch>
   gh pr create
   ```

4. **CI runs on the PR** — the `build` check must be green. Merge via squash once approved + green.

## Local development

Recommended layout: use the **NexusFFXIV workspace** (clone NexusKit + NexusKit.Modules + PlayerNexusTracker side by side under a common parent). The workspace's `Directory.Build.targets` rewires `PackageReference Include="NexusKit.*"` to `ProjectReference` against the sibling NexusKit source. Edits in NexusKit propagate to Modules instantly — no NuGet publish needed during development.

If you only clone this repo standalone, restore needs a GitHub Packages PAT (`read:packages` scope) in `$env:GITHUB_PACKAGES_PAT`. The `nuget.config` in this repo references that variable.

Smoke-test before opening a PR:
```powershell
dotnet build NexusKit.Modules.sln -c Release
```

If you also have the integration tests cloned (in `NexusFFXIV/localTools/tests/`):
```powershell
dotnet test ../localTools/tests/NexusKit.Modules.ExternalData.Tests
```

## Cutting a testing build

For changes that need real-world validation before going to all consumers (e.g. a Lodestone scraper change you want to verify against live Lodestone HTML before stable users get it), publish a **testing release** first:

1. Land the work on `main` via the normal PR flow.
2. From `main`, tag with a pre-release suffix:
   ```powershell
   git tag -a v0.2.0-rc.1 -m "v0.2.0-rc.1 — testing build for <reason>"
   git push origin v0.2.0-rc.1
   ```
3. CI publishes pre-release NuGets. The GitHub Release is automatically marked **Pre-release** because the tag contains `-`.
4. Consumer plugins can opt in by temporarily pinning the pre-release version, e.g.:
   ```xml
   <PackageReference Include="NexusKit.Modules.PlayerEnrichment" Version="0.2.0-rc.1" />
   ```
5. After validation, cut the stable version (`v0.2.0`) — no separate code change, just the tag. Consumers on floating `[0.1.0,)` automatically pick up `0.2.0`.

Suffix conventions (descending stability): `-rc.N`, `-beta.N`, `-preview.N`.

The PR does **not** need to know whether it will ship as testing or stable — that's a tag-time decision.

## Module boundaries

Two architectural rules carry over from NexusKit and **must not** be broken:

- **`InternalData` and `ExternalData` do not reference each other.** Their bridge lives in `PlayerEnrichment`. PRs that add a direct ref between them will be rejected.
- **`External/` modules are standalone**: `FfxivCollect`, `Lodestone`, `PluginBridge` reference only `NexusKit.Core` / `NexusKit.Persistence` / `NexusKit.GameData` from the framework. They do not reference InternalData/ExternalData/PlayerEnrichment.

## Code style

Follows [NexusKit's coding-conventions.md](https://github.com/NexusFFXIV/NexusKit/blob/main/docs/coding-conventions.md). Highlights:

- `m`-prefix on private instance fields
- `ConfigureAwait(false)` on every background-I/O `await`
- File-scoped namespaces, one public type per file
- `Nullable` enabled — no `!` to silence the compiler

## License

By contributing, you agree your contribution is licensed under **AGPL-3.0-only** (NetStone, a transitive dep of `Lodestone`, is also AGPL — your fork must stay open).
