# MediaFlux Development Workflow

MediaFlux uses a two-branch release model.

- `main` represents production/stable code and published release boundaries.
- `develop` is the integration branch for ongoing development.
- Focused work should be performed on short-lived branches and merged into `develop` after validation.
- Release candidates are promoted from an exact tested `develop` commit to `main` and tagged there.

## Pull request validation

Pull requests targeting `develop` or `main` should pass the MediaFlux CI workflow. CI restores the solution, builds it in Release configuration, and runs the automated test suite on Windows with .NET 8.

Do not merge a change that weakens staged-output validation, final verification, fail-closed source deletion, Library Analyzer reconciliation safeguards, or updater/user-data preservation without explicit replacement coverage.

## MediaFlux 1.9.0 Automatic queue

Automatic prepares one deterministic V1 production plan. The source-aware planner uses source video bitrate, codec complexity, output geometry and frame rate, then budgets mapped audio, ancillary streams and container overhead. Its five bitrate scales are Smaller `0.78`, Efficient `0.90`, Balanced `1.00`, High Quality `1.12`, Maximum Quality `1.25`; the selected target controls VBR rate settings, while the displayed output size is a metadata projection. The completed output size comes from the validated, finalized file.

This restores two earlier principles from v0.1.20/v0.1.50: prefer source video bitrate and media metadata over a fabricated fixed savings percentage, and keep quality intent separate from storage policy. Automatic therefore publishes a target-based projection and needs no empirical CQ sample. Manual/Legacy Numeric keeps its CQ/CRF compatibility and supported measured previews. Storage Savings and recommendations remain downstream policy; they cannot change Automatic target bitrate, select CQ, manufacture an estimate, or delay Start Encode. A low-bitrate efficient HEVC/AV1 source can require Review when the projection is unfavorable. Missing required metadata remains explicitly unavailable.

The GUI queue validates source identity, estimate generation, prepared-plan identity, and fingerprint before its single atomic production claim. Estimates and dispatch use that prepared plan; changes to quality or source invalidate the prior binding. NVENC target plans use their planned bitrate/maxrate/VBV settings, software HEVC maps the same target to its bitrate controls, and Manual continues to use provider-normalized CQ/CRF settings. Output validation precedes finalization/promotion, recovery remains classified and bounded, and source deletion stays disabled unless the existing explicit, fail-closed deletion gates are enabled.

For a headless queue load run, prepare an isolated UserData directory containing `config.json`, an already-existing output directory, and 300 unique small `.mp4` fixture files in the fixture directory. Then run the Release executable (use `--count 100` for the 100-row pass):

```text
MediaFlux.exe --benchmark-queue-estimates --source-directory <fixture-directory> --user-data <isolated-user-data> --output-directory <existing-output> --count 300 --quality-mode Automatic --metadata-replay
```

`--metadata-replay` supplies deterministic source facts to each unique fixture path without starting FFprobe; it is intended for 100/300-row scheduling measurements. The JSON reports unique sources, admission and work-scheduling time, published estimate-state counts and ready-plan latency percentiles, completed measured estimates, metadata probe source/count/cache hits/coalesced waiters/peak concurrency, and empirical sample counts. Scheduling or a published plan state is not a completed measured estimate. Automatic admission and five-profile recalculation should launch zero empirical sample encodes. Concurrent metadata coalescing is covered separately by `EmpiricalSizePreviewTests`. Use `QueueEstimateOwnershipTests` and `ProductionPlanFingerprintTests` for deterministic queue-handoff coverage; run the real headless handoff only with isolated UserData and disposable output. Full validation is `dotnet build MediaFlux.sln -c Release` followed by `dotnet test MediaFlux.sln -c Release --no-build`.

In v1.9.0, Automatic (the default) and Manual are the two GUI-selectable quality modes. Manual retains explicit CQ/CRF controls. Legacy Numeric is retired from normal GUI selection; historical saved settings remain compatible and retain their numeric quality and encoder settings. Keep compatibility handling on that historical path so it does not affect new Automatic or Manual work.

## Versioning

Public releases use semantic versions (`MAJOR.MINOR.PATCH`) with optional prerelease identifiers, for example `1.0.0-rc.1`. The release workflow supplies the validated release version to MSBuild and Velopack so the executable, package, tag, and GitHub Release use one version. The version in `MediaFlux.csproj` is the development/default version and is not the authoritative value for a tagged release.

## Release process

See `docs/releasing.md` for packaging mechanics and `docs/v1-release-readiness.md` for the v1.0 release gate.
