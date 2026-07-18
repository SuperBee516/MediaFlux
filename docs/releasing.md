# Releasing MediaFlux

MediaFlux uses Velopack packages hosted in GitHub Releases. Installed copies check the stable GitHub release channel through **Help > Check for Updates**.

## Before the first public release

1. Make `SuperBee516/MediaFlux` public, or publish the release assets from a separate public release repository and update `UpdateManager.RepositoryUrl` accordingly.
2. Confirm the repository has an appropriate license and public-facing documentation.
3. Configure code signing when a certificate or approved open-source signing service is available. Unsigned test releases are supported but can trigger Windows SmartScreen warnings.

Never place a GitHub access token in the MediaFlux application or its configuration. The workflow uses GitHub's short-lived repository token only while publishing a release.

## Create a release

The simplest path is **Actions > Build and Publish MediaFlux Release > Run workflow**. Enter a three-part semantic version such as `0.2.0`. A version containing a suffix, such as `0.2.0-beta.1`, is published as a pre-release and is not offered to normal installations.

The same workflow runs when a `v*` tag is pushed. It performs these tasks automatically:

1. Restore and build MediaFlux.
2. Publish a self-contained Windows x64 application.
3. Reject payloads containing user data, caches, logs, or PDB files.
4. Generate release notes from commits since the previous tag.
5. Create the Velopack installer, portable bundle, full update package, and delta package when a prior package exists.
6. Publish the GitHub Release and its update feed.

## Validate an update

Before announcing a stable release:

1. Install the prior version using `MediaFlux-Setup.exe`.
2. Create a new release with a higher version.
3. In the installed prior version, select **Help > Check for Updates**.
4. Confirm settings, history, presets, Explorer commands, and configured FFmpeg tools still work after restart.

Legacy ZIP-based copies are not Velopack installations. Their update command opens the Releases page so the installer can be run once. All later upgrades are handled inside MediaFlux.
