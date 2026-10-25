// Standalone Metal reproducer for the TOTK white-flash fault.
//
// The fault, as characterised in docs/METAL_WHITE_FLASH_HANDOFF_2026-08-08.md:
// a long-lived, uncompressed RG11B10Float colour target is filled with a known
// constant, and a later full-screen pass that reads it with an explicit texel
// fetch at level 0 gets a uniform near-white instead - on roughly 40% of frames,
// intermittently, with the texture's memory demonstrably holding the constant.
// Every host-side write channel, storage mode, usage flag, queue topology,
// barrier, residency declaration and synchronisation mechanism was excluded at
// measurement power inside the emulator.
//
// This program has no emulator and no game in it. It reproduces only the shape
// of that workload so the question can be asked in seconds instead of in
// ten-minute daylight windows against a save file:
//
//   - one persistent RG11B10Float target, created once, never resized
//   - written by many draws across many passes, LoadAction Load
//   - read by a full-screen pass doing twelve tex.read(uint2(c), 0)
//   - about two hundred render passes per frame, revisiting attachments
//   - several frames in flight, results read back off the fence, never waited on
//
// Every property is a knob, so a reproduction can be minimised and a
// non-reproduction can be swept:
//
//   FRAMES=2000 PASSES=200 DRAWS=12 W=1600 H=896 OUTW=1920 OUTH=1080
//   FMT=rg11b10|rgba16f|rgba8      USAGE=full|lean      ARGBUF=0|1
//   INFLIGHT=4  REVISIT=1  VERBOSE=0
//
// Conditions the plain shape did not include, each a knob because the plain
// shape does NOT reproduce (0 flat in 3000 frames):
//
//   ALIAS=1   write some scene passes through an RGBA8Unorm view of the same
//             storage. The guest genuinely does this - substituting a 64bpp
//             format for the scene class fails at creation with "source texture
//             pixelFormat not compatible with texture view pixelFormat
//             (MTLPixelFormatRGBA8Unorm)" - so the real target is read as
//             RG11B10Float and written as RGBA8Unorm through two views.
//   DEPTH=1   attach depth to every pass, as the real frame does.
//   CHURN=n   create and drop n scene-sized textures a frame, standing in for
//             dynamic resolution's constant allocate/free traffic.
//   TRIS=n    n scattered triangles per draw in the scene passes. Twelve
//             full-screen triangles cannot overflow anything; the real scene
//             pass carries about 1528 draws of ordinary geometry, and only that
//             can fill the tiled vertex buffer and force the hardware into a
//             partial render - store every tile mid-pass, reload, continue -
//             which is the one published fault family whose symptom is
//             "attachments come back garbage" (Rosenzweig, The Impossible Bug).
//             Capping draws per pass at 25 in the emulator moved the flash rate
//             39.1% -> 33.4%, so this path is not excluded there either.
//   SPLITCB=n split each frame across n command buffers, as auto-flush does.
//
// Scale, which is what the shape-only configurations were missing. The binding
// was proved identical on flat and normal frames inside the emulator, and the
// shape alone never fails here, so what is left between the two is how much the
// emulator has going on:
//
//   LIVE=n    keep n scene-sized textures alive, for footprint and allocation
//             pressure - the emulator runs with thousands of live textures and
//             was measured at tens of gigabytes.
//   THREADS=n n background threads issuing their own command buffers on the
//             same queue - the emulator has texture readback and uploads
//             running off the render thread continuously.
//   COMPUTE=n n compute dispatches interleaved into each frame; the real frame
//             ends about seven passes on a compute encoder.
//   HDR=v     the geometry draws write v, v*0.8, v*0.6 into the scene target
//             before the constant fill lands on top. RG11B10Float has no sign
//             bit and a five-bit exponent, so a bright daylight scene lives in
//             high exponents and a night one does not - and the fault is gated
//             by day/night while being independent of what the texture holds at
//             fetch time. Constant injection in the emulator only controlled the
//             latter: the game's own HDR writes still happened earlier in the
//             frame, so damage done while writing high exponents would survive
//             it. This is the one condition that reading leaves open.
//
// Build:  clang -fobjc-arc -framework Foundation -framework Metal -O2 \
//               flashrepro.m -o flashrepro
//
// A frame counts as FLAT when the composite's output is a uniform near-white
// while the scene target holds the constant. Exit status is 1 if any frame is
// flat, so this can be used as a bisection oracle.

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>

static int envInt(const char *name, int fallback) {
    const char *value = getenv(name);
    return value && *value ? atoi(value) : fallback;
}

static const char *envStr(const char *name, const char *fallback) {
    const char *value = getenv(name);
    return value && *value ? value : fallback;
}

// The scene constant. 0x55-ish in every channel: not the "scene", and nowhere
// near white, so a flat frame cannot be confused with a legitimate bright one.
static const float kSceneR = 0.33f, kSceneG = 0.21f, kSceneB = 0.13f;

// FlashGuard's calibrated criterion, reused verbatim so "flat" means here what
// it means in the emulator's own measurements.
static const float kSaturatedLuma = 235.0f;
static const int kGridSide = 5;
static const int kSamples = kGridSide * kGridSide;
static const int kSaturatedNeeded = 6;

static NSString *const kShaderSource = @R"(
#include <metal_stdlib>
using namespace metal;

struct VOut { float4 pos [[position]]; };

vertex VOut vmain(uint vid [[vertex_id]]) {
    // Full-screen triangle, no vertex buffer.
    float2 p[3] = { float2(-1.0, -1.0), float2(3.0, -1.0), float2(-1.0, 3.0) };
    VOut o;
    o.pos = float4(p[vid], 0.0, 1.0);
    return o;
}

struct Fill { float4 colour; };

fragment float4 ffill(VOut in [[stage_in]], constant Fill &f [[buffer(0)]]) {
    return f.colour;
}

// Scattered small triangles, generated from the vertex id so no vertex buffer
// is needed. Spread across the whole surface so their post-transform data lands
// in many tiles, which is what fills the tiled vertex buffer.
vertex VOut vgeom(uint vid [[vertex_id]]) {
    uint tri = vid / 3u, corner = vid % 3u;
    float a = float(tri) * 0.6180339887;
    float2 c = float2(fract(a * 71.0), fract(a * 131.0)) * 2.0 - 1.0;
    float2 offs[3] = { float2(0.0, 0.06), float2(-0.05, -0.04), float2(0.05, -0.04) };
    VOut o;
    o.pos = float4(c + offs[corner], 0.5, 1.0);
    return o;
}

struct CompositeArgs {
    texture2d<float> tex [[id(0)]];
};

struct Scale { float2 s; };

// Twelve explicit texel fetches at level 0, matching the shape of the guest
// composite (ee89b4e471373459): no sampler, no LOD selection, no mip filtering.
static inline float3 fetch12(texture2d<float> tex, float2 pos, float2 scale) {
    uint2 base = uint2(pos * scale);
    uint w = tex.get_width(), h = tex.get_height();
    float3 acc = float3(0.0);
    for (int i = 0; i < 12; ++i) {
        uint2 c = uint2(min(base.x + uint(i % 4), w - 1),
                        min(base.y + uint(i / 4), h - 1));
        acc += tex.read(c, 0).xyz;
    }
    return acc / 12.0;
}

fragment float4 fcomposite(VOut in [[stage_in]],
                           texture2d<float> tex [[texture(0)]],
                           constant Scale &sc [[buffer(0)]]) {
    return float4(fetch12(tex, in.pos.xy, sc.s), 1.0);
}

kernel void kmain(device float *out [[buffer(0)]], uint tid [[thread_position_in_grid]]) {
    out[tid] = out[tid] * 0.5 + float(tid & 255u) * 0.01;
}

fragment float4 fcomposite_ab(VOut in [[stage_in]],
                              device CompositeArgs &args [[buffer(1)]],
                              constant Scale &sc [[buffer(0)]]) {
    return float4(fetch12(args.tex, in.pos.xy, sc.s), 1.0);
}
)";

static MTLPixelFormat parseFormat(const char *name) {
    if (!strcmp(name, "rgba16f")) return MTLPixelFormatRGBA16Float;
    if (!strcmp(name, "rgba8")) return MTLPixelFormatRGBA8Unorm;
    return MTLPixelFormatRG11B10Float;
}

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        setvbuf(stdout, NULL, _IONBF, 0);

        const int frames = envInt("FRAMES", 2000);
        const int passes = envInt("PASSES", 200);
        const int drawsPerPass = envInt("DRAWS", 12);
        const int sceneW = envInt("W", 1600), sceneH = envInt("H", 896);
        const int outW = envInt("OUTW", 1920), outH = envInt("OUTH", 1080);
        const int inflight = envInt("INFLIGHT", 4);
        const int revisit = envInt("REVISIT", 1);
        const int useArgBuf = envInt("ARGBUF", 1);
        const int verbose = envInt("VERBOSE", 0);
        const BOOL lean = !strcmp(envStr("USAGE", "full"), "lean");
        const int useAlias = envInt("ALIAS", 0);
        const int useDepth = envInt("DEPTH", 0);
        const int churn = envInt("CHURN", 0);
        const int tris = envInt("TRIS", 0);
        const int splitCb = envInt("SPLITCB", 1);
        const int live = envInt("LIVE", 0);
        const int threads = envInt("THREADS", 0);
        const int computes = envInt("COMPUTE", 0);
        const float hdr = (float)atof(envStr("HDR", "0"));
        const MTLPixelFormat sceneFormat = parseFormat(envStr("FMT", "rg11b10"));

        id<MTLDevice> device = MTLCreateSystemDefaultDevice();
        if (!device) { fprintf(stderr, "no Metal device\n"); return 2; }

        printf("device      %s (argument buffers tier %lu)\n",
               device.name.UTF8String, (unsigned long)device.argumentBuffersSupport);
        printf("scene       %dx%d fmt=%s usage=%s\n", sceneW, sceneH,
               envStr("FMT", "rg11b10"), lean ? "lean" : "full");
        printf("workload    %d frames, %d passes/frame, %d draws/pass, %d in flight, argbuf=%d revisit=%d\n",
               frames, passes, drawsPerPass, inflight, useArgBuf, revisit);
        printf("conditions  alias=%d depth=%d churn=%d tris=%d splitcb=%d\n",
               useAlias, useDepth, churn, tris, splitCb);
        printf("scale       live=%d (~%.1f GB) threads=%d compute=%d hdr=%.1f\n",
               live, live * (double)sceneW * sceneH * 4.0 / 1073741824.0, threads, computes, hdr);

        NSError *error = nil;
        id<MTLLibrary> library = [device newLibraryWithSource:kShaderSource
                                                     options:nil
                                                       error:&error];
        if (!library) { fprintf(stderr, "shader: %s\n", error.description.UTF8String); return 2; }

        id<MTLCommandQueue> queue = [device newCommandQueue];

        // The persistent scene target. Usage mirrors the emulator's: it declares
        // PixelFormatView and ShaderWrite on every colour texture, and on Apple
        // GPUs either flag alone takes the surface off the lossless-compressed
        // path - which is how the flash came to live on an uncompressed target.
        MTLTextureUsage sceneUsage = MTLTextureUsageShaderRead | MTLTextureUsageRenderTarget;
        if (!lean) sceneUsage |= MTLTextureUsagePixelFormatView | MTLTextureUsageShaderWrite;

        MTLTextureDescriptor *sceneDesc =
            [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:sceneFormat
                                                               width:sceneW
                                                              height:sceneH
                                                           mipmapped:NO];
        sceneDesc.usage = sceneUsage;
        sceneDesc.storageMode = MTLStorageModePrivate;
        id<MTLTexture> scene = [device newTextureWithDescriptor:sceneDesc];

        MTLTextureDescriptor *outDesc =
            [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm
                                                               width:outW
                                                              height:outH
                                                           mipmapped:NO];
        outDesc.usage = MTLTextureUsageShaderRead | MTLTextureUsageRenderTarget;
        outDesc.storageMode = MTLStorageModePrivate;
        id<MTLTexture> output = [device newTextureWithDescriptor:outDesc];

        // Filler targets, alternated so passes revisit attachments the way the
        // real frame does (same=19, aba=14 revisits per frame).
        const int fillerCount = 4;
        id<MTLTexture> filler[fillerCount];
        for (int i = 0; i < fillerCount; ++i) {
            MTLTextureDescriptor *d =
                [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:sceneFormat
                                                                   width:512
                                                                  height:512
                                                               mipmapped:NO];
            d.usage = sceneUsage;
            d.storageMode = MTLStorageModePrivate;
            filler[i] = [device newTextureWithDescriptor:d];
        }

        // An RGBA8Unorm view of the very same storage the composite reads as
        // RG11B10Float. Both are 32bpp, which is the only reason Metal permits
        // the pair - and the reason the RGBA16Float experiment was impossible.
        id<MTLTexture> sceneAlias = nil;
        if (useAlias) {
            sceneAlias = [scene newTextureViewWithPixelFormat:MTLPixelFormatRGBA8Unorm];
            if (!sceneAlias) { fprintf(stderr, "alias view failed\n"); return 2; }
        }

        id<MTLTexture> sceneDepth = nil, outDepth = nil;
        if (useDepth) {
            MTLTextureDescriptor *dd =
                [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatDepth32Float
                                                                   width:sceneW height:sceneH mipmapped:NO];
            dd.usage = MTLTextureUsageRenderTarget | MTLTextureUsageShaderRead;
            dd.storageMode = MTLStorageModePrivate;
            sceneDepth = [device newTextureWithDescriptor:dd];

            dd.width = outW; dd.height = outH;
            outDepth = [device newTextureWithDescriptor:dd];
        }

        MTLRenderPipelineDescriptor *pd = [MTLRenderPipelineDescriptor new];
        pd.vertexFunction = [library newFunctionWithName:@"vmain"];
        pd.fragmentFunction = [library newFunctionWithName:@"ffill"];
        pd.colorAttachments[0].pixelFormat = sceneFormat;
        if (useDepth) pd.depthAttachmentPixelFormat = MTLPixelFormatDepth32Float;
        id<MTLRenderPipelineState> fillPipe = [device newRenderPipelineStateWithDescriptor:pd error:&error];
        if (!fillPipe) { fprintf(stderr, "fill pipe: %s\n", error.description.UTF8String); return 2; }

        pd.vertexFunction = [library newFunctionWithName:@"vgeom"];
        id<MTLRenderPipelineState> geomPipe = [device newRenderPipelineStateWithDescriptor:pd error:&error];
        if (!geomPipe) { fprintf(stderr, "geom pipe: %s\n", error.description.UTF8String); return 2; }
        pd.vertexFunction = [library newFunctionWithName:@"vmain"];

        id<MTLRenderPipelineState> aliasPipe = nil;
        if (useAlias) {
            pd.colorAttachments[0].pixelFormat = MTLPixelFormatRGBA8Unorm;
            aliasPipe = [device newRenderPipelineStateWithDescriptor:pd error:&error];
            if (!aliasPipe) { fprintf(stderr, "alias pipe: %s\n", error.description.UTF8String); return 2; }
        }

        pd.fragmentFunction = [library newFunctionWithName:useArgBuf ? @"fcomposite_ab" : @"fcomposite"];
        pd.colorAttachments[0].pixelFormat = MTLPixelFormatRGBA8Unorm;
        id<MTLRenderPipelineState> compositePipe = [device newRenderPipelineStateWithDescriptor:pd error:&error];
        if (!compositePipe) { fprintf(stderr, "composite pipe: %s\n", error.description.UTF8String); return 2; }

        // Tier 2 argument buffer holding the composite's only texture, which is
        // how the emulator binds every texture - not through setFragmentTexture.
        id<MTLBuffer> argBuffer = nil;
        if (useArgBuf) {
            argBuffer = [device newBufferWithLength:sizeof(MTLResourceID)
                                            options:MTLResourceStorageModeShared];
            *(MTLResourceID *)argBuffer.contents = scene.gpuResourceID;
        }

        // Footprint. Held in an array so ARC cannot collect them.
        NSMutableArray *liveTextures = [NSMutableArray arrayWithCapacity:(NSUInteger)MAX(live, 1)];
        for (int i = 0; i < live; ++i) {
            MTLTextureDescriptor *ld =
                [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:sceneFormat
                                                                   width:sceneW height:sceneH mipmapped:NO];
            ld.usage = sceneUsage;
            ld.storageMode = MTLStorageModePrivate;
            id<MTLTexture> t = [device newTextureWithDescriptor:ld];
            if (!t) { fprintf(stderr, "live allocation failed at %d\n", i); break; }
            [liveTextures addObject:t];
        }
        if (live) printf("allocated   %lu live textures\n", (unsigned long)liveTextures.count);

        id<MTLComputePipelineState> computePipe = nil;
        id<MTLBuffer> computeBuf = nil;
        if (computes > 0) {
            computePipe = [device newComputePipelineStateWithFunction:[library newFunctionWithName:@"kmain"]
                                                                error:&error];
            if (!computePipe) { fprintf(stderr, "compute pipe: %s\n", error.description.UTF8String); return 2; }
            computeBuf = [device newBufferWithLength:1 << 20 options:MTLResourceStorageModePrivate];
        }

        // Background traffic on the same queue: its own command buffers, its own
        // blits, never synchronised against the render loop - which is exactly
        // what the emulator's texture readback and uploads do.
        __block volatile int32_t stopThreads = 0;
        for (int t = 0; t < threads; ++t) {
            dispatch_async(dispatch_get_global_queue(DISPATCH_QUEUE_PRIORITY_DEFAULT, 0), ^{
                id<MTLBuffer> staging = [device newBufferWithLength:1 << 20
                                                            options:MTLResourceStorageModeShared];
                while (!stopThreads) {
                    @autoreleasepool {
                        id<MTLCommandBuffer> bcb = [queue commandBuffer];
                        id<MTLBlitCommandEncoder> blit = [bcb blitCommandEncoder];
                        [blit copyFromTexture:scene
                                  sourceSlice:0 sourceLevel:0
                                 sourceOrigin:MTLOriginMake(0, 0, 0)
                                   sourceSize:MTLSizeMake(256, 256, 1)
                                     toBuffer:staging
                            destinationOffset:0
                       destinationBytesPerRow:256 * 4
                     destinationBytesPerImage:256 * 256 * 4];
                        [blit endEncoding];
                        [bcb commit];
                        [bcb waitUntilCompleted];
                    }
                }
            });
        }

        float sceneColour[4] = { kSceneR, kSceneG, kSceneB, 1.0f };
        id<MTLBuffer> fillColour = [device newBufferWithBytes:sceneColour
                                                       length:sizeof(sceneColour)
                                                      options:MTLResourceStorageModeShared];

        float hdrColour[4] = { hdr, hdr * 0.8f, hdr * 0.6f, 1.0f };
        id<MTLBuffer> hdrBuf = [device newBufferWithBytes:hdrColour
                                                   length:sizeof(hdrColour)
                                                  options:MTLResourceStorageModeShared];
        float scale[2] = { (float)sceneW / (float)outW, (float)sceneH / (float)outH };
        id<MTLBuffer> scaleBuf = [device newBufferWithBytes:scale
                                                     length:sizeof(scale)
                                                    options:MTLResourceStorageModeShared];

        // Sample ring, read off the fence several frames later so nothing is
        // ever waited on - a per-frame GPU sync is known to close this race.
        const int slots = inflight + 2;
        id<MTLBuffer> readback = [device newBufferWithLength:slots * kSamples * 4
                                                     options:MTLResourceStorageModeShared];

        dispatch_semaphore_t inflightSem = dispatch_semaphore_create(inflight);
        __block int flatFrames = 0, checkedFrames = 0, firstFlat = -1;
        __block int constantFrames = 0, otherFrames = 0;
        __block unsigned lastR = 0, lastG = 0, lastB = 0;

        for (int frame = 0; frame < frames; ++frame) {
          @autoreleasepool {
            dispatch_semaphore_wait(inflightSem, DISPATCH_TIME_FOREVER);
            const int slot = frame % slots;

            id<MTLCommandBuffer> cb = [queue commandBuffer];   // reassigned when split

            // Pass 1: the composite, early in the frame, consuming what the
            // previous frame left in the scene target - the real one runs as
            // pass 2 of 169 while the scene is finished at pass 167.
            {
                MTLRenderPassDescriptor *rp = [MTLRenderPassDescriptor renderPassDescriptor];
                rp.colorAttachments[0].texture = output;
                rp.colorAttachments[0].loadAction = MTLLoadActionLoad;
                rp.colorAttachments[0].storeAction = MTLStoreActionStore;
                if (useDepth) {
                    rp.depthAttachment.texture = outDepth;
                    rp.depthAttachment.loadAction = MTLLoadActionLoad;
                    rp.depthAttachment.storeAction = MTLStoreActionStore;
                }
                id<MTLRenderCommandEncoder> enc = [cb renderCommandEncoderWithDescriptor:rp];
                [enc setRenderPipelineState:compositePipe];
                [enc setFragmentBuffer:scaleBuf offset:0 atIndex:0];
                if (useArgBuf) {
                    [enc setFragmentBuffer:argBuffer offset:0 atIndex:1];
                    [enc useResource:scene usage:MTLResourceUsageRead stages:MTLRenderStageFragment];
                } else {
                    [enc setFragmentTexture:scene atIndex:0];
                }
                [enc drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
                [enc endEncoding];
            }

            // Filler passes, then the scene writes, so the target is finished
            // late in the frame and consumed by the next one.
            for (int p = 1; p < passes; ++p) {
                BOOL writesScene = (p % 8) == 0;
                // Alternate the scene writes between the two views of the same
                // storage, so the target is written as RGBA8Unorm and read as
                // RG11B10Float within one frame.
                BOOL throughAlias = writesScene && useAlias && ((p / 8) % 2) == 1;
                id<MTLTexture> target = writesScene
                    ? (throughAlias ? sceneAlias : scene)
                    : filler[revisit ? (p % fillerCount) : 0];

                MTLRenderPassDescriptor *rp = [MTLRenderPassDescriptor renderPassDescriptor];
                rp.colorAttachments[0].texture = target;
                rp.colorAttachments[0].loadAction = MTLLoadActionLoad;
                rp.colorAttachments[0].storeAction = MTLStoreActionStore;
                if (useDepth) {
                    rp.depthAttachment.texture = writesScene ? sceneDepth : nil;
                    if (rp.depthAttachment.texture) {
                        rp.depthAttachment.loadAction = MTLLoadActionLoad;
                        rp.depthAttachment.storeAction = MTLStoreActionStore;
                    }
                }

                id<MTLRenderCommandEncoder> enc = [cb renderCommandEncoderWithDescriptor:rp];
                [enc setFragmentBuffer:fillColour offset:0 atIndex:0];

                // Geometry first, to load the tiler, then the full-screen fill
                // so the target still ends the frame holding the constant and a
                // flat frame stays unambiguous.
                if (tris > 0 && writesScene) {
                    [enc setRenderPipelineState:(throughAlias ? aliasPipe : geomPipe)];
                    if (hdr > 0.0f) [enc setFragmentBuffer:hdrBuf offset:0 atIndex:0];
                    if (!throughAlias) {
                        for (int d = 0; d < drawsPerPass; ++d) {
                            [enc drawPrimitives:MTLPrimitiveTypeTriangle
                                    vertexStart:0
                                    vertexCount:(NSUInteger)tris * 3];
                        }
                    }
                }

                // The detection constant lands last, so the target ends every
                // frame holding it however bright the geometry wrote.
                [enc setFragmentBuffer:fillColour offset:0 atIndex:0];
                [enc setRenderPipelineState:(throughAlias ? aliasPipe : fillPipe)];
                for (int d = 0; d < drawsPerPass; ++d) {
                    [enc drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
                }
                [enc endEncoding];

                if (splitCb > 1 && (p % (passes / splitCb + 1)) == 0 && p + 1 < passes) {
                    [cb commit];
                    cb = [queue commandBuffer];
                }
            }

            for (int c = 0; c < computes; ++c) {
                id<MTLComputeCommandEncoder> ce = [cb computeCommandEncoder];
                [ce setComputePipelineState:computePipe];
                [ce setBuffer:computeBuf offset:0 atIndex:0];
                [ce dispatchThreadgroups:MTLSizeMake(64, 1, 1)
                   threadsPerThreadgroup:MTLSizeMake(64, 1, 1)];
                [ce endEncoding];
            }

            // Dynamic resolution allocates and frees scene-sized targets
            // continuously; the emulator was measured at about ninety
            // create/destroy pairs a second.
            for (int c = 0; c < churn; ++c) {
                MTLTextureDescriptor *cd =
                    [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:sceneFormat
                                                                       width:sceneW height:sceneH mipmapped:NO];
                cd.usage = sceneUsage;
                cd.storageMode = MTLStorageModePrivate;
                (void)[device newTextureWithDescriptor:cd];
            }

            // Sample the composite's output on the same grid the emulator uses.
            {
                id<MTLBlitCommandEncoder> blit = [cb blitCommandEncoder];
                for (int i = 0; i < kSamples; ++i) {
                    MTLOrigin o = MTLOriginMake(outW * (i % kGridSide + 1) / (kGridSide + 1),
                                                outH * (i / kGridSide + 1) / (kGridSide + 1), 0);
                    [blit copyFromTexture:output
                              sourceSlice:0
                              sourceLevel:0
                             sourceOrigin:o
                               sourceSize:MTLSizeMake(1, 1, 1)
                                 toBuffer:readback
                        destinationOffset:(slot * kSamples + i) * 4
                   destinationBytesPerRow:4
                 destinationBytesPerImage:4];
                }
                [blit endEncoding];
            }

            [cb addCompletedHandler:^(id<MTLCommandBuffer> done) {
                if (done.error) {
                    fprintf(stderr, "command buffer failed: %s\n",
                            done.error.localizedDescription.UTF8String);
                }
                const uint8_t *p = (const uint8_t *)readback.contents + slot * kSamples * 4;
                int saturated = 0;
                for (int i = 0; i < kSamples; ++i) {
                    const uint8_t *px = p + i * 4;
                    if ((px[0] + px[1] + px[1] + px[2]) * 0.25f >= kSaturatedLuma) saturated++;
                }
                checkedFrames++;
                lastR = p[0]; lastG = p[1]; lastB = p[2];

                const int wantR = (int)(kSceneR * 255), wantG = (int)(kSceneG * 255);
                const int wantB = (int)(kSceneB * 255);
                BOOL isConstant = abs((int)p[0] - wantR) <= 6 &&
                                  abs((int)p[1] - wantG) <= 6 &&
                                  abs((int)p[2] - wantB) <= 6;

                if (saturated >= kSaturatedNeeded) {
                    if (firstFlat < 0) firstFlat = checkedFrames;
                    flatFrames++;
                    if (verbose) {
                        fprintf(stderr, "flat frame %d: %u,%u,%u\n",
                                checkedFrames, p[0], p[1], p[2]);
                    }
                } else if (isConstant) {
                    constantFrames++;
                } else {
                    otherFrames++;
                    if (verbose && otherFrames < 5) {
                        fprintf(stderr, "other frame %d: %u,%u,%u\n",
                                checkedFrames, p[0], p[1], p[2]);
                    }
                }
                dispatch_semaphore_signal(inflightSem);
            }];

            [cb commit];
          }
        }

        // Drain.
        for (int i = 0; i < inflight; ++i) {
            dispatch_semaphore_wait(inflightSem, DISPATCH_TIME_FOREVER);
        }
        stopThreads = 1;

        printf("last sample %u,%u,%u   (constant is about %u,%u,%u)\n",
               lastR, lastG, lastB,
               (unsigned)(kSceneR * 255), (unsigned)(kSceneG * 255), (unsigned)(kSceneB * 255));
        printf("frames      constant %d, flat %d, other %d, checked %d\n",
               constantFrames, flatFrames, otherFrames, checkedFrames);
        if (constantFrames == 0) {
            printf("HARNESS     the composite never once read the constant - this is a broken\n");
            printf("            harness, not a negative result. Fix before believing any count.\n");
        }
        printf("RESULT      flat %d / %d checked", flatFrames, checkedFrames);
        if (flatFrames) printf("  (%.1f%%, first at frame %d)", 100.0 * flatFrames / checkedFrames, firstFlat);
        printf("\n");

        return flatFrames ? 1 : 0;
    }
}
