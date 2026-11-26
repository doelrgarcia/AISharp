R"AI_FAILURE(
/* Bounded structural cause data: no payload, arguments, addresses or source text. */
typedef struct { uint32_t function,expression,offset,length,line,column; } ai_failure_frame;
typedef struct { bool known; ai_failure_frame primary,sites[8]; uint32_t count,omitted; } ai_cause;
ai_failure_frame ai_frame(uint32_t f,uint32_t e,uint32_t o,uint32_t n,uint32_t l,uint32_t c) { ai_failure_frame x={f,e,o,n,l,c};return x; }
ai_cause ai_cause_new(ai_failure_frame frame) { ai_cause value={0};value.known=true;value.primary=frame;return value; }
void ai_cause_push(ai_cause* cause,ai_failure_frame frame) { if(cause->count<8)cause->sites[cause->count++]=frame;else if(cause->omitted<UINT32_MAX)++cause->omitted; }
int ai_frame_report(ai_failure_frame frame) { return printf("{\"function\":%" PRIu32 ",\"expression\":%" PRIu32 ",\"offset\":%" PRIu32 ",\"length\":%" PRIu32 ",\"line\":%" PRIu32 ",\"column\":%" PRIu32 "}",frame.function,frame.expression,frame.offset,frame.length,frame.line,frame.column); }
int ai_cause_report(const ai_cause* cause) {
    if(printf("{\"sourceSha256\":\"%s\",\"originKnown\":%s,\"origin\":",ai_source_hash,cause->known ? "true" : "false")<0)return -1;
    if(cause->known){if(ai_frame_report(cause->primary)<0)return -1;}else if(fputs("null",stdout)<0)return -1;
    if(fputs(",\"propagation\":[",stdout)<0)return -1;
    for(uint32_t i=0;i<cause->count;++i){if(i && fputc(',',stdout)==EOF)return -1;if(ai_frame_report(cause->sites[i])<0)return -1;}
    return printf("],\"omittedFrames\":%" PRIu32 "}",cause->omitted);
}
)AI_FAILURE"
