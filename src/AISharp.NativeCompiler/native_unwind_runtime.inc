R"AI_UNWIND(
/* Private bootstrap transport. Language owners come only from verified IR. */
struct ai_native_fault { const char* kind; ai_cause cause; bool origin_pending; };
struct ai_unwind_signal {};
struct ai_unwind_pending {bool active;ai_native_fault fault;};
static thread_local ai_unwind_pending ai_pending={};
struct ai_unwind_scope;
struct ai_unwind_node { ai_unwind_node *previous,*next; ai_unwind_scope* scope; void* storage; void (*drop)(void*); };
static thread_local ai_unwind_scope* ai_scope_top = nullptr;
struct ai_unwind_scope {
    ai_unwind_scope* parent; ai_unwind_node* last; ai_failure_frame site; bool entered,operation;
    explicit ai_unwind_scope(ai_failure_frame declaration) noexcept : parent(ai_scope_top),last(nullptr),site(parent ? parent->site : declaration),entered(false),operation(false) { ai_scope_top=this; }
    ai_unwind_scope(const ai_unwind_scope&)=delete;
    ai_unwind_scope& operator=(const ai_unwind_scope&)=delete;
    void remove(ai_unwind_node& node) noexcept {
        if(!node.scope)return;
        if(node.previous)node.previous->next=node.next;
        if(node.next)node.next->previous=node.previous;else node.scope->last=node.previous;
        node.scope=nullptr;node.previous=nullptr;node.next=nullptr;
    }
    void track(ai_unwind_node& node,void* storage,void (*drop)(void*)) noexcept {
        remove(node);node.scope=this;node.storage=storage;node.drop=drop;node.previous=last;node.next=nullptr;
        if(last)last->next=&node;last=&node;
    }
    void at(ai_failure_frame frame) noexcept {site=frame;operation=true;}
    bool enter() noexcept {entered=true;return ++ai_depth>AISHARP_RECURSION_LIMIT;}
    void propagate(ai_native_fault& fault) const noexcept {
        if(!operation)return;
        if(fault.origin_pending){fault.origin_pending=false;return;}
        ai_cause_push(&fault.cause,site);
    }
    ~ai_unwind_scope() noexcept {
        if(ai_pending.active)propagate(ai_pending.fault);
        while(last){auto* node=last;remove(*node);node->drop(node->storage);}
        if(entered)--ai_depth;ai_scope_top=parent;
    }
};
template<class T,void (*Drop)(T*)> void ai_unwind_drop(void* storage) {Drop(static_cast<T*>(storage));}
template<class T> T ai_unwind_take(T& source,T empty) noexcept {T value=source;source=empty;return value;}
bool ai_supported_unwind(const char* kind) {
    return strcmp(kind,"System.OverflowException")==0 || strcmp(kind,"System.DivideByZeroException")==0 || strcmp(kind,"System.IndexOutOfRangeException")==0 || strcmp(kind,"System.ArgumentOutOfRangeException")==0 || strcmp(kind,"System.OutOfMemoryException")==0 || strcmp(kind,"AISharp.AllocationLimitException")==0 || strcmp(kind,"AISharp.RecursionLimitException")==0 || strcmp(kind,"AISharp.CancelledException")==0 || strcmp(kind,"AISharp.DeadlineExceededException")==0;
}
__declspec(noreturn) void ai_fail(const char* kind) {
    if(!ai_scope_top || !ai_supported_unwind(kind))ai_terminal_fail(kind);
    if(ai_pending.active)ai_terminal_fail("AISharp.InvalidUnwindStateException");
    ai_pending.fault={kind,ai_cause_new(ai_scope_top->site),true};ai_pending.active=true;
    throw ai_unwind_signal{};
}

/* Cooperative cancellation and monotonic deadlines for recovery boundaries.
   AISHARP_CLOCK_FAKE pins the clock for deterministic tests. External linkage
   keeps /W4 /WX quiet for unreferenced helpers; NOMINMAX protects user names. */
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifdef _WIN32
#include <windows.h>
#endif
// Parse a non-negative decimal environment value. Used only by the test clock.
static bool ai_env_uint(const char* name,uint64_t* result) {
#ifdef _MSC_VER
    char* text = nullptr; size_t text_len = 0;
    if(_dupenv_s(&text, &text_len, name) != 0 || !text) return false;
#else
    const char* text = getenv(name);
    if(!text || !*text) return false;
    size_t text_len = strlen(text);
#endif
    uint64_t value = 0; bool digits = false;
    for(size_t i=0;i<text_len;++i) {
        char c = text[i];
        if(c < '0' || c > '9') break;
        digits = true;
        if(value > (UINT64_MAX - (uint64_t)(c - '0')) / 10) { value = UINT64_MAX; break; }
        value = value*10 + (uint64_t)(c - '0');
    }
#ifdef _MSC_VER
    free(text);
#endif
    if(!digits) return false;
    *result = value; return true;
}
// A fake clock pins the observation sequence for deterministic acceptance:
// AISHARP_CLOCK_FAKE is the first value and AISHARP_CLOCK_STEP is added on
// every subsequent read. Without a fake the clock is monotonic real time.
static uint64_t ai_clock_fake_base = 0, ai_clock_fake_step = 0, ai_clock_fake_calls = 0;
static bool ai_clock_configured = false, ai_clock_fake = false;
static void ai_clock_init(void) {
    if(ai_clock_configured) return;
    ai_clock_configured = true;
    ai_clock_fake = ai_env_uint("AISHARP_CLOCK_FAKE", &ai_clock_fake_base);
    if(ai_clock_fake) ai_env_uint("AISHARP_CLOCK_STEP", &ai_clock_fake_step);
}
uint64_t ai_now_ms(void) {
    ai_clock_init();
    if(ai_clock_fake) {
        uint64_t value = ai_clock_fake_base;
        if(ai_clock_fake_calls != 0 && ai_clock_fake_step > (UINT64_MAX - ai_clock_fake_base) / ai_clock_fake_calls) value = UINT64_MAX;
        else value = ai_clock_fake_base + ai_clock_fake_calls*ai_clock_fake_step;
        ++ai_clock_fake_calls;
        return value;
    }
#ifdef _WIN32
    static LARGE_INTEGER freq = {0}, base = {0};
    if(!freq.QuadPart) { QueryPerformanceFrequency(&freq); QueryPerformanceCounter(&base); }
    LARGE_INTEGER now; QueryPerformanceCounter(&now);
    return (uint64_t)(((now.QuadPart - base.QuadPart) * 1000) / freq.QuadPart);
#else
    return 0;
#endif
}
static thread_local int ai_cancel_state = 0;
void ai_cancel_request(void) { ai_cancel_state = 1; }
bool ai_cancelled(void) { return ai_cancel_state != 0; }
void ai_check_cancelled(void) { if(ai_cancel_state) ai_fail("AISharp.CancelledException"); }
void ai_check_deadline(uint64_t deadline_ms) { if(ai_now_ms() >= deadline_ms) ai_fail("AISharp.DeadlineExceededException"); }
void ai_sleep_ms(int32_t milliseconds) {
#ifdef _WIN32
    if(milliseconds > 0) Sleep((DWORD)milliseconds);
#else
    (void)milliseconds;
#endif
}
/* Consuming primitives own their incoming buffer even when their body faults. */
ai_owner ai_unwind_buffer_set(ai_owner owner,int32_t index,int32_t value) {
    try{return ai_buffer_set(owner,index,value);}catch(ai_unwind_signal&){ai_owner_drop(&owner);throw;}
}
int32_t ai_unwind_buffer_get(ai_owner owner,int32_t index) {
    try{return ai_buffer_get(owner,index);}catch(ai_unwind_signal&){ai_owner_drop(&owner);throw;}
}
int32_t ai_unwind_buffer_length(ai_owner owner) {
    try{return ai_buffer_length(owner);}catch(ai_unwind_signal&){ai_owner_drop(&owner);throw;}
}
int ai_unwind_report(const ai_native_fault& fault) {
    if(ai_scope_top)ai_terminal_fail("AISharp.InvalidUnwindStateException");
    int reported=printf("{\"schemaVersion\":\"aisharp.native-result/3\",\"outcome\":\"faulted\",\"fault\":\"%s\",\"cause\":",fault.kind);
    int cause=ai_cause_report(&fault.cause),memory=ai_memory_report(),end=fputs("}\n",stdout);
    return reported<0 || cause<0 || memory<0 || end<0 || fflush(stdout)!=0 ? 74 : 70;
}
)AI_UNWIND"
