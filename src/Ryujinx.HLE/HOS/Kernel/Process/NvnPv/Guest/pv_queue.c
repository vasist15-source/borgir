// Paravirtual NVN: queues, fences and windows. Submissions are numbered (fence values): a sync
// fenced on a queue is signalled once the host reports every submission up to its value complete.

#define QUEUE_MAGIC MAGIC('Q', 'U', 'E', 'U')
#define WIN_MAGIC MAGIC('W', 'I', 'N', 'D')
#define QB(b) ((queue_builder_t *)(b))

NVN(nvnQueueBuilderSetDefaults, void, NVNqueueBuilder *b)
{
    NVNdevice *dev = QB(b)->dev;
    memset(b, 0, sizeof(queue_builder_t));
    QB(b)->dev = dev;
    QB(b)->cmd_sz = 65536;   // device defaults
    QB(b)->comp_sz = 262144;
    QB(b)->ctl_sz = 16384;
    QB(b)->flush = 4096;
}

NVN(nvnQueueBuilderSetDevice, void, NVNqueueBuilder *b, NVNdevice *d) { QB(b)->dev = d; }
NVN(nvnQueueBuilderSetFlags, void, NVNqueueBuilder *b, int f) { QB(b)->flags = (uint32_t)f; }
NVN(nvnQueueBuilderSetCommandMemorySize, void, NVNqueueBuilder *b, size_t n) { QB(b)->cmd_sz = n; }
NVN(nvnQueueBuilderSetComputeMemorySize, void, NVNqueueBuilder *b, size_t n) { QB(b)->comp_sz = n; }
NVN(nvnQueueBuilderSetControlMemorySize, void, NVNqueueBuilder *b, size_t n) { QB(b)->ctl_sz = n; }
NVN(nvnQueueBuilderSetCommandFlushThreshold, void, NVNqueueBuilder *b, size_t n) { QB(b)->flush = n; }
NVN(nvnQueueBuilderSetQueueMemory, void, NVNqueueBuilder *b, void *m, size_t n) { QB(b)->mem = m; QB(b)->mem_sz = n; }

// Queue memory: the three blocks rounded to 4 KiB (the layer does not use it; the game only allocates it).
NVN(nvnQueueBuilderGetQueueMemorySize, size_t, const NVNqueueBuilder *b)
{
    const queue_builder_t *o = (const queue_builder_t *)b;

    return (o->cmd_sz + o->comp_sz + o->ctl_sz + 4095) & ~(size_t)4095;
}

static uint32_t g_queue_ids;

NVN(nvnQueueInitialize, NVNboolean, NVNqueue *q, const NVNqueueBuilder *b)
{
    queue_t *o = (queue_t *)q;
    *o = (queue_t){ QUEUE_MAGIC, __atomic_add_fetch(&g_queue_ids, 1, __ATOMIC_RELAXED), ((const queue_builder_t *)b)->dev, 0 };
    pv_logf("nvnpv: queue %u initialised (flags %x)", o->id, ((const queue_builder_t *)b)->flags);

    return 1;
}

NVN(nvnQueueFinalize, void, NVNqueue *q) { ((queue_t *)q)->magic = 0; }
NVN(nvnQueueSetDebugLabel, void, NVNqueue *o, const char *s) { (void)o; (void)s; }

NVN(nvnQueueGetError, NVNqueueGetErrorResult, NVNqueue *q, NVNqueueErrorInfo *info)
{
    return NVN_QUEUE_GET_ERROR_RESULT_GPU_NO_ERROR;
}

NVN(nvnQueueGetTotalCommandMemoryUsed, size_t, NVNqueue *q) { return 0; }
NVN(nvnQueueGetTotalControlMemoryUsed, size_t, NVNqueue *q) { return 0; }
NVN(nvnQueueGetTotalComputeMemoryUsed, size_t, NVNqueue *q) { return 0; }
NVN(nvnQueueResetMemoryUsageCounts, void, NVNqueue *q) { (void)q; }

// Submissions are numbered in submit order across queues; the host replays them in that order.
static volatile uint64_t g_submit_lock;

NVN(nvnQueueSubmitCommands, void, NVNqueue *q, int n, const NVNcommandHandle *h)
{
    queue_t *o = (queue_t *)q;

    if (n <= 0)
    {
        return;
    }

    pv_lock(&g_submit_lock);
    uint64_t fence = ++pv_ctl.submitted_fence;
    o->submits++;
    pv_host(PV_HOST_SUBMIT, o->id, (uint64_t)n, (uint64_t)(uintptr_t)h, fence);
    pv_unlock(&g_submit_lock);
    pv_atomic_add(&pv_ctl.submits, 1);
}

NVN(nvnQueueFlush, void, NVNqueue *q) { (void)q; }

NVN(nvnQueueFinish, void, NVNqueue *q)
{
    uint64_t fence = __atomic_load_n(&pv_ctl.submitted_fence, __ATOMIC_ACQUIRE);

    if (__atomic_load_n(&pv_ctl.completed_fence, __ATOMIC_ACQUIRE) < fence)
    {
        pv_host(PV_HOST_FINISH, fence, 0, 0, 0);
    }
}

// A fence covers everything submitted before it.
NVN(nvnQueueFenceSync, void, NVNqueue *q, NVNsync *s, NVNsyncCondition c, int f)
{
    sync_t *o = (sync_t *)s;
    o->fence = __atomic_load_n(&pv_ctl.submitted_fence, __ATOMIC_ACQUIRE);
    o->fenced = 1;
}

NVN(nvnQueueWaitSync, NVNboolean, NVNqueue *q, const NVNsync *s)
{
    // GPU-side wait: the host replays submissions in order on one queue, so later work already
    // runs after everything the sync covers.
    return 1;
}

// ---------- Windows ----------

#define WB(b) ((win_builder_t *)(b))
#define CWB(b) ((const win_builder_t *)(b))
#define WN(w) ((win_t *)(w))
#define CWN(w) ((const win_t *)(w))

NVN(nvnWindowBuilderSetDefaults, void, NVNwindowBuilder *b) { NVNdevice *dev = WB(b)->dev; memset(b, 0, sizeof(win_builder_t)); WB(b)->dev = dev; WB(b)->interval = 1; }
NVN(nvnWindowBuilderSetDevice, void, NVNwindowBuilder *b, NVNdevice *d) { WB(b)->dev = d; }
NVN(nvnWindowBuilderSetNativeWindow, void, NVNwindowBuilder *b, NVNnativeWindow w) { WB(b)->native = (void *)w; }
NVN(nvnWindowBuilderSetPresentInterval, void, NVNwindowBuilder *b, int v) { WB(b)->interval = v; }

NVN(nvnWindowBuilderSetTextures, void, NVNwindowBuilder *b, int n, NVNtexture *const *t)
{
    if (n < 0 || n > 4)
    {
        pv_logf("nvnpv: WindowBuilderSetTextures with %d textures", n);
        n = n < 0 ? 0 : 4;
    }

    WB(b)->ntex = n;

    for (int i = 0; i < n; i++)
    {
        WB(b)->tex[i] = t[i];
    }
}

NVN_NOPROTO(nvnWindowBuilderSetNumActiveTextures, void, NVNwindowBuilder *b, int n) { WB(b)->active = n; }
NVN_NOPROTO(nvnWindowBuilderGetNumActiveTextures, int, const NVNwindowBuilder *b) { return CWB(b)->active ? CWB(b)->active : CWB(b)->ntex; }
NVN_NOPROTO(nvnWindowBuilderGetDevice, NVNdevice *, const NVNwindowBuilder *b) { return CWB(b)->dev; }
NVN(nvnWindowBuilderGetNativeWindow, NVNnativeWindow, const NVNwindowBuilder *b) { return (NVNnativeWindow)CWB(b)->native; }
NVN(nvnWindowBuilderGetPresentInterval, int, const NVNwindowBuilder *b) { return CWB(b)->interval; }
NVN_NOPROTO(nvnWindowBuilderGetNumTextures, int, const NVNwindowBuilder *b) { return CWB(b)->ntex; }
NVN_NOPROTO(nvnWindowBuilderGetTexture, NVNtexture *, const NVNwindowBuilder *b, int i) { return i >= 0 && i < CWB(b)->ntex ? CWB(b)->tex[i] : NULL; }

static uint32_t g_window_ids;

NVN(nvnWindowInitialize, NVNboolean, NVNwindow *w, const NVNwindowBuilder *b)
{
    const win_builder_t *s = CWB(b);
    win_t *o = WN(w);

    memset(o, 0, sizeof *o);
    o->magic = WIN_MAGIC;
    o->id = __atomic_add_fetch(&g_window_ids, 1, __ATOMIC_RELAXED);
    o->native = s->native;
    o->ntex = s->ntex;
    o->interval = s->interval;
    o->active = s->active ? s->active : s->ntex;

    if (o->ntex < 1 || o->ntex > 4 || o->active < 1 || o->active > o->ntex)
    {
        pv_logf("nvnpv: WindowInitialize with %d textures (%d active)", o->ntex, o->active);
        return 0;
    }

    memcpy(o->tex, s->tex, sizeof o->tex);

    pv_tex t[4];

    for (int i = 0; i < o->ntex; i++)
    {
        t[i] = ((const tex_t *)o->tex[i])->t;
    }

    pv_host(PV_HOST_WINDOW, (uint64_t)(uintptr_t)o, (uint64_t)o->ntex, (uint64_t)(uintptr_t)t, 0);

    return 1;
}

NVN(nvnWindowFinalize, void, NVNwindow *w) { WN(w)->magic = 0; }
NVN(nvnWindowSetDebugLabel, void, NVNwindow *o, const char *s) { (void)o; (void)s; }
NVN(nvnWindowGetNativeWindow, NVNnativeWindow, const NVNwindow *w) { return (NVNnativeWindow)CWN(w)->native; }
NVN(nvnWindowGetPresentInterval, int, const NVNwindow *w) { return CWN(w)->interval; }
NVN(nvnWindowSetPresentInterval, void, NVNwindow *w, int v) { WN(w)->interval = v; }
NVN_NOPROTO(nvnWindowGetNumTextures, int, const NVNwindow *w) { return CWN(w)->ntex; }
NVN_NOPROTO(nvnWindowSetNumActiveTextures, void, NVNwindow *w, int n) { if (n >= 1 && n <= WN(w)->ntex) WN(w)->active = n; }
NVN_NOPROTO(nvnWindowGetNumActiveTextures, int, const NVNwindow *w) { return CWN(w)->active ? CWN(w)->active : CWN(w)->ntex; }

NVN(nvnWindowSetCrop, void, NVNwindow *w, int x, int y, int ww, int hh)
{
    win_t *o = WN(w);
    o->crop[0] = x;
    o->crop[1] = y;
    o->crop[2] = ww;
    o->crop[3] = hh;
}

NVN(nvnWindowGetCrop, void, const NVNwindow *w, NVNrectangle *r)
{
    const win_t *o = CWN(w);

    if (!o->crop[2] && !o->crop[3] && o->ntex)
    {
        const tex_t *t = (const tex_t *)o->tex[0];
        *r = (NVNrectangle){ 0, 0, (int)t->t.d.width, (int)t->t.d.height };
        return;
    }

    r->x = o->crop[0];
    r->y = o->crop[1];
    r->width = o->crop[2];
    r->height = o->crop[3];
}

// The texture to draw into next. The host waits until the display no longer reads it; every
// later GPU use is ordered after that on its one queue, so the sync is signalled at once
// (fencing it to the newest submission would make the game wait for the whole previous frame).
NVN(nvnWindowAcquireTexture, NVNwindowAcquireTextureResult, NVNwindow *w, NVNsync *s, int *idx)
{
    win_t *o = WN(w);

    *idx = (int)pv_host(PV_HOST_ACQUIRE, (uint64_t)(uintptr_t)o, (uint64_t)o->active, 0, 0);

    if (s)
    {
        ((sync_t *)s)->fence = 0;
        ((sync_t *)s)->fenced = 1;
    }

    return NVN_WINDOW_ACQUIRE_TEXTURE_RESULT_SUCCESS;
}

NVN(nvnQueueAcquireTexture, NVNqueueAcquireTextureResult, NVNqueue *q, NVNwindow *w, int *idx)
{
    h_nvnWindowAcquireTexture(w, NULL, idx);

    return NVN_QUEUE_ACQUIRE_TEXTURE_RESULT_SUCCESS;
}

NVN(nvnQueuePresentTexture, void, NVNqueue *q, NVNwindow *w, int index)
{
    win_t *o = WN(w);

    if (index < 0 || index >= o->ntex)
    {
        pv_logf("nvnpv: PresentTexture index %d of %d", index, o->ntex);
        return;
    }

    pv_lock(&g_submit_lock);
    uint64_t fence = __atomic_load_n(&pv_ctl.submitted_fence, __ATOMIC_ACQUIRE);
    pv_host(PV_HOST_PRESENT, (uint64_t)(uintptr_t)o, (uint64_t)index, (uint64_t)(uintptr_t)&((const tex_t *)o->tex[index])->t, fence);
    pv_unlock(&g_submit_lock);

    o->presents++;
    pv_atomic_add(&pv_ctl.presents, 1);
}
