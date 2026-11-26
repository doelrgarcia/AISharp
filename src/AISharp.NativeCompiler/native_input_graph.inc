// Recomputed finite input graph. Exact prefixes and cyclic tail nodes are distinct.
template<class Charge> void build_native_input_graph(IrFunction& f,Charge& charge){
    f.inputs.clear();f.external_places.clear();f.external_paths.clear();f.external_ids.clear();f.external_history.clear();f.summary_places.clear();f.summary_anchors.clear();f.input_rebuild=false;
    IrPlaceGraph local(f.places);std::map<std::pair<int,int>,int> fields;int next_root=-1000000,next_field=-2000000,next_loan=-3000000;std::set<int> loan_ids;std::size_t metadata_facts=0;
    auto add_place=[&](auto&& self,IrPlace place,RegionPath path,bool summary,const IrInputEvents& history)->void{
        charge(1+path.selectors.size()+history.size()+place.type.reference_depth());
        if(f.places.size()+f.external_places.size()>=65536 || path.selectors.size()>256)fail("AI3100","Native input projection region limit exceeded.",place.at);
        if((metadata_facts+=1+path.selectors.size()+history.size())>1000000)fail("AI3100","Native input graph metadata fact limit exceeded.",place.at);
        f.external_places.push_back(place);f.external_paths.emplace(place.id,path);if(!f.external_ids.emplace(path,place.id).second)fail("AI3100","Duplicate input graph display identity.",place.at);f.external_history.emplace(place.id,history);if(summary)f.summary_places.insert(place.id);
        if(place.type.is_aggregate())for(std::size_t i=0;i<place.type.definition().fields.size();++i){IrPlace child{};child.id=next_field--;child.type=place.type.field_type(i);child.at=place.at;child.mutable_=place.mutable_;child.parent=place.id;child.field=static_cast<int>(i);fields.emplace(std::pair{place.id,child.field},child.id);self(self,child,path.field(child.field),summary,history);}
    };
    auto add_edge=[&](int parameter,int cell,int storage,Type type,RegionPath path,bool writable,bool folded,int anchor,Location at){
        charge(1+path.selectors.size());int id=loan_ids.insert(storage).second ? storage : next_loan--;if(id!=storage)loan_ids.insert(id);
        f.inputs.push_back({parameter,path.depth(),storage,type,at,cell,std::move(path),writable,id,folded,anchor});
    };
    struct Tail {int parameter;Type type;bool writable;int anchor,storage;};std::vector<Tail> tails;
    std::function<int(int,Type,bool,int,RegionPath,Location)> tail;
    auto populate_tail=[&](auto&& self,int parameter,int cell,Type type,bool writable,int anchor,RegionPath path,Location at)->void{
        charge(1+path.selectors.size());
        if(type.is_reference()){bool access=writable && type.borrow_kind()==Type::Borrow::exclusive;auto target=path.dereference();int storage=tail(parameter,type.pointee(),access,anchor,target,at);add_edge(parameter,cell,storage,type,std::move(target),access,true,anchor,at);}
        else if(type.is_aggregate() && type.contains_references())for(std::size_t i=0;i<type.definition().fields.size();++i){auto child=type.field_type(i);if(child.contains_references())self(self,parameter,fields.at({cell,static_cast<int>(i)}),child,writable,-1-type.product_id()*128-static_cast<int>(i),path.field(static_cast<int>(i)),at);}
    };
    tail=[&](int parameter,Type type,bool writable,int anchor,RegionPath path,Location at)->int{
        for(auto& entry:tails){charge(1+type.reference_depth());if(entry.parameter==parameter && entry.type==type && entry.writable==writable && entry.anchor==anchor)return entry.storage;}
        int id=next_root--;f.summary_anchors.emplace(id,anchor);tails.push_back({parameter,type,writable,anchor,id});IrPlace place{};place.id=id;place.type=type;place.at=at;place.mutable_=writable;add_place(add_place,place,path,true,{});populate_tail(populate_tail,parameter,id,type,writable,anchor,path,at);return id;
    };
    std::vector<Type> stack;
    auto exact=[&](auto&& self,int parameter,int cell,Type type,RegionPath path,bool writable,int anchor,const IrInputEvents& history,Location at)->void{
        charge(1+path.selectors.size()+history.size()+type.reference_depth());if(path.selectors.size()>256)fail("AI3100","Native input reference selector limit exceeded.",at);
        if(type.is_reference()){
            auto target=path.dereference();auto demand=f.input_demands.find(target);bool expanded=demand!=f.input_demands.end() && demand->second.expanded;bool cycle=std::find(stack.begin(),stack.end(),type)!=stack.end();bool access=writable && type.borrow_kind()==Type::Borrow::exclusive;
            if(cycle && !expanded){int storage=tail(parameter,type.pointee(),access,anchor,target,at);add_edge(parameter,cell,storage,type,std::move(target),access,true,anchor,at);return;}
            IrInputEvents next=history;if(expanded){charge(demand->second.history.size());next.insert(demand->second.history.begin(),demand->second.history.end());}
            int storage=target.plain() ? -2-parameter*128-target.depth() : next_root--;IrPlace place{};place.id=storage;place.type=type.pointee();place.at=at;place.mutable_=access;add_place(add_place,place,target,false,next);add_edge(parameter,cell,storage,type,target,access,false,anchor,at);
            stack.push_back(type);self(self,parameter,storage,type.pointee(),target,access,anchor,next,at);stack.pop_back();
        }else if(type.is_aggregate() && type.contains_references())for(std::size_t i=0;i<type.definition().fields.size();++i){auto child=type.field_type(i);if(child.contains_references()){int id=cell>=0 ? local.child(cell,static_cast<int>(i)) : fields.at({cell,static_cast<int>(i)});self(self,parameter,id,child,path.field(static_cast<int>(i)),writable,-1-type.product_id()*128-static_cast<int>(i),history,at);}}
    };
    for(std::size_t i=0;i<f.parameters.size();++i){auto& p=local.at(f.parameters[i]);exact(exact,static_cast<int>(i),p.id,p.type,RegionPath{static_cast<int>(i),0},true,-20000-static_cast<int>(i),{},p.at);}
}

// Check the constructed graph independently of its expansion cache.
void verify_native_input_graph(const IrFunction& f){
    auto invalid=[](Location at={}){fail("AI3100","Native typed input graph invariant failed.",at);};
    auto all=f.places;all.insert(all.end(),f.external_places.begin(),f.external_places.end());IrPlaceGraph graph(all);std::map<int,const IrInputRegion*> cells;std::map<int,int> incoming_anchors;std::set<int> loans;std::set<int> external;std::size_t work=0;
    auto charge=[&](std::size_t n=1){if(n>16000000-work)fail("AI3100","Native input graph verification work limit exceeded.",{});work+=n;};
    if(f.external_places.size()!=f.external_paths.size() || f.external_places.size()!=f.external_ids.size() || f.external_places.size()!=f.external_history.size())invalid();
    for(auto& place:f.external_places){
        charge();if(place.id>=-1 || !external.insert(place.id).second)invalid(place.at);auto path=f.external_paths.find(place.id);if(path==f.external_paths.end() || path->second.first<0 || static_cast<std::size_t>(path->second.first)>=f.parameters.size() || path->second.selectors.empty() || path->second.selectors.size()>256 || path->second.depth()<1 || !f.external_ids.contains(path->second) || f.external_ids.at(path->second)!=place.id)invalid(place.at);
        Type type=graph.at(f.parameters[static_cast<std::size_t>(path->second.first)]).type;bool writable=true;
        for(int step:path->second.selectors){charge(1+type.reference_depth());if(step==-1){if(!type.is_reference())invalid(place.at);writable=writable && type.borrow_kind()==Type::Borrow::exclusive;type=type.pointee();}else{if(step<0 || !type.is_aggregate() || static_cast<std::size_t>(step)>=type.definition().fields.size())invalid(place.at);type=type.field_type(static_cast<std::size_t>(step));}}
        if(type!=place.type || place.mutable_!=writable || !f.external_history.contains(place.id))invalid(place.at);
        if(place.parent!=-1 && (!f.external_paths.contains(place.parent) || !f.external_history.contains(place.parent)))invalid(place.at);
        if(place.parent!=-1 && (f.summary_places.contains(place.id)!=f.summary_places.contains(place.parent) || f.external_history.at(place.id)!=f.external_history.at(place.parent)))invalid(place.at);
        if(f.summary_places.contains(place.id) && !f.external_history.at(place.id).empty())invalid(place.at);
    }
    for(auto& input:f.inputs){
        charge(1+input.path.selectors.size());if(input.parameter<0 || static_cast<std::size_t>(input.parameter)>=f.parameters.size() || !graph.contains(input.cell) || !external.contains(input.storage) || input.loan_id>=-1 || input.structural_event>=0 || input.structural_event< -20127 || !loans.insert(input.loan_id).second || !cells.emplace(input.cell,&input).second)invalid(input.at);
        auto [anchor,new_anchor]=incoming_anchors.emplace(input.storage,input.structural_event);if(!new_anchor && anchor->second!=input.structural_event)invalid(input.at);
        auto& cell=graph.at(input.cell);auto& target=graph.at(input.storage);if(cell.type!=input.type || !input.type.is_reference() || target.type!=input.type.pointee() || target.parent!=-1 || input.depth!=input.path.depth() || input.folded!=f.summary_places.contains(input.storage))invalid(input.at);
        bool writable=(input.cell>=0 || cell.mutable_) && input.type.borrow_kind()==Type::Borrow::exclusive;if(input.mutable_access!=writable || target.mutable_!=writable || f.external_paths.at(input.storage).first!=input.parameter)invalid(input.at);
        RegionPath expected{input.parameter,0};
        if(input.cell>=0){if(graph.root(input.cell)!=f.parameters[static_cast<std::size_t>(input.parameter)])invalid(input.at);for(int field:graph.path(input.cell))expected=expected.field(field);}else {if(!f.external_paths.contains(input.cell) || f.external_paths.at(input.cell).first!=input.parameter)invalid(input.at);expected=f.external_paths.at(input.cell);}
        expected=expected.dereference();if(input.path!=expected || (!input.folded && f.external_paths.at(input.storage)!=expected))invalid(input.at);
        if(input.folded && (!f.summary_anchors.contains(input.storage) || f.summary_anchors.at(input.storage)!=input.structural_event))invalid(input.at);
        if(cell.parent!=-1){auto& parent=graph.at(cell.parent);if(input.structural_event!=-1-parent.type.product_id()*128-cell.field)invalid(input.at);}else if(input.cell>=0 && input.structural_event!=-20000-input.parameter)invalid(input.at);
    }
    for(auto& input:f.inputs)if(input.cell<0 && graph.at(input.cell).parent==-1){charge();auto anchor=incoming_anchors.find(input.cell);if(anchor==incoming_anchors.end() || anchor->second!=input.structural_event)invalid(input.at);}
    for(auto& place:f.external_places)if(place.type.is_reference() && !cells.contains(place.id))invalid(place.at);
    for(int parameter:f.parameters){auto& type=graph.at(parameter).type;if(type.is_reference()){if(!cells.contains(parameter))invalid();}else if(type.is_aggregate() && type.contains_references())for(auto& path:type.definition().reference_paths){int cell=parameter;for(int field:path){charge();cell=graph.child(cell,field);}if(!cells.contains(cell))invalid();}}
    std::set<int> seen;std::vector<int> pending;for(auto& input:f.inputs)if(input.cell>=0)pending.push_back(input.storage);
    while(!pending.empty()){int node=pending.back();pending.pop_back();charge();if(!seen.insert(node).second)continue;for(int child:graph.children(node))pending.push_back(child);auto edge=cells.find(node);if(edge!=cells.end())pending.push_back(edge->second->storage);}
    if(seen!=external)invalid();std::size_t roots=0;for(int node:f.summary_places){if(!external.contains(node))invalid();if(graph.at(node).parent==-1){++roots;if(!f.summary_anchors.contains(node))invalid();}}if(roots!=f.summary_anchors.size())invalid();
}
