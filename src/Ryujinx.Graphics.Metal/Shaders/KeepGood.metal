#include <metal_stdlib>

using namespace metal;

// Keeps the most recent frame that was not flat.
//
// Writes into a persistent 'keep' texture: nine taps decide whether the incoming frame
// is a flat near-white one, and the shader either takes the new frame or leaves the
// stored one in place. The old value arrives through framebuffer fetch, so no second
// sampled texture and no CPU round trip is needed - and unlike a CPU decision it is made
// for the same frame it applies to, which is what earlier attempts could not do.

struct CopyVertexOut {
    float4 position [[position]];
    float2 uv;
};

struct TexCoords {
    float data[4];
};

struct ConstantBuffers {
    constant TexCoords* tex_coord;
};

struct Textures
{
    texture2d<float, access::sample> texture;
    sampler sampler;
};

struct KeepOut {
    float4 color [[color(0)]];
};

vertex CopyVertexOut vertexMain(uint vid [[vertex_id]],
                                constant ConstantBuffers &constant_buffers [[buffer(CONSTANT_BUFFERS_INDEX)]]) {
    CopyVertexOut out;

    int low = vid & 1;
    int high = vid >> 1;
    out.uv.x = constant_buffers.tex_coord->data[low];
    out.uv.y = constant_buffers.tex_coord->data[2 + high];
    out.position.x = (float(low) - 0.5f) * 2.0f;
    out.position.y = (float(high) - 0.5f) * 2.0f;
    out.position.z = 0.0f;
    out.position.w = 1.0f;

    return out;
}

fragment KeepOut fragmentMain(CopyVertexOut in [[stage_in]],
                              constant Textures &textures [[buffer(TEXTURES_INDEX)]],
                              float4 stored [[color(0)]]) {
    // Count saturated taps rather than asking the frame to be uniform. The HUD is
    // composited into this image and survives the fault intact, so a uniformity test is
    // decided by whether a tap happens to land on the minimap - which is why an earlier
    // version of this fired on some camera angles and not others. Measured over 300
    // captured frames: a flat frame has at least 8 of 25 taps saturated (median 24),
    // an ordinary one at most 4 (median 2).
    int saturated = 0;

    for (int y = 1; y <= 5; ++y) {
        for (int x = 1; x <= 5; ++x) {
            float3 c = textures.texture.sample(textures.sampler,
                                               float2(float(x) / 6.0f, float(y) / 6.0f)).rgb;

            if (dot(c, float3(0.25f, 0.5f, 0.25f)) >= (235.0f / 255.0f)) {
                saturated++;
            }
        }
    }

    bool flat = saturated >= 6;

    KeepOut out;
    out.color = flat ? stored : textures.texture.sample(textures.sampler, in.uv);

    return out;
}
