# Standalone Metal reproducer for the TOTK white flash

No emulator, no game. It reproduces only the *shape* of the workload the flash
lives in, so the question can be asked in seconds instead of in ten-minute
daylight windows against a save file.

```
clang -fobjc-arc -framework Foundation -framework Metal -O2 flashrepro.m -o flashrepro
FRAMES=3000 ./flashrepro
```

A frame counts FLAT when the composite's output is uniform near-white while the
scene target demonstrably holds a constant, using FlashGuard's own calibrated
criterion (25-point grid, at least 6 samples over luma 235). Exit status is 1 if
any frame is flat, so it works as a bisection oracle.

`constant N` in the output is the harness self-check: if it is ever zero the
composite never read the constant at all, and the flat count means nothing.

## Knobs

| Variable | Default | What it models |
|---|---|---|
| `FRAMES` | 2000 | frames to run |
| `PASSES` | 200 | render passes per frame (the real frame is ~197) |
| `DRAWS` | 12 | draws per pass (~12.8 real) |
| `W` `H` | 1600x896 | the scene target, gameplay resolution |
| `FMT` | `rg11b10` | also `rgba16f`, `rgba8` |
| `USAGE` | `full` | `full` adds PixelFormatView+ShaderWrite as the backend does, which takes the surface off the compressed path; `lean` omits them |
| `ARGBUF` | 1 | bind through a Tier 2 argument buffer + useResource, as the backend does |
| `REVISIT` | 1 | alternate attachments so passes revisit them |
| `ALIAS` | 0 | write some scene passes through an RGBA8Unorm view of the same storage - the guest genuinely aliases it that way |
| `DEPTH` | 0 | attach depth to every pass |
| `CHURN` | 0 | allocate and drop N scene-sized textures per frame (dynamic resolution) |
| `TRIS` | 0 | N scattered triangles per scene draw, to load the tiler toward a partial render |
| `SPLITCB` | 1 | split each frame across N command buffers, as auto-flush does |
| `INFLIGHT` | 4 | frames in flight; results are read off the fence, never waited on |

## Result: does not reproduce, nineteen configurations

Apple M1 Max / macOS 26.5, roughly fifteen thousand frames total:

```
plain shape (3000 frames)                                   flat 0
ALIAS=1 / DEPTH=1 / CHURN=3 and their combinations          flat 0
TRIS=20000 / TRIS=100000                                    flat 0
SPLITCB=4                                                   flat 0
LIVE=2000 / LIVE=4000 (verified: 4000 textures, ~21 GB)     flat 0
THREADS=6 / THREADS=8                                       flat 0
COMPUTE=7                                                   flat 0
HDR=40 / HDR=1000                                           flat 0
everything at once                                          flat 0
```

The harness self-check passes in every run - the composite reads the injected
constant on essentially every frame - so these are real negatives.

## What that means

Inside the emulator every step of the chain is now positively verified: the
argument buffer hands the shader the same resource id on flat frames as on
normal ones (2219 flat and 4964 normal frames, byte for byte identical), the
texture's memory demonstrably holds a constant we injected, and the fetch
returns uniform near-white anyway.

Outside it, none of those properties reproduce the fault, individually or
together, even at twenty gigabytes of live textures with eight threads on the
queue and thirty million triangles a frame.

So the fault is real and localised, but not yet *characterised*: the list of
properties we can name is not sufficient to cause it. Blind knob-guessing has
now returned nineteen negatives and should stop. The two approaches with
different information content are:

  - a scoped Xcode GPU capture of a flat frame, diffed against a capture of this
    program, to find a property nobody thought to name
  - subtraction inside the emulator rather than addition here: disable frame
    stages against the reproducing save until the rate moves, bracketing the
    condition from the other side

Either way this program stays useful: it is the control. Anything proposed as
"the condition" can be added here in minutes and tested in seconds, and if it
ever does turn white, the result is a few hundred lines of self-contained Metal
that can be filed as-is.
