# MediaFlux Development Workflow

MediaFlux uses a two-branch release model.

- `main` represents production/stable code and published release boundaries.
- `develop` is the integration branch for ongoing development.
- Focused work should be performed on short-lived branches and merged into `develop` after validation.
- Release candidates are promoted from an exact tested `develop` commit to `main` and tagged there.

## Pull request validation

Pull requests targeting `develop` or `main` should pass the MediaFlux CI workflow. CI restores the solution, builds it in Release configuration, and runs the automated test suite on Windows with .NET 8.

Do not merge a change that weakens staged-output validation, final verification, fail-closed source deletion, Library Analyzer reconciliation safeguards, or updater/user-data preservation without explicit replacement coverage.

## Versioning

Public releases use semantic versions (`MAJOR.MINOR.PATCH`) with optional prerelease identifiers, for example `1.0.0-rc.1`. The release workflow supplies the validated release version to MSBuild and Velopack so the executable, package, tag, and GitHub Release use one version. The version in `MediaFlux.csproj` is the development/default version and is not the authoritative value for a tagged release.

## Release process

See `docs/releasing.md` for packaging mechanics and `docs/v1-release-readiness.md` for the v1.0 release gate.
