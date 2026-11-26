    bool unwinding=false;
    std::map<std::pair<char,int>,std::size_t> unwind_nodes;
    static std::string unwind_site(int function,int operation,Location at){return "ai_frame("+std::to_string(function)+","+(operation<0 ? "UINT32_MAX" : std::to_string(operation))+","+std::to_string(at.offset)+","+std::to_string(at.length)+","+std::to_string(at.line)+","+std::to_string(at.column)+")";}
    void unwind_track(char kind,int id,const std::string& storage,Type type){
        if(!unwinding || !type.needs_drop())return;auto node=unwind_nodes.find({kind,id});if(node==unwind_nodes.end())fail("AI3100","Native emission lost a verified unwind registration.",{});
        out<<"ai_scope.track(ai_nodes["<<node->second<<"], &("<<storage<<"), &ai_unwind_drop<"<<storage_ctype(type)<<", "<<(type==Type::buffer ? "ai_owner_drop" : storage_ctype(type)+"_drop")<<">);\n";
    }
    void unwind_value(const IrOp& o){unwind_track('v',o.id,value(o.id),o.type);}
    void unwind_remove(char kind,int id){if(!unwinding)return;auto node=unwind_nodes.find({kind,id});if(node!=unwind_nodes.end())out<<"ai_scope.remove(ai_nodes["<<node->second<<"]);\n";}
    void unwind_main(){if(unwinding)out<<"int main(void) { try { return ai_entry(); } catch(ai_unwind_signal&) { int code=ai_unwind_report(ai_pending.fault); ai_pending.active=false; return code; } }\n";}
