# Releasing NexusKit.Modules

NexusKit.Modules follows [Semantic Versioning](https://semver.org/). Versions are derived from git tags via [MinVer](https://github.com/adamralph/minver).

All 6 packages in this repo ship with the **same version**. PackageReference constraints like `[0.1.0,)` point at NexusKit, but a critical NuGet behaviour to keep in mind: `PackageReference` resolves to the **lowest** version satisfying the range, **not** the latest. So a new NexusKit release does **not** get picked up automatically — the constraint floor has to be bumped (e.g. `[0.1.0,)` → `[0.1.1,)`) and `packages.lock.json` regenerated, otherwise restore keeps resolving the old version. Same applies in reverse for downstream consumers of Modules packages.

## Cutting a release

1. **Make sure NexusKit is at the version you expect**. If your PR depends on a new NexusKit feature, NexusKit must be released first (see "Cross-repo coordination" in NexusKit's [RELEASING.md](https://github.com/NexusFFXIV/NexusKit/blob/main/RELEASING.md)).

2. **Verify `main` is green**:
   ```powershell
   git fetch origin
   git checkout main; git pull
   gh run list --limit 1
   ```

3. **Pick the version** per SemVer (patch / minor / major):
   ```powershell
   git describe --tags --abbrev=0
   ```

4. **Tag with annotation**. First line becomes the Release title:
   ```powershell
   git tag -a v0.2.0 -m "v0.2.0 — adds PluginBridge for Visibility, fixes Lodestone retry"
   git push origin v0.2.0
   ```

5. **CI auto-publishes** `.github/workflows/ci.yml`:
   - Restore (pulls NexusKit NuGets from GitHub Packages)
   - Build in Release config
   - `dotnet pack` each project (6 `.nupkg` + 6 `.snupkg`)
   - Push to GitHub Packages at `https://nuget.pkg.github.com/NexusFFXIV/`
   - Create a GitHub Release with auto-generated notes and the `.nupkg`/`.snupkg` files attached

6. **Verify**:
   - Release: `https://github.com/NexusFFXIV/NexusKit.Modules/releases/tag/v0.2.0`
   - Packages should appear **public** in `https://github.com/orgs/NexusFFXIV/packages?repo_name=NexusKit.Modules`

## Cross-repo coordination

If you bumped Modules and the Plugin consumes a new API:

1. **NexusKit** (if upstream changes needed): land + tag → 7 NuGets out
2. **NexusKit.Modules** (this repo): pull, adapt, land via PR, tag → 6 NuGets out
3. **PlayerNexusTracker**: pull, adapt, land, tag → Plugin zip released

The workspace's `Directory.Build.targets` lets you develop all three in parallel against source; only the tag step needs to be sequential.

## Hotfix releases

Same pattern as NexusKit's hotfix flow — branch off the released tag, fix, tag a patch version, push:

```powershell
git checkout -b hotfix/<thing> v0.2.0
# fix + commit + push
git tag -a v0.2.1 -m "v0.2.1 — hotfix: <description>"
git push origin v0.2.1
# Open separate PR to merge the fix into main so v0.3.0 doesn't regress
```

## Pre-release versions (testing builds)

Same mechanic as in NexusKit — tag with a suffix containing `-` to publish a pre-release:

```powershell
git tag -a v0.2.0-rc.1 -m "v0.2.0-rc.1"
git push origin v0.2.0-rc.1
```

NuGets land with the `-rc.1` suffix; the GitHub Release is automatically marked **Pre-release**. Pre-releases are ignored by `PackageReference` ranges by default (`[0.1.0,)` won't resolve to `0.2.0-rc.1`), so stable consumers keep building against the previous stable. Testers pin the pre-release explicitly:

```xml
<PackageReference Include="NexusKit.Modules.PlayerEnrichment" Version="0.2.0-rc.1" />
```

### Cross-repo testing cascade

A typical end-to-end testing flow when a change spans NexusKit and the plugin:

1. **NexusKit**: land + tag `v0.2.0-rc.1` → 7 pre-release NuGets in GHP
2. **NexusKit.Modules** (this repo): pull main, adapt to the new NexusKit API, **temporarily pin** the upstream pre-release in your csprojs:
   ```xml
   <PackageReference Include="NexusKit.Core" Version="0.2.0-rc.1" />
   ```
   land via PR, tag `v0.2.0-rc.1` → 6 pre-release NuGets
3. **PlayerNexusTracker**: pull, pin both upstream pre-releases, tag `v0.2.0-rc.1` → plugin pre-release lands in DalamudRepo's `DownloadLinkTesting` field, surfaced for testers who enabled the toggle in Dalamud Settings
4. Tester feedback comes in
5. **Promote to stable**: replace the pinned `Version="0.2.0-rc.1"` constraints with a bumped range floor (`Version="[0.2.0,)"`), merge that change, then tag all three repos with `v0.2.0` in the same order
   - Re-running `dotnet restore --force-evaluate` (or letting CI restore on the new branch) regenerates `packages.lock.json` against the new floor
   - Reverting all the way back to `[0.1.0,)` would re-resolve to `0.1.0` (NuGet picks the lowest of the range) — the floor must move forward
   - Stable plugin lands in DalamudRepo's `DownloadLinkInstall` — every player gets the update

If the cascade is too heavy for a small change (no NexusKit-side change needed), skip step 1 and tag only Modules + Plugin.
