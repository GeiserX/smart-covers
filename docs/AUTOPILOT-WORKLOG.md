# Autopilot Worklog — smart-covers

> Append-only journal of the autonomous loop. **Newest entry at the BOTTOM.** Running uninterrupted;
> no pings. Genuine human-decision items are parked in `DEFERRED-QUESTIONS.md`, not asked here.
> Each entry records what shipped + a tally + the test/CI/commit evidence, so a fresh session (or Sergio)
> can resume from the bottom of this file alone.

## TL;DR for Sergio (read this first — updated each cycle)

- **Where it stands:** CBZ/CBR extraction implemented + 190/190 tests green locally; review pass next, then PR.
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
- **Next:** push branch + open PR (Closes #17). Note: repo ruleset requires the `build` check, which
  only runs on push to main — same catch-22 as the previous release PR; merging will need Sergio.
