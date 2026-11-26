// Fault injection into the private verified representation. No public IR loader.
#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <functional>
#include <iostream>
namespace aisharp {
int mutation_tests() {
    const std::string source="module Mutation\nintent \"Verifier structural faults\"\nfn helper(b: bool) -> int\nensures true\n{var n=1\nif(b){n=2}else{n=3}\nlet z=b && ((n+1)==4)\nreturn n}\nfn main() -> int\nensures true\n{let n=1\nreturn helper(n==1)}\n";
    using Change=std::function<void(std::vector<IrFunction>&)>;
    auto find_op=[](std::vector<IrFunction>& ir,const std::string& kind)->IrOp& {for(auto& f:ir)for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind==kind)return o;throw std::runtime_error("Missing mutation target");};
    std::vector<std::pair<std::string,Change>> cases;
    cases.push_back({"invalid-edge",[](auto& ir){ir[0].blocks[0].successors={999999};}});
    cases.push_back({"duplicate-place",[](auto& ir){ir[0].places.push_back(ir[0].places[0]);}});
    cases.push_back({"unknown-operator",[&](auto& ir){find_op(ir,"binary:+").kind="binary:injected";}});
    cases.push_back({"wrong-read-type",[&](auto& ir){find_op(ir,"copy-read").type=Type::unit;}});
    cases.push_back({"bad-phi-edge",[&](auto& ir){find_op(ir,"phi").incoming[0]=999999;}});
    cases.push_back({"bad-call-target",[&](auto& ir){find_op(ir,"call").function=999999;}});
    cases.push_back({"wrong-call-arity",[&](auto& ir){find_op(ir,"call").operands.clear();}});
    cases.push_back({"missing-parameter-init",[](auto& ir){auto& ops=ir[0].blocks[0].ops;ops.erase(std::remove_if(ops.begin(),ops.end(),[](auto& o){return o.kind=="parameter";}),ops.end());}});
    cases.push_back({"non-dominating-return",[](auto& ir){auto& f=ir[0];int branch_value=-1;for(auto& o:f.blocks[1].ops)if(o.kind=="constant")branch_value=o.id;if(branch_value<0)throw std::runtime_error("Missing branch literal");for(auto& b:f.blocks)if(b.end=="return")b.result=branch_value;}});
    cases.push_back({"missing-storage-end",[](auto& ir){for(auto& b:ir[0].blocks)b.ops.erase(std::remove_if(b.ops.begin(),b.ops.end(),[](auto& o){return o.kind=="storage-dead";}),b.ops.end());}});
    cases.push_back({"read-before-init",[](auto& ir){auto& f=ir[1];for(auto& b:f.blocks)b.ops.erase(std::remove_if(b.ops.begin(),b.ops.end(),[](auto& o){return o.kind=="write";}),b.ops.end());}});
    cases.push_back({"immutable-second-write",[](auto& ir){auto& ops=ir[1].blocks[0].ops;auto it=std::find_if(ops.begin(),ops.end(),[](auto& o){return o.kind=="write";});auto copy=*it;copy.id=100001;ops.insert(it+1,std::move(copy));}});
    cases.push_back({"read-after-storage-end",[](auto& ir){auto& ops=ir[1].blocks[0].ops;auto read=std::find_if(ops.begin(),ops.end(),[](auto& o){return o.kind=="copy-read";});auto dead=std::find_if(ops.begin(),ops.end(),[](auto& o){return o.kind=="storage-dead";});std::iter_swap(read,dead);}});
    cases.push_back({"empty-control-flow",[](auto& ir){ir[0].blocks.clear();}});
    cases.push_back({"value-use-before-definition",[](auto& ir){auto& ops=ir[1].blocks[0].ops;auto read=std::find_if(ops.begin(),ops.end(),[](auto& o){return o.kind=="copy-read";});auto copy=*read;ops.erase(read);auto dead=std::find_if(ops.begin(),ops.end(),[](auto& o){return o.kind=="storage-dead";});ops.insert(dead,std::move(copy));}});
    cases.push_back({"possible-immutable-reinitialization",[](auto& ir){ir=verified_storage("module Branch\nintent \"Possible initialization\"\nfn helper(b: bool) -> int\nensures true\n{var n: int\nif(b){n=1}\nn=2\nreturn n}\nfn main() -> int\nensures true\n{return helper(true)}\n");for(auto& p:ir[0].places)if(!p.parameter)p.mutable_=false;}});
    cases.push_back({"unproved-conditional-edge-removal",[](auto& ir){for(auto& b:ir[0].blocks)if(b.end=="branch" && b.successors.size()==2){b.successors.resize(1);return;}throw std::runtime_error("Missing conditional edge");}});
    for(auto& [name,change]:cases){auto ir=verified_storage(source);change(ir);bool rejected=false;try{StorageVerifier().run(ir);}catch(const Diagnostic& d){if(d.code!="AI3100" && d.code!="AI3102")throw;rejected=true;}if(!rejected)throw std::runtime_error("Accepted corrupted IR: "+name);std::cout<<name<<'\n';}
    auto ir=verified_storage(source);auto first=storage_json(ir);StorageVerifier().run(ir);if(first!=storage_json(ir))throw std::runtime_error("Verifier rerun changed proof");
    std::cout<<"PASS 17 corrupted representations rejected; valid proof stable on revalidation\n";return 0;
}
}
int main(){try{return aisharp::mutation_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
