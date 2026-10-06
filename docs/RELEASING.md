# Releasing Sightline

`VERSION` is the one version. Every build reads it: Gradle, every .NET project, and the release workflows.

## Two cadences

| Trigger | What is published | Version |
| --- | --- | --- |
| A merge to `main` | The rolling prereleases `latest-windows` and `latest-android`, their files replaced each time | `<VERSION>-rolling.<run>.g<commit>` |
| A push of the tag `v<VERSION>` | A release that never changes, with both platforms' files | `<VERSION>` exactly |

A tag that names anything but `VERSION` fails the release, so a download can never be named one thing and stamped another. The rolling version is ordered by GitHub's run number, which only counts up, so a newer rolling build always reaches installed copies. `tools/scripts/release_version.py` does this, and its tests say so.

To cut a release: raise `VERSION` in a commit of its own, merge it, then tag that commit `v<VERSION>` and push the tag.

## What each release carries

Windows (`.github/workflows/release-windows.yml`, built by `tools/scripts/package.ps1`):

- `Sightline-Setup.exe`, `Sightline-Portable.exe`, `sightline-cli.exe`, and each under a name with its version;
- the Velopack feed, `*.nupkg` and `releases.win.json`, which installed copies update from;
- `windows-release.json`, each download's size and SHA-256, and `SHA256SUMS.txt`.

Android (`.github/workflows/release-android.yml`):

- `Sightline.apk` and `Sightline-<version>.apk`, signed with the release key, and R8's mapping file;
- `android-release.json` and `SHA256SUMS.txt`.

The site offers the newest tagged release; until there is one, it links to the releases page.

## The Android signing key

A phone takes an update only when it is signed with the key the installed app was signed with, so the key must never change and must never be lost. The release workflow refuses to run without it, and refuses to publish an APK signed with a debug key.

Creating it is the owner's job, once:

1. Generate it somewhere outside every repository, and back it up somewhere safe:

   ```powershell
   keytool -genkeypair -keystore $HOME/.rex/keys/sightline.jks -alias sightline -keyalg RSA -keysize 4096 -validity 36500
   ```

2. Give the repository the four secrets the workflow reads:

   ```powershell
   gh secret set SIGHTLINE_KEYSTORE_BASE64 --body ([Convert]::ToBase64String([IO.File]::ReadAllBytes("$HOME/.rex/keys/sightline.jks")))
   gh secret set SIGHTLINE_KEYSTORE_PASSWORD
   gh secret set SIGHTLINE_KEY_ALIAS --body sightline
   gh secret set SIGHTLINE_KEY_PASSWORD
   ```

Locally, a release build is signed when `SIGHTLINE_KEYSTORE_PATH`, `SIGHTLINE_KEYSTORE_PASSWORD`, `SIGHTLINE_KEY_ALIAS` and `SIGHTLINE_KEY_PASSWORD` are all set, and built unsigned otherwise.

## Windows signing

None yet. Windows warns that the publisher is unknown, and every place that offers a download says so.
