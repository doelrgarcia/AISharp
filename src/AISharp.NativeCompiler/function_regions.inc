#include "native_input_graph.inc"
// Recompute module region contracts from verified signatures and bodies.
// Start at all type-compatible input origins and narrow to a bounded fixed point.
void verify_function_regions(std::vector<IrFunction>& functions) {
    std::map<int,const IrFunction*> signatures;
    std::size_t work=0,facts=0;
    auto charge=[&](std::size_t n=1){if(n>64000000-work)fail("AI3100","Native function region analysis work limit exceeded.",{});work+=n;};
    auto insert=[&](RegionSet& set,RegionPath path){charge(1+path.selectors.size());if(set.insert(path).second && (facts+=1+path.selectors.size())>1000000)fail("AI3100","Native function region summary fact limit exceeded.",{});};
    for(auto& f:functions){signatures.emplace(f.id,&f);f.input_demands.clear();f.input_facts=0;}
    for(;;){
    for(auto& f:functions){
        f.summary={};f.summary.possible_tags=native_variant_tags(f.type,charge);f.loans.clear();build_native_input_graph(f,charge);verify_native_input_graph(f);
        if(f.type.is_reference())f.summary.possible_components.insert(ComponentPath{});else if(f.type.is_aggregate())f.summary.possible_components.insert(f.type.definition().reference_paths.begin(),f.type.definition().reference_paths.end());
        std::map<RegionPath,Type> available;for(auto& p:f.external_places)available.emplace(f.external_paths.at(p.id),p.type);
        for(auto& [path,type]:available){charge(1+type.reference_depth()+f.type.reference_depth());if(f.type.is_reference() && type==f.type.pointee())insert(f.summary.returned,path);}
        if(f.type.is_aggregate() && f.type.contains_references())for(auto& component:f.type.definition().reference_paths){auto type=f.type;for(int field:component){charge();type=type.field_type(static_cast<std::size_t>(field));}auto& origins=f.summary.returned_components[component];for(auto& [path,referent]:available){charge(1+path.selectors.size()+type.reference_depth());if(type.pointee()==referent)insert(origins,path);}}
        for(auto& place:f.external_places){
            if(!place.type.is_reference())continue;
            auto path=f.external_paths.at(place.id);auto& effect=f.summary.effects[path];auto edge=std::find_if(f.inputs.begin(),f.inputs.end(),[&](auto& input){charge();return input.cell==place.id;});if(edge==f.inputs.end())fail("AI3100","Missing input reference graph edge.",place.at);insert(effect,f.external_paths.at(edge->storage));
            if(place.mutable_)for(auto& [source,type]:available){charge(1+type.reference_depth()+place.type.reference_depth());if(type==place.type.pointee())insert(effect,source);}
        }
        if(f.unwind)f.summary.unwind_effects=f.summary.effects;
    }
    bool rebuild=false;
    // Jacobi rounds keep declaration order from affecting recursive contracts.
    for(;;){
        bool changed=false;std::vector<IrRegionSummary> next;next.reserve(functions.size());
        for(auto& f:functions){charge();f.input_observations_changed=false;auto summary=analyze_loans(f,signatures,false,work);rebuild=rebuild || f.input_rebuild;changed=changed || f.input_observations_changed;
            // Inference is a narrowing analysis. An escaped local is diagnosed
            // in the checking pass, never invented as an input lifetime.
            charge(summary.returned.size()+f.summary.returned.size());
            for(auto& [path,cases]:summary.possible_tags){auto prior=f.summary.possible_tags.find(path);if(prior==f.summary.possible_tags.end() || !std::includes(prior->second.begin(),prior->second.end(),cases.begin(),cases.end()))fail("AI3100","Payload tag inference did not narrow.",{});}
            if(!std::includes(f.summary.possible_components.begin(),f.summary.possible_components.end(),summary.possible_components.begin(),summary.possible_components.end()))fail("AI3100","Payload presence inference did not narrow.",{});
            if(!std::includes(f.summary.returned.begin(),f.summary.returned.end(),summary.returned.begin(),summary.returned.end()))fail("AI3100","Function return region inference did not narrow.",{});
            for(auto& [component,values]:summary.returned_components){charge(1+component.size()+values.size());auto prior=f.summary.returned_components.find(component);if(prior==f.summary.returned_components.end() || !std::includes(prior->second.begin(),prior->second.end(),values.begin(),values.end()))fail("AI3100","Product result region inference did not narrow.",{});}
            for(auto& [path,values]:summary.effects){charge(values.size());auto prior=f.summary.effects.find(path);if(prior!=f.summary.effects.end())charge(prior->second.size());if(prior==f.summary.effects.end() || !std::includes(prior->second.begin(),prior->second.end(),values.begin(),values.end()))fail("AI3100","Function effect region inference did not narrow.",{});}
            for(auto& [path,values]:summary.unwind_effects){charge(values.size());auto prior=f.summary.unwind_effects.find(path);if(prior!=f.summary.unwind_effects.end())charge(prior->second.size());if(prior==f.summary.unwind_effects.end() || !std::includes(prior->second.begin(),prior->second.end(),values.begin(),values.end()))fail("AI3100","Function unwind effect inference did not narrow.",{});}
            changed=changed || summary!=f.summary;next.push_back(std::move(summary));
        }
        if(rebuild)break;
        for(std::size_t i=0;i<functions.size();++i)functions[i].summary=std::move(next[i]);
        if(!changed)break;
    }
    if(rebuild)continue;
    for(auto& f:functions){auto summary=analyze_loans(f,signatures,true,work);if(summary!=f.summary || f.input_rebuild)fail("AI3100","Function region proof differs from its contract.",{});}
    break;
    }
}
