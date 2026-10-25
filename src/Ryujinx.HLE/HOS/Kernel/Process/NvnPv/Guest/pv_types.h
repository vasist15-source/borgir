// Paravirtual NVN: object layouts kept in the game's NVN objects, and the command record format
// the host replays. The host reads records and descriptors only (never the objects themselves),
// so everything a replay needs is copied into records by value; NvnPvRecords.cs mirrors the
// record and descriptor layouts below and must change with them.
//
// Layouts follow MacNativeGames / D2R Mobile's NVN layer (MIT) where the game constrains them:
// sizes fit nvn.h's reserved sizes (FITS), NVNtextureView is 16 bytes (the game keeps it on its
// stack), and NVNcommandBuffer keeps the two inline fast-path cursors the game's code reads.
#pragma once

#include "rt.h"
#include "nvn_protos.h"

#define MAGIC(a, b, c, d) ((uint32_t)(a) | (uint32_t)(b) << 8 | (uint32_t)(c) << 16 | (uint32_t)(d) << 24)
#define FITS(T, H) _Static_assert(sizeof(T) <= sizeof(H), #T " does not fit " #H)

// ---------- Texture descriptors (records, texture pools) ----------

// What the game set on a texture builder; the layout follows from it (pv_tex.c).
typedef struct
{
    uint32_t width, height, depth, levels;
    uint32_t stride;      // linear textures: row pitch (0 = derived)
    uint32_t flags;       // NVNtextureFlags
    uint16_t format;      // NVNformat
    uint8_t target;       // NVNtextureTarget
    uint8_t samples;      // 0 or 1 = single-sampled
    int8_t pkg_block_h;   // packaged layout byte 0 + 1 (low nibble log2 block height, high nibble log2 block depth); 0 = automatic
    uint8_t swizzle[4];   // NVNtextureSwizzle per channel
    uint8_t pad[3];
} pv_texdesc;
_Static_assert(sizeof(pv_texdesc) == 36, "pv_texdesc");

typedef struct
{
    uint64_t size, layer_size;   // total storage bytes, bytes per layer
    uint32_t align, layers, bpb; // alignment, layer count, bytes per block
    uint8_t tile_h, tile_d;      // level 0 block height / depth (log2 GOBs)
    uint8_t pad[2];
} pv_texlayout;
_Static_assert(sizeof(pv_texlayout) == 32, "pv_texlayout");

// A texture as the host sees it: its storage address (= GPU address), its description and layout.
// serial changes at every nvnTextureInitialize, so the host never confuses two textures that
// reuse one storage address with different formats or sizes.
typedef struct
{
    uint64_t addr;
    uint64_t serial;
    pv_texdesc d;
    uint32_t pad;
    pv_texlayout l;
} pv_tex;
_Static_assert(sizeof(pv_tex) == 88, "pv_tex");

// NVNtextureView: the game reserves 16 bytes for it (on its stack).
typedef struct
{
    uint8_t set_levels : 1, set_layers : 1, set_format : 1, set_target : 1, set_swizzle : 1, pad0 : 3;
    uint8_t base_level, nlevels, target;
    uint16_t min_layer, nlayers;
    uint16_t format;
    uint8_t swz[4];
    uint8_t pad1[2];
} pv_view;
_Static_assert(sizeof(pv_view) == 16, "NVNtextureView is 16 bytes in TOTK's NVN");
FITS(pv_view, NVNtextureView);

// Texture pool shadow entry (host-readable table the guest keeps per NVNtexturePool).
typedef struct
{
    pv_tex tex;
    pv_view view;
    uint32_t has_view, valid;
    uint64_t pad;
} pv_tex_entry;
_Static_assert(sizeof(pv_tex_entry) == 120, "pv_tex_entry");

// ---------- Samplers ----------

typedef struct
{
    uint8_t minf, magf, ws, wt, wr, cmp_mode, cmp_func, reduction;
    float lod_min, lod_max, lod_bias, aniso, border[4], lod_snap;
    uint32_t border_int;   // border set with SetBorderColori / ui: border holds the integer bits
} pv_samp;
_Static_assert(sizeof(pv_samp) == 48, "pv_samp");

typedef struct
{
    pv_samp samp;
    uint32_t valid, pad;
} pv_samp_entry;
_Static_assert(sizeof(pv_samp_entry) == 56, "pv_samp_entry");

// ---------- Objects in the game's memory ----------

typedef struct { uint32_t flags; } dev_builder_t;
FITS(dev_builder_t, NVNdeviceBuilder);
typedef struct { uint32_t magic, flags, origin, depth_mode; uint64_t dbg_cb, dbg_data; } dev_t_;
FITS(dev_t_, NVNdevice);

typedef struct { NVNdevice *dev; uint32_t flags; uint32_t pad; uint64_t cmd_sz, comp_sz, ctl_sz, flush; void *mem; uint64_t mem_sz; } queue_builder_t;
FITS(queue_builder_t, NVNqueueBuilder);
typedef struct { uint32_t magic, id; NVNdevice *dev; uint64_t submits; } queue_t;
FITS(queue_t, NVNqueue);

typedef struct { NVNdevice *dev; void *mem; uint64_t size; uint32_t flags; } pool_builder_t;
FITS(pool_builder_t, NVNmemoryPoolBuilder);
// mem: the pool's storage, which is also its GPU address (the host maps GPU addresses to guest
// memory one to one). Virtual pools get a guest address range from the host.
typedef struct { uint32_t magic, flags; uint8_t *mem; uint64_t size; uint64_t host_id; } pool_t;
FITS(pool_t, NVNmemoryPool);

typedef struct { NVNdevice *dev; pool_t *pool; int64_t off; uint64_t size; } buf_builder_t;
FITS(buf_builder_t, NVNbufferBuilder);
typedef struct { uint32_t magic, pad; pool_t *pool; int64_t off; uint64_t size; } buf_t;
FITS(buf_t, NVNbuffer);

// Texture and sampler pools: the descriptors live in a guest-side shadow table (pv arena).
typedef struct { const pool_t *pool; int64_t off; int32_t n; uint32_t pad; void *table; } dpool_t;
FITS(dpool_t, NVNtexturePool);
FITS(dpool_t, NVNsamplerPool);

typedef struct
{
    NVNdevice *dev;
    pv_texdesc d;
    uint32_t pad;
    pool_t *pool;
    int64_t off;
    uint8_t has_layout, layout[8];
    uint8_t pad2[7];
    const void *pkg_data;   // SetPackagedTextureData: copied into the storage at Initialize
} tex_builder_t;
FITS(tex_builder_t, NVNtextureBuilder);

// NVNtexture: 128 bytes in TOTK (the game allocates that much).
typedef struct
{
    uint32_t magic;
    uint32_t pad;
    pv_tex t;          // 88
    pool_t *pool;
    int64_t off;
    uint8_t has_layout, layout[8];
    uint8_t pad2[7];
} tex_t;
FITS(tex_t, NVNtexture);

typedef struct { NVNdevice *dev; pv_samp p; } samp_builder_t;
FITS(samp_builder_t, NVNsamplerBuilder);
typedef struct { pv_samp p; } samp_t;
FITS(samp_t, NVNsampler);

// NVNshaderData of every stage, by value: records copy it, the host translates from it.
typedef struct { uint32_t magic, nstage; NVNshaderData sd[6]; uint64_t serial; } prog_t;
FITS(prog_t, NVNprogram);

typedef struct { uint32_t magic, fenced; uint64_t fence; } sync_t;
FITS(sync_t, NVNsync);
typedef struct { const pool_t *pool; int64_t off; } ev_builder_t;
FITS(ev_builder_t, NVNeventBuilder);
typedef struct { uint32_t magic, pad; volatile uint32_t *val; } ev_t;
FITS(ev_t, NVNevent);

typedef struct { NVNdevice *dev; void *native; int32_t ntex, interval, active, pad; NVNtexture *tex[4]; } win_builder_t;
FITS(win_builder_t, NVNwindowBuilder);
typedef struct
{
    uint32_t magic, id;
    void *native;
    int32_t ntex, interval, next, active, crop[4];
    NVNtexture *tex[4];
    uint64_t presents;
    uint64_t released[4];   // host: present count after which texture i may be drawn into again
    uint64_t presented[4];  // guest: present number that last showed texture i
} win_t;
FITS(win_t, NVNwindow);

// NVNcommandBuffer. The game's inlined NVN code checks two write cursors for room before
// writing GPU methods itself: +0x00/+0x08 (command memory, 0x18 bytes) and +0x20/+0x28
// (control memory, 0x30 bytes), and sets a dirty byte at +0x10. Both cursors are kept at
// ~0 / 0 so the room check always fails and the game calls the NVN entry points instead.
typedef struct
{
    uint64_t inline_cmd_cur, inline_cmd_end;   // +0x00, +0x08: sentinels
    uint64_t dirty;                            // +0x10: written by the game
    uint8_t *cur;                              // +0x18: write pointer in the current segment
    uint64_t inline_ctl_cur, inline_ctl_end;   // +0x20, +0x28: sentinels
    uint8_t *end;                              // end of the current segment (room for a final JUMP / END)
    uint8_t *first;                            // first segment of the recording in progress
    PFNNVNCOMMANDBUFFERMEMORYCALLBACKPROC mem_cb;
    void *mem_cb_data;
    NVNdevice *dev;
    uint8_t *ctl;                              // control memory the game gave (marks live there)
    uint64_t ctl_size, ctl_used;
    uint64_t game_size, hw_used;               // command memory the game gave, and what the real driver would have used of it
    uint32_t magic, recording;
    uint32_t last_rec;                         // size of the last record (usage accounting)
    uint32_t seg_cls;                          // size class of the current segment
} cmd_t;
FITS(cmd_t, NVNcommandBuffer);
_Static_assert(__builtin_offsetof(cmd_t, dirty) == 0x10, "NVN inline dirty flag");
_Static_assert(__builtin_offsetof(cmd_t, inline_ctl_cur) == 0x20 && __builtin_offsetof(cmd_t, inline_ctl_end) == 0x28, "inline control cursor");

// ---------- State objects (packed in place; the game copies them by value) ----------

typedef struct { uint32_t blend_en : 8, logic_op : 4, alpha_func : 4, pad : 16; } color_st;
FITS(color_st, NVNcolorState);
typedef struct { uint32_t mask; } chmask_st;   // 4 bits per target: r=1 g=2 b=4 a=8, set = written
FITS(chmask_st, NVNchannelMaskState);
typedef struct { uint64_t target : 3, src_rgb : 7, dst_rgb : 7, src_a : 7, dst_a : 7, eq_rgb : 3, eq_a : 3, pad : 27; } blend_st;
FITS(blend_st, NVNblendState);
typedef struct { uint8_t enable, samples, a2c, a2c_dither, raster_samples, cov_mod, cov2color_en, cov2color_out, sl_en, sl_grid; } ms_st;
FITS(ms_st, NVNmultisampleState);
typedef struct { uint32_t cull : 2, front : 1, mode : 2, offset_en : 3, pad : 24; } poly_st;
FITS(poly_st, NVNpolygonState);
typedef struct
{
    uint64_t dt : 1, dw : 1, df : 4, st : 1, sf_f : 4, sf_b : 4,
             fail_f : 4, zfail_f : 4, zpass_f : 4, fail_b : 4, zfail_b : 4, zpass_b : 4, pad : 25;
} ds_st;
FITS(ds_st, NVNdepthStencilState);
typedef struct { uint32_t fmt : 8, offset : 19, stream : 5; } va_st;
FITS(va_st, NVNvertexAttribState);
typedef struct { uint32_t stride, divisor; } vs_st;
FITS(vs_st, NVNvertexStreamState);

// ---------- Command records: {op, size (with header, multiple of 8)} + payload ----------

typedef struct { uint32_t op, size; } op_hdr;

enum
{
    OP_END = 0, OP_JUMP, OP_CALL,
    OP_SET_RT, OP_CLEAR_COLOR, OP_CLEAR_DS, OP_VIEWPORTS, OP_SCISSORS, OP_DEPTH_RANGES,
    OP_BIND_PROGRAM, OP_BIND_STATE, OP_BIND_VATTR, OP_BIND_VSTREAM, OP_BIND_VBUF,
    OP_BIND_UBO, OP_BIND_SSBO, OP_BIND_TEX, OP_BIND_IMG, OP_BIND_SAMP,
    OP_STENCIL, OP_POLY_OFFSET, OP_BLEND_COLOR, OP_DEPTH_CLAMP, OP_RENDER_ENABLE, OP_RENDER_COND, OP_RASTER_DISCARD,
    OP_DRAW, OP_DRAW_IDX, OP_DRAW_INDIRECT, OP_DRAW_IDX_INDIRECT, OP_DISPATCH, OP_DISPATCH_INDIRECT,
    OP_BARRIER, OP_SET_TEX_POOL, OP_SET_SAMP_POOL,
    OP_UPDATE_UB, OP_COPY_B2B, OP_COPY_B2T, OP_COPY_T2B, OP_COPY_T2T, OP_COPY_STRIDE, OP_CLEAR_TEX, OP_CLEAR_BUF,
    OP_REPORT_COUNTER, OP_RESET_COUNTER, OP_SIGNAL_EVENT, OP_WAIT_EVENT,
    OP_PRIM_RESTART, OP_LINE_WIDTH, OP_POINT_SIZE, OP_ALPHA_REF, OP_SAMPLE_MASK, OP_DEPTH_BOUNDS,
    OP_TILED_CACHE, OP_DISCARD, OP_SCRATCH, OP_PATCH, OP_DRAW_TEXTURE,
    OP_COUNT_
};

typedef struct { uint64_t next; } p_jump;
typedef struct { uint64_t first; } p_call;   // first segment of the called recording

typedef struct { pv_tex tex; pv_view view; uint32_t present, has_view; } p_rt_target;   // 112
typedef struct { int32_t n, has_depth; p_rt_target t[]; } p_set_rt;                     // t[n] colours, then t[n] = depth when has_depth

typedef struct { int32_t index, mask; float rgba[4]; } p_clear_color;
typedef struct { float depth; int32_t dmask, stencil, smask; } p_clear_ds;
typedef struct { int32_t first, n; float v[][4]; } p_viewports;
typedef struct { int32_t first, n; int32_t r[][4]; } p_scissors;
typedef struct { int32_t first, n; float r[][2]; } p_depth_ranges;

typedef struct { int32_t stages, nstage; uint64_t serial; NVNshaderData sd[6]; } p_bind_program;
typedef struct { int32_t kind, pad; uint8_t bytes[24]; } p_bind_state;   // 0 blend, 1 colour, 2 channel mask, 3 multisample, 4 polygon, 5 depth stencil
typedef struct { int32_t n, pad; uint32_t st[]; } p_bind_vattr;
typedef struct { int32_t n, pad; vs_st st[]; } p_bind_vstream;
typedef struct { int32_t first, n; NVNbufferRange r[]; } p_bind_vbuf;
typedef struct { int32_t stage, first, n, pad; NVNbufferRange r[]; } p_bind_buf;
// src != 0: a deferred bind; the handles are read from the game's array at src when the host replays.
// kind (OP_BIND_TEX only): 0 combined texture + sampler handles (nvnCommandBufferBindTexture[s]),
// 1 separate texture handles (BindSeparateTexture[s]); the driver keeps them in different parts of c2.
typedef struct { int32_t stage, first, n, kind; uint64_t src; uint64_t h[]; } p_bind_handles;

typedef struct { int32_t face, value_mask, write_mask, ref, which; } p_stencil;   // which: 0 value mask, 1 write mask, 2 reference
typedef struct { float factor, units, clamp; } p_poly_offset;
typedef struct { float rgba[4]; } p_blend_color;
typedef struct { int32_t value; } p_flag;
typedef struct { int32_t mode, pad; uint64_t addr; } p_render_cond;

typedef struct { int32_t mode, first, count, base_instance, instances; } p_draw;
typedef struct { int32_t mode, type, count, base_vertex, base_instance, instances; uint64_t index_addr; } p_draw_idx;
typedef struct { int32_t mode, type; uint64_t index_addr, indirect_addr; } p_draw_indirect;
typedef struct { int32_t x, y, z, pad; } p_dispatch;
typedef struct { uint64_t addr; } p_dispatch_indirect;
typedef struct { int32_t bits; } p_barrier;
typedef struct { uint64_t table; int32_t n, pad; } p_set_pool;

typedef struct { uint64_t dst, size; uint8_t data[]; } p_update_ub;
typedef struct { uint64_t src, dst, size; int32_t flags, pad; } p_copy_b2b;
typedef struct { uint64_t buf; pv_tex tex; pv_view view; int32_t has_view, flags; NVNcopyRegion r; } p_copy_bt;   // buffer <-> texture
typedef struct { pv_tex src, dst; pv_view sv, dv; int32_t has_sv, has_dv; NVNcopyRegion sr, dr; int32_t flags, pad; } p_copy_t2t;
typedef struct { int64_t stride; } p_copy_stride;
typedef struct { pv_tex tex; pv_view view; int32_t has_view, mask, is_uint, pad; NVNcopyRegion r; union { float f[4]; uint32_t u[4]; } c; } p_clear_tex;
typedef struct { uint64_t addr, size; uint32_t value, pad; } p_clear_buf;

typedef struct { int32_t type, pad; uint64_t addr; } p_report_counter;
typedef struct { int32_t type, pad; } p_reset_counter;
typedef struct { uint64_t addr; int32_t mode, location, flags; uint32_t value; } p_signal_event;
typedef struct { uint64_t addr; int32_t mode; uint32_t value; } p_wait_event;

typedef struct { int32_t enable, index; } p_prim_restart;
typedef struct { float value; } p_float;
typedef struct { int32_t enable; float lo, hi; } p_depth_bounds;
typedef struct { int32_t action; } p_tiled_cache;
typedef struct { int32_t what, index; } p_discard;   // what: 0 colour, 1 depth stencil
typedef struct { uint64_t addr, size; } p_scratch;
typedef struct { int32_t size, pad; } p_patch;

_Static_assert(sizeof(p_rt_target) == 112, "p_rt_target");
_Static_assert(sizeof(p_bind_program) == 112, "p_bind_program");
_Static_assert(sizeof(p_copy_bt) == 144, "p_copy_bt");
_Static_assert(sizeof(p_copy_t2t) == 272, "p_copy_t2t");
_Static_assert(sizeof(p_clear_tex) == 160, "p_clear_tex");
