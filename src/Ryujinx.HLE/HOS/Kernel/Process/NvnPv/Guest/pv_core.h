// Paravirtual NVN, guest side: shared declarations of the single translation unit (nvnpv.c
// includes every .c file of this directory).
#pragma once

#include "pv_types.h"

// ---------- Host calls (SVC 0xF8, operation in x8) ----------
enum
{
    PV_HOST_LOG = 1,          // x0 = message
    PV_HOST_HELLO = 2,        // x0 = control block; returns the protocol version
    PV_HOST_UNIMPL = 3,       // x0 = function name: an entry point the layer does not implement was called
    PV_HOST_POOL = 4,         // x0 = pool storage (GPU address), x1 = size, x2 = flags, x3 = 1 add / 0 remove
    PV_HOST_VPOOL = 5,        // x0 = size: reserve a guest range for a virtual pool; returns its address (0 = failed)
    PV_HOST_VPOOL_MAP = 6,    // x0 = virtual pool address, x1 = request count, x2 = requests (pv_vmap_req)
    PV_HOST_SUBMIT = 7,       // x0 = queue id, x1 = handle count, x2 = handles, x3 = fence value of this submission
    PV_HOST_WAIT = 8,         // x0 = fence value, x1 = timeout (ns, ~0 = forever); returns 1 when signalled
    PV_HOST_PRESENT = 9,      // x0 = window, x1 = texture index, x2 = pv_tex of the texture, x3 = fence value
    PV_HOST_ACQUIRE = 10,     // x0 = window; returns the texture index to draw into (waits for one to be free)
    PV_HOST_TEXELS = 11,      // x0 = pv_texels request
    PV_HOST_WINDOW = 12,      // x0 = window, x1 = texture count, x2 = pv_tex array: window initialised
    PV_HOST_FINISH = 13,      // x0 = fence value: wait until the GPU finished everything up to it
    PV_HOST_PROGRAM = 14,     // x0 = prog_t: shaders set (the host may start translating)
    PV_HOST_TEX_FINAL = 15,   // x0 = serial, x1 = storage address: texture finalized
};

typedef struct { uint64_t physical_addr, virtual_offset, size, physical_size; } pv_vmap_req;

typedef struct
{
    uint64_t write;        // 1 = WriteTexels (buffer -> texture), 0 = ReadTexels
    uint64_t data;         // game buffer
    int64_t row, image;    // strides in bytes
    pv_tex tex;
    pv_view view;
    uint32_t has_view, pad;
    NVNcopyRegion r;
} pv_texels;
_Static_assert(sizeof(pv_texels) == 168, "pv_texels");

// ---------- Control block (also read and written by the host) ----------

#define PV_MAGIC 0x313030565056564Eull   // "NVNPV001"

struct pv_ctl
{
    uint64_t magic;
    uint64_t real_bootstrap;     // host: trampoline running the real nvnBootstrapLoader
    uint64_t host_version;       // host: answer to PV_HOST_HELLO
    uint64_t boot_calls;         // statistics, read by the host
    uint64_t gpa_calls;
    uint64_t wrapped;
    uint64_t presents;
    uint64_t mode;               // host: 0 = forward everything to the real driver, 1 = take NVN over
    uint64_t arena_base;         // host: guest address of the arena
    uint64_t arena_size;
    uint64_t arena_bump;         // guest: bytes of the arena handed out
    uint64_t completed_fence;    // host: every submission with a fence value <= this has completed on the GPU
    uint64_t submitted_fence;    // guest: fence value of the last submission
    uint64_t serial;             // guest: object serials
    uint64_t unimpl_calls;
    uint64_t segments_live;      // statistics
    uint64_t recordings_live;
    uint64_t records;
    uint64_t submits;
    uint64_t seg_lock;           // guest locks (spin)
    uint64_t rec_lock;
    uint64_t seg_free;           // free segment list head (guest address)
    uint64_t names_seen;         // statistics: distinct names handed out
    uint64_t oracle;             // host: 1 = check layouts against the real driver
    uint64_t pool_gen;           // guest: bumped after every texture / sampler registration (host caches bindings by it)
};

extern struct pv_ctl pv_ctl;

// ---------- Small runtime helpers ----------

#define B8(x) ((x) & 0xff)
#define PV_ALIGN(v, a) (((v) + (a) - 1) & ~(uint64_t)((a) - 1))

static inline void pv_lock(volatile uint64_t *l)
{
    while (__atomic_exchange_n(l, 1, __ATOMIC_ACQUIRE))
    {
        while (__atomic_load_n(l, __ATOMIC_RELAXED))
        {
            __asm__ volatile("yield");
        }
    }
}

static inline void pv_unlock(volatile uint64_t *l)
{
    __atomic_store_n(l, 0, __ATOMIC_RELEASE);
}

static inline uint64_t pv_new_serial(void)
{
    return __atomic_add_fetch(&pv_ctl.serial, 1, __ATOMIC_RELAXED);
}

void pv_logf(const char *fmt, ...);
void *pv_arena_alloc(uint64_t size, uint64_t align);

// pv_tex.c
int pv_fmt_bpb(int f, int *bw, int *bh);
int pv_tex_is_linear(const pv_texdesc *d);
uint32_t pv_tex_linear_stride(const pv_texdesc *d);
void pv_tex_layout(const pv_texdesc *d, pv_texlayout *o);
uint64_t pv_tex_level_offset(const pv_texdesc *d, unsigned level);

// ---------- Implementation registration ----------

typedef struct { const char *name; void *fn; } pv_impl;

// Every implementation is checked against the prototype in nvn_protos.h at compile time and
// listed in the pv_nvn section, which pv_bootstrap searches by name.
#define NVN(name, ret, ...)                                                                        \
    static ret h_##name(__VA_ARGS__);                                                              \
    static const NVNT_##name chk_##name __attribute__((unused)) = h_##name;                        \
    __attribute__((used, section("pv_nvn"))) static const pv_impl e_##name = {#name, (void *)h_##name}; \
    static ret h_##name(__VA_ARGS__)

// Entry points without a prototype in nvn_protos.h (newer NVN, or the game's inline fast-path fallbacks).
#define NVN_NOPROTO(name, ret, ...)                                                                \
    static ret h_##name(__VA_ARGS__);                                                              \
    __attribute__((used, section("pv_nvn"))) static const pv_impl e_##name = {#name, (void *)h_##name}; \
    static ret h_##name(__VA_ARGS__)
