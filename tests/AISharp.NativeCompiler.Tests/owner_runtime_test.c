/* Appended to real CLI emit-c output after renaming the AI# main entry.
   These are private runtime ABI tests, not AI# ownership-language tests. */
typedef struct test_allocator_state {
    unsigned allocations, releases;
    bool fail;
    ai_owner* reenter;
} test_allocator_state;
void* test_allocate(void* context, size_t size) {
    test_allocator_state* state = (test_allocator_state*)context;
    ++state->allocations;
    return state->fail ? NULL : malloc(size);
}
void test_release(void* context, void* data) {
    test_allocator_state* state = (test_allocator_state*)context;
    ++state->releases;
    if (state->reenter) ai_owner_drop(state->reenter);
    free(data);
}
#define REQUIRE(condition) do { if (!(condition)) { fprintf(stderr,"runtime assertion at %d\n",__LINE__); return 1; } } while (0)
int main(void) {
    test_allocator_state state = {0};
    ai_owner_allocator allocator = {test_allocate,test_release,&state};
    ai_owner a = ai_owner_empty(), b = ai_owner_empty(), c = ai_owner_empty();
    unsigned char value = 99;
    REQUIRE(ai_owner_create(&a,17,allocator)==AI_OWNER_OK);
    REQUIRE(state.allocations==1 && state.releases==0 && a.active && a.length==17);
    REQUIRE(ai_owner_read(&a,0,&value)==AI_OWNER_OK && value==0);
    REQUIRE(ai_owner_read(&a,16,&value)==AI_OWNER_OK && value==0);
    REQUIRE(ai_owner_write(&a,16,255)==AI_OWNER_OK);
    REQUIRE(ai_owner_move(&b,&a)==AI_OWNER_OK && !a.active && !a.data && a.length==0);
    REQUIRE(ai_owner_read(&a,0,&value)==AI_OWNER_INVALID);
    REQUIRE(ai_owner_write(&a,0,1)==AI_OWNER_INVALID);
    REQUIRE(ai_owner_read(&b,16,&value)==AI_OWNER_OK && value==255);
    REQUIRE(ai_owner_read(&b,17,&value)==AI_OWNER_BOUNDS);
    REQUIRE(ai_owner_write(&b,SIZE_MAX,1)==AI_OWNER_BOUNDS);
    REQUIRE(ai_owner_move(&a,&a)==AI_OWNER_INVALID);
    REQUIRE(ai_owner_move(&c,&a)==AI_OWNER_INVALID);
    REQUIRE(ai_owner_create(&b,1,allocator)==AI_OWNER_INVALID && state.allocations==1);
    REQUIRE(ai_owner_create(&c,1,allocator)==AI_OWNER_OK);
    REQUIRE(ai_owner_move(&c,&b)==AI_OWNER_INVALID && b.active && c.active);
    REQUIRE(ai_owner_read(&b,16,&value)==AI_OWNER_OK && value==255);
    REQUIRE(ai_owner_read(&c,0,&value)==AI_OWNER_OK && value==0);
    ai_owner_drop(&a); REQUIRE(state.releases==0);
    ai_owner_drop(&c); REQUIRE(state.releases==1);
    REQUIRE(ai_owner_move(&c,&b)==AI_OWNER_OK && !b.active);
    state.reenter=&c; ai_owner_drop(&c); REQUIRE(state.releases==2 && !c.active);
    ai_owner_drop(&c); REQUIRE(state.releases==2);
    state.reenter=NULL; state.fail=true;
    REQUIRE(ai_owner_create(&a,8,allocator)==AI_OWNER_NO_MEMORY && !a.active && !a.data);
    ai_owner_drop(&a); REQUIRE(state.allocations==3 && state.releases==2);
    state.fail=false;
    REQUIRE(ai_owner_create(&a,0,allocator)==AI_OWNER_OK && a.active && !a.data);
    REQUIRE(ai_owner_read(&a,0,&value)==AI_OWNER_BOUNDS);
    REQUIRE(ai_owner_move(&b,&a)==AI_OWNER_OK); ai_owner_drop(&b);
    REQUIRE(state.allocations==3 && state.releases==2);
    REQUIRE(ai_owner_create(&a,SIZE_MAX,allocator)==AI_OWNER_CAPACITY);
    REQUIRE(ai_owner_create(&a,AI_OWNER_MAX_BYTES+1,allocator)==AI_OWNER_CAPACITY);
    REQUIRE(ai_owner_create(&a,AI_OWNER_MAX_BYTES,allocator)==AI_OWNER_OK);
    REQUIRE(ai_owner_read(&a,AI_OWNER_MAX_BYTES-1,&value)==AI_OWNER_OK && value==0);
    ai_owner_drop(&a); REQUIRE(state.allocations==4 && state.releases==3);
    allocator.release=NULL;
    REQUIRE(ai_owner_create(&a,1,allocator)==AI_OWNER_INVALID && state.allocations==4);
    REQUIRE(ai_owner_create(NULL,1,allocator)==AI_OWNER_INVALID);
    REQUIRE(ai_owner_read(NULL,0,&value)==AI_OWNER_INVALID);
    REQUIRE(ai_owner_write(NULL,0,1)==AI_OWNER_INVALID);
    REQUIRE(ai_owner_move(NULL,&a)==AI_OWNER_INVALID);
    ai_owner_drop(NULL);
    REQUIRE(ai_owner_create(&a,1,ai_owner_system_allocator())==AI_OWNER_OK); ai_owner_drop(&a);
    {
        test_allocator_state other = {0};
        ai_owner_allocator pair = {test_allocate,test_release,&other};
        REQUIRE(ai_owner_create(&a,3,pair)==AI_OWNER_OK);
        REQUIRE(ai_owner_move(&c,&a)==AI_OWNER_OK);
        ai_owner_drop(&c);
        REQUIRE(other.allocations==1 && other.releases==1);
        REQUIRE(state.allocations==4 && state.releases==3);
    }
    {
        uint64_t counter=UINT64_MAX-1;
        ai_mem_overflow=false;
        ai_mem_add(&counter,1);
        REQUIRE(counter==UINT64_MAX && !ai_mem_overflow);
        ai_mem_add(&counter,1);
        REQUIRE(counter==UINT64_MAX && ai_mem_overflow);
        ai_mem_add(&counter,0);
        REQUIRE(counter==UINT64_MAX && ai_mem_overflow);
    }
    puts("private owner runtime PASS"); return 0;
}
