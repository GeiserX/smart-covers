# Development

## Build

The plugin targets .NET 9.0 and references `Jellyfin.Controller` and `Jellyfin.Model` pinned to 10.11.0, the
lowest patch of the supported line, so a build never depends on a newer patch than a user runs.

```bash
dotnet publish SmartCovers/SmartCovers.csproj -c Release -o publish
```

The output is in `publish/`. It is not yet what ships: see [What ships in the zip](#what-ships-in-the-zip).

## Test

```bash
dotnet test SmartCovers.Tests/SmartCovers.Tests.csproj --configuration Release
```

The suite covers each extractor (PDF, EPUB, MOBI, CBZ and CBR, audio, folder audiobooks, sidecar images), the
online fetcher and its retry order, the status endpoint, the scheduled task and the configuration. The CBR
fixtures under `SmartCovers.Tests/Fixtures/` are built by `make-fixtures.py` there.

## Formatting

The root `.editorconfig` pins the style rules; CI fails on a diff.

```bash
for project in SmartCovers/SmartCovers.csproj SmartCovers.Tests/SmartCovers.Tests.csproj; do
  dotnet format whitespace "$project" --verify-no-changes
  dotnet format style "$project" --verify-no-changes
done
```

## What ships in the zip

A release zip is flat and holds:

- `SmartCovers.dll` with SharpCompress merged into it by ILRepack. Jellyfin 10.11 ships no SharpCompress, and
  its plugin scanner resolves a plugin's referenced types before any plugin code runs, so a separate
  `SharpCompress.dll` makes the plugin load as NotSupported. Merging removes the reference.
- `PDFtoImage.lib`: the PDFtoImage managed library, renamed from `.dll` so the scanner skips it (it rejects the
  SkiaSharp version PDFtoImage was built against). `Plugin.cs` resolves it at runtime.
- `runtimes/<rid>/native/`: the PDFium native library for Linux (x64, arm64, musl), macOS and Windows.
- `meta.json` with an `assemblies` list naming only `SmartCovers.dll`, so Jellyfin loads nothing else.
- `build.yaml` and `THIRD-PARTY-NOTICES.md`.

To build the merged assembly by hand:

```bash
dotnet tool install -g dotnet-ilrepack
ilrepack /internalize /out:merged/SmartCovers.dll publish/SmartCovers.dll publish/SharpCompress.dll /lib:publish
```

To try it on a server, copy `merged/SmartCovers.dll`, `publish/PDFtoImage.dll` renamed to `PDFtoImage.lib`, and
`publish/runtimes/` into `<jellyfin-config>/plugins/SmartCovers_<version>/` and restart Jellyfin. The
[Build and Release workflow](https://github.com/GeiserX/smart-covers/blob/main/.github/workflows/build.yml) is
the reference for the exact library paths and the `meta.json` it writes.

## Release

The catalog manifest is edited by hand and never auto-committed.

1. Bump `<AssemblyVersion>` and `<FileVersion>` in `SmartCovers/SmartCovers.csproj` and `version` in
   `build.yaml`; they must match. Tags are `v7.4.0.0` style.
2. Merge to `main`. The Build and Release workflow builds, tests, merges SharpCompress, re-runs the suite against
   the merged assembly with `SharpCompress.dll` deleted, packages the zip, creates the GitHub release and prints
   the zip's MD5 in the job summary.
3. Prepend the new version to `manifest.json` with that checksum, commit and push to `main`. The Docs workflow
   publishes the committed `manifest.json` with the site, so the catalog updates within a minute. If the deploy
   hits a transient "try again later", re-run that workflow; no new release is needed.
4. Check the release at the catalog URL, not only the repo file:
   `curl -sf https://geiserx.github.io/smart-covers/manifest.json | python3 -m json.tool | head`.

## The documentation site

These pages are built with MkDocs Material from `docs/` and published by the Docs workflow: every pull request
runs the strict build, a push to `main` deploys.

```bash
pip install -r docs/requirements-docs.txt
mkdocs build --strict
mkdocs serve
```
