<!--
Thanks for the PR. A quick summary + how you verified it goes a long way.
See CONTRIBUTING.md and RELEASING.md for the full workflow.
-->

## Summary
<!-- 1–3 sentences: what changes and why? -->

## Changes
<!-- Optional bullet list if the summary doesn't cover details -->

## Module(s) touched
<!-- Which module(s) does this PR affect? -->
- [ ] InternalData
- [ ] ExternalData
- [ ] PlayerEnrichment
- [ ] FfxivCollect (External)
- [ ] Lodestone (External)
- [ ] PluginBridge (External)

## Test plan
- [ ] `dotnet build NexusKit.Modules.sln -c Release` is green
- [ ] (if relevant) `dotnet test ../localTools/tests/NexusKit.Modules.ExternalData.Tests` is green
- [ ] (if UI/runtime change) manual smoke test in a Dalamud-loaded build

## Notes for reviewer
<!-- Optional: anything that needs special attention -->

## Breaking change?
<!-- Yes / No. If yes, what consumers (PlayerNexusTracker) need to follow? Does NexusKit need a bump first? -->
