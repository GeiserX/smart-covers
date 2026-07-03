# Autopilot Worklog — smart-covers

> Append-only journal of the autonomous loop. **Newest entry at the BOTTOM.** Running uninterrupted;
> no pings. Genuine human-decision items are parked in `DEFERRED-QUESTIONS.md`, not asked here.
> Each entry records what shipped + a tally + the test/CI/commit evidence, so a fresh session (or Sergio)
> can resume from the bottom of this file alone.

## TL;DR for Sergio (read this first — updated each cycle)

- **Where it stands:** **GOAL MET.** PR #18 merged (Sergio approved), **v7.3.0.0 released**,
  manifest.json updated, **issue #17 answered and closed**. Loop complete; ralph state torn down.
- **Loop:** /research! → /implement! → /review-pr! → (back to research)
- **Open vs closed:** 0 closed / 1 open _(update each entry)_

## Plan of attack (dependency order)

1. Research: how Jellyfin resolves `.cbz`/`.cbr` items, how the current provider gates formats, and the safest RAR-read path for a Jellyfin plugin (dependency-loading constraints).
2. Implement: CBZ (zip) + CBR (rar) first-image cover extraction wired into `CoverImageProvider`, config/status surfaces, docs.
3. Review: full review pass, regression tests, PR with CI green.

## Log

### Entry 1 — loop kickoff (2026-07-03)

- Scaffolded the loop docs (`GOAL.md`, this worklog).
- **Next:** research phase — 3 parallel researchers (codebase gating, Jellyfin/Bookshelf comic handling, RAR-in-.NET options).
- **Tally:** 0 closed / 1 open (issue #17).
- **CI / tests:** _(none yet — record `suite N pass / M skip / 0 fail`, CI run id, and commit SHA from the next real entry on)_

### Entry 2 — research + implementation done, 190/190 green (2026-07-03, WIP — review next)

- **Research findings (3 parallel researchers):** `.cbz`/`.cbr` resolve as `Book` items in core
  (`BookResolver`), core does NOT extract comic covers (plugin territory); Bookshelf uses
  `ArchiveFactory` + lexicographic first-image (weaknesses: no natural sort, root-only cover match —
  both beaten here); Jellyfin ships SharpCompress 0.49.1 and plugin deps bind the server's copy via
  Default-ALC fallback → pinned csproj to 0.49.1.
- **Implemented:** `.cbz`/`.cbr` routing in `CoverImageProvider` via SharpCompress `ArchiveFactory`
  (content-sniffed → mislabeled archives work), cover-named entry preferred, else first page by new
  `NaturalStringComparer` (page-2 < page-10); junk (`__MACOSX/`, dot-files) + tiny (<1 KB) skipped;
  magic-byte verification per candidate; solid archives use the forward reader (random access throws on
  solid — found by test, confirmed standalone), non-solid use random access (`ExtractAllEntries` throws
  on non-solid in 0.49.x). BMP magic detection added. `Plugin.cs` Resolving handler branch for
  SharpCompress (future-proofing). CI bundles `SharpCompress.dll` (verified present in publish output);
  whitelist unchanged. Version 7.3.0.0 (csproj + build.yaml). Docs: README (formats table, comic
  section, troubleshooting), CLAUDE.md (pipeline + 4 learned patterns), THIRD-PARTY-NOTICES.md (new).
- **Tests:** +16 (ComicCoverTests ×9 CBZ runtime-zips + ×5 CBR binary fixtures, NaturalStringComparer
  suite, BMP detect). Fixtures committed: hand-crafted stored RAR4 (RAR 7.x can't author RAR4), RAR5,
  solid RAR5, zip-as-.cbr, rar-as-.cbz + `make-fixtures.py` regenerator. All comic tests mock `Audio`
  so the online fallback can never mask a local regression (caught a real one: a live network fetch had
  turned a solid-RAR failure into a false pass).
- **Tally:** 0 closed / 1 open (issue #17 — pending review + PR + release).
- **CI / tests:** local suite **190 pass / 0 skip / 0 fail**; CI not yet run (PR next).
- **Next:** code-review pass → fix findings → PR referencing #17 (merge needs Sergio's approval).

### Entry 3 — review pass done (inline), 191/191 green, PR next (2026-07-03)

- Spawned code-reviewer + security-reviewer subagents; **both died on the session usage limit**
  (resets 04:50 Europe/Madrid), so the review ran **inline under security-review mode** instead —
  adversarial pass over the full diff.
- **Security sweep (clean):** no disk-write extraction APIs anywhere (zip-slip surface = zero; the
  SharpCompress path-traversal CVEs incl. CVE-2026-44788 are `WriteToDirectory`-only — not used);
  entry keys never used to build filesystem paths; decompression-bomb guard enforced *while copying*
  (64 MB cap; headers can lie) with pre-filtering by header size; per-chunk cancellation; OCE never
  swallowed; 29× `ConfigureAwait(false)`; no secrets/PII. Known accepted limitation: bailing out of a
  solid-archive entry still pays a decompress-and-discard on stream dispose (CPU-bounded, memory
  stays capped) — strictly better posture than the pre-existing EPUB path, which has no cap at all.
- **Finding fixed (Medium):** tiny/oversize entries were skipped only at extraction time — *after*
  `Take(5)` — so junk could consume all candidate slots and leave a real page untried. Size gate
  moved into the image-entry filter (unknown sizes still pass; in-copy guards remain the authority).
  Regression test added: 8 tiny junk images ahead of a real page → cover still found.
- **Verified explicitly:** `NaturalStringComparer` is a total order (tuple-lexicographic over digit-run
  tokens: value → zero-count tiebreak; adversarial triples hold transitivity); response-stream
  ownership passes to Jellyfin per house pattern; `Resolving` handler is safe under concurrent misses
  (ALC dedupes same-path loads); 7.3.0.0 consistent across csproj/build.yaml; SharpCompress.dll
  confirmed in publish output; meta.json whitelist untouched. Hygiene: dropped an unused using.
- **Tally:** 0 closed / 1 open.
- **CI / tests:** local suite **191 pass / 0 skip / 0 fail**; commit amended on feat/cbz-cbr-covers.
- **Next:** push branch + open PR (Closes #17).

### Entry 4 — PR #18 open, CI green on the PR itself (2026-07-03)

- **PR:** https://github.com/GeiserX/smart-covers/pull/18 (branch `feat/cbz-cbr-covers`, Closes #17).
- Main had moved since the branch point by 3 CI-only commits (#14 run build on PRs + gate release steps
  to push, #15 GitHub-hosted ubuntu-latest, #16 checkout v7). Only `build.yml` overlaps and the hunks
  compose cleanly — the PR is MERGEABLE and its merge-ref run used the combined workflow.
- **The PR-#12 catch-22 no longer exists:** the required `build` check ran ON the pull request and is
  **green** — `check-version` ✓, restore/build ✓, **tests ✓ (the full suite passed on CI)**, package ✓
  (with `SharpCompress.dll`), checksum ✓; release/Pages steps correctly **skipped** on the
  pull_request event (verified: no new tag, no new release — v7.2.0.0 still latest). GitGuardian ✓;
  CodeRabbit pending (not blocking per standing practice).
- One scare handled: before inspecting main's new workflow I believed the PR run might publish
  v7.3.0.0 prematurely (my stale local copy of `build.yml` had no event gates); cancel arrived after
  completion — but the gates on main meant nothing was released. Verified against tags + releases.
- **Tally:** 0 closed / 1 open (issue #17 — PR awaits Sergio's merge approval; then release + manifest
  + issue answer).
- **CI / tests:** PR run 28648749685 **success** (build+tests+package); local 191/0/0.
- **Next:** Sergio merges → push-to-main run tags + releases v7.3.0.0 → update `manifest.json`
  (documented manual step) → comment + close issue #17.

### Entry 5 — GOAL MET: merged, released, issue closed (2026-07-03)

- Sergio approved; **PR #18 squash-merged** to main as `4c0fbcb` (branch deleted). Normal merge — the
  required `build` check was green on the PR itself.
- Push-to-main run released **v7.3.0.0**: tag + GitHub Release with `smart-covers_7.3.0.0.zip`
  (45,036,408 bytes). Zip contents verified by download: `SmartCovers.dll`, `PDFtoImage.lib`,
  **`SharpCompress.dll`**, `meta.json` (whitelist intact), pdfium natives.
- **`manifest.json` updated to 7.3.0.0** (checksum `bb98ba92b4022fa3ea67ae55eb1468aa`, timestamp
  2026-07-03T09:03:00Z) and pushed to main — the documented manual completion; Pages already serves
  the in-run generated manifest.
- **Issue #17: auto-closed by the merge (COMPLETED)** + explanation comment posted thanking the
  reporter.
- **Tally:** **1 closed / 0 open.** Loop complete → ralph state cleared; stopping.
- **CI / tests:** release run success; local 191 pass / 0 skip / 0 fail.
