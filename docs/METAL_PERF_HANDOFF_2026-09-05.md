# TOTK on the Metal backend — performance line and open issues, state as of 2026-09-05


## Correctness update: v483 (2026-09-06)

The default guest texture-barrier policy is now `all`; `hazard` skipping is an explicit
diagnostic opt-in. Same-process campfire controls reproduced platform corruption twice
with hazard and eliminated it twice with all at about49FPS. Clean v483 passed600s mixed
play and user confirmation, with independent white flash still open. Do not re-enable
hazard as a default based on the older performance conclusions below. Its barrier-time
binding check cannot establish the absence of dependencies for later changed bindings.
See START_HERE and local v483-verification.md; no new source commit yet.


This is the hand-off for the work between v393 (2026-08-30) and v426 (2026-09-04). The
commit messages carry the per-change reasoning; this file is the map. Numbers are
scene-scoped: a result measured at one save says nothing about another (that lesson
cost a week once).

Test rig: M1 Max, TOTK 1.4.2, UltraCam mod (`sdcard/UltraCam/TOTK/Config/maxlastbreath.ini`),
render 1920x1080 (was 2560x1440 until 2026-09-04), `MaxFPS = 50` (45 before; 60 crashes
the guest - see below). Every stats block the backend logs ends with a `config:` field
naming the switches it ran under, so a log can always be told from another build.

## What is on by default now, and why

| Change | Commit / build | Effect (measured) | Switch |
|---|---|---|---|
| Self-reads of 8-bit colour attachments do not split the pass (Switch semantics: the read is allowed to see stale data) | e5d8c073, 1417564a, default since v400 | equal scene 27.8 -> 44.7 fps median (+61%), passes 530 -> 198, sync waits 2686 -> 193 ms per 120 frames | `RYUJINX_METAL_SKIP_SELF_SPLIT=0`, hot file `/tmp/ryujinx-metal-skip-self-split` |
| Same-pixel self-reads of the R32Float attachment are served by framebuffer fetch instead of a split | d5835430, 02f6962c, e186fff2 (v405-v409) | surface save passes 278 -> 186, GPU busy 81 -> 65%; a draw that writes the slot it reads is never fetched (intra-draw semantics differ - it painted black decals) | `RYUJINX_METAL_FB_FETCH=0`, hot file `/tmp/ryujinx-metal-fb-fetch` |
| Guest texture barriers end the pass only for a hazard the draw-time check would split on | ab5ad5ec, c0f67455, 74471eda (v407-v419) | after the v410/v411 corrections it saves about nothing (hill save 269 vs 273 passes, sky-island save 247 vs 245); kept as default on the user's decision, `all` is the conservative fallback | `RYUJINX_METAL_BARRIER_SCOPE=all`, hot file `/tmp/ryujinx-metal-barrier-scope` (`hazard`/`all`) |
| Crash ring: memory-mapped record of the render thread's last 32k events plus a separate 4k ring of buffer/program disposals | 9402007b, cbc2cd56 (v425/v426) | nanoseconds per event, no I/O; survives a segfault | `RYUJINX_METAL_CRASH_RING=0`; file `/tmp/ryujinx-metal-crash-ring.bin` |

The format whitelist for stale self-reads was reached by three user-visible failures and
should not be widened: R32F stale broke Ultrahand (a data read, not a picture), RG11B10
stale striped the water (refraction samples the HDR target at a displaced coordinate).
The same displaced-coordinate read is why RG11B10 was withdrawn from fetch (v408): the
patcher cannot prove a sample is same-pixel from the shader text alone, so only R32F
depth self-reads - same-pixel by construction - are fetched. Any colour target needs a
runtime same-pixel proof first.

## The frame-time model, and where it stops applying

Measured with the command buffers' own GPU timestamps (v393 era, 1440p):
`frame ms ~= 45.8 us x render passes + 11.4 ms`, R = 0.92, over real play. About 60% of
the per-pass cost is fixed pipeline drain, 40% scales with pixels (0.8x resolution: 62.5
-> 53.0 us per pass, fps unchanged at that save).

Dense-scene picture from the user's play (v402 log, 34.8 minutes): 4000-5000 draws a
frame sits at the cap; 5000-6500 draws 40.5 fps at 349 passes, GPU 85%; the densest
blocks pair GPU saturation with `BufferModifiedRange` guest waits (22.6 ms per frame
summed over three guest threads) - both relax when passes drop.

The model has a floor. At a 9200-draw view (row-2 sky save) the GPU is only 69% busy,
the guest takes exactly three sync waits a frame (two created by WaitForIdle, one by
SetReference, all `BufferModifiedRange`) and the GPU idles 7.3 ms a frame in three
bubbles. There, removing passes does not move the frame: every "save passes but add
latency" idea lost (tile snapshot, below). Do not measure pass-count ideas at that save.

## Lines that were tried and are closed (do not redo without new evidence)

- **Tile-shader snapshot for writer self-reads** (v415-v418, 1080fbe4, off by default,
  `RYUJINX_METAL_TILE_SNAPSHOT=1`): a memoryless twin attachment filled by a tile kernel
  so the reader can fetch the pre-draw value. Works, correct (screenshots identical), but
  at the row-2 save GPU time fell 0.7 ms and frame time rose 1.35 ms. Mode 2 of the
  experiment is not interpretable (it contradicts mode 1); do not quote its per-dispatch cost.
- **Learned store actions / dead stores** (4d7d9357, `StoreLiveness`, `RYUJINX_METAL_STORE_LIVENESS=1`):
  700-1030 stores a frame, only ~1 a frame (a clear) is really dead. Not worth a policy.
- **Skipping barriers by storage-buffer identity** (v414 probe): fragment stores hit
  99-104 draws a frame that rebind an overlapping buffer; WAW cannot be told from RAW at
  the binding level. Closed.
- **Guest-sync line**: ceiling +14% at the user's dense scenes (GPU 88% busy), and the
  lever is core thread hand-off, not the backend. Not opened.
- **Residency sets, argument-buffer hashing, 45 -> 60 cap, pass reordering**: closed
  earlier (`docs/METAL_WHITE_FLASH_HANDOFF_2026-08-08.md` and commit messages).
- **PSO compile bursts** (80f38773 probe): they explain about a quarter of the slow frames
  while turning the camera and none of the sustained frame rate. The fix, if wanted, is
  async pipeline creation with skip-until-ready, not a binary archive.

The one untested mechanism with a large prize (the ~300 writer self-read splits a frame
in dense scenes): a stamp-and-twin fetch variant - a memoryless uint attachment stamped
with the draw id and a memoryless copy of the pre-draw value, so later fragments of the
same draw read the copy. Exact semantics without a tile dispatch; its cost is fetch's
own serialisation of overlapping fragments, unmeasured. Only worth building if a play log
shows GPU >= 85% busy with fetchWriter >= 200 a frame.

## Open issue 1: fog/cloud layer missing for single frames

**2026-09-05 correction:** the causal interpretation below is withdrawn. The v428
declaration audit shows guest program `02cf57fb52f91612` has only fragment bindings
128/129 (handle words 8/A), not 136. A matching draw trace identifies it as Metal
program `3ebc3a8f6b77cc8f`, the scene/bloom tone mapper. The old probe iterated every
slot in the shared binding cache, including slots left by other programs. Consequently
the binding-136 swaps and subsequent table reads do not establish a wrong input to
this draw. The write probe observed `ConstantBufferUpdater.FlushUboDirty` on the GPU
thread during startup; no link to a bad frame has been established. Keep the following
paragraph as investigation history, not a proven mechanism. The user's newer report
at the Nachoyah Shrine manual save is a one-frame loss of wall/ground sunlight, which
must be distinguished from the older cloud-band symptom.

Symptom: a fog sheet (or the sky-island cloud band) drops out, or pops in, for one to
three frames, every 1-2 s while it is in view; stronger at the hot-spring save (Y=263).
Present with `barrier=all`, with `RYUJINX_METAL_DEPTH_RAW=1`, and in a clean build.

Frame-level mechanism (v420-v424 probes, `RYUJINX_WATCH_PROG=02cf57fb52f91612
RYUJINX_WATCH_BINDINGS_ALL=1 RYUJINX_WATCH_BINDING=136`): the composite that blends
the fog buffer (960x540 R11G11B10Float, sampled at fragment binding 136) runs once a
frame; on the bad frames its binding resolves to a different texture (pool id 728, a
500x400 R16G16Unorm at 0x10FB800000, descriptor and texture consistent). The handle the
emulator read from the guest's table at bind time really was 728 (`atBind == raw`), and a
direct read of the same word microseconds later, in the same GPU-thread call, gives yet
another stable value. So the table word is rewritten right after the emulator reads it,
every frame; on the bad frames the rewrite lands before the read. Who rewrites it is the
remaining question. Candidates, in order: a host-to-guest flush of a buffer that still
carries a stale "GPU-modified" range (`BufferCache.CopyBufferSingleRange` marks a copy
destination modified and leaves guest memory untouched when the source was modified);
guest-side table recycling; a real guest write. The map-flicker fix of 2026-08-29
(`TexturePool.GetInternal` descriptor check, `RYUJINX_POOL_DESC_CHECK`) is on and is not
this. Tooling: `tools/` and the scratchpad scripts named in the memory notes
(`video_ab.sh`, `flash_count.py`, `band_events.py`, `crash_ring_read.py`).

## Current sunlight investigation (2026-09-05)

### Verified v445 result (latest)

The dirty-state check preceded RenderResourcesPrepass. Buffer resolution can switch the
encoder from Render to Blit or rotate the command buffer inside that prepass. The new
encoder must receive every binding set, including sets that were clean before the
transition. v444 detected this path and re-ran preparation with RenderAll dirty.
Same-binary on/control testing at the manual save gave 0 versus 16 PRESENT jump edges
per 6000 frames (edges count up/down separately), at about 50 FPS.

The clean v445 is baseline `910872594a9ec3897c1376a5dc006042ab8d98b7` plus only
this default-enabled fix and the cached-program output
map correction. No new GPU/CPU sampling probes are in it. A 600-second video and another
180-second video after a user-performed manual reload both had zero patch excursions;
the same detector found 11 in the control video. The 600-second whole-frame check was
also zero. Average throughput over 247 120-frame blocks was 49.62 FPS; the slowest block
averaged 45.13 FPS (not a minimum individual-frame rate). RSS started around 14501 MiB
and ended around 12882 MiB. This is scene-specific validation, not a claim to solve the
older cloud-band report or indexed-draw driver crash. The earlier Vulkan comparison was
not rerun after the user's controller/audio changes; do not claim a new Vulkan speedup.

Evidence: `v445-fix.patch`, `v445-source.json`, `v445-binary.json`,
`v445-clean-performance.json`, `v445-patch-control-and-soak.jsonl`, and
`v445-user-reload-patches.json` in the local diagnostics directory. The two source fixes are committed with this hand-off;
the binary, recordings, and diagnostic manifests are local artifacts excluded from Git.
The remaining text below is the historical investigation preceding this result.


The user confirmed that the new manual-save report is wall/ground lighting changing
for one frame. They did not adjust UltraCam time, weather, sun, or shadow settings.
The first manual save is Nachoyah Shrine, 09/05/2026 1:31 AM, position approximately
(388.81, 2404.59, 1660.98). Its thumbnail is sunlit; fresh loads start around 06:45 AM.

The original 12-hour v425 session produced 169 one-frame brightness excursions in
30 seconds near noon. Fresh v426 produced 6 in 20 seconds after loading the outdoor
Great Plateau save and returning, then none in the next short clip. In fresh sessions,
the usual state is dark, and the transient frame gains direct sunlight. Do not label
the dark state itself faulty: the same v429 executable on Vulkan stayed dark from
morning to afternoon, with 0 excursions in 300 seconds. Vulkan's observed steady
status-bar FPS was 23.26 versus roughly 50 on Metal; these are not matched-throughput
trials and cannot exclude a common timing issue.

One independent code omission was corrected in v429-cache-output-mask: disk-cached
programs now receive `ShaderInfo.FragmentOutputMap`, just as freshly translated
programs do. It loaded 4961 cached shaders and produced masked-pipeline counts of 125
at the title and 714 in the save. However, its first 60-second video still has 28
sunlight excursions. The same build with `RYUJINX_METAL_DEPTH_RAW=1` has one verified
excursion at video time 7.783s in 90 seconds. Neither is a completed flicker fix.

Earlier conservative hot-switch and per-draw pass-split A/B/A clips had no flickering
control arm. They cannot exclude those mechanisms. v430 compared live texture/sampler
descriptors with their pool cache entries on the binding fast path. It recorded 24
excursions/90s after reloading, with 29 million comparisons and no mismatches. This
does not check cached binding-object identity against the pool item, nor descriptor
arrays. Do not reuse the invalid binding-136 attribution.

v431 samples output patches immediately after small direct draws. Its two 90-second
clips had no excursions; the same process with sampling finished then produced 3/90s.
This instrument adds pass boundaries and cannot be treated as a fix. Some late draws
hit its per-frame capacity, but the three watched lighting programs and tone mapper
were present in all 5353 gameplay frames of the first sweep. Its present pixels match
ICC-corrected screenshots within 0.003 RGB, and readbacks require a completed fence.

v432 adds a CPU-only record of actual emitted fragment uniforms (up to 4 KiB), texture
and sampler IDs, draws, and present timestamps. The watched programs are Metal labels
`17a20d725b4b8c73`, `104821aad64ec9a6`, and `5a6eec8e1d385f76`. Fresh load: 0/90s;
reload: 8/90s, with complete records and no skipped bindings. All 26 watched texture
bindings kept the same IDs/dimensions/formats throughout the video. Some environment
constants pulsed near the visual events, but external video start latency was unknown.

The same v432 binary with CPU tracing plus **present-only** GPU samples produced
20/90s on its next fresh load. The internal frame IDs agree (5999 shared frames,
timestamps within 21 microseconds). `5a6eec`'s `fp_c13[36].x` pulses followed 11 positive
patch events by exactly two frame IDs; `fp_c13[54].x` dropped 1 -> 0 -> 1 two IDs after
seven other patch events. `fp_c13[37].z` follows 0.8 + 0.2*x^3. These are structured
environment updates, not evidence of random memory corruption. Feedback is a plausible
interpretation, but queued presentation and in-flight buffer writes must be considered;
the ordering does not yet establish the cause. In this watched shader, [36].x is first
reduced by 0.3 and clamped, so its measured small pulses cannot directly change that use.

Later v432/v433 output and input sampling localized some events to earlier lighting
outputs and others to the tone-map output, without establishing one root cause. v434
confirmed native HDR input/attachment overlap and tested copying overlapping inputs
before drawing. That candidate failed: the same-process 90-second recordings had 11
events with copying off and 16 with copying on. Keep this opt-in experiment disabled;
it is not a sunlight fix. Record by verified window ID: one earlier display-2 recording
captured the wrong screen and was explicitly marked invalid.

The existing RYUJINX_METAL_SYNC_STRICT=3 diagnostic disables signal coalescing and
includes already committed command buffers in new sync handles. Its first 90 seconds
had no detected events, but the user subsequently saw flickering in that same run;
a following 90-second window recording detected 19 short brightness excursions.
A quiet first load is not sufficient validation. Neither this result nor the descriptor
audit excludes other cache-validity or shader-translation logic errors. In particular,
the previous white-flash bug was shared translator logic despite differing visible
behavior on Vulkan. No sunlight fix has been delivered. Current process IDs, active
trials, and exact artefact paths belong in the local investigation log.

Evidence, build manifests, source snapshots, videos, and exact counts are indexed in
`artifacts/diagnostics/fog-2026-09-05/investigation.md` (local, not committed). The
player's v426 directory remains intact. The historical candidate probes remain
uncommitted; the two verified v445 source fixes are committed with this hand-off.
UltraCam INI is byte-identical to the baseline: MaxFPS=50, MenuFPS=60, mMenuFPS=30.
The Vulkan CLI switch persisted the backend choice; launching the subsequent Metal
trial restored Config.json to the pre-Vulkan copy. No FPS cap was changed.

## Open issue 2: segfault inside `drawIndexedPrimitives`

Four times on 2026-09-04 (02:30 tile experiment - explained, stale `_applied` pipeline;
then 20:12, 20:23, 21:03), all on `GUI.RenderThread`, all `KERN_INVALID_ADDRESS at 0x8a0`
inside `-[AGXG13XFamilyRenderContext drawIndexedPrimitives:...]+204`, i.e. the driver
dereferencing a nil object. The 21:03 one was the clean v419 build, 19 minutes in, in the
Depths; the earlier two were minutes after warping to the Great Sky Island. v405 ran 11
hours overnight without a crash, so this is a regression from the v412-v419 range or from
the same-day config change (50 fps cap, 1080p). Excluded: a failed pipeline build (the
draw guard skips those), fetch variants (0 at the time), tile snapshot (off). The Metal
validation layer ran 4 minutes without reproducing. Next crash: read the crash ring
(`crash_ring_read.py --last 200`) - it marks a draw whose pipeline belongs to a disposed
program, whose index buffer was disposed earlier, or whose pipeline pointer is null.

## Working rules that were learned the hard way

- Ask before restarting or closing the game, even an instance this side launched - the
  user may have taken it over. Kill by PID, never `pkill -x Ryujinx`.
- One-frame artefacts need per-frame video (`screencapture -x -v -V 30 -D 2`), not
  screenshot bursts; a 1 fps burst cannot tell a drift from a single-frame event.
- Before an A/B, make the changed code prove it is running (the `config:` field, a
  counter in the stats line). Before quoting a probe, check the probe against a case
  whose answer is known.
- Any change to the mod ini or config is an intervention and goes into the A/B ledger.
