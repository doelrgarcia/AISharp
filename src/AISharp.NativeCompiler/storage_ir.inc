// Private typed native representation. Included after the bound AST definitions.
// It is the checked input to native emission, not a parallel ownership claim.
struct IrPlace { int id; Type type; Location at; bool parameter, mutable_, constant; int parent=-1,field=-1; };
struct IrOp {
    int id; std::string kind; Type type; Location at;
    int place=-1, function=-1,field=-1; std::vector<int> operands, incoming;std::string external_name;
    bool wraps=false; std::int64_t literal=0,literal2=0;int cause_place=-1;
};
struct IrBlock {
    int id; std::vector<IrOp> ops; std::vector<int> successors;
    std::string end; int condition=-1, result=-1; Location at;
    bool reachable=false;
    std::set<int> initialized_in, initialized_out, live_in, live_out;
    std::set<int> maybe_initialized_in, maybe_initialized_out, active_in, active_out, maybe_active_in, maybe_active_out;
};
struct IrLoan { Type type=Type::unit; int creation=-1,input_parameter=-1,input_depth=0; std::vector<int> result_component;int id,place=-1; bool exclusive=false,access_only=false; Location at; std::set<int> roots,parents,region,ends_after; };
struct RegionPath {
    int first=0;std::vector<int> selectors;
    RegionPath()=default;RegionPath(int parameter,int depth):first(parameter),selectors(static_cast<std::size_t>(depth),-1){}
    bool operator==(const RegionPath&)const=default;
    bool operator<(const RegionPath& other)const{return std::tie(first,selectors)<std::tie(other.first,other.selectors);}
    RegionPath field(int index)const{auto path=*this;path.selectors.push_back(index);return path;}
    RegionPath dereference()const{auto path=*this;path.selectors.push_back(-1);return path;}
    int depth()const{return static_cast<int>(std::count(selectors.begin(),selectors.end(),-1));}
    bool plain()const{return std::all_of(selectors.begin(),selectors.end(),[](int step){return step==-1;});}
};
using RegionSet=std::set<RegionPath>;
using IrInputEvent=std::pair<int,int>;
using IrInputEvents=std::set<IrInputEvent>;
struct IrInputDemand { bool expanded=false;IrInputEvents history,observations; };
struct IrInputRegion { int parameter,depth,storage; Type type; Location at;int cell=-1;RegionPath path;bool mutable_access=false;int loan_id=-1;bool folded=false;int structural_event=-1; };
using ComponentPath=std::vector<int>;
using ReferenceComponents=std::map<ComponentPath,std::set<int>>;
using NativeVariantTags=std::map<ComponentPath,std::set<int>>;
struct IrRegionSummary { RegionSet returned;std::set<ComponentPath> possible_components;NativeVariantTags possible_tags;std::map<ComponentPath,RegionSet> returned_components; std::map<RegionPath,RegionSet> effects,unwind_effects; bool operator==(const IrRegionSummary&) const=default; };
struct IrMatchArm {Location at,guard_at,value_at;bool wildcard=false;int source=-1;int test=-1,pattern=-1,condition=-1,guard_entry=-1,guard_end=-1,guard=-1,body=-1,value_block=-1,value=-1,next=-1;};
struct IrMatchSource {Location at,guard_at,value_at;int pattern=-1,guard_mode=-2;};
struct IrMatchDispatch {int block=-1,selector=-1,condition=-1,group=-1,next=-1;};
struct IrMatchGroup {int entry=-1,tail=-1;bool other=false;std::vector<int> selectors;std::vector<IrMatchArm> arms;};
struct IrMatch {Location at;Type type=Type::unit;bool propagation=false;int subject=-1,scrutinee=-1,entry=-1,merge=-1,place=-1,result=-1,fault=-1,dispatch_end=-1;std::vector<IrMatchSource> sources;std::vector<IrMatchDispatch> dispatch;std::vector<IrMatchGroup> groups;};
struct IrUnwindPlan {std::vector<std::pair<char,int>> nodes;std::vector<int> sites;bool operator==(const IrUnwindPlan&)const=default;};
struct IrStatic { std::string name;Type type;std::int64_t value=0; };
struct IrFunction { std::vector<IrStatic> statics;std::vector<IrMatch> matches; std::map<RegionPath,IrInputDemand> input_demands;std::map<int,IrInputEvents> external_history;std::set<int> summary_places;std::map<int,int> summary_anchors;bool input_rebuild=false,input_observations_changed=false;std::size_t input_facts=0;std::shared_ptr<const NativeProductTable> products;std::vector<IrPlace> external_places;std::map<int,RegionPath> external_paths;std::map<RegionPath,int> external_ids;std::map<int,ReferenceComponents> reference_components;std::vector<IrInputRegion> inputs; IrRegionSummary summary; std::vector<IrLoan> loans; Location declaration;int id; Type type; bool entry=false; std::vector<IrPlace> places; std::vector<int> parameters; std::vector<IrBlock> blocks;bool fallible=false,unwind=false;Type success_type=Type::unit,error_type=Type::unit;Location failure_declaration,fault_mode_location;IrUnwindPlan unwind_plan; };
#include "native_unwind_plan.inc"
#include "place_graph.inc"
#include "native_variant_verifier.inc"
#include "owner_flow.inc"
void verify_function_regions(std::vector<IrFunction>& functions);
void verify_native_failures(const std::vector<IrFunction>& functions);
class StorageLowerer {
    IrFunction f; int current=0, next_value=0,next_place=0;
    std::set<int> static_symbols;std::map<int,std::string> static_names;
    std::map<std::pair<int,int>,int> field_places;
    std::map<int,int> pattern_places;std::set<int> pattern_units;
    std::vector<std::vector<int>> scopes;
    struct ExpressionCleanup{int place=-1,value=-1;Type type=Type::unit;};
    std::vector<ExpressionCleanup> expression_cleanups;
    std::map<int,std::string> value_kinds;
    void remember_value(int value,Type type){if(!type.is_copy() && value_kinds.at(value)!="reference-use" && value_kinds.at(value)!="reference-field" && !value_kinds.at(value).starts_with("reference-follow-"))expression_cleanups.push_back({-1,value,type});}
    void propagation_cleanup(Location at){for(auto it=expression_cleanups.rbegin();it!=expression_cleanups.rend();++it){if(it->place>=0)op("storage-dead",Type::unit,at,{},it->place);else op(it->type.is_reference() ? "end-reference" : "drop-value",Type::unit,at,{it->value});}cleanup(0,at);}

    struct Loop { int stop, next; std::size_t scope_depth; };
    std::vector<Loop> loops;
    std::map<int,int> label_blocks;
    int block() {
        if(f.blocks.size()>=50000)fail("AI3100","Native storage IR block limit exceeded.",{});
        int id=static_cast<int>(f.blocks.size());IrBlock b{};b.id=id;f.blocks.push_back(std::move(b));return id;
    }
    IrBlock& here(){return f.blocks[static_cast<std::size_t>(current)];}
    int op(std::string kind,Type type,Location at,std::vector<int> values={},int place=-1) {
        if(next_value>=400000)fail("AI3100","Native storage IR instruction limit exceeded.",at);
        IrOp value{};value.id=next_value++;value.kind=std::move(kind);value.type=type;value.at=at;value.operands=std::move(values);value.place=place;
        int id=value.id;value_kinds.emplace(id,value.kind);here().ops.push_back(std::move(value));return id;
    }
    void jump(int target,Location at={}) {here().end="goto";here().successors={target};here().at=at;}
    void branch(int value,int yes,int no,const Expr& e) {
        here().end="branch";here().condition=value;here().at=e.token.at;
        if(e.constant)here().successors={e.constant->number ? yes : no};else here().successors={yes,no};
    }
    void cleanup(std::size_t keep,Location at) {
        for(std::size_t i=scopes.size();i>keep;--i)for(auto p=scopes[i-1].rbegin();p!=scopes[i-1].rend();++p){if(static_symbols.contains(*p))continue;op("storage-dead",Type::unit,at,{},*p);}
    }
    void scoped(const Stmt& s) {
        scopes.emplace_back();statement(s);if(here().end.empty())cleanup(scopes.size()-1,s.end);scopes.pop_back();
    }
    void add_place(int id,Type type,Location at,bool parameter,bool mutable_,bool constant,int parent=-1,int field=-1){
        if(f.places.size()>=65536)fail("AI3100","Native expanded storage place limit exceeded.",at);f.places.push_back({id,type,at,parameter,mutable_,constant,parent,field});
        if(parent>=0)field_places.emplace(std::pair{parent,field},id);
        if(type.is_aggregate())for(std::size_t i=0;i<type.definition().fields.size();++i){auto child=next_place++;add_place(child,type.field_type(i),at,false,mutable_,constant,id,static_cast<int>(i));}
    }
    int direct_place(const Expr& e)const{
        if(e.kind==Expr::name){auto alias=pattern_places.find(e.symbol);return alias==pattern_places.end() ? e.symbol : alias->second;}if(e.kind!=Expr::field)return -1;auto parent=direct_place(*e.children[0]);if(parent<0)return -1;auto it=field_places.find({parent,e.field_index});if(it==field_places.end())fail("AI3100","Bound native field lost its storage projection.",e.token.at);return it->second;
    }
    int place_pointer(const Expr& e,bool mutation){
        if(e.kind==Expr::dereference)return reference_access(*e.children[0],mutation);
        if(e.kind==Expr::field){auto base=place_pointer(*e.children[0],mutation);auto pointer=Type::reference(e.type,mutation ? Type::Borrow::exclusive : Type::Borrow::shared);auto id=op("reference-field",pointer,e.token.at,{base});here().ops.back().field=e.field_index;return id;}
        int place=direct_place(e);if(place>=0)return op(mutation ? "borrow-exclusive" : "borrow-shared",Type::reference(e.type,mutation ? Type::Borrow::exclusive : Type::Borrow::shared),e.token.at,{},place);
        fail("AI3115","Field access requires stable native storage.",e.token.at);
    }
    int reference_access(const Expr& e,bool mutation) {
        if(e.kind==Expr::field && e.op=="temporary")return expression(e);
        if(e.kind==Expr::overflow_context)return reference_access(*e.children[0],mutation);
        int direct=direct_place(e);if(direct>=0)return op("reference-use",e.type,e.token.at,{},direct);
        if(e.kind==Expr::dereference || e.kind==Expr::field){auto parent=e.kind==Expr::dereference ? reference_access(*e.children[0],mutation) : place_pointer(e,mutation);auto type=e.type;if(!mutation)type=Type::reference(type.pointee(),Type::Borrow::shared);return op(mutation ? "reference-follow-exclusive" : "reference-follow-shared",type,e.token.at,{parent});}
        return expression(e);
    }
    int integer_conversion(int value,Type source,Type target,Location at,bool wraps=false) {
        if(source==target)return value;
        if(source.is_enum() || target.is_enum())return op(target.is_enum() ? "integer-enum" : "enum-integer",target,at,{value});
        if(source.is_decimal() || target.is_decimal()){
            const char* kind = target.is_decimal() ? (source.is_integer() ? "integer-decimal" : source.is_float32() ? "float-decimal" : "double-decimal")
                : (target.is_integer() ? "decimal-integer" : target.is_float32() ? "decimal-float" : "decimal-double");
            return op(kind,target,at,{value});
        }
        const bool small=source.narrow_integer() || target.narrow_integer();
        const bool widens=native_integer_widens(source,target);
        const bool u32=source.is_uint32() || target.is_uint32();
        const bool u64=source.is_ulong64() || target.is_ulong64();
        const bool f32=source.is_float32() || target.is_float32();
        const bool f64=source.is_float64() || target.is_float64();
        if(f32 || f64){
            const char* float_kind = target.is_float64() ? (source.is_float32() ? "float-double" : "integer-double") :
                target.is_float32() ? (source.is_float64() ? "double-float" : "integer-float") : (source.is_float64() ? "double-integer" : "float-integer");
            auto id=op(float_kind,target,at,{value});here().ops.back().wraps=wraps;return id;
        }
        const char* kind=u64 ? (widens ? "integer-widen" : (target.narrow_integer() ? "integer-narrow" : "integer-u64"))
                             : u32 ? (widens ? "integer-widen" : (target.narrow_integer() ? "integer-narrow" : "integer-u32"))
                             : (small ? (widens ? "integer-widen" : "integer-narrow") : "integer-convert");
        auto id=op(kind,target,at,{value});
        here().ops.back().wraps=!widens && wraps;
        return id;
    }
#include "native_match_lowering.inc"
    int expression(const Expr& e){auto keep=expression_cleanups.size();int result=expression_impl(e);expression_cleanups.resize(keep);return result;}
    int expression_impl(const Expr& e) {
        if(e.constant){int id=op("constant",e.type,e.token.at);here().ops.back().literal=e.constant->number;here().ops.back().literal2=e.constant->number2;return id;}
        switch(e.kind) {
        case Expr::match:return match_expression(e);
        case Expr::unit_value:return op("unit",Type::unit,e.token.at);
        case Expr::propagate:fail("AI3100","Unbound propagation in native storage IR.",e.token.at);
        case Expr::nameof_sym:{int id=op("string-literal",Type::string,e.token.at);here().ops.back().external_name=e.op;return id;}
        case Expr::literal:if(e.type==Type::string){if(e.null_literal)return op("null-string",Type::string,e.token.at);int id=op("string-literal",Type::string,e.token.at);here().ops.back().external_name=e.op;return id;}fail("AI3100","Unbound literal in native storage IR.",e.token.at);
        case Expr::args_value:return op("args-value",Type::args,e.token.at);
        case Expr::stack_alloc:{int id=op("stack-alloc",Type::buffer,e.token.at);here().ops.back().field=static_cast<int>(e.value);return id;}
        case Expr::class_upcast:{int value=expression(*e.children[0]);return op("class-upcast",e.type,e.token.at,{value});}
        case Expr::class_downcast:{int value=expression(*e.children[0]);return op("class-upcast",e.type,e.token.at,{value});}
        case Expr::class_tag:{int value=expression(*e.children[0]);return op("class-tag",Type::integer,e.token.at,{value});}
        case Expr::fixed_pin:return op("constant",Type::unit,e.token.at);
        case Expr::await_value:return expression(*e.children[0]);
        case Expr::class_new:{std::vector<int> fields;for(auto& field:e.children)fields.push_back(expression(*field));int id=op("class-new",e.type,e.token.at,std::move(fields));here().ops.back().function=e.nominal;return id;}
        case Expr::class_field_assign:{int handle=expression(*e.children[0]);int value=expression(*e.children[1]);int id=op("class-field-store",Type::unit,e.token.at,{handle,value});here().ops.back().function=e.nominal;here().ops.back().field=e.field_index;return id;}
        case Expr::args_length:{int value=expression(*e.children[0]);return op("args-length",Type::integer,e.token.at,{value});}
        case Expr::index:{int base=expression(*e.children[0]);int index=expression(*e.children[1]);return op("args-at",Type::string,e.token.at,{base,index});}
        case Expr::as_test:{int value=expression(*e.children[0]);if(e.op=="as-identity")return value;return op("as-string",Type::string,e.token.at,{value});}
        case Expr::integer_conversion: {
            const auto& operand=*e.children[0];
            auto value=expression(operand);
            if(e.op=="object-box"){int id=op("object-box",e.type,e.token.at,{value});here().ops.back().field=e.field_index;return id;}
            if(e.op=="object-unbox"){int id=op("object-unbox",e.type,e.token.at,{value});here().ops.back().field=e.field_index;return id;}
            return integer_conversion(value,operand.type,e.type,e.token.at,e.wraps);
        }
        case Expr::construct:{std::vector<int> fields;for(auto& field:e.children){int value=expression(*field);fields.push_back(value);remember_value(value,field->type);}return op("aggregate-pack",e.type,e.token.at,std::move(fields));}
        case Expr::variant:{std::vector<int> payload;for(auto& item:e.children){int value=expression(*item);payload.push_back(value);remember_value(value,item->type);}int id=op("variant-pack",e.type,e.token.at,std::move(payload));here().ops.back().field=e.field_index;return id;}
        case Expr::field:{if(e.op=="class-field"){int handle=expression(*e.children[0]);int id=op("class-field-load",e.type,e.token.at,{handle});here().ops.back().function=e.nominal;here().ops.back().field=e.field_index;return id;}int place=direct_place(e);if(place>=0)return op(e.type.is_copy() ? "copy-read" : "move",e.type,e.token.at,{},place);if(e.op=="temporary"){auto aggregate=expression(*e.children[0]);auto id=op("aggregate-extract",e.type,e.token.at,{aggregate});here().ops.back().field=e.field_index;return id;}return op("reference-read",e.type,e.token.at,{place_pointer(e,false)});}
        case Expr::name:if(pattern_units.contains(e.symbol))return op("unit",Type::unit,e.token.at);if(static_symbols.contains(e.symbol)){int id=op("static-load",e.type,e.token.at);here().ops.back().external_name=static_names.at(e.symbol);return id;}return op(e.type.is_copy() ? "copy-read" : "move",e.type,e.token.at,{},direct_place(e));
        case Expr::borrow: {
            auto& target=*e.children[0];auto kind=e.op=="exclusive" ? "borrow-exclusive" : "borrow-shared";
            int direct=direct_place(target);if(direct>=0)return op(kind,e.type,e.token.at,{},direct);
            auto pointer=place_pointer(target,e.op=="exclusive");return op(e.op=="exclusive" ? "reborrow-exclusive" : "reborrow-shared",e.type,e.token.at,{pointer});
        }
        case Expr::dereference:return op("reference-read",e.type,e.token.at,{reference_access(*e.children[0],false)});
        case Expr::overflow_context:return expression(*e.children[0]);
        case Expr::is_test: {
            auto subject_type=e.children[0]->type;
            int subject=expression(*e.children[0]);
            int constant=op("constant",subject_type,e.token.at);here().ops.back().literal=e.symbol;
            return op("binary:==",Type::boolean,e.token.at,{subject,constant});
        }
        case Expr::with_expr: {
            auto subject_type=e.children[0]->type;auto& definition=subject_type.definition();
            int subject=expression(*e.children[0]);int temp=next_place++;add_place(temp,subject_type,e.token.at,false,true,false);
            op("storage-live",Type::unit,e.token.at,{},temp);op("write",Type::unit,e.token.at,{subject},temp);
            std::map<int,int> updates;
            for(auto& update:e.with_updates){auto name=canonical(update.first.text);int field_index=-1;for(std::size_t i=0;i<definition.fields.size();++i)if(definition.fields[i].name==name){field_index=static_cast<int>(i);break;}if(field_index<0)fail("AI3100","Unverified with-update field in native storage IR.",update.first.at);updates.emplace(field_index,expression(*update.second));}
            std::vector<int> fields;
            for(std::size_t index=0;index<definition.fields.size();++index){auto updated=updates.find(static_cast<int>(index));if(updated!=updates.end()){fields.push_back(updated->second);continue;}auto found_place=field_places.find({temp,static_cast<int>(index)});if(found_place==field_places.end())fail("AI3100","Native with-expression field place was not allocated.",e.token.at);fields.push_back(op("copy-read",subject_type.field_type(index),e.token.at,{},found_place->second));}
            int result=op("aggregate-pack",subject_type,e.token.at,std::move(fields));op("storage-dead",Type::unit,e.token.at,{},temp);return result;
        }
        case Expr::params_pack: {
            std::vector<int> values;
            for(auto& argument:e.children){int value=expression(*argument);values.push_back(value);remember_value(value,argument->type);}
            auto constant=[&](std::int64_t number){int id=op("constant",Type::integer,e.token.at);here().ops.back().literal=number;return id;};
            int owner=op("call",Type::buffer,e.token.at,{constant(static_cast<std::int64_t>(values.size()))});
            here().ops.back().function=-2;
            for(std::size_t index=0;index<values.size();++index){
                int next=op("call",Type::buffer,e.token.at,{owner,constant(static_cast<std::int64_t>(index)),values[index]});
                here().ops.back().function=-3;
                owner=next;
            }
            remember_value(owner,Type::buffer);
            return owner;
        }
        case Expr::call: {
            if(!e.external_name.empty()){
                std::vector<int> args;for(auto& child:e.children){int value=expression(*child);args.push_back(value);remember_value(value,child->type);}
                int id=op("external-call",e.type,e.token.at,std::move(args));here().ops.back().external_name=e.external_name;return id;
            }
            std::vector<int> args;for(std::size_t i=0;i<e.children.size();++i){int value=i==0 && e.symbol<=-6 && e.symbol>=-8 ? reference_access(*e.children[i],e.symbol==-8) : expression(*e.children[i]);args.push_back(value);remember_value(value,e.children[i]->type);}
            int id=op("call",e.type,e.token.at,std::move(args));here().ops.back().function=e.symbol;return id;
        }
        case Expr::unary: {
            if(e.op=="new")return expression(*e.children[0]);
            auto a=expression(*e.children[0]);int id=op("unary:"+e.op,e.type,e.token.at,{a});here().ops.back().wraps=e.wraps;return id;
        }
        case Expr::binary: {
            auto a=expression(*e.children[0]);
            if(e.op=="&&" || e.op=="||") {
                int left=current,right=block(),join=block();
                branch(a,e.op=="&&" ? right : join,e.op=="&&" ? join : right,*e.children[0]);
                current=right;auto b=expression(*e.children[1]);int right_end=current;jump(join,e.token.at);current=join;
                std::vector<int> operands,incoming;
                if(std::find(f.blocks[static_cast<std::size_t>(left)].successors.begin(),f.blocks[static_cast<std::size_t>(left)].successors.end(),join)!=f.blocks[static_cast<std::size_t>(left)].successors.end()){operands.push_back(a);incoming.push_back(left);}
                operands.push_back(b);incoming.push_back(right_end);
                int id=op("phi",Type::boolean,e.token.at,std::move(operands));here().ops.back().incoming=std::move(incoming);return id;
            }
            auto b=expression(*e.children[1]);int id=op("binary:"+e.op,e.type,e.token.at,{a,b});here().ops.back().wraps=e.wraps;return id;
        }
        case Expr::assign:case Expr::update: {
            auto& target=*e.children[0];
            if(target.kind==Expr::name && static_symbols.contains(target.symbol)){
                auto load=[&](){int id=op("static-load",target.type,target.token.at);here().ops.back().external_name=static_names.at(target.symbol);return id;};
                int value;
                if(e.kind==Expr::update){int old=load();int one=op("constant",Type::integer,e.token.at);here().ops.back().literal=1;value=op("binary:"+std::string(e.op=="++" ? "+" : "-"),target.type,e.token.at,{old,one});}
                else if(e.op=="=")value=expression(*e.children[1]);
                else {int old=load();int right=expression(*e.children[1]);value=op("binary:"+e.op.substr(0,e.op.size()-1),target.type,e.token.at,{old,right});}
                int id=op("static-store",Type::unit,e.token.at,{value});here().ops.back().external_name=static_names.at(target.symbol);return id;
            }auto stored=target.type;int direct=direct_place(target);int pointer=direct<0 ? place_pointer(target,true) : -1;if(pointer>=0)remember_value(pointer,Type::reference(stored,Type::Borrow::exclusive));
            int old=-1;if(e.kind==Expr::update || e.op!="=")old=pointer>=0 ? op("reference-read",stored,target.token.at,{pointer}) : op("copy-read",stored,target.token.at,{},direct);
            auto arithmetic=native_integer_operator_type(stored);
            int value;
            if(old<0)value=expression(*e.children[1]);
            else {
                auto left=integer_conversion(old,stored,arithmetic,e.token.at);
                int right;
                std::string operation;
                if(e.kind==Expr::update){
                    right=op("constant",arithmetic,e.token.at);here().ops.back().literal=1;
                    operation=e.op=="++" ? "+" : "-";
                }else{
                    right=expression(*e.children[1]);
                    operation=e.op.substr(0,e.op.size()-1);
                }
                value=op("binary:"+operation,arithmetic,e.token.at,{left,right});
                here().ops.back().wraps=e.wraps;
                value=integer_conversion(value,arithmetic,stored,e.token.at,e.wraps);
            }
            if(pointer>=0)op("reference-write",Type::unit,e.token.at,{pointer,value});else op("write",Type::unit,e.token.at,{value},direct);
            if(e.type==Type::unit)return op("unit",Type::unit,e.token.at);return e.postfix ? old : value;
        }
        }
        fail("AI3100","Unknown expression in native storage IR.",e.token.at);
    }
    void statement(const Stmt& s) {
        if(s.kind==Stmt::label){
            auto found=label_blocks.find(s.symbol);if(found==label_blocks.end())fail("AI3100","Unbound native label block.",s.token.at);
            if(here().end.empty())jump(found->second,s.token.at);
            current=found->second;return;
        }
        if(!here().end.empty())return;
        switch(s.kind) {
        case Stmt::block:
            scopes.emplace_back();for(auto& c:s.children)statement(*c);if(here().end.empty())cleanup(scopes.size()-1,s.end);scopes.pop_back();break;
        case Stmt::local: {
            if(s.static_binding){scopes.back().push_back(s.symbol);break;}
            int value=s.expr ? expression(*s.expr) : -1;
            add_place(s.symbol,s.type,s.token.at,false,s.mutable_,s.constant);scopes.back().push_back(s.symbol);
            op("storage-live",Type::unit,s.token.at,{},s.symbol);if(value>=0)op("write",Type::unit,s.token.at,{value},s.symbol);break;
        }
        case Stmt::expression:if(s.expr){auto value=expression(*s.expr);if(!s.expr->type.is_copy())op(s.expr->type.is_reference() ? "end-reference" : "drop-value",Type::unit,s.expr->token.at,{value});}break;
        case Stmt::failure:fail("AI3100","Unbound failure statement in native storage IR.",s.token.at);
        case Stmt::result: {
            int value=s.expr ? expression(*s.expr) : -1;if(s.expr && s.expr->type==Type::unit)value=-1;cleanup(0,s.token.at);here().end="return";here().result=value;here().at=s.token.at;break;
        }
        case Stmt::branch: {
            auto value=expression(*s.expr);int yes=block(),no=block(),join=block();branch(value,yes,no,*s.expr);
            current=yes;scoped(*s.children[0]);if(here().end.empty())jump(join,s.token.at);
            current=no;if(s.children.size()==2)scoped(*s.children[1]);if(here().end.empty())jump(join,s.token.at);
            current=join;break;
        }
        case Stmt::loop:case Stmt::for_loop: {
            scopes.emplace_back();if(s.kind==Stmt::for_loop)statement(*s.children[0]);
            int condition=block(),body=block(),update=block(),end=block();jump(condition,s.token.at);current=condition;
            if(s.expr){auto value=expression(*s.expr);branch(value,body,end,*s.expr);}else jump(body,s.token.at);
            loops.push_back({end,update,scopes.size()});current=body;scoped(*s.children.back());if(here().end.empty())jump(update,s.token.at);loops.pop_back();
            current=update;if(s.update){auto value=expression(*s.update);if(!s.update->type.is_copy())op(s.update->type.is_reference() ? "end-reference" : "drop-value",Type::unit,s.update->token.at,{value});}jump(condition,s.token.at);current=end;
            cleanup(scopes.size()-1,s.end);scopes.pop_back();break;
        }
        case Stmt::do_loop: {
            scopes.emplace_back();int body=block(),condition=block(),end=block();jump(body,s.token.at);
            loops.push_back({end,condition,scopes.size()});current=body;scoped(*s.children[0]);if(here().end.empty())jump(condition,s.token.at);loops.pop_back();
            current=condition;auto value=expression(*s.expr);branch(value,body,end,*s.expr);current=end;
            cleanup(scopes.size()-1,s.end);scopes.pop_back();break;
        }
        case Stmt::stop:case Stmt::next:
            cleanup(loops.back().scope_depth,s.token.at);jump(s.kind==Stmt::stop ? loops.back().stop : loops.back().next,s.token.at);break;
        case Stmt::goto_jump: {
            auto found=label_blocks.find(s.symbol);if(found==label_blocks.end())fail("AI3100","Unbound native goto target.",s.token.at);
            jump(found->second,s.token.at);current=block();break;
        }
        }
    }
public:
    IrFunction run(const Function& source) {
        for(auto& st:source.statics){f.statics.push_back({st.name,st.type,st.value});static_symbols.insert(st.symbol);static_names.emplace(st.symbol,st.name);}
        f.products=source.products;f.unwind=source.unwind;f.fault_mode_location=source.fault_mode_location;f.fallible=source.fallible;f.success_type=source.fallible ? source.success_type : source.type;f.error_type=source.error_type;f.failure_declaration=source.failure_declaration;f.declaration=source.token.at;f.id=source.symbol;f.type=source.type;f.entry=canonical(source.token.text)=="main";block();scopes.emplace_back();
        for(auto& p:source.parameters)next_place=std::max(next_place,p.symbol+1);
        auto scan=[&](auto&& self,const Stmt& s)->void{if(s.kind==Stmt::local)next_place=std::max(next_place,s.symbol+1);for(auto& child:s.children)self(self,*child);};scan(scan,*source.body);
        auto labels=[&](auto&& self,const Stmt& s)->void{if(s.kind==Stmt::label)label_blocks[s.symbol]=block();for(auto& child:s.children)self(self,*child);};labels(labels,*source.body);
        for(auto& p:source.parameters){add_place(p.symbol,p.type,p.token.at,true,p.mutable_parameter,false);f.parameters.push_back(p.symbol);scopes.back().push_back(p.symbol);op("parameter",Type::unit,p.token.at,{},p.symbol);}
        statement(*source.body);if(here().end.empty()){cleanup(0,source.body->end);here().end=source.type==Type::unit ? "return" : "unreachable";here().at=source.body->end;}
        scopes.pop_back();f.unwind_plan=native_unwind_plan(f);return std::move(f);
    }
};
// Bitsets bound the memory used by joins; an explicit work budget bounds analysis.
using IrBits=std::vector<std::uint64_t>;
#include "native_match_verifier.inc"
class StorageVerifier {
    std::size_t work=0,facts=0;
    void charge(std::size_t n,Location at={}){if(n>16000000-work)fail("AI3100","Native storage analysis work limit exceeded.",at);work+=n;}
    static void put(IrBits& s,std::size_t n,bool value){auto bit=std::uint64_t{1}<<(n%64);if(value)s[n/64]|=bit;else s[n/64]&=~bit;}
    static bool has(const IrBits& s,std::size_t n){return (s[n/64]>>(n%64)&1)!=0;}
    std::set<int> expose(const IrBits& s,const IrFunction& f){charge(f.places.size());std::set<int> result;for(std::size_t i=0;i<f.places.size();++i)if(has(s,i)){if(facts++>=16000000)fail("AI3100","Native storage proof fact limit exceeded.",{});result.insert(f.places[i].id);}return result;}
    [[noreturn]] static void invalid(Location at){fail("AI3100","Native typed storage IR invariant failed.",at);}
public:
    void run(std::vector<IrFunction>& functions) {
        if(functions.size()>native_function_limit)invalid({});std::map<int,const IrFunction*> signatures;std::size_t entries=0;for(auto& f:functions){if(!signatures.emplace(f.id,&f).second)invalid({});if(f.entry)++entries;if(f.fallible){if(!f.type.is_core_carrier("Result") || !f.success_type.valid() || !f.error_type.valid() || Type::from_shape(f.products,f.type.definition().arguments[0])!=f.success_type || Type::from_shape(f.products,f.type.definition().arguments[1])!=f.error_type || f.failure_declaration.length==0)invalid(f.declaration);}else if(f.failure_declaration.length)invalid(f.declaration);}if(entries!=1)invalid({});
        std::shared_ptr<const NativeProductTable> product_schema;std::set<const NativeProductTable*> verified_schemas;
        for(auto& f:functions)if(f.products && verified_schemas.insert(f.products.get()).second)verify_native_product_table(f.products);
        for(auto& f:functions)if(f.products && (!f.products->definitions.empty() || !f.products->generics.empty())){if(!f.products->sealed || (product_schema && product_schema!=f.products))invalid({});product_schema=f.products;}
        auto input_origin=[&](Location at){charge(1,{});if(at.origin!=SourceOrigin::input)fail("AI3100","Native executable IR cannot claim a declaration source origin.",{});};
        for(auto& f:functions){
            input_origin(f.declaration);input_origin(f.failure_declaration);input_origin(f.fault_mode_location);for(auto& p:f.places)input_origin(p.at);
            for(auto& b:f.blocks){input_origin(b.at);for(auto& o:b.ops)input_origin(o.at);}
            for(auto& m:f.matches){input_origin(m.at);for(auto& a:m.sources){input_origin(a.at);input_origin(a.guard_at);input_origin(a.value_at);}for(auto& g:m.groups)for(auto& a:g.arms){input_origin(a.at);input_origin(a.guard_at);input_origin(a.value_at);}}
        }
        for(auto& f:functions){
            if(f.type.has_product_root() && f.type.products()!=f.products)invalid({});for(auto& p:f.places)if(p.type.has_product_root() && p.type.products()!=f.products)invalid(p.at);for(auto& b:f.blocks)for(auto& o:b.ops)if(o.type.has_product_root() && o.type.products()!=f.products)invalid(o.at);
            const auto count=f.blocks.size(),words=(f.places.size()+63)/64;
            if(f.id<0 || f.id>=native_function_limit || count==0 || count>50000 || !f.type.local_supported() || (f.entry && (((f.fallible ? f.success_type : f.type)!=Type::integer && (f.fallible ? f.success_type : f.type)!=Type::boolean) || !f.parameters.empty())))invalid({});
            if(words && count>2000000/words)fail("AI3100","Native storage analysis workspace limit exceeded.",{});
            std::map<int,std::size_t> places;std::set<int> parameters(f.parameters.begin(),f.parameters.end());if(f.parameters.size()>128 || parameters.size()!=f.parameters.size())invalid({});
            for(std::size_t i=0;i<f.places.size();++i){auto& p=f.places[i];if(p.id<0 || !p.type.local_supported(false) || (p.constant && p.mutable_) || p.parameter!=parameters.contains(p.id) || !places.emplace(p.id,i).second)invalid(p.at);}
            IrPlaceGraph projections(f.places);std::map<int,int> cause_sources;for(auto& match:f.matches)if(match.propagation)for(auto& group:match.groups)for(auto& arm:group.arms)if(arm.source==1 && !cause_sources.emplace(arm.value,match.subject).second)invalid(match.at);
            std::map<int,const IrOp*> values;std::map<int,int> value_blocks;std::map<int,std::size_t> value_positions;
            std::vector<std::vector<int>> predecessors(count);
            for(auto& b:f.blocks){
                b.reachable=false;b.initialized_in.clear();b.initialized_out.clear();b.live_in.clear();b.live_out.clear();
                b.maybe_initialized_in.clear();b.maybe_initialized_out.clear();b.active_in.clear();b.active_out.clear();b.maybe_active_in.clear();b.maybe_active_out.clear();
                if(b.id<0 || static_cast<std::size_t>(b.id)>=count || &f.blocks[static_cast<std::size_t>(b.id)]!=&b)invalid(b.at);
                for(int target:b.successors){if(target<0 || static_cast<std::size_t>(target)>=count)invalid(b.at);predecessors[static_cast<std::size_t>(target)].push_back(b.id);}
                if((b.end=="goto" && b.successors.size()!=1) || (b.end=="branch" && (b.successors.empty() || b.successors.size()>2)) || ((b.end=="return" || b.end=="unreachable" || b.end=="match-fault") && !b.successors.empty()) || (b.end!="goto" && b.end!="branch" && b.end!="return" && b.end!="unreachable" && b.end!="match-fault"))invalid(b.at);
                if((b.end!="branch" && b.condition!=-1) || (b.end!="return" && b.result!=-1))invalid(b.at);
                for(std::size_t i=0;i<b.ops.size();++i){auto& o=b.ops[i];auto cause=cause_sources.find(o.id);if(o.cause_place!=(cause==cause_sources.end() ? -1 : cause->second))invalid(o.at);if(o.id<0 || o.id>=400000 || !values.emplace(o.id,&o).second)invalid(o.at);value_blocks.emplace(o.id,b.id);value_positions.emplace(o.id,i);}
            }
            auto value_type=[&](int id,Location at){auto it=values.find(id);if(it==values.end())invalid(at);return it->second->type;};
            for(int parameter:f.parameters){std::size_t declarations=0;for(auto& o:f.blocks[0].ops)if(o.kind=="parameter" && o.place==parameter)++declarations;if(declarations!=1)invalid({});}
            auto access_alias=[&](int id){if(!values.contains(id))invalid({});auto& kind=values.at(id)->kind;return kind=="reference-use" || kind=="reference-field" || kind.starts_with("reference-follow-");};
            for(auto& b:f.blocks){
                if(b.result>=0 && access_alias(b.result))invalid(b.at);
                for(auto& use:b.ops)for(std::size_t i=0;i<use.operands.size();++i)if(access_alias(use.operands[i]) && !(i==0 && (use.kind.starts_with("reborrow-") || use.kind=="reference-field" || use.kind.starts_with("reference-follow-") || use.kind=="reference-read" || use.kind=="reference-write" || (use.kind=="call" && use.function<=-6 && use.function>=-8))))invalid(use.at);
                if(b.end=="branch" && value_type(b.condition,b.at)!=Type::boolean)invalid(b.at);
                if(b.end=="branch" && b.successors.size()==1 && values.at(b.condition)->kind!="constant")invalid(b.at);
                if(b.end=="return" && (b.result<0 ? Type::unit : value_type(b.result,b.at))!=f.type)invalid(b.at);
                for(auto& o:b.ops){
                    if(!o.type.local_supported())invalid(o.at);
                    for(auto v:o.operands)if(value_type(v,o.at)==Type::unit && o.kind!="variant-pack")invalid(o.at);
                    auto place=places.find(o.place);
                    bool storage=o.kind=="variant-borrow" || o.kind=="variant-tag" || o.kind=="parameter" || o.kind=="storage-live" || o.kind=="storage-dead" || o.kind=="write" || (o.kind=="copy-read" || o.kind=="move" || o.kind=="reference-use" || o.kind=="borrow-shared" || o.kind=="borrow-exclusive");
                    if((o.field!=-1 && o.kind!="aggregate-extract" && o.kind!="reference-field" && o.kind!="variant-pack" && o.kind!="variant-borrow" && o.kind!="object-box" && o.kind!="object-unbox" && o.kind!="stack-alloc" && o.kind!="class-new" && o.kind!="class-field-load" && o.kind!="class-field-store" && o.kind!="class-upcast" && o.kind!="class-tag") || (!storage && o.place!=-1) || (o.kind!="call" && o.kind!="class-new" && o.kind!="class-field-load" && o.kind!="class-field-store" && o.kind!="class-upcast" && o.kind!="class-tag" && o.function!=-1) || (o.kind!="phi" && !o.incoming.empty()))invalid(o.at);
                    if(o.kind=="variant-borrow" || o.kind=="variant-tag" || o.kind=="parameter" || o.kind=="storage-live" || o.kind=="storage-dead" || o.kind=="write" || (o.kind=="copy-read" || o.kind=="move" || o.kind=="reference-use" || o.kind=="borrow-shared" || o.kind=="borrow-exclusive")){
                        if(place==places.end())invalid(o.at);auto& p=f.places[place->second];
                        if(o.kind=="variant-tag"){if((!p.type.is_payload_union() && !(p.type.is_reference() && p.type.pointee().is_union())) || o.type!=Type::integer || !o.operands.empty())invalid(o.at);}else if(o.kind=="variant-borrow"){if(!p.type.is_reference() || !p.type.pointee().is_payload_union() || o.field<0 || static_cast<std::size_t>(o.field)>=p.type.pointee().definition().fields.size() || !o.operands.empty() || o.type!=Type::reference(p.type.pointee().field_type(static_cast<std::size_t>(o.field)),p.type.borrow_kind()))invalid(o.at);}else if((o.kind=="copy-read" || o.kind=="move" || o.kind=="reference-use" || o.kind=="borrow-shared" || o.kind=="borrow-exclusive")){if(!o.operands.empty())invalid(o.at);if(o.kind=="borrow-shared" || o.kind=="borrow-exclusive"){if(o.type!=Type::reference(p.type,o.kind=="borrow-exclusive" ? Type::Borrow::exclusive : Type::Borrow::shared) || (o.kind=="borrow-exclusive" && !p.mutable_))invalid(o.at);}else {if(o.type!=p.type)invalid(o.at);if(o.kind=="reference-use"){if(!p.type.is_reference())invalid(o.at);}else if((o.kind=="move")==p.type.is_copy())invalid(o.at);}}
                        else if(o.type!=Type::unit || (o.kind=="write" ? o.operands.size()!=1 || value_type(o.operands[0],o.at)!=p.type : !o.operands.empty()))invalid(o.at);
                        if(o.kind=="parameter" && (!p.parameter || b.id!=0 || std::find(f.parameters.begin(),f.parameters.end(),p.id)==f.parameters.end()))invalid(o.at);
                        if((o.kind=="storage-live" || o.kind=="storage-dead") && p.parent!=-1)invalid(o.at);
                        if(o.kind=="storage-live" && p.parameter)invalid(o.at);
                    }else if(o.kind=="variant-pack"){
                        if(!o.type.is_payload_union() || o.field<0 || static_cast<std::size_t>(o.field)>=o.type.definition().cases.size())invalid(o.at);auto& c=o.type.definition().cases[static_cast<std::size_t>(o.field)];if(o.operands.size()!=c.payload.size())invalid(o.at);for(std::size_t i=0;i<c.payload.size();++i)if(value_type(o.operands[i],o.at)!=Type::from_shape(f.products,c.payload[i].type))invalid(o.at);
                    }else if(o.kind=="aggregate-pack"){
                        if(!o.type.is_product() || o.operands.size()!=o.type.definition().fields.size())invalid(o.at);for(std::size_t i=0;i<o.operands.size();++i)if(value_type(o.operands[i],o.at)!=o.type.field_type(i))invalid(o.at);
                    }else if(o.kind=="aggregate-extract"){
                        if(o.operands.size()!=1 || o.field<0)invalid(o.at);auto parent=value_type(o.operands[0],o.at);if(!parent.is_product() || static_cast<std::size_t>(o.field)>=parent.definition().fields.size() || o.type!=parent.field_type(static_cast<std::size_t>(o.field)))invalid(o.at);
                    }else if(o.kind=="reference-field"){
                        if(o.operands.size()!=1 || o.field<0)invalid(o.at);auto parent=value_type(o.operands[0],o.at);if(!parent.is_reference() || !parent.pointee().is_product() || !o.type.is_reference() || static_cast<std::size_t>(o.field)>=parent.pointee().definition().fields.size() || o.type.pointee()!=parent.pointee().field_type(static_cast<std::size_t>(o.field)) || (o.type.borrow_kind()==Type::Borrow::exclusive && parent.borrow_kind()!=Type::Borrow::exclusive))invalid(o.at);
                    }else if(o.kind=="unit"){if(o.type!=Type::unit || !o.operands.empty())invalid(o.at);
                    }else if(o.kind=="end-reference"){if(o.type!=Type::unit || o.operands.size()!=1 || !value_type(o.operands[0],o.at).is_reference() || value_type(o.operands[0],o.at).is_copy())invalid(o.at);
                    }else if(o.kind=="reborrow-shared" || o.kind=="reborrow-exclusive"){
                        if(o.operands.size()!=1)invalid(o.at);auto parent=value_type(o.operands[0],o.at);if(!parent.is_reference() || o.type!=Type::reference(parent.pointee(),o.kind=="reborrow-exclusive" ? Type::Borrow::exclusive : Type::Borrow::shared) || (o.kind=="reborrow-exclusive" && parent.borrow_kind()!=Type::Borrow::exclusive))invalid(o.at);
                    }else if(o.kind=="reference-follow-shared" || o.kind=="reference-follow-exclusive"){
                        if(o.operands.size()!=1)invalid(o.at);auto parent=value_type(o.operands[0],o.at);if(!parent.is_reference() || !parent.pointee().is_reference())invalid(o.at);auto inner=parent.pointee();if(o.kind=="reference-follow-exclusive"){if(parent.borrow_kind()!=Type::Borrow::exclusive || o.type!=inner)invalid(o.at);}else if(o.type!=Type::reference(inner.pointee(),Type::Borrow::shared))invalid(o.at);
                    }else if(o.kind=="reference-read" || o.kind=="reference-write"){
                        bool write=o.kind=="reference-write";if(o.operands.size()!=(write ? 2u : 1u))invalid(o.at);auto pointer=value_type(o.operands[0],o.at);if(!pointer.is_reference())invalid(o.at);if(write){if(pointer.borrow_kind()!=Type::Borrow::exclusive || o.type!=Type::unit || value_type(o.operands[1],o.at)!=pointer.pointee())invalid(o.at);}else if(o.type!=pointer.pointee() || !o.type.is_copy())invalid(o.at);
                    }else if(o.kind=="constant"){if((!o.type.is_scalar() && !o.type.is_union()) || o.type.is_payload_union() || (o.type.is_union() && (o.literal<0 || static_cast<std::uint64_t>(o.literal)>=o.type.definition().cases.size())) || !o.operands.empty() || (o.type==Type::boolean && o.literal!=0 && o.literal!=1) || (o.type!=Type::long_integer && (o.type.is_uint32() ? (o.literal<0 || o.literal>4294967295LL) : o.type.is_ulong64() || o.type.is_float32() || o.type.is_float64() || o.type.is_enum() || o.type.is_decimal() ? false : (o.literal<INT32_MIN || o.literal>INT32_MAX))))invalid(o.at);
                        if(auto small=o.type.narrow_integer();small && (o.literal<small->minimum || o.literal>small->maximum))invalid(o.at);
                    }
                    else if(o.kind=="integer-convert" || o.kind=="integer-widen" || o.kind=="integer-narrow" || o.kind=="integer-u32" || o.kind=="integer-u64" || o.kind=="integer-float" || o.kind=="float-integer" || o.kind=="integer-double" || o.kind=="double-integer" || o.kind=="float-double" || o.kind=="double-float" || o.kind=="integer-enum" || o.kind=="enum-integer" || o.kind=="integer-decimal" || o.kind=="float-decimal" || o.kind=="double-decimal" || o.kind=="decimal-integer" || o.kind=="decimal-float" || o.kind=="decimal-double"){
                        if(o.operands.size()!=1 || o.place!=-1 || o.function!=-1 || o.field!=-1 ||
                           o.cause_place!=-1 || o.literal!=0 || !o.incoming.empty())invalid(o.at);
                        auto source=value_type(o.operands[0],o.at);
                        if(o.kind=="integer-convert"){
                            if(!((source==Type::integer && o.type==Type::long_integer && !o.wraps) ||
                                 (source==Type::long_integer && o.type==Type::integer)))invalid(o.at);
                        }else if(o.kind=="integer-widen"){
                            if(o.wraps || (!(source.narrow_integer() || source.is_uint32()) && !(o.type.narrow_integer() || o.type.is_uint32())) ||
                               !native_integer_widens(source,o.type))invalid(o.at);
                        }else if(o.kind=="integer-u32"){
                            const bool target_uint=o.type.is_uint32();
                            const bool source_uint=source.is_uint32();
                            const bool signed_narrow=source.narrow_integer() && source.narrow_integer()->minimum<0;
                            if(!((target_uint && (source==Type::integer || source==Type::long_integer || signed_narrow)) ||
                                 (source_uint && o.type==Type::integer)))invalid(o.at);
                        }else if(o.kind=="integer-u64"){
                            const bool target_ulong=o.type.is_ulong64();
                            const bool source_ulong=source.is_ulong64();
                            const bool signed_narrow=source.narrow_integer() && source.narrow_integer()->minimum<0;
                            if(!((target_ulong && (source==Type::integer || source==Type::long_integer || signed_narrow)) ||
                                 (source_ulong && (o.type==Type::integer || o.type==Type::long_integer || o.type.is_uint32()))))invalid(o.at);
                        }else if(o.kind=="integer-float"){
                            if(!source.is_integer() || !o.type.is_float32() || o.wraps)invalid(o.at);
                        }else if(o.kind=="integer-decimal"){
                            if(!source.is_integer() || !o.type.is_decimal())invalid(o.at);
                        }else if(o.kind=="float-decimal"){
                            if(!source.is_float32() || !o.type.is_decimal())invalid(o.at);
                        }else if(o.kind=="double-decimal"){
                            if(!source.is_float64() || !o.type.is_decimal())invalid(o.at);
                        }else if(o.kind=="decimal-integer"){
                            if(!source.is_decimal() || !o.type.is_integer())invalid(o.at);
                        }else if(o.kind=="decimal-float"){
                            if(!source.is_decimal() || !o.type.is_float32())invalid(o.at);
                        }else if(o.kind=="decimal-double"){
                            if(!source.is_decimal() || !o.type.is_float64())invalid(o.at);
                        }else if(o.kind=="integer-enum"){
                            if(!source.is_integer() || !o.type.is_enum())invalid(o.at);
                        }else if(o.kind=="enum-integer"){
                            if(!source.is_enum() || !o.type.is_integer())invalid(o.at);
                        }else if(o.kind=="float-integer"){
                            if(!source.is_float32() || !o.type.is_integer() || o.type.is_float32())invalid(o.at);
                        }else if(o.kind=="integer-double"){
                            if(!source.is_integer() || !o.type.is_float64() || o.wraps)invalid(o.at);
                        }else if(o.kind=="double-integer"){
                            if(!source.is_float64() || !o.type.is_integer() || o.type.is_float64())invalid(o.at);
                        }else if(o.kind=="float-double"){
                            if(!source.is_float32() || !o.type.is_float64() || o.wraps)invalid(o.at);
                        }else if(o.kind=="double-float"){
                            if(!source.is_float64() || !o.type.is_float32())invalid(o.at);
                        }else if(!o.type.narrow_integer() || !source.is_integer() || source==o.type ||
                                 native_integer_widens(source,o.type))invalid(o.at);
                    }
                    else if(o.kind=="phi"){
                        if(o.type!=Type::boolean || o.operands.empty() || o.operands.size()>2 || o.incoming.size()!=o.operands.size())invalid(o.at);
                        std::set<int> expected(predecessors[static_cast<std::size_t>(b.id)].begin(),predecessors[static_cast<std::size_t>(b.id)].end()),actual(o.incoming.begin(),o.incoming.end());if(expected!=actual || actual.size()!=o.incoming.size())invalid(o.at);
                        for(std::size_t i=0;i<o.operands.size();++i)if(value_type(o.operands[i],o.at)!=o.type)invalid(o.at);
                    }else if(o.kind=="external-call"){
                        if(o.external_name.empty() || o.place!=-1 || o.function!=-1 || o.field!=-1 || o.cause_place!=-1 || !o.incoming.empty() || o.operands.size()>8)invalid(o.at);
                        if(!(o.type==Type::unit || (o.type.local_supported(false) && !o.type.is_reference() && !o.type.is_product() && !o.type.is_union() && o.type!=Type::buffer && !o.type.is_type_token())))invalid(o.at);
                        for(int operand:o.operands){auto type=value_type(operand,o.at);if(type.is_reference() || type.is_product() || type.is_union() || type==Type::buffer || type==Type::unit || type.is_type_token() || !type.local_supported(false))invalid(o.at);}
                    }else if(o.kind=="object-box"){
                        if(o.place!=-1 || o.function!=-1 || o.field<0 || o.operands.size()!=1 || !o.incoming.empty() || (o.type!=Type::object && o.type!=Type::dynamic))invalid(o.at);
                        auto source=value_type(o.operands[0],o.at);if(!(source.is_scalar() || source==Type::string))invalid(o.at);
                    }else if(o.kind=="object-unbox"){
                        if(o.place!=-1 || o.function!=-1 || o.field<0 || o.operands.size()!=1 || !o.incoming.empty())invalid(o.at);
                        if(!(o.type.is_scalar() || o.type==Type::string) || (value_type(o.operands[0],o.at)!=Type::object && value_type(o.operands[0],o.at)!=Type::dynamic))invalid(o.at);
                    }else if(o.kind=="as-string"){
                        if(o.place!=-1 || o.function!=-1 || o.field!=-1 || o.operands.size()!=1 || !o.incoming.empty() || o.type!=Type::string || value_type(o.operands[0],o.at)!=Type::object)invalid(o.at);
                    }else if(o.kind=="args-value"){
                        if(o.place!=-1 || o.function!=-1 || o.field!=-1 || !o.operands.empty() || !o.incoming.empty() || o.type!=Type::args)invalid(o.at);
                    }else if(o.kind=="args-length"){
                        if(o.place!=-1 || o.function!=-1 || o.field!=-1 || o.operands.size()!=1 || !o.incoming.empty() || o.type!=Type::integer || value_type(o.operands[0],o.at)!=Type::args)invalid(o.at);
                    }else if(o.kind=="args-at"){
                        if(o.place!=-1 || o.function!=-1 || o.field!=-1 || o.operands.size()!=2 || !o.incoming.empty() || o.type!=Type::string)invalid(o.at);
                        if(value_type(o.operands[0],o.at)!=Type::args || value_type(o.operands[1],o.at)!=Type::integer)invalid(o.at);
                    }else if(o.kind=="string-literal"){
                        if(o.external_name.empty() || o.place!=-1 || o.function!=-1 || o.field!=-1 || !o.operands.empty() || !o.incoming.empty() || o.type!=Type::string)invalid(o.at);
                    }else if(o.kind=="null-string"){
                        if(o.place!=-1 || o.function!=-1 || o.field!=-1 || !o.operands.empty() || !o.incoming.empty() || o.type!=Type::string)invalid(o.at);
                    }else if(o.kind=="static-load"){
                        if(o.external_name.empty() || o.place!=-1 || o.function!=-1 || o.field!=-1 || !o.operands.empty() || !o.incoming.empty())invalid(o.at);
                        if(!(o.type.is_integer() || o.type.is_char() || o.type==Type::boolean || o.type.is_enum()))invalid(o.at);
                    }else if(o.kind=="static-store"){
                        if(o.external_name.empty() || o.place!=-1 || o.function!=-1 || o.field!=-1 || o.operands.size()!=1 || !o.incoming.empty() || o.type!=Type::unit)invalid(o.at);
                        auto type=value_type(o.operands[0],o.at);if(!(type.is_integer() || type.is_char() || type==Type::boolean || type.is_enum()))invalid(o.at);
                    }else if(o.kind=="call"){
                        if(o.function<0){std::vector<Type> params;Type result;
                            if(o.function<=-6 && o.function>=-8){
                                if(o.operands.empty())invalid(o.at);auto pointer=value_type(o.operands[0],o.at);if(!pointer.is_reference() || pointer.pointee()!=Type::buffer || (o.function==-8 && pointer.borrow_kind()!=Type::Borrow::exclusive))invalid(o.at);params={pointer};if(o.function!=-6)params.push_back(Type::integer);if(o.function==-8)params.push_back(Type::integer);result=o.function==-8 ? Type::unit : Type::integer;
                            }else if(o.function==-2){params={Type::integer};result=Type::buffer;}else if(o.function==-3){params={Type::buffer,Type::integer,Type::integer};result=Type::buffer;}else if(o.function==-4){params={Type::buffer,Type::integer};result=Type::integer;}else if(o.function==-5){params={Type::buffer};result=Type::integer;}else if(o.function==-9){params={Type::string};result=Type::buffer;}else if(o.function==-10){params={Type::string,Type::buffer};result=Type::unit;}else if(o.function==-11){params={Type::string};result=Type::buffer;}else if(o.function==-12){params={Type::integer};result=Type::buffer;}else invalid(o.at);
                            if(o.type!=result || o.operands.size()!=params.size())invalid(o.at);for(std::size_t i=0;i<params.size();++i)if(value_type(o.operands[i],o.at)!=params[i])invalid(o.at);continue;
                        }
                        auto target=signatures.find(o.function);if(target==signatures.end() || target->second->type!=o.type || target->second->parameters.size()!=o.operands.size())invalid(o.at);
                        for(std::size_t i=0;i<o.operands.size();++i){auto& callee=*target->second;auto it=std::find_if(callee.places.begin(),callee.places.end(),[&](auto& p){return p.id==callee.parameters[i];});if(it==callee.places.end() || it->type!=value_type(o.operands[i],o.at))invalid(o.at);}
                    }else if(o.kind=="drop-value"){if(o.type!=Type::unit || o.operands.size()!=1 || (value_type(o.operands[0],o.at).is_copy() || value_type(o.operands[0],o.at).is_reference()))invalid(o.at);
                    }else if(o.kind.starts_with("unary:")){
                        if(o.kind!="unary:+" && o.kind!="unary:-" && o.kind!="unary:!" && o.kind!="unary:~")invalid(o.at);
                        const bool allowed=o.kind=="unary:!" ? o.type==Type::boolean :
                            (o.type.is_signed_integer() || ((o.type.is_uint32() || o.type.is_ulong64()) && (o.kind=="unary:+" || o.kind=="unary:~"))
                             || ((o.type.is_float32() || o.type.is_float64() || o.type.is_decimal()) && (o.kind=="unary:+" || o.kind=="unary:-")));
                        if(o.operands.size()!=1 || value_type(o.operands[0],o.at)!=o.type || !allowed)invalid(o.at);
                    }else if(o.kind.starts_with("binary:")){
                        if(o.operands.size()!=2)invalid(o.at);auto left=value_type(o.operands[0],o.at),right=value_type(o.operands[1],o.at);auto operation=o.kind.substr(7);
                        bool compare=operation=="==" || operation=="!=" || operation=="<" || operation==">" || operation=="<=" || operation==">=";
                        static const std::set<std::string> operations={"+","-","*","/","%","&","|","^","<<",">>",">>>","==","!=","<",">","<=",">="};if(!operations.contains(operation))invalid(o.at);
                        if(native_integer_shift(operation)){
                            if(!(left.is_signed_integer() || left.is_uint32() || left.is_ulong64()) || right!=Type::integer || o.type!=left)invalid(o.at);
                            continue;
                        }
                        if(left.is_decimal() || right.is_decimal()){
                            if(right!=left)invalid(o.at);
                            if(compare){if(o.type!=Type::boolean)invalid(o.at);}
                            else if(o.type!=left || !(operation=="+" || operation=="-" || operation=="*" || operation=="/"))invalid(o.at);
                            continue;
                        }
                        if(left.is_enum() || right.is_enum()){
                            if(right!=left || o.type!=Type::boolean || !(operation=="==" || operation=="!=" || operation=="<" || operation==">" || operation=="<=" || operation==">="))invalid(o.at);
                            continue;
                        }
                        if(left==Type::long_integer){
                            if(right!=left || (compare ? o.type!=Type::boolean : o.type!=left))invalid(o.at);
                            continue;
                        }
                        if(left.narrow_integer())invalid(o.at);
                        if((!left.is_scalar() && !(left.is_class() && (operation=="==" || operation=="!=")) && !(left.is_union() && !left.is_payload_union() && (operation=="==" || operation=="!=")) && !(left==Type::string && (operation=="==" || operation=="!=" || operation=="+")) && !(left==Type::object && (operation=="==" || operation=="!=")) && !(left==Type::dynamic)) || left!=right || (compare ? o.type!=Type::boolean : o.type!=left) || ((operation!="&" && operation!="|" && operation!="^" && operation!="==" && operation!="!=" && !(left==Type::string && operation=="+") && !(left==Type::object) && !(left==Type::dynamic)) && left!=Type::integer && !left.is_uint32() && !left.is_ulong64() && !left.is_float32() && !left.is_float64()))invalid(o.at);
                    }else if(o.kind=="stack-alloc"){
                        if(o.type!=Type::buffer || o.place!=-1 || o.field<=0 || o.field>4096 || !o.operands.empty())invalid(o.at);
                    }else if(o.kind=="class-tag"){
                        if(o.type!=Type::integer || o.place!=-1 || o.field!=-1 || o.operands.size()!=1 || !o.incoming.empty() || !value_type(o.operands[0],o.at).is_class())invalid(o.at);
                    }else if(o.kind=="class-upcast"){
                        if(!o.type.is_class() || o.place!=-1 || o.field!=-1 || o.operands.size()!=1 || !o.incoming.empty() || !value_type(o.operands[0],o.at).is_class())invalid(o.at);
                    }else if(o.kind=="class-new"){
                        if(!o.type.is_class() || o.place!=-1 || o.function<0 || o.field!=-1 || !o.incoming.empty())invalid(o.at);auto& definition=o.type.definition();if(o.operands.size()!=definition.fields.size())invalid(o.at);for(std::size_t i=0;i<o.operands.size();++i)if(value_type(o.operands[i],o.at)!=o.type.field_type(i))invalid(o.at);
                    }else if(o.kind=="class-field-load"){
                        if(o.place!=-1 || o.function<0 || o.field<0 || o.operands.size()!=1 || !o.incoming.empty())invalid(o.at);auto type=Type::from_shape(f.products,{Type::class_ref,o.function,{}},o.at);if(static_cast<std::size_t>(o.field)>=type.definition().fields.size() || o.type!=type.field_type(static_cast<std::size_t>(o.field)) || value_type(o.operands[0],o.at)!=type)invalid(o.at);
                    }else if(o.kind=="class-field-store"){
                        if(o.type!=Type::unit || o.place!=-1 || o.function<0 || o.field<0 || o.operands.size()!=2 || !o.incoming.empty())invalid(o.at);auto type=Type::from_shape(f.products,{Type::class_ref,o.function,{}},o.at);if(static_cast<std::size_t>(o.field)>=type.definition().fields.size() || value_type(o.operands[0],o.at)!=type || value_type(o.operands[1],o.at)!=type.field_type(static_cast<std::size_t>(o.field)))invalid(o.at);
                    }else invalid(o.at);
                }
            }
            std::vector<int> pending={0};while(!pending.empty()){charge(1);int id=pending.back();pending.pop_back();auto& b=f.blocks[static_cast<std::size_t>(id)];if(b.reachable)continue;b.reachable=true;pending.insert(pending.end(),b.successors.begin(),b.successors.end());}
            // Compute immediate dominators in reverse postorder, without recursion.
            std::vector<bool> seen(count,false);std::vector<std::pair<int,std::size_t>> stack={{0,0}};std::vector<int> order;seen[0]=true;
            while(!stack.empty()){auto& frame=stack.back();auto& successors=f.blocks[static_cast<std::size_t>(frame.first)].successors;if(frame.second==successors.size()){order.push_back(frame.first);stack.pop_back();}else {int next=successors[frame.second++];if(!seen[static_cast<std::size_t>(next)]){seen[static_cast<std::size_t>(next)]=true;stack.emplace_back(next,0);}}}
            std::reverse(order.begin(),order.end());std::vector<int> rank(count,-1),dom(count,-1);for(std::size_t i=0;i<order.size();++i)rank[static_cast<std::size_t>(order[i])]=static_cast<int>(i);dom[0]=0;
            bool dom_changed=true;while(dom_changed){dom_changed=false;for(int id:order)if(id!=0){charge(1);int parent=-1;for(int p:predecessors[static_cast<std::size_t>(id)])if(dom[static_cast<std::size_t>(p)]>=0){charge(1);if(parent<0)parent=p;else {int a=parent,b=p;while(a!=b){charge(1);if(rank[static_cast<std::size_t>(a)]>rank[static_cast<std::size_t>(b)])a=dom[static_cast<std::size_t>(a)];else b=dom[static_cast<std::size_t>(b)];}parent=a;}}if(parent<0)invalid({});if(dom[static_cast<std::size_t>(id)]!=parent){dom[static_cast<std::size_t>(id)]=parent;dom_changed=true;}}}
            auto dominates=[&](int a,int b){while(rank[static_cast<std::size_t>(b)]>rank[static_cast<std::size_t>(a)]){charge(1);b=dom[static_cast<std::size_t>(b)];}return a==b;};
            for(auto& b:f.blocks)if(b.reachable){
                auto available=[&](int value,int use_block,std::size_t use_position,Location at){auto it=value_blocks.find(value);if(it==value_blocks.end() || !f.blocks[static_cast<std::size_t>(it->second)].reachable || !dominates(it->second,use_block) || (it->second==use_block && value_positions.at(value)>=use_position))invalid(at);};
                for(auto& o:b.ops)for(std::size_t i=0;i<o.operands.size();++i){if(o.kind=="phi"){auto from=o.incoming[i];if(f.blocks[static_cast<std::size_t>(from)].reachable)available(o.operands[i],from,400001,o.at);}else available(o.operands[i],b.id,value_positions.at(o.id),o.at);}
                if(b.condition>=0)available(b.condition,b.id,400001,b.at);if(b.result>=0)available(b.result,b.id,400001,b.at);
            }
            auto match_charge=[&](std::size_t n,Location at=Location{}){charge(n,at);};verify_native_matches(f,values,places,value_blocks,dominates,match_charge);verify_native_variant_places(f,values,value_blocks,dominates,match_charge);
            std::vector<IrBits> in(count,IrBits(words,UINT64_MAX)),out=in,alive_in=in,alive_out=in;
            auto transfer=[&](const IrBlock& b,IrBits state,IrBits alive,bool check,const IrBits* maybe_initial=nullptr,const IrBits* maybe_live=nullptr){
                IrBits maybe_state=check ? *maybe_initial : IrBits{},maybe_alive=check ? *maybe_live : IrBits{};
                auto set_tree=[&](IrBits& bits,int id,bool value){auto nested=projections.subtree(id);charge(nested.size(),b.at);for(int child:nested)put(bits,projections.index(child),value);};
                auto refresh=[&](IrBits& bits,int id){for(int parent:projections.parents(id)){auto children=projections.children(parent);charge(children.size(),b.at);bool complete=true;for(int child:children)complete=complete && has(bits,projections.index(child));put(bits,projections.index(parent),complete);}};
                for(auto& o:b.ops){charge(1,o.at);auto p=places.find(o.place);if(p==places.end())continue;auto index=p->second;
                    if(o.kind=="parameter"){if(check && has(maybe_alive,index))invalid(o.at);set_tree(alive,o.place,true);set_tree(state,o.place,true);if(check){set_tree(maybe_alive,o.place,true);set_tree(maybe_state,o.place,true);}}
                    else if(o.kind=="storage-live"){if(check && has(maybe_alive,index))invalid(o.at);set_tree(alive,o.place,true);set_tree(state,o.place,false);if(check){set_tree(maybe_alive,o.place,true);set_tree(maybe_state,o.place,false);}}
                    else if(o.kind=="storage-dead"){if(check && !has(alive,index))invalid(o.at);set_tree(alive,o.place,false);set_tree(state,o.place,false);if(check){set_tree(maybe_alive,o.place,false);set_tree(maybe_state,o.place,false);}}
                    else if(o.kind=="write"){if(check && (!has(alive,index) || (!f.places[index].mutable_ && has(maybe_state,index))))invalid(o.at);set_tree(state,o.place,true);refresh(state,o.place);if(check){set_tree(maybe_state,o.place,true);refresh(maybe_state,o.place);}}
                    else if(o.kind=="variant-borrow" || o.kind=="variant-tag" || o.kind=="copy-read" || o.kind=="move" || o.kind=="reference-use" || o.kind=="borrow-shared" || o.kind=="borrow-exclusive"){if(check && (!has(alive,index) || !has(state,index))){if(f.places[index].parent!=-1 || f.places[index].type.is_product() || !f.places[index].type.is_copy() || f.places[index].type.is_reference())owner_unavailable(f,o,b.id);fail("AI3102","Storage is uninitialized or moved on a reaching IR path.",o.at);}if(o.kind=="move"){set_tree(state,o.place,false);refresh(state,o.place);if(check){set_tree(maybe_state,o.place,false);refresh(maybe_state,o.place);}}}
                }return std::pair{std::move(state),std::move(alive)};
            };
            bool changed=true;
            while(changed){changed=false;for(auto& b:f.blocks)if(b.reachable){
                IrBits state(words,b.id==0 ? 0 : UINT64_MAX),alive=state;
                if(b.id!=0)for(auto pred:predecessors[static_cast<std::size_t>(b.id)])if(f.blocks[static_cast<std::size_t>(pred)].reachable){charge(words);for(std::size_t w=0;w<words;++w){state[w]&=out[static_cast<std::size_t>(pred)][w];alive[w]&=alive_out[static_cast<std::size_t>(pred)][w];}}
                auto next=transfer(b,state,alive,false);auto i=static_cast<std::size_t>(b.id);if(in[i]!=state || alive_in[i]!=alive || out[i]!=next.first || alive_out[i]!=next.second){changed=true;in[i]=std::move(state);alive_in[i]=std::move(alive);out[i]=std::move(next.first);alive_out[i]=std::move(next.second);}
            }}
            std::vector<IrBits> maybe_in(count,IrBits(words,0)),maybe_out=maybe_in,maybe_alive_in=maybe_in,maybe_alive_out=maybe_in;
            changed=true;while(changed){changed=false;for(auto& b:f.blocks)if(b.reachable){
                IrBits state(words,0),alive=state;
                if(b.id!=0)for(auto pred:predecessors[static_cast<std::size_t>(b.id)])if(f.blocks[static_cast<std::size_t>(pred)].reachable){charge(words);for(std::size_t w=0;w<words;++w){state[w]|=maybe_out[static_cast<std::size_t>(pred)][w];alive[w]|=maybe_alive_out[static_cast<std::size_t>(pred)][w];}}
                auto next=transfer(b,state,alive,false);auto i=static_cast<std::size_t>(b.id);if(maybe_in[i]!=state || maybe_alive_in[i]!=alive || maybe_out[i]!=next.first || maybe_alive_out[i]!=next.second){changed=true;maybe_in[i]=std::move(state);maybe_alive_in[i]=std::move(alive);maybe_out[i]=std::move(next.first);maybe_alive_out[i]=std::move(next.second);}
            }}
            for(auto& b:f.blocks)if(b.reachable){auto i=static_cast<std::size_t>(b.id);transfer(b,in[i],alive_in[i],true,&maybe_in[i],&maybe_alive_in[i]);b.initialized_in=expose(in[i],f);b.initialized_out=expose(out[i],f);b.maybe_initialized_in=expose(maybe_in[i],f);b.maybe_initialized_out=expose(maybe_out[i],f);b.active_in=expose(alive_in[i],f);b.active_out=expose(alive_out[i],f);b.maybe_active_in=expose(maybe_alive_in[i],f);b.maybe_active_out=expose(maybe_alive_out[i],f);if(b.end=="unreachable" || (b.end=="return" && !b.maybe_active_out.empty()))invalid(b.at);}
            std::vector<IrBits> live_in(count,IrBits(words,0)),live_out=live_in;
            changed=true;while(changed){changed=false;for(auto it=f.blocks.rbegin();it!=f.blocks.rend();++it)if(it->reachable){
                auto& b=*it;IrBits after(words,0);for(auto next:b.successors){charge(words);for(std::size_t w=0;w<words;++w)after[w]|=live_in[static_cast<std::size_t>(next)][w];}
                auto before=after;for(auto o=b.ops.rbegin();o!=b.ops.rend();++o){charge(1,o->at);auto p=places.find(o->place);if(p!=places.end()){bool read=o->kind=="variant-borrow" || o->kind=="variant-tag" || o->kind=="copy-read" || o->kind=="move" || o->kind=="reference-use" || o->kind=="borrow-shared" || o->kind=="borrow-exclusive";auto nested=projections.subtree(o->place);charge(nested.size(),o->at);for(int child:nested)put(before,projections.index(child),read);}}
                auto i=static_cast<std::size_t>(b.id);if(before!=live_in[i] || after!=live_out[i]){changed=true;live_in[i]=std::move(before);live_out[i]=std::move(after);}
            }}
            for(auto& b:f.blocks)if(b.reachable){auto i=static_cast<std::size_t>(b.id);b.live_in=expose(live_in[i],f);b.live_out=expose(live_out[i],f);}
            verify_owner_values(f);
            if(f.unwind_plan!=native_unwind_plan(f))invalid(f.declaration);
        }
        for(auto& f:functions)if(f.unwind!=functions.front().unwind || f.fault_mode_location!=functions.front().fault_mode_location || (f.unwind && f.fault_mode_location.length!=6) || (f.fault_mode_location.length && f.fault_mode_location.length!=6))invalid(f.declaration);
        verify_function_regions(functions);
        verify_native_failures(functions);
    }
};
#include "loan_flow.inc"
#include "function_regions.inc"
#include "native_failure_flow.inc"
std::string ir_type(const Type& t){return t.label();}
void ir_location(std::ostream& out,Location at){out<<"{\"offset\":"<<at.offset<<",\"length\":"<<at.length<<",\"line\":"<<at.line<<",\"column\":"<<at.column;if(at.origin==SourceOrigin::core_outcomes)out<<",\"sourceDocument\":\""<<core_outcomes_uri<<"\",\"sourceSha256\":\""<<core_outcomes_sha256<<'"';out<<'}';}
template<class Range> void ir_ids(std::ostream& out,const Range& ids){out<<'[';bool comma=false;for(int id:ids){if(comma)out<<',';comma=true;out<<id;}out<<']';}
void ir_input_events(std::ostream& out,const IrInputEvents& events){out<<'[';bool comma=false;for(auto event:events){if(comma)out<<',';comma=true;out<<'['<<event.first<<','<<event.second<<']';}out<<']';}
void ir_paths(std::ostream& out,const RegionSet& paths,const IrFunction* function=nullptr){out<<'[';bool comma=false;for(auto& path:paths){if(comma)out<<',';comma=true;out<<"{\"parameter\":"<<path.first<<",\"depth\":"<<path.depth();auto first_field=std::find_if(path.selectors.begin(),path.selectors.end(),[](int step){return step>=0;});if(first_field!=path.selectors.end()){bool tail=std::all_of(first_field,path.selectors.end(),[](int step){return step>=0;});if(tail && first_field!=path.selectors.begin()){out<<",\"fields\":";ir_ids(out,ComponentPath(first_field,path.selectors.end()));}else{out<<",\"selectors\":";ir_ids(out,path.selectors);}}if(function){auto node=function->external_ids.find(path);if(node==function->external_ids.end())fail("AI3100","Unknown serialized input graph region.",{});if(function->summary_places.contains(node->second))out<<",\"graphStorage\":"<<node->second<<",\"summary\":true";}out<<'}';}out<<']';}

#include "native_match_json.inc"
#include "native_generic_json.inc"
std::string storage_json(const std::vector<IrFunction>& functions){
    std::ostringstream out;out<<"{\"schemaVersion\":\"aisharp.storage-ir/11\",\"memoryModel\":\"owned-buffers-function-regions\",\"borrowCheckerImplemented\":true,\"borrowCheckingScope\":\"monomorphic-function-regions\",\"interproceduralReferences\":true,\"literalPayloadsIncluded\":false,\"productSupport\":\"native-products-cyclic-input-graphs\",\"sumTypeSupport\":\"payload-case-unions\",\"genericTypeSupport\":\"concrete-schema-instantiation\",\"coreOutcomeSupport\":\"embedded-std-core/1\",\"coreSourceDocument\":\""<<core_outcomes_uri<<"\",\"coreSourceSha256\":\""<<core_outcomes_sha256<<"\"";
    std::shared_ptr<const NativeProductTable> products;for(auto& f:functions)if(f.products && (!f.products->definitions.empty() || !f.products->generics.empty())){products=f.products;break;}out<<",\"genericTypes\":";ir_generic_types(out,products);out<<",\"productTypes\":[";bool tc=false;
    if(products)for(std::size_t id=0;id<products->definitions.size();++id){if(tc)out<<',';tc=true;auto& type=products->definitions[id];out<<"{\"id\":"<<id<<",\"name\":\""<<type.name<<"\",\"genericDefinition\":"<<type.generic<<",\"typeArguments\":";ir_type_arguments(out,products,type);out<<",\"copy\":"<<(type.copy ? "true" : "false")<<",\"needsDrop\":"<<(type.drop ? "true" : "false")<<",\"containsReferences\":"<<(type.has_references ? "true" : "false")<<",\"expandedLeaves\":"<<type.leaves<<",\"location\":";ir_location(out,type.at);out<<",\"kind\":\""<<(type.enum_ ? "enum" : type.union_ ? "union" : "product")<<"\",\"cases\":[";bool case_comma=false;for(std::size_t c=0;c<type.cases.size();++c){if(case_comma)out<<',';case_comma=true;out<<"{\"index\":"<<c<<",\"name\":\""<<type.cases[c].name<<"\",\"location\":";ir_location(out,type.cases[c].at);ir_case_payload(out,type.cases[c],products);out<<'}';}out<<"],\"fields\":[";bool field_comma=false;for(std::size_t index=0;index<type.fields.size();++index){if(field_comma)out<<',';field_comma=true;auto& field=type.fields[index];out<<"{\"index\":"<<index<<",\"name\":\""<<field.name<<"\",\"type\":\""<<Type::from_shape(products,field.type).label()<<"\",\"location\":";ir_location(out,field.at);out<<'}';if(out.tellp()>64*1024*1024)fail("AI3100","Native storage proof output limit exceeded.",field.at);}out<<"]}";}
    out<<"],\"functions\":[";bool fc=false;
    for(auto& f:functions){IrPlaceGraph projections(f.places);if(fc)out<<',';fc=true;out<<"{\"id\":"<<f.id<<",\"entry\":"<<(f.entry ? "true" : "false")<<",\"resultType\":\""<<ir_type(f.type)<<"\",\"parameters\":";ir_ids(out,f.parameters);if(f.fallible){out<<",\"declaredFailure\":{\"successType\":\""<<f.success_type.label()<<"\",\"errorType\":\""<<f.error_type.label()<<"\",\"location\":";ir_location(out,f.failure_declaration);out<<'}';}if(f.fault_mode_location.length){out<<",\"faultMode\":\""<<(f.unwind ? "unwind" : "abort")<<"\",\"faultModeLocation\":";ir_location(out,f.fault_mode_location);}if(f.unwind){out<<",\"unwindPlan\":{\"entryDepthLimit\":128,\"nodes\":[";bool nc=false;for(auto [kind,id]:f.unwind_plan.nodes){if(nc)out<<',';nc=true;out<<"{\"kind\":\""<<(kind=='p' ? "storage" : "value")<<"\",\"id\":"<<id<<'}';}out<<"],\"faultSites\":";ir_ids(out,f.unwind_plan.sites);out<<'}';}out<<",\"matches\":";ir_matches(out,f);out<<",\"places\":[";bool pc=false;
        for(auto& p:f.places){if(pc)out<<',';pc=true;out<<"{\"id\":"<<p.id<<",\"type\":\""<<ir_type(p.type)<<"\",\"copy\":"<<(p.type.is_copy() ? "true" : "false")<<",\"needsDrop\":"<<(p.type.needs_drop() ? "true" : "false")<<",\"parameter\":"<<(p.parameter ? "true" : "false")<<",\"mutable\":"<<(p.mutable_ ? "true" : "false")<<",\"constant\":"<<(p.constant ? "true" : "false")<<",\"rootStorage\":"<<projections.root(p.id)<<",\"parent\":"<<p.parent<<",\"field\":"<<p.field<<",\"projection\":";ir_ids(out,projections.path(p.id));out<<",\"location\":";ir_location(out,p.at);out<<'}';if(out.tellp()>64*1024*1024)fail("AI3100","Native storage proof output limit exceeded.",p.at);}
        out<<"],\"externalPlaces\":[";bool xc=false;if(!f.external_places.empty()){auto all=f.places;all.insert(all.end(),f.external_places.begin(),f.external_places.end());IrPlaceGraph external_graph(all);for(auto& p:f.external_places){if(xc)out<<',';xc=true;out<<"{\"id\":"<<p.id<<",\"summary\":"<<(f.summary_places.contains(p.id) ? "true" : "false")<<",\"type\":\""<<p.type.label()<<"\",\"rootStorage\":"<<external_graph.root(p.id)<<",\"parent\":"<<p.parent<<",\"field\":"<<p.field<<",\"projection\":";ir_ids(out,external_graph.path(p.id));out<<",\"location\":";ir_location(out,p.at);out<<'}';if(out.tellp()>64*1024*1024)fail("AI3100","Native storage proof output limit exceeded.",p.at);}}
        out<<"],\"inputRegions\":[";bool ic=false;for(auto& input:f.inputs){if(ic)out<<',';ic=true;out<<"{\"parameter\":"<<input.parameter<<",\"depth\":"<<input.depth<<",\"storage\":"<<input.storage<<",\"loan\":"<<input.loan_id<<",\"folded\":"<<(input.folded ? "true" : "false")<<",\"noAliasRequired\":"<<(input.mutable_access ? "true" : "false")<<",\"cell\":"<<input.cell<<",\"structuralEvent\":"<<input.structural_event<<",\"mutableAccess\":"<<(input.mutable_access ? "true" : "false")<<",\"selectors\":";ir_ids(out,input.path.selectors);out<<",\"observations\":";auto demand=f.input_demands.find(input.path);ir_input_events(out,demand==f.input_demands.end() ? IrInputEvents{} : demand->second.observations);out<<",\"history\":";ir_input_events(out,f.external_history.at(input.storage));out<<",\"referenceType\":\""<<ir_type(input.type)<<"\",\"location\":";ir_location(out,input.at);out<<'}';if(out.tellp()>64*1024*1024)fail("AI3100","Native input graph proof output limit exceeded.",input.at);}
        out<<"],\"regionSummary\":{\"returned\":";ir_paths(out,f.summary.returned,&f);if(products && std::any_of(products->definitions.begin(),products->definitions.end(),[](auto& d){return d.payload_;})){out<<",\"possibleComponents\":[";bool presence_comma=false;for(auto& component:f.summary.possible_components){if(presence_comma)out<<',';presence_comma=true;ir_ids(out,component);}out<<"],\"possibleVariantTags\":[";bool pt=false;for(auto& [path,cases]:f.summary.possible_tags){if(pt)out<<',';pt=true;out<<"{\"fields\":";ir_ids(out,path);out<<",\"cases\":";ir_ids(out,cases);out<<'}';}out<<']';}if(!f.summary.returned_components.empty()){out<<",\"returnedComponents\":[";bool rc=false;for(auto& [path,origins]:f.summary.returned_components){if(rc)out<<',';rc=true;out<<"{\"fields\":";ir_ids(out,path);out<<",\"origins\":";ir_paths(out,origins,&f);out<<'}';}out<<']';}out<<",\"effects\":[";bool ec=false;for(auto& [path,origins]:f.summary.effects){if(ec)out<<',';ec=true;out<<"{\"parameter\":"<<path.first<<",\"depth\":"<<path.depth();if(!path.plain()){out<<",\"selectors\":";ir_ids(out,path.selectors);}auto destination=f.external_ids.at(path);if(f.summary_places.contains(destination))out<<",\"graphStorage\":"<<destination<<",\"summary\":true";out<<",\"origins\":";ir_paths(out,origins,&f);out<<'}';if(out.tellp()>64*1024*1024)fail("AI3100","Native storage proof output limit exceeded.",{});}out<<']';if(f.unwind){out<<",\"unwindEffects\":[";bool uc=false;for(auto& [path,origins]:f.summary.unwind_effects){if(uc)out<<',';uc=true;out<<"{\"parameter\":"<<path.first<<",\"selectors\":";ir_ids(out,path.selectors);out<<",\"origins\":";ir_paths(out,origins,&f);out<<'}';}out<<']';}out<<"},\"loans\":[";bool lc=false;for(auto& loan:f.loans){if(lc)out<<',';lc=true;out<<"{\"id\":"<<loan.id<<",\"creationInstruction\":"<<loan.creation<<",\"inputParameter\":"<<loan.input_parameter<<",\"resultComponent\":";ir_ids(out,loan.result_component);out<<",\"accessOnly\":"<<(loan.access_only ? "true" : "false")<<",\"inputDepth\":"<<loan.input_depth<<",\"referenceType\":\""<<ir_type(loan.type)<<"\",\"kind\":\""<<(loan.exclusive ? "exclusive" : "shared")<<"\",\"storage\":"<<loan.place<<",\"roots\":";ir_ids(out,loan.roots);out<<",\"parents\":";ir_ids(out,loan.parents);out<<",\"regionInstructions\":";ir_ids(out,loan.region);out<<",\"endsAfter\":";ir_ids(out,loan.ends_after);out<<",\"location\":";ir_location(out,loan.at);out<<'}';if(out.tellp()>64*1024*1024)fail("AI3100","Native storage proof output limit exceeded.",loan.at);}out<<"],\"referenceComponents\":[";bool vc=false;for(auto& [value,components]:f.reference_components)for(auto& [path,origins]:components){if(vc)out<<',';vc=true;out<<"{\"instruction\":"<<value<<",\"fields\":";ir_ids(out,path);out<<",\"origins\":";ir_ids(out,origins);out<<'}';if(out.tellp()>64*1024*1024)fail("AI3100","Native storage proof output limit exceeded.",{});}out<<"],\"blocks\":[";bool bc=false;for(auto& b:f.blocks){if(bc)out<<',';bc=true;out<<"{\"id\":"<<b.id<<",\"reachable\":"<<(b.reachable ? "true" : "false")<<",\"initializedIn\":";ir_ids(out,b.initialized_in);out<<",\"initializedOut\":";ir_ids(out,b.initialized_out);out<<",\"maybeInitializedIn\":";ir_ids(out,b.maybe_initialized_in);out<<",\"maybeInitializedOut\":";ir_ids(out,b.maybe_initialized_out);out<<",\"activeIn\":";ir_ids(out,b.active_in);out<<",\"activeOut\":";ir_ids(out,b.active_out);out<<",\"maybeActiveIn\":";ir_ids(out,b.maybe_active_in);out<<",\"maybeActiveOut\":";ir_ids(out,b.maybe_active_out);out<<",\"liveIn\":";ir_ids(out,b.live_in);out<<",\"liveOut\":";ir_ids(out,b.live_out);out<<",\"instructions\":[";bool oc=false;
            for(auto& o:b.ops){if(oc)out<<',';oc=true;out<<"{\"id\":"<<o.id<<",\"kind\":\""<<o.kind<<"\",\"type\":\""<<ir_type(o.type)<<"\",\"place\":"<<o.place<<",\"function\":"<<o.function<<",\"field\":"<<o.field<<",\"wraps\":"<<(o.wraps ? "true" : "false")<<",\"operands\":";ir_ids(out,o.operands);if(o.kind=="constant" && o.type.is_union())out<<",\"caseIndex\":"<<o.literal;if(o.cause_place>=0)out<<",\"causeSourcePlace\":"<<o.cause_place;out<<",\"incomingBlocks\":";ir_ids(out,o.incoming);out<<",\"location\":";ir_location(out,o.at);out<<'}';if(out.tellp()>64*1024*1024)fail("AI3100","Native storage proof output limit exceeded.",o.at);}
            if(out.tellp()>64*1024*1024)fail("AI3100","Native storage proof output limit exceeded.",b.at);
            out<<"],\"terminator\":{\"kind\":\""<<b.end<<"\",\"condition\":"<<b.condition<<",\"result\":"<<b.result<<",\"successors\":";ir_ids(out,b.successors);out<<",\"location\":";ir_location(out,b.at);out<<"}}";
        }out<<"]}";
    }out<<"]}";return out.str();
}
std::string storage_ctype(const Type& type){return type.is_reference() ? type.reference_name() : type.is_union() ? "ai_union_"+std::to_string(type.product_id()) : type.is_class() ? "int32_t" : type.is_product() ? "ai_product_"+std::to_string(type.product_id()) : ctype(type);}
class StorageEmitter {
    std::ostringstream out;
    const IrPlaceGraph* projections=nullptr;
    static std::string value(int id){return "t"+std::to_string(id);}
    std::string place(int id)const{return projections ? projections->cplace(id) : "v"+std::to_string(id);}
    static std::string empty(const Type& type){return type.is_decimal() ? "ai_dec_zero_value()" : type.is_class() ? "0" : type.is_aggregate() ? storage_ctype(type)+"_empty()" : type==Type::buffer ? "ai_owner_empty()" : type==Type::string ? "ai_string_empty()" : type==Type::args ? "ai_args_empty()" : type==Type::object ? "ai_object_null()" : type==Type::dynamic ? "ai_object_null()" : "0";}
    void drop(const std::string& destination,const Type& type){if(type==Type::buffer)out<<"ai_owner_drop(&("<<destination<<"));\n";else if(type.is_aggregate() && type.needs_drop())out<<storage_ctype(type)<<"_drop(&("<<destination<<"));\n";}
#include "native_variant_emission.inc"
    void transfer(const std::string& destination,const std::string& source,const Type& type,bool replace){
        if(replace)drop(destination,type);
        if(type==Type::buffer)out<<"ai_owner_check(ai_owner_move(&"<<destination<<", &"<<source<<"));\n";
        else{out<<destination<<" = "<<source<<";\n";if(!type.is_copy())out<<source<<" = "<<empty(type)<<";\n";}
    }
    std::string binary(const IrOp& o,const std::map<int,Type>& value_types){if(o.operands.size()==2 && value_types.at(o.operands[0])==Type::dynamic){std::string operation=o.kind.substr(7);std::string left=value(o.operands[0]),right=value(o.operands[1]);std::string helper=operation=="+" ? "ai_dynamic_add" : operation=="-" ? "ai_dynamic_sub" : operation=="*" ? "ai_dynamic_mul" : operation=="/" ? "ai_dynamic_div" : operation=="%" ? "ai_dynamic_mod" : operation=="==" ? "ai_dynamic_eq" : operation=="!=" ? "ai_dynamic_ne" : operation=="<" ? "ai_dynamic_lt" : operation==">" ? "ai_dynamic_gt" : operation=="<=" ? "ai_dynamic_le" : "ai_dynamic_ge";return helper+"("+left+", "+right+")";}if(o.operands.size()==2 && value_types.at(o.operands[0])==Type::object){std::string operation=o.kind.substr(7);std::string left=value(o.operands[0]),right=value(o.operands[1]);return std::string(operation=="==" ? "ai_object_equal(" : "!ai_object_equal(")+left+", "+right+")";}if(o.operands.size()==2 && value_types.at(o.operands[0])==Type::string){std::string operation=o.kind.substr(7);std::string left=value(o.operands[0]),right=value(o.operands[1]);if(operation=="+")return "ai_string_concat("+left+", "+right+")";return std::string(operation=="==" ? "ai_string_equal(" : "!ai_string_equal(")+left+", "+right+")";}
        auto operation=o.kind.substr(7),a=value(o.operands[0]),b=value(o.operands[1]);
        if(o.type==Type::long_integer){
            const auto* wide=native_int64_operator(operation);
            if(!wide)fail("AI3100","Unverified Int64 operation in native emission.",o.at);
            return "ai_i64_require(ai_i64_binary("+std::string(wide->emitted_id)+", "+a+", "+b+", "+(o.wraps ? "true" : "false")+"))";
        }
        const auto left_type=value_types.at(o.operands[0]);
        if(o.type.is_decimal() || left_type.is_decimal()){
            if(o.type.is_decimal()){
                if(operation=="+")return "ai_dec_add("+a+", "+b+")";
                if(operation=="-")return "ai_dec_sub("+a+", "+b+")";
                if(operation=="*")return "ai_dec_mul("+a+", "+b+")";
                if(operation=="/")return "ai_dec_div("+a+", "+b+")";
                fail("AI3100","Unverified Decimal arithmetic in native emission.",o.at);
            }
            return "(ai_dec_cmp("+a+", "+b+") "+operation+" 0)";
        }
        if(o.type.is_enum() || left_type.is_enum()){
            return "("+a+" "+operation+" "+b+")";
        }
        if(o.type.is_type_token() || left_type.is_type_token()){
            return "("+a+" "+operation+" "+b+")";
        }
        if(o.type.is_float32() || left_type.is_float32() || o.type.is_float64() || left_type.is_float64()){
            return "("+a+" "+operation+" "+b+")";
        }
        if(o.type.is_ulong64() || left_type.is_ulong64()){
            if(operation=="+" || operation=="-" || operation=="*"){
                std::string helper=operation=="+" ? "ai_u64_add" : operation=="-" ? "ai_u64_sub" : "ai_u64_mul";
                if(o.wraps)helper+="_wrap";
                return helper+"("+a+", "+b+")";
            }
            if(operation=="/")return "ai_u64_div("+a+", "+b+")";
            if(operation=="%")return "ai_u64_rem("+a+", "+b+")";
            if(operation=="<<")return "ai_u64_shl("+a+", "+b+")";
            if(operation==">>" || operation==">>>")return "ai_u64_shr("+a+", "+b+")";
            return "("+a+" "+operation+" "+b+")";
        }
        if(o.type.is_uint32() || left_type.is_uint32()){
            if(operation=="+" || operation=="-" || operation=="*"){
                std::string helper=operation=="+" ? "ai_u32_add" : operation=="-" ? "ai_u32_sub" : "ai_u32_mul";
                if(o.wraps)helper+="_wrap";
                return helper+"("+a+", "+b+")";
            }
            if(operation=="/")return "ai_u32_div("+a+", "+b+")";
            if(operation=="%")return "ai_u32_rem("+a+", "+b+")";
            if(operation=="<<")return "ai_u32_shl("+a+", "+b+")";
            if(operation==">>" || operation==">>>")return "ai_u32_shr("+a+", "+b+")";
            return "("+a+" "+operation+" "+b+")";
        }
        static const std::map<std::string,std::string> helpers={{"+","ai_add"},{"-","ai_sub"},{"*","ai_mul"},{"/","ai_div"},{"%","ai_rem"},{"<<","ai_shl"},{">>","ai_shr"},{">>>","ai_ushr"}};
        auto found=helpers.find(operation);if(found!=helpers.end()){auto helper=found->second;if(o.wraps && (operation=="+" || operation=="-" || operation=="*"))helper+="_wrap";return helper+"("+a+", "+b+")";}return "("+a+" "+operation+" "+b+")";
    }
    void edge(const IrFunction& f,int from,int to){
        for(auto& o:f.blocks[static_cast<std::size_t>(to)].ops)if(o.kind=="phi")for(std::size_t i=0;i<o.incoming.size();++i)if(o.incoming[i]==from)out<<value(o.id)<<" = "<<value(o.operands[i])<<";\n";
        out<<"goto b"<<to<<";\n";
    }
#include "native_unwind_emission.inc"
    void signature(const IrFunction& f){out<<storage_ctype(f.type)<<" f"<<f.id<<'(';if(f.parameters.empty())out<<"void";for(std::size_t i=0;i<f.parameters.size();++i){if(i)out<<", ";auto p=std::find_if(f.places.begin(),f.places.end(),[&](auto& v){return v.id==f.parameters[i];});out<<storage_ctype(p->type)<<' '<<"v"+std::to_string(p->id);}out<<')';}
public:
    std::string run(const std::vector<IrFunction>& functions,int main,std::string_view source_hash){
        if(functions.empty())fail("AI3100","Native emission requires a verified module.",{});
        unwinding=functions.front().unwind;for(auto& f:functions)if(f.unwind!=unwinding || f.unwind_plan!=native_unwind_plan(f))fail("AI3100","Native emission requires a consistent verified unwind plan.",f.declaration);
        if(unwinding)out<<"#define AISHARP_NATIVE_UNWIND 1\n#define _Static_assert static_assert\n";
        out<<
#include "runtime.inc"
#include "owner_runtime.inc"
        ;
        bool ai_string_used=false;bool ai_file_used=false;bool ai_text_used=false;auto uses_string=[&](const Type& type){return type==Type::string;};
        bool ai_args_used=false;
        for(auto& f:functions){if(uses_string(f.type) || uses_string(f.success_type) || uses_string(f.error_type))ai_string_used=true;for(auto& p:f.places)if(uses_string(p.type))ai_string_used=true;for(auto& s:f.statics)if(uses_string(s.type))ai_string_used=true;for(auto& b:f.blocks)for(auto& o:b.ops){if(uses_string(o.type))ai_string_used=true;if(o.type==Type::args || o.kind=="args-value" || o.kind=="args-length" || o.kind=="args-at"){ai_args_used=true;ai_string_used=true;}}}
        bool ai_object_used=false;
        for(auto& f:functions)for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind=="object-box" || o.kind=="object-unbox" || o.type==Type::object || o.type==Type::dynamic){ai_object_used=true;ai_string_used=true;}
        for(auto& f:functions)for(auto& b:f.blocks)for(auto& o:b.ops){if(o.kind=="call" && (o.function==-9 || o.function==-10))ai_file_used=true;if(o.kind=="call" && (o.function==-11 || o.function==-12))ai_text_used=true;}
    if(ai_string_used){
        // A union payload can require the type without any string operation, so
        // the helpers can be unreferenced. C++ (unwind) programs are compiled
        // with /W4 /WX; suppress only there so C emission is unchanged.
        if(unwinding)out<<"#ifdef _MSC_VER\n#pragma warning(push)\n#pragma warning(disable: 4505)\n#endif\n";
        out<<R"AI_STRING(
typedef struct ai_string { char bytes[256]; uint32_t length; bool is_null; } ai_string;
static ai_string ai_string_empty(void) { ai_string s; memset(&s,0,sizeof(s)); s.is_null=false; return s; }
static ai_string ai_string_null(void) { ai_string s = ai_string_empty(); s.is_null = true; return s; }
static ai_string ai_string_from_text(const char* text) { ai_string s = ai_string_empty(); const char* p = text; if (p) { while (*p) { if (s.length >= sizeof(s.bytes)) { ai_fail("AISharp.StringCapacityException"); } s.bytes[s.length++] = *p++; } } return s; }
static bool ai_string_equal(ai_string a, ai_string b) { if (a.is_null || b.is_null) return a.is_null && b.is_null; return a.length == b.length && (a.length == 0 || memcmp(a.bytes,b.bytes,a.length) == 0); }
static ai_string ai_string_concat(ai_string a, ai_string b) { ai_string s = ai_string_empty(); uint32_t al = a.is_null ? 0 : a.length; uint32_t bl = b.is_null ? 0 : b.length; if ((uint64_t)al + (uint64_t)bl > sizeof(s.bytes)) { ai_fail("AISharp.StringCapacityException"); } for (uint32_t i = 0; i < al; ++i) s.bytes[s.length++] = a.bytes[i]; for (uint32_t i = 0; i < bl; ++i) s.bytes[s.length++] = b.bytes[i]; return s; }
)AI_STRING";
        if(unwinding)out<<"#ifdef _MSC_VER\n#pragma warning(pop)\n#endif\n";
    }
    if(ai_file_used)out<<R"AI_FILE(
typedef struct ai_selfhost_file_handle { FILE* stream; } ai_selfhost_file_handle;
static ai_string ai_selfhost_path_text(ai_string path) { if (path.is_null || path.length >= 256) ai_fail("AISharp.FileNotFoundException"); return path; }
static ai_owner ai_file_read(ai_string path) { ai_string value = ai_selfhost_path_text(path); char name[257]; memcpy(name,value.bytes,value.length); name[value.length]=0; FILE* stream=fopen(name,"rb"); if(!stream) ai_fail("AISharp.FileNotFoundException"); if(fseek(stream,0,SEEK_END)!=0){fclose(stream);ai_fail("AISharp.IOException");} long length=ftell(stream); if(length<0 || (uint64_t)length>AI_OWNER_MAX_BYTES){fclose(stream);ai_fail("AISharp.IOException");} rewind(stream); ai_owner owner=ai_buffer_create((int32_t)length); if(length>0 && fread(owner.data,1,(size_t)length,stream)!=(size_t)length){fclose(stream);ai_owner_drop(&owner);ai_fail("AISharp.IOException");} fclose(stream); return owner; }
static void ai_file_write(ai_string path, ai_owner value) { ai_string text=ai_selfhost_path_text(path); char name[257]; memcpy(name,text.bytes,text.length); name[text.length]=0; FILE* stream=fopen(name,"wb"); if(!stream){ai_owner_drop(&value);ai_fail("AISharp.IOException");} if(value.length && fwrite(value.data,1,value.length,stream)!=value.length){fclose(stream);ai_owner_drop(&value);ai_fail("AISharp.IOException");} if(fclose(stream)!=0){ai_owner_drop(&value);ai_fail("AISharp.IOException");} ai_owner_drop(&value); }
)AI_FILE";
    if(ai_text_used)out<<R"AI_TEXT(
static ai_owner ai_string_bytes(ai_string value) { ai_owner owner; if(value.is_null) ai_fail("AISharp.InvalidOwnerException"); owner=ai_buffer_create((int32_t)value.length); if(value.length) memcpy(owner.data,value.bytes,value.length); return owner; }
static ai_owner ai_int_text(int32_t value) { char text[32]; int length=snprintf(text,sizeof(text),"%d",(int)value); ai_owner owner; if(length<0 || length>=32) ai_fail("AISharp.IOException"); owner=ai_buffer_create((int32_t)length); if(length) memcpy(owner.data,text,(size_t)length); return owner; }
)AI_TEXT";
        if(ai_object_used)out<<R"AI_OBJECT(
#pragma warning(push)
#pragma warning(disable: 4505)
typedef struct ai_object { int32_t tag; int32_t i32; int64_t i64; bool b; uint16_t c; float f; double d; ai_string s; } ai_object;
static ai_object ai_object_null(void) { ai_object o; memset(&o,0,sizeof(o)); return o; }
static ai_object ai_object_box_i32(int32_t value) { ai_object o = ai_object_null(); o.tag = 1; o.i32 = value; return o; }
static ai_object ai_object_box_i64(int64_t value) { ai_object o = ai_object_null(); o.tag = 2; o.i64 = value; return o; }
static ai_object ai_object_box_bool(bool value) { ai_object o = ai_object_null(); o.tag = 3; o.b = value; return o; }
static ai_object ai_object_box_char(uint16_t value) { ai_object o = ai_object_null(); o.tag = 4; o.c = value; return o; }
static ai_object ai_object_box_f32(float value) { ai_object o = ai_object_null(); o.tag = 5; o.f = value; return o; }
static ai_object ai_object_box_f64(double value) { ai_object o = ai_object_null(); o.tag = 6; o.d = value; return o; }
static ai_object ai_object_box_string(ai_string value) { ai_object o = ai_object_null(); o.tag = 7; o.s = value; return o; }
static int32_t ai_object_unbox_i32(ai_object o) { if (o.tag != 1) ai_fail("AISharp.InvalidCastException"); return o.i32; }
static int64_t ai_object_unbox_i64(ai_object o) { if (o.tag != 2) ai_fail("AISharp.InvalidCastException"); return o.i64; }
static bool ai_object_unbox_bool(ai_object o) { if (o.tag != 3) ai_fail("AISharp.InvalidCastException"); return o.b; }
static uint16_t ai_object_unbox_char(ai_object o) { if (o.tag != 4) ai_fail("AISharp.InvalidCastException"); return o.c; }
static float ai_object_unbox_f32(ai_object o) { if (o.tag != 5) ai_fail("AISharp.InvalidCastException"); return o.f; }
static double ai_object_unbox_f64(ai_object o) { if (o.tag != 6) ai_fail("AISharp.InvalidCastException"); return o.d; }
static ai_string ai_object_unbox_string(ai_object o) { if (o.tag != 7) ai_fail("AISharp.InvalidCastException"); return o.s; }
static ai_string ai_object_as_string(ai_object o) { if (o.tag == 7) return o.s; return ai_string_null(); }
static bool ai_object_equal(ai_object a,ai_object b) { if (a.tag != b.tag) return false; switch (a.tag) { case 0: return true; case 1: return a.i32 == b.i32; case 2: return a.i64 == b.i64; case 3: return a.b == b.b; case 4: return a.c == b.c; case 5: return a.f == b.f; case 6: return a.d == b.d; case 7: return ai_string_equal(a.s,b.s); default: return false; } }
#pragma warning(pop)
)AI_OBJECT";
        bool ai_dynamic_used=false;
        for(auto& f:functions)for(auto& b:f.blocks)for(auto& o:b.ops)if(o.type==Type::dynamic){ai_dynamic_used=true;ai_object_used=true;ai_string_used=true;}
        if(ai_dynamic_used)out<<R"AI_DYNAMIC(
#pragma warning(push)
#pragma warning(disable: 4505)
static ai_object ai_dynamic_bad(void) { ai_fail("AISharp.DynamicBindingException"); return ai_object_null(); }
static int ai_dynamic_is_number(ai_object a) { return a.tag==1 || a.tag==2 || a.tag==5 || a.tag==6; }
static double ai_dynamic_number(ai_object a) { if (a.tag==1) return (double)a.i32; if (a.tag==2) return (double)a.i64; if (a.tag==5) return (double)a.f; if (a.tag==6) return a.d; return 0.0; }
static ai_object ai_dynamic_add(ai_object a,ai_object b) { if (a.tag==7 && b.tag==7) return ai_object_box_string(ai_string_concat(a.s,b.s)); if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_object_box_f64(ai_dynamic_number(a)+ai_dynamic_number(b)); return ai_dynamic_bad(); }
static ai_object ai_dynamic_sub(ai_object a,ai_object b) { if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_object_box_f64(ai_dynamic_number(a)-ai_dynamic_number(b)); return ai_dynamic_bad(); }
static ai_object ai_dynamic_mul(ai_object a,ai_object b) { if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_object_box_f64(ai_dynamic_number(a)*ai_dynamic_number(b)); return ai_dynamic_bad(); }
static ai_object ai_dynamic_div(ai_object a,ai_object b) { if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_object_box_f64(ai_dynamic_number(a)/ai_dynamic_number(b)); return ai_dynamic_bad(); }
static ai_object ai_dynamic_mod(ai_object a,ai_object b) { if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_object_box_f64(fmod(ai_dynamic_number(a),ai_dynamic_number(b))); return ai_dynamic_bad(); }
static bool ai_dynamic_eq(ai_object a,ai_object b) { if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_dynamic_number(a)==ai_dynamic_number(b); return ai_object_equal(a,b); }
static bool ai_dynamic_ne(ai_object a,ai_object b) { return !ai_dynamic_eq(a,b); }
static bool ai_dynamic_lt(ai_object a,ai_object b) { if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_dynamic_number(a)<ai_dynamic_number(b); return false; }
static bool ai_dynamic_gt(ai_object a,ai_object b) { if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_dynamic_number(a)>ai_dynamic_number(b); return false; }
static bool ai_dynamic_le(ai_object a,ai_object b) { if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_dynamic_number(a)<=ai_dynamic_number(b); return false; }
static bool ai_dynamic_ge(ai_object a,ai_object b) { if (ai_dynamic_is_number(a) && ai_dynamic_is_number(b)) return ai_dynamic_number(a)>=ai_dynamic_number(b); return false; }
#pragma warning(pop)
)AI_DYNAMIC";
        if(ai_args_used)out<<R"AI_ARGS(
typedef struct ai_args { int32_t count; ai_string values[16]; } ai_args;
static ai_args ai_args_empty(void) { ai_args a; memset(&a,0,sizeof(a)); return a; }
static ai_args ai_args_from_command_line(int argc,char** argv) { ai_args a = ai_args_empty(); a.count = argc > 17 ? 16 : (argc > 0 ? argc - 1 : 0); for (int i = 0; i < a.count; ++i) a.values[i] = ai_string_from_text(argv[i+1]); return a; }
static int32_t ai_args_length(ai_args a) { return a.count; }
static ai_string ai_args_at(ai_args a,int32_t index) { if (index < 0 || index >= a.count) ai_fail("AISharp.IndexOutOfRangeException"); return a.values[index]; }
)AI_ARGS";
        if(ai_args_used)out<<"static ai_args ai_args_value;\n";
        if(std::any_of(functions.begin(),functions.end(),[](const auto& f){
            return std::any_of(f.blocks.begin(),f.blocks.end(),[](const auto& block){
                return std::any_of(block.ops.begin(),block.ops.end(),[](const auto& op){
                    return op.kind=="unary:-" && op.type==Type::long_integer;
                });
            });
        }))out<<R"AI_LONG_NEG(
int64_t ai_neg64(int64_t value) {
    if(value==INT64_MIN)ai_fail("System.OverflowException");
    return -value;
}
int64_t ai_neg64_wrap(int64_t value) { return value==INT64_MIN ? value : -value; }
)AI_LONG_NEG";
        if(std::any_of(functions.begin(),functions.end(),[](const auto& f){
            return std::any_of(f.blocks.begin(),f.blocks.end(),[](const auto& block){
                return std::any_of(block.ops.begin(),block.ops.end(),[](const auto& op){
                    return op.kind.starts_with("binary:") && op.type==Type::long_integer;
                });
            });
        })){
#define AI_INT64_CODE(...) #__VA_ARGS__ "\n"
            out<<
#include "native_int64_ops.inc"
            ;
#undef AI_INT64_CODE
            out<<R"AI_I64_RUNTIME(
int64_t ai_i64_require(ai_i64_result result) {
    if(result.error==ai_i64_overflow)ai_fail("System.OverflowException");
    if(result.error==ai_i64_zero)ai_fail("System.DivideByZeroException");
    if(result.error!=ai_i64_ok)ai_fail("AISharp.InvalidIntegerOperation");
    return result.value;
}
)AI_I64_RUNTIME";
        }
        if(std::any_of(functions.begin(),functions.end(),[](const auto& f){
            return std::any_of(f.blocks.begin(),f.blocks.end(),[](const auto& block){
                return std::any_of(block.ops.begin(),block.ops.end(),[](const auto& op){
                    return op.kind=="integer-narrow";
                });
            });
        })){
#define AI_NARROW_CODE(...) #__VA_ARGS__ "\n"
            out<<
#include "native_narrow_integer_ops.inc"
            ;
#undef AI_NARROW_CODE
            out<<R"AI_NARROW_RUN(
int32_t ai_small_convert(int64_t value, int32_t minimum, int32_t maximum, bool wraps) {
    if(wraps)return ai_small_wrap(value,minimum,maximum);
    if(value<minimum || value>maximum)ai_fail("System.OverflowException");
    return (int32_t)value;
}
)AI_NARROW_RUN";
        }
        bool decimal_used=false;
        for(auto& f:functions){
            if(f.type.is_decimal() || f.success_type.is_decimal() || f.error_type.is_decimal())decimal_used=true;
            for(auto& place:f.places)if(place.type.is_decimal())decimal_used=true;
            for(auto& block:f.blocks)for(auto& instruction:block.ops)if(instruction.type.is_decimal())decimal_used=true;
            if(f.products){
                for(auto& definition:f.products->definitions)for(auto& field:definition.fields)if(Type::from_shape(f.products,field.type).is_decimal())decimal_used=true;
                for(auto& generic:f.products->generics)for(auto& field:generic.fields)if(field.type.kind==NativeTypeExpression::concrete && field.type.shape.primitive==Type::decimal)decimal_used=true;
            }
        }
        if(decimal_used){
#define AI_DECIMAL_CODE(...) #__VA_ARGS__ "\n"
            out<<
#include "native_decimal_ops.inc"
            ;
#undef AI_DECIMAL_CODE
            out<<R"AI_DECIMAL_RUN(
static ai_decimal ai_dec_zero_value(void) { return ai_dec_make(0,0,0,0); }
static ai_decimal ai_dec_require(int error) {
    if(error==2)ai_fail("System.DivideByZeroException");
    if(error)ai_fail("System.OverflowException");
    return ai_dec_make(0,0,0,0);
}
static ai_decimal ai_dec_add(ai_decimal a, ai_decimal b) { ai_decimal r; if(ai_dec_core_add(a,b,&r))ai_dec_require(1); return r; }
static ai_decimal ai_dec_sub(ai_decimal a, ai_decimal b) { ai_decimal r; if(ai_dec_core_sub(a,b,&r))ai_dec_require(1); return r; }
static ai_decimal ai_dec_mul(ai_decimal a, ai_decimal b) { ai_decimal r; if(ai_dec_core_mul(a,b,&r))ai_dec_require(1); return r; }
static ai_decimal ai_dec_div(ai_decimal a, ai_decimal b) { ai_decimal r; int error=ai_dec_core_div(a,b,&r); if(error)ai_dec_require(error); return r; }
static ai_decimal ai_dec_neg(ai_decimal a) { ai_decimal r; if(ai_dec_core_neg(a,&r))ai_dec_require(1); return r; }
static ai_decimal ai_dec_from_i32(int32_t value) { ai_decimal r; if(ai_dec_core_from_i64(value,&r))ai_dec_require(1); return r; }
static ai_decimal ai_dec_from_i64(int64_t value) { ai_decimal r; if(ai_dec_core_from_i64(value,&r))ai_dec_require(1); return r; }
static ai_decimal ai_dec_from_u32(uint32_t value) { ai_decimal r; if(ai_dec_core_from_u64(value,&r))ai_dec_require(1); return r; }
static ai_decimal ai_dec_from_u64(uint64_t value) { ai_decimal r; if(ai_dec_core_from_u64(value,&r))ai_dec_require(1); return r; }
static ai_decimal ai_dec_from_double(double value) { ai_decimal r; if(ai_dec_core_from_double(value,&r))ai_dec_require(1); return r; }
static ai_decimal ai_dec_from_float(float value) { ai_decimal r; if(ai_dec_core_from_float(value,&r))ai_dec_require(1); return r; }
static int32_t ai_i32_from_dec(ai_decimal value) { uint64_t magnitude=0; int overflow=0; ai_dec_to_u64(value,&magnitude,&overflow); if(overflow)ai_dec_require(1); if(ai_dec_negative(value)){ if(magnitude>2147483648ULL)ai_dec_require(1); return magnitude==2147483648ULL ? INT32_MIN : -(int32_t)magnitude; } if(magnitude>2147483647ULL)ai_dec_require(1); return (int32_t)magnitude; }
static int64_t ai_i64_from_dec(ai_decimal value) { uint64_t magnitude=0; int overflow=0; ai_dec_to_u64(value,&magnitude,&overflow); if(overflow)ai_dec_require(1); if(ai_dec_negative(value)){ if(magnitude>9223372036854775808ULL)ai_dec_require(1); return magnitude==9223372036854775808ULL ? INT64_MIN : -(int64_t)magnitude; } if(magnitude>(uint64_t)INT64_MAX)ai_dec_require(1); return (int64_t)magnitude; }
static uint32_t ai_u32_from_dec(ai_decimal value) { uint64_t magnitude=0; int overflow=0; ai_dec_to_u64(value,&magnitude,&overflow); if(overflow || (ai_dec_negative(value) && magnitude) || magnitude>4294967295ULL)ai_dec_require(1); return (uint32_t)magnitude; }
static uint64_t ai_u64_from_dec(ai_decimal value) { uint64_t magnitude=0; int overflow=0; ai_dec_to_u64(value,&magnitude,&overflow); if(overflow || (ai_dec_negative(value) && magnitude))ai_dec_require(1); return magnitude; }
static int64_t ai_small_from_dec(ai_decimal value, int64_t minimum, int64_t maximum) { int64_t converted=ai_i64_from_dec(value); if(converted<minimum || converted>maximum)ai_dec_require(1); return converted; }
static double ai_f64_from_dec(ai_decimal value) { double result=0; ai_dec_to_double_value(value,&result); return result; }
static float ai_f32_from_dec(ai_decimal value) { double result=0; ai_dec_to_double_value(value,&result); return (float)result; }
)AI_DECIMAL_RUN";
        }
        out<<"static const char ai_source_hash[] = \""<<source_hash<<"\";\n";
        out<<
#include "native_failure_runtime.inc"
        ;
        if(unwinding)out<<
#include "native_unwind_runtime.inc"
        ;
        std::shared_ptr<const NativeProductTable> products;for(auto& f:functions)if(f.products && (!f.products->definitions.empty() || !f.products->generics.empty())){products=f.products;break;}
        if(products)for(std::size_t id=0;id<products->definitions.size();++id){auto& d=products->definitions[id];if(d.enum_)continue;if(d.union_ && !d.payload_)out<<"typedef int32_t ai_union_"<<id<<";\n";else {auto name=std::string(d.union_ ? "ai_union_" : "ai_product_")+std::to_string(id);out<<"typedef struct "<<name<<' '<<name<<";\n";}}
        for(auto& f:functions)if(f.type.is_reference() && f.summary.returned.empty())fail("AI3100","Reference result lacks a verified input region.",{});
        std::map<std::string,Type> reference_types;
        for(auto& f:functions)if(f.type.is_reference()){auto type=f.type;while(type.is_reference()){reference_types.emplace(type.reference_name(),type);type=type.pointee();}}
        for(auto& f:functions)for(auto& block:f.blocks)for(auto& o:block.ops)if(o.type.is_reference()){auto type=o.type;while(type.is_reference()){reference_types.emplace(type.reference_name(),type);type=type.pointee();}}
        for(auto& f:functions)for(auto& p:f.places)if(p.type.is_reference()){auto type=p.type;while(type.is_reference()){reference_types.emplace(type.reference_name(),type);type=type.pointee();}}
        if(products)for(auto& product:products->definitions)for(auto& field:product.fields){auto type=Type::from_shape(products,field.type);while(type.is_reference()){reference_types.emplace(type.reference_name(),type);type=type.pointee();}}
        std::vector<Type> ordered;for(auto& [name,type]:reference_types){(void)name;ordered.push_back(type);}std::sort(ordered.begin(),ordered.end(),[](const Type& a,const Type& b){if(a.reference_depth()!=b.reference_depth())return a.reference_depth()<b.reference_depth();return a.reference_name()<b.reference_name();});
        for(auto& type:ordered)out<<"typedef "<<(type.borrow_kind()==Type::Borrow::shared ? "const " : "")<<storage_ctype(type.pointee())<<" * "<<type.reference_name()<<";\n";
        if(products){
            for(int id:products->layout_order){if(products->definitions[static_cast<std::size_t>(id)].enum_)continue;emit_schema(products,id);}
        }
        {std::shared_ptr<const NativeProductTable> class_table;for(auto& f:functions)if(f.products){class_table=f.products;break;}if(class_table){for(std::size_t id=0;id<class_table->definitions.size();++id)if(class_table->definitions[id].class_)out<<"static ai_product_"<<id<<" ai_class_"<<id<<"[1024];\n";out<<"static int32_t ai_class_shared_used=0; static int32_t ai_class_tags[1024];\n";}}
        for(auto& f:functions){signature(f);out<<";\n";}
        for(auto& f:functions){IrPlaceGraph graph(f.places);projections=&graph;std::map<int,Type> value_types,local_types;for(auto& block:f.blocks)for(auto& instruction:block.ops)value_types.emplace(instruction.id,instruction.type);for(auto& local:f.places)local_types.emplace(local.id,local.type);signature(f);out<<" {\n";if(!unwinding)out<<"if (++ai_depth > AISHARP_RECURSION_LIMIT) ai_fail(\"AISharp.RecursionLimitException\");\n";
            for(auto& st:f.statics)out<<"static "<<storage_ctype(st.type)<<' '<<st.name<<" = "<<st.value<<";\n";
        for(auto& p:f.places)if(p.parent==-1){if(!p.parameter)out<<storage_ctype(p.type)<<' '<<place(p.id)<<" = "<<empty(p.type)<<";\n";out<<"(void)"<<place(p.id)<<";\n";}
            for(auto& b:f.blocks)if(b.reachable)for(auto& o:b.ops)if(o.type!=Type::unit){if(o.kind=="stack-alloc")out<<"unsigned char ai_stack_"<<o.id<<"[4096]; ai_owner "<<value(o.id)<<" = ai_owner_stack(ai_stack_"<<o.id<<", "<<static_cast<std::size_t>(o.field)<<"); (void)"<<value(o.id)<<";\n";else out<<storage_ctype(o.type)<<' '<<value(o.id)<<" = "<<empty(o.type)<<"; (void)"<<value(o.id)<<";\n";}
            unwind_nodes.clear();if(unwinding){
                for(std::size_t i=0;i<f.unwind_plan.nodes.size();++i)unwind_nodes.emplace(f.unwind_plan.nodes[i],i);
                out<<"ai_unwind_node ai_nodes["<<std::max(std::size_t{1},f.unwind_plan.nodes.size())<<"]={}; (void)ai_nodes;\nai_unwind_scope ai_scope("<<unwind_site(f.id,-1,f.declaration)<<");\n";
                for(int parameter:f.parameters)unwind_track('p',parameter,place(parameter),local_types.at(parameter));out<<"if(ai_scope.enter()) ai_fail(\"AISharp.RecursionLimitException\");\n";
            }
            out<<"goto b0;\n";
            for(auto& b:f.blocks)if(b.reachable){out<<'b'<<b.id<<": ;\n";
                for(auto& o:b.ops){if(o.kind=="stack-alloc")continue;
                    if(o.kind=="class-new"){int class_id=o.function;out<<value(o.id)<<" = ai_class_shared_used++; if("<<value(o.id)<<">=1024) ai_fail(\"AISharp.OutOfMemoryException\");\n";std::vector<int> chain;int cursor=class_id;std::size_t guard=0;while(cursor>=0 && guard++<32){chain.push_back(cursor);cursor=f.products ? f.products->definitions[static_cast<std::size_t>(cursor)].base_class : -1;}for(int ancestor:chain)out<<"ai_class_"<<ancestor<<"["<<value(o.id)<<"] = ai_product_"<<ancestor<<"_empty();\n";out<<"ai_class_tags["<<value(o.id)<<"] = "<<class_id<<";\n";for(std::size_t i=0;i<o.operands.size();++i)out<<"ai_class_"<<class_id<<"["<<value(o.id)<<"].m"<<i<<" = "<<value(o.operands[i])<<";\n";continue;}
                    if(o.kind=="class-field-store"){out<<"ai_class_"<<o.function<<"["<<value(o.operands[0])<<"].m"<<o.field<<" = "<<value(o.operands[1])<<";\n";continue;}
                    std::string rhs;
                    if(unwinding && native_fault_site(o))out<<"ai_scope.at("<<unwind_site(f.id,o.id,o.at)<<");\n";
                    if(unwinding && o.kind=="storage-live"){int root=graph.root(o.place);auto node=unwind_nodes.find({'p',root});if(node!=unwind_nodes.end()){out<<"if(!ai_nodes["<<node->second<<"].scope) {\n";unwind_track('p',root,place(root),local_types.at(root));out<<"}\n";}}
                    if(o.kind=="class-field-load"){rhs="ai_class_"+std::to_string(o.function)+"["+value(o.operands[0])+"].m"+std::to_string(o.field);}
                    else if(o.kind=="class-upcast"){rhs=value(o.operands[0]);}
                    else if(o.kind=="class-tag"){rhs="ai_class_tags["+value(o.operands[0])+"]";}
                    else if(o.kind=="class-new"){}
                    else if(o.kind=="constant"){
                        if(o.type==Type::long_integer)rhs=o.literal==INT64_MIN ? "INT64_MIN" : "INT64_C("+std::to_string(o.literal)+")";
                        else if(o.type.is_uint32())rhs=std::to_string(static_cast<std::uint32_t>(o.literal))+"u";
                        else if(o.type.is_ulong64())rhs=std::to_string(static_cast<std::uint64_t>(o.literal))+"ull";
                        else if(o.type.is_float32()){std::ostringstream bits;bits<<std::hex<<static_cast<std::uint32_t>(o.literal);rhs="ai_f32_bits(0x"+bits.str()+"u)";}
                        else if(o.type.is_float64()){std::ostringstream bits;bits<<std::hex<<static_cast<std::uint64_t>(o.literal);rhs="ai_f64_bits(0x"+bits.str()+"ull)";}
                        else if(o.type.is_type_token())rhs=std::to_string(o.literal)+"u";
                        else if(o.type.is_enum()){auto underlying=o.type.enum_underlying();rhs="("+storage_ctype(underlying)+")"+std::to_string(o.literal)+(underlying.is_uint32() ? "u" : underlying.is_ulong64() ? "ull" : "");}
                        else if(o.type.is_decimal())rhs="ai_dec_make("+std::to_string(static_cast<std::uint32_t>(o.literal))+"u, "+std::to_string(static_cast<std::uint32_t>(static_cast<std::uint64_t>(o.literal)>>32))+"u, "+std::to_string(static_cast<std::uint32_t>(o.literal2))+"u, "+std::to_string(static_cast<std::uint32_t>(static_cast<std::uint64_t>(o.literal2)>>32))+"u)";
                        else rhs=o.type==Type::boolean ? (o.literal ? "true" : "false") : o.literal==INT32_MIN ? "INT32_MIN" : std::to_string(o.literal);
                    }else if(o.kind=="integer-widen")rhs="("+storage_ctype(o.type)+")"+value(o.operands[0]);
                    else if(o.kind=="integer-narrow"){
                        auto small=o.type.narrow_integer();
                        rhs="("+storage_ctype(o.type)+")ai_small_convert("+value(o.operands[0])+", "+
                            std::to_string(small->minimum)+", "+std::to_string(small->maximum)+", "+
                            (o.wraps ? "true" : "false")+")";
                    }else if(o.kind=="integer-convert"){
                        auto operand=value(o.operands[0]);
                        if(o.type==Type::long_integer)rhs="(int64_t)"+operand;
                        else rhs=o.wraps ? "ai_bits((uint32_t)"+operand+")" : "ai_narrow("+operand+")";
                    }else if(o.kind=="integer-u32"){
                        auto source=value_types.at(o.operands[0]);auto operand=value(o.operands[0]);
                        const std::string wraps=o.wraps ? "true" : "false";
                        if(o.type.is_uint32()){
                            if(source==Type::integer || (source.narrow_integer() && !source.is_char() && source.narrow_integer()->minimum<0))
                                rhs="ai_u32_from_i32((int32_t)"+operand+", "+wraps+")";
                            else rhs="ai_u32_from_i64((int64_t)"+operand+", "+wraps+")";
                        }else rhs="ai_i32_from_u32("+operand+", "+wraps+")";
                    }else if(o.kind=="integer-u64"){
                        auto source=value_types.at(o.operands[0]);auto operand=value(o.operands[0]);
                        const std::string wraps=o.wraps ? "true" : "false";
                        if(o.type.is_ulong64()){
                            rhs="ai_u64_from_i64((int64_t)"+operand+", "+wraps+")";
                        }else if(source.is_ulong64()){
                            if(o.type==Type::long_integer)rhs="ai_i64_from_u64("+operand+", "+wraps+")";
                            else if(o.type==Type::integer)rhs="ai_i32_from_u64("+operand+", "+wraps+")";
                            else rhs="ai_u32_from_u64("+operand+", "+wraps+")";
                        }else fail("AI3100","Unverified UInt64 conversion in native emission.",o.at);
                    }else if(o.kind=="integer-enum" || o.kind=="enum-integer")rhs="("+storage_ctype(o.type)+")"+value(o.operands[0]);
                    else if(o.kind=="integer-decimal"){auto operand=value(o.operands[0]);auto source=value_types.at(o.operands[0]);rhs=source==Type::long_integer ? "ai_dec_from_i64("+operand+")" : source.is_ulong64() ? "ai_dec_from_u64("+operand+")" : source.is_uint32() ? "ai_dec_from_u32("+operand+")" : "ai_dec_from_i32("+operand+")";}
                    else if(o.kind=="float-decimal")rhs="ai_dec_from_float("+value(o.operands[0])+")";
                    else if(o.kind=="double-decimal")rhs="ai_dec_from_double("+value(o.operands[0])+")";
                    else if(o.kind=="decimal-integer"){auto operand=value(o.operands[0]);if(o.type==Type::integer)rhs="ai_i32_from_dec("+operand+")";else if(o.type==Type::long_integer)rhs="ai_i64_from_dec("+operand+")";else if(o.type.is_uint32())rhs="ai_u32_from_dec("+operand+")";else if(o.type.is_ulong64())rhs="ai_u64_from_dec("+operand+")";else if(o.type.narrow_integer() || o.type.is_char()){const auto* small=o.type.narrow_integer();const long long minimum=small?small->minimum:0;const long long maximum=small?small->maximum:65535;rhs="("+storage_ctype(o.type)+")ai_small_from_dec("+operand+", "+std::to_string(minimum)+", "+std::to_string(maximum)+")";}else fail("AI3100","Unverified Decimal conversion in native emission.",o.at);}
                    else if(o.kind=="decimal-float")rhs="ai_f32_from_dec("+value(o.operands[0])+")";
                    else if(o.kind=="decimal-double")rhs="ai_f64_from_dec("+value(o.operands[0])+")";
                    else if(o.kind=="integer-float")rhs="(float)"+value(o.operands[0]);
                    else if(o.kind=="integer-double")rhs="(double)"+value(o.operands[0]);
                    else if(o.kind=="float-double")rhs="(double)"+value(o.operands[0]);
                    else if(o.kind=="double-float")rhs="ai_f32_from_f64("+value(o.operands[0])+", "+std::string(o.wraps ? "true" : "false")+")";
                    else if(o.kind=="double-integer"){
                        auto operand=value(o.operands[0]);const std::string wraps=o.wraps ? "true" : "false";
                        if(o.type==Type::integer)rhs="ai_i32_from_f64("+operand+", "+wraps+")";
                        else if(o.type==Type::long_integer)rhs="ai_i64_from_f64("+operand+", "+wraps+")";
                        else if(o.type.is_uint32())rhs="ai_u32_from_f64("+operand+", "+wraps+")";
                        else if(o.type.is_ulong64())rhs="ai_u64_from_f64("+operand+", "+wraps+")";
                        else if(o.type.narrow_integer() || o.type.is_char()){const auto* small=o.type.narrow_integer();const long long minimum=small ? small->minimum : 0;const long long maximum=small ? small->maximum : 65535;rhs="("+storage_ctype(o.type)+")ai_small_from_f64("+operand+", "+std::to_string(minimum)+", "+std::to_string(maximum)+", "+wraps+")";}
                        else fail("AI3100","Unverified Double conversion in native emission.",o.at);
                    }
                    else if(o.kind=="float-integer"){
                        auto operand=value(o.operands[0]);const std::string wraps=o.wraps ? "true" : "false";
                        if(o.type==Type::integer)rhs="ai_i32_from_f32("+operand+", "+wraps+")";
                        else if(o.type==Type::long_integer)rhs="ai_i64_from_f32("+operand+", "+wraps+")";
                        else if(o.type.is_uint32())rhs="ai_u32_from_f32("+operand+", "+wraps+")";
                        else if(o.type.is_ulong64())rhs="ai_u64_from_f32("+operand+", "+wraps+")";
                        else if(o.type.narrow_integer() || o.type.is_char()){const auto* small=o.type.narrow_integer();const long long minimum=small ? small->minimum : 0;const long long maximum=small ? small->maximum : 65535;rhs="("+storage_ctype(o.type)+")ai_small_from_f32("+operand+", "+std::to_string(minimum)+", "+std::to_string(maximum)+", "+wraps+")";}
                        else fail("AI3100","Unverified Single conversion in native emission.",o.at);
                    }
                    else if(o.kind=="variant-tag"){auto type=local_types.at(o.place);rhs=type.is_reference() ? (type.pointee().is_payload_union() ? place(o.place)+"->tag" : "*"+place(o.place)) : place(o.place)+".tag";}
                    else if(o.kind=="variant-borrow"){auto type=local_types.at(o.place).pointee();rhs="&("+place(o.place)+"->data.c"+std::to_string(type.field_case(static_cast<std::size_t>(o.field)))+".m"+std::to_string(o.field)+")";}
                    else if(o.kind=="copy-read" || o.kind=="reference-use")rhs=place(o.place);
                    else if(o.kind=="borrow-shared" || o.kind=="borrow-exclusive")rhs="&"+place(o.place);
                    else if(o.kind.starts_with("reborrow-"))rhs=value(o.operands[0]);
                    else if(o.kind=="reference-read" || o.kind.starts_with("reference-follow-"))rhs="*"+value(o.operands[0]);
                    else if(o.kind=="reference-field"){rhs="&("+value(o.operands[0])+"->m"+std::to_string(o.field)+")";}
                    else if(o.kind=="aggregate-pack"){if(o.operands.empty())out<<value(o.id)<<" = "<<empty(o.type)<<";\n";for(std::size_t i=0;i<o.operands.size();++i)transfer(value(o.id)+".m"+std::to_string(i),value(o.operands[i]),o.type.field_type(i),false);unwind_value(o);continue;}
                    else if(o.kind=="variant-pack"){out<<value(o.id)<<" = "<<empty(o.type)<<";\n";auto& c=o.type.definition().cases[static_cast<std::size_t>(o.field)];for(std::size_t i=0;i<c.fields.size();++i)if(c.fields[i]>=0)transfer(value(o.id)+".data.c"+std::to_string(o.field)+".m"+std::to_string(c.fields[i]),value(o.operands[i]),o.type.field_type(static_cast<std::size_t>(c.fields[i])),false);out<<value(o.id)<<".tag = "<<o.field<<";\n";if(o.field==1 && (o.type.is_core_carrier("Result") || o.type.is_core_carrier("Option"))){auto frame="ai_frame("+std::to_string(f.id)+","+std::to_string(o.id)+","+std::to_string(o.at.offset)+","+std::to_string(o.at.length)+","+std::to_string(o.at.line)+","+std::to_string(o.at.column)+")";if(o.cause_place>=0)out<<value(o.id)<<".cause = "<<place(o.cause_place)<<".cause;\nai_cause_push(&"<<value(o.id)<<".cause,"<<frame<<");\n";else out<<value(o.id)<<".cause = ai_cause_new("<<frame<<");\n";}unwind_value(o);continue;}
                    else if(o.kind=="aggregate-extract"){auto parent=value(o.operands[0]);transfer(value(o.id),parent+".m"+std::to_string(o.field),o.type,false);drop(parent,value_types.at(o.operands[0]));unwind_value(o);continue;}
                    else if(o.kind=="reference-write"){auto pointer=value(o.operands[0]);transfer("*"+pointer,value(o.operands[1]),value_types.at(o.operands[1]),true);continue;}
                    else if(o.kind=="end-reference")continue;
                    else if(o.kind=="move"){transfer(value(o.id),place(o.place),o.type,false);unwind_value(o);continue;}
                    else if(o.kind=="drop-value"){drop(value(o.operands[0]),value_types.at(o.operands[0]));unwind_remove('v',o.operands[0]);continue;}
                    else if(o.kind=="storage-dead"){drop(place(o.place),local_types.at(o.place));unwind_remove('p',o.place);continue;}
                    else if(o.kind=="write"){transfer(place(o.place),value(o.operands[0]),local_types.at(o.place),true);continue;}
                    else if(o.kind=="external-call"){rhs=o.external_name+"(";for(std::size_t i=0;i<o.operands.size();++i){if(i)rhs+=", ";rhs+=value(o.operands[i]);}rhs+=")";}
                    else if(o.kind=="static-load"){rhs=o.external_name;}
                    else if(o.kind=="string-literal"){rhs="ai_string_from_text("+o.external_name+")";}
                    else if(o.kind=="null-string"){rhs="ai_string_null()";}
                    else if(o.kind=="args-value"){rhs="ai_args_value";}
                    else if(o.kind=="object-box"){auto v=value(o.operands[0]);std::string helper=o.field==5 ? "ai_object_box_i64(" : o.field==1 ? "ai_object_box_bool(" : o.field==10 ? "ai_object_box_char(" : o.field==13 ? "ai_object_box_f32(" : o.field==14 ? "ai_object_box_f64(" : o.field==17 ? "ai_object_box_string(" : "ai_object_box_i32(";rhs=helper+v+")";}
                    else if(o.kind=="as-string"){rhs="ai_object_as_string("+value(o.operands[0])+")";}
                    else if(o.kind=="object-unbox"){auto v=value(o.operands[0]);std::string helper=o.field==5 ? "ai_object_unbox_i64(" : o.field==1 ? "ai_object_unbox_bool(" : o.field==10 ? "ai_object_unbox_char(" : o.field==13 ? "ai_object_unbox_f32(" : o.field==14 ? "ai_object_unbox_f64(" : o.field==17 ? "ai_object_unbox_string(" : "ai_object_unbox_i32(";rhs=helper+v+")";}
                    else if(o.kind=="args-length"){rhs="ai_args_length("+value(o.operands[0])+")";}
                    else if(o.kind=="args-at"){rhs="ai_args_at("+value(o.operands[0])+", "+value(o.operands[1])+")";}
                    else if(o.kind=="static-store"){out<<o.external_name<<" = "<<value(o.operands[0])<<";\n";continue;}
                    else if(o.kind=="call"){rhs=(o.function==-2 ? "ai_buffer_create" : o.function==-3 ? (unwinding ? "ai_unwind_buffer_set" : "ai_buffer_set") : o.function==-4 ? (unwinding ? "ai_unwind_buffer_get" : "ai_buffer_get") : o.function==-5 ? (unwinding ? "ai_unwind_buffer_length" : "ai_buffer_length") : o.function==-6 ? "ai_buffer_size" : o.function==-7 ? "ai_buffer_peek" : o.function==-8 ? "ai_buffer_write" : o.function==-9 ? "ai_file_read" : o.function==-10 ? "ai_file_write" : o.function==-11 ? "ai_string_bytes" : o.function==-12 ? "ai_int_text" : "f"+std::to_string(o.function))+"(";for(std::size_t i=0;i<o.operands.size();++i){if(i)rhs+=", ";auto type=value_types.at(o.operands[i]);auto argument=value(o.operands[i]);rhs+=unwinding && !type.is_copy() && (o.function>=0 || type==Type::buffer) ? "ai_unwind_take<"+storage_ctype(type)+">("+argument+", "+empty(type)+")" : argument;}rhs+=")";}
                    else if(o.kind.starts_with("binary:"))rhs=binary(o,value_types);
                    else if(o.kind.starts_with("unary:")){auto operation=o.kind.substr(6),a=value(o.operands[0]);rhs=o.type.is_decimal() ? (operation=="-" ? "ai_dec_neg("+a+")" : a) : operation=="-" ? std::string(o.type==Type::long_integer ? (o.wraps ? "ai_neg64_wrap(" : "ai_neg64(") : (o.wraps ? "ai_neg_wrap(" : "ai_neg("))+a+")" : "("+operation+a+")";}
                    else continue;
                    if(o.type!=Type::unit)out<<value(o.id)<<" = ";out<<rhs<<";\n";
                    unwind_value(o);
                    if(o.kind=="call" && !unwinding)for(int argument:o.operands){if((o.function>=0 || value_types.at(argument)==Type::buffer) && !value_types.at(argument).is_copy())out<<value(argument)<<" = "<<empty(value_types.at(argument))<<";\n";}
                }
                if(b.end=="match-fault"){out<<"ai_fail(\"AISharp.MatchExhaustionException\");\n";}
                else if(b.end=="return"){if(!unwinding)out<<"--ai_depth; ";out<<"return";if(b.result>=0){auto type=value_types.at(b.result);out<<' '<<(unwinding && !type.is_copy() ? "ai_unwind_take<"+storage_ctype(type)+">("+value(b.result)+", "+empty(type)+")" : value(b.result));}out<<";\n";}
                else if(b.successors.size()==1)edge(f,b.id,b.successors[0]);
                else if(b.end=="branch"){out<<"if ("<<value(b.condition)<<") {\n";edge(f,b.id,b.successors[0]);out<<"} else {\n";edge(f,b.id,b.successors[1]);out<<"}\n";}
            }out<<"}\n";
        }
        auto entry=std::find_if(functions.begin(),functions.end(),[&](auto& f){return f.id==main;});
        if(entry->fallible){
            auto& cases=entry->type.definition().cases;int success=cases[0].fields[0];
            if(ai_args_used)out<<"int main(int argc,char** argv) {\nai_args_value = ai_args_from_command_line(argc,argv);\n";else out<<"int "<<(unwinding ? "ai_entry" : "main")<<"(void) {\n";out<<storage_ctype(entry->type)<<" outcome = f"<<main<<"();\nif(outcome.tag==1) {\nai_cause cause = outcome.cause;\n";drop("outcome",entry->type);
            out<<"int reported = fputs(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/2\\\",\\\"outcome\\\":\\\"failed\\\",\\\"errorType\\\":\\\""<<entry->error_type.label()<<"\\\",\\\"cause\\\":\",stdout);\nint cause_reported = ai_cause_report(&cause);\nint memory = ai_memory_report(); int end = fputs(\"}\\n\",stdout);\nreturn reported<0 || cause_reported<0 || memory<0 || end<0 || fflush(stdout)!=0 ? 74 : 71;\n}\nif(outcome.tag!=0) ai_fail(\"AISharp.MatchExhaustionException\");\n";
            out<<storage_ctype(entry->success_type)<<" result = outcome.data.c0.m"<<success<<";\n";drop("outcome",entry->type);
        if(entry->success_type==Type::integer)out<<"int reported = printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/2\\\",\\\"outcome\\\":\\\"returned\\\",\\\"value\\\":%\" PRId32 \"\", result);\n";
            else out<<"int reported = printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/2\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"bool\\\",\\\"value\\\":%s\", result ? \"true\" : \"false\");\n";
            out<<"int memory = ai_memory_report(); int end = fputs(\"}\\n\",stdout);\nreturn reported<0 || memory<0 || end<0 || fflush(stdout)!=0 ? 74 : 0;\n}\n";unwind_main();return out.str();
        }
        if(ai_args_used)out<<"int main(int argc,char** argv) {\nai_args_value = ai_args_from_command_line(argc,argv);\n";else out<<"int "<<(unwinding ? "ai_entry" : "main")<<"(void) {\n";out<<ctype(entry->type)<<" result = f"<<main<<"();\n";
        if(entry->type==Type::integer)out<<"int reported = printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"value\\\":%\" PRId32 \"\", result);\n";
        else out<<"int reported = printf(\"{\\\"schemaVersion\\\":\\\"aisharp.native-result/1\\\",\\\"outcome\\\":\\\"returned\\\",\\\"type\\\":\\\"bool\\\",\\\"value\\\":%s\", result ? \"true\" : \"false\");\n";
        out<<"int memory = ai_memory_report(); int end = fputs(\"}\\n\", stdout);\nreturn reported < 0 || memory < 0 || end < 0 || fflush(stdout) != 0 ? 74 : 0;\n}\n";unwind_main();return out.str();
    }
};
std::vector<IrFunction> verified_storage(std::string_view source){
    if(source.size()>1024*1024)fail("AI3100","Native bootstrap source limit is 1 MiB.",{});
    auto functions=Parser(source).run();Binder().run(functions);std::vector<IrFunction> ir;
    // Preserve declaration order; the entry identity is recorded separately below.
    for(auto& f:functions)if(!f.external && !f.abstract_method)ir.push_back(StorageLowerer().run(f));StorageVerifier().run(ir);return ir;
}
