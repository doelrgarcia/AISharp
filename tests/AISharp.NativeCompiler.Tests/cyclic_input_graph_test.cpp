#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <functional>
#include <iostream>
namespace aisharp {
int cyclic_graph_tests(){
    const std::string source="module GraphProof\nintent \"No trusted cyclic input hints\"\ntype Node={value:int,next:&Node}\nfn walk(p:&Node,n:int)->&Node\nensures true\n{var cur=p\nvar i=0\nwhile(i<n){cur=(*cur).next\ni++}\nreturn cur}\nfn next(p:&Node)->&Node\nensures true\n{return (*p).next}\nfn keep(seed:&Node)->&Node\nensures true\n{let local=Node(0,seed)\nreturn next(&local)}\nfn nested(p:&&Node)->&Node\nensures true\n{return *p}\nfn main()->int\nensures true\n{return 0}\n";
    using Change=std::function<void(IrFunction&)>;std::vector<Change> changes;
    changes.push_back([](auto& f){f.inputs[0].storage=f.parameters[0];});
    changes.push_back([](auto& f){f.inputs[0].cell=999;});
    changes.push_back([](auto& f){f.inputs[0].type=Type::boolean;});
    changes.push_back([](auto& f){f.inputs[0].mutable_access=!f.inputs[0].mutable_access;});
    changes.push_back([](auto& f){f.inputs[1].loan_id=f.inputs[0].loan_id;});
    changes.push_back([](auto& f){f.inputs[0].parameter=999;});
    changes.push_back([](auto& f){f.inputs[0].folded=!f.inputs[0].folded;});
    changes.push_back([](auto& f){f.external_paths.erase(f.external_paths.begin());});
    changes.push_back([](auto& f){f.external_ids.clear();});
    changes.push_back([](auto& f){auto path=f.external_ids.begin();path->second=999;});
    changes.push_back([](auto& f){for(auto& p:f.external_places)if(p.parent==-1){if(f.summary_places.contains(p.id))f.summary_places.erase(p.id);else f.summary_places.insert(p.id);return;}});
    changes.push_back([](auto& f){f.external_history.at(*f.summary_places.begin()).insert({999,999});});
    changes.push_back([](auto& f){f.inputs.pop_back();});
    changes.push_back([](auto& f){f.inputs[0].structural_event=1;});
    changes.push_back([](auto& f){++f.inputs[0].depth;});
    changes.push_back([](auto& f){f.inputs[0].loan_id=-1;});
    changes.push_back([](auto& f){f.inputs[0].path=f.inputs[0].path.dereference();++f.inputs[0].depth;});
    changes.push_back([](auto& f){for(auto& edge:f.inputs)if(edge.folded){f.summary_anchors.at(edge.storage)=edge.structural_event-1;return;}});
    changes.push_back([](auto& f){for(auto& edge:f.inputs)if(edge.cell<0){--edge.structural_event;return;}});
    changes.push_back([](auto& f){for(auto& edge:f.inputs)if(edge.cell<0 && !edge.folded){edge.storage=f.inputs[0].storage;return;}});
    int rejected=0;for(auto& change:changes){auto module=verified_storage(source);change(module[0]);bool failed=false;try{verify_native_input_graph(module[0]);}catch(const Diagnostic& d){if(d.code!="AI3100")throw;failed=true;}if(!failed)throw std::runtime_error("Malformed cyclic input graph accepted");++rejected;}
    auto nested=verified_storage(source);auto& root_edge=nested[3].inputs[1];root_edge.structural_event=-19999;bool bad_anchor=false;try{verify_native_input_graph(nested[3]);}catch(const Diagnostic& d){if(d.code!="AI3100")throw;bad_anchor=true;}if(!bad_anchor)throw std::runtime_error("Forged inherited graph anchor accepted");++rejected;
    auto module=verified_storage(source);auto expected=storage_json(module);
    for(auto& f:module){f.input_demands[RegionPath{999,999}]={true,{{999,999}},{{999,999}}};f.input_facts=999999;f.input_rebuild=true;f.input_observations_changed=true;f.summary_places={999};f.external_history[999]={{999,999}};f.external_ids[RegionPath{999,999}]=999;for(auto& input:f.inputs){input.cell=999;input.storage=999;input.loan_id=999;input.mutable_access=true;}for(auto& loan:f.loans){loan.parents={999};loan.roots={999};}}
    StorageVerifier().run(module);if(storage_json(module)!=expected)throw std::runtime_error("Forged cyclic region caches trusted");
    auto invalid_id=verified_storage(source);invalid_id[0].id=2147483647;bool failed=false;try{StorageVerifier().run(invalid_id);}catch(const Diagnostic& d){if(d.code!="AI3100")throw;failed=true;}if(!failed)throw std::runtime_error("Unbounded function/event identity accepted");++rejected;
    std::cout<<"{\"rejections\":"<<rejected<<",\"recomputedProof\":true,\"supplementalCyclicGraphTestsOnly\":true}\n";return 0;
}
}
int main(){try{return aisharp::cyclic_graph_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
