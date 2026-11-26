#include "native_payload_presence.inc"
// Storage/loan proof with symbolic input regions and caller-instantiated effects.
IrRegionSummary analyze_loans(IrFunction& f,const std::map<int,const IrFunction*>& signatures,bool checking,std::size_t& module_work) {
    using Set=std::set<int>;using State=std::map<int,Set>;
    f.loans.clear();f.reference_components.clear();bool references=false;std::map<int,const IrPlace*> places;std::map<int,const IrOp*> ops;
    for(auto& p:f.places){places.emplace(p.id,&p);references|=p.type.contains_references();}
    for(auto& b:f.blocks)for(auto& o:b.ops){ops.emplace(o.id,&o);references|=o.type.contains_references();}

    std::size_t work=0,facts=0;
    auto charge=[&](std::size_t n=1){if(n>16000000-work)fail("AI3100","Native loan analysis work limit exceeded.",{});work+=n;if(n>64000000-module_work)fail("AI3100","Native function region analysis work limit exceeded.",{});module_work+=n;};
    auto presence=native_payload_presence(f,signatures,charge);
    if(!references){IrRegionSummary summary;summary.possible_components=presence.returned;summary.possible_tags=presence.returned_tags;return summary;}
    auto join=[&](Set& into,const Set& from){bool changed=false;for(int id:from){charge();if(into.insert(id).second){if(++facts>1000000)fail("AI3100","Native loan analysis fact limit exceeded.",{});changed=true;}}return changed;};
    auto copy=[&](const Set& from){Set result;join(result,from);return result;};
    auto merge=[&](State& into,const State& from){for(auto& [place,loans]:from)join(into[place],loans);};
    std::vector<IrPlace> all_places=f.places;all_places.insert(all_places.end(),f.external_places.begin(),f.external_places.end());State initial;const auto& external_paths=f.external_paths;
    IrPlaceGraph projections(all_places);places.clear();for(auto& p:all_places)places.emplace(p.id,&p);
    auto overlaps=[&](const Set& targets,int root){for(int target:targets){charge(1+projections.depth(target)+projections.depth(root));if(projections.overlaps(target,root))return true;}return false;};
    std::map<int,const IrInputRegion*> input_cells;for(auto& input:f.inputs)if(!input_cells.emplace(input.cell,&input).second)fail("AI3100","Duplicate native input reference cell.",input.at);
    auto observe=[&](int cell,IrInputEvent event){
        auto found=input_cells.find(cell);if(found==input_cells.end())return;auto& input=*found->second;charge(1+input.path.selectors.size());
        auto [entry,created]=f.input_demands.try_emplace(input.path);auto& demand=entry->second;
        auto fact=[&](){if(++f.input_facts>1000000 || f.input_demands.size()>65536)fail("AI3100","Native input demand graph fact limit exceeded.",input.at);};if(created)fact();
        if(demand.observations.insert(event).second){fact();f.input_observations_changed=true;}
        if(!input.folded || f.summary_places.contains(cell))return;IrInputEvents history;if(cell<0)history=f.external_history.at(cell);charge(history.size());if(history.contains(event))return;
        bool changed=!demand.expanded;demand.expanded=true;history.insert(event);for(auto item:history)if(demand.history.insert(item).second){fact();changed=true;}f.input_rebuild=f.input_rebuild || changed;
    };
    auto initial_origin=[&](const IrFunction& target,const RegionPath& destination){
        auto cell=std::find_if(target.external_places.begin(),target.external_places.end(),[&](auto& place){charge();return target.external_paths.at(place.id)==destination;});if(cell==target.external_places.end() || !cell->type.is_reference())fail("AI3100","Invalid reference effect graph cell.",{});
        auto edge=std::find_if(target.inputs.begin(),target.inputs.end(),[&](auto& input){charge();return input.cell==cell->id;});if(edge==target.inputs.end())fail("AI3100","Missing reference effect input edge.",{});return target.external_paths.at(edge->storage);
    };
    std::map<int,std::size_t> indices;std::map<int,std::vector<int>> creations;
    for(auto& input:f.inputs){charge();IrLoan l{};l.id=input.loan_id;l.type=input.type;l.input_parameter=input.parameter;l.input_depth=input.depth;l.at=input.at;l.exclusive=input.mutable_access;l.roots={input.storage};indices.emplace(l.id,f.loans.size());f.loans.push_back(l);initial[input.cell]={l.id};}
    for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind.starts_with("borrow-") || o.kind.starts_with("reborrow-") || o.kind=="reference-field" || o.kind=="variant-borrow" || (o.kind=="call" && o.type.is_reference())){
        IrLoan loan{};loan.type=o.type;loan.access_only=o.kind=="reference-field";loan.creation=o.id;loan.id=o.id;loan.place=o.place;loan.at=o.at;loan.exclusive=o.type.borrow_kind()==Type::Borrow::exclusive;
        if(o.place>=0 && o.kind!="variant-borrow")loan.roots.insert(o.place);indices.emplace(o.id,f.loans.size());creations[o.id].push_back(o.id);f.loans.push_back(std::move(loan));
    }
    std::map<std::pair<int,RegionPath>,int> effect_ids;std::map<std::pair<int,ComponentPath>,int> result_component_ids;int next_effect=400000;
    auto new_call_loan=[&](const IrOp& call,Type type,ComponentPath component){
        charge(1+component.size());if(next_effect>=1400000)fail("AI3100","Native function effect/component fact limit exceeded.",call.at);IrLoan l{};l.id=next_effect++;l.creation=call.id;l.type=type;l.exclusive=type.borrow_kind()==Type::Borrow::exclusive;l.at=call.at;l.result_component=std::move(component);indices.emplace(l.id,f.loans.size());creations[call.id].push_back(l.id);f.loans.push_back(std::move(l));return f.loans.back().id;
    };
    for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind=="call" && o.function>=0){
        auto target=signatures.at(o.function);charge(target->external_places.size());IrPlaceGraph target_graph(target->external_places);
        for(auto& [destination,values]:target->summary.effects){charge(values.size()+1);if(values==RegionSet{initial_origin(*target,destination)})continue;auto region=std::find_if(target->external_places.begin(),target->external_places.end(),[&](auto& place){charge();return target->external_paths.at(place.id)==destination;});if(region==target->external_places.end() || !region->type.is_reference())fail("AI3100","Invalid reference effect region.",o.at);bool conditional=false;for(int parent:target_graph.parents(region->id)){charge();if(target_graph.at(parent).type.is_payload_union())conditional=true;}effect_ids.emplace(std::pair{o.id,destination},values.empty() && conditional ? -1 : new_call_loan(o,region->type,{}));}
        for(auto& [component,values]:target->summary.returned_components){if(!target->summary.possible_components.contains(component) || !presence.values[o.id].contains(component))continue;charge(1+values.size()+component.size());auto type=target->type;for(int field:component)type=type.field_type(static_cast<std::size_t>(field));if(!type.is_reference())fail("AI3100","Invalid product result reference component.",o.at);result_component_ids.emplace(std::pair{o.id,component},new_call_loan(o,type,component));}
    }
    auto absent_input=[&](const IrInputRegion& input,const IrOp& call){ComponentPath path;for(int step:input.path.selectors){if(step<0)break;path.push_back(step);}if(input.parameter<0 || static_cast<std::size_t>(input.parameter)>=call.operands.size())fail("AI3100","Invalid payload input parameter.",call.at);return !presence.values[call.operands[static_cast<std::size_t>(input.parameter)]].contains(path);};
    auto loan=[&](int id)->IrLoan&{auto it=indices.find(id);if(it==indices.end())fail("AI3100","Unproved native reference origin.",{});return f.loans[it->second];};
    State origins;std::map<int,ReferenceComponents> components;std::vector<State> outputs(f.blocks.size());std::vector<std::vector<int>> predecessors(f.blocks.size());
    for(auto& b:f.blocks)if(b.reachable && presence.feasible.contains(b.id))for(int n:b.successors)predecessors[static_cast<std::size_t>(n)].push_back(b.id);
    auto roots=[&](const Set& ids){Set result;for(int id:ids){charge();join(result,loan(id).roots);}return result;};
    struct Snapshot{Set parents,targets;};
    auto instantiate=[&](const IrFunction& target,const State& state,const IrOp& call){
        std::map<int,Snapshot> nodes;IrPlaceGraph local(target.places);
        auto add=[&](int node,const Set& parents,const Set& targets){auto& value=nodes[node];bool changed=join(value.parents,parents);return join(value.targets,targets) || changed;};
        for(auto& input:target.inputs)if(input.cell>=0){
            charge();if(input.parameter<0 || static_cast<std::size_t>(input.parameter)>=call.operands.size())fail("AI3100","Invalid input graph parameter.",call.at);int argument=call.operands[static_cast<std::size_t>(input.parameter)];auto path=local.path(input.cell);Set pointer=path.empty() ? copy(origins[argument]) : copy(components[argument][path]);add(input.storage,pointer,roots(pointer));
        }
        bool changed=true;while(changed){changed=false;
            for(auto& place:target.external_places)if(place.parent!=-1){auto parent=nodes.find(place.parent);if(parent==nodes.end())continue;Set projected;for(int root:parent->second.targets){charge();projected.insert(projections.child(root,place.field));}changed=add(place.id,parent->second.parents,projected) || changed;}
            for(auto& input:target.inputs)if(input.cell<0){
                auto cell=nodes.find(input.cell);if(cell==nodes.end())continue;Set pointer;auto demand=target.input_demands.find(input.path);IrInputEvents events;
                if(demand!=target.input_demands.end())for(auto event:demand->second.observations){charge();events.insert({event.first,f.id*400000+call.id});}
                if(events.empty())events.insert({input.structural_event,f.id*400000+call.id});
                for(int actual:cell->second.targets){charge();for(auto event:events)observe(actual,event);auto value=state.find(actual);if(value!=state.end())join(pointer,value->second);}
                changed=add(input.storage,pointer,roots(pointer)) || changed;
            }
        }
        std::map<RegionPath,Snapshot> result;for(auto& place:target.external_places){charge();result.emplace(target.external_paths.at(place.id),std::move(nodes[place.id]));}return result;
    };
    auto component_place=[&](int root,const ComponentPath& path){for(int field:path){charge();root=projections.child(root,field);}return root;};
    auto transfer=[&](State& state,const IrOp& o,bool inference)->bool {
        charge();bool changed=false;Set value;
        auto load=[&](const Set& targets,const Type& type){
            for(int target:targets){charge();if(type.is_reference()){observe(target,{f.id*400000+o.id,-1});join(value,state[target]);}else if(inference && type.is_aggregate() && type.contains_references())for(auto& path:type.definition().reference_paths){charge(1+path.size());auto cell=component_place(target,path);observe(cell,{f.id*400000+o.id,-1});changed|=join(components[o.id][path],state[cell]);}}
        };
        auto store=[&](int target,const Type& type,int source,bool strong){
            strong=strong && !f.summary_places.contains(target);if(type.is_reference()){if(strong)state[target]=copy(origins[source]);else join(state[target],origins[source]);}
            else if(type.is_aggregate() && type.contains_references())for(auto& path:type.definition().reference_paths){charge(1+path.size());auto child=component_place(target,path);if(strong)state[child]=copy(components[source][path]);else join(state[child],components[source][path]);}
        };
        if(o.kind=="call" && o.function>=0){
            auto target=signatures.at(o.function);auto snapshot=instantiate(*target,state,o);
            if(o.type.is_reference()){value.insert(o.id);if(inference){auto& result=loan(o.id);for(auto path:target->summary.returned){changed|=join(result.parents,snapshot.at(path).parents);changed|=join(result.roots,snapshot.at(path).targets);}}}
            for(auto& [component,paths]:target->summary.returned_components){if(!target->summary.possible_components.contains(component) || !presence.values[o.id].contains(component))continue;auto id=result_component_ids.at({o.id,component});if(inference){auto& output=loan(id);for(auto& path:paths){changed|=join(output.parents,snapshot.at(path).parents);changed|=join(output.roots,snapshot.at(path).targets);}changed|=join(components[o.id][component],Set{id});}}
            std::map<int,Set> writes;std::map<int,bool> strong;
            for(auto& [destination,paths]:target->summary.effects){auto id=effect_ids.find({o.id,destination});if(id==effect_ids.end())continue;Set source,source_roots;for(auto path:paths){join(source,snapshot.at(path).parents);join(source_roots,snapshot.at(path).targets);}if(id->second>=0 && inference){auto& output=loan(id->second);changed|=join(output.parents,source);changed|=join(output.roots,source_roots);}auto destinations=copy(snapshot.at(destination).targets);for(int root:destinations){auto& write=writes[root];if(id->second>=0)write.insert(id->second);if(!strong.contains(root))strong[root]=destinations.size()==1 && !f.summary_places.contains(root) && !target->summary_places.contains(target->external_ids.at(destination));else strong[root]=false;}}
            for(auto& [root,values]:writes){if(strong[root])state[root]=copy(values);else join(state[root],values);}
        }else if(o.kind=="storage-live" || o.kind=="storage-dead"){auto nested=projections.subtree(o.place);charge(nested.size());for(int child:nested)state.erase(child);}
        else if(o.kind=="copy-read" || o.kind=="move" || o.kind=="reference-use"){
            load(Set{o.place},o.type);if(o.kind=="move"){auto nested=projections.subtree(o.place);charge(nested.size());for(int child:nested)state.erase(child);}
        }else if(o.kind=="variant-borrow"){
            value.insert(o.id);if(inference){auto& result=loan(o.id);changed|=join(result.parents,state[o.place]);for(int parent:roots(state[o.place])){charge();changed|=join(result.roots,Set{projections.child(parent,o.field)});}}
        }else if(o.kind=="borrow-shared" || o.kind=="borrow-exclusive")value.insert(o.id);
        else if(o.kind.starts_with("reborrow-")){
            value.insert(o.id);if(inference){auto& result=loan(o.id);changed|=join(result.parents,origins[o.operands[0]]);auto target=roots(origins[o.operands[0]]);changed|=join(result.roots,target);}
        }else if(o.kind=="reference-field"){
            value.insert(o.id);if(inference){auto& result=loan(o.id);changed|=join(result.parents,origins[o.operands[0]]);for(int parent:roots(origins[o.operands[0]])){charge();changed|=join(result.roots,Set{projections.child(parent,o.field)});}}
        }else if(o.kind.starts_with("reference-follow-") || (o.kind=="reference-read" && o.type.contains_references())){
            load(roots(origins[o.operands[0]]),o.type);
        }else if(o.kind=="write"){
            store(o.place,places.at(o.place)->type,o.operands[0],true);
        }else if(o.kind=="reference-write"){
            auto target=roots(origins[o.operands[0]]);
            for(int id:target)store(id,places.at(id)->type,o.operands[1],target.size()==1);
        }
        if((o.kind=="aggregate-pack" || o.kind=="variant-pack") && inference){
            for(std::size_t i=0;i<o.operands.size();++i){int field=o.kind=="variant-pack" ? o.type.definition().cases[static_cast<std::size_t>(o.field)].fields[i] : static_cast<int>(i);if(field<0)continue;auto type=o.type.field_type(static_cast<std::size_t>(field));ComponentPath prefix={field};if(type.is_reference())changed|=join(components[o.id][prefix],origins[o.operands[i]]);else if(type.is_aggregate() && type.contains_references())for(auto& [path,ids]:components[o.operands[i]]){auto nested=prefix;nested.insert(nested.end(),path.begin(),path.end());charge(1+nested.size());changed|=join(components[o.id][nested],ids);}}
        }
        if(o.kind=="aggregate-extract"){
            ComponentPath prefix={o.field};if(o.type.is_reference())value=copy(components[o.operands[0]][prefix]);else if(inference && o.type.is_aggregate() && o.type.contains_references())for(auto& [path,ids]:components[o.operands[0]])if(!path.empty() && path.front()==o.field){ComponentPath nested(path.begin()+1,path.end());charge(1+nested.size());changed|=join(components[o.id][nested],ids);}
        }
        if(inference && o.type.is_reference())changed|=join(origins[o.id],value);
        return changed;
    };
    bool changed=true;
    while(changed){changed=false;for(auto& b:f.blocks)if(b.reachable && presence.feasible.contains(b.id)){
        State state;if(b.id==0)merge(state,initial);for(int parent:predecessors[static_cast<std::size_t>(b.id)])if(presence.edges.contains({parent,b.id}))merge(state,outputs[static_cast<std::size_t>(parent)]);
        for(auto& o:b.ops)changed|=transfer(state,o,true);
        for(auto it=state.begin();it!=state.end();)if(it->second.empty())it=state.erase(it);else ++it;
        if(state!=outputs[static_cast<std::size_t>(b.id)]){outputs[static_cast<std::size_t>(b.id)]=std::move(state);changed=true;}
    }}
    IrRegionSummary summary;summary.possible_components=presence.returned;summary.possible_tags=presence.returned_tags;
    auto escaped=[&](const Set& values,Location at,const char* obligation){RegionSet result;for(int root:roots(values)){auto path=external_paths.find(root);if(path!=external_paths.end())result.insert(path->second);else if(checking){if(root< -1)fail("AI3100","Product field return/store requires a verified input field-region relationship.",at);Diagnostic d("AI3117","Reference escapes storage owned by the current function.",at);d.ownershipObligation=obligation;d.ownerSymbol=root;d.related.push_back({"storage-declaration",places.at(root)->at});for(int id:values)if(loan(id).roots.contains(root)){d.related.push_back({"loan-origin",loan(id).at});break;}throw d;}}return result;};
    for(auto& b:f.blocks)if(b.reachable && presence.feasible.contains(b.id) && b.end=="return"){
        if(f.type.is_reference()){if(checking && origins[b.result].empty())fail("AI3117","Reference return has no proven input lifetime.",b.at);auto returned=escaped(origins[b.result],b.at,"reference-return-region");summary.returned.insert(returned.begin(),returned.end());}
        if(f.type.is_aggregate() && f.type.contains_references())for(auto& component:f.type.definition().reference_paths){charge(1+component.size());auto& component_origins=components[b.result][component];if(checking && presence.values[b.result].contains(component) && component_origins.empty())fail("AI3117","Product result component has no proven input lifetime.",b.at);auto returned=escaped(component_origins,b.at,"product-return-region");auto& output=summary.returned_components[component];output.insert(returned.begin(),returned.end());}
        for(auto& place:f.external_places)if((charge(),place.type.is_reference())){auto it=outputs[static_cast<std::size_t>(b.id)].find(place.id);Set empty;auto values=escaped(it==outputs[static_cast<std::size_t>(b.id)].end() ? empty : it->second,b.at,"reference-store-region");auto& output=summary.effects[external_paths.at(place.id)];output.insert(values.begin(),values.end());}
    }
    if(f.unwind){
        auto record_unwind=[&](const State& state,Location at){
            for(auto& place:f.external_places)if((charge(),place.type.is_reference())){
                auto value=state.find(place.id);Set empty;auto values=escaped(value==state.end() ? empty : value->second,at,"reference-unwind-region");auto& output=summary.unwind_effects[external_paths.at(place.id)];output.insert(values.begin(),values.end());
            }
        };
        // A checked entry fault precedes body effects. Normal effects remain
        // separate so normal callers do not inherit pre-entry aliases.
        record_unwind(initial,f.declaration);
        for(auto& b:f.blocks)if(b.reachable && presence.feasible.contains(b.id)){
            State state;if(b.id==0)merge(state,initial);for(int parent:predecessors[static_cast<std::size_t>(b.id)])if(presence.edges.contains({parent,b.id}))merge(state,outputs[static_cast<std::size_t>(parent)]);
            for(auto& o:b.ops){
                if(native_fault_site(o)){
                    State fault_state=state;
                    if(o.kind=="call" && o.function>=0){
                        auto& target=*signatures.at(o.function);auto snapshot=instantiate(target,state,o);std::map<int,Set> writes;std::map<int,bool> strong;
                        for(auto& [destination,paths]:target.summary.unwind_effects){
                            Set source;for(auto& path:paths)join(source,snapshot.at(path).parents);auto destinations=copy(snapshot.at(destination).targets);
                            for(int root:destinations){join(writes[root],source);if(!strong.contains(root))strong[root]=destinations.size()==1 && !f.summary_places.contains(root) && !target.summary_places.contains(target.external_ids.at(destination));else strong[root]=false;}
                        }
                        for(auto& [root,values]:writes){if(strong[root])fault_state[root]=copy(values);else join(fault_state[root],values);}
                    }
                    record_unwind(fault_state,o.at);
                }
                transfer(state,o,false);
            }
        }
    }
    if(!checking)return summary;
    // Recompute guard regions from verified match edges. A guard may change
    // unrelated state, but cannot mutate the candidate or its reference targets.
    std::map<int,Set> guard_subjects;
    for(auto& match:f.matches)if(match.subject>=0 && places.at(match.subject)->type.is_reference())for(auto& group:match.groups)for(auto& arm:group.arms)if(arm.guard>=0){
        Set seen;std::vector<int> pending={arm.guard_entry};while(!pending.empty()){int block=pending.back();pending.pop_back();charge();if(!seen.insert(block).second)continue;guard_subjects[block].insert(match.subject);if(block!=arm.guard_end)for(int next:f.blocks[static_cast<std::size_t>(block)].successors)pending.push_back(next);}
    }
    auto guard_roots=[&](int block,const State& state){
        Set result,seen;std::vector<int> pending;for(int subject:guard_subjects[block]){auto it=state.find(subject);if(it!=state.end())pending.insert(pending.end(),it->second.begin(),it->second.end());}
        while(!pending.empty()){int id=pending.back();pending.pop_back();charge();if(!seen.insert(id).second)continue;
            for(int root:loan(id).roots){charge();result.insert(root);auto retain=[&](int cell){auto it=state.find(cell);if(it!=state.end())pending.insert(pending.end(),it->second.begin(),it->second.end());};auto type=places.at(root)->type;if(type.is_reference())retain(root);else if(type.is_aggregate() && type.contains_references())for(auto& path:type.definition().reference_paths){charge(1+path.size());retain(component_place(root,path));}}
        }return result;
    };
    // Source-place and SSA-reference liveness select the actual last-use region.
    std::vector<Set> live_in(f.blocks.size()),live_out(f.blocks.size());
    auto reference_value=[&](int id){auto it=ops.find(id);return it!=ops.end() && it->second->type.contains_references();};
    changed=true;while(changed){changed=false;for(auto it=f.blocks.rbegin();it!=f.blocks.rend();++it)if(it->reachable && presence.feasible.contains(it->id)){
        auto& b=*it;Set after;for(int n:b.successors)if(presence.edges.contains({b.id,n}))join(after,live_in[static_cast<std::size_t>(n)]);auto before=copy(after);
        if(b.result>=0 && reference_value(b.result))before.insert(b.result);
        for(auto o=b.ops.rbegin();o!=b.ops.rend();++o){charge();before.erase(o->id);for(int arg:o->operands)if(reference_value(arg))before.insert(arg);}
        auto i=static_cast<std::size_t>(b.id);if(before!=live_in[i] || after!=live_out[i]){live_in[i]=std::move(before);live_out[i]=std::move(after);changed=true;}
    }}
    auto active=[&](const State& state,const Set& slots,const Set& values){
        Set result;for(auto& place:f.external_places)if((charge(),place.type.is_reference())){auto it=state.find(place.id);if(it!=state.end())join(result,it->second);}for(int id:slots){auto it=state.find(id);if(it!=state.end())join(result,it->second);}for(int id:values){join(result,origins[id]);for(auto& [path,ids]:components[id]){charge(1+path.size());join(result,ids);}}
        std::vector<int> pending(result.begin(),result.end());
        for(std::size_t i=0;i<pending.size();++i){
            auto retain=[&](int id){charge();if(result.insert(id).second){if(++facts>1000000)fail("AI3100","Native loan analysis fact limit exceeded.",{});pending.push_back(id);}};
            auto& current=loan(pending[i]);if(current.access_only)for(int parent:current.parents)retain(parent);
            for(int root:current.roots){charge();if(places.at(root)->type.is_aggregate() && places.at(root)->type.contains_references()){for(auto& path:places.at(root)->type.definition().reference_paths){charge(1+path.size());auto it=state.find(component_place(root,path));if(it!=state.end())for(int id:it->second)retain(id);}}else{auto it=state.find(root);if(it!=state.end())for(int id:it->second)retain(id);}}
        }
        return result;
    };
    // Only definite ancestry grants access. Cyclic abstract backedge ancestry
    // grants no extra permission; direct reference origins retain their identity.
    auto authorized=[&](const Set& ids,int target=-1){
        Set result=copy(ids);
        for(int start:ids){if(target!=-1 && !overlaps(loan(start).roots,target))continue;Set ancestors;std::vector<std::pair<int,Set>> stack={{start,{start}}};bool cycle=false;bool first=true;
            while(!stack.empty()){
                auto [id,path]=std::move(stack.back());stack.pop_back();charge();Set parents;for(int parent:loan(id).parents){charge();if(target==-1 || overlaps(loan(parent).roots,target))parents.insert(parent);}
                if(parents.empty()){if(first){ancestors=path;first=false;}else {charge(ancestors.size()+path.size());Set common;std::set_intersection(ancestors.begin(),ancestors.end(),path.begin(),path.end(),std::inserter(common,common.end()));ancestors=std::move(common);}continue;}
                for(int parent:parents){charge();if(path.contains(parent)){cycle=true;continue;}auto next=copy(path);next.insert(parent);stack.emplace_back(parent,std::move(next));}
            }
            if(!cycle)join(result,ancestors);
        }return result;
    };
    // Distinct exclusive input edges denote disjoint incoming capabilities.
    // A cyclic tail node is a set of physical objects, not proof that these
    // incoming owners alias. A joined/cyclic cursor can have different input
    // owners on different paths. Derived user loans never receive this exemption.
    auto separated_inputs=[&](const Set& permission,int other,int root){
        if(!f.summary_places.contains(root) || loan(other).input_parameter<0 || !loan(other).exclusive)return false;
        Set seen;std::vector<int> pending(permission.begin(),permission.end());
        while(!pending.empty()){int id=pending.back();pending.pop_back();charge();if(!seen.insert(id).second)continue;auto& origin=loan(id);if(origin.input_parameter>=0 && origin.exclusive && overlaps(origin.roots,root))return true;for(int parent:origin.parents)pending.push_back(parent);}return false;
    };
    auto conflict=[&](const IrOp& o,int root,int other,bool ending){
        Diagnostic d(ending ? "AI3116" : "AI3114",ending ? "Borrowed storage ends before its last reference use." : "Access conflicts with a live shared or exclusive loan.",o.at);
        d.ownershipObligation=ending ? "reference-storage-lifetime" : "reference-access-conflict";d.ownerSymbol=root;
        d.related.push_back({"storage-declaration",places.at(root)->at});d.related.push_back({"loan-origin",loan(other).at});throw d;
    };
    for(auto& b:f.blocks)if(b.reachable && presence.feasible.contains(b.id)){
        std::vector<Set> value_before(b.ops.size()),place_before(b.ops.size());auto terminal=copy(live_out[static_cast<std::size_t>(b.id)]);if(b.result>=0 && reference_value(b.result))terminal.insert(b.result);auto values=copy(terminal),slots=copy(b.live_out);
        for(std::size_t i=b.ops.size();i>0;--i){auto& o=b.ops[i-1];values.erase(o.id);for(int arg:o.operands)if(reference_value(arg))values.insert(arg);
            if(o.place>=0){bool read=o.kind=="variant-borrow" || o.kind=="variant-tag" || o.kind=="copy-read" || o.kind=="move" || o.kind=="reference-use" || o.kind=="borrow-shared" || o.kind=="borrow-exclusive";auto nested=projections.subtree(o.place);charge(nested.size());for(int child:nested){if(read)slots.insert(child);else slots.erase(child);}}
            value_before[i-1]=copy(values);place_before[i-1]=copy(slots);
        }
        State state;if(b.id==0)merge(state,initial);for(int parent:predecessors[static_cast<std::size_t>(b.id)])if(presence.edges.contains({parent,b.id}))merge(state,outputs[static_cast<std::size_t>(parent)]);
        for(std::size_t i=0;i<b.ops.size();++i){auto& o=b.ops[i];auto before=active(state,place_before[i],value_before[i]);
            Set targets,permission;bool access=false,write=false,ending=false;
            auto guarded=guard_roots(b.id,state);auto reject_guard_write=[&](const Set& targets){for(int root:targets)if(overlaps(guarded,root))fail("AI3100","Native guard mutates or exclusively accesses a candidate payload before selection.",o.at);};
            if(o.kind=="variant-borrow" || (o.kind=="variant-tag" && places.at(o.place)->type.is_reference())){
                permission=copy(state[o.place]);if(permission.empty())fail("AI3100","Borrowed match subject has no proven storage origin.",o.at);targets=roots(permission);if(o.kind=="variant-borrow"){Set fields;for(int root:targets){charge();fields.insert(projections.child(root,o.field));}targets=std::move(fields);}access=true;write=o.kind=="variant-borrow" && o.type.borrow_kind()==Type::Borrow::exclusive;
            }else if(o.kind=="copy-read" || o.kind=="move" || o.kind=="write" || o.kind=="storage-dead" || o.kind=="reference-use" || o.kind.starts_with("borrow-")){
                targets.insert(o.place);access=true;write=o.kind=="move" || o.kind=="write" || o.kind=="storage-dead" || o.kind=="borrow-exclusive";ending=o.kind=="storage-dead";
            }else if(o.kind=="reference-read" || o.kind=="reference-write" || o.kind.starts_with("reference-follow-") || o.kind.starts_with("reborrow-") || o.kind=="reference-field" || (o.kind=="call" && o.function<=-6 && o.function>=-8)){
                if(origins[o.operands[0]].empty())fail("AI3100","Native reference has no proven storage origin.",o.at);
                targets=roots(origins[o.operands[0]]);permission=copy(origins[o.operands[0]]);access=o.kind!="reference-field";write=o.kind=="reference-write" || o.kind=="reference-follow-exclusive" || o.kind=="reborrow-exclusive" || (o.kind=="call" && o.function==-8);
            }
            if(o.kind=="call" && o.function>=0){auto target=signatures.at(o.function);auto snapshot=instantiate(*target,state,o);for(auto& input:target->inputs){if(absent_input(input,o))continue;auto pointer=snapshot.at(target->external_paths.at(input.storage));if(pointer.parents.empty()){bool inactive=input.cell<0;Set cells;if(inactive)cells=snapshot.at(target->external_paths.at(input.cell)).targets;if(cells.empty())inactive=false;for(int cell:cells){charge();bool conditional=false;if(cell!=-1 && projections.contains(cell)){for(int parent:projections.parents(cell)){charge();if(projections.at(parent).type.is_payload_union())conditional=true;}}if(!conditional || presence.possible_cells[o.id].contains(cell))inactive=false;}if(inactive)continue;fail("AI3100","Call reference lacks a proven input origin.",o.at);}for(int root:pointer.targets){auto auth=authorized(pointer.parents,root);for(int other:before){charge();if(!loan(other).access_only && !auth.contains(other) && !separated_inputs(auth,other,root) && overlaps(loan(other).roots,root) && (input.mutable_access || loan(other).exclusive))conflict(o,root,other,false);}}if(input.mutable_access)reject_guard_write(pointer.targets);}}
            if(access)for(int root:targets){auto auth=authorized(permission,root);for(int other:before){charge();if((ending || !loan(other).access_only) && !auth.contains(other) && !separated_inputs(auth,other,root) && overlaps(loan(other).roots,root) && (write || loan(other).exclusive))conflict(o,root,other,ending);}}
            if(access && write && o.kind!="variant-borrow")reject_guard_write(targets);
            for(int id:before){auto& proof=loan(id);join(proof.region,Set{o.id});}
            for(int id:creations[o.id]){auto& created=loan(id);if(created.roots.empty())fail("AI3100","Native loan has no proven storage target.",o.at);for(int root:created.roots)if((charge(1+created.type.reference_depth()),places.at(root)->type!=created.type.pointee()))fail("AI3100","Native loan target type differs from its reference.",o.at);created.region.insert(o.id);}
            transfer(state,o,false);
            const auto& after_slots=i+1<b.ops.size() ? place_before[i+1] : b.live_out;const auto& after_values=i+1<b.ops.size() ? value_before[i+1] : terminal;auto after=active(state,after_slots,after_values);
            for(int id:creations[o.id])before.insert(id);for(int id:before)if(!after.contains(id))loan(id).ends_after.insert(o.id);
        }
        if(b.end=="return" && f.type.contains_references()){
            IrOp boundary{};boundary.at=b.at;auto before=active(state,b.live_out,terminal);
            std::vector<std::tuple<Set,Type,bool,Set>> pending_returns;
            auto check_return=[&](Set pointer,Type type){pending_returns.emplace_back(std::move(pointer),type,true,Set{});};
            if(f.type.is_reference())check_return(copy(origins[b.result]),f.type);
            else if(f.type.is_aggregate())for(auto& component:f.type.definition().reference_paths){if(!presence.values[b.result].contains(component))continue;auto type=f.type;for(int field:component){charge();type=type.field_type(static_cast<std::size_t>(field));}check_return(copy(components[b.result][component]),type);}
            std::set<std::tuple<Set,bool,Set>> checked_returns;
            while(!pending_returns.empty()){
                auto [pointer,type,mutation,exported]=std::move(pending_returns.back());pending_returns.pop_back();
                while(type.is_reference()){
                    charge(1+pointer.size()+exported.size());mutation=mutation && type.borrow_kind()==Type::Borrow::exclusive;
                    if(pointer.empty())fail("AI3100","Returned product reference lacks a proven component origin.",b.at);
                    if(!checked_returns.emplace(pointer,mutation,exported).second)break;
                    auto targets=roots(pointer);
                    for(int root:targets){auto auth=authorized(pointer,root);for(int other:before){charge();if(!loan(other).access_only && !auth.contains(other) && !exported.contains(other) && !separated_inputs(auth,other,root) && overlaps(loan(other).roots,root) && (mutation || loan(other).exclusive))conflict(boundary,root,other,false);}}
                    auto referent=type.pointee();
                    if(referent.is_aggregate() && referent.contains_references())for(auto& component:referent.definition().reference_paths){
                        charge(1+component.size());Type child=referent;for(int field:component)child=child.field_type(static_cast<std::size_t>(field));Set nested;
                        for(int root:targets){auto it=state.find(component_place(root,component));if(it!=state.end())join(nested,it->second);}
                        if(pending_returns.size()>=65536)fail("AI3100","Native returned component graph limit exceeded.",b.at);auto context=copy(exported);join(context,pointer);pending_returns.emplace_back(std::move(nested),child,mutation,std::move(context));
                    }
                    Set next;for(int root:targets){auto it=state.find(root);if(it!=state.end())join(next,it->second);}pointer=std::move(next);type=std::move(referent);
                }
            }
        }
    }
    f.reference_components=std::move(components);
    return summary;
}
