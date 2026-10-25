// Paravirtual NVN: the arena, command record segments and recordings.
//
// The host maps one arena into the game's address space. Records go into 64 KiB segments taken
// from it (a recording that outgrows one continues in another, linked by OP_JUMP). A finished
// recording is named by a mark the layer writes into the command buffer's control memory, as the
// real driver writes its own descriptor there: the command handle is the mark's address. Adding
// that control memory again releases the recordings marked in it - the game only does that once
// it is done with them, as with the real driver.

// ---------- Formatting for the log (no C library) ----------

static char *fmt_u(char *p, char *end, uint64_t v, unsigned base, int width, char pad)
{
    char tmp[24];
    int n = 0;

    do
    {
        unsigned d = (unsigned)(v % base);
        tmp[n++] = (char)(d < 10 ? '0' + d : 'a' + d - 10);
        v /= base;
    }
    while (v && n < 24);

    while (n < width && n < 24)
    {
        tmp[n++] = pad;
    }

    while (n && p < end)
    {
        *p++ = tmp[--n];
    }

    return p;
}

void pv_logf(const char *fmt, ...)
{
    char buf[256];
    char *p = buf, *end = buf + sizeof buf - 1;
    __builtin_va_list ap;
    __builtin_va_start(ap, fmt);

    for (const char *f = fmt; *f && p < end; f++)
    {
        if (*f != '%')
        {
            *p++ = *f;
            continue;
        }

        f++;
        int width = 0;
        char pad = ' ';

        if (*f == '0')
        {
            pad = '0';
            f++;
        }

        while (*f >= '0' && *f <= '9')
        {
            width = width * 10 + (*f++ - '0');
        }

        int lng = 0;

        while (*f == 'l')
        {
            lng++;
            f++;
        }

        switch (*f)
        {
            case 'd':
                {
                    int64_t v = lng ? __builtin_va_arg(ap, int64_t) : __builtin_va_arg(ap, int);

                    if (v < 0 && p < end)
                    {
                        *p++ = '-';
                        v = -v;
                    }

                    p = fmt_u(p, end, (uint64_t)v, 10, width, pad);
                    break;
                }
            case 'u':
                p = fmt_u(p, end, lng ? __builtin_va_arg(ap, uint64_t) : __builtin_va_arg(ap, unsigned), 10, width, pad);
                break;
            case 'x':
                p = fmt_u(p, end, lng ? __builtin_va_arg(ap, uint64_t) : __builtin_va_arg(ap, unsigned), 16, width, pad);
                break;
            case 'p':
                if (p + 2 < end)
                {
                    *p++ = '0';
                    *p++ = 'x';
                }
                p = fmt_u(p, end, (uint64_t)(uintptr_t)__builtin_va_arg(ap, void *), 16, 0, '0');
                break;
            case 's':
                {
                    const char *s = __builtin_va_arg(ap, const char *);

                    if (!s)
                    {
                        s = "(null)";
                    }

                    while (*s && p < end)
                    {
                        *p++ = *s++;
                    }

                    break;
                }
            case 'f':
                {
                    double v = __builtin_va_arg(ap, double);

                    if (v < 0 && p < end)
                    {
                        *p++ = '-';
                        v = -v;
                    }

                    uint64_t whole = (uint64_t)v;
                    p = fmt_u(p, end, whole, 10, 0, ' ');

                    if (p < end)
                    {
                        *p++ = '.';
                    }

                    p = fmt_u(p, end, (uint64_t)((v - (double)whole) * 1000.0), 10, 3, '0');
                    break;
                }
            case '%':
                *p++ = '%';
                break;
            default:
                *p++ = '?';
                break;
        }
    }

    *p = 0;
    __builtin_va_end(ap);
    pv_log(buf);
}

// ---------- Arena ----------

void *pv_arena_alloc(uint64_t size, uint64_t align)
{
    uint64_t old = __atomic_load_n(&pv_ctl.arena_bump, __ATOMIC_RELAXED);

    for (;;)
    {
        uint64_t at = PV_ALIGN(old, align);
        uint64_t next = at + size;

        if (next > pv_ctl.arena_size)
        {
            static int said;

            if (!said++)
            {
                pv_logf("nvnpv: arena exhausted (%llu bytes asked, %llu of %llu used)", size, at, pv_ctl.arena_size);
            }

            return NULL;
        }

        // On failure the compare-exchange reloads `old`; try again from there.
        if (__atomic_compare_exchange_n(&pv_ctl.arena_bump, &old, next, 1, __ATOMIC_RELAXED, __ATOMIC_RELAXED))
        {
            return (void *)(uintptr_t)(pv_ctl.arena_base + at);
        }
    }
}

// ---------- Segments ----------

// Segments come in size classes: a recording starts in a small one and each further segment is
// one class larger, so a short recording wastes little and a long one needs few links. Freed
// segments go to their class's list; segments over the largest class stay with the arena.
#define SEG_HDR 16u          // seg_hdr
#define SEG_RESERVE 24u      // room kept at a segment's end for the final JUMP / END record
#define SEG_CLASSES 6

static const uint32_t g_seg_size[SEG_CLASSES] = { 512, 2048, 8192, 32768, 131072, 524288 };

typedef struct seg_hdr
{
    uint32_t size;           // bytes, header included
    uint32_t cls;            // size class, or SEG_CLASSES for a one-off large segment
    uint64_t gen;            // bumped at every reuse: a mark names a recording by first segment and generation
} seg_hdr;

static uint64_t g_seg_free[SEG_CLASSES];

static uint8_t *seg_alloc(uint64_t need, uint32_t min_cls)
{
    uint32_t cls = min_cls;

    while (cls < SEG_CLASSES && need + SEG_HDR + SEG_RESERVE > g_seg_size[cls])
    {
        cls++;
    }

    seg_hdr *s = NULL;
    uint64_t size;

    if (cls < SEG_CLASSES)
    {
        size = g_seg_size[cls];
        pv_lock(&pv_ctl.seg_lock);

        if (g_seg_free[cls])
        {
            s = (seg_hdr *)(uintptr_t)g_seg_free[cls];
            g_seg_free[cls] = *(uint64_t *)(s + 1);   // next link stored after the header
        }

        pv_unlock(&pv_ctl.seg_lock);
    }
    else
    {
        size = PV_ALIGN(need + SEG_HDR + SEG_RESERVE, 4096);
    }

    if (!s)
    {
        s = pv_arena_alloc(size, size >= 4096 ? 4096 : size);

        if (!s)
        {
            return NULL;
        }

        s->gen = 0;
    }

    s->size = (uint32_t)size;
    s->cls = cls;
    s->gen++;
    pv_atomic_add(&pv_ctl.segments_live, 1);

    return (uint8_t *)s;
}

static void seg_free_one(seg_hdr *s)
{
    __atomic_fetch_sub(&pv_ctl.segments_live, 1, __ATOMIC_RELAXED);
    s->gen++;

    if (s->cls >= SEG_CLASSES)
    {
        return;   // large one-off segments stay with the arena (rare: big uniform updates)
    }

    pv_lock(&pv_ctl.seg_lock);
    *(uint64_t *)(s + 1) = g_seg_free[s->cls];
    g_seg_free[s->cls] = (uint64_t)(uintptr_t)s;
    pv_unlock(&pv_ctl.seg_lock);
}

// Frees every segment of a recording, following its JUMP records.
static void recording_free(uint8_t *first)
{
    uint8_t *seg = first;

    while (seg)
    {
        uint8_t *p = seg + SEG_HDR;
        uint8_t *next = NULL;

        for (;;)
        {
            op_hdr h;
            __builtin_memcpy(&h, p, sizeof h);

            if (h.size < 8 || (h.size & 7) || p + h.size > seg + ((seg_hdr *)seg)->size)
            {
                pv_logf("nvnpv: corrupt record (op %u size %u) freeing recording %p", h.op, h.size, first);
                break;
            }

            if (h.op == OP_END)
            {
                break;
            }

            if (h.op == OP_JUMP)
            {
                next = (uint8_t *)(uintptr_t)((const p_jump *)(p + 8))->next;
                break;
            }

            p += h.size;
        }

        seg_free_one((seg_hdr *)seg);
        seg = next;
    }

    __atomic_fetch_sub(&pv_ctl.recordings_live, 1, __ATOMIC_RELAXED);
}

// ---------- Recording into a command buffer ----------

static void rec_start(cmd_t *c)
{
    uint8_t *seg = seg_alloc(0, 0);

    c->first = seg;
    c->cur = seg ? seg + SEG_HDR : NULL;
    c->end = seg ? seg + ((seg_hdr *)seg)->size - SEG_RESERVE : NULL;
    c->seg_cls = 0;
}

static __attribute__((noinline)) int rec_grow(cmd_t *c, uint32_t sz)
{
    uint32_t cls;
    uint8_t *seg;

    // One class above the segment being left, so long recordings take few links.
    cls = c->seg_cls + 1 < SEG_CLASSES ? c->seg_cls + 1 : SEG_CLASSES - 1;
    seg = seg_alloc(sz, cls);

    if (!seg)
    {
        return 0;
    }

    if (c->cur)
    {
        op_hdr *h = (op_hdr *)c->cur;
        h->op = OP_JUMP;
        h->size = 16;
        ((p_jump *)(h + 1))->next = (uint64_t)(uintptr_t)seg;
    }
    else
    {
        c->first = seg;
    }

    c->cur = seg + SEG_HDR;
    c->end = seg + ((seg_hdr *)seg)->size - SEG_RESERVE;
    c->seg_cls = ((seg_hdr *)seg)->cls < SEG_CLASSES ? ((seg_hdr *)seg)->cls : SEG_CLASSES - 1;

    return 1;
}

// Room for one record of `payload` bytes; returns its payload (zeroed), or a scratch buffer when
// the arena is exhausted (the record is then lost, which the log reports).
static void *rec(NVNcommandBuffer *cb, uint32_t op, size_t payload)
{
    static uint8_t lost[0x10000 + 64];
    cmd_t *c = (cmd_t *)cb;
    uint32_t sz = (uint32_t)((8 + payload + 7) & ~(size_t)7);

    if (PV_UNLIKELY(!c->cur || c->cur + sz > c->end))
    {
        if (!c->recording)
        {
            pv_logf("nvnpv: command op %u recorded outside BeginRecording on %p", op, cb);
        }

        if (!rec_grow(c, sz))
        {
            return memset(lost, 0, payload < sizeof lost ? payload : sizeof lost);
        }
    }

    op_hdr *h = (op_hdr *)c->cur;
    h->op = op;
    h->size = sz;
    c->cur += sz;
    c->last_rec = sz;
    pv_atomic_add(&pv_ctl.records, 1);

    void *p = h + 1;
    memset(p, 0, sz - 8);

    return p;
}

#define REC(c, OP, T) ((T *)rec((c), (OP), sizeof(T)))
#define RECN(c, OP, T, ARR, N) ((T *)rec((c), (OP), __builtin_offsetof(T, ARR) + sizeof(((T *)0)->ARR[0]) * (size_t)(N)))

// The game sizes some of its heaps from nvnCommandBufferGetCommandMemoryUsed, so usage is reported
// as what the real driver's methods would take, not our record sizes. Known encodings (from a
// real recording, via MacNativeGames): each record type counts as its record size unless fixed up.
static inline void hw_fix(NVNcommandBuffer *cb, uint32_t bytes)
{
    ((cmd_t *)cb)->hw_used += bytes;
}

static inline void hw_count(NVNcommandBuffer *cb)
{
    cmd_t *c = (cmd_t *)cb;
    c->hw_used += c->last_rec;
}

// ---------- Recordings named by marks in control memory ----------

#define MARK_MAGIC 0x4B52414Du          // "MARK"
#define MARK_BYTES 16

typedef struct
{
    uint32_t magic;
    uint32_t gen;       // generation of the first segment when the recording ended (low 32 bits)
    uint64_t first;     // first segment
} pv_mark;
_Static_assert(sizeof(pv_mark) == MARK_BYTES, "pv_mark");

// Registry of live recordings by control-memory page, so AddControlMemory can release the
// recordings marked in the memory it gets back. Buckets hold mark addresses.
#define REG_BUCKETS 4096
#define REG_NODES 0x40000

typedef struct reg_node
{
    uint64_t mark;
    struct reg_node *next;
} reg_node;

static reg_node **g_reg;
static reg_node *g_reg_free;
static reg_node *g_reg_pool;
static uint32_t g_reg_pool_used;

static uint32_t reg_bucket(uint64_t page)
{
    return (uint32_t)((page * 0x9E3779B97F4A7C15ull) >> 52) & (REG_BUCKETS - 1);
}

// A mark still names a live recording: its first segment lies in the arena, aligned as segments
// are (512 bytes at least), and carries the mark's generation. A mark the game partly overwrote
// (another command buffer's control memory can start 8 bytes into it) fails this instead of
// sending the caller through a pointer made of the newer mark's bytes.
static int mark_live(const pv_mark *m)
{
    uint64_t first = m->first;
    uint64_t base = pv_ctl.arena_base, end = pv_ctl.arena_base + pv_ctl.arena_size;

    return m->magic == MARK_MAGIC && first >= base && first + SEG_HDR <= end && (first & 511) == 0 &&
           (uint32_t)((const seg_hdr *)(uintptr_t)first)->gen == m->gen;
}

static void reg_add(uint64_t mark)
{
    pv_lock(&pv_ctl.rec_lock);

    // Once per address: two nodes for one mark would release its recording twice.
    if (g_reg)
    {
        for (reg_node *e = g_reg[reg_bucket(mark >> 12)]; e; e = e->next)
        {
            if (e->mark == mark)
            {
                pv_unlock(&pv_ctl.rec_lock);
                return;
            }
        }
    }

    if (!g_reg)
    {
        g_reg = pv_arena_alloc(sizeof(reg_node *) * REG_BUCKETS, 64);
        g_reg_pool = pv_arena_alloc(sizeof(reg_node) * REG_NODES, 64);

        if (g_reg)
        {
            memset(g_reg, 0, sizeof(reg_node *) * REG_BUCKETS);
        }
    }

    reg_node *n = g_reg_free;

    if (n)
    {
        g_reg_free = n->next;
    }
    else if (g_reg_pool && g_reg_pool_used < REG_NODES)
    {
        n = &g_reg_pool[g_reg_pool_used++];
    }

    if (n && g_reg)
    {
        uint32_t b = reg_bucket(mark >> 12);
        n->mark = mark;
        n->next = g_reg[b];
        g_reg[b] = n;
    }
    else
    {
        static int said;

        if (!said++)
        {
            pv_log("nvnpv: recording registry full; recordings are no longer released");
        }
    }

    pv_unlock(&pv_ctl.rec_lock);
}

// Releases the recordings whose marks overlap [lo, hi) - a mark is 16 bytes, so one starting up
// to 15 bytes before lo is overwritten by the new owner too. A mark the game has overwritten names
// nothing any more; its recording is released all the same when the mark still reads as live
// (its segments are known only through the mark, which is why the registry keeps the mark
// address and the mark keeps the rest).
static void reg_release(uint64_t lo, uint64_t hi)
{
    if (!g_reg || hi <= lo)
    {
        return;
    }

    lo = lo >= MARK_BYTES - 1 ? lo - (MARK_BYTES - 1) : 0;

    pv_lock(&pv_ctl.rec_lock);

    for (uint64_t page = lo >> 12; page <= (hi - 1) >> 12; page++)
    {
        reg_node **pp = &g_reg[reg_bucket(page)];

        while (*pp)
        {
            reg_node *n = *pp;

            if (n->mark >= lo && n->mark < hi && (n->mark >> 12) == page)
            {
                pv_mark *m = (pv_mark *)(uintptr_t)n->mark;

                if (mark_live(m))
                {
                    recording_free((uint8_t *)(uintptr_t)m->first);
                }

                m->magic = 0;
                *pp = n->next;
                n->next = g_reg_free;
                g_reg_free = n;
                continue;
            }

            pp = &n->next;
        }
    }

    pv_unlock(&pv_ctl.rec_lock);
}

// The recording a handle names, or NULL (with a log line) when the handle is not a live mark.
static uint8_t *handle_recording(NVNcommandHandle h, uint32_t *gen)
{
    const pv_mark *m = (const pv_mark *)(uintptr_t)h;

    if (!h || !mark_live(m))
    {
        static int said;

        if (said++ < 16)
        {
            pv_logf("nvnpv: command handle %llx is not a live recording", (uint64_t)h);
        }

        return NULL;
    }

    if (gen)
    {
        *gen = m->gen;
    }

    return (uint8_t *)(uintptr_t)m->first;
}
