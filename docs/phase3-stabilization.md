# Phase 3: estimator accuracy and queue presentation

## Scope and baseline

- Branch: `develop`
- Starting HEAD: `9e07f2d7ba79be5200c57248ab695b353bddc743` (`Stabilize fast estimates and real-media encoding`)
- Starting `origin/develop`: `676d6a8582df504b3cfb3ce673c044d4aa8127d0`
- Starting branch distance: 3 ahead, 0 behind
- Version: 1.8.0
- Phase 1: `7c1e4a3cd2b899cf552d7aa0c800342227051cba`
- Phase 2: `9e07f2d7ba79be5200c57248ab695b353bddc743`
- The Phase 2 worktree was clean. No release metadata or publishing operation was in scope.

Before edits, the Release build succeeded with 0 warnings and 0 errors. The focused Phase 2 smoke set passed 77 tests, with 0 failures and 0 skips. Phase 2 reported 2,372 full Release tests passed, 0 failed, and 2 skipped.

## Deterministic estimator before this change

`SizeEstimateService.EstimateAutoTargetMbSmartDetailed` calculates a video bitrate from cached source metadata and selected output settings. It does not encode or probe media itself.

Let `S` be source size in MiB, `T` duration in seconds, source dimensions be `W × H`, frame rate be `F`, and let measured bitrates be in kbit/s.

1. Essential source size, duration, dimensions, and frame rate must be positive; otherwise the result is unavailable. “No Compression” returns `S` directly.
2. The source total rate is `Rtotal = S × 8388.608 / T`. This converts binary MiB to kbit/s and includes audio, other streams, and container bytes. FFprobe’s reported format bitrate is diagnostic only.
3. Measured audio, subtitle, and data rates are capped at `Rtotal`. Mapped subtitle rate is measured subtitle rate, or 8 kbit/s per subtitle stream when the rate is absent. Data rate uses the measured rate or 8 kbit/s per data stream. Attachment bytes become `8 × bytes / 1000 / T` kbit/s.
4. Source container allowance is `max(16, 0.01 × Rtotal)` kbit/s. Measured source video is capped at `max(1, Rtotal − source ancillary rate)`. If video bitrate is absent, video is derived from total minus known audio/ancillary/container, or falls back to `max(1, 0.90 × Rtotal − ancillary)` when audio is also unknown.
5. Planned audio is copied measured audio unless an output channel count is selected. A selected channel count budgets 192 kbit/s per audio stream below six channels, or 384 kbit/s per stream at six or more. If the measured audio rate is missing but audio streams exist, the residual total after video, ancillary, and container is used.
6. Source density is `sourceBpp = 1000 × sourceVideoKbps / (W × H × F)`. Nominal codec densities are AVC/H.264 0.085, HEVC/H.265 0.055, AV1 0.045, MPEG-2 0.14, and 0.10 for other codecs. Content complexity is `clamp(sqrt(sourceBpp / nominalSourceBpp), 0.55, 1.65)`.
7. Output height is the selected positive target height or source height. Output width preserves source aspect ratio, rounds to the nearest integer, is at least 2, and rounds odd widths up to an even value. Frame rate is unchanged. Target pixel rate is `outputWidth × outputHeight × F`.
8. Effective quality is the selected storage-quality value or the provided quality clamped to 0–51. The constant-quality scale is `2^((23 − effectiveQuality) / 12)`. The profile factor is Very High 0.95, High 0.85, Medium 0.75, Low 0.65, Very Low 0.55, and No Compression 1.00.
9. Target video rate is:

   `targetVideoKbps = outputPixelsPerSecond × targetCodecNominalBpp × profileFactor × qualityScale × complexity / 1000`.

   Storage bitrate mode instead uses the configured percentage of source video bitrate. Storage quality mode uses its selected quality value and a profile factor of 1. Automatic quality intent is resolved upstream by the selected encoder’s quality policy; target offsets are Smaller File +6, Efficient +3, Balanced 0, High Quality −3, and Maximum Quality −5, with source/resolution adjustments. The resulting CQ/CRF/ICQ value is then used in step 8.
10. Before Phase 3, eligible Source Adaptive same-codec jobs at unchanged resolution had only an upper bound at measured source video bitrate. Manual/legacy same-codec jobs retained a full-source-bitrate lower bound. “Same efficient codec” means equal nominal BPP no greater than 0.055 and unchanged dimensions; this includes HEVC and AV1, not H.264. Conversion, scaling, unavailable measured bitrate, storage mode, or other ineligible cases bypassed the Source Adaptive ceiling.
11. Planned output rate is `subtotal = max(0, targetVideoKbps) + max(0, plannedAudioKbps + mappedSubtitleKbps)`. Container allowance is `subtotal / 0.99` when that allowance is at least 16 kbit/s; otherwise it is `subtotal + 16` kbit/s.
12. Estimated size is `max(0.1, targetTotalKbps × T / 8388.608)` MiB. The fixed 16 kbit/s allowance and 0.1 MiB minimum disproportionately affect very short/small outputs.

The deterministic BPP equation does not directly use source bit depth, encoder family, encoder preset, or content features such as motion/grain. Hardware/software encoder choice can change the resolved quality value upstream, but there is no per-encoder bitrate coefficient in this size formula. Audio is modeled by rate and copy/transcode channel budget, not by codec-specific compression behavior. Optional historical calibration remains outside the required fast arithmetic estimate; this phase adds no database/history lookup.

### Assumptions retained

- A single square-root density term is used as a proxy for content complexity even though bitrate and codec alone do not describe scene motion, grain, denoising, or encoder behavior.
- The same CQ exponent and legacy profile multiplier are applied across codec and encoder families. For ordinary automatic quality, a resolved CQ/CRF can therefore still be multiplied by the legacy Medium profile factor when storage mode is off. Removing that factor globally worsened one of the near-matching H.264 cases, so it was retained.
- Source/container/audio rates and the fixed overhead are estimates. Mapped data/attachments are excluded from the output video/audio subtotal; subtitle mapping is determined by the encode plan.
- No generic H.264→HEVC coefficient was changed. The H.264 media evidence showed opposite prediction behavior by content, so a global multiplier would fit one source while worsening another.

## Real-media validation matrix

The primary matrix used six safe copies of representative real media and 10 validated visible MainForm encodes. Target was NVENC HEVC 10-bit, preset p5; source audio was copied. A and B were each encoded at Balanced, High, and Maximum. C-low, C-long, D, and E were Balanced. “Predicted” is the deterministic queue estimate captured before encoding; “actual” is the finalized and validated output size. Signed error is `(predicted − actual) / actual × 100`.

| Source | Source facts | Target / quality |
|---|---|---|
| A: landscape | H.264 8-bit; 6,201,050 bytes; 25.600 s; 1920×1080 at 30 fps; video 1,801.277 kbit/s; audio 128 kbit/s | HEVC NVENC 10-bit; Balanced CQ25, High CQ22, Maximum CQ20 |
| B: low resolution, audio heavy | H.264 8-bit; 5,723,206 bytes; 31.431 s; 854×470 at about 30 fps; video 1,129.840 kbit/s; audio about 320 kbit/s | HEVC NVENC 10-bit; Balanced CQ24, High CQ21, Maximum CQ19 |
| C-low: short/low-rate HEVC | HEVC Main10; 2,994,287 bytes; 31.465 s; 854×470 at 29.970 fps; video 432.899 kbit/s; audio about 320 kbit/s | HEVC NVENC 10-bit; Balanced CQ24 |
| C-long: phone HEVC | HEVC Main10; 109,001,448 bytes; 99.032 s; 1920×1080 at 29.99 fps; video 8,539.864 kbit/s; audio 202 kbit/s; data streams about 47 kbit/s excluded from output | HEVC NVENC 10-bit; Balanced CQ25 |
| D: portrait | H.264 8-bit; 16,550,769 bytes; 31.556 s; 608×1080 at 30 fps; video about 4,064 kbit/s; audio about 129 kbit/s | HEVC NVENC 10-bit; Balanced CQ25 |
| E: 720p short | H.264 8-bit; 2,054,797 bytes; 11.667 s; 1280×720 at 30 fps; video about 1,213 kbit/s; audio about 131 kbit/s | HEVC NVENC 10-bit; Balanced CQ24 |

| Run | CQ | Before: predicted / actual bytes | Before error | After: predicted / actual bytes | After error |
|---|---:|---:|---:|---:|---:|
| A Balanced | 25 | 4,730,258 / 12,762,519 | −62.94% | 4,730,258 / 12,762,519 | −62.94% |
| A High | 22 | 5,542,293 / 18,102,004 | −69.38% | 5,542,293 / 18,102,004 | −69.38% |
| A Maximum | 20 | 6,170,347 / 22,092,717 | −72.07% | 6,170,347 / 22,092,717 | −72.07% |
| B Balanced | 24 | 3,254,763 / 2,994,287 | +8.70% | 3,254,763 / 2,994,287 | +8.70% |
| B High | 21 | 3,620,812 / 3,670,494 | −1.35% | 3,620,812 / 3,670,494 | −1.35% |
| B Maximum | 19 | 3,902,560 / 4,243,988 | −8.04% | 3,902,560 / 4,243,988 | −8.04% |
| C-low Balanced | 24 | 2,810,040 / 2,799,763 | +0.37% | 2,853,890 / 2,799,763 | +1.93% |
| C-long Balanced | 25 | 47,677,432 / 104,681,985 | −54.45% | 98,620,285 / 104,681,985 | −5.79% |
| D Balanced | 25 | 5,015,709 / 6,151,155 | −18.46% | 5,015,709 / 6,151,155 | −18.46% |
| E Balanced | 24 | 1,342,216 / 6,851,198 | −80.41% | 1,342,216 / 6,851,198 | −80.41% |

The Phase 2 five-second Dad2 observation remains separate: 894,084 predicted versus 2,199,168 actual bytes (−59.34%). Its full source metadata/quality tuple was not retained with this Phase 3 temp matrix, so it is not included in the primary six-source/10-run statistics. It is included below only as a short-duration warning alongside E.

## Accuracy summary

| Cohort | n | Before MAPE | After MAPE | After median absolute | After worst absolute |
|---|---:|---:|---:|---:|---:|
| Primary 10-run matrix | 10 | 37.62% | 32.91% | 13.58% | 80.41% |
| H.264→HEVC | 8 | 40.17% | 40.17% | 40.70% | 80.41% |
| HEVC→HEVC | 2 | 27.41% | 3.86% | 3.86% | 5.79% |
| Balanced | 6 | 37.55% | 29.70% | 13.58% | 80.41% |
| High | 2 | 35.37% | 35.37% | 35.37% | 69.38% |
| Maximum | 2 | 40.06% | 40.06% | 40.06% | 72.07% |
| Longer than 12 seconds | 9 | 32.86% | 27.63% | 8.70% | 72.07% |

On the primary 10 runs, mean signed error changed from −35.80% to −30.78%, and median signed error changed from −36.46% to −13.25%. The Dad2-inclusive 11-row engineering summary is: mean signed −33.38%, median signed −18.46%, MAPE 35.31%, median absolute 18.46%, worst absolute 80.41%. Including Dad2 and E as the short cohort gives n=2, MAPE 69.88%, median absolute 69.88%, worst 80.41%; this is descriptive only. All remaining rows longer than 12 seconds have n=9. These small samples do not support statistical-significance claims.

Quality behavior differs by content: A worsened from −62.94% at CQ25 to −72.07% at CQ20, while B stayed within +8.70% to −8.04% across its three CQ values. A shared H.264→HEVC multiplier would reduce B’s already-small estimates and is not supported by this evidence. The clear systematic failure was C-long: same-codec, same-resolution output was near source size while the original model projected less than half that size. The 90% source-video floor fixes that case without lifting C-low materially. E remains an 80.41% short-source miss, demonstrating that deterministic metadata cannot resolve all content/short-clip behavior.

## Model change

For eligible Source Adaptive same-efficient-codec, unchanged-resolution jobs with a measured source video bitrate and storage savings disabled, target video rate is now bounded as:

`targetVideoKbps = clamp(modelTargetVideoKbps, 0.90 × sourceVideoKbps, sourceVideoKbps)`.

The upper bound preserves the existing no-upward-same-codec protection. The new 90% lower bound only replaces a model output below that floor. It uses measured source video rate and the existing eligibility boundary; it does not add sampling, source probing, history access, per-file search, or a new state store. Legacy/manual behavior remains at its existing full source-video floor. H.264→HEVC and scaled jobs remain unchanged.

For C-low, the new target is 389.609 kbit/s (90% of 432.899); with copied 320 kbit/s audio and the existing small-rate overhead rule, predicted output becomes 2,853,890 bytes. For C-long, the target is 7,685.1 kbit/s (90% of 8,539 kbit/s); with copied 202 kbit/s audio and existing 1% overhead, predicted output becomes 98,620,285 bytes. The run table above uses the same actual files; post-change “actual” values are unchanged pre-change validated bytes, not a post-change encode.

## Queue and Recommendation presentation

- The visible queue estimate column is labeled **Estimated Output** and contains only the formatted size (or a neutral estimating/unavailable/skip state). It no longer appends savings percentages, provisional/low-confidence markers, CQ profile, empirical forecast, or calibration/provenance text.
- Estimated Output tooltips contain only available output size, savings/increase percentage, quality target, and encoder family/codec. Recommendation tooltips show the recommendation, expected savings, and one primary reason; confidence and internal estimator reasons remain in Plan & Analysis/Inspector.
- Pending recommendation cells show an em dash with no tooltip. Pending and unavailable advisory results do not count as queue Attention. Actionable source or transformation issues (interlace, upscale, unusual streams, likely animation, audio-heavy input, inefficient target) can still be Review.
- Generic BPP low confidence and deep-sample disagreement now lower confidence or remain in technical reasons; neither creates Review by itself. Missing essential metadata remains Unavailable.
- Recommendation-based Start prompts and candidate-only filtering were removed. Queue scope, duplicate exclusion, explicit preflight, and production safety checks remain separate from advisory classifications. The old warning setting remains readable in existing config files but no longer has UI or runtime effect.
- Inspector retains quality resolution, estimate evidence/diagnostics, confidence, and recommendation reasons. Media details remain cache-only; opening Inspector does not start a probe.

## Performance and validation limits

Phase 2 observed approximately 88–107 ms to estimate-ready, 184–201 ms to visible, and under 0.7 ms for estimator arithmetic. The Phase 3 change is one `Math.Clamp`-equivalent pair of comparisons in the already-running arithmetic; it adds no I/O. A post-change visible quality-change timing run was not completed because native Windows input returned repeated access-denied/Browse failures and the computer-use recovery policy required stopping desktop input. The additional visible HEVC High/Maximum matrix runs, post-change queue visual/tooltip inspection, and post-change visible end-to-end encode are likewise unverified.

All primary pre-change encodes were visible MainForm runs and passed MediaFlux’s output verification/finalization. The source copies and outputs are retained in `C:\Users\ghirschberg\AppData\Local\Temp\MediaFlux-Phase3-20261004`. Post-change model predictions were replayed from the captured media metadata and estimator formula; they are arithmetic projections, not new output measurements.

## Remaining limitations and Phase 4 direction

- The 90–100% bound addresses one narrow same-codec underprojection pattern; it does not repair H.264→HEVC content-dependent misses or the E short-source miss.
- Larger, stratified real-media validation should cover grain/motion/detail, frame-rate tiers, source bit depth, audio layouts, and software versus hardware encoders. Predictions and actuals should be paired without turning advisory evidence into production acceptance.
- Evaluate a small, fast, source-metadata/content descriptor only if it can be computed from already-cached evidence. Do not add synchronous sampling or history dependency to ordinary quality changes.
- Re-run visible warm quality timing, actual queue/Inspector inspection, and one post-change real encode after native desktop control is restored.
