# Deferred Questions — smart-covers

> Items that would normally be a question to Sergio. The loop takes a reversible default and keeps
> moving; change the default here if you disagree.

## 0. OPEN — merge approval for PR #18 (2026-07-03)

- **Context:** https://github.com/GeiserX/smart-covers/pull/18 (CBZ/CBR cover support, closes #17) is
  ready: required `build` check green on the PR, 191/191 tests, review pass done. Merging is the one
  action the loop never takes without explicit approval.
- **Default taken:** none — waiting. No merge happens autonomously.
- **When approved:** squash-merge → push-to-main run auto-releases v7.3.0.0 → update `manifest.json`
  (documented manual step) → post explanation on issue #17 and verify it closes.

## 1. New NuGet dependency for CBR support (2026-07-03)

- **Context:** Repo CLAUDE.md lists "Add NuGet dependencies" under *Ask First*. Issue #17 asks for CBR
  (RAR comic archive) support; .NET has no built-in RAR reader, so the feature intrinsically requires a
  RAR-capable library (candidate: SharpCompress — MIT, pure managed).
- **Default taken:** Added `SharpCompress` (MIT, pure managed, single DLL, no transitive packages),
  pinned to **0.49.1 — the exact version Jellyfin 10.11 itself ships**. Rationale: plugin dependencies
  resolve through Jellyfin's Default ALC first, so at runtime the plugin binds the server's own copy;
  compiling against the same version rules out API drift. The bundled DLL is only a fallback for future
  Jellyfin versions (a `Resolving` handler in `Plugin.cs` wires it). MIT notice added in
  `THIRD-PARTY-NOTICES.md`. The directive to implement issue #17 is read as covering its intrinsic
  dependency.
- **To change:** Drop the dependency and ship CBZ-only (zip is handled by System.IO.Compression in-box),
  answering the issue that CBR needs a dependency Sergio declined. One commit to revert.
