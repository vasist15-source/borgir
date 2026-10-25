// Paravirtual NVN: device, memory pools, buffers, textures, samplers, descriptor pools, programs,
// syncs, events and state objects. Semantics follow MacNativeGames / D2R Mobile's NVN layer (MIT)
// where it was checked against TOTK; object state lives in the game's NVN objects (pv_types.h).

// ---------- Device ----------

NVN(nvnDeviceBuilderSetDefaults, void, NVNdeviceBuilder *b) { memset(b, 0, sizeof(dev_builder_t)); }
NVN(nvnDeviceBuilderSetFlags, void, NVNdeviceBuilder *b, int f) { ((dev_builder_t *)b)->flags = (uint32_t)f; }
NVN_NOPROTO(nvnDeviceBuilderGetFlags, NVNdeviceFlagBits, const NVNdeviceBuilder *b) { return (NVNdeviceFlagBits)((const dev_builder_t *)b)->flags; }

NVN(nvnDeviceInitialize, NVNboolean, NVNdevice *d, const NVNdeviceBuilder *b)
{
    dev_t_ *o = (dev_t_ *)d;
    memset(o, 0, sizeof *o);
    o->magic = MAGIC('D', 'E', 'V', 0);
    o->flags = ((const dev_builder_t *)b)->flags;
    pv_logf("nvnpv: DeviceInitialize flags %x", o->flags);

    return 1;
}

NVN(nvnDeviceFinalize, void, NVNdevice *d) { ((dev_t_ *)d)->magic = 0; }
NVN(nvnDeviceSetDebugLabel, void, NVNdevice *o, const char *s) { (void)o; (void)s; }

// Device properties: Tegra X1 values (nnheaders nvn_DeviceConstantsNX.h, measured on SMO by the
// MacNativeGames authors). TOTK requires API major 55, minor >= 15.
static const int g_devinfo[] = {
    [0x00] = 55, [0x01] = 15,
    [0x02] = 14, [0x03] = 65536, [0x04] = 256, [0x05] = 8, [0x06] = 16, [0x07] = 4, [0x08] = 16, [0x09] = 32,
    [0x0A] = 16, [0x0B] = 4, [0x0C] = 4, [0x0D] = 4, [0x0E] = 16, [0x0F] = 32, [0x10] = 32, [0x11] = 256, [0x12] = 256,
    [0x13] = 4, [0x14] = 8, [0x15] = 512, [0x16] = 256, [0x17] = 2, [0x18] = 256, [0x19] = 4096, [0x1A] = 131072,
    [0x1B] = 16, [0x1C] = 1536, [0x1D] = 1024, [0x1E] = 64, [0x1F] = 1536, [0x20] = 65535, [0x21] = 65535, [0x22] = 65535,
    [0x23] = 8, [0x24] = 1048576, [0x25] = 4096, [0x26] = 16, [0x27] = 4096, [0x28] = 1, [0x29] = 1, [0x2A] = 1,
    [0x2B] = 262144, [0x2C] = 15, [0x2D] = 2048, [0x2E] = 1, [0x2F] = 1, [0x30] = 14, [0x31] = 5, [0x32] = 1, [0x33] = 8,
    [0x34] = 8, [0x35] = 4, [0x36] = 32, [0x37] = 128, [0x38] = 32, [0x39] = 128, [0x3A] = 65536, [0x3B] = 1, [0x3C] = 4,
    [0x3D] = 16384, [0x3E] = 134217728, [0x3F] = 2048, [0x40] = 16384, [0x41] = 16384, [0x42] = 1, [0x43] = 1, [0x44] = 1,
    [0x45] = 1, [0x46] = 5, [0x47] = 1, [0x48] = 1, [0x49] = 1, [0x4A] = 1, [0x4B] = 1, [0x4C] = 1, [0x4D] = 16,
    [0x4E] = 1024, [0x4F] = 32, [0x50] = 4096, [0x51] = 65536, [0x52] = 65536, [0x53] = 256, [0x54] = 16384,
    [0x55] = 262144, [0x56] = 4096, [0x57] = 1, [0x58] = 4, [0x59] = 2, [0x5A] = 0, [0x5B] = 4096, [0x5C] = 16384,
    [0x5D] = 4096, [0x5E] = 128, [0x5F] = 4096,
};

NVN(nvnDeviceGetInteger, void, const NVNdevice *d, NVNdeviceInfo what, int *out)
{
    unsigned w = (unsigned)what;

    if (w >= sizeof g_devinfo / sizeof *g_devinfo)
    {
        pv_logf("nvnpv: unsupported device property %x", w);
        *out = 0;
        return;
    }

    *out = g_devinfo[w];
}

static uint64_t counter_ns(void)
{
    uint64_t ticks, freq;
    __asm__ volatile("mrs %0, cntvct_el0" : "=r"(ticks));
    __asm__ volatile("mrs %0, cntfrq_el0" : "=r"(freq));

    return freq ? ticks / freq * 1000000000u + ticks % freq * 1000000000u / freq : 0;
}

NVN(nvnDeviceGetCurrentTimestampInNanoseconds, uint64_t, const NVNdevice *d) { return counter_ns(); }

// GPU timestamps are written by the host in Tegra ticks (19.2 MHz): ns = ticks * 625 / 12.
NVN(nvnDeviceGetTimestampInNanoseconds, uint64_t, const NVNdevice *d, const NVNcounterData *c)
{
    return c->timestamp / 12 * 625 + c->timestamp % 12 * 625 / 12;
}

NVN(nvnDeviceSetIntermediateShaderCache, void, NVNdevice *d, int n) { (void)d; (void)n; }

// Combined handle = texture id (20 bits) | sampler id << 20; the host decodes it the same way.
NVN(nvnDeviceGetTextureHandle, NVNtextureHandle, const NVNdevice *d, int tid, int sid) { return (NVNtextureHandle)((uint64_t)(uint32_t)tid | (uint64_t)(uint32_t)sid << 20); }
NVN(nvnDeviceGetTexelFetchHandle, NVNtextureHandle, const NVNdevice *d, int tid) { return (NVNtextureHandle)(uint32_t)tid; }
NVN(nvnDeviceGetImageHandle, NVNimageHandle, const NVNdevice *d, int id) { return (NVNimageHandle)(uint32_t)id; }
NVN(nvnDeviceGetSeparateTextureHandle, NVNseparateTextureHandle, const NVNdevice *d, int id) { return (NVNseparateTextureHandle){ (uint64_t)(uint32_t)id }; }
NVN(nvnDeviceGetSeparateSamplerHandle, NVNseparateSamplerHandle, const NVNdevice *d, int id) { return (NVNseparateSamplerHandle){ (uint64_t)(uint32_t)id << 20 }; }

NVN(nvnDeviceInstallDebugCallback, void, NVNdevice *d, const PFNNVNDEBUGCALLBACKPROC cb, void *data, uint32_t enable)
{
    dev_t_ *o = (dev_t_ *)d;
    o->dbg_cb = B8(enable) ? (uint64_t)(uintptr_t)cb : 0;
    o->dbg_data = (uint64_t)(uintptr_t)data;
}

NVN(nvnDeviceGenerateDebugDomainId, NVNdebugDomainId, const NVNdevice *d, const char *s) { static uint32_t n; return (NVNdebugDomainId)++n; }

static uint64_t g_depth_minus_w;

NVN(nvnDeviceSetWindowOriginMode, void, NVNdevice *d, NVNwindowOriginMode m) { ((dev_t_ *)d)->origin = m; pv_logf("nvnpv: window origin mode %d", (int)m); }

NVN(nvnDeviceSetDepthMode, void, NVNdevice *d, NVNdepthMode m)
{
    ((dev_t_ *)d)->depth_mode = m;
    g_depth_minus_w = m == NVN_DEPTH_MODE_NEAR_IS_MINUS_W;
    pv_logf("nvnpv: depth mode %d", (int)m);
}

NVN(nvnDeviceGetWindowOriginMode, NVNwindowOriginMode, const NVNdevice *d) { return (NVNwindowOriginMode)((const dev_t_ *)d)->origin; }
NVN(nvnDeviceGetDepthMode, NVNdepthMode, const NVNdevice *d) { return (NVNdepthMode)((const dev_t_ *)d)->depth_mode; }
NVN(nvnDeviceRegisterFastClearColor, NVNboolean, NVNdevice *d, const float *c, NVNformat f) { return 0; }
NVN(nvnDeviceRegisterFastClearColori, NVNboolean, NVNdevice *d, const int *c, NVNformat f) { return 0; }
NVN(nvnDeviceRegisterFastClearColorui, NVNboolean, NVNdevice *d, const uint32_t *c, NVNformat f) { return 0; }
NVN(nvnDeviceRegisterFastClearDepth, NVNboolean, NVNdevice *d, float v) { return 0; }
NVN(nvnDeviceApplyDeferredFinalizes, void, NVNdevice *d, int age) { (void)d; (void)age; }
NVN(nvnDeviceFinalizeCommandHandle, void, NVNdevice *d, NVNcommandHandle h) { (void)d; (void)h; }
NVN(nvnDeviceIsExternalDebuggerAttached, NVNboolean, const NVNdevice *d) { return 0; }
NVN_NOPROTO(nvnDeviceSetAutomaticCheckpointsMask, void, NVNdevice *d, uint32_t m) { (void)d; (void)m; }

// ---------- Memory pools ----------

#define POOL_MAGIC MAGIC('P', 'O', 'O', 'L')

NVN(nvnMemoryPoolBuilderSetDefaults, void, NVNmemoryPoolBuilder *b) { NVNdevice *dev = ((pool_builder_t *)b)->dev; memset(b, 0, sizeof(pool_builder_t)); ((pool_builder_t *)b)->dev = dev; }
NVN(nvnMemoryPoolBuilderSetDevice, void, NVNmemoryPoolBuilder *b, NVNdevice *d) { ((pool_builder_t *)b)->dev = d; }
NVN(nvnMemoryPoolBuilderSetStorage, void, NVNmemoryPoolBuilder *b, void *m, size_t n) { ((pool_builder_t *)b)->mem = m; ((pool_builder_t *)b)->size = n; }
NVN(nvnMemoryPoolBuilderSetFlags, void, NVNmemoryPoolBuilder *b, int f) { ((pool_builder_t *)b)->flags = (uint32_t)f; }
NVN_NOPROTO(nvnMemoryPoolBuilderGetDevice, const NVNdevice *, const NVNmemoryPoolBuilder *b) { return ((const pool_builder_t *)b)->dev; }
NVN_NOPROTO(nvnMemoryPoolBuilderGetMemory, void *, const NVNmemoryPoolBuilder *b) { return ((const pool_builder_t *)b)->mem; }
NVN(nvnMemoryPoolBuilderGetSize, size_t, const NVNmemoryPoolBuilder *b) { return ((const pool_builder_t *)b)->size; }
NVN(nvnMemoryPoolBuilderGetFlags, NVNmemoryPoolFlags, const NVNmemoryPoolBuilder *b) { return (NVNmemoryPoolFlags)((const pool_builder_t *)b)->flags; }

NVN(nvnMemoryPoolInitialize, NVNboolean, NVNmemoryPool *p, const NVNmemoryPoolBuilder *b)
{
    const pool_builder_t *s = (const pool_builder_t *)b;
    pool_t *o = (pool_t *)p;

    memset(o, 0, sizeof *o);
    o->magic = POOL_MAGIC;
    o->flags = s->flags;
    o->mem = s->mem;
    o->size = s->size;

    if (o->flags & NVN_MEMORY_POOL_FLAGS_VIRTUAL)
    {
        // No storage of its own: the host reserves a guest range that MapVirtual fills with
        // pages of physical pools.
        o->mem = (uint8_t *)(uintptr_t)pv_host(PV_HOST_VPOOL, o->size, o->flags, 0, 0);

        if (!o->mem)
        {
            pv_logf("nvnpv: virtual pool of %llx bytes: no guest range", o->size);
            o->magic = 0;
            return 0;
        }
    }

    if (!o->mem)
    {
        pv_logf("nvnpv: MemoryPoolInitialize without storage (flags %x size %llx)", o->flags, o->size);
        o->magic = 0;
        return 0;
    }

    o->host_id = pv_host(PV_HOST_POOL, (uint64_t)(uintptr_t)o->mem, o->size, o->flags, 1);

    return 1;
}

NVN(nvnMemoryPoolFinalize, void, NVNmemoryPool *p)
{
    pool_t *o = (pool_t *)p;

    if (o->magic == POOL_MAGIC && o->mem)
    {
        pv_host(PV_HOST_POOL, (uint64_t)(uintptr_t)o->mem, o->size, o->flags, 0);
    }

    o->magic = 0;
}

NVN(nvnMemoryPoolSetDebugLabel, void, NVNmemoryPool *o, const char *s) { (void)o; (void)s; }
NVN(nvnMemoryPoolFlushMappedRange, void, const NVNmemoryPool *p, ptrdiff_t off, size_t n) { (void)p; (void)off; (void)n; }
NVN(nvnMemoryPoolInvalidateMappedRange, void, const NVNmemoryPool *p, ptrdiff_t off, size_t n) { (void)p; (void)off; (void)n; }
NVN(nvnMemoryPoolGetFlags, NVNmemoryPoolFlags, const NVNmemoryPool *p) { return (NVNmemoryPoolFlags)((const pool_t *)p)->flags; }
NVN(nvnMemoryPoolGetSize, size_t, const NVNmemoryPool *p) { return ((const pool_t *)p)->size; }

NVN(nvnMemoryPoolMap, void *, const NVNmemoryPool *p)
{
    const pool_t *v = (const pool_t *)p;

    if (v->magic != POOL_MAGIC || (v->flags & NVN_MEMORY_POOL_FLAGS_CPU_NO_ACCESS))
    {
        return NULL;
    }

    return v->mem;
}

NVN(nvnMemoryPoolGetBufferAddress, NVNbufferAddress, const NVNmemoryPool *p) { return (NVNbufferAddress)(uintptr_t)((const pool_t *)p)->mem; }

// NVNmappingRequest (0x28 bytes, runtime-verified by MacNativeGames).
typedef struct
{
    const NVNmemoryPool *physical_pool;
    int64_t physical_offset;
    int64_t virtual_offset;
    uint64_t size;
    int32_t storage_class;
    uint32_t padding;
} pv_mapping_request;

NVN(nvnMemoryPoolMapVirtual, NVNboolean, NVNmemoryPool *p, int n, const NVNmappingRequest *r)
{
    pool_t *v = (pool_t *)p;
    const pv_mapping_request *req = (const pv_mapping_request *)(const void *)r;

    if (v->magic != POOL_MAGIC || !(v->flags & NVN_MEMORY_POOL_FLAGS_VIRTUAL) || n < 0 || n > 4096)
    {
        pv_logf("nvnpv: MapVirtual on %p (flags %x) with %d requests refused", p, v->flags, n);
        return 0;
    }

    pv_vmap_req tmp[64];
    int done = 0;

    while (done < n)
    {
        int k = n - done < 64 ? n - done : 64;

        for (int i = 0; i < k; i++)
        {
            const pv_mapping_request *q = &req[done + i];
            const pool_t *ph = (const pool_t *)(const void *)q->physical_pool;

            tmp[i].physical_addr = ph ? (uint64_t)(uintptr_t)ph->mem + (uint64_t)q->physical_offset : 0;
            tmp[i].physical_size = ph ? ph->size - (uint64_t)q->physical_offset : 0;
            tmp[i].virtual_offset = (uint64_t)q->virtual_offset;
            tmp[i].size = q->size;
        }

        if (!pv_host(PV_HOST_VPOOL_MAP, (uint64_t)(uintptr_t)v->mem, (uint64_t)k, (uint64_t)(uintptr_t)tmp, v->size))
        {
            return 0;
        }

        done += k;
    }

    return 1;
}

// ---------- Buffers ----------

#define BUF_MAGIC MAGIC('B', 'U', 'F', 0)

NVN(nvnBufferBuilderSetDevice, void, NVNbufferBuilder *b, NVNdevice *d) { ((buf_builder_t *)b)->dev = d; }
NVN(nvnBufferBuilderSetDefaults, void, NVNbufferBuilder *b) { NVNdevice *d = ((buf_builder_t *)b)->dev; memset(b, 0, sizeof(buf_builder_t)); ((buf_builder_t *)b)->dev = d; }
NVN(nvnBufferBuilderSetStorage, void, NVNbufferBuilder *b, NVNmemoryPool *p, ptrdiff_t off, size_t size) { buf_builder_t *v = (buf_builder_t *)b; v->pool = (pool_t *)p; v->off = off; v->size = size; }
NVN_NOPROTO(nvnBufferBuilderGetDevice, NVNdevice *, const NVNbufferBuilder *b) { return ((const buf_builder_t *)b)->dev; }
NVN_NOPROTO(nvnBufferBuilderGetMemoryPool, NVNmemoryPool *, const NVNbufferBuilder *b) { return (NVNmemoryPool *)((const buf_builder_t *)b)->pool; }
NVN(nvnBufferBuilderGetMemoryOffset, ptrdiff_t, const NVNbufferBuilder *b) { return ((const buf_builder_t *)b)->off; }
NVN(nvnBufferBuilderGetSize, size_t, const NVNbufferBuilder *b) { return ((const buf_builder_t *)b)->size; }

NVN(nvnBufferInitialize, NVNboolean, NVNbuffer *b, const NVNbufferBuilder *builder)
{
    const buf_builder_t *v = (const buf_builder_t *)builder;

    if (!v->pool || v->pool->magic != POOL_MAGIC || v->off < 0 || (uint64_t)v->off > v->pool->size || v->size > v->pool->size - (uint64_t)v->off)
    {
        pv_logf("nvnpv: BufferInitialize %p: bad storage (pool %p off %llx size %llx)", b, v->pool, (uint64_t)v->off, v->size);
        return 0;
    }

    *(buf_t *)b = (buf_t){ BUF_MAGIC, 0, v->pool, v->off, v->size };

    return 1;
}

NVN(nvnBufferFinalize, void, NVNbuffer *b) { memset(b, 0, sizeof(buf_t)); }
NVN(nvnBufferSetDebugLabel, void, NVNbuffer *o, const char *s) { (void)o; (void)s; }

NVN(nvnBufferMap, void *, const NVNbuffer *b)
{
    const buf_t *v = (const buf_t *)b;

    return v->pool && v->pool->mem ? v->pool->mem + v->off : NULL;
}

NVN(nvnBufferGetAddress, NVNbufferAddress, const NVNbuffer *b)
{
    const buf_t *v = (const buf_t *)b;

    return v->pool ? (NVNbufferAddress)(uintptr_t)(v->pool->mem + v->off) : 0;
}

NVN(nvnBufferGetMemoryPool, NVNmemoryPool *, const NVNbuffer *b) { return (NVNmemoryPool *)((const buf_t *)b)->pool; }
NVN(nvnBufferGetMemoryOffset, ptrdiff_t, const NVNbuffer *b) { return ((const buf_t *)b)->off; }
NVN(nvnBufferGetSize, size_t, const NVNbuffer *b) { return ((const buf_t *)b)->size; }
NVN(nvnBufferGetDebugID, uint64_t, const NVNbuffer *b) { return (uint64_t)(uintptr_t)b; }
NVN(nvnBufferFlushMappedRange, void, const NVNbuffer *b, ptrdiff_t off, size_t size) { (void)b; (void)off; (void)size; }
NVN(nvnBufferInvalidateMappedRange, void, const NVNbuffer *b, ptrdiff_t off, size_t size) { (void)b; (void)off; (void)size; }

// ---------- Textures ----------

#define TEX_MAGIC MAGIC('T', 'E', 'X', 0)
#define TB(b) ((tex_builder_t *)(b))
#define CTB(b) ((const tex_builder_t *)(b))
#define TX(t) ((tex_t *)(t))
#define CTX(t) ((const tex_t *)(t))

NVN(nvnTextureBuilderSetDefaults, void, NVNtextureBuilder *b)
{
    NVNdevice *dev = TB(b)->dev;
    memset(b, 0, sizeof(tex_builder_t));
    TB(b)->dev = dev;
    TB(b)->d.swizzle[0] = NVN_TEXTURE_SWIZZLE_R;
    TB(b)->d.swizzle[1] = NVN_TEXTURE_SWIZZLE_G;
    TB(b)->d.swizzle[2] = NVN_TEXTURE_SWIZZLE_B;
    TB(b)->d.swizzle[3] = NVN_TEXTURE_SWIZZLE_A;
    TB(b)->d.target = NVN_TEXTURE_TARGET_2D;
    TB(b)->d.width = TB(b)->d.height = TB(b)->d.depth = TB(b)->d.levels = 1;
}

NVN(nvnTextureBuilderSetDevice, void, NVNtextureBuilder *b, NVNdevice *d) { TB(b)->dev = d; }
NVN(nvnTextureBuilderSetFlags, void, NVNtextureBuilder *b, int f) { TB(b)->d.flags = (uint32_t)f; }
NVN(nvnTextureBuilderSetTarget, void, NVNtextureBuilder *b, NVNtextureTarget t) { TB(b)->d.target = (uint8_t)t; }
NVN(nvnTextureBuilderSetWidth, void, NVNtextureBuilder *b, int v) { TB(b)->d.width = (uint32_t)v; }
NVN(nvnTextureBuilderSetHeight, void, NVNtextureBuilder *b, int v) { TB(b)->d.height = (uint32_t)v; }
NVN(nvnTextureBuilderSetDepth, void, NVNtextureBuilder *b, int v) { TB(b)->d.depth = (uint32_t)v; }
NVN(nvnTextureBuilderSetSize1D, void, NVNtextureBuilder *b, int w) { TB(b)->d.width = (uint32_t)w; TB(b)->d.height = TB(b)->d.depth = 1; }
NVN(nvnTextureBuilderSetSize2D, void, NVNtextureBuilder *b, int w, int h) { TB(b)->d.width = (uint32_t)w; TB(b)->d.height = (uint32_t)h; TB(b)->d.depth = 1; }
NVN(nvnTextureBuilderSetSize3D, void, NVNtextureBuilder *b, int w, int h, int d) { TB(b)->d.width = (uint32_t)w; TB(b)->d.height = (uint32_t)h; TB(b)->d.depth = (uint32_t)d; }
NVN(nvnTextureBuilderSetLevels, void, NVNtextureBuilder *b, int v) { TB(b)->d.levels = (uint32_t)v; }
NVN(nvnTextureBuilderSetFormat, void, NVNtextureBuilder *b, NVNformat f) { TB(b)->d.format = (uint16_t)f; }
NVN(nvnTextureBuilderSetSamples, void, NVNtextureBuilder *b, int s) { TB(b)->d.samples = (uint8_t)s; }
NVN(nvnTextureBuilderSetStride, void, NVNtextureBuilder *b, ptrdiff_t s) { TB(b)->d.stride = (uint32_t)s; }
NVN(nvnTextureBuilderSetStorage, void, NVNtextureBuilder *b, NVNmemoryPool *p, ptrdiff_t off) { TB(b)->pool = (pool_t *)p; TB(b)->off = off; }

NVN(nvnTextureBuilderSetSwizzle, void, NVNtextureBuilder *b, NVNtextureSwizzle r, NVNtextureSwizzle g, NVNtextureSwizzle bl, NVNtextureSwizzle a)
{
    TB(b)->d.swizzle[0] = (uint8_t)r;
    TB(b)->d.swizzle[1] = (uint8_t)g;
    TB(b)->d.swizzle[2] = (uint8_t)bl;
    TB(b)->d.swizzle[3] = (uint8_t)a;
}

NVN(nvnTextureBuilderSetDepthStencilMode, void, NVNtextureBuilder *b, NVNtextureDepthStencilMode m)
{
    if (m != NVN_TEXTURE_DEPTH_STENCIL_MODE_DEPTH)
    {
        pv_logf("nvnpv: stencil-plane texture sampling requested (not implemented)");
    }
}

NVN(nvnTextureBuilderSetPackagedTextureLayout, void, NVNtextureBuilder *b, const NVNpackagedTextureLayout *l)
{
    TB(b)->has_layout = l != NULL;

    if (l)
    {
        memcpy(TB(b)->layout, l->layout, 8);
    }

    TB(b)->d.pkg_block_h = l ? (int8_t)(l->layout[0] + 1) : 0;
}

NVN(nvnTextureBuilderSetPackagedTextureData, void, NVNtextureBuilder *b, const void *data) { TB(b)->pkg_data = data; }
NVN(nvnTextureBuilderGetPackagedTextureData, const void *, const NVNtextureBuilder *b) { return CTB(b)->pkg_data; }

NVN_NOPROTO(nvnTextureBuilderGetPackagedTextureLayout, const NVNpackagedTextureLayout *, const NVNtextureBuilder *b)
{
    return CTB(b)->has_layout ? (const NVNpackagedTextureLayout *)(const void *)CTB(b)->layout : NULL;
}

static NVNstorageClass storage_class(const pv_texdesc *d) { return d->target == NVN_TEXTURE_TARGET_BUFFER ? 0 : 1; }

NVN(nvnTextureBuilderGetStorageClass, NVNstorageClass, const NVNtextureBuilder *b) { return storage_class(&CTB(b)->d); }
NVN(nvnTextureBuilderGetFlags, NVNtextureFlags, const NVNtextureBuilder *b) { return (NVNtextureFlags)CTB(b)->d.flags; }
NVN_NOPROTO(nvnTextureBuilderGetDevice, NVNdevice *, const NVNtextureBuilder *b) { return CTB(b)->dev; }
NVN(nvnTextureBuilderGetTarget, NVNtextureTarget, const NVNtextureBuilder *b) { return (NVNtextureTarget)CTB(b)->d.target; }
NVN(nvnTextureBuilderGetWidth, int, const NVNtextureBuilder *b) { return (int)CTB(b)->d.width; }
NVN(nvnTextureBuilderGetHeight, int, const NVNtextureBuilder *b) { return (int)CTB(b)->d.height; }
NVN(nvnTextureBuilderGetDepth, int, const NVNtextureBuilder *b) { return (int)CTB(b)->d.depth; }
NVN(nvnTextureBuilderGetLevels, int, const NVNtextureBuilder *b) { return (int)CTB(b)->d.levels; }
NVN(nvnTextureBuilderGetFormat, NVNformat, const NVNtextureBuilder *b) { return (NVNformat)CTB(b)->d.format; }
NVN(nvnTextureBuilderGetSamples, int, const NVNtextureBuilder *b) { return CTB(b)->d.samples; }
NVN(nvnTextureBuilderGetDepthStencilMode, NVNtextureDepthStencilMode, const NVNtextureBuilder *b) { return NVN_TEXTURE_DEPTH_STENCIL_MODE_DEPTH; }
NVN(nvnTextureBuilderGetStride, ptrdiff_t, const NVNtextureBuilder *b) { return (ptrdiff_t)CTB(b)->d.stride; }
NVN_NOPROTO(nvnTextureBuilderGetMemoryPool, const NVNmemoryPool *, const NVNtextureBuilder *b) { return (const NVNmemoryPool *)CTB(b)->pool; }
NVN(nvnTextureBuilderGetMemoryOffset, ptrdiff_t, const NVNtextureBuilder *b) { return CTB(b)->off; }

NVN(nvnTextureBuilderGetSwizzle, void, const NVNtextureBuilder *b, NVNtextureSwizzle *r, NVNtextureSwizzle *g, NVNtextureSwizzle *bl, NVNtextureSwizzle *a)
{
    *r = (NVNtextureSwizzle)CTB(b)->d.swizzle[0];
    *g = (NVNtextureSwizzle)CTB(b)->d.swizzle[1];
    *bl = (NVNtextureSwizzle)CTB(b)->d.swizzle[2];
    *a = (NVNtextureSwizzle)CTB(b)->d.swizzle[3];
}

NVN(nvnTextureBuilderGetStorageSize, size_t, const NVNtextureBuilder *b)
{
    pv_texlayout l;
    pv_tex_layout(&CTB(b)->d, &l);

    if (!l.bpb)
    {
        pv_logf("nvnpv: TextureBuilderGetStorageSize: unknown format %x", CTB(b)->d.format);
    }

    return l.size;
}

NVN(nvnTextureBuilderGetStorageAlignment, size_t, const NVNtextureBuilder *b)
{
    pv_texlayout l;
    pv_tex_layout(&CTB(b)->d, &l);

    return l.align;
}

NVN(nvnTextureBuilderGetZCullStorageSize, size_t, const NVNtextureBuilder *b) { return 0; }

NVN_NOPROTO(nvnTextureBuilderGetViewOffset, ptrdiff_t, const NVNtextureBuilder *b, const NVNtextureView *v)
{
    const pv_view *w = (const pv_view *)v;

    if (!w)
    {
        return 0;
    }

    pv_texlayout l;
    pv_tex_layout(&CTB(b)->d, &l);

    uint64_t off = w->set_levels ? pv_tex_level_offset(&CTB(b)->d, w->base_level) : 0;

    if (w->set_layers)
    {
        off += (uint64_t)w->min_layer * l.layer_size;
    }

    return (ptrdiff_t)off;
}

NVN(nvnTextureInitialize, NVNboolean, NVNtexture *t, const NVNtextureBuilder *b)
{
    const tex_builder_t *s = CTB(b);
    tex_t *o = TX(t);

    if (o->magic == TEX_MAGIC && o->t.serial)
    {
        pv_host(PV_HOST_TEX_FINAL, o->t.serial, o->t.addr, 0, 0);   // re-initialised without Finalize
    }

    memset(o, 0, sizeof *o);
    o->magic = TEX_MAGIC;
    o->t.d = s->d;
    o->pool = s->pool;
    o->off = s->off;
    o->has_layout = s->has_layout;
    memcpy(o->layout, s->layout, 8);
    pv_tex_layout(&o->t.d, &o->t.l);
    o->t.addr = s->pool && s->pool->mem ? (uint64_t)(uintptr_t)(s->pool->mem + s->off) : 0;
    o->t.serial = pv_new_serial();

    if (s->pkg_data)
    {
        if (!o->t.addr || (s->pool->flags & (NVN_MEMORY_POOL_FLAGS_VIRTUAL | NVN_MEMORY_POOL_FLAGS_CPU_NO_ACCESS)))
        {
            pv_logf("nvnpv: packaged texture data for storage the CPU cannot write (pool flags %x)", s->pool ? s->pool->flags : 0);
            return 0;
        }

        // Already laid out for the GPU: copied into the storage, where the host picks it up as
        // any other CPU write to a texture's memory.
        memcpy((void *)(uintptr_t)o->t.addr, s->pkg_data, (size_t)o->t.l.size);
    }

    return 1;
}

NVN(nvnTextureFinalize, void, NVNtexture *t)
{
    if (TX(t)->magic == TEX_MAGIC)
    {
        pv_host(PV_HOST_TEX_FINAL, TX(t)->t.serial, TX(t)->t.addr, 0, 0);
    }

    TX(t)->magic = 0;
}

NVN(nvnTextureSetDebugLabel, void, NVNtexture *o, const char *s) { (void)o; (void)s; }
NVN(nvnTextureGetStorageClass, NVNstorageClass, const NVNtexture *t) { return storage_class(&CTX(t)->t.d); }
NVN(nvnTextureGetFlags, NVNtextureFlags, const NVNtexture *t) { return (NVNtextureFlags)CTX(t)->t.d.flags; }
NVN(nvnTextureGetTarget, NVNtextureTarget, const NVNtexture *t) { return (NVNtextureTarget)CTX(t)->t.d.target; }
NVN(nvnTextureGetWidth, int, const NVNtexture *t) { return (int)CTX(t)->t.d.width; }
NVN(nvnTextureGetHeight, int, const NVNtexture *t) { return (int)CTX(t)->t.d.height; }
NVN(nvnTextureGetDepth, int, const NVNtexture *t) { return (int)CTX(t)->t.d.depth; }
NVN(nvnTextureGetLevels, int, const NVNtexture *t) { return (int)CTX(t)->t.d.levels; }
NVN(nvnTextureGetFormat, NVNformat, const NVNtexture *t) { return (NVNformat)CTX(t)->t.d.format; }
NVN(nvnTextureGetSamples, int, const NVNtexture *t) { return CTX(t)->t.d.samples; }
NVN(nvnTextureGetDepthStencilMode, NVNtextureDepthStencilMode, const NVNtexture *t) { return NVN_TEXTURE_DEPTH_STENCIL_MODE_DEPTH; }
NVN(nvnTextureGetStride, ptrdiff_t, const NVNtexture *t) { return (ptrdiff_t)CTX(t)->t.d.stride; }
NVN(nvnTextureGetTextureAddress, NVNtextureAddress, const NVNtexture *t) { return (NVNtextureAddress)CTX(t)->t.addr; }
NVN_NOPROTO(nvnTextureGetMemoryPool, const NVNmemoryPool *, const NVNtexture *t) { return (const NVNmemoryPool *)CTX(t)->pool; }
NVN(nvnTextureGetMemoryOffset, ptrdiff_t, const NVNtexture *t) { return CTX(t)->off; }
NVN(nvnTextureGetStorageSize, int, const NVNtexture *t) { return (int)CTX(t)->t.l.size; }
NVN(nvnTextureGetDebugID, uint64_t, const NVNtexture *t) { return CTX(t)->t.serial; }
NVN(nvnTextureGetZCullStorageSize, size_t, const NVNtexture *t) { return 0; }
NVN_NOPROTO(nvnTextureGetRawStorageClass, int, const NVNtexture *t) { return 1; }

NVN(nvnTextureGetSwizzle, void, const NVNtexture *t, NVNtextureSwizzle *r, NVNtextureSwizzle *g, NVNtextureSwizzle *b, NVNtextureSwizzle *a)
{
    *r = (NVNtextureSwizzle)CTX(t)->t.d.swizzle[0];
    *g = (NVNtextureSwizzle)CTX(t)->t.d.swizzle[1];
    *b = (NVNtextureSwizzle)CTX(t)->t.d.swizzle[2];
    *a = (NVNtextureSwizzle)CTX(t)->t.d.swizzle[3];
}

NVN(nvnTextureCompare, NVNboolean, const NVNtexture *a, const NVNtexture *b)
{
    const tex_t *x = CTX(a), *y = CTX(b);

    return !memcmp(&x->t.d, &y->t.d, sizeof x->t.d) && x->t.addr == y->t.addr;
}

NVN(nvnTextureGetViewOffset, ptrdiff_t, const NVNtexture *t, const NVNtextureView *v)
{
    const tex_t *o = CTX(t);
    const pv_view *w = (const pv_view *)v;

    if (!w)
    {
        return 0;
    }

    uint64_t off = w->set_levels ? pv_tex_level_offset(&o->t.d, w->base_level) : 0;

    if (w->set_layers)
    {
        off += (uint64_t)w->min_layer * o->t.l.layer_size;
    }

    return (ptrdiff_t)off;
}

static void texels(const tex_t *o, const NVNtextureView *v, const NVNcopyRegion *r, const void *data, ptrdiff_t row, ptrdiff_t img, int write)
{
    pv_texels q;
    memset(&q, 0, sizeof q);
    q.write = (uint64_t)write;
    q.data = (uint64_t)(uintptr_t)data;
    q.row = row;
    q.image = img;
    q.tex = o->t;
    q.has_view = v != NULL;

    if (v)
    {
        q.view = *(const pv_view *)v;
    }

    q.r = *r;
    pv_host(PV_HOST_TEXELS, (uint64_t)(uintptr_t)&q, 0, 0, 0);
}

NVN(nvnTextureWriteTexels, void, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, const void *src) { texels(CTX(t), v, r, src, 0, 0, 1); }
NVN(nvnTextureWriteTexelsStrided, void, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, const void *src, ptrdiff_t row, ptrdiff_t img) { texels(CTX(t), v, r, src, row, img, 1); }
NVN(nvnTextureReadTexels, void, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, void *dst) { texels(CTX(t), v, r, dst, 0, 0, 0); }
NVN(nvnTextureReadTexelsStrided, void, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, void *dst, ptrdiff_t row, ptrdiff_t img) { texels(CTX(t), v, r, dst, row, img, 0); }
NVN(nvnTextureFlushTexels, void, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r) { (void)t; (void)v; (void)r; }
NVN(nvnTextureInvalidateTexels, void, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r) { (void)t; (void)v; (void)r; }

// ---------- Texture views (16 bytes, on the game's stack) ----------

#define VW(v) ((pv_view *)(v))
#define CVW(v) ((const pv_view *)(v))

NVN(nvnTextureViewSetDefaults, void, NVNtextureView *v) { memset(v, 0, sizeof(pv_view)); }
NVN(nvnTextureViewSetLevels, void, NVNtextureView *v, int base, int n) { VW(v)->set_levels = 1; VW(v)->base_level = (uint8_t)base; VW(v)->nlevels = (uint8_t)n; }
NVN(nvnTextureViewSetLayers, void, NVNtextureView *v, int min, int n) { VW(v)->set_layers = 1; VW(v)->min_layer = (uint16_t)min; VW(v)->nlayers = (uint16_t)n; }
NVN(nvnTextureViewSetFormat, void, NVNtextureView *v, NVNformat f) { VW(v)->set_format = 1; VW(v)->format = (uint16_t)f; }
NVN(nvnTextureViewSetTarget, void, NVNtextureView *v, NVNtextureTarget t) { VW(v)->set_target = 1; VW(v)->target = (uint8_t)t; }

NVN(nvnTextureViewSetSwizzle, void, NVNtextureView *v, NVNtextureSwizzle r, NVNtextureSwizzle g, NVNtextureSwizzle b, NVNtextureSwizzle a)
{
    VW(v)->set_swizzle = 1;
    VW(v)->swz[0] = (uint8_t)r;
    VW(v)->swz[1] = (uint8_t)g;
    VW(v)->swz[2] = (uint8_t)b;
    VW(v)->swz[3] = (uint8_t)a;
}

NVN(nvnTextureViewSetDepthStencilMode, void, NVNtextureView *v, NVNtextureDepthStencilMode m)
{
    if (m != NVN_TEXTURE_DEPTH_STENCIL_MODE_DEPTH)
    {
        pv_logf("nvnpv: stencil-plane texture view requested (not implemented)");
    }
}

NVN(nvnTextureViewGetLevels, NVNboolean, const NVNtextureView *v, int *base, int *n) { *base = CVW(v)->base_level; *n = CVW(v)->nlevels; return CVW(v)->set_levels; }
NVN(nvnTextureViewGetLayers, NVNboolean, const NVNtextureView *v, int *min, int *n) { *min = CVW(v)->min_layer; *n = CVW(v)->nlayers; return CVW(v)->set_layers; }
NVN(nvnTextureViewGetFormat, NVNboolean, const NVNtextureView *v, NVNformat *f) { *f = (NVNformat)CVW(v)->format; return CVW(v)->set_format; }
NVN(nvnTextureViewGetTarget, NVNboolean, const NVNtextureView *v, NVNtextureTarget *t) { *t = (NVNtextureTarget)CVW(v)->target; return CVW(v)->set_target; }
NVN(nvnTextureViewGetDepthStencilMode, NVNboolean, const NVNtextureView *v, NVNtextureDepthStencilMode *m) { *m = NVN_TEXTURE_DEPTH_STENCIL_MODE_DEPTH; return 0; }

NVN(nvnTextureViewGetSwizzle, NVNboolean, const NVNtextureView *v, NVNtextureSwizzle *r, NVNtextureSwizzle *g, NVNtextureSwizzle *b, NVNtextureSwizzle *a)
{
    const pv_view *o = CVW(v);
    *r = (NVNtextureSwizzle)o->swz[0];
    *g = (NVNtextureSwizzle)o->swz[1];
    *b = (NVNtextureSwizzle)o->swz[2];
    *a = (NVNtextureSwizzle)o->swz[3];

    return o->set_swizzle;
}

NVN(nvnTextureViewCompare, NVNboolean, const NVNtextureView *a, const NVNtextureView *b)
{
    const pv_view *x = CVW(a), *y = CVW(b);

    if (x->set_levels != y->set_levels || x->set_layers != y->set_layers || x->set_format != y->set_format || x->set_target != y->set_target || x->set_swizzle != y->set_swizzle) return 0;
    if (x->set_levels && (x->base_level != y->base_level || x->nlevels != y->nlevels)) return 0;
    if (x->set_layers && (x->min_layer != y->min_layer || x->nlayers != y->nlayers)) return 0;
    if (x->set_format && x->format != y->format) return 0;
    if (x->set_target && x->target != y->target) return 0;

    return !x->set_swizzle || !memcmp(x->swz, y->swz, 4);
}

// ---------- Samplers ----------

#define SB(b) (&((samp_builder_t *)(b))->p)
#define CSB(b) (&((const samp_builder_t *)(b))->p)
#define CSP(s) (&((const samp_t *)(s))->p)

NVN(nvnSamplerBuilderSetDefaults, void, NVNsamplerBuilder *b)
{
    NVNdevice *dev = ((samp_builder_t *)b)->dev;
    memset(b, 0, sizeof(samp_builder_t));
    ((samp_builder_t *)b)->dev = dev;

    pv_samp *o = SB(b);
    o->minf = NVN_MIN_FILTER_NEAREST_MIPMAP_LINEAR;   // GL defaults
    o->magf = NVN_MAG_FILTER_LINEAR;
    o->ws = o->wt = o->wr = NVN_WRAP_MODE_REPEAT;
    o->cmp_func = NVN_COMPARE_FUNC_LESS;
    o->lod_min = -1000.f;
    o->lod_max = 1000.f;
    o->aniso = 1.f;
}

NVN(nvnSamplerBuilderSetDevice, void, NVNsamplerBuilder *b, NVNdevice *d) { ((samp_builder_t *)b)->dev = d; }
NVN_NOPROTO(nvnSamplerBuilderGetDevice, NVNdevice *, const NVNsamplerBuilder *b) { return ((const samp_builder_t *)b)->dev; }
NVN(nvnSamplerBuilderSetMinMagFilter, void, NVNsamplerBuilder *b, NVNminFilter mi, NVNmagFilter ma) { SB(b)->minf = (uint8_t)mi; SB(b)->magf = (uint8_t)ma; }
NVN(nvnSamplerBuilderSetWrapMode, void, NVNsamplerBuilder *b, NVNwrapMode s, NVNwrapMode t, NVNwrapMode r) { SB(b)->ws = (uint8_t)s; SB(b)->wt = (uint8_t)t; SB(b)->wr = (uint8_t)r; }
NVN(nvnSamplerBuilderSetLodClamp, void, NVNsamplerBuilder *b, float lo, float hi) { SB(b)->lod_min = lo; SB(b)->lod_max = hi; }
NVN(nvnSamplerBuilderSetLodBias, void, NVNsamplerBuilder *b, float v) { SB(b)->lod_bias = v; }
NVN(nvnSamplerBuilderSetCompare, void, NVNsamplerBuilder *b, NVNcompareMode m, NVNcompareFunc f) { SB(b)->cmp_mode = (uint8_t)m; SB(b)->cmp_func = (uint8_t)f; }
NVN(nvnSamplerBuilderSetBorderColor, void, NVNsamplerBuilder *b, const float *c) { memcpy(SB(b)->border, c, 16); SB(b)->border_int = 0; }
NVN(nvnSamplerBuilderSetBorderColori, void, NVNsamplerBuilder *b, const int *c) { memcpy(SB(b)->border, c, 16); SB(b)->border_int = 1; }
NVN(nvnSamplerBuilderSetBorderColorui, void, NVNsamplerBuilder *b, const uint32_t *c) { memcpy(SB(b)->border, c, 16); SB(b)->border_int = 2; }
NVN(nvnSamplerBuilderSetMaxAnisotropy, void, NVNsamplerBuilder *b, float v) { SB(b)->aniso = v; }
NVN(nvnSamplerBuilderSetReductionFilter, void, NVNsamplerBuilder *b, NVNsamplerReduction r) { SB(b)->reduction = (uint8_t)r; }
NVN(nvnSamplerBuilderSetLodSnap, void, NVNsamplerBuilder *b, float v) { SB(b)->lod_snap = v; }

NVN(nvnSamplerBuilderGetMinMagFilter, void, const NVNsamplerBuilder *b, NVNminFilter *mi, NVNmagFilter *ma) { *mi = (NVNminFilter)CSB(b)->minf; *ma = (NVNmagFilter)CSB(b)->magf; }
NVN(nvnSamplerBuilderGetWrapMode, void, const NVNsamplerBuilder *b, NVNwrapMode *s, NVNwrapMode *t, NVNwrapMode *r) { *s = (NVNwrapMode)CSB(b)->ws; *t = (NVNwrapMode)CSB(b)->wt; *r = (NVNwrapMode)CSB(b)->wr; }
NVN(nvnSamplerBuilderGetLodClamp, void, const NVNsamplerBuilder *b, float *lo, float *hi) { *lo = CSB(b)->lod_min; *hi = CSB(b)->lod_max; }
NVN(nvnSamplerBuilderGetLodBias, float, const NVNsamplerBuilder *b) { return CSB(b)->lod_bias; }
NVN(nvnSamplerBuilderGetCompare, void, const NVNsamplerBuilder *b, NVNcompareMode *m, NVNcompareFunc *f) { *m = (NVNcompareMode)CSB(b)->cmp_mode; *f = (NVNcompareFunc)CSB(b)->cmp_func; }
NVN(nvnSamplerBuilderGetBorderColor, void, const NVNsamplerBuilder *b, float *c) { memcpy(c, CSB(b)->border, 16); }
NVN(nvnSamplerBuilderGetBorderColori, void, const NVNsamplerBuilder *b, int *c) { memcpy(c, CSB(b)->border, 16); }
NVN(nvnSamplerBuilderGetBorderColorui, void, const NVNsamplerBuilder *b, uint32_t *c) { memcpy(c, CSB(b)->border, 16); }
NVN(nvnSamplerBuilderGetMaxAnisotropy, float, const NVNsamplerBuilder *b) { return CSB(b)->aniso; }
NVN(nvnSamplerBuilderGetReductionFilter, NVNsamplerReduction, const NVNsamplerBuilder *b) { return (NVNsamplerReduction)CSB(b)->reduction; }
NVN(nvnSamplerBuilderGetLodSnap, float, const NVNsamplerBuilder *b) { return CSB(b)->lod_snap; }

NVN(nvnSamplerInitialize, NVNboolean, NVNsampler *s, const NVNsamplerBuilder *b) { ((samp_t *)s)->p = *CSB(b); return 1; }
NVN(nvnSamplerFinalize, void, NVNsampler *s) { (void)s; }
NVN(nvnSamplerSetDebugLabel, void, NVNsampler *o, const char *s) { (void)o; (void)s; }
NVN(nvnSamplerGetMinMagFilter, void, const NVNsampler *s, NVNminFilter *mi, NVNmagFilter *ma) { *mi = (NVNminFilter)CSP(s)->minf; *ma = (NVNmagFilter)CSP(s)->magf; }
NVN(nvnSamplerGetWrapMode, void, const NVNsampler *s, NVNwrapMode *a, NVNwrapMode *b, NVNwrapMode *c) { *a = (NVNwrapMode)CSP(s)->ws; *b = (NVNwrapMode)CSP(s)->wt; *c = (NVNwrapMode)CSP(s)->wr; }
NVN(nvnSamplerGetLodClamp, void, const NVNsampler *s, float *lo, float *hi) { *lo = CSP(s)->lod_min; *hi = CSP(s)->lod_max; }
NVN(nvnSamplerGetLodBias, float, const NVNsampler *s) { return CSP(s)->lod_bias; }
NVN(nvnSamplerGetCompare, void, const NVNsampler *s, NVNcompareMode *m, NVNcompareFunc *f) { *m = (NVNcompareMode)CSP(s)->cmp_mode; *f = (NVNcompareFunc)CSP(s)->cmp_func; }
NVN(nvnSamplerGetBorderColor, void, const NVNsampler *s, float *c) { memcpy(c, CSP(s)->border, 16); }
NVN(nvnSamplerGetBorderColori, void, const NVNsampler *s, int *c) { memcpy(c, CSP(s)->border, 16); }
NVN(nvnSamplerGetBorderColorui, void, const NVNsampler *s, uint32_t *c) { memcpy(c, CSP(s)->border, 16); }
NVN(nvnSamplerGetMaxAnisotropy, float, const NVNsampler *s) { return CSP(s)->aniso; }
NVN(nvnSamplerGetReductionFilter, NVNsamplerReduction, const NVNsampler *s) { return (NVNsamplerReduction)CSP(s)->reduction; }
NVN(nvnSamplerCompare, NVNboolean, const NVNsampler *a, const NVNsampler *b) { return !memcmp(CSP(a), CSP(b), sizeof(pv_samp)); }
NVN(nvnSamplerGetDebugID, uint64_t, const NVNsampler *s) { return (uint64_t)(uintptr_t)s; }

// ---------- Descriptor pools: shadow tables in the arena, read by the host at replay ----------

NVN(nvnTexturePoolInitialize, NVNboolean, NVNtexturePool *tp, const NVNmemoryPool *p, ptrdiff_t off, int n)
{
    dpool_t *o = (dpool_t *)tp;
    *o = (dpool_t){ (const pool_t *)p, off, n, 0, NULL };
    o->table = n > 0 ? pv_arena_alloc((uint64_t)n * sizeof(pv_tex_entry), 64) : NULL;

    if (o->table)
    {
        memset(o->table, 0, (size_t)n * sizeof(pv_tex_entry));
    }

    pv_logf("nvnpv: texture pool of %d descriptors, table %p", n, o->table);

    return o->table != NULL || n == 0;
}

NVN(nvnTexturePoolFinalize, void, NVNtexturePool *tp) { (void)tp; }
NVN(nvnTexturePoolSetDebugLabel, void, NVNtexturePool *o, const char *s) { (void)o; (void)s; }
NVN(nvnTexturePoolGetMemoryPool, const NVNmemoryPool *, const NVNtexturePool *tp) { return (const NVNmemoryPool *)((const dpool_t *)tp)->pool; }
NVN(nvnTexturePoolGetMemoryOffset, ptrdiff_t, const NVNtexturePool *tp) { return ((const dpool_t *)tp)->off; }
NVN(nvnTexturePoolGetSize, int, const NVNtexturePool *tp) { return ((const dpool_t *)tp)->n; }

NVN(nvnTexturePoolRegisterTexture, void, const NVNtexturePool *tp, int id, const NVNtexture *t, const NVNtextureView *v)
{
    const dpool_t *o = (const dpool_t *)tp;

    if (id < 0 || id >= o->n || !o->table)
    {
        pv_logf("nvnpv: RegisterTexture id %d outside a pool of %d", id, o->n);
        return;
    }

    pv_tex_entry *e = &((pv_tex_entry *)o->table)[id];
    e->tex = CTX(t)->t;
    e->has_view = v != NULL;

    if (v)
    {
        e->view = *CVW(v);
    }
    else
    {
        memset(&e->view, 0, sizeof e->view);
    }

    e->valid = 1;
    __atomic_add_fetch(&pv_ctl.pool_gen, 1, __ATOMIC_RELEASE);
}

// Images share the texture registrations (the game does not call RegisterImage on its own).
NVN(nvnTexturePoolRegisterImage, void, const NVNtexturePool *tp, int id, const NVNtexture *t, const NVNtextureView *v)
{
    h_nvnTexturePoolRegisterTexture(tp, id, t, v);
}

NVN(nvnSamplerPoolInitialize, NVNboolean, NVNsamplerPool *sp, const NVNmemoryPool *p, ptrdiff_t off, int n)
{
    dpool_t *o = (dpool_t *)sp;
    *o = (dpool_t){ (const pool_t *)p, off, n, 0, NULL };
    o->table = n > 0 ? pv_arena_alloc((uint64_t)n * sizeof(pv_samp_entry), 64) : NULL;

    if (o->table)
    {
        memset(o->table, 0, (size_t)n * sizeof(pv_samp_entry));
    }

    pv_logf("nvnpv: sampler pool of %d descriptors, table %p", n, o->table);

    return o->table != NULL || n == 0;
}

NVN(nvnSamplerPoolFinalize, void, NVNsamplerPool *sp) { (void)sp; }
NVN(nvnSamplerPoolSetDebugLabel, void, NVNsamplerPool *o, const char *s) { (void)o; (void)s; }
NVN(nvnSamplerPoolGetMemoryPool, const NVNmemoryPool *, const NVNsamplerPool *sp) { return (const NVNmemoryPool *)((const dpool_t *)sp)->pool; }
NVN(nvnSamplerPoolGetMemoryOffset, ptrdiff_t, const NVNsamplerPool *sp) { return ((const dpool_t *)sp)->off; }
NVN(nvnSamplerPoolGetSize, int, const NVNsamplerPool *sp) { return ((const dpool_t *)sp)->n; }

NVN(nvnSamplerPoolRegisterSampler, void, const NVNsamplerPool *sp, int id, const NVNsampler *s)
{
    const dpool_t *o = (const dpool_t *)sp;

    if (id < 0 || id >= o->n || !o->table)
    {
        pv_logf("nvnpv: RegisterSampler id %d outside a pool of %d", id, o->n);
        return;
    }

    pv_samp_entry *e = &((pv_samp_entry *)o->table)[id];
    e->samp = *CSP(s);
    e->valid = 1;
    __atomic_add_fetch(&pv_ctl.pool_gen, 1, __ATOMIC_RELEASE);
}

NVN(nvnSamplerPoolRegisterSamplerBuilder, void, const NVNsamplerPool *sp, int id, const NVNsamplerBuilder *b)
{
    const dpool_t *o = (const dpool_t *)sp;

    if (id < 0 || id >= o->n || !o->table)
    {
        return;
    }

    pv_samp_entry *e = &((pv_samp_entry *)o->table)[id];
    e->samp = *CSB(b);
    e->valid = 1;
    __atomic_add_fetch(&pv_ctl.pool_gen, 1, __ATOMIC_RELEASE);
}

// ---------- Programs ----------

#define PROG_MAGIC MAGIC('P', 'R', 'O', 'G')

NVN(nvnProgramInitialize, NVNboolean, NVNprogram *p, NVNdevice *d) { memset(p, 0, sizeof(prog_t)); ((prog_t *)p)->magic = PROG_MAGIC; return 1; }
NVN(nvnProgramFinalize, void, NVNprogram *p) { ((prog_t *)p)->magic = 0; }
NVN(nvnProgramSetDebugLabel, void, NVNprogram *o, const char *s) { (void)o; (void)s; }

NVN(nvnProgramSetShaders, NVNboolean, NVNprogram *p, int n, const NVNshaderData *sd)
{
    prog_t *o = (prog_t *)p;

    if (n < 0 || n > 6)
    {
        pv_logf("nvnpv: ProgramSetShaders with %d stages", n);
        return 0;
    }

    o->nstage = (uint32_t)n;
    memcpy(o->sd, sd, (size_t)n * sizeof *sd);
    o->serial = pv_new_serial();
    pv_host(PV_HOST_PROGRAM, (uint64_t)(uintptr_t)o, 0, 0, 0);

    return 1;
}

// NVNshaderDataExt = {data, control, reserved}, 0x18 bytes (MacNativeGames, runtime dump of the game's call).
typedef struct { NVNbufferAddress data; const void *control; uint64_t reserved; } pv_shader_data_ext;

NVN_NOPROTO(nvnProgramSetShadersExt, NVNboolean, NVNprogram *p, int n, const pv_shader_data_ext *sd)
{
    if (n < 0 || n > 6)
    {
        return 0;
    }

    NVNshaderData plain[6];

    for (int i = 0; i < n; i++)
    {
        plain[i] = (NVNshaderData){ sd[i].data, sd[i].control };
    }

    return h_nvnProgramSetShaders(p, n, plain);
}

NVN_NOPROTO(nvnProgramGetSubtileSize, void, const NVNprogram *p) { (void)p; }
NVN_NOPROTO(nvnProgramSetSampleShading, void, NVNprogram *p, int m) { (void)p; (void)m; }

// ---------- Syncs and events ----------

#define SYNC_MAGIC MAGIC('S', 'Y', 'N', 'C')

NVN(nvnSyncInitialize, NVNboolean, NVNsync *s, NVNdevice *d) { *(sync_t *)s = (sync_t){ SYNC_MAGIC, 0, 0 }; return 1; }
NVN(nvnSyncFinalize, void, NVNsync *s) { ((sync_t *)s)->magic = 0; }
NVN(nvnSyncSetDebugLabel, void, NVNsync *o, const char *s) { (void)o; (void)s; }

static int fence_done(uint64_t fence)
{
    return __atomic_load_n(&pv_ctl.completed_fence, __ATOMIC_ACQUIRE) >= fence;
}

// A sync never fenced waits until the timeout, as on hardware; a fenced one is released once the
// host reports every submission up to its fence complete.
NVN(nvnSyncWait, NVNsyncWaitResult, const NVNsync *s, uint64_t timeout)
{
    const sync_t *o = (const sync_t *)s;

    if (!o->fenced)
    {
        return NVN_SYNC_WAIT_RESULT_TIMEOUT_EXPIRED;
    }

    if (fence_done(o->fence))
    {
        return NVN_SYNC_WAIT_RESULT_ALREADY_SIGNALED;
    }

    if (!timeout)
    {
        return NVN_SYNC_WAIT_RESULT_TIMEOUT_EXPIRED;
    }

    return pv_host(PV_HOST_WAIT, o->fence, timeout, 0, 0) ? NVN_SYNC_WAIT_RESULT_CONDITION_SATISFIED : NVN_SYNC_WAIT_RESULT_TIMEOUT_EXPIRED;
}

NVN_NOPROTO(nvnSyncInitializeFromFencedGLSync, NVNboolean, NVNsync *s, NVNdevice *d, const void *gl, int *st) { return 0; }
NVN_NOPROTO(nvnSyncCreateGLSync, int, const NVNsync *s, uint64_t *gl) { return 0; }

#define EV_MAGIC MAGIC('E', 'V', 'N', 'T')

NVN(nvnEventBuilderSetDefaults, void, NVNeventBuilder *b) { memset(b, 0, sizeof(ev_builder_t)); }
NVN(nvnEventBuilderSetStorage, void, NVNeventBuilder *b, const NVNmemoryPool *p, int64_t off) { *(ev_builder_t *)b = (ev_builder_t){ (const pool_t *)p, off }; }

NVN(nvnEventInitialize, NVNboolean, NVNevent *e, const NVNeventBuilder *b)
{
    const ev_builder_t *s = (const ev_builder_t *)b;

    if (!s->pool || !s->pool->mem)
    {
        pv_log("nvnpv: EventInitialize without storage");
        return 0;
    }

    *(ev_t *)e = (ev_t){ EV_MAGIC, 0, (volatile uint32_t *)(s->pool->mem + s->off) };

    return 1;
}

NVN(nvnEventFinalize, void, NVNevent *e) { ((ev_t *)e)->magic = 0; }
NVN(nvnEventGetValue, uint32_t, const NVNevent *e) { return *((const ev_t *)e)->val; }

NVN(nvnEventSignal, void, NVNevent *e, NVNeventSignalMode m, uint32_t v)
{
    volatile uint32_t *p = ((const ev_t *)e)->val;

    if (m == NVN_EVENT_SIGNAL_MODE_ADD)
    {
        __atomic_fetch_add(p, v, __ATOMIC_RELEASE);
    }
    else
    {
        __atomic_store_n(p, v, __ATOMIC_RELEASE);
    }
}

// ---------- State objects (packed in place) ----------

#define ST(T, x) ((T *)(x))
#define CS(T, x) ((const T *)(const void *)(x))

NVN(nvnBlendStateSetDefaults, void, NVNblendState *s)
{
    blend_st v = { 0 };
    v.src_rgb = NVN_BLEND_FUNC_ONE;
    v.dst_rgb = NVN_BLEND_FUNC_ZERO;
    v.src_a = NVN_BLEND_FUNC_ONE;
    v.dst_a = NVN_BLEND_FUNC_ZERO;
    v.eq_rgb = NVN_BLEND_EQUATION_ADD;
    v.eq_a = NVN_BLEND_EQUATION_ADD;
    *ST(blend_st, s) = v;
}

NVN(nvnBlendStateSetBlendTarget, void, NVNblendState *s, int t) { ST(blend_st, s)->target = (uint64_t)t; }

NVN(nvnBlendStateSetBlendFunc, void, NVNblendState *s, NVNblendFunc sr, NVNblendFunc dr, NVNblendFunc sa, NVNblendFunc da)
{
    blend_st *o = ST(blend_st, s);
    o->src_rgb = sr;
    o->dst_rgb = dr;
    o->src_a = sa;
    o->dst_a = da;
}

NVN(nvnBlendStateSetBlendEquation, void, NVNblendState *s, NVNblendEquation er, NVNblendEquation ea) { ST(blend_st, s)->eq_rgb = er; ST(blend_st, s)->eq_a = ea; }
NVN(nvnBlendStateSetAdvancedMode, void, NVNblendState *s, NVNblendAdvancedMode m) { if (m) pv_logf("nvnpv: advanced blend mode %d not implemented", (int)m); }
NVN(nvnBlendStateSetAdvancedOverlap, void, NVNblendState *s, NVNblendAdvancedOverlap o) { (void)s; (void)o; }
NVN(nvnBlendStateSetAdvancedPremultipliedSrc, void, NVNblendState *s, uint32_t v) { (void)s; (void)v; }
NVN(nvnBlendStateSetAdvancedNormalizedDst, void, NVNblendState *s, uint32_t v) { (void)s; (void)v; }
NVN(nvnBlendStateGetBlendTarget, int, const NVNblendState *s) { return (int)CS(blend_st, s)->target; }

NVN(nvnBlendStateGetBlendFunc, void, const NVNblendState *s, NVNblendFunc *sr, NVNblendFunc *dr, NVNblendFunc *sa, NVNblendFunc *da)
{
    const blend_st *b = CS(blend_st, s);
    *sr = (NVNblendFunc)b->src_rgb;
    *dr = (NVNblendFunc)b->dst_rgb;
    *sa = (NVNblendFunc)b->src_a;
    *da = (NVNblendFunc)b->dst_a;
}

NVN(nvnBlendStateGetBlendEquation, void, const NVNblendState *s, NVNblendEquation *rgb, NVNblendEquation *a) { *rgb = (NVNblendEquation)CS(blend_st, s)->eq_rgb; *a = (NVNblendEquation)CS(blend_st, s)->eq_a; }
NVN(nvnBlendStateGetAdvancedMode, NVNblendAdvancedMode, const NVNblendState *s) { return (NVNblendAdvancedMode)0; }
NVN(nvnBlendStateGetAdvancedOverlap, NVNblendAdvancedOverlap, const NVNblendState *s) { return (NVNblendAdvancedOverlap)0; }
NVN(nvnBlendStateGetAdvancedPremultipliedSrc, NVNboolean, const NVNblendState *s) { return 0; }
NVN(nvnBlendStateGetAdvancedNormalizedDst, NVNboolean, const NVNblendState *s) { return 0; }

NVN(nvnColorStateSetDefaults, void, NVNcolorState *s) { color_st v = { 0 }; v.logic_op = NVN_LOGIC_OP_COPY; v.alpha_func = NVN_ALPHA_FUNC_ALWAYS; *ST(color_st, s) = v; }

NVN(nvnColorStateSetBlendEnable, void, NVNcolorState *s, int i, uint32_t en)
{
    color_st *o = ST(color_st, s);

    if (i < 0 || i > 7)
    {
        return;
    }

    o->blend_en = B8(en) ? (o->blend_en | 1u << i) : (o->blend_en & ~(1u << i));
}

NVN(nvnColorStateSetLogicOp, void, NVNcolorState *s, NVNlogicOp op) { ST(color_st, s)->logic_op = op; }
NVN(nvnColorStateSetAlphaTest, void, NVNcolorState *s, NVNalphaFunc f) { ST(color_st, s)->alpha_func = f; }
NVN(nvnColorStateGetBlendEnable, NVNboolean, const NVNcolorState *s, int i) { return i >= 0 && i < 8 && (CS(color_st, s)->blend_en >> i & 1); }
NVN(nvnColorStateGetLogicOp, NVNlogicOp, const NVNcolorState *s) { return (NVNlogicOp)CS(color_st, s)->logic_op; }
NVN(nvnColorStateGetAlphaTest, NVNalphaFunc, const NVNcolorState *s) { return (NVNalphaFunc)CS(color_st, s)->alpha_func; }

NVN(nvnChannelMaskStateSetDefaults, void, NVNchannelMaskState *s) { ST(chmask_st, s)->mask = 0xffffffffu; }

NVN(nvnChannelMaskStateSetChannelMask, void, NVNchannelMaskState *s, int i, uint32_t r, uint32_t g, uint32_t b, uint32_t a)
{
    if (i < 0 || i > 7)
    {
        return;
    }

    uint32_t m = (B8(r) ? 1u : 0) | (B8(g) ? 2u : 0) | (B8(b) ? 4u : 0) | (B8(a) ? 8u : 0);
    chmask_st *o = ST(chmask_st, s);
    o->mask = (o->mask & ~(0xfu << (4 * i))) | m << (4 * i);
}

NVN(nvnChannelMaskStateGetChannelMask, void, const NVNchannelMaskState *s, int i, NVNboolean *r, NVNboolean *g, NVNboolean *b, NVNboolean *a)
{
    uint32_t m = i >= 0 && i < 8 ? CS(chmask_st, s)->mask >> (4 * i) : 0;
    *r = m & 1;
    *g = m >> 1 & 1;
    *b = m >> 2 & 1;
    *a = m >> 3 & 1;
}

NVN(nvnMultisampleStateSetDefaults, void, NVNmultisampleState *s) { memset(s, 0, sizeof(ms_st)); ST(ms_st, s)->enable = 1; }
NVN(nvnMultisampleStateSetMultisampleEnable, void, NVNmultisampleState *s, uint32_t e) { ST(ms_st, s)->enable = B8(e) != 0; }
NVN(nvnMultisampleStateSetSamples, void, NVNmultisampleState *s, int n) { ST(ms_st, s)->samples = (uint8_t)n; }
NVN(nvnMultisampleStateSetAlphaToCoverageEnable, void, NVNmultisampleState *s, uint32_t e) { ST(ms_st, s)->a2c = B8(e) != 0; }
NVN(nvnMultisampleStateSetAlphaToCoverageDither, void, NVNmultisampleState *s, uint32_t e) { ST(ms_st, s)->a2c_dither = B8(e) != 0; }
NVN(nvnMultisampleStateSetRasterSamples, void, NVNmultisampleState *s, int n) { ST(ms_st, s)->raster_samples = (uint8_t)n; }
NVN(nvnMultisampleStateSetCoverageModulationMode, void, NVNmultisampleState *s, NVNcoverageModulationMode m) { ST(ms_st, s)->cov_mod = (uint8_t)m; }
NVN(nvnMultisampleStateSetCoverageToColorEnable, void, NVNmultisampleState *s, uint32_t e) { ST(ms_st, s)->cov2color_en = B8(e) != 0; }
NVN(nvnMultisampleStateSetCoverageToColorOutput, void, NVNmultisampleState *s, int c) { ST(ms_st, s)->cov2color_out = (uint8_t)c; }
NVN(nvnMultisampleStateSetSampleLocationsEnable, void, NVNmultisampleState *s, uint32_t e) { ST(ms_st, s)->sl_en = B8(e) != 0; }
NVN(nvnMultisampleStateSetSampleLocationsGridEnable, void, NVNmultisampleState *s, uint32_t e) { ST(ms_st, s)->sl_grid = B8(e) != 0; }
NVN(nvnMultisampleStateSetSampleLocations, void, NVNmultisampleState *s, int start, int n, const float *v) { (void)s; (void)start; (void)n; (void)v; }
NVN(nvnMultisampleStateGetMultisampleEnable, NVNboolean, const NVNmultisampleState *s) { return CS(ms_st, s)->enable; }
NVN(nvnMultisampleStateGetSamples, int, const NVNmultisampleState *s) { return CS(ms_st, s)->samples; }
NVN(nvnMultisampleStateGetAlphaToCoverageEnable, NVNboolean, const NVNmultisampleState *s) { return CS(ms_st, s)->a2c; }
NVN(nvnMultisampleStateGetAlphaToCoverageDither, NVNboolean, const NVNmultisampleState *s) { return CS(ms_st, s)->a2c_dither; }
NVN(nvnMultisampleStateGetRasterSamples, int, NVNmultisampleState *s) { return CS(ms_st, s)->raster_samples; }
NVN(nvnMultisampleStateGetCoverageModulationMode, NVNcoverageModulationMode, const NVNmultisampleState *s) { return (NVNcoverageModulationMode)CS(ms_st, s)->cov_mod; }
NVN(nvnMultisampleStateGetCoverageToColorEnable, NVNboolean, const NVNmultisampleState *s) { return CS(ms_st, s)->cov2color_en; }
NVN(nvnMultisampleStateGetCoverageToColorOutput, int, const NVNmultisampleState *s) { return CS(ms_st, s)->cov2color_out; }
NVN(nvnMultisampleStateGetSampleLocationsEnable, NVNboolean, const NVNmultisampleState *s) { return CS(ms_st, s)->sl_en; }
NVN(nvnMultisampleStateGetSampleLocationsGridEnable, NVNboolean, const NVNmultisampleState *s) { return CS(ms_st, s)->sl_grid; }
NVN(nvnMultisampleStateGetSampleLocationsGrid, void, NVNmultisampleState *s, int *w, int *h) { *w = 1; *h = 1; }

NVN(nvnPolygonStateSetDefaults, void, NVNpolygonState *s) { poly_st v = { 0 }; v.cull = NVN_FACE_NONE; v.front = NVN_FRONT_FACE_CCW; v.mode = NVN_POLYGON_MODE_FILL; *ST(poly_st, s) = v; }
NVN(nvnPolygonStateSetCullFace, void, NVNpolygonState *s, NVNface f) { ST(poly_st, s)->cull = f; }
NVN(nvnPolygonStateSetFrontFace, void, NVNpolygonState *s, NVNfrontFace f) { ST(poly_st, s)->front = f; }
NVN(nvnPolygonStateSetPolygonMode, void, NVNpolygonState *s, NVNpolygonMode m) { ST(poly_st, s)->mode = m; }
NVN(nvnPolygonStateSetPolygonOffsetEnables, void, NVNpolygonState *s, int e) { ST(poly_st, s)->offset_en = (uint32_t)e; }
NVN(nvnPolygonStateGetCullFace, NVNface, const NVNpolygonState *s) { return (NVNface)CS(poly_st, s)->cull; }
NVN(nvnPolygonStateGetFrontFace, NVNfrontFace, const NVNpolygonState *s) { return (NVNfrontFace)CS(poly_st, s)->front; }
NVN(nvnPolygonStateGetPolygonMode, NVNpolygonMode, const NVNpolygonState *s) { return (NVNpolygonMode)CS(poly_st, s)->mode; }
NVN(nvnPolygonStateGetPolygonOffsetEnables, NVNpolygonOffsetEnable, const NVNpolygonState *s) { return (NVNpolygonOffsetEnable)CS(poly_st, s)->offset_en; }

NVN(nvnDepthStencilStateSetDefaults, void, NVNdepthStencilState *s)
{
    ds_st v = { 0 };
    v.dw = 1;
    v.df = NVN_DEPTH_FUNC_LESS;
    v.sf_f = v.sf_b = NVN_STENCIL_FUNC_ALWAYS;
    v.fail_f = v.zfail_f = v.zpass_f = v.fail_b = v.zfail_b = v.zpass_b = NVN_STENCIL_OP_KEEP;
    *ST(ds_st, s) = v;
}

NVN(nvnDepthStencilStateSetDepthTestEnable, void, NVNdepthStencilState *s, uint32_t e) { ST(ds_st, s)->dt = B8(e) != 0; }
NVN(nvnDepthStencilStateSetDepthWriteEnable, void, NVNdepthStencilState *s, uint32_t e) { ST(ds_st, s)->dw = B8(e) != 0; }
NVN(nvnDepthStencilStateSetDepthFunc, void, NVNdepthStencilState *s, NVNdepthFunc f) { ST(ds_st, s)->df = f; }
NVN(nvnDepthStencilStateSetStencilTestEnable, void, NVNdepthStencilState *s, uint32_t e) { ST(ds_st, s)->st = B8(e) != 0; }

NVN(nvnDepthStencilStateSetStencilFunc, void, NVNdepthStencilState *s, NVNface f, NVNstencilFunc fn)
{
    ds_st *o = ST(ds_st, s);

    if (f & NVN_FACE_FRONT) o->sf_f = fn;
    if (f & NVN_FACE_BACK) o->sf_b = fn;
}

NVN(nvnDepthStencilStateSetStencilOp, void, NVNdepthStencilState *s, NVNface f, NVNstencilOp fail, NVNstencilOp zfail, NVNstencilOp zpass)
{
    ds_st *o = ST(ds_st, s);

    if (f & NVN_FACE_FRONT) { o->fail_f = fail; o->zfail_f = zfail; o->zpass_f = zpass; }
    if (f & NVN_FACE_BACK) { o->fail_b = fail; o->zfail_b = zfail; o->zpass_b = zpass; }
}

NVN(nvnDepthStencilStateGetDepthTestEnable, NVNboolean, const NVNdepthStencilState *s) { return CS(ds_st, s)->dt; }
NVN(nvnDepthStencilStateGetDepthWriteEnable, NVNboolean, const NVNdepthStencilState *s) { return CS(ds_st, s)->dw; }
NVN(nvnDepthStencilStateGetDepthFunc, NVNdepthFunc, const NVNdepthStencilState *s) { return (NVNdepthFunc)CS(ds_st, s)->df; }
NVN(nvnDepthStencilStateGetStencilTestEnable, NVNboolean, const NVNdepthStencilState *s) { return CS(ds_st, s)->st; }
NVN(nvnDepthStencilStateGetStencilFunc, NVNstencilFunc, const NVNdepthStencilState *s, NVNface f) { return (NVNstencilFunc)(f == NVN_FACE_BACK ? CS(ds_st, s)->sf_b : CS(ds_st, s)->sf_f); }

NVN(nvnDepthStencilStateGetStencilOp, void, const NVNdepthStencilState *s, NVNface f, NVNstencilOp *fail, NVNstencilOp *zf, NVNstencilOp *zp)
{
    const ds_st *d = CS(ds_st, s);
    int back = f == NVN_FACE_BACK;

    *fail = (NVNstencilOp)(back ? d->fail_b : d->fail_f);
    *zf = (NVNstencilOp)(back ? d->zfail_b : d->zfail_f);
    *zp = (NVNstencilOp)(back ? d->zpass_b : d->zpass_f);
}

NVN(nvnVertexAttribStateSetDefaults, void, NVNvertexAttribState *s) { va_st v = { 0 }; *ST(va_st, s) = v; }
NVN(nvnVertexAttribStateSetFormat, void, NVNvertexAttribState *s, NVNformat f, ptrdiff_t off) { ST(va_st, s)->fmt = f; ST(va_st, s)->offset = (uint32_t)off; }
NVN(nvnVertexAttribStateSetStreamIndex, void, NVNvertexAttribState *s, int i) { ST(va_st, s)->stream = (uint32_t)i; }
NVN(nvnVertexAttribStateGetFormat, void, const NVNvertexAttribState *s, NVNformat *f, ptrdiff_t *off) { *f = (NVNformat)CS(va_st, s)->fmt; *off = (ptrdiff_t)CS(va_st, s)->offset; }
NVN(nvnVertexAttribStateGetStreamIndex, int, const NVNvertexAttribState *s) { return (int)CS(va_st, s)->stream; }
NVN(nvnVertexStreamStateSetDefaults, void, NVNvertexStreamState *s) { *ST(vs_st, s) = (vs_st){ 0, 0 }; }
NVN(nvnVertexStreamStateSetStride, void, NVNvertexStreamState *s, ptrdiff_t st) { ST(vs_st, s)->stride = (uint32_t)st; }
NVN(nvnVertexStreamStateSetDivisor, void, NVNvertexStreamState *s, int d) { ST(vs_st, s)->divisor = (uint32_t)d; }
NVN(nvnVertexStreamStateGetStride, ptrdiff_t, const NVNvertexStreamState *s) { return (ptrdiff_t)CS(vs_st, s)->stride; }
NVN(nvnVertexStreamStateGetDivisor, int, const NVNvertexStreamState *s) { return (int)CS(vs_st, s)->divisor; }
