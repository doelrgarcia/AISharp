R"AI_RUNTIME(
#include <stdint.h>
#include <inttypes.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#include <math.h>
#ifndef AISHARP_RECURSION_LIMIT
#define AISHARP_RECURSION_LIMIT 128
#endif
_Static_assert(sizeof(int32_t)==4 && sizeof(uint32_t)==4, "AI# Int32 target contract");
#ifdef AISHARP_NATIVE_UNWIND
static thread_local unsigned ai_depth = 0;
#else
static unsigned ai_depth = 0;
#endif
/* Diagnostic evidence is opt-in; no buffer bytes, addresses or source values. */
static bool ai_mem_initialized=false,ai_mem_enabled=false,ai_mem_overflow=false;
static uint64_t ai_mem_attempts=0,ai_mem_allocations=0,ai_mem_releases=0,ai_mem_creates=0,ai_mem_drops=0,ai_mem_failures=0,ai_mem_live_bytes=0,ai_mem_fail_at=0;
static uint64_t ai_mem_trace[256];
static unsigned ai_mem_trace_count=0;
static bool ai_mem_trace_truncated=false;
void ai_mem_add(uint64_t* counter,uint64_t amount) {
    if(UINT64_MAX-*counter<amount){*counter=UINT64_MAX;ai_mem_overflow=true;}else *counter+=amount;
}
void ai_mem_init(void) {
    char enabled[2]={0},failure[24]={0};size_t size=0;
    if(ai_mem_initialized)return;
    ai_mem_initialized=true;
    if(getenv_s(&size,enabled,sizeof(enabled),"AISHARP_MEMORY_EVIDENCE")!=0 || size!=2 || enabled[0]!='1')return;
    ai_mem_enabled=true;
    if(getenv_s(&size,failure,sizeof(failure),"AISHARP_MEMORY_FAIL_AT")==0 && size>1){
        uint64_t value=0;size_t i;bool valid=true;
        for(i=0;failure[i];++i){unsigned digit=(unsigned)(failure[i]-'0');if(digit>9 || value>(UINT64_MAX-digit)/10){valid=false;break;}value=value*10+digit;}
        if(valid)ai_mem_fail_at=value;
    }
}
int ai_memory_report(void) {
    unsigned i;int result=0;ai_mem_init();
    if(!ai_mem_enabled)return 0;
    if(printf(",\"memory\":{\"schemaVersion\":\"aisharp.memory-evidence/1\",\"attempts\":%" PRIu64 ",\"allocations\":%" PRIu64 ",\"releases\":%" PRIu64 ",\"ownersCreated\":%" PRIu64 ",\"drops\":%" PRIu64 ",\"allocationFailures\":%" PRIu64 ",\"liveBytes\":%" PRIu64 ",\"overflowed\":%s,\"dropTraceTruncated\":%s,\"dropOrder\":[",ai_mem_attempts,ai_mem_allocations,ai_mem_releases,ai_mem_creates,ai_mem_drops,ai_mem_failures,ai_mem_live_bytes,ai_mem_overflow ? "true" : "false",ai_mem_trace_truncated ? "true" : "false")<0)result=-1;
    for(i=0;i<ai_mem_trace_count;++i)if(printf("%s%" PRIu64,i ? "," : "",ai_mem_trace[i])<0)result=-1;
    if(fputs("]}",stdout)<0)result=-1;
    return result;
}
__declspec(noreturn) void ai_terminal_fail(const char* kind) {
    int report = printf("{\"schemaVersion\":\"aisharp.native-result/1\",\"outcome\":\"threw\",\"exception\":\"%s\"", kind);
    int memory=ai_memory_report(),end=fputs("}\n",stdout);
    exit(report < 0 || memory < 0 || end < 0 || fflush(stdout) != 0 ? 74 : 70);
}
#ifdef AISHARP_NATIVE_UNWIND
__declspec(noreturn) void ai_fail(const char* kind);
#else
__declspec(noreturn) void ai_fail(const char* kind) {ai_terminal_fail(kind);}
#endif
int32_t ai_narrow(int64_t n) { if(n<INT32_MIN || n>INT32_MAX)ai_fail("System.OverflowException"); return (int32_t)n; }
int32_t ai_bits(uint32_t n) { int32_t result; memcpy(&result,&n,sizeof(result)); return result; }
int32_t ai_add_wrap(int32_t a,int32_t b) { return ai_bits((uint32_t)a+(uint32_t)b); }
int32_t ai_sub_wrap(int32_t a,int32_t b) { return ai_bits((uint32_t)a-(uint32_t)b); }
int32_t ai_mul_wrap(int32_t a,int32_t b) { return ai_bits((uint32_t)a*(uint32_t)b); }
int32_t ai_neg_wrap(int32_t a) { return ai_bits(0u-(uint32_t)a); }
int32_t ai_add(int32_t a,int32_t b) { return ai_narrow((int64_t)a+b); }
int32_t ai_sub(int32_t a,int32_t b) { return ai_narrow((int64_t)a-b); }
int32_t ai_mul(int32_t a,int32_t b) { return ai_narrow((int64_t)a*b); }
int32_t ai_neg(int32_t a) { return ai_narrow(-(int64_t)a); }
int32_t ai_div(int32_t a,int32_t b) { if(!b)ai_fail("System.DivideByZeroException"); if(a==INT32_MIN && b==-1)ai_fail("System.OverflowException"); return a/b; }
int32_t ai_rem(int32_t a,int32_t b) { if(!b)ai_fail("System.DivideByZeroException"); if(a==INT32_MIN && b==-1)ai_fail("System.OverflowException"); return a%b; }
int32_t ai_shl(int32_t a,int32_t b) { return ai_bits((uint32_t)a << ((uint32_t)b & 31u)); }
int32_t ai_ushr(int32_t a,int32_t b) { return ai_bits((uint32_t)a >> ((uint32_t)b & 31u)); }
int32_t ai_shr(int32_t a,int32_t b) { unsigned n=(uint32_t)b & 31u; if(n==0)return a; uint32_t r=(uint32_t)a >> n; if(a<0)r|=UINT32_MAX << (32u-n); return ai_bits(r); }
uint32_t ai_u32_from_i32(int32_t value,bool wraps) { if(!wraps && value<0)ai_fail("System.OverflowException"); return (uint32_t)value; }
uint32_t ai_u32_from_i64(int64_t value,bool wraps) { if(wraps)return (uint32_t)value; if(value<0 || (uint64_t)value>UINT32_MAX)ai_fail("System.OverflowException"); return (uint32_t)value; }
int32_t ai_i32_from_u32(uint32_t value,bool wraps) { if(!wraps && value>INT32_MAX)ai_fail("System.OverflowException"); return ai_bits(value); }
uint32_t ai_u32_add(uint32_t a,uint32_t b) { if(a>UINT32_MAX-b)ai_fail("System.OverflowException"); return a+b; }
uint32_t ai_u32_add_wrap(uint32_t a,uint32_t b) { return a+b; }
uint32_t ai_u32_sub(uint32_t a,uint32_t b) { if(a<b)ai_fail("System.OverflowException"); return a-b; }
uint32_t ai_u32_sub_wrap(uint32_t a,uint32_t b) { return a-b; }
uint32_t ai_u32_mul(uint32_t a,uint32_t b) { if(a!=0 && b>UINT32_MAX/a)ai_fail("System.OverflowException"); return a*b; }
uint32_t ai_u32_mul_wrap(uint32_t a,uint32_t b) { return a*b; }
uint32_t ai_u32_div(uint32_t a,uint32_t b) { if(!b)ai_fail("System.DivideByZeroException"); return a/b; }
uint32_t ai_u32_rem(uint32_t a,uint32_t b) { if(!b)ai_fail("System.DivideByZeroException"); return a%b; }
uint32_t ai_u32_shl(uint32_t a,uint32_t b) { return a << (b & 31u); }
uint32_t ai_u32_shr(uint32_t a,uint32_t b) { return a >> (b & 31u); }
uint64_t ai_u64_from_i64(int64_t value,bool wraps) { if(!wraps && value<0)ai_fail("System.OverflowException"); return (uint64_t)value; }
int64_t ai_i64_from_u64(uint64_t value,bool wraps) { if(!wraps && value>INT64_MAX)ai_fail("System.OverflowException"); return (int64_t)value; }
int32_t ai_i32_from_u64(uint64_t value,bool wraps) { if(!wraps && value>INT32_MAX)ai_fail("System.OverflowException"); return ai_bits((uint32_t)value); }
uint32_t ai_u32_from_u64(uint64_t value,bool wraps) { if(!wraps && value>UINT32_MAX)ai_fail("System.OverflowException"); return (uint32_t)value; }
uint64_t ai_u64_add(uint64_t a,uint64_t b) { if(a>UINT64_MAX-b)ai_fail("System.OverflowException"); return a+b; }
uint64_t ai_u64_add_wrap(uint64_t a,uint64_t b) { return a+b; }
uint64_t ai_u64_sub(uint64_t a,uint64_t b) { if(a<b)ai_fail("System.OverflowException"); return a-b; }
uint64_t ai_u64_sub_wrap(uint64_t a,uint64_t b) { return a-b; }
uint64_t ai_u64_mul(uint64_t a,uint64_t b) { if(a!=0 && b>UINT64_MAX/a)ai_fail("System.OverflowException"); return a*b; }
uint64_t ai_u64_mul_wrap(uint64_t a,uint64_t b) { return a*b; }
uint64_t ai_u64_div(uint64_t a,uint64_t b) { if(!b)ai_fail("System.DivideByZeroException"); return a/b; }
uint64_t ai_u64_rem(uint64_t a,uint64_t b) { if(!b)ai_fail("System.DivideByZeroException"); return a%b; }
uint64_t ai_u64_shl(uint64_t a,uint32_t b) { return a << (b & 63u); }
uint64_t ai_u64_shr(uint64_t a,uint32_t b) { return a >> (b & 63u); }
float ai_f32_bits(uint32_t bits) { float value; memcpy(&value,&bits,sizeof(value)); return value; }
int32_t ai_i32_from_f32(float value,bool wraps) { const double v=(double)value; if(wraps){ if(!(v==v))return 0; if(v>=2147483648.0)return INT32_MAX; if(v<-2147483648.0)return INT32_MIN; return (int32_t)v; } if(!(v>=-2147483648.0 && v<=2147483647.0))ai_fail("System.OverflowException"); return (int32_t)v; }
int64_t ai_i64_from_f32(float value,bool wraps) { const double v=(double)value; if(wraps){ if(!(v==v))return 0; if(v>=9223372036854775808.0)return INT64_MAX; if(v<-9223372036854775808.0)return INT64_MIN; return (int64_t)v; } if(!(v>=-9223372036854775808.0 && v<9223372036854775808.0))ai_fail("System.OverflowException"); return (int64_t)v; }
uint32_t ai_u32_from_f32(float value,bool wraps) { const double v=(double)value; if(wraps){ if(!(v==v) || v<=0.0)return 0; if(v>=4294967296.0)return UINT32_MAX; return (uint32_t)v; } if(!(v>=0.0 && v<=4294967295.0))ai_fail("System.OverflowException"); return (uint32_t)v; }
uint64_t ai_u64_from_f32(float value,bool wraps) { const double v=(double)value; if(wraps){ if(!(v==v) || v<=0.0)return 0; if(v>=18446744073709551616.0)return UINT64_MAX; return (uint64_t)v; } if(!(v>=0.0 && v<18446744073709551616.0))ai_fail("System.OverflowException"); return (uint64_t)v; }
int64_t ai_small_from_f32(float value,int64_t minimum,int64_t maximum,bool wraps) { const double v=(double)value; if(wraps){ int64_t converted=ai_i32_from_f32(value,true); uint32_t range=(uint32_t)(maximum-minimum)+1u; int32_t bits=(int32_t)((uint32_t)converted & (range-1u)); return bits>(int32_t)maximum ? bits-(int32_t)range : bits; } if(!(v>=(double)minimum && v<=(double)maximum))ai_fail("System.OverflowException"); return (int64_t)v; }
double ai_f64_bits(uint64_t bits) { double value; memcpy(&value,&bits,sizeof(value)); return value; }
float ai_f32_from_f64(double value,bool wraps) { (void)wraps; volatile double v=value; return (float)v; }
int32_t ai_i32_from_f64(double value,bool wraps) { if(wraps){ if(!(value==value))return 0; if(value>=2147483648.0)return INT32_MAX; if(value<-2147483648.0)return INT32_MIN; return (int32_t)value; } if(!(value>=-2147483648.0 && value<=2147483647.0))ai_fail("System.OverflowException"); return (int32_t)value; }
int64_t ai_i64_from_f64(double value,bool wraps) { if(wraps){ if(!(value==value))return 0; if(value>=9223372036854775808.0)return INT64_MAX; if(value<-9223372036854775808.0)return INT64_MIN; return (int64_t)value; } if(!(value>=-9223372036854775808.0 && value<9223372036854775808.0))ai_fail("System.OverflowException"); return (int64_t)value; }
uint32_t ai_u32_from_f64(double value,bool wraps) { if(wraps){ if(!(value==value) || value<=0.0)return 0; if(value>=4294967296.0)return UINT32_MAX; return (uint32_t)value; } if(!(value>=0.0 && value<=4294967295.0))ai_fail("System.OverflowException"); return (uint32_t)value; }
uint64_t ai_u64_from_f64(double value,bool wraps) { if(wraps){ if(!(value==value) || value<=0.0)return 0; if(value>=18446744073709551616.0)return UINT64_MAX; return (uint64_t)value; } if(!(value>=0.0 && value<18446744073709551616.0))ai_fail("System.OverflowException"); return (uint64_t)value; }
int64_t ai_small_from_f64(double value,int64_t minimum,int64_t maximum,bool wraps) { if(wraps){ int64_t converted=ai_i32_from_f64(value,true); uint32_t range=(uint32_t)(maximum-minimum)+1u; int32_t bits=(int32_t)((uint32_t)converted & (range-1u)); return bits>(int32_t)maximum ? bits-(int32_t)range : bits; } if(!(value>=(double)minimum && value<=(double)maximum))ai_fail("System.OverflowException"); return (int64_t)value; }
)AI_RUNTIME"
