// Reconstruct the backend cleanup inventory from typed executable IR.
// No caller-supplied cleanup list or mode hint grants an owner or fault site.
bool native_fault_site(const IrOp& o){return (o.kind=="call" && o.function!=-5 && o.function!=-6) || o.kind=="binary:/" || o.kind=="binary:%" || (!o.wraps && (o.kind=="binary:+" || o.kind=="binary:-" || o.kind=="binary:*" || o.kind=="unary:-" || (o.kind=="integer-convert" && o.type==Type::integer) || o.kind=="integer-narrow" || o.kind=="integer-u32" || o.kind=="integer-u64" || o.kind=="float-integer" || o.kind=="double-integer" || o.kind=="decimal-integer" || o.kind=="float-decimal" || o.kind=="double-decimal"));}
IrUnwindPlan native_unwind_plan(const IrFunction& f){
    IrUnwindPlan plan;if(!f.unwind)return plan;
    for(auto& p:f.places)if(p.parent==-1 && p.type.needs_drop())plan.nodes.emplace_back('p',p.id);
    for(auto& b:f.blocks)for(auto& o:b.ops){if(o.type.needs_drop())plan.nodes.emplace_back('v',o.id);if(native_fault_site(o))plan.sites.push_back(o.id);}
    if(plan.nodes.size()>50000 || plan.sites.size()>400000)fail("AI3100","Native unwind cleanup inventory limit exceeded.",f.declaration);
    return plan;
}
