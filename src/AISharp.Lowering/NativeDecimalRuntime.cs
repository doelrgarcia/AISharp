using System.Globalization;
using System.Linq;

namespace AISharp.Lowering;

/// <summary>
/// The C11 decimal runtime emitted into native programs that use the <c>decimal</c> type (N6).
/// It reproduces System.Decimal (96-bit mantissa, sign, scale 0..28) including the exact scaling, banker's rounding,
/// zero-sign and zero-scale behavior of the .NET runtime. It expects the host program to have declared
/// <c>AI_NORETURN static void ai_fault(const char *exception)</c> and to have included stdint/string/stdio/math.
/// The differential test compiles exactly this text and compares it against the real System.Decimal.
/// </summary>
public static class NativeDecimalRuntime
{
    public static readonly string Source = Build();

    private static string Build()
    {
        var doubles = string.Join(",", Enumerable.Range(0, 29).Select(i =>
            "0x" + System.BitConverter.DoubleToUInt64Bits(double.Parse("1e" + i, CultureInfo.InvariantCulture)).ToString("X16", CultureInfo.InvariantCulture) + "ull"));
        var u64 = string.Join(",", Enumerable.Range(0, 20).Select(i =>
        {
            System.Numerics.BigInteger v = System.Numerics.BigInteger.Pow(10, i);
            return v.ToString(CultureInfo.InvariantCulture) + "ull";
        }));
        return Template.Replace("@DOUBLES@", doubles).Replace("@U64@", u64);
    }

    private const string Template = """
#include <math.h>
#ifdef _MSC_VER
#pragma warning(disable: 4505)
#endif
typedef struct { uint32_t lo, mid, hi, flags; } ai_dec;
typedef struct { uint32_t w[8]; } ai_big;
#define AI_DEC_SIGN 0x80000000u
#define AI_DEC_SMASK 0x00FF0000u
static const uint32_t ai_dp32[10] = {1u,10u,100u,1000u,10000u,100000u,1000000u,10000000u,100000000u,1000000000u};
static const uint64_t ai_dp64[20] = {@U64@};
static const uint64_t ai_dpd[29] = {@DOUBLES@};
static double ai_dec_pd(int i) { double v; memcpy(&v, &ai_dpd[i], sizeof v); return v; }
static void ai_dec_ovf(void) { ai_fault("System.OverflowException"); }
static int ai_dec_scale_of(ai_dec d) { return (int)((d.flags >> 16) & 0xFFu); }
static ai_big ai_big_of(ai_dec d) { ai_big b; memset(&b, 0, sizeof b); b.w[0] = d.lo; b.w[1] = d.mid; b.w[2] = d.hi; return b; }
static ai_big ai_big_u64(uint64_t v) { ai_big b; memset(&b, 0, sizeof b); b.w[0] = (uint32_t)v; b.w[1] = (uint32_t)(v >> 32); return b; }
static int ai_big_zero(const ai_big *a) { for (int i = 0; i < 8; ++i) if (a->w[i]) return 0; return 1; }
static int ai_big_fits96(const ai_big *a) { for (int i = 3; i < 8; ++i) if (a->w[i]) return 0; return 1; }
static int ai_big_cmp(const ai_big *a, const ai_big *b) { for (int i = 7; i >= 0; --i) if (a->w[i] != b->w[i]) return a->w[i] > b->w[i] ? 1 : -1; return 0; }
static uint32_t ai_big_mul_small(ai_big *a, uint32_t m) { uint64_t c = 0; for (int i = 0; i < 8; ++i) { uint64_t t = (uint64_t)a->w[i] * m + c; a->w[i] = (uint32_t)t; c = t >> 32; } return (uint32_t)c; }
static uint32_t ai_big_div_small(ai_big *a, uint32_t d) { uint64_t r = 0; for (int i = 7; i >= 0; --i) { uint64_t cur = (r << 32) | a->w[i]; a->w[i] = (uint32_t)(cur / d); r = cur % d; } return (uint32_t)r; }
static void ai_big_add(ai_big *a, const ai_big *b) { uint64_t c = 0; for (int i = 0; i < 8; ++i) { uint64_t t = (uint64_t)a->w[i] + b->w[i] + c; a->w[i] = (uint32_t)t; c = t >> 32; } }
static void ai_big_sub(ai_big *a, const ai_big *b) { uint64_t br = 0; for (int i = 0; i < 8; ++i) { uint64_t t = (uint64_t)a->w[i] - b->w[i] - br; a->w[i] = (uint32_t)t; br = (t >> 32) & 1u; } }
static void ai_big_add_small(ai_big *a, uint32_t v) { uint64_t c = v; for (int i = 0; i < 8; ++i) { uint64_t t = (uint64_t)a->w[i] + c; a->w[i] = (uint32_t)t; c = t >> 32; } }
static void ai_big_mul_pow10(ai_big *a, int k) { while (k >= 9) { ai_big_mul_small(a, 1000000000u); k -= 9; } if (k > 0) ai_big_mul_small(a, ai_dp32[k]); }
static void ai_big_shl1(ai_big *a) { for (int i = 7; i > 0; --i) a->w[i] = (a->w[i] << 1) | (a->w[i - 1] >> 31); a->w[0] <<= 1; }
static void ai_big_divmod(const ai_big *n, const ai_big *d, ai_big *q, ai_big *r) {
    memset(q, 0, sizeof *q); memset(r, 0, sizeof *r);
    int top = 7; while (top > 0 && n->w[top] == 0) --top;
    for (int i = top * 32 + 31; i >= 0; --i) {
        ai_big_shl1(r); r->w[0] |= (n->w[i >> 5] >> (i & 31)) & 1u;
        if (ai_big_cmp(r, d) >= 0) { ai_big_sub(r, d); q->w[i >> 5] |= 1u << (i & 31); }
    }
}
static ai_dec ai_dec_pack(const ai_big *m, int scale, uint32_t sign) { ai_dec d; d.lo = m->w[0]; d.mid = m->w[1]; d.hi = m->w[2]; d.flags = sign | ((uint32_t)scale << 16); return d; }
/* Reduces v (exact, possibly wider than 96 bits) to at most 96 bits and scale <= 28 with one round-half-even step; returns the new scale. */
static int ai_dec_scale_result(ai_big *v, int scale) {
    int k = 0, sticky = 0; uint32_t last = 0;
    while (!ai_big_fits96(v) || scale - k > 28) {
        if (k >= scale) ai_dec_ovf();
        uint32_t rem = ai_big_div_small(v, 10u);
        if (last != 0) sticky = 1;
        last = rem; ++k;
    }
    if (k > 0) {
        if (last > 5 || (last == 5 && (sticky || (v->w[0] & 1u)))) ai_big_add_small(v, 1u);
        if (!ai_big_fits96(v)) {
            if (scale - k == 0) ai_dec_ovf();
            memset(v, 0, sizeof *v); v->w[0] = 0x9999999Au; v->w[1] = 0x99999999u; v->w[2] = 0x19999999u; ++k;
        }
    }
    return scale - k;
}
static ai_dec ai_dec_neg(ai_dec a) { a.flags ^= AI_DEC_SIGN; return a; }
static ai_dec ai_dec_addsub(ai_dec d1, ai_dec d2, int subtract) {
    uint32_t xf = d1.flags ^ d2.flags;
    int sign = subtract ^ ((xf & AI_DEC_SIGN) != 0);
    uint32_t basis = d1.flags & AI_DEC_SIGN;
    int scale; ai_big x, y, v;
    if ((xf & AI_DEC_SMASK) == 0) { x = ai_big_of(d1); y = ai_big_of(d2); scale = ai_dec_scale_of(d1); }
    else {
        int s1 = ai_dec_scale_of(d1), s2 = ai_dec_scale_of(d2), shift;
        if (s2 > s1) { x = ai_big_of(d1); y = ai_big_of(d2); scale = s2; shift = s2 - s1; }
        else { x = ai_big_of(d2); y = ai_big_of(d1); scale = s1; shift = s1 - s2; if (sign) basis ^= AI_DEC_SIGN; }
        if (ai_big_zero(&x)) return ai_dec_pack(&y, scale, basis ^ (sign ? AI_DEC_SIGN : 0u));
        ai_big_mul_pow10(&x, shift);
    }
    if (!sign) { v = x; ai_big_add(&v, &y); }
    else if (ai_big_cmp(&x, &y) >= 0) { v = x; ai_big_sub(&v, &y); }
    else { v = y; ai_big_sub(&v, &x); basis ^= AI_DEC_SIGN; }
    if (!ai_big_fits96(&v)) scale = ai_dec_scale_result(&v, scale);
    return ai_dec_pack(&v, scale, basis);
}
static ai_dec ai_dec_add(ai_dec a, ai_dec b) { return ai_dec_addsub(a, b, 0); }
static ai_dec ai_dec_sub(ai_dec a, ai_dec b) { return ai_dec_addsub(a, b, 1); }
static ai_dec ai_dec_mul(ai_dec a, ai_dec b) {
    int scale = ai_dec_scale_of(a) + ai_dec_scale_of(b);
    uint32_t sign = (a.flags ^ b.flags) & AI_DEC_SIGN;
    ai_dec z = {0u, 0u, 0u, 0u};
    if ((a.hi | a.mid) == 0 && (b.hi | b.mid) == 0) {
        uint64_t p = (uint64_t)a.lo * b.lo;
        if (scale > 28) {
            if (scale > 47) return z;
            uint64_t power = ai_dp64[scale - 28];
            uint64_t q = p / power, rem = p % power;
            power >>= 1;
            if (rem >= power && (rem > power || (q & 1u))) q++;
            p = q; scale = 28;
        }
        ai_dec r; r.lo = (uint32_t)p; r.mid = (uint32_t)(p >> 32); r.hi = 0; r.flags = sign | ((uint32_t)scale << 16);
        return r;
    }
    ai_big x = ai_big_of(a), y = ai_big_of(b), p;
    memset(&p, 0, sizeof p);
    for (int i = 0; i < 3; ++i) {
        uint64_t c = 0;
        for (int j = 0; j < 3; ++j) { uint64_t t = (uint64_t)x.w[i] * y.w[j] + p.w[i + j] + c; p.w[i + j] = (uint32_t)t; c = t >> 32; }
        p.w[i + 3] = (uint32_t)c;
    }
    if (ai_big_zero(&p)) return z;
    if (!ai_big_fits96(&p) || scale > 28) scale = ai_dec_scale_result(&p, scale);
    return ai_dec_pack(&p, scale, sign);
}
static int ai_dec_search_scale(const ai_big *q, int scale) {
    int limit = 9, k = 0;
    if (scale > 19) limit = 28 - scale;
    for (int t = 1; t <= limit; ++t) { ai_big x = *q; ai_big_mul_pow10(&x, t); if (!ai_big_fits96(&x)) break; k = t; }
    if (k != 9 && k + scale < 0) ai_dec_ovf();
    return k;
}
/* q holds the wrapped 96-bit value of a quotient that overflowed 96 bits by exactly 2^96. */
static int ai_dec_overflow_unscale(ai_big *q, int scale, int sticky) {
    if (--scale < 0) ai_dec_ovf();
    q->w[3] = 1u;
    uint32_t rem = ai_big_div_small(q, 10u);
    if (rem > 5 || (rem == 5 && (sticky || (q->w[0] & 1u)))) ai_big_add_small(q, 1u);
    return scale;
}
static ai_dec ai_dec_div(ai_dec a, ai_dec b) {
    ai_big n = ai_big_of(a), d = ai_big_of(b), q, r;
    if (ai_big_zero(&d)) ai_fault("System.DivideByZeroException");
    int scale = (int)(int8_t)(uint8_t)(((a.flags - b.flags) >> 16) & 0xFFu);
    uint32_t sign = (a.flags ^ b.flags) & AI_DEC_SIGN;
    int unscale = 0;
    ai_big_divmod(&n, &d, &q, &r);
    for (;;) {
        int cur;
        if (ai_big_zero(&r)) {
            if (scale >= 0) break;
            cur = -scale < 9 ? -scale : 9;
        } else {
            unscale = 1;
            if (scale == 28 || (cur = ai_dec_search_scale(&q, scale)) == 0) {
                ai_big r2 = r; ai_big_shl1(&r2);
                int c = ai_big_cmp(&r2, &d);
                if (c > 0 || (c == 0 && (q.w[0] & 1u))) {
                    ai_big_add_small(&q, 1u);
                    if (!ai_big_fits96(&q)) { q.w[3] = 0; scale = ai_dec_overflow_unscale(&q, scale, 1); }
                }
                break;
            }
        }
        scale += cur;
        ai_big_mul_small(&q, ai_dp32[cur]);
        if (!ai_big_fits96(&q)) ai_dec_ovf();
        ai_big rp = r, dv, nr;
        ai_big_mul_small(&rp, ai_dp32[cur]);
        ai_big_divmod(&rp, &d, &dv, &nr);
        r = nr;
        ai_big_add(&q, &dv);
        if (!ai_big_fits96(&q)) { q.w[3] = 0; scale = ai_dec_overflow_unscale(&q, scale, !ai_big_zero(&r)); break; }
    }
    if (unscale) {
        for (;;) {
            if (scale <= 0) break;
            ai_big t = q; uint32_t rem = ai_big_div_small(&t, 10u);
            if (rem != 0) break;
            q = t; --scale;
        }
    }
    return ai_dec_pack(&q, scale, sign);
}
/* The remainder follows System.Decimal's VarDecMod/VarDecModFull step for step, including its fixed-width long-division kernels. */
static uint32_t ai_div96by64(uint32_t *n, uint64_t den) {
    uint64_t num; uint32_t num2 = n[2], quo, den_hi = (uint32_t)(den >> 32);
    if (num2 == 0) {
        num = n[0] | ((uint64_t)n[1] << 32);
        if (num < den) return 0;
        uint64_t q = num / den, r = num % den;
        n[0] = (uint32_t)r; n[1] = (uint32_t)(r >> 32);
        return (uint32_t)q;
    }
    if (num2 >= den_hi) {
        num = n[0] | ((uint64_t)n[1] << 32);
        num -= den << 32; quo = 0;
        do { quo--; num += den; } while (num >= den);
        n[0] = (uint32_t)num; n[1] = (uint32_t)(num >> 32);
        return quo;
    }
    uint64_t num64 = n[1] | ((uint64_t)n[2] << 32);
    if (num64 < den_hi) return 0;
    quo = (uint32_t)(num64 / den_hi);
    num = n[0] | ((num64 - (uint64_t)quo * den_hi) << 32);
    uint64_t prod = (uint64_t)quo * (uint32_t)den;
    num -= prod;
    if (num > ~prod) { do { quo--; num += den; } while (num >= den); }
    n[0] = (uint32_t)num; n[1] = (uint32_t)(num >> 32);
    return quo;
}
static uint32_t ai_div128by96(uint32_t *n, const uint32_t *dn) {
    uint64_t dividend = n[2] | ((uint64_t)n[3] << 32);
    uint32_t den = dn[2];
    if (dividend < den) return 0;
    uint32_t quo = (uint32_t)(dividend / den);
    uint32_t remainder = (uint32_t)dividend - quo * den;
    uint64_t prod1 = (uint64_t)quo * dn[0], prod2 = (uint64_t)quo * dn[1];
    prod2 += prod1 >> 32;
    prod1 = (uint32_t)prod1 | (prod2 << 32);
    prod2 >>= 32;
    uint64_t num = n[0] | ((uint64_t)n[1] << 32);
    int pos = 0;
    num -= prod1;
    remainder -= (uint32_t)prod2;
    if (num > ~prod1) { remainder--; if (remainder < ~(uint32_t)prod2) pos = 1; }
    else if (remainder <= ~(uint32_t)prod2) pos = 1;
    if (!pos) {
        prod1 = dn[0] | ((uint64_t)dn[1] << 32);
        for (;;) {
            quo--; num += prod1; remainder += den;
            if (num < prod1) { if (remainder++ < den) break; }
            if (remainder < den) break;
        }
    }
    n[0] = (uint32_t)num; n[1] = (uint32_t)(num >> 32); n[2] = remainder;
    return quo;
}
static int ai_clz32(uint32_t v) { int n = 0; while (n < 32 && !(v & 0x80000000u)) { v <<= 1; ++n; } return n; }
static void ai_dec_mod_full(uint32_t *d1, const uint32_t *d2, int scale) {
    uint32_t tmp = d2[2]; if (tmp == 0) tmp = d2[1];
    int shift = ai_clz32(tmp);
    uint32_t b[8]; memset(b, 0, sizeof b);
    uint64_t low = (d1[0] | ((uint64_t)d1[1] << 32)) << shift;
    uint64_t mid = (d1[1] + ((uint64_t)d1[2] << 32)) >> (32 - shift);
    b[0] = (uint32_t)low; b[1] = (uint32_t)(low >> 32); b[2] = (uint32_t)mid; b[3] = (uint32_t)(mid >> 32);
    uint32_t high = 3;
    while (scale < 0) {
        uint32_t power = scale <= -9 ? 1000000000u : ai_dp32[-scale];
        uint64_t t = (uint64_t)b[0] * power;
        b[0] = (uint32_t)t;
        for (uint32_t i = 1; i <= high; ++i) { t >>= 32; t += (uint64_t)b[i] * power; b[i] = (uint32_t)t; }
        if (t > 2147483647ull) b[++high] = (uint32_t)(t >> 32);
        scale += 9;
    }
    if (d2[2] == 0) {
        uint64_t divisor = (d2[0] | ((uint64_t)d2[1] << 32)) << shift;
        switch (high) {
            case 6: ai_div96by64(&b[4], divisor); /* fallthrough */
            case 5: ai_div96by64(&b[3], divisor); /* fallthrough */
            case 4: ai_div96by64(&b[2], divisor); break;
            default: break;
        }
        ai_div96by64(&b[1], divisor);
        ai_div96by64(&b[0], divisor);
        uint64_t r = (b[0] | ((uint64_t)b[1] << 32)) >> shift;
        d1[0] = (uint32_t)r; d1[1] = (uint32_t)(r >> 32); d1[2] = 0;
    } else {
        uint32_t dv[3];
        uint64_t dl = (d2[0] | ((uint64_t)d2[1] << 32)) << shift;
        dv[0] = (uint32_t)dl; dv[1] = (uint32_t)(dl >> 32);
        dv[2] = (uint32_t)((d2[1] + ((uint64_t)d2[2] << 32)) >> (32 - shift));
        switch (high) {
            case 6: ai_div128by96(&b[3], dv); /* fallthrough */
            case 5: ai_div128by96(&b[2], dv); /* fallthrough */
            case 4: ai_div128by96(&b[1], dv); break;
            default: break;
        }
        ai_div128by96(&b[0], dv);
        uint64_t r = ((b[0] | ((uint64_t)b[1] << 32)) >> shift) + (((uint64_t)b[2] << (32 - shift)) << 32);
        d1[0] = (uint32_t)r; d1[1] = (uint32_t)(r >> 32); d1[2] = b[2] >> shift;
    }
}
static ai_dec ai_dec_mod(ai_dec a, ai_dec b) {
    ai_big m1 = ai_big_of(a), m2 = ai_big_of(b);
    if (ai_big_zero(&m2)) ai_fault("System.DivideByZeroException");
    if (ai_big_zero(&m1)) return a;
    int s1 = ai_dec_scale_of(a), s2 = ai_dec_scale_of(b), e = s1 > s2 ? s1 : s2;
    uint32_t sign = a.flags & AI_DEC_SIGN, d2flags = (b.flags & ~AI_DEC_SIGN) | sign;
    ai_big x = m1, y = m2;
    ai_big_mul_pow10(&x, e - s1); ai_big_mul_pow10(&y, e - s2);
    int c = ai_big_cmp(&x, &y);
    if (c < 0) return a;
    if (c == 0) { ai_big z; memset(&z, 0, sizeof z); return ai_dec_pack(&z, e, sign); }
    int scale = s1 - s2;
    uint32_t d1[3] = {a.lo, a.mid, a.hi}, d2[3] = {b.lo, b.mid, b.hi}, flags1 = a.flags;
    if (scale > 0) { ai_big_mul_pow10(&m2, scale); d2[0] = m2.w[0]; d2[1] = m2.w[1]; d2[2] = m2.w[2]; scale = 0; }
    do {
        if (scale < 0) {
            flags1 = d2flags;
            ai_big q; memset(&q, 0, sizeof q); q.w[0] = d1[0]; q.w[1] = d1[1]; q.w[2] = d1[2];
            do {
                int cs = ai_dec_search_scale(&q, 28 + scale);
                if (cs == 0) break;
                uint32_t power = cs >= 9 ? 1000000000u : ai_dp32[cs];
                scale += cs;
                ai_big_mul_small(&q, power);
                if (power != 1000000000u) break;
            } while (scale < 0);
            d1[0] = q.w[0]; d1[1] = q.w[1]; d1[2] = q.w[2];
        }
        if (d1[2] == 0) {
            uint64_t n64 = d1[0] | ((uint64_t)d1[1] << 32), dd = d2[0] | ((uint64_t)d2[1] << 32);
            n64 %= dd; d1[0] = (uint32_t)n64; d1[1] = (uint32_t)(n64 >> 32);
            break;
        } else if ((d2[2] | d2[1]) == 0) {
            uint32_t den = d2[0];
            uint64_t t = ((uint64_t)d1[2] << 32) | d1[1];
            t = ((t % den) << 32) | d1[0];
            t %= den; d1[0] = (uint32_t)t; d1[1] = (uint32_t)(t >> 32); d1[2] = 0;
        } else {
            ai_dec_mod_full(d1, d2, scale);
            break;
        }
    } while (scale < 0);
    ai_dec r; r.lo = d1[0]; r.mid = d1[1]; r.hi = d1[2]; r.flags = flags1;
    return r;
}
static int ai_dec_cmp(ai_dec a, ai_dec b) {
    ai_big x = ai_big_of(a), y = ai_big_of(b);
    int za = ai_big_zero(&x), zb = ai_big_zero(&y);
    if (za && zb) return 0;
    int na = !za && (a.flags & AI_DEC_SIGN) != 0, nb = !zb && (b.flags & AI_DEC_SIGN) != 0;
    if (na != nb) return na ? -1 : 1;
    int sa = ai_dec_scale_of(a), sb = ai_dec_scale_of(b);
    if (sa < sb) ai_big_mul_pow10(&x, sb - sa); else if (sb < sa) ai_big_mul_pow10(&y, sa - sb);
    int c = ai_big_cmp(&x, &y);
    return na ? -c : c;
}
static ai_dec ai_dec_from_u64(uint64_t v) { ai_dec d; d.lo = (uint32_t)v; d.mid = (uint32_t)(v >> 32); d.hi = 0; d.flags = 0; return d; }
static ai_dec ai_dec_from_i64(int64_t v) { if (v >= 0) return ai_dec_from_u64((uint64_t)v); ai_dec d = ai_dec_from_u64(0ull - (uint64_t)v); d.flags = AI_DEC_SIGN; return d; }
/* Truncates toward zero and returns the magnitude; faults when it needs more than 64 bits. */
static uint64_t ai_dec_trunc_mag(ai_dec d, int *neg) {
    ai_big m = ai_big_of(d); int s = ai_dec_scale_of(d);
    while (s >= 9) { ai_big_div_small(&m, 1000000000u); s -= 9; }
    if (s > 0) ai_big_div_small(&m, ai_dp32[s]);
    for (int i = 2; i < 8; ++i) if (m.w[i]) ai_dec_ovf();
    *neg = (d.flags & AI_DEC_SIGN) != 0;
    return ((uint64_t)m.w[1] << 32) | m.w[0];
}
static int64_t ai_dec_to_si(ai_dec d, int64_t lo, int64_t hi) {
    int neg; uint64_t mag = ai_dec_trunc_mag(d, &neg); int64_t v;
    if (neg) { if (mag > 9223372036854775808ull) ai_dec_ovf(); v = (int64_t)(0ull - mag); }
    else { if (mag > 9223372036854775807ull) ai_dec_ovf(); v = (int64_t)mag; }
    if (v < lo || v > hi) ai_dec_ovf();
    return v;
}
static uint64_t ai_dec_to_ui(ai_dec d, uint64_t hi) {
    int neg; uint64_t mag = ai_dec_trunc_mag(d, &neg);
    if ((neg && mag != 0) || mag > hi) ai_dec_ovf();
    return mag;
}
static double ai_dec_u2d(uint64_t v) { return v <= 9223372036854775807ull ? (double)(int64_t)v : (double)(int64_t)((v >> 1) | (v & 1u)) * 2.0; }
static double ai_dec_to_f64(ai_dec d) {
    double dbl = (ai_dec_u2d(((uint64_t)d.mid << 32) | d.lo) + (double)d.hi * 18446744073709551616.0) / ai_dec_pd(ai_dec_scale_of(d));
    if (d.flags & AI_DEC_SIGN) dbl = -dbl;
    return dbl;
}
static float ai_dec_to_f32(ai_dec d) { return (float)ai_dec_to_f64(d); }
static ai_dec ai_dec_from_scaled(uint64_t mant, int power, uint32_t flags, int maxlmax, int single) {
    ai_dec r = {0u, 0u, 0u, 0u};
    if (power < 0) {
        ai_big m = ai_big_u64(mant); ai_big_mul_pow10(&m, -power);
        r = ai_dec_pack(&m, 0, flags);
        return r;
    }
    int lmax = power > maxlmax ? maxlmax : power;
    if (!single && lmax >= 8 && mant % 100000000ull == 0) { mant /= 100000000ull; power -= 8; lmax -= 8; }
    if (lmax >= 4 && mant % 10000ull == 0) { mant /= 10000ull; power -= 4; lmax -= 4; }
    if (lmax >= 2 && mant % 100ull == 0) { mant /= 100ull; power -= 2; lmax -= 2; }
    if (lmax >= 1 && mant % 10ull == 0) { mant /= 10ull; power--; }
    r.lo = (uint32_t)mant; r.mid = (uint32_t)(mant >> 32); r.flags = flags | ((uint32_t)power << 16);
    return r;
}
static ai_dec ai_dec_from_f64(double input) {
    ai_dec z = {0u, 0u, 0u, 0u};
    uint64_t bits; memcpy(&bits, &input, sizeof bits);
    int exp = (int)((bits >> 52) & 0x7FFu) - 1022;
    if (exp < -94) return z;
    if (exp > 96) ai_dec_ovf();
    uint32_t flags = 0;
    if (input < 0) { input = -input; flags = AI_DEC_SIGN; }
    double dbl = input;
    int power = 14 - ((exp * 19728) >> 16);
    if (power >= 0) { if (power > 28) power = 28; dbl *= ai_dec_pd(power); }
    else { if (power != -1 || dbl >= 1e15) dbl /= ai_dec_pd(-power); else power = 0; }
    if (dbl < 1e14 && power < 28) { dbl *= 10; power++; }
    uint64_t mant = (uint64_t)(int64_t)nearbyint(dbl);
    if (mant == 0) return z;
    return ai_dec_from_scaled(mant, power, flags, 14, 0);
}
static ai_dec ai_dec_from_f32(float input) {
    ai_dec z = {0u, 0u, 0u, 0u};
    uint32_t bits; memcpy(&bits, &input, sizeof bits);
    int exp = (int)((bits >> 23) & 0xFFu) - 126;
    if (exp < -94) return z;
    if (exp > 96) ai_dec_ovf();
    uint32_t flags = 0;
    if (input < 0) { input = -input; flags = AI_DEC_SIGN; }
    double dbl = input;
    int power = 6 - ((exp * 19728) >> 16);
    if (power >= 0) { if (power > 28) power = 28; dbl *= ai_dec_pd(power); }
    else { if (power != -1 || dbl >= 1e7) dbl /= ai_dec_pd(-power); else power = 0; }
    if (dbl < 1e6 && power < 28) { dbl *= 10; power++; }
    uint64_t mant = (uint64_t)(uint32_t)(int32_t)nearbyint(dbl);
    if (mant == 0) return z;
    return ai_dec_from_scaled(mant, power, flags, 6, 1);
}
/* Invariant-culture text that preserves scale; out needs 48 bytes. Zero never prints a sign. */
static void ai_dec_format(ai_dec d, char *out) {
    ai_big m = ai_big_of(d); char rev[40]; int n = 0, s = ai_dec_scale_of(d), p = 0;
    int zero = ai_big_zero(&m);
    while (!ai_big_zero(&m)) rev[n++] = (char)('0' + ai_big_div_small(&m, 10u));
    while (n < s + 1) rev[n++] = '0';
    if ((d.flags & AI_DEC_SIGN) && !zero) out[p++] = '-';
    for (int i = n - 1; i >= 0; --i) { out[p++] = rev[i]; if (i == s && s > 0) out[p++] = '.'; }
    out[p] = 0;
}
""";
}
