#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
namespace aisharp {
int propagation_ir_tests(){
    int checks=0,rejections=0;auto check=[&](bool ok){if(!ok)throw std::runtime_error("Propagation IR assertion failed");++checks;};
    auto reject=[&](auto action,const char* code="AI3100"){bool caught=false;try{action();}catch(const Diagnostic& d){if(d.code!=code)throw;caught=true;++rejections;}check(caught);};
    auto module=verified_storage(R"AI(module FailureProof
intent "Recompute declared propagation and owner proof"
fn get()->int fails bool ensures true {fail true}
fn main()->int fails bool ensures true {let owner=buffer(3)
return get()?}
)AI");
    auto& original=module[1];check(original.fallible && original.matches.size()==1 && original.matches[0].propagation);
    int failure=-1,success=-1;for(auto& group:original.matches[0].groups)for(auto& arm:group.arms)(arm.source==1 ? failure : success)=arm.value;
    check(failure>=0 && success>=0);
    auto op=[](IrFunction& f,int id)->IrOp&{for(auto& b:f.blocks)for(auto& o:b.ops)if(o.id==id)return o;throw std::runtime_error("Missing corruption target");};
    auto corrupt=[&](auto change){auto copy=module;change(copy[1]);reject([&]{StorageVerifier().run(copy);});};
    corrupt([](auto& f){f.success_type=Type::boolean;});
    corrupt([](auto& f){f.error_type=Type::integer;});
    corrupt([](auto& f){f.failure_declaration={};});
    corrupt([](auto& f){f.fallible=false;});
    corrupt([](auto& f){f.failure_declaration.origin=SourceOrigin::core_outcomes;});
    corrupt([](auto& f){f.matches[0].propagation=false;});
    corrupt([](auto& f){f.matches[0].sources[1].guard_mode=0;});
    corrupt([&](auto& f){op(f,failure).cause_place=-1;});
    corrupt([&](auto& f){op(f,failure).cause_place=0;});
    corrupt([&](auto& f){op(f,success).cause_place=f.matches[0].subject;});
    corrupt([&](auto& f){op(f,failure).operands.clear();});
    corrupt([&](auto& f){op(f,failure).field=0;});
    corrupt([&](auto& f){auto& payload=op(f,op(f,failure).operands[0]);payload.kind="constant";payload.place=-1;payload.literal=1;});
    corrupt([&](auto& f){auto& payload=op(f,success);payload.kind="constant";payload.place=-1;payload.literal=42;});
    corrupt([&](auto& f){for(auto& b:f.blocks)if(b.result==failure){b.end="goto";b.result=-1;b.successors={f.matches[0].merge};return;}});
    corrupt([&](auto& f){for(auto& b:f.blocks)if(b.result==failure){auto i=std::find_if(b.ops.begin(),b.ops.end(),[](auto& o){return o.kind=="storage-dead" && o.place==0;});check(i!=b.ops.end());b.ops.erase(i);return;}});
    corrupt([&](auto& f){for(auto& b:f.blocks)if(b.result==failure){auto i=std::find_if(b.ops.begin(),b.ops.end(),[](auto& o){return o.kind=="storage-dead" && o.place==0;});check(i!=b.ops.end());auto extra=*i;int maximum=0;for(auto& block:f.blocks)for(auto& o:block.ops)maximum=std::max(maximum,o.id);extra.id=maximum+1;b.ops.push_back(extra);return;}});
    corrupt([&](auto& f){for(auto& b:f.blocks)if(b.result==failure){auto i=std::find_if(b.ops.begin(),b.ops.end(),[&](auto& o){return o.kind=="storage-dead" && o.place==f.matches[0].subject;});check(i!=b.ops.end());b.ops.erase(i);return;}});
    auto clean=storage_json(module);for(auto& f:module){f.summary={};f.reference_components.clear();f.loans.clear();for(auto& b:f.blocks){b.reachable=false;b.initialized_in.clear();b.active_out.clear();}}StorageVerifier().run(module);check(storage_json(module)==clean);
    // A forged producer-free hint cannot erase an actual API obligation.
    auto ast=Parser("module Lost\nintent \"Recompute producer obligations\"\nfn get()->int fails bool ensures true {return 1}\nfn main()->int ensures true {get()\nreturn 42}\n").run();Binder().run(ast);std::vector<IrFunction> lost;for(auto& f:ast)lost.push_back(StorageLowerer().run(f));reject([&]{StorageVerifier().run(lost);},"AI3133");
    std::cout<<"{\"supplementalPropagationTestsOnly\":true,\"recomputedProof\":true,\"checks\":"<<checks<<",\"rejections\":"<<rejections<<"}\n";return 0;
}
}
int main(){try{return aisharp::propagation_ir_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
