// Verify non-Copy SSA temporaries separately from lexical source storage.
// Source storage moves are checked by the common definite-initialization pass.
[[noreturn]] void owner_unavailable(const IrFunction& f,const IrOp& use,int block_id) {
    auto declaration=std::find_if(f.places.begin(),f.places.end(),[&](auto& p){return p.id==use.place;});
    Diagnostic diagnostic("AI3102",declaration->parent!=-1 ? "Product field is uninitialized or moved on a reaching path." : declaration->type.is_product() ? "Product value is partially initialized or moved on a reaching path." : "Owned value is uninitialized or moved on a reaching path.",use.at);
    diagnostic.ownerSymbol=use.place;
    if(declaration->type.is_reference())diagnostic.ownershipObligation="reference-value-availability";
    diagnostic.related.push_back({"declaration",declaration->at});
    std::vector<std::vector<int>> predecessors(f.blocks.size());
    for(auto& block:f.blocks)if(block.reachable)for(int next:block.successors)predecessors[static_cast<std::size_t>(next)].push_back(block.id);
    IrPlaceGraph projections(f.places);
    if(declaration->parent!=-1){diagnostic.ownershipObligation="product-field-availability";auto& parent=projections.at(declaration->parent);diagnostic.related.push_back({"field-declaration",parent.type.definition().fields[static_cast<std::size_t>(declaration->field)].at});}
    else if(declaration->type.is_product())diagnostic.ownershipObligation="product-value-availability";
    auto& initial=f.blocks[static_cast<std::size_t>(block_id)];
    auto at=std::find_if(initial.ops.begin(),initial.ops.end(),[&](auto& o){return o.id==use.id;});
    using Needed=std::set<int>;Needed leaves;for(int id:projections.subtree(use.place))if(projections.children(id).empty())leaves.insert(id);
    using Position=std::tuple<int,std::size_t,Needed>;
    std::vector<Position> pending={{block_id,static_cast<std::size_t>(at-initial.ops.begin()),leaves}};
    std::set<Position> visited;std::set<std::size_t> offsets;std::set<int> origins;std::size_t work=0;
    auto charge=[&](std::size_t n=1){if(n>1000000-work)fail("AI3100","Owner diagnostic provenance limit exceeded.",use.at);work+=n;};
    while(!pending.empty()){
        auto position=std::move(pending.back());pending.pop_back();charge(1+std::get<2>(position).size());if(!visited.insert(position).second)continue;
        auto& block=f.blocks[static_cast<std::size_t>(std::get<0>(position))];auto needed=std::move(std::get<2>(position));
        for(std::size_t i=std::get<1>(position);i>0 && !needed.empty();--i){charge();auto& o=block.ops[i-1];if(!projections.contains(o.place))continue;
            if(o.kind!="move" && o.kind!="write" && o.kind!="parameter" && o.kind!="storage-live" && o.kind!="storage-dead")continue;
            auto covered=projections.subtree(o.place);charge(covered.size());bool affected=false;for(int id:covered)affected=needed.erase(id)!=0 || affected;
            if(affected && o.kind=="move"){if(offsets.insert(o.at.offset).second)diagnostic.related.push_back({"possible-move",o.at});origins.insert(block.id);}
        }
        if(!needed.empty())for(int parent:predecessors[static_cast<std::size_t>(block.id)]){charge(1+needed.size());pending.emplace_back(parent,f.blocks[static_cast<std::size_t>(parent)].ops.size(),needed);}
    }
    // Only a nonempty path out of the use block counts as a possible cycle.
    std::vector<int> forward=initial.successors;std::set<int> seen;
    while(!forward.empty()){charge();int id=forward.back();forward.pop_back();if(!seen.insert(id).second)continue;if(origins.contains(id))diagnostic.possibleCycle=true;auto& next=f.blocks[static_cast<std::size_t>(id)].successors;forward.insert(forward.end(),next.begin(),next.end());}
    std::sort(diagnostic.related.begin()+(declaration->parent!=-1 ? 2 : 1),diagnostic.related.end(),[](auto& a,auto& b){return a.location.offset<b.location.offset;});
    throw diagnostic;
}
void verify_owner_values(const IrFunction& f) {
    std::map<int,std::size_t> owners;std::set<int> dropping;
    for(auto& b:f.blocks)for(auto& o:b.ops)if(!o.type.is_copy() && o.kind!="reference-use" && !o.kind.starts_with("reference-follow-")){owners.emplace(o.id,owners.size());if(o.type.needs_drop())dropping.insert(o.id);}
    if(owners.empty())return;
    auto count=f.blocks.size(),words=(owners.size()+63)/64;
    if(count>1000000/words)fail("AI3100","Owned temporary analysis workspace limit exceeded.",{});
    using Bits=std::vector<std::uint64_t>;
    std::vector<Bits> must(count,Bits(words,UINT64_MAX)),may(count,Bits(words,0));
    std::vector<std::vector<int>> predecessors(count);
    for(auto& b:f.blocks)for(int next:b.successors)predecessors[static_cast<std::size_t>(next)].push_back(b.id);
    std::size_t work=0;
    auto charge=[&](std::size_t n){if(n>16000000-work)fail("AI3100","Owned temporary analysis work limit exceeded.",{});work+=n;};
    auto transfer=[&](const IrBlock& b,Bits state,bool validate){
        auto consume=[&](int id,Location at,bool erase){auto it=owners.find(id);if(it==owners.end())return;auto index=it->second;auto bit=std::uint64_t{1}<<(index%64);if(validate && !(state[index/64]&bit))fail("AI3102","Owned temporary is moved or unavailable on a reaching path.",at);if(erase || dropping.contains(id))state[index/64]&=~bit;};
        for(auto& o:b.ops){charge(1);for(std::size_t a=0;a<o.operands.size();++a)consume(o.operands[a],o.at,o.kind=="write" || o.kind=="aggregate-pack" || o.kind=="variant-pack" || o.kind=="aggregate-extract" || o.kind=="drop-value" || o.kind=="end-reference" || (o.kind=="reference-write" && a==1) || (o.kind=="call" && o.function>=0));auto it=owners.find(o.id);if(it!=owners.end()){auto index=it->second;auto bit=std::uint64_t{1}<<(index%64);if(validate && dropping.contains(o.id) && (state[index/64]&bit))fail("AI3100","Owned temporary is overwritten before transfer or drop.",o.at);state[index/64]|=bit;}}
        if(b.end=="return")consume(b.result,b.at,true);return state;
    };
    bool changed=true;while(changed){changed=false;for(auto& b:f.blocks)if(b.reachable){
        Bits before(words,b.id==0 ? 0 : UINT64_MAX),possible(words,0);
        if(b.id)for(int parent:predecessors[static_cast<std::size_t>(b.id)])if(f.blocks[static_cast<std::size_t>(parent)].reachable){charge(words);for(std::size_t i=0;i<words;++i){before[i]&=must[static_cast<std::size_t>(parent)][i];possible[i]|=may[static_cast<std::size_t>(parent)][i];}}
        auto definite=transfer(b,before,false),maybe=transfer(b,possible,false);auto index=static_cast<std::size_t>(b.id);
        if(must[index]!=definite || may[index]!=maybe){must[index]=std::move(definite);may[index]=std::move(maybe);changed=true;}
    }}
    for(auto& b:f.blocks)if(b.reachable){
        Bits before(words,b.id==0 ? 0 : UINT64_MAX);if(b.id)for(int p:predecessors[static_cast<std::size_t>(b.id)])if(f.blocks[static_cast<std::size_t>(p)].reachable)for(std::size_t i=0;i<words;++i)before[i]&=must[static_cast<std::size_t>(p)][i];
        transfer(b,before,true);
        Bits possible(words,0);if(b.id)for(int p:predecessors[static_cast<std::size_t>(b.id)])if(f.blocks[static_cast<std::size_t>(p)].reachable)for(std::size_t i=0;i<words;++i)possible[i]|=may[static_cast<std::size_t>(p)][i];transfer(b,possible,true);
        if(b.end=="return")for(auto& [id,index]:owners){(void)id;if(dropping.contains(id) && ((may[static_cast<std::size_t>(b.id)][index/64]>>(index%64))&1u))fail("AI3100","Owned temporary lacks a normal-exit transfer or drop.",b.at);}
    }
}
