// A projected payload is available only below a verified tag dispatch edge.
// Recompute incoming edges; a forged extra entry cannot bypass the case test.
template<class Dominates,class Charge> void verify_native_variant_places(const IrFunction& f,const std::map<int,const IrOp*>& values,const std::map<int,int>& value_blocks,Dominates& dominates,Charge& charge){
    auto bad=[](Location at={}){fail("AI3100","Native active payload projection invariant failed.",at);};IrPlaceGraph graph(f.places);
    std::vector<std::set<int>> predecessors(f.blocks.size());for(auto& b:f.blocks)for(int next:b.successors)predecessors[static_cast<std::size_t>(next)].insert(b.id);
    std::map<std::pair<int,int>,std::vector<int>> proofs;std::set<int> subjects,propagation_exits;for(auto& m:f.matches)if(m.propagation)for(auto& group:m.groups)for(auto& arm:group.arms)if(arm.source==1)propagation_exits.insert(arm.value_block);
    for(auto& match:f.matches)if(match.subject>=0){
        auto& subject=graph.at(match.subject);if(!subjects.insert(subject.id).second)bad(match.at);auto tag=values.at(match.scrutinee);if(subject.mutable_ || subject.parameter || subject.parent!=-1 || tag->kind!="variant-tag" || tag->place!=subject.id || value_blocks.at(tag->id)!=match.entry)bad(match.at);
        int live=0,write=0,dead=0;for(auto& b:f.blocks)for(auto& o:b.ops)if(o.place==subject.id){charge(1);if(o.kind=="storage-live"){++live;if(b.id!=match.entry)bad(o.at);}else if(o.kind=="write"){++write;if(b.id!=match.entry)bad(o.at);}else if(o.kind=="storage-dead"){++dead;if(b.id!=match.merge && !(b.end=="return" && propagation_exits.contains(b.id)))bad(o.at);}else if(o.kind=="variant-tag"){if(o.id!=tag->id)bad(o.at);}else if(o.kind=="variant-borrow"){if(!subject.type.is_reference())bad(o.at);}else bad(o.at);}if(live!=1 || write!=1 || dead<1)bad(match.at);
        for(std::size_t index=0;index<match.groups.size();++index){auto& group=match.groups[index];if(group.selectors.size()!=1 || group.other)continue;int selected=group.selectors[0];auto& selector=*values.at(selected);std::set<int> incoming;
            for(auto& dispatch:match.dispatch)if(dispatch.group==static_cast<int>(index)){charge(1);if(dispatch.selector!=selected)bad(match.at);auto& condition=*values.at(dispatch.condition);if(condition.kind!="binary:==" || condition.operands!=std::vector<int>{tag->id,selected})bad(match.at);incoming.insert(dispatch.block);}
            if(incoming.empty() || predecessors[static_cast<std::size_t>(group.entry)]!=incoming)bad(match.at);proofs[{subject.id,static_cast<int>(selector.literal)}].push_back(group.entry);
        }
    }
    for(auto& b:f.blocks)if(b.reachable)for(auto& o:b.ops)if(graph.contains(o.place)){
        if(o.kind=="variant-borrow"){auto type=graph.at(o.place).type;if(!type.is_reference() || !type.pointee().is_payload_union())bad(o.at);int c=type.pointee().field_case(static_cast<std::size_t>(o.field));bool proved=false;for(int entry:proofs[{o.place,c}]){charge(1);if(dominates(entry,b.id))proved=true;}if(!proved)bad(o.at);}
        int child=o.place;while(graph.at(child).parent!=-1){charge(1);int parent=graph.at(child).parent;auto type=graph.at(parent).type;if(type.is_payload_union()){int c=type.field_case(static_cast<std::size_t>(graph.at(child).field));bool proved=false;for(int entry:proofs[{parent,c}]){charge(1);if(dominates(entry,b.id))proved=true;}if(!proved)bad(o.at);}child=parent;}
    }
}
