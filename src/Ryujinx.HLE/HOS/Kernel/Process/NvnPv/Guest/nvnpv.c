// Paravirtual NVN, guest side.
//
// The game obtains every NVN function from nvnBootstrapLoader (imported from nnSdk) and
// nvnDeviceGetProcAddress. The host links this object into the game's address space and points
// the bootstrap at pv_bootstrap. With pv_ctl.mode = 0 the game gets the real driver's functions
// (the layer only counts presents); with mode = 1 it gets this layer's implementation of NVN for
// every name it knows and a reporting stub for the rest: the real driver is then never called,
// commands are recorded into the arena (pv_rec.c) and the host replays them on Metal.
//
// Everything here runs on the game's threads with the game's calling convention; the host is
// reached only through supervisor calls (rt.h), so what stays in this layer costs what a function
// call costs. It is one translation unit: the files below are included, not linked.
//
// nvn.h is h1k421's reconstruction of the NVN types (MIT); nvn_protos.h, names.txt and the NVN
// semantics come from MacNativeGames / D2R Mobile's NVN layer (MIT); pv_tex.c derives from deko3d (zlib).
#include "pv_core.h"

// ---------- C runtime pieces the compiler may call ----------

__attribute__((no_builtin("memcpy"))) void *memcpy(void *dst, const void *src, size_t n)
{
    uint8_t *d = dst;
    const uint8_t *s = src;

    while (n >= 8)
    {
        uint64_t v;
        __builtin_memcpy_inline(&v, s, 8);
        __builtin_memcpy_inline(d, &v, 8);
        d += 8;
        s += 8;
        n -= 8;
    }

    while (n--)
    {
        *d++ = *s++;
    }

    return dst;
}

__attribute__((no_builtin("memmove"))) void *memmove(void *dst, const void *src, size_t n)
{
    uint8_t *d = dst;
    const uint8_t *s = src;

    if (d <= s || d >= s + n)
    {
        return memcpy(dst, src, n);
    }

    while (n--)
    {
        d[n] = s[n];
    }

    return dst;
}

__attribute__((no_builtin("memset"))) void *memset(void *dst, int c, size_t n)
{
    uint8_t *d = dst;
    uint64_t v = (uint8_t)c * 0x0101010101010101ull;

    while (n >= 8)
    {
        __builtin_memcpy_inline(d, &v, 8);
        d += 8;
        n -= 8;
    }

    while (n--)
    {
        *d++ = (uint8_t)c;
    }

    return dst;
}

__attribute__((no_builtin("memcmp"))) int memcmp(const void *a, const void *b, size_t n)
{
    const uint8_t *x = a, *y = b;

    for (size_t i = 0; i < n; i++)
    {
        if (x[i] != y[i])
        {
            return x[i] < y[i] ? -1 : 1;
        }
    }

    return 0;
}

PV_EXPORT struct pv_ctl pv_ctl = { .magic = PV_MAGIC };

#include "pv_tex.c"
#include "pv_rec.c"
#include "pv_obj.c"
#include "pv_cmd.c"
#include "pv_queue.c"

// ---------- Debug labels and the like: meaningful only to NVN debugging tools ----------

NVN_NOPROTO(nvnEventSetDebugLabel, void, NVNevent *o, const char *s) { (void)o; (void)s; }

// ---------- Entry points the layer does not implement ----------

// One stub per name handed out without an implementation (mode 1): the first call logs the name;
// every call returns 0. The game may survive a missing query; a missing command shows as
// something wrong on screen, never as a crash in the real driver reading this layer's objects.
#define UNIMPL_MAX 256

static const char *g_unimpl_name[UNIMPL_MAX];
static uint32_t g_unimpl_n;
static uint8_t g_unimpl_said[UNIMPL_MAX];

static __attribute__((noinline)) uint64_t pv_unimpl(unsigned i)
{
    pv_atomic_add(&pv_ctl.unimpl_calls, 1);

    if (i < UNIMPL_MAX && !g_unimpl_said[i])
    {
        g_unimpl_said[i] = 1;
        pv_host(PV_HOST_UNIMPL, (uint64_t)(uintptr_t)g_unimpl_name[i], 0, 0, 0);
    }

    return 0;
}

// Stub names are three digits h t o (h = 1..4, t and o = 0..7): table index (h - 1) * 64 + t * 8 + o.
#define UIDX(n) (((n) / 100 - 1) * 64 + ((n) / 10 % 10) * 8 + (n) % 10)
#define U1(n) static uint64_t pv_unimpl_##n(void) { return pv_unimpl(UIDX(n)); }
#define U8(n) U1(n##0) U1(n##1) U1(n##2) U1(n##3) U1(n##4) U1(n##5) U1(n##6) U1(n##7)
#define U64(n) U8(n##0) U8(n##1) U8(n##2) U8(n##3) U8(n##4) U8(n##5) U8(n##6) U8(n##7)
U64(1) U64(2) U64(3) U64(4)

#define P1(n) (void *)pv_unimpl_##n,
#define P8(n) P1(n##0) P1(n##1) P1(n##2) P1(n##3) P1(n##4) P1(n##5) P1(n##6) P1(n##7)
#define P64(n) P8(n##0) P8(n##1) P8(n##2) P8(n##3) P8(n##4) P8(n##5) P8(n##6) P8(n##7)
static void *const g_unimpl_stub[UNIMPL_MAX] = { P64(1) P64(2) P64(3) P64(4) };

static volatile uint64_t g_unimpl_lock;

static void *unimpl_stub(const char *name)
{
    pv_lock(&g_unimpl_lock);

    for (uint32_t i = 0; i < g_unimpl_n; i++)
    {
        if (pv_streq(g_unimpl_name[i], name))
        {
            pv_unlock(&g_unimpl_lock);
            return g_unimpl_stub[i];
        }
    }

    void *stub = NULL;

    if (g_unimpl_n < UNIMPL_MAX)
    {
        g_unimpl_name[g_unimpl_n] = name;   // names handed to GetProcAddress are string literals of the game
        stub = g_unimpl_stub[g_unimpl_n++];
    }

    pv_unlock(&g_unimpl_lock);

    return stub;
}

// ---------- Name lookup ----------

extern const pv_impl __start_pv_nvn[];
extern const pv_impl __stop_pv_nvn[];

static void *impl_of(const char *name)
{
    for (const pv_impl *e = __start_pv_nvn; e < __stop_pv_nvn; e++)
    {
        if (pv_streq(e->name, name))
        {
            return e->fn;
        }
    }

    return NULL;
}

typedef PFNNVNGENERICFUNCPTRPROC (*pv_boot_fn)(const char *);

static NVNT_nvnDeviceGetProcAddress real_get_proc_address;
static NVNT_nvnQueuePresentTexture real_present;

// Mode 0: the real driver does everything; only presents are counted.
static void pv_counting_present(NVNqueue *queue, NVNwindow *window, int index)
{
    if (pv_atomic_add(&pv_ctl.presents, 1) == 0)
    {
        pv_ctl.host_version = pv_host(PV_HOST_HELLO, (uint64_t)(uintptr_t)&pv_ctl, 0, 0, 0);
    }

    real_present(queue, window, index);
}

static PFNNVNGENERICFUNCPTRPROC pv_get_proc_address(const NVNdevice *device, const char *name);

static PFNNVNGENERICFUNCPTRPROC pv_resolve(const char *name, PFNNVNGENERICFUNCPTRPROC real)
{
    if (!name)
    {
        return real;
    }

    if (pv_streq(name, "nvnDeviceGetProcAddress"))
    {
        return (PFNNVNGENERICFUNCPTRPROC)pv_get_proc_address;
    }

    if (!pv_ctl.mode)
    {
        if (pv_streq(name, "nvnQueuePresentTexture") && real)
        {
            real_present = (NVNT_nvnQueuePresentTexture)real;
            pv_atomic_add(&pv_ctl.wrapped, 1);

            return (PFNNVNGENERICFUNCPTRPROC)pv_counting_present;
        }

        return real;
    }

    void *fn = impl_of(name);

    if (fn)
    {
        pv_atomic_add(&pv_ctl.wrapped, 1);
        return (PFNNVNGENERICFUNCPTRPROC)fn;
    }

    // Names the real driver does not know either stay unknown (the game may probe for them).
    if (!real)
    {
        return NULL;
    }

    return (PFNNVNGENERICFUNCPTRPROC)unimpl_stub(name);
}

static PFNNVNGENERICFUNCPTRPROC pv_get_proc_address(const NVNdevice *device, const char *name)
{
    pv_atomic_add(&pv_ctl.gpa_calls, 1);

    // The real driver answers which names exist; with the layer in charge its own device is never
    // initialised, so it is asked without one (core names do not need it).
    PFNNVNGENERICFUNCPTRPROC real = real_get_proc_address ? real_get_proc_address(pv_ctl.mode ? NULL : device, name) : NULL;

    return pv_resolve(name, real);
}

// Replaces nvnBootstrapLoader: its first instruction branches here.
PV_EXPORT PFNNVNGENERICFUNCPTRPROC pv_bootstrap(const char *name)
{
    pv_boot_fn real_boot = (pv_boot_fn)(uintptr_t)pv_ctl.real_bootstrap;

    if (pv_atomic_add(&pv_ctl.boot_calls, 1) == 0 && pv_ctl.mode)
    {
        pv_ctl.host_version = pv_host(PV_HOST_HELLO, (uint64_t)(uintptr_t)&pv_ctl, 0, 0, 0);
        pv_logf("nvnpv: taking NVN over (%u entry points implemented)", (unsigned)(__stop_pv_nvn - __start_pv_nvn));
    }

    if (!real_get_proc_address)
    {
        real_get_proc_address = (NVNT_nvnDeviceGetProcAddress)real_boot("nvnDeviceGetProcAddress");
    }

    return pv_resolve(name, name && pv_streq(name, "nvnDeviceGetProcAddress") ? NULL : real_boot(name));
}
