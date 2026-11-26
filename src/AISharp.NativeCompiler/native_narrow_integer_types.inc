struct NativeNarrowInteger {
    int primitive;
    const char* keyword;
    const char* alias;
    const char* label;
    const char* storage;
    int32_t minimum, maximum;
    char reference_tag;
};
// Keep the existing primitive proof tags 0 through 5 stable.
constexpr NativeNarrowInteger native_narrow_integers[]={
    {6,"byte","u8","uint8","uint8_t",0,255,'u'},
    {7,"sbyte","i8","int8","int8_t",-128,127,'s'},
    {8,"short","i16","int16","int16_t",-32768,32767,'h'},
    {9,"ushort","u16","uint16","uint16_t",0,65535,'t'},
    {10,"char","","char","uint16_t",0,65535,'c'},
};
// Char is primitive 10 and uint is primitive 11; uint is a first-class
// unsigned scalar, not a narrow integer promoted to Int32.
constexpr int native_primitive_limit=21;
const NativeNarrowInteger* native_narrow_integer(int primitive) {
    for(const auto& descriptor:native_narrow_integers)if(descriptor.primitive==primitive)return &descriptor;
    return nullptr;
}
const NativeNarrowInteger* native_narrow_integer(std::string_view spelling) {
    for(const auto& descriptor:native_narrow_integers)
        if(spelling==descriptor.keyword || spelling==descriptor.alias)return &descriptor;
    return nullptr;
}
