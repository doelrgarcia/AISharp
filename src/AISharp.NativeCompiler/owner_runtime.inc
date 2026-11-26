R"AI_OWNER(
/* Private backend ABI. Only verified generated ownership operations may use it.
   Raw C copies/allocator callbacks are not a safe AI# API or a borrow checker. */
typedef enum ai_owner_status {
    AI_OWNER_OK, AI_OWNER_INVALID, AI_OWNER_CAPACITY, AI_OWNER_NO_MEMORY, AI_OWNER_BOUNDS
} ai_owner_status;
typedef void* (*ai_owner_allocate_fn)(void*, size_t);
typedef void (*ai_owner_release_fn)(void*, void*);
typedef struct ai_owner_allocator {
    ai_owner_allocate_fn allocate;
    ai_owner_release_fn release;
    void* context;
} ai_owner_allocator;
typedef struct ai_owner {
    unsigned char* data;
    size_t length;
    bool active;
    ai_owner_allocator allocator;
    uint64_t evidence_id;
} ai_owner;
#define AI_OWNER_MAX_BYTES ((size_t)64 * 1024 * 1024)
ai_owner ai_owner_empty(void) { ai_owner value = {0}; return value; }
void* ai_owner_system_allocate(void* context, size_t size) { (void)context; return malloc(size); }
void ai_owner_system_release(void* context, void* data) { (void)context; free(data); }
ai_owner_allocator ai_owner_system_allocator(void) {
    ai_owner_allocator value = {ai_owner_system_allocate, ai_owner_system_release, NULL}; return value;
}
ai_owner_status ai_owner_create(ai_owner* destination, size_t length, ai_owner_allocator allocator) {
    unsigned char* data;
    if (!destination || destination->active || !allocator.allocate || !allocator.release) return AI_OWNER_INVALID;
    if (length > AI_OWNER_MAX_BYTES) return AI_OWNER_CAPACITY;
    ai_mem_init();
    if(length)ai_mem_add(&ai_mem_attempts,1);
    data = length && !(ai_mem_enabled && ai_mem_fail_at && ai_mem_attempts==ai_mem_fail_at) ? (unsigned char*)allocator.allocate(allocator.context, length) : NULL;
    if (length && !data) {ai_mem_add(&ai_mem_failures,1);return AI_OWNER_NO_MEMORY;}
    if (length) memset(data, 0, length);
    destination->data = data; destination->length = length;
    destination->allocator = allocator; destination->active = true;
    ai_mem_add(&ai_mem_creates,1);destination->evidence_id=ai_mem_creates;
    if(length){ai_mem_add(&ai_mem_allocations,1);ai_mem_add(&ai_mem_live_bytes,(uint64_t)length);}
    return AI_OWNER_OK;
}
ai_owner_status ai_owner_move(ai_owner* destination, ai_owner* source) {
    if (!destination || !source || destination == source || destination->active || !source->active) return AI_OWNER_INVALID;
    *destination = *source; *source = ai_owner_empty(); return AI_OWNER_OK;
}
/* Lexically scoped stack storage supplied by verified stackalloc lowering. */
void ai_owner_stack_release(void* context, void* data) {(void)context;(void)data;}
void* ai_owner_stack_allocate(void* context, size_t size) {(void)size;return context;}
ai_owner ai_owner_stack(unsigned char* data, size_t length) {
    ai_owner value = ai_owner_empty();
    value.data = data; value.length = length; value.active = true;
    value.allocator.allocate = ai_owner_stack_allocate;
    value.allocator.release = ai_owner_stack_release;
    value.allocator.context = data;
    return value;
}
/* Conditional cleanup of inactive/moved storage is intentionally a no-op.
   Explicit invalid source operations still require frontend diagnostics. */
void ai_owner_drop(ai_owner* owner) {
    ai_owner previous;
    if (!owner || !owner->active) return;
    previous = *owner; *owner = ai_owner_empty();
    if (previous.allocator.release == ai_owner_stack_release) return;
    ai_mem_add(&ai_mem_drops,1);
    if(ai_mem_trace_count<256)ai_mem_trace[ai_mem_trace_count++]=previous.evidence_id;else ai_mem_trace_truncated=true;
    if (previous.data) {
        ai_mem_add(&ai_mem_releases,1);
        if(ai_mem_live_bytes<(uint64_t)previous.length)ai_mem_overflow=true;else ai_mem_live_bytes-=(uint64_t)previous.length;
        previous.allocator.release(previous.allocator.context, previous.data);
    }
}
ai_owner_status ai_owner_read(const ai_owner* owner, size_t index, unsigned char* result) {
    if (!owner || !owner->active || !result) return AI_OWNER_INVALID;
    if (index >= owner->length) return AI_OWNER_BOUNDS;
    *result = owner->data[index]; return AI_OWNER_OK;
}
ai_owner_status ai_owner_write(ai_owner* owner, size_t index, unsigned char value) {
    if (!owner || !owner->active) return AI_OWNER_INVALID;
    if (index >= owner->length) return AI_OWNER_BOUNDS;
    owner->data[index] = value; return AI_OWNER_OK;
}
void ai_owner_check(ai_owner_status status) {
    if(status==AI_OWNER_OK)return;
    ai_fail(status==AI_OWNER_NO_MEMORY ? "System.OutOfMemoryException" : status==AI_OWNER_BOUNDS ? "System.IndexOutOfRangeException" : status==AI_OWNER_CAPACITY ? "AISharp.AllocationLimitException" : "AISharp.InvalidOwnerException");
}
ai_owner ai_buffer_create(int32_t length) {
    ai_owner owner=ai_owner_empty();
    if(length<0)ai_fail("System.ArgumentOutOfRangeException");
    ai_owner_check(ai_owner_create(&owner,(size_t)length,ai_owner_system_allocator()));return owner;
}
ai_owner ai_buffer_set(ai_owner owner,int32_t index,int32_t value) {
    if(value<0 || value>255)ai_fail("System.ArgumentOutOfRangeException");
    if(index<0)ai_fail("System.IndexOutOfRangeException");
    ai_owner_check(ai_owner_write(&owner,(size_t)index,(unsigned char)value));return owner;
}
int32_t ai_buffer_get(ai_owner owner,int32_t index) {
    unsigned char value=0;
    if(index<0)ai_fail("System.IndexOutOfRangeException");
    ai_owner_check(ai_owner_read(&owner,(size_t)index,&value));ai_owner_drop(&owner);return (int32_t)value;
}
int32_t ai_buffer_length(ai_owner owner) {
    int32_t length;
    if(!owner.active)ai_fail("AISharp.InvalidOwnerException");
    length=(int32_t)owner.length;ai_owner_drop(&owner);return length;
}
int32_t ai_buffer_size(const ai_owner* owner) {
    if(!owner || !owner->active)ai_fail("AISharp.InvalidOwnerException");
    return (int32_t)owner->length;
}
int32_t ai_buffer_peek(const ai_owner* owner,int32_t index) {
    unsigned char value=0;if(index<0)ai_fail("System.IndexOutOfRangeException");
    ai_owner_check(ai_owner_read(owner,(size_t)index,&value));return (int32_t)value;
}
void ai_buffer_write(ai_owner* owner,int32_t index,int32_t value) {
    if(value<0 || value>255)ai_fail("System.ArgumentOutOfRangeException");
    if(index<0)ai_fail("System.IndexOutOfRangeException");
    ai_owner_check(ai_owner_write(owner,(size_t)index,(unsigned char)value));
}
)AI_OWNER"
