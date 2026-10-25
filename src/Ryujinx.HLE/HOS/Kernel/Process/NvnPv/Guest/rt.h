// Freestanding runtime for the guest side of the paravirtual NVN layer.
//
// This code runs inside the game's address space, on the game's own threads, under the
// Switch's calling convention. Nothing from a C library is available: the few routines the
// compiler may call on its own (memcpy, memset, memmove, memcmp) are defined in nvnpv.c,
// and everything that needs the host goes through a supervisor call (pv_host).
#pragma once

#include <stddef.h>
#include <stdint.h>

#define PV_EXPORT __attribute__((used, visibility("default")))
#define PV_UNUSED __attribute__((unused))
#define PV_LIKELY(x) __builtin_expect(!!(x), 1)
#define PV_UNLIKELY(x) __builtin_expect(!!(x), 0)

void *memcpy(void *dst, const void *src, size_t n);
void *memmove(void *dst, const void *src, size_t n);
void *memset(void *dst, int c, size_t n);
int memcmp(const void *a, const void *b, size_t n);

static inline int pv_streq(const char *a, const char *b)
{
    while (*a && *a == *b)
    {
        a++;
        b++;
    }

    return *a == *b;
}

static inline size_t pv_strlen(const char *s)
{
    size_t n = 0;

    while (s[n])
    {
        n++;
    }

    return n;
}

// Host calls: SVC 0xF8, operation in x8, arguments in x0-x5, results in x0 and x1.
// x16/x17 are left alone (the host never touches them), x18 is never used by this code
// (-ffixed-x18: the game's own code may keep something in it).
enum
{
    PV_SVC_LOG = 1,        // x0 = NUL-terminated message
    PV_SVC_HELLO = 2,      // x0 = control block address; returns the host's protocol version
};

static inline uint64_t pv_host(uint64_t op, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3)
{
    register uint64_t x0 __asm__("x0") = a0;
    register uint64_t x1 __asm__("x1") = a1;
    register uint64_t x2 __asm__("x2") = a2;
    register uint64_t x3 __asm__("x3") = a3;
    register uint64_t x8 __asm__("x8") = op;

    __asm__ volatile("svc #0xF8" : "+r"(x0), "+r"(x1), "+r"(x2), "+r"(x3) : "r"(x8) : "x4", "x5", "x6", "x7", "memory");

    return x0;
}

static inline void pv_log(const char *message)
{
    pv_host(PV_SVC_LOG, (uint64_t)(uintptr_t)message, 0, 0, 0);
}

static inline uint64_t pv_atomic_add(volatile uint64_t *p, uint64_t v)
{
    return __atomic_fetch_add(p, v, __ATOMIC_RELAXED);
}
