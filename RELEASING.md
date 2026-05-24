# Releasing NexusKit.Modules

NexusKit.Modules follows [Semantic Versioning](https://semver.org/). Versions are derived from git tags via [MinVer](https://github.com/adamralph/minver).

All 6 packages in this repo ship with the **same version**. Floating `PackageReference` constraints (`[0.1.0,)`) point at NexusKit, so consumers of Modules pick up NexusKit patch updates automatically; pin in `Directory.Packages.props` if your consumer wants reproducible builds.

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
