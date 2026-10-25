#include <metal_stdlib>

using namespace metal;

struct CopyVertexOut {
    float4 position [[position]];
    float2 uv;
};

struct PresentParams {
    float data[8];
};

struct ConstantBuffers {
    constant PresentParams* present_params;
};

struct Textures
{
    texture2d<FORMAT, access::sample> texture;
    sampler sampler;
};

static float cubicWeight(float x) {
    x = abs(x);
    float x2 = x * x;
    float x3 = x2 * x;

    if (x <= 1.0f) {
        return 1.5f * x3 - 2.5f * x2 + 1.0f;
    }

    if (x < 2.0f) {
        return -0.5f * x3 + 2.5f * x2 - 4.0f * x + 2.0f;
    }

    return 0.0f;
}

static FORMAT4 sampleCatmullRom(texture2d<FORMAT, access::sample> texture,
                                sampler samplerState,
                                float2 uv,
                                float2 texel) {
    float2 sourceSize = 1.0f / texel;
    float2 pixel = uv * sourceSize - 0.5f;
    float2 basePixel = floor(pixel);
    float2 fraction = pixel - basePixel;

    float4 color = float4(0.0f);
    float weightSum = 0.0f;

    for (int y = -1; y <= 2; y++) {
        float wy = cubicWeight(float(y) - fraction.y);

        for (int x = -1; x <= 2; x++) {
            float wx = cubicWeight(float(x) - fraction.x);
            float weight = wx * wy;
            float2 sampleUv = (basePixel + float2(float(x), float(y)) + 0.5f) * texel;

            color += float4(texture.sample(samplerState, sampleUv)) * weight;
            weightSum += weight;
        }
    }

    return FORMAT4(color / max(weightSum, 0.00001f));
}

vertex CopyVertexOut vertexMain(uint vid [[vertex_id]],
                                constant ConstantBuffers &constant_buffers [[buffer(CONSTANT_BUFFERS_INDEX)]]) {
    CopyVertexOut out;

    int low = vid & 1;
    int high = vid >> 1;
    out.uv.x = constant_buffers.present_params->data[low];
    out.uv.y = constant_buffers.present_params->data[2 + high];
    out.position.x = (float(low) - 0.5f) * 2.0f;
    out.position.y = (float(high) - 0.5f) * 2.0f;
    out.position.z = 0.0f;
    out.position.w = 1.0f;

    return out;
}

fragment FORMAT4 fragmentMain(CopyVertexOut in [[stage_in]],
                              constant ConstantBuffers &constant_buffers [[buffer(CONSTANT_BUFFERS_INDEX)]],
                              constant Textures &textures [[buffer(TEXTURES_INDEX)]]) {
    float2 texel = float2(constant_buffers.present_params->data[4],
                          constant_buffers.present_params->data[5]);
    float sharpness = constant_buffers.present_params->data[6];

    FORMAT4 center = textures.texture.sample(textures.sampler, in.uv);

    if (sharpness <= 0.0f) {
        return center;
    }

    center = sampleCatmullRom(textures.texture, textures.sampler, in.uv, texel);

    float3 north = float3(textures.texture.sample(textures.sampler, in.uv + float2(0.0f, -texel.y)).rgb);
    float3 south = float3(textures.texture.sample(textures.sampler, in.uv + float2(0.0f, texel.y)).rgb);
    float3 east = float3(textures.texture.sample(textures.sampler, in.uv + float2(texel.x, 0.0f)).rgb);
    float3 west = float3(textures.texture.sample(textures.sampler, in.uv + float2(-texel.x, 0.0f)).rgb);

    float3 e = float3(center.rgb);

    // NaN guard. A NaN anywhere in the 5-tap neighbourhood propagates through
    // min/max/clamp differently per implementation and ends up in the RG11B10Float
    // output as the maximum representable value - which is a uniform ~250 white once
    // tonemapped to BGRA8. If the source scene texture carries NaN texels, this is
    // where they become the flash. Replace NaN with the centre tap; if the centre
    // itself is NaN, black.
    if (any(isnan(e))) { e = float3(0.0f); center = float4(e, 1.0f); }
    north = select(north, e, isnan(north));
    south = select(south, e, isnan(south));
    east = select(east, e, isnan(east));
    west = select(west, e, isnan(west));

    float3 mn4 = min(min(north, south), min(east, west));
    float3 mx4 = max(max(north, south), max(east, west));

    float3 hitMin = min(mn4, e) / max(4.0f * mx4, float3(0.00001f));
    float3 hitMax = (1.0f - max(mx4, e)) / min(4.0f * mn4 - 4.0f, float3(-0.00001f));
    float3 lobeRgb = max(-hitMin, hitMax);

    float lobe = max(max(lobeRgb.r, lobeRgb.g), lobeRgb.b);
    float rcasSharpness = exp2(-(1.5f - clamp(sharpness, 0.0f, 1.0f) * 1.5f));

    lobe = max(-0.1875f, min(lobe, 0.0f)) * rcasSharpness;

    float rcpL = 1.0f / (4.0f * lobe + 1.0f);
    float3 sharpened = clamp((lobe * (north + south + east + west) + e) * rcpL, 0.0f, 1.0f);

    return FORMAT4(sharpened, center.a);
}
