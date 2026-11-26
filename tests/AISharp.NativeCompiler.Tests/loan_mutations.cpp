#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <functional>
#include <iostream>
namespace aisharp {
int loan_mutations(){
    const std::string source="module Loans\nintent \"Corrupted reference proof\"\nfn main() -> int\nensures true\n{var x=1\nlet p=&mut x\n*p=2\nreturn x}\n";
    auto find=[](auto& ir,const std::string& kind)->IrOp&{for(auto& b:ir[0].blocks)for(auto& o:b.ops)if(o.kind==kind)return o;throw std::runtime_error("Missing mutation target");};
    using Change=std::function<void(std::vector<IrFunction>&)>;std::vector<std::pair<std::string,Change>> cases;
    cases.push_back({"borrow-mode-type",[&](auto& ir){find(ir,"borrow-exclusive").kind="borrow-shared";}});
    cases.push_back({"borrow-immutable",[&](auto& ir){auto root=find(ir,"borrow-exclusive").place;for(auto& p:ir[0].places)if(p.id==root)p.mutable_=false;}});
    cases.push_back({"borrow-wrong-root-type",[&](auto& ir){find(ir,"borrow-exclusive").type=Type::reference(Type::boolean,Type::Borrow::exclusive);}});
    cases.push_back({"borrow-missing-root",[&](auto& ir){find(ir,"borrow-exclusive").place=999999;}});
    cases.push_back({"reference-access-type",[&](auto& ir){find(ir,"reference-use").type=Type::integer;}});
    cases.push_back({"reference-write-shared",[&](auto& ir){for(auto& p:ir[0].places)if(p.type.is_reference())p.type=Type::reference(Type::integer,Type::Borrow::shared);for(auto& b:ir[0].blocks)for(auto& o:b.ops){if(o.type.is_reference())o.type=Type::reference(Type::integer,Type::Borrow::shared);if(o.kind=="borrow-exclusive")o.kind="borrow-shared";}}});
    cases.push_back({"access-alias-escape",[&](auto& ir){auto& write=find(ir,"reference-write");auto alias=write.operands[0];int local=-1;for(auto& p:ir[0].places)if(p.type.is_reference())local=p.id;write.kind="write";write.place=local;write.operands={alias};}});
    cases.push_back({"exclusive-value-duplicate",[&](auto& ir){auto& b=ir[0].blocks[0];auto it=std::find_if(b.ops.begin(),b.ops.end(),[&](auto& o){return o.kind=="write" && o.operands[0]==find(ir,"borrow-exclusive").id;});auto duplicate=*it;duplicate.id=100001;b.ops.insert(it+1,duplicate);}});
    cases.push_back({"uninitialized-reference",[&](auto& ir){auto root=find(ir,"reference-use").place;for(auto& b:ir[0].blocks)b.ops.erase(std::remove_if(b.ops.begin(),b.ops.end(),[&](auto& o){return o.kind=="write" && o.place==root;}),b.ops.end());}});
    cases.push_back({"unproved-call-reference",[&](auto& ir){auto& o=find(ir,"reference-use");o.kind="call";o.place=-1;o.function=-2;o.operands={};}});
    for(auto& [name,change]:cases){auto ir=verified_storage(source);change(ir);bool rejected=false;try{StorageVerifier().run(ir);}catch(const Diagnostic& d){if(d.code!="AI3100" && d.code!="AI3102" && d.code!="AI3114")throw;rejected=true;}if(!rejected)throw std::runtime_error("Accepted mutated reference IR: "+name);std::cout<<name<<'\n';}
    auto ir=verified_storage(source);auto proof=storage_json(ir);ir[0].loans[0].roots={999};ir[0].loans[0].parents={999};ir[0].loans[0].region={999};StorageVerifier().run(ir);if(storage_json(ir)!=proof)throw std::runtime_error("Loan hints trusted or proof unstable");
    std::cout<<"PASS 10 corrupted reference representations rejected; loan facts recomputed\n";return 0;
}
}
int main(){try{return aisharp::loan_mutations();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
