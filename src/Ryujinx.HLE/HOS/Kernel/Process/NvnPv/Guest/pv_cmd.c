// Paravirtual NVN: command buffers. Every GPU command becomes a record (pv_types.h) in arena
// segments; objects are copied into records by value, so the host never reads a game object at
// replay. Semantics follow MacNativeGames / D2R Mobile's NVN layer (MIT); the reported command
// memory usage keeps their measured real-driver encodings (hw_fix), since the game sizes heaps
// from it.

#define CB(c) ((cmd_t *)(c))
#define CMD_MAGIC MAGIC('C', 'M', 'D', 'B')

static void cmd_reset_sentinels(cmd_t *o)
{
    o->inline_cmd_cur = ~0ull;
    o->inline_cmd_end = 0;
    o->inline_ctl_cur = ~0ull;
    o->inline_ctl_end = 0;
}

NVN(nvnCommandBufferInitialize, NVNboolean, NVNcommandBuffer *c, NVNdevice *d)
{
    memset(c, 0, sizeof(cmd_t));
    CB(c)->magic = CMD_MAGIC;
    CB(c)->dev = d;
    cmd_reset_sentinels(CB(c));

    return 1;
}

NVN(nvnCommandBufferFinalize, void, NVNcommandBuffer *c)
{
    cmd_t *o = CB(c);

    if (o->recording && o->first)
    {
        // A recording abandoned half way: nothing names it, so it goes now.
        rec(c, OP_END, 0);
        __atomic_fetch_add(&pv_ctl.recordings_live, 1, __ATOMIC_RELAXED);
        recording_free(o->first);
    }

    o->magic = 0;
}

NVN(nvnCommandBufferSetDebugLabel, void, NVNcommandBuffer *o, const char *s) { (void)o; (void)s; }
NVN(nvnCommandBufferSetMemoryCallback, void, NVNcommandBuffer *c, PFNNVNCOMMANDBUFFERMEMORYCALLBACKPROC cb) { CB(c)->mem_cb = cb; }
NVN(nvnCommandBufferSetMemoryCallbackData, void, NVNcommandBuffer *c, void *d) { CB(c)->mem_cb_data = d; }
NVN(nvnCommandBufferGetMemoryCallback, PFNNVNCOMMANDBUFFERMEMORYCALLBACKPROC, const NVNcommandBuffer *c) { return CB(c)->mem_cb; }
NVN_NOPROTO(nvnCommandBufferGetMemoryCallbackData, void *, const NVNcommandBuffer *c) { return CB(c)->mem_cb_data; }
NVN_NOPROTO(nvnCommandBufferSetControlMemoryCallbackEnabled, void, NVNcommandBuffer *c, uint32_t e) { (void)c; (void)e; }
NVN_NOPROTO(nvnCommandBufferSetCommandMemoryCallbackEnabled, void, NVNcommandBuffer *c, uint32_t e) { (void)c; (void)e; }

// Command memory is accounted only: records live in the arena. The game decides from the usage
// whether a recording is empty and sizes heaps from it, so it is what the real driver would use.
NVN(nvnCommandBufferAddCommandMemory, void, NVNcommandBuffer *c, const NVNmemoryPool *p, ptrdiff_t off, size_t n)
{
    CB(c)->game_size = n;
    CB(c)->hw_used = 0;
    cmd_reset_sentinels(CB(c));
}

// Control memory holds the marks that name recordings; getting it back releases them.
NVN(nvnCommandBufferAddControlMemory, void, NVNcommandBuffer *c, void *m, size_t n)
{
    reg_release((uint64_t)(uintptr_t)m, (uint64_t)(uintptr_t)m + n);
    CB(c)->ctl = m;
    CB(c)->ctl_size = n;
    CB(c)->ctl_used = 0;
    cmd_reset_sentinels(CB(c));
}

NVN(nvnCommandBufferGetCommandMemorySize, size_t, const NVNcommandBuffer *c) { return CB(c)->game_size; }
NVN(nvnCommandBufferGetCommandMemoryUsed, size_t, const NVNcommandBuffer *c) { const cmd_t *o = CB(c); return o->hw_used < o->game_size ? o->hw_used : o->game_size; }
NVN(nvnCommandBufferGetCommandMemoryFree, size_t, const NVNcommandBuffer *c) { const cmd_t *o = CB(c); return o->game_size - (o->hw_used < o->game_size ? o->hw_used : o->game_size); }
NVN(nvnCommandBufferGetControlMemorySize, size_t, const NVNcommandBuffer *c) { return CB(c)->ctl_size; }
NVN(nvnCommandBufferGetControlMemoryUsed, size_t, const NVNcommandBuffer *c) { const cmd_t *o = CB(c); return o->ctl_used < o->ctl_size ? o->ctl_used : o->ctl_size; }
NVN(nvnCommandBufferGetControlMemoryFree, size_t, const NVNcommandBuffer *c) { const cmd_t *o = CB(c); return o->ctl_size - (o->ctl_used < o->ctl_size ? o->ctl_used : o->ctl_size); }
NVN(nvnCommandBufferIsRecording, NVNboolean, const NVNcommandBuffer *c) { return CB(c)->recording != 0; }

NVN(nvnCommandBufferBeginRecording, void, NVNcommandBuffer *c)
{
    cmd_t *o = CB(c);

    if (o->recording)
    {
        pv_logf("nvnpv: BeginRecording on %p while recording", c);
    }

    o->recording = 1;
    rec_start(o);
    cmd_reset_sentinels(o);
}

NVN(nvnCommandBufferEndRecording, NVNcommandHandle, NVNcommandBuffer *c)
{
    cmd_t *o = CB(c);

    rec(c, OP_END, 0);
    o->recording = 0;
    cmd_reset_sentinels(o);

    if (!o->first)
    {
        return 0;
    }

    __atomic_fetch_add(&pv_ctl.recordings_live, 1, __ATOMIC_RELAXED);

    // The handle is the mark's address in the control memory the game gave, as with the real
    // driver (whose descriptor lives there). Out of control memory the game is asked for more,
    // as the real driver does; failing that the mark goes into the arena and the recording is
    // never released.
    if ((!o->ctl || o->ctl_used + MARK_BYTES > o->ctl_size) && o->mem_cb)
    {
        o->mem_cb(c, NVN_COMMAND_BUFFER_MEMORY_EVENT_OUT_OF_CONTROL_MEMORY, MARK_BYTES, o->mem_cb_data);
    }

    pv_mark *m;

    if (o->ctl && o->ctl_used + MARK_BYTES <= o->ctl_size)
    {
        m = (pv_mark *)(o->ctl + o->ctl_used);
        o->ctl_used += MARK_BYTES;
        reg_add((uint64_t)(uintptr_t)m);
    }
    else
    {
        static int said;

        if (said++ < 4)
        {
            pv_logf("nvnpv: recording on %p without control memory left (%llu of %llu used): it is kept for good", c, o->ctl_used, o->ctl_size);
        }

        m = pv_arena_alloc(MARK_BYTES, 16);

        if (!m)
        {
            return 0;
        }
    }

    m->magic = MARK_MAGIC;
    m->gen = (uint32_t)((seg_hdr *)o->first)->gen;
    m->first = (uint64_t)(uintptr_t)o->first;

    o->first = NULL;
    o->cur = o->end = NULL;

    return (NVNcommandHandle)(uintptr_t)m;
}

// CallCommands refers to the called recording (NVN keeps it valid until the caller ran);
// CopyCommands copies it at the call position.
NVN(nvnCommandBufferCallCommands, void, NVNcommandBuffer *c, int n, const NVNcommandHandle *h)
{
    for (int i = 0; i < n; i++)
    {
        uint8_t *first = handle_recording(h[i], NULL);

        if (first)
        {
            REC(c, OP_CALL, p_call)->first = (uint64_t)(uintptr_t)first;
            hw_fix(c, 8);
        }
    }
}

NVN(nvnCommandBufferCopyCommands, void, NVNcommandBuffer *c, int n, const NVNcommandHandle *h)
{
    for (int i = 0; i < n; i++)
    {
        uint8_t *seg = handle_recording(h[i], NULL);

        while (seg)
        {
            uint8_t *p = seg + SEG_HDR;
            uint8_t *next = NULL;

            for (;;)
            {
                op_hdr hd;
                __builtin_memcpy(&hd, p, sizeof hd);

                if (hd.op == OP_END || hd.size < 8)
                {
                    break;
                }

                if (hd.op == OP_JUMP)
                {
                    next = (uint8_t *)(uintptr_t)((const p_jump *)(p + 8))->next;
                    break;
                }

                void *dst = rec(c, hd.op, hd.size - 8);
                memcpy(dst, p + 8, hd.size - 8);
                hw_count(c);
                p += hd.size;
            }

            seg = next;
        }
    }
}

// ---------- Render targets, clears, viewports ----------

static void put_target(p_rt_target *t, const NVNtexture *tex, const NVNtextureView *v)
{
    t->present = tex != NULL;

    if (tex)
    {
        t->tex = ((const tex_t *)tex)->t;
    }

    t->has_view = v != NULL;

    if (v)
    {
        t->view = *(const pv_view *)v;
    }
}

NVN(nvnCommandBufferSetRenderTargets, void, NVNcommandBuffer *c, int n, const NVNtexture *const *ct, const NVNtextureView *const *cv,
    const NVNtexture *dt, const NVNtextureView *dv)
{
    if (n < 0 || n > 8)
    {
        pv_logf("nvnpv: SetRenderTargets with %d colour targets", n);
        n = n < 0 ? 0 : 8;
    }

    int nt = n + (dt != NULL);
    p_set_rt *r = (p_set_rt *)rec(c, OP_SET_RT, sizeof(p_set_rt) + sizeof(p_rt_target) * (size_t)nt);
    r->n = n;
    r->has_depth = dt != NULL;

    for (int i = 0; i < n; i++)
    {
        put_target(&r->t[i], ct ? ct[i] : NULL, cv ? cv[i] : NULL);
    }

    if (dt)
    {
        put_target(&r->t[n], dt, dv);
    }

    hw_count(c);
}

NVN(nvnCommandBufferClearColor, void, NVNcommandBuffer *c, int index, const float *rgba, int mask)
{
    p_clear_color *r = REC(c, OP_CLEAR_COLOR, p_clear_color);
    r->index = index;
    r->mask = mask;
    memcpy(r->rgba, rgba, 16);
    hw_count(c);
}

NVN(nvnCommandBufferClearColori, void, NVNcommandBuffer *c, int index, const int *rgba, int mask) { h_nvnCommandBufferClearColor(c, index, (const float *)(const void *)rgba, mask); }
NVN(nvnCommandBufferClearColorui, void, NVNcommandBuffer *c, int index, const uint32_t *rgba, int mask) { h_nvnCommandBufferClearColor(c, index, (const float *)(const void *)rgba, mask); }

NVN(nvnCommandBufferClearDepthStencil, void, NVNcommandBuffer *c, float depth, uint32_t dmask, int stencil, int smask)
{
    *REC(c, OP_CLEAR_DS, p_clear_ds) = (p_clear_ds){ depth, (int)B8(dmask), stencil, smask };
    hw_count(c);
}

NVN(nvnCommandBufferSetViewports, void, NVNcommandBuffer *c, int first, int n, const float *v)
{
    if (first < 0 || n < 0 || first + n > 16)
    {
        pv_logf("nvnpv: SetViewports first %d n %d", first, n);
        return;
    }

    p_viewports *r = RECN(c, OP_VIEWPORTS, p_viewports, v, n);
    r->first = first;
    r->n = n;
    memcpy(r->v, v, 16 * (size_t)n);
    hw_count(c);
}

NVN(nvnCommandBufferSetViewport, void, NVNcommandBuffer *c, int x, int y, int w, int h)
{
    float v[4] = { (float)x, (float)y, (float)w, (float)h };
    h_nvnCommandBufferSetViewports(c, 0, 1, v);
}

NVN(nvnCommandBufferSetScissors, void, NVNcommandBuffer *c, int first, int n, const int *v)
{
    if (first < 0 || n < 0 || first + n > 16)
    {
        pv_logf("nvnpv: SetScissors first %d n %d", first, n);
        return;
    }

    p_scissors *r = RECN(c, OP_SCISSORS, p_scissors, r, n);
    r->first = first;
    r->n = n;
    memcpy(r->r, v, 16 * (size_t)n);
    hw_count(c);
}

NVN(nvnCommandBufferSetScissor, void, NVNcommandBuffer *c, int x, int y, int w, int h)
{
    int v[4] = { x, y, w, h };
    h_nvnCommandBufferSetScissors(c, 0, 1, v);
}

NVN(nvnCommandBufferSetDepthRanges, void, NVNcommandBuffer *c, int first, int n, const float *v)
{
    if (first < 0 || n < 0 || first + n > 16)
    {
        pv_logf("nvnpv: SetDepthRanges first %d n %d", first, n);
        return;
    }

    p_depth_ranges *r = RECN(c, OP_DEPTH_RANGES, p_depth_ranges, r, n);
    r->first = first;
    r->n = n;
    memcpy(r->r, v, 8 * (size_t)n);
    hw_count(c);
}

NVN(nvnCommandBufferSetDepthRange, void, NVNcommandBuffer *c, float n, float f)
{
    float v[2] = { n, f };
    h_nvnCommandBufferSetDepthRanges(c, 0, 1, v);
}

NVN(nvnCommandBufferSetViewportSwizzles, void, NVNcommandBuffer *c, int first, int n, const NVNviewportSwizzle *s)
{
    static int said;

    if (!said++)
    {
        pv_log("nvnpv: SetViewportSwizzles ignored");
    }
}

// ---------- Programs and state ----------

NVN(nvnCommandBufferBindProgram, void, NVNcommandBuffer *c, const NVNprogram *p, int stages)
{
    const prog_t *o = (const prog_t *)p;
    p_bind_program *r = REC(c, OP_BIND_PROGRAM, p_bind_program);
    r->stages = stages;

    if (o)
    {
        r->nstage = (int32_t)o->nstage;
        r->serial = o->serial;
        memcpy(r->sd, o->sd, sizeof r->sd);
    }

    hw_count(c);
}

static void bind_state(NVNcommandBuffer *c, int kind, const void *st, size_t n)
{
    p_bind_state *r = REC(c, OP_BIND_STATE, p_bind_state);
    r->kind = kind;
    memcpy(r->bytes, st, n);
    hw_count(c);
}

NVN(nvnCommandBufferBindBlendState, void, NVNcommandBuffer *c, const NVNblendState *st) { bind_state(c, 0, st, sizeof(blend_st)); }
NVN(nvnCommandBufferBindColorState, void, NVNcommandBuffer *c, const NVNcolorState *st) { bind_state(c, 1, st, sizeof(color_st)); }
NVN(nvnCommandBufferBindChannelMaskState, void, NVNcommandBuffer *c, const NVNchannelMaskState *st) { bind_state(c, 2, st, sizeof(chmask_st)); }
NVN(nvnCommandBufferBindMultisampleState, void, NVNcommandBuffer *c, const NVNmultisampleState *st) { bind_state(c, 3, st, sizeof(ms_st)); }
NVN(nvnCommandBufferBindPolygonState, void, NVNcommandBuffer *c, const NVNpolygonState *st) { bind_state(c, 4, st, sizeof(poly_st)); }
NVN(nvnCommandBufferBindDepthStencilState, void, NVNcommandBuffer *c, const NVNdepthStencilState *st) { bind_state(c, 5, st, sizeof(ds_st)); }

NVN(nvnCommandBufferBindVertexAttribState, void, NVNcommandBuffer *c, int n, const NVNvertexAttribState *st)
{
    if (n < 0 || n > 32)
    {
        pv_logf("nvnpv: BindVertexAttribState n %d", n);
        return;
    }

    p_bind_vattr *r = RECN(c, OP_BIND_VATTR, p_bind_vattr, st, n);
    r->n = n;
    memcpy(r->st, st, 4 * (size_t)n);
    hw_fix(c, 68);   // the driver writes all 16 attribute formats: 17 words
}

NVN(nvnCommandBufferBindVertexStreamState, void, NVNcommandBuffer *c, int n, const NVNvertexStreamState *st)
{
    if (n < 0 || n > 32)
    {
        pv_logf("nvnpv: BindVertexStreamState n %d", n);
        return;
    }

    p_bind_vstream *r = RECN(c, OP_BIND_VSTREAM, p_bind_vstream, st, n);
    r->n = n;
    memcpy(r->st, st, 8 * (size_t)n);
    hw_fix(c, 4 * (16 + (uint32_t)n));
}

NVN(nvnCommandBufferBindVertexBuffers, void, NVNcommandBuffer *c, int first, int n, const NVNbufferRange *rg)
{
    if (first < 0 || n < 0 || first + n > 32)
    {
        pv_logf("nvnpv: BindVertexBuffers first %d n %d", first, n);
        return;
    }

    p_bind_vbuf *r = RECN(c, OP_BIND_VBUF, p_bind_vbuf, r, n);
    r->first = first;
    r->n = n;
    memcpy(r->r, rg, sizeof *rg * (size_t)n);
    hw_fix(c, 24 * (uint32_t)n);
}

NVN(nvnCommandBufferBindVertexBuffer, void, NVNcommandBuffer *c, int i, NVNbufferAddress a, size_t n)
{
    NVNbufferRange r = { a, n };
    h_nvnCommandBufferBindVertexBuffers(c, i, 1, &r);
}

static void bind_buffers(NVNcommandBuffer *c, uint32_t op, NVNshaderStage st, int first, int n, const NVNbufferRange *rg)
{
    if (first < 0 || n < 0 || n > 32)
    {
        pv_logf("nvnpv: buffer bind first %d n %d", first, n);
        return;
    }

    p_bind_buf *r = RECN(c, op, p_bind_buf, r, n);
    r->stage = st;
    r->first = first;
    r->n = n;
    memcpy(r->r, rg, sizeof *rg * (size_t)n);
    hw_fix(c, 20 * (uint32_t)n);   // CB_SIZE / ADDRESS (4 words) + CB_BIND (1) per buffer
}

NVN(nvnCommandBufferBindUniformBuffer, void, NVNcommandBuffer *c, NVNshaderStage st, int i, NVNbufferAddress a, size_t n) { NVNbufferRange r = { a, n }; bind_buffers(c, OP_BIND_UBO, st, i, 1, &r); }
NVN(nvnCommandBufferBindUniformBuffers, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNbufferRange *rg) { bind_buffers(c, OP_BIND_UBO, st, first, n, rg); }
NVN(nvnCommandBufferBindStorageBuffer, void, NVNcommandBuffer *c, NVNshaderStage st, int i, NVNbufferAddress a, size_t n) { NVNbufferRange r = { a, n }; bind_buffers(c, OP_BIND_SSBO, st, i, 1, &r); }
NVN(nvnCommandBufferBindStorageBuffers, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNbufferRange *rg) { bind_buffers(c, OP_BIND_SSBO, st, first, n, rg); }

// The inline fast paths' fallbacks (NVN 55). Buffer ranges bind at record time; handle binds read
// the game's array when the host replays the submission, as the real driver does: the game fills
// material handle tables after recording (MacNativeGames found the flicker that reading them early causes).
NVN_NOPROTO(nvnCommandBufferBindUniformBuffersDeferred, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNbufferRange *rg) { bind_buffers(c, OP_BIND_UBO, st, first, n, rg); }
NVN_NOPROTO(nvnCommandBufferBindStorageBuffersDeferred, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNbufferRange *rg) { bind_buffers(c, OP_BIND_SSBO, st, first, n, rg); }

static void bind_handles_kind(NVNcommandBuffer *c, uint32_t op, int kind, int st, int first, int n, const uint64_t *h, int deferred, uint32_t hw)
{
    if (n < 0 || n > 32)
    {
        pv_logf("nvnpv: handle bind count %d", n);
        return;
    }

    p_bind_handles *r = RECN(c, op, p_bind_handles, h, n);
    r->stage = st;
    r->first = first;
    r->n = n;
    r->kind = kind;
    r->src = deferred ? (uint64_t)(uintptr_t)h : 0;
    memcpy(r->h, h, 8 * (size_t)n);
    hw_fix(c, hw);
}

#define bind_handles(c, op, st, first, n, h, deferred, hw) bind_handles_kind((c), (op), 0, (st), (first), (n), (h), (deferred), (hw))
#define bind_separate(c, st, first, n, h, deferred, hw) bind_handles_kind((c), OP_BIND_TEX, 1, (st), (first), (n), (h), (deferred), (hw))

NVN(nvnCommandBufferBindTexture, void, NVNcommandBuffer *c, NVNshaderStage st, int i, NVNtextureHandle h) { uint64_t v = h; bind_handles(c, OP_BIND_TEX, st, i, 1, &v, 0, 20); }
NVN(nvnCommandBufferBindTextures, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNtextureHandle *h) { bind_handles(c, OP_BIND_TEX, st, first, n, (const uint64_t *)h, 0, 16 + 4 * (uint32_t)n); }
NVN(nvnCommandBufferBindImage, void, NVNcommandBuffer *c, NVNshaderStage st, int i, NVNimageHandle h) { uint64_t v = h; bind_handles(c, OP_BIND_IMG, st, i, 1, &v, 0, 24); }
NVN(nvnCommandBufferBindImages, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNimageHandle *h) { bind_handles(c, OP_BIND_IMG, st, first, n, (const uint64_t *)h, 0, 16 + 8 * (uint32_t)n); }
NVN(nvnCommandBufferBindSeparateTexture, void, NVNcommandBuffer *c, NVNshaderStage st, int i, NVNseparateTextureHandle h) { bind_separate(c, st, i, 1, &h.value, 0, 24); }
NVN(nvnCommandBufferBindSeparateTextures, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNseparateTextureHandle *h) { bind_separate(c, st, first, n, (const uint64_t *)h, 0, 16 + 8 * (uint32_t)n); }
NVN(nvnCommandBufferBindSeparateSampler, void, NVNcommandBuffer *c, NVNshaderStage st, int i, NVNseparateSamplerHandle h) { bind_handles(c, OP_BIND_SAMP, st, i, 1, &h.value, 0, 24); }
NVN(nvnCommandBufferBindSeparateSamplers, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNseparateSamplerHandle *h) { bind_handles(c, OP_BIND_SAMP, st, first, n, (const uint64_t *)h, 0, 16 + 8 * (uint32_t)n); }
NVN_NOPROTO(nvnCommandBufferBindTexturesDeferred, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNtextureHandle *h) { bind_handles(c, OP_BIND_TEX, st, first, n, (const uint64_t *)h, 1, 16 + 4 * (uint32_t)n); }
NVN_NOPROTO(nvnCommandBufferBindImagesDeferred, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNimageHandle *h) { bind_handles(c, OP_BIND_IMG, st, first, n, (const uint64_t *)h, 1, 16 + 8 * (uint32_t)n); }
NVN_NOPROTO(nvnCommandBufferBindSeparateTexturesDeferred, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNseparateTextureHandle *h) { bind_separate(c, st, first, n, (const uint64_t *)h, 1, 16 + 8 * (uint32_t)n); }
NVN_NOPROTO(nvnCommandBufferBindSeparateSamplersDeferred, void, NVNcommandBuffer *c, NVNshaderStage st, int first, int n, const NVNseparateSamplerHandle *h) { bind_handles(c, OP_BIND_SAMP, st, first, n, (const uint64_t *)h, 1, 16 + 8 * (uint32_t)n); }

NVN(nvnCommandBufferSetTexturePool, void, NVNcommandBuffer *c, const NVNtexturePool *p)
{
    const dpool_t *o = (const dpool_t *)p;
    p_set_pool *r = REC(c, OP_SET_TEX_POOL, p_set_pool);
    r->table = (uint64_t)(uintptr_t)o->table;
    r->n = o->n;
    hw_count(c);
}

NVN(nvnCommandBufferSetSamplerPool, void, NVNcommandBuffer *c, const NVNsamplerPool *p)
{
    const dpool_t *o = (const dpool_t *)p;
    p_set_pool *r = REC(c, OP_SET_SAMP_POOL, p_set_pool);
    r->table = (uint64_t)(uintptr_t)o->table;
    r->n = o->n;
    hw_count(c);
}

NVN(nvnCommandBufferSetShaderScratchMemory, void, NVNcommandBuffer *c, const NVNmemoryPool *p, ptrdiff_t off, size_t n)
{
    const pool_t *o = (const pool_t *)p;
    p_scratch *r = REC(c, OP_SCRATCH, p_scratch);
    r->addr = o ? (uint64_t)(uintptr_t)o->mem + (uint64_t)off : 0;
    r->size = n;
    hw_count(c);
}

NVN(nvnCommandBufferSetStencilValueMask, void, NVNcommandBuffer *c, NVNface f, int m) { *REC(c, OP_STENCIL, p_stencil) = (p_stencil){ f, m, 0, 0, 0 }; hw_count(c); }
NVN(nvnCommandBufferSetStencilMask, void, NVNcommandBuffer *c, NVNface f, int m) { *REC(c, OP_STENCIL, p_stencil) = (p_stencil){ f, 0, m, 0, 1 }; hw_count(c); }
NVN(nvnCommandBufferSetStencilRef, void, NVNcommandBuffer *c, NVNface f, int v) { *REC(c, OP_STENCIL, p_stencil) = (p_stencil){ f, 0, 0, v, 2 }; hw_count(c); }
NVN(nvnCommandBufferSetPolygonOffsetClamp, void, NVNcommandBuffer *c, float fa, float un, float cl) { *REC(c, OP_POLY_OFFSET, p_poly_offset) = (p_poly_offset){ fa, un, cl }; hw_count(c); }
NVN(nvnCommandBufferSetBlendColor, void, NVNcommandBuffer *c, const float *v) { memcpy(REC(c, OP_BLEND_COLOR, p_blend_color)->rgba, v, 16); hw_count(c); }
NVN(nvnCommandBufferSetDepthClamp, void, NVNcommandBuffer *c, uint32_t e) { REC(c, OP_DEPTH_CLAMP, p_flag)->value = B8(e) != 0; hw_count(c); }
NVN(nvnCommandBufferSetRenderEnable, void, NVNcommandBuffer *c, uint32_t e) { REC(c, OP_RENDER_ENABLE, p_flag)->value = B8(e) != 0; hw_count(c); }
NVN(nvnCommandBufferSetRasterizerDiscard, void, NVNcommandBuffer *c, uint32_t e) { REC(c, OP_RASTER_DISCARD, p_flag)->value = B8(e) != 0; hw_count(c); }

// Conditional rendering: enabled when the counters at addr and addr + 16 compare equal (mode 0)
// or not equal (mode 1). TOTK gates most of its clears with it; the host evaluates it in GPU order.
NVN(nvnCommandBufferSetRenderEnableConditional, void, NVNcommandBuffer *c, NVNconditionalRenderMode m, NVNbufferAddress a)
{
    p_render_cond *r = REC(c, OP_RENDER_COND, p_render_cond);
    r->mode = (int32_t)m;
    r->addr = a;
    hw_count(c);
}

NVN(nvnCommandBufferSetPrimitiveRestart, void, NVNcommandBuffer *c, uint32_t e, int index) { *REC(c, OP_PRIM_RESTART, p_prim_restart) = (p_prim_restart){ (int)B8(e), index }; hw_count(c); }
NVN(nvnCommandBufferSetLineWidth, void, NVNcommandBuffer *c, float w) { REC(c, OP_LINE_WIDTH, p_float)->value = w; hw_count(c); }
NVN(nvnCommandBufferSetPointSize, void, NVNcommandBuffer *c, float s) { REC(c, OP_POINT_SIZE, p_float)->value = s; hw_count(c); }
NVN(nvnCommandBufferSetAlphaRef, void, NVNcommandBuffer *c, float r) { REC(c, OP_ALPHA_REF, p_float)->value = r; hw_count(c); }
NVN(nvnCommandBufferSetSampleMask, void, NVNcommandBuffer *c, int m) { REC(c, OP_SAMPLE_MASK, p_flag)->value = m; hw_count(c); }
NVN(nvnCommandBufferSetDepthBounds, void, NVNcommandBuffer *c, uint32_t e, float lo, float hi) { *REC(c, OP_DEPTH_BOUNDS, p_depth_bounds) = (p_depth_bounds){ (int)B8(e), lo, hi }; hw_count(c); }
NVN(nvnCommandBufferSetTiledCacheAction, void, NVNcommandBuffer *c, NVNtiledCacheAction a) { REC(c, OP_TILED_CACHE, p_tiled_cache)->action = (int32_t)a; hw_count(c); }
NVN(nvnCommandBufferSetTiledCacheTileSize, void, NVNcommandBuffer *c, int w, int h) { (void)c; (void)w; (void)h; }
NVN(nvnCommandBufferDiscardColor, void, NVNcommandBuffer *c, int i) { *REC(c, OP_DISCARD, p_discard) = (p_discard){ 0, i }; hw_count(c); }
NVN(nvnCommandBufferDiscardDepthStencil, void, NVNcommandBuffer *c) { *REC(c, OP_DISCARD, p_discard) = (p_discard){ 1, 0 }; hw_count(c); }
NVN(nvnCommandBufferSetPatchSize, void, NVNcommandBuffer *c, int n) { REC(c, OP_PATCH, p_patch)->size = n; hw_count(c); }
NVN(nvnCommandBufferSetSubpixelPrecisionBias, void, NVNcommandBuffer *c, int x, int y) { (void)c; (void)x; (void)y; }
NVN(nvnCommandBufferSetConservativeRasterDilate, void, NVNcommandBuffer *c, float d) { (void)c; (void)d; }
NVN(nvnCommandBufferSaveZCullData, void, NVNcommandBuffer *c, NVNbufferAddress a, size_t n) { (void)c; (void)a; (void)n; }
NVN(nvnCommandBufferRestoreZCullData, void, NVNcommandBuffer *c, NVNbufferAddress a, size_t n) { (void)c; (void)a; (void)n; }
NVN_NOPROTO(nvnCommandBufferOverrideSubtileSize, void, NVNcommandBuffer *c) { (void)c; }
NVN_NOPROTO(nvnCommandBufferSetZCullZF32CompressionEnable, void, NVNcommandBuffer *c, uint32_t e) { (void)c; (void)e; }

NVN(nvnCommandBufferSetConservativeRasterEnable, void, NVNcommandBuffer *c, uint32_t e)
{
    static int said;

    if (B8(e) && !said++)
    {
        pv_log("nvnpv: conservative rasterisation requested (drawn normally)");
    }
}

NVN_NOPROTO(nvnCommandBufferSetProvokingVertexMode, void, NVNcommandBuffer *c, int mode)
{
    static int said;

    if (mode && !said++)
    {
        pv_logf("nvnpv: provoking vertex mode %d not implemented", mode);
    }
}

// ---------- Draws and dispatches ----------

NVN(nvnCommandBufferDrawArrays, void, NVNcommandBuffer *c, NVNdrawPrimitive pr, int first, int n) { *REC(c, OP_DRAW, p_draw) = (p_draw){ pr, first, n, 0, 1 }; hw_count(c); }

NVN(nvnCommandBufferDrawArraysInstanced, void, NVNcommandBuffer *c, NVNdrawPrimitive pr, int first, int n, int bi, int inst)
{
    if (inst <= 0)
    {
        return;   // zero instances draw nothing
    }

    *REC(c, OP_DRAW, p_draw) = (p_draw){ pr, first, n, bi, inst };
    hw_count(c);
}

NVN(nvnCommandBufferDrawElements, void, NVNcommandBuffer *c, NVNdrawPrimitive pr, NVNindexType it, int n, NVNbufferAddress ib)
{
    *REC(c, OP_DRAW_IDX, p_draw_idx) = (p_draw_idx){ pr, it, n, 0, 0, 1, ib };
    hw_fix(c, 48);
}

NVN(nvnCommandBufferDrawElementsBaseVertex, void, NVNcommandBuffer *c, NVNdrawPrimitive pr, NVNindexType it, int n, NVNbufferAddress ib, int bv)
{
    *REC(c, OP_DRAW_IDX, p_draw_idx) = (p_draw_idx){ pr, it, n, bv, 0, 1, ib };
    hw_fix(c, 44);
}

NVN(nvnCommandBufferDrawElementsInstanced, void, NVNcommandBuffer *c, NVNdrawPrimitive pr, NVNindexType it, int n, NVNbufferAddress ib, int bv, int bi, int inst)
{
    if (inst <= 0)
    {
        return;
    }

    *REC(c, OP_DRAW_IDX, p_draw_idx) = (p_draw_idx){ pr, it, n, bv, bi, inst, ib };
    hw_fix(c, 44);
}

NVN(nvnCommandBufferDrawArraysIndirect, void, NVNcommandBuffer *c, NVNdrawPrimitive pr, NVNbufferAddress ind)
{
    *REC(c, OP_DRAW_INDIRECT, p_draw_indirect) = (p_draw_indirect){ pr, 0, 0, ind };
    hw_count(c);
}

NVN(nvnCommandBufferDrawElementsIndirect, void, NVNcommandBuffer *c, NVNdrawPrimitive pr, NVNindexType it, NVNbufferAddress ib, NVNbufferAddress ind)
{
    *REC(c, OP_DRAW_IDX_INDIRECT, p_draw_indirect) = (p_draw_indirect){ pr, it, ib, ind };
    hw_count(c);
}

NVN(nvnCommandBufferDrawTexture, void, NVNcommandBuffer *c, NVNtextureHandle h, const NVNdrawTextureRegion *dst, const NVNdrawTextureRegion *src)
{
    static int said;

    if (!said++)
    {
        pv_log("nvnpv: DrawTexture not implemented");
    }
}

NVN(nvnCommandBufferDispatchCompute, void, NVNcommandBuffer *c, int x, int y, int z) { *REC(c, OP_DISPATCH, p_dispatch) = (p_dispatch){ x, y, z, 0 }; hw_count(c); }
NVN(nvnCommandBufferDispatchComputeIndirect, void, NVNcommandBuffer *c, NVNbufferAddress a) { REC(c, OP_DISPATCH_INDIRECT, p_dispatch_indirect)->addr = a; hw_count(c); }
NVN(nvnCommandBufferBarrier, void, NVNcommandBuffer *c, int bits) { REC(c, OP_BARRIER, p_barrier)->bits = bits; hw_count(c); }

// ---------- Copies, clears of resources, uniform updates ----------

static void put_view(pv_view *dst, int32_t *has, const NVNtextureView *v)
{
    *has = v != NULL;

    if (v)
    {
        *dst = *(const pv_view *)v;
    }
}

NVN(nvnCommandBufferCopyBufferToTexture, void, NVNcommandBuffer *c, NVNbufferAddress src, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, int flags)
{
    p_copy_bt *o = REC(c, OP_COPY_B2T, p_copy_bt);
    o->buf = src;
    o->tex = ((const tex_t *)t)->t;
    put_view(&o->view, &o->has_view, v);
    o->r = *r;
    o->flags = flags;
    hw_count(c);
}

NVN(nvnCommandBufferCopyTextureToBuffer, void, NVNcommandBuffer *c, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, NVNbufferAddress dst, int flags)
{
    p_copy_bt *o = REC(c, OP_COPY_T2B, p_copy_bt);
    o->buf = dst;
    o->tex = ((const tex_t *)t)->t;
    put_view(&o->view, &o->has_view, v);
    o->r = *r;
    o->flags = flags;
    hw_count(c);
}

NVN(nvnCommandBufferCopyTextureToTexture, void, NVNcommandBuffer *c, const NVNtexture *s, const NVNtextureView *sv, const NVNcopyRegion *sr,
    const NVNtexture *d, const NVNtextureView *dv, const NVNcopyRegion *dr, int flags)
{
    p_copy_t2t *o = REC(c, OP_COPY_T2T, p_copy_t2t);
    o->src = ((const tex_t *)s)->t;
    o->dst = ((const tex_t *)d)->t;
    put_view(&o->sv, &o->has_sv, sv);
    put_view(&o->dv, &o->has_dv, dv);
    o->sr = *sr;
    o->dr = *dr;
    o->flags = flags;
    hw_count(c);
}

NVN(nvnCommandBufferCopyBufferToBuffer, void, NVNcommandBuffer *c, NVNbufferAddress src, NVNbufferAddress dst, size_t n, int flags)
{
    *REC(c, OP_COPY_B2B, p_copy_b2b) = (p_copy_b2b){ src, dst, n, flags, 0 };
    hw_count(c);
}

static int64_t g_copy_row_stride_unused;

NVN(nvnCommandBufferSetCopyRowStride, void, NVNcommandBuffer *c, ptrdiff_t st) { REC(c, OP_COPY_STRIDE, p_copy_stride)->stride = st; hw_count(c); }
NVN(nvnCommandBufferSetCopyImageStride, void, NVNcommandBuffer *c, ptrdiff_t st) { (void)c; g_copy_row_stride_unused = st; }
NVN(nvnCommandBufferGetCopyRowStride, ptrdiff_t, const NVNcommandBuffer *c) { return 0; }
NVN(nvnCommandBufferGetCopyImageStride, ptrdiff_t, const NVNcommandBuffer *c) { return 0; }

static void clear_texture(NVNcommandBuffer *c, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, const void *rgba, int mask, int is_uint)
{
    p_clear_tex *o = REC(c, OP_CLEAR_TEX, p_clear_tex);
    o->tex = ((const tex_t *)t)->t;
    put_view(&o->view, &o->has_view, v);
    o->r = *r;
    o->mask = mask;
    o->is_uint = is_uint;
    memcpy(o->c.u, rgba, 16);
    hw_count(c);
}

NVN(nvnCommandBufferClearTexture, void, NVNcommandBuffer *c, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, const float *rgba, int mask) { clear_texture(c, t, v, r, rgba, mask, 0); }
NVN(nvnCommandBufferClearTexturei, void, NVNcommandBuffer *c, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, const int *rgba, int mask) { clear_texture(c, t, v, r, rgba, mask, 2); }
NVN(nvnCommandBufferClearTextureui, void, NVNcommandBuffer *c, const NVNtexture *t, const NVNtextureView *v, const NVNcopyRegion *r, const uint32_t *rgba, int mask) { clear_texture(c, t, v, r, rgba, mask, 1); }

NVN(nvnCommandBufferClearBuffer, void, NVNcommandBuffer *c, NVNbufferAddress a, size_t n, uint32_t v)
{
    *REC(c, OP_CLEAR_BUF, p_clear_buf) = (p_clear_buf){ a, n, v, 0 };
    hw_count(c);
}

// Inline uniform update: the data is copied at record time (the caller may change its copy
// afterwards) and written into the buffer in GPU order.
NVN(nvnCommandBufferUpdateUniformBuffer, void, NVNcommandBuffer *c, NVNbufferAddress buf, size_t bufsz, ptrdiff_t off, size_t n, const void *data)
{
    if (n > 0x10000)
    {
        pv_logf("nvnpv: UpdateUniformBuffer of %llu bytes", (uint64_t)n);
        return;
    }

    p_update_ub *r = (p_update_ub *)rec(c, OP_UPDATE_UB, sizeof(p_update_ub) + n);
    r->dst = buf + (uint64_t)off;
    r->size = n;
    memcpy(r->data, data, n);
    hw_count(c);
}

// ---------- Counters and events ----------

NVN(nvnCommandBufferReportCounter, void, NVNcommandBuffer *c, NVNcounterType t, NVNbufferAddress a)
{
    p_report_counter *r = REC(c, OP_REPORT_COUNTER, p_report_counter);
    r->type = (int32_t)t;
    r->addr = a;
    hw_count(c);
}

NVN(nvnCommandBufferResetCounter, void, NVNcommandBuffer *c, NVNcounterType t) { REC(c, OP_RESET_COUNTER, p_reset_counter)->type = (int32_t)t; hw_count(c); }

NVN(nvnCommandBufferReportValue, void, NVNcommandBuffer *c, uint32_t v, NVNbufferAddress a)
{
    p_signal_event *r = REC(c, OP_SIGNAL_EVENT, p_signal_event);
    r->addr = a;
    r->mode = NVN_EVENT_SIGNAL_MODE_WRITE;
    r->value = v;
    hw_count(c);
}

NVN(nvnCommandBufferSignalEvent, void, NVNcommandBuffer *c, const NVNevent *e, NVNeventSignalMode m, NVNeventSignalLocation loc, int flags, uint32_t v)
{
    p_signal_event *r = REC(c, OP_SIGNAL_EVENT, p_signal_event);
    r->addr = (uint64_t)(uintptr_t)((const ev_t *)e)->val;
    r->mode = (int32_t)m;
    r->location = (int32_t)loc;
    r->flags = flags;
    r->value = v;
    hw_count(c);
}

NVN(nvnCommandBufferWaitEvent, void, NVNcommandBuffer *c, const NVNevent *e, NVNeventWaitMode m, uint32_t v)
{
    p_wait_event *r = REC(c, OP_WAIT_EVENT, p_wait_event);
    r->addr = (uint64_t)(uintptr_t)((const ev_t *)e)->val;
    r->mode = (int32_t)m;
    r->value = v;
    hw_count(c);
}

// ---------- Debug annotations ----------

NVN(nvnCommandBufferPushDebugGroupStatic, void, NVNcommandBuffer *c, uint32_t id, const char *s) { (void)c; (void)id; (void)s; }
NVN(nvnCommandBufferPushDebugGroupDynamic, void, NVNcommandBuffer *c, uint32_t id, const char *s) { (void)c; (void)id; (void)s; }
NVN(nvnCommandBufferPushDebugGroup, void, NVNcommandBuffer *c, uint32_t id, const char *s) { (void)c; (void)id; (void)s; }
NVN(nvnCommandBufferPopDebugGroup, void, NVNcommandBuffer *c) { (void)c; }
NVN(nvnCommandBufferPopDebugGroupId, void, NVNcommandBuffer *c, uint32_t id) { (void)c; (void)id; }
NVN(nvnCommandBufferInsertDebugMarkerStatic, void, NVNcommandBuffer *c, uint32_t id, const char *s) { (void)c; (void)id; (void)s; }
NVN(nvnCommandBufferInsertDebugMarkerDynamic, void, NVNcommandBuffer *c, uint32_t id, const char *s) { (void)c; (void)id; (void)s; }
NVN(nvnCommandBufferInsertDebugMarker, void, NVNcommandBuffer *c, const char *s) { (void)c; (void)s; }
NVN_NOPROTO(nvnCommandBufferInsertCheckpointStatic, void, NVNcommandBuffer *c, uint32_t id, const char *s) { (void)c; (void)id; (void)s; }
NVN_NOPROTO(nvnCommandBufferInsertCheckpointDynamic, void, NVNcommandBuffer *c, uint32_t id, const char *s) { (void)c; (void)id; (void)s; }
