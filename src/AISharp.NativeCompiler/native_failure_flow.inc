// Closed-module outcome obligations are checked through real call contexts.
// Values/cells retain producer identity across copies, projections and borrows.
// No scalar, buffer byte, address or source text is serialized by this analysis.
class NativeFailureFlow {
    using Path=std::vector<int>;
    struct Cell {int context=0;int root=0;Path path;bool operator==(const Cell&)const=default;bool operator<(const Cell& o)const{return std::tie(context,root,path)<std::tie(o.context,o.root,o.path);}};
    struct Predicate {Cell source;int selected=0;bool equal=true;bool operator==(const Predicate&)const=default;};
    struct Value {
        Type type=Type::unit;std::map<Path,std::set<int>> tokens;std::map<Path,Cell> pointers;std::map<Path,std::set<int>> tags;std::map<Path,std::int64_t> scalars;std::optional<Cell> tag_source;std::optional<Predicate> predicate;
    };
    struct Producer {int context=0;int operation=-1;Path component;int instance=0;Location at,declaration;bool operator==(const Producer&)const=default;};
    struct Frame {int function=0,block=0,position=0,previous=-1,call=-1;int context=0;bool operator==(const Frame&)const=default;};
    struct State {std::vector<Frame> stack;std::map<Cell,Value> cells,values;std::map<int,Producer> pending;int external=0;};
    std::map<int,std::vector<std::vector<std::set<int>>>> live_values;
    void prepare_liveness(){for(auto& f:functions){auto count=f.blocks.size();std::vector<std::set<int>> input(count),output(count);bool changed=true;while(changed){changed=false;for(auto it=f.blocks.rbegin();it!=f.blocks.rend();++it){std::set<int> after;for(int next:it->successors)after.insert(input[static_cast<std::size_t>(next)].begin(),input[static_cast<std::size_t>(next)].end());auto before=after;if(it->condition>=0)before.insert(it->condition);if(it->result>=0)before.insert(it->result);for(auto op=it->ops.rbegin();op!=it->ops.rend();++op){charge(1+before.size());before.erase(op->id);before.insert(op->operands.begin(),op->operands.end());}auto i=static_cast<std::size_t>(it->id);if(before!=input[i] || after!=output[i]){input[i]=std::move(before);output[i]=std::move(after);changed=true;}}}auto& points=live_values[f.id];points.resize(count);std::size_t nodes=0;for(auto& b:f.blocks){auto live=output[static_cast<std::size_t>(b.id)];if(b.condition>=0)live.insert(b.condition);if(b.result>=0)live.insert(b.result);auto& at=points[static_cast<std::size_t>(b.id)];at.resize(b.ops.size()+1);at.back()=live;nodes+=live.size();for(std::size_t i=b.ops.size();i>0;--i){live.erase(b.ops[i-1].id);live.insert(b.ops[i-1].operands.begin(),b.ops[i-1].operands.end());nodes+=live.size();if(nodes>1000000)fail("AI3100","Native failure SSA workspace limit exceeded.",b.at);at[i-1]=live;}}}}
    void trim(State& state){for(auto& frame:state.stack){auto& live=live_values.at(frame.function)[static_cast<std::size_t>(frame.block)][static_cast<std::size_t>(frame.position)];for(auto it=state.values.begin();it!=state.values.end();)if(it->first.context==frame.context && !live.contains(it->first.root))it=state.values.erase(it);else ++it;}}
    const std::vector<IrFunction>& functions;std::map<int,const IrFunction*> signatures;std::size_t work=0,facts=0,text_bytes=0;std::map<std::tuple<int,int,int>,int> contexts;
    int context(int parent,int call,int function){auto [p,added]=contexts.emplace(std::tuple{parent,call,function},static_cast<int>(contexts.size()+1));if(added && contexts.size()>65536)fail("AI3100","Native failure call-context limit exceeded.",{});return p->second;}
    void charge(std::size_t n=1){if(n>16000000-work)fail("AI3100","Native failure obligation analysis work limit exceeded.",{});work+=n;}
    const IrFunction& fn(int id)const{return *signatures.at(id);}
    static bool prefix(const Path& a,const Path& b){return a.size()<=b.size() && std::equal(a.begin(),a.end(),b.begin());}
    static Path tail(const Path& p,std::size_t count){return Path(p.begin()+static_cast<std::ptrdiff_t>(count),p.end());}
    Type path_type(Type t,const Path& path){for(int step:path){charge();t=step<0 ? t.pointee() : t.field_type(static_cast<std::size_t>(step));}return t;}
    Value unknown(Type t){Value v;v.type=t;std::vector<std::pair<Type,Path>> todo={{t,{}}};while(!todo.empty()){auto [next,path]=std::move(todo.back());todo.pop_back();charge(1+path.size());if(next.is_reference())continue;if(next.is_union())for(std::size_t i=0;i<next.definition().cases.size();++i)v.tags[path].insert(static_cast<int>(i));if(next.is_aggregate())for(std::size_t i=0;i<next.definition().fields.size();++i){auto p=path;p.push_back(static_cast<int>(i));todo.emplace_back(next.field_type(i),std::move(p));}}return v;}
    Value project(const Value& source,const Path& path){Value v;v.type=path_type(source.type,path);for(auto& [p,x]:source.tokens)if(prefix(path,p))v.tokens.emplace(tail(p,path.size()),x);for(auto& [p,x]:source.pointers)if(prefix(path,p))v.pointers.emplace(tail(p,path.size()),x);for(auto& [p,x]:source.tags)if(prefix(path,p))v.tags.emplace(tail(p,path.size()),x);for(auto& [p,x]:source.scalars)if(prefix(path,p))v.scalars.emplace(tail(p,path.size()),x);if(path.empty()){v.tag_source=source.tag_source;v.predicate=source.predicate;}return v;}
    template<class M> void erase_paths(M& map,const Path& path){for(auto it=map.begin();it!=map.end();)if(prefix(path,it->first))it=map.erase(it);else ++it;}
    void put(Value& into,const Path& path,const Value& source){erase_paths(into.tokens,path);erase_paths(into.pointers,path);erase_paths(into.tags,path);erase_paths(into.scalars,path);auto copy=[&](auto& a,const auto& b){for(auto& [p,x]:b){auto nested=path;nested.insert(nested.end(),p.begin(),p.end());a[nested]=x;}};copy(into.tokens,source.tokens);copy(into.pointers,source.pointers);copy(into.tags,source.tags);copy(into.scalars,source.scalars);if(path.empty()){into.tag_source=source.tag_source;into.predicate=source.predicate;}}
    Cell place(const Frame& frame,int id){auto& f=fn(frame.function);IrPlaceGraph g(f.places);return {frame.context,g.root(id),g.path(id)};}
    Cell value_cell(const Frame& frame,int id)const{return {frame.context,id,{}};}
    Value read(State& s,Cell cell,Type type){Cell root=cell;root.path.clear();auto p=s.cells.find(root);if(p==s.cells.end())return unknown(type);auto v=project(p->second,cell.path);std::vector<Path> references;if(v.type.is_reference())references.push_back({});else if(v.type.is_aggregate())references=v.type.definition().reference_paths;for(auto& path:references)if(!v.pointers.contains(path)){Cell external{-1,s.external++,{}};s.cells.emplace(external,unknown(path_type(v.type,path).pointee()));v.pointers[path]=external;auto at=cell.path;at.insert(at.end(),path.begin(),path.end());p->second.pointers[at]=external;}return v;}
    void write(State& s,Cell cell,const Value& v){Cell root=cell;root.path.clear();auto p=s.cells.find(root);if(p==s.cells.end()){if(!cell.path.empty())fail("AI3100","Failure analysis lost a storage root.",{});s.cells[root]=v;}else put(p->second,cell.path,v);}
    Value get(State& s,const Frame& frame,int id){auto p=s.values.find(value_cell(frame,id));if(p==s.values.end())fail("AI3100","Failure analysis lost an SSA value.",{});return p->second;}
    Type place_type(const Frame& frame,int id){auto& f=fn(frame.function);auto p=std::find_if(f.places.begin(),f.places.end(),[&](auto& x){return x.id==id;});if(p==f.places.end())fail("AI3100","Failure analysis lost a place.",{});return p->type;}
    Cell pointer(State& s,Value& v){auto p=v.pointers.find({});if(p!=v.pointers.end())return p->second;Cell external{-1,s.external++,{}};s.cells.emplace(external,unknown(v.type.pointee()));v.pointers[{}]=external;return external;}
    void clear(State& s,int token){s.pending.erase(token);for(auto* storage:{&s.cells,&s.values})for(auto& [cell,v]:*storage){(void)cell;charge(1+v.tokens.size());for(auto it=v.tokens.begin();it!=v.tokens.end();){it->second.erase(token);if(it->second.empty())it=v.tokens.erase(it);else ++it;}}}
    void handle(State& s,const Value& v){auto p=v.tokens.find({});if(p==v.tokens.end())return;auto tokens=p->second;for(int token:tokens)clear(s,token);}
    bool present(Type t,const Path& path,const Value& v){Path at;for(int field:path){if(t.is_payload_union()){auto tag=v.tags.find(at);if(tag!=v.tags.end() && !tag->second.contains(t.field_case(static_cast<std::size_t>(field))))return false;}t=t.field_type(static_cast<std::size_t>(field));at.push_back(field);}return true;}
    void produce(State& s,Value& v,const Frame& caller,const IrOp& call,const IrFunction& target){std::vector<std::pair<Type,Path>> todo={{v.type,{}}};while(!todo.empty()){auto [t,path]=std::move(todo.back());todo.pop_back();charge(1+path.size());if(t.is_reference())continue;if(t.is_core_carrier("Result") && present(v.type,path,v)){int token=0;while(s.pending.contains(token)){charge();++token;}if(token>=65536)fail("AI3100","Native pending outcome limit exceeded.",call.at);int instance=0;for(auto& [id,p]:s.pending){(void)id;if(p.context==caller.context && p.operation==call.id && p.component==path)instance=std::max(instance,p.instance+1);}s.pending.emplace(token,Producer{caller.context,call.id,path,instance,call.at,target.fallible ? target.failure_declaration : target.declaration});v.tokens[path].insert(token);}if(t.is_aggregate())for(std::size_t i=0;i<t.definition().fields.size();++i){auto p=path;p.push_back(static_cast<int>(i));todo.emplace_back(t.field_type(i),std::move(p));}}}
    void refine(State& s,const Predicate& condition,bool truth){auto& cell=condition.source;auto v=read(s,cell,Type::unit);if(!v.type.is_union())return;auto tags=v.tags[{}];bool equal=truth==condition.equal;if(equal){if(!tags.empty() && !tags.contains(condition.selected)){s.stack.clear();return;}tags={condition.selected};}else{tags.erase(condition.selected);if(tags.empty()){s.stack.clear();return;}}
        std::set<int> absent;for(auto& [path,tokens]:v.tokens)if(!path.empty() && v.type.is_payload_union() && !tags.contains(v.type.field_case(static_cast<std::size_t>(path[0]))))absent.insert(tokens.begin(),tokens.end());for(int token:absent)clear(s,token);v=read(s,cell,v.type);v.tags[{}]=tags;write(s,cell,v);
    }
    bool contains(const Value& v,int token)const{for(auto& [path,tokens]:v.tokens){(void)path;if(tokens.contains(token))return true;}return false;}
    [[noreturn]] void lost(const Producer& producer,Location at){Diagnostic d("AI3133","An unhandled native outcome is lost on a normal exit.",at);d.related.push_back({"failure-producer",producer.at});d.related.push_back({"failure-declaration",producer.declaration});throw d;}
    void check_lost(const State& s,Location at,const Value* returned=nullptr){for(auto& [token,p]:s.pending){bool found=returned && contains(*returned,token);for(auto& [cell,v]:s.cells){(void)cell;charge();found=found || contains(v,token);}for(auto& [cell,v]:s.values){(void)cell;charge();found=found || contains(v,token);}if(!found)lost(p,at);}}
    std::optional<std::int64_t> scalar(const Value& v)const{auto p=v.scalars.find({});return p==v.scalars.end() ? std::nullopt : std::optional<std::int64_t>{p->second};}
    std::optional<std::int64_t> binary_value(const IrOp& o,const Value& a,const Value& b){auto x=scalar(a),y=scalar(b);if(!x || !y)return std::nullopt;auto op=o.kind.substr(7);std::int64_t n=0;
        if(op=="==")return *x==*y;if(op=="!=")return *x!=*y;if(op=="<")return *x<*y;if(op==">")return *x>*y;if(op=="<=")return *x<=*y;if(op==">=")return *x>=*y;
        if(o.type==Type::long_integer){
            const auto* operation=native_int64_operator(op);
            if(!operation)return std::nullopt;
            const auto result=ai_i64_binary(operation->id,*x,*y,o.wraps);
            return result.error==ai_i64_ok ? std::optional<std::int64_t>{result.value} : std::nullopt;
        }
        if(op=="+")n=*x+*y;else if(op=="-")n=*x-*y;else if(op=="*")n=*x**y;else if(op=="/"){if(*y==0 || (*x==INT32_MIN && *y==-1))return std::nullopt;n=*x / *y;}else if(op=="%"){if(*y==0 || (*x==INT32_MIN && *y==-1))return std::nullopt;n=*x % *y;}else if(op=="&")n=*x & *y;else if(op=="|")n=*x | *y;else if(op=="^")n=*x ^ *y;else return native_int32_shift(op,*x,*y);
        if(n<INT32_MIN || n>INT32_MAX){if(!o.wraps)return std::nullopt;n=static_cast<std::int32_t>(static_cast<std::uint32_t>(n));}return n;
    }
    // Retained-state facts include nested token/tag/reference paths. The conservative
    // bound must reject before the x86 Debug allocator exhausts its address space.
    // Structural keys deliberately omit scalar values; repeated loop states widen
    // only those scalar facts that disagree, retaining every outcome/alias fact.
    std::string key(const State& s){std::ostringstream out;auto path=[&](const Path& p){out<<'[';for(int n:p)out<<n<<',';out<<']';};auto cell=[&](const Cell& c){out<<c.context<<':'<<c.root;path(c.path);};for(auto& f:s.stack){out<<'F'<<f.function<<':'<<f.block<<':'<<f.position<<':'<<f.previous<<':'<<f.call;out<<':'<<f.context<<';';}for(auto& [token,p]:s.pending){out<<'T'<<token<<':'<<p.operation<<':'<<p.instance;out<<':'<<p.context<<';';path(p.component);}auto values=[&](const auto& map){for(auto& [c,v]:map){cell(c);out<<'t';auto type=v.type;while(type.is_reference()){out<<(type.borrow_kind()==Type::Borrow::shared ? 's' : 'm');type=type.pointee();}if(type.has_product_root())out<<'p'<<type.product_id();else if(auto small=type.narrow_integer())out<<small->reference_tag;else out<<(type==Type::integer ? 'i' : type==Type::long_integer ? 'l' : type==Type::boolean ? 'b' : type==Type::buffer ? 'o' : 'u');out<<';';for(auto& [p,tokens]:v.tokens){path(p);for(int n:tokens)out<<n<<',';}out<<'p';for(auto& [p,x]:v.pointers){path(p);cell(x);}out<<'g';for(auto& [p,tags]:v.tags){path(p);for(int n:tags)out<<n<<',';}if(v.tag_source){out<<'d';cell(*v.tag_source);}if(v.predicate){out<<'q';cell(v.predicate->source);out<<v.predicate->selected<<':'<<v.predicate->equal;}}};values(s.cells);out<<'V';values(s.values);return out.str();}
    bool widen(State& old,const State& incoming){bool changed=false;auto values=[&](auto& into,const auto& from){for(auto& [cell,v]:into){auto p=from.find(cell);if(p==from.end())continue;for(auto it=v.scalars.begin();it!=v.scalars.end();){auto q=p->second.scalars.find(it->first);if(q==p->second.scalars.end() || q->second!=it->second){it=v.scalars.erase(it);changed=true;}else ++it;}}};values(old.cells,incoming.cells);values(old.values,incoming.values);return changed;}
public:
    explicit NativeFailureFlow(const std::vector<IrFunction>& fs):functions(fs){for(auto& f:fs)signatures.emplace(f.id,&f);}
    void run();
};

void NativeFailureFlow::run(){
    bool relevant=false;for(auto& f:functions)for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind=="call" && o.function>=0){std::vector<Type> todo={o.type};while(!todo.empty()){auto t=todo.back();todo.pop_back();charge();if(t.is_reference())continue;if(t.is_core_carrier("Result"))relevant=true;if(t.is_aggregate())for(std::size_t i=0;i<t.definition().fields.size();++i)todo.push_back(t.field_type(i));}}
    if(!relevant)return;
    prepare_liveness();
    for(auto& root_function:functions){
        State initial;Frame root;root.function=root_function.id;root.context=context(0,-1,root_function.id);initial.stack.push_back(root);
        for(int id:root_function.parameters){auto type=place_type(root,id);initial.cells[place(root,id)]=unknown(type);}
        std::vector<State> todo;todo.push_back(std::move(initial));std::map<std::string,State> visited;
        auto enqueue=[&](State state){
            if(state.stack.empty())return;trim(state);charge(1+state.cells.size()+state.values.size()+state.pending.size());auto code=key(state);if(code.size()>4*1024*1024)fail("AI3100","Native failure state text limit exceeded.",{});
            auto previous=visited.find(code);
            if(previous==visited.end()){
                auto retain=[&](std::size_t n){if(n>500000-facts)fail("AI3100","Native failure analysis workspace limit exceeded.",{});facts+=n;};
                retain(1+state.cells.size()+state.values.size()+state.pending.size());
                for(auto* storage:{&state.cells,&state.values})for(auto& [cell,v]:*storage){
                    retain(cell.path.size()+v.tokens.size()+v.pointers.size()+v.tags.size()+v.scalars.size());
                    for(auto& [path,tokens]:v.tokens)retain(path.size()+tokens.size());
                    for(auto& [path,tags]:v.tags)retain(path.size()+tags.size());
                    for(auto& [path,pointer]:v.pointers)retain(path.size()+pointer.path.size());
                    for(auto& [path,scalar]:v.scalars){(void)scalar;retain(path.size());}
                }
                if(code.size()>16*1024*1024-text_bytes)fail("AI3100","Native failure analysis text workspace limit exceeded.",{});text_bytes+=code.size();
                visited.emplace(std::move(code),state);todo.push_back(std::move(state));
            }else if(widen(previous->second,state))todo.push_back(previous->second);
        };
        while(!todo.empty()){
            State state=std::move(todo.back());todo.pop_back();charge();if(state.stack.empty())continue;auto frame=state.stack.back();auto& f=fn(frame.function);auto& block=f.blocks[static_cast<std::size_t>(frame.block)];
            if(static_cast<std::size_t>(frame.position)<block.ops.size()){
                auto& o=block.ops[static_cast<std::size_t>(frame.position)];++state.stack.back().position;Value result=unknown(o.type);auto operand=[&](std::size_t i){return get(state,frame,o.operands.at(i));};auto slot=[&](){return place(frame,o.place);};
                if(o.kind=="parameter"){}
                else if(o.kind=="storage-live")state.cells[slot()]=unknown(place_type(frame,o.place));
                else if(o.kind=="storage-dead"){auto cell=slot();state.cells.erase(cell);}
                else if(o.kind=="constant"){result.scalars[{}]=o.literal;if(o.type.is_union())result.tags[{}]={static_cast<int>(o.literal)};}
                else if(o.kind=="copy-read" || o.kind=="move" || o.kind=="reference-use"){result=read(state,slot(),o.type);if(o.kind=="move")write(state,slot(),unknown(o.type));}
                else if(o.kind=="borrow-shared" || o.kind=="borrow-exclusive")result.pointers[{}]=slot();
                else if(o.kind.starts_with("reborrow-"))result=operand(0);
                else if(o.kind=="reference-field"){auto v=operand(0);auto cell=pointer(state,v);state.values[value_cell(frame,o.operands[0])]=v;cell.path.push_back(o.field);result.pointers[{}]=cell;}
                else if(o.kind=="reference-read" || o.kind.starts_with("reference-follow-")){auto v=operand(0);auto cell=pointer(state,v);state.values[value_cell(frame,o.operands[0])]=v;result=read(state,cell,o.type);}
                else if(o.kind=="variant-borrow"){auto type=place_type(frame,o.place);auto v=read(state,slot(),type);auto cell=pointer(state,v);write(state,slot(),v);cell.path.push_back(o.field);result.pointers[{}]=cell;}
                else if(o.kind=="variant-tag"){
                    auto type=place_type(frame,o.place);auto cell=slot();auto v=read(state,cell,type);if(type.is_reference()){cell=pointer(state,v);write(state,slot(),v);v=read(state,cell,type.pointee());}if(v.type.is_core_carrier("Result"))handle(state,v);v=read(state,cell,v.type);auto tags=v.tags.find({});if(tags!=v.tags.end() && tags->second.size()==1)result.scalars[{}]=*tags->second.begin();result.tag_source=cell;
                }
                else if(o.kind=="write"){auto v=operand(0);write(state,slot(),v);if(!v.type.is_copy())state.values[value_cell(frame,o.operands[0])]=unknown(v.type);}
                else if(o.kind=="reference-write"){auto pointer_value=operand(0),v=operand(1);auto cell=pointer(state,pointer_value);state.values[value_cell(frame,o.operands[0])]=pointer_value;write(state,cell,v);if(!v.type.is_copy())state.values[value_cell(frame,o.operands[1])]=unknown(v.type);}
                else if(o.kind=="aggregate-pack" || o.kind=="variant-pack"){
                    if(o.kind=="variant-pack"){result.tags.clear();result.tags[{}]={o.field};}
                    for(std::size_t i=0;i<o.operands.size();++i){auto v=operand(i);int field=o.kind=="variant-pack" ? o.type.definition().cases[static_cast<std::size_t>(o.field)].fields[i] : static_cast<int>(i);if(field>=0)put(result,{field},v);if(!v.type.is_copy())state.values[value_cell(frame,o.operands[i])]=unknown(v.type);}
                }
                else if(o.kind=="aggregate-extract"){auto v=operand(0);result=project(v,{o.field});state.values[value_cell(frame,o.operands[0])]=unknown(v.type);}
                else if(o.kind=="drop-value" || o.kind=="end-reference"){auto v=operand(0);state.values[value_cell(frame,o.operands[0])]=unknown(v.type);}
                else if(o.kind=="phi"){auto it=std::find(o.incoming.begin(),o.incoming.end(),frame.previous);if(it==o.incoming.end())fail("AI3100","Failure analysis phi edge is absent.",o.at);result=operand(static_cast<std::size_t>(it-o.incoming.begin()));}
                else if(o.kind=="integer-widen" || o.kind=="integer-convert"){
                    if(auto n=scalar(operand(0))){
                        if(o.type==Type::long_integer || (*n>=INT32_MIN && *n<=INT32_MAX))result.scalars[{}]=*n;
                        else if(o.wraps)result.scalars[{}]=wrapped(*n);
                    }
                }
                else if(o.kind=="integer-narrow"){
                    auto small=o.type.narrow_integer(); // Verified conversion destination.
                    if(auto n=scalar(operand(0))){
                        if(o.wraps)result.scalars[{}]=ai_small_wrap(*n,small->minimum,small->maximum);
                        else if(*n>=small->minimum && *n<=small->maximum)result.scalars[{}]=*n;
                    }
                }
                else if(o.kind.starts_with("unary:")){
                    if(auto n=scalar(operand(0))){
                        auto op=o.kind.substr(6);
                        if(op=="+")result.scalars[{}]=*n;
                        else if(op=="!")result.scalars[{}]=!*n;
                        else if(op=="~"){
                            if(o.type==Type::long_integer)result.scalars[{}]=ai_i64_bits(~static_cast<std::uint64_t>(*n));
                            else result.scalars[{}]=std::bit_cast<std::int32_t>(~static_cast<std::uint32_t>(*n));
                        }else if(op=="-"){
                            if(o.type==Type::long_integer){
                                auto negated=ai_i64_binary(ai_i64_sub,0,*n,o.wraps);
                                if(negated.error==ai_i64_ok)result.scalars[{}]=negated.value;
                            }else if(*n!=INT32_MIN || o.wraps)result.scalars[{}]=wrapped(-*n);
                        }
                    }
                }
                else if(o.kind.starts_with("binary:")){auto a=operand(0),b=operand(1);if(auto n=binary_value(o,a,b))result.scalars[{}]=*n;auto op=o.kind.substr(7);if((op=="==" || op=="!=") && a.tag_source && scalar(b))result.predicate=Predicate{*a.tag_source,static_cast<int>(*scalar(b)),op=="=="};}
                else if(o.kind=="call" && o.function>=0){
                    // Runtime recursion has the same 128-frame terminal boundary.
                    if(state.stack.size()>=128)continue;
                    auto& target=fn(o.function);Frame child;child.function=target.id;child.call=o.id;child.context=context(frame.context,o.id,target.id);
                    for(std::size_t i=0;i<o.operands.size();++i){auto v=operand(i);state.cells[place(child,target.parameters[i])]=v;if(!v.type.is_copy())state.values[value_cell(frame,o.operands[i])]=unknown(v.type);}
                    state.values.erase(value_cell(frame,o.id));state.stack.push_back(child);enqueue(std::move(state));continue;
                }
                else if(o.kind=="call"){for(int id:o.operands){auto v=get(state,frame,id);if(!v.type.is_copy())state.values[value_cell(frame,id)]=unknown(v.type);}}
                if(o.type!=Type::unit){result.type=o.type;state.values[value_cell(frame,o.id)]=std::move(result);}else state.values[value_cell(frame,o.id)]=unknown(Type::unit);
                enqueue(std::move(state));continue;
            }
            if(block.end=="match-fault" || block.end=="unreachable")continue;
            if(block.end=="return"){
                Value returned=block.result>=0 ? get(state,frame,block.result) : unknown(Type::unit);
                for(auto it=state.cells.begin();it!=state.cells.end();)if(it->first.context==frame.context)it=state.cells.erase(it);else ++it;
                for(auto it=state.values.begin();it!=state.values.end();)if(it->first.context==frame.context)it=state.values.erase(it);else ++it;
                state.stack.pop_back();if(state.stack.empty()){for(auto& [token,p]:state.pending){bool forwarded=contains(returned,token);for(auto& [cell,v]:state.cells)if(cell.context<0)forwarded=forwarded || contains(v,token);if(!forwarded)lost(p,block.at);}continue;}
                auto caller=state.stack.back();auto& caller_block=fn(caller.function).blocks[static_cast<std::size_t>(caller.block)];auto& call=caller_block.ops[static_cast<std::size_t>(caller.position-1)];if(call.id!=frame.call)fail("AI3100","Native failure call/return context mismatch.",block.at);
                produce(state,returned,caller,call,f);state.values[value_cell(caller,call.id)]=std::move(returned);check_lost(state,call.at);enqueue(std::move(state));continue;
            }
            auto go=[&](State next,int id){auto& top=next.stack.back();top.previous=top.block;top.block=id;top.position=0;enqueue(std::move(next));};
            if(block.successors.size()==1){go(std::move(state),block.successors[0]);continue;}
            if(block.end=="branch" && block.successors.size()==2){auto condition=get(state,frame,block.condition);auto known=scalar(condition);for(int side=0;side<2;++side){bool truth=side==0;if(known && (*known!=0)!=truth)continue;auto next=state;if(condition.predicate)refine(next,*condition.predicate,truth);if(!next.stack.empty())go(std::move(next),block.successors[static_cast<std::size_t>(side)]);}continue;}
            fail("AI3100","Failure analysis found an invalid terminal.",block.at);
        }
    }
}
void verify_native_failures(const std::vector<IrFunction>& functions){NativeFailureFlow(functions).run();}
