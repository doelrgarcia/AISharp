/* This C-compatible kernel is compiled into the host for constant folding
   and rendered into generated C/C++ for runtime arithmetic. */
AI_INT64_CODE(
typedef enum {
    ai_i64_add, ai_i64_sub, ai_i64_mul, ai_i64_div, ai_i64_rem,
    ai_i64_and, ai_i64_or, ai_i64_xor, ai_i64_shl, ai_i64_shr, ai_i64_ushr
} ai_i64_operation;
)
AI_INT64_CODE(
typedef enum { ai_i64_ok, ai_i64_overflow, ai_i64_zero, ai_i64_invalid } ai_i64_error;
typedef struct { int64_t value; ai_i64_error error; } ai_i64_result;
)
AI_INT64_CODE(
static ai_i64_result ai_i64_make(int64_t value, ai_i64_error error) {
    ai_i64_result result = { value, error };
    return result;
}
)
AI_INT64_CODE(
static int64_t ai_i64_bits(uint64_t value) {
    if(value <= (uint64_t)INT64_MAX)return (int64_t)value;
    return INT64_MIN + (int64_t)(value - ((uint64_t)INT64_MAX + 1));
}
)
AI_INT64_CODE(
static ai_i64_result ai_i64_binary(ai_i64_operation operation, int64_t a, int64_t b, bool wraps) {
    uint64_t x = (uint64_t)a, y = (uint64_t)b;
    uint64_t bits = 0;
    switch(operation) {
    case ai_i64_add:
        if(!wraps && ((b > 0 && a > INT64_MAX - b) || (b < 0 && a < INT64_MIN - b)))
            return ai_i64_make(0, ai_i64_overflow);
        bits = x + y;
        break;
    case ai_i64_sub:
        if(!wraps && ((b > 0 && a < INT64_MIN + b) || (b < 0 && a > INT64_MAX + b)))
            return ai_i64_make(0, ai_i64_overflow);
        bits = x - y;
        break;
    case ai_i64_mul:
        if(!wraps) {
            uint64_t ax = a < 0 ? (uint64_t)0 - x : x;
            uint64_t by = b < 0 ? (uint64_t)0 - y : y;
            uint64_t limit = (uint64_t)INT64_MAX + ((a < 0) != (b < 0) ? 1u : 0u);
            if(ax && by > limit / ax)return ai_i64_make(0, ai_i64_overflow);
        }
        bits = x * y;
        break;
    case ai_i64_div:
    case ai_i64_rem:
        if(!b)return ai_i64_make(0, ai_i64_zero);
        if(a == INT64_MIN && b == -1)
            return ai_i64_make(0, ai_i64_overflow);
        return ai_i64_make(operation == ai_i64_div ? a / b : a % b, ai_i64_ok);
    case ai_i64_and: bits = x & y; break;
    case ai_i64_or: bits = x | y; break;
    case ai_i64_xor: bits = x ^ y; break;
    case ai_i64_shl:
    case ai_i64_shr:
    case ai_i64_ushr: {
        unsigned count = (unsigned)(y & 63u);
        bits = operation == ai_i64_shl ? x << count : x >> count;
        if(operation == ai_i64_shr && a < 0 && count)bits |= UINT64_MAX << (64u - count);
        break;
    }
    default: return ai_i64_make(0, ai_i64_invalid);
    }
    return ai_i64_make(ai_i64_bits(bits), ai_i64_ok);
}
)
