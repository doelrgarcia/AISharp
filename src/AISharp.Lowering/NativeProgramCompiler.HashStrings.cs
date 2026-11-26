namespace AISharp.Lowering;

public static partial class NativeProgramCompiler
{
    private sealed partial class Emitter
    {
        private bool usesHashSeed;
        private void HashSeed()
        {
            usesHashSeed=true;
            Helper("ai_hash_01_seed","static uint64_t ai_hash_key0, ai_hash_key1;\nstatic void ai_hash_seed_init(void) { unsigned int words[4]; for (int i = 0; i < 4; ++i) if (rand_s(&words[i]) != 0) ai_fault(\"System.InvalidOperationException\"); ai_hash_key0 = (uint64_t)words[0] | ((uint64_t)words[1] << 32); ai_hash_key1 = (uint64_t)words[2] | ((uint64_t)words[3] << 32); }\n");
        }
        private void HashSip()
        {
            HashSeed();
            Helper("ai_hash_02_sip",HashSipRuntime);
        }
        private string EmitStringHash(string value)
        {
            HashSip();
            return Helper("ai_hash_string","static int32_t ai_hash_string(ai_str value) { if (value.p == NULL) return INT32_C(0); uint64_t h = ai_hash_bytes((const uint8_t *)value.p, (size_t)value.n * sizeof(uint16_t)); return ai_hash_00_bits((uint32_t)h ^ (uint32_t)(h >> 32)); }\n")+"("+value+")";
        }
        private const string HashSipRuntime="""
static uint64_t ai_hash_rotate(uint64_t value, unsigned int count) { return (value << count) | (value >> (64u - count)); }
static void ai_hash_round(uint64_t *a, uint64_t *b, uint64_t *c, uint64_t *d) {
    *a += *b; *b = ai_hash_rotate(*b, 13u); *b ^= *a; *a = ai_hash_rotate(*a, 32u);
    *c += *d; *d = ai_hash_rotate(*d, 16u); *d ^= *c;
    *a += *d; *d = ai_hash_rotate(*d, 21u); *d ^= *a;
    *c += *b; *b = ai_hash_rotate(*b, 17u); *b ^= *c; *c = ai_hash_rotate(*c, 32u);
}
static uint64_t ai_hash_bytes(const uint8_t *data, size_t length) {
    uint64_t a = UINT64_C(0x736f6d6570736575) ^ ai_hash_key0;
    uint64_t b = UINT64_C(0x646f72616e646f6d) ^ ai_hash_key1;
    uint64_t c = UINT64_C(0x6c7967656e657261) ^ ai_hash_key0;
    uint64_t d = UINT64_C(0x7465646279746573) ^ ai_hash_key1;
    size_t i = 0;
    for (; length - i >= 8u; i += 8u) {
        uint64_t word = 0;
        for (unsigned int j = 0; j < 8u; ++j) word |= (uint64_t)data[i + j] << (j * 8u);
        d ^= word;
        ai_hash_round(&a, &b, &c, &d); ai_hash_round(&a, &b, &c, &d);
        a ^= word;
    }
    uint64_t tail = (uint64_t)length << 56;
    for (unsigned int j = 0; i + j < length; ++j) tail |= (uint64_t)data[i + j] << (j * 8u);
    d ^= tail;
    ai_hash_round(&a, &b, &c, &d); ai_hash_round(&a, &b, &c, &d);
    a ^= tail; c ^= UINT64_C(0xff);
    for (int round = 0; round < 4; ++round) ai_hash_round(&a, &b, &c, &d);
    return a ^ b ^ c ^ d;
}

""";
    }
}
