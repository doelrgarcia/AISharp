#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <functional>
#include <iostream>
namespace aisharp {
int owner_flow_mutations() {
    const std::string prefix="module OwnerFault\nintent \"Typed ownership verification faults\"\n";
    const std::string simple=prefix+"fn main() -> int\nensures true\n{buffer(3)\nlet b=buffer(2)\nreturn bufferLength(b)}\n";
    using Change=std::function<void(std::vector<IrFunction>&)>;
    std::vector<std::pair<std::string,Change>> tests;
    tests.push_back({"owner-cannot-copy-read",[](auto& ir){for(auto& b:ir[0].blocks)for(auto& o:b.ops)if(o.kind=="move"){o.kind="copy-read";return;}}});
    tests.push_back({"temporary-must-drop",[](auto& ir){for(auto& b:ir[0].blocks)b.ops.erase(std::remove_if(b.ops.begin(),b.ops.end(),[](auto& o){return o.kind=="drop-value";}),b.ops.end());}});
    tests.push_back({"temporary-cannot-drop-twice",[](auto& ir){auto& ops=ir[0].blocks[0].ops;auto it=std::find_if(ops.begin(),ops.end(),[](auto& o){return o.kind=="drop-value";});auto copy=*it;copy.id=100001;ops.insert(it+1,std::move(copy));}});
    tests.push_back({"owner-storage-must-end",[](auto& ir){for(auto& b:ir[0].blocks)b.ops.erase(std::remove_if(b.ops.begin(),b.ops.end(),[](auto& o){return o.kind=="storage-dead";}),b.ops.end());}});
    tests.push_back({"moved-source-cannot-move-again",[](auto& ir){auto& ops=ir[0].blocks[0].ops;auto it=std::find_if(ops.begin(),ops.end(),[](auto& o){return o.kind=="move";});auto copy=*it;copy.id=100002;ops.insert(it+1,std::move(copy));}});
    tests.push_back({"branch-cannot-abandon-temporary",[&](auto& ir){ir=verified_storage(prefix+"fn choose(b: bool) -> int\nensures true\n{buffer(3)\nif(b){return 1}\nreturn 2}\nfn main() -> int\nensures true\n{return choose(true)}\n");auto& ops=ir[0].blocks[0].ops;auto it=std::find_if(ops.begin(),ops.end(),[](auto& o){return o.kind=="drop-value";});auto drop=*it;ops.erase(it);ir[0].blocks[1].ops.push_back(std::move(drop));}});
    tests.push_back({"loop-cannot-overwrite-temporary",[&](auto& ir){ir=verified_storage(prefix+"fn main() -> int\nensures true\n{while(true){buffer(1)}}\n");for(auto& b:ir[0].blocks)b.ops.erase(std::remove_if(b.ops.begin(),b.ops.end(),[](auto& o){return o.kind=="drop-value";}),b.ops.end());}});
    for(auto& [name,change]:tests){auto ir=verified_storage(simple);change(ir);bool rejected=false;try{StorageVerifier().run(ir);}catch(const Diagnostic& d){if(d.code!="AI3100" && d.code!="AI3102")throw;rejected=true;}if(!rejected)throw std::runtime_error("Corrupt ownership accepted: "+name);std::cout<<name<<'\n';}
    auto valid=verified_storage(simple);auto first=storage_json(valid);StorageVerifier().run(valid);if(storage_json(valid)!=first)throw std::runtime_error("Owner proof is not stable");
    std::cout<<"PASS 7 owned-value mutations rejected; valid proof stable\n";return 0;
}
}
int main(){try{return aisharp::owner_flow_mutations();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
