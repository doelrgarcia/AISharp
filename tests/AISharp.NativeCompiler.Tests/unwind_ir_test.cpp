#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
namespace aisharp {
int unwind_ir_tests(){
    int checks=0,rejections=0;auto check=[&](bool ok){if(!ok)throw std::runtime_error("Unwind IR assertion failed");++checks;};
    auto reject=[&](auto action,const char* code="AI3100"){bool caught=false;try{action();}catch(const Diagnostic& d){if(d.code!=code)throw;caught=true;++rejections;}check(caught);};
    auto module=verified_storage(R"AI(module UnwindProof
intent "Recompute native fault cleanup"
faults unwind
fn zero()->int ensures true {return 0}
fn bad(b:owned buffer)->int ensures true {return 1/zero()}
fn main()->int ensures true {let earlier=buffer(3)
return bad(buffer(4))}
)AI");
    check(module.size()==3 && module[2].unwind);check(module[2].unwind_plan.nodes.size()>=3 && module[2].unwind_plan.sites.size()>=2);
    auto corrupt=[&](auto change){auto copy=module;change(copy[2]);reject([&]{StorageVerifier().run(copy);});};
    corrupt([](auto& f){f.unwind_plan.nodes.clear();});
    corrupt([](auto& f){f.unwind_plan.sites.clear();});
    corrupt([](auto& f){f.unwind_plan.nodes.push_back(f.unwind_plan.nodes[0]);});
    corrupt([](auto& f){std::reverse(f.unwind_plan.nodes.begin(),f.unwind_plan.nodes.end());});
    corrupt([](auto& f){f.unwind_plan.nodes[0].first='x';});
    corrupt([](auto& f){f.unwind_plan.nodes[0].second=999999;});
    corrupt([](auto& f){f.unwind_plan.sites[0]=999999;});
    corrupt([](auto& f){f.unwind_plan.sites.push_back(f.unwind_plan.sites[0]);});
    corrupt([](auto& f){f.unwind=false;});
    corrupt([](auto& f){f.fault_mode_location={};});
    corrupt([](auto& f){f.fault_mode_location.origin=SourceOrigin::core_outcomes;});
    corrupt([](auto& f){++f.fault_mode_location.offset;});
    corrupt([&](auto& f){for(auto& b:f.blocks){auto p=std::find_if(b.ops.begin(),b.ops.end(),[](auto& o){return o.kind=="storage-dead";});if(p!=b.ops.end()){b.ops.erase(p);return;}}check(false);});
    auto copy=module;copy[0].unwind=false;copy[0].unwind_plan={};reject([&]{StorageVerifier().run(copy);});
    auto abort=verified_storage("module Abort\nintent \"Default ABI stays separate\"\nfn main()->int ensures true {return 42}\n");abort[0].unwind_plan.sites={0};reject([&]{StorageVerifier().run(abort);});
    auto proof=storage_json(module);for(auto& f:module){f.summary.unwind_effects[RegionPath{999,999}]={{999,999}};f.loans.clear();f.reference_components.clear();}StorageVerifier().run(module);check(storage_json(module)==proof);
    auto regions=verified_storage(R"AI(module SeparateEffects
intent "Normal replacement must not inherit an entry-fault alias"
faults unwind
fn set(dst:&mut &int,p:&int)->unit ensures true {*dst=p}
fn main()->int ensures true {let fresh=42
var p=&fresh
{let old=0
p=&old
set(&mut p,&fresh)}
return *p}
)AI");
    check(!regions[0].summary.effects.empty() && !regions[0].summary.unwind_effects.empty() && regions[0].summary.effects!=regions[0].summary.unwind_effects);
    auto region_proof=storage_json(regions);for(auto& f:regions){f.summary={};f.loans.clear();f.inputs.clear();}StorageVerifier().run(regions);check(storage_json(regions)==region_proof);
    IrFunction bound{};bound.unwind=true;for(int i=0;i<50000;++i)bound.places.push_back({i,Type::buffer,{},false,false,false});check(native_unwind_plan(bound).nodes.size()==50000);bound.places.push_back({50000,Type::buffer,{},false,false,false});reject([&]{native_unwind_plan(bound);});
    std::cout<<"{\"supplementalUnwindTestsOnly\":true,\"recomputedProof\":true,\"checks\":"<<checks<<",\"rejections\":"<<rejections<<"}\n";return 0;
}
}
int main(){try{return aisharp::unwind_ir_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
