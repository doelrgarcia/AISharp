/* Shared by constant evaluation and generated native code. The range
   comes from a verified 8- or 16-bit destination descriptor. */
AI_NARROW_CODE(
static int32_t ai_small_wrap(int64_t value, int32_t minimum, int32_t maximum) {
    uint32_t range = (uint32_t)(maximum - minimum) + 1u;
    int32_t bits = (int32_t)((uint32_t)value & (range - 1u));
    return bits > maximum ? bits - (int32_t)range : bits;
}
)
