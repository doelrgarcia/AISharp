#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <functional>
#include <iostream>
#include <fstream>
namespace aisharp {
int alias_reuse(const char* path){
    const std::string source="module AliasReuse\nintent \"Private verified IR alias reuse\"\nfn main()->int\nensures true\n{var b=buffer(1)\nlet p=&mut b\nbufferWrite(p,0,42)\nreturn bufferPeek(p,0)}\n";auto ir=verified_storage(source);
    int alias=-1;for(auto& b:ir[0].blocks)for(auto& o:b.ops){if(o.kind=="call" && o.function==-8)alias=o.operands[0];if(o.kind=="call" && o.function==-7){if(alias<0)throw std::runtime_error("Missing private access alias");o.operands[0]=alias;}}
    StorageVerifier().run(ir);std::ofstream out(path,std::ios::binary);out<<StorageEmitter().run(ir,0,native_source_sha(source));if(!out)throw std::runtime_error("Private C evidence write failed");return 0;
}
int region_mutations(){
    const std::string source="module RegionMutations\nintent \"Checked function regions\"\nfn id(p:&int)->&int\nensures true\n{return p}\nfn main()->int\nensures true\n{let x=42\nreturn *id(&x)}\n";
    using Change=std::function<void(std::vector<IrFunction>&)>;std::vector<Change> changes;
    changes.push_back([](auto& ir){ir[0].type=Type::reference(Type::boolean,Type::Borrow::shared);});
    changes.push_back([](auto& ir){ir[0].places[0].type=Type::reference(Type::boolean,Type::Borrow::shared);});
    changes.push_back([](auto& ir){for(auto& b:ir[1].blocks)for(auto& o:b.ops)if(o.kind=="call")o.operands.clear();});
    changes.push_back([](auto& ir){for(auto& b:ir[1].blocks)for(auto& o:b.ops)if(o.kind=="call")o.type=Type::reference(Type::boolean,Type::Borrow::shared);});
    changes.push_back([](auto& ir){for(auto& b:ir[1].blocks)for(auto& o:b.ops)if(o.kind=="borrow-shared")o.place=-3;});
    changes.push_back([](auto& ir){for(auto& b:ir[0].blocks)for(auto& o:b.ops)if(o.kind=="copy-read")o.kind="reference-use";});
    int rejected=0;for(auto& change:changes){auto ir=verified_storage(source);change(ir);bool failed=false;try{StorageVerifier().run(ir);}catch(const Diagnostic& d){if(d.code!="AI3100")throw;failed=true;}if(!failed)throw std::runtime_error("Corrupt function reference representation accepted");++rejected;}
    auto lower=[](const std::string& text){auto functions=Parser(text).run();Binder().run(functions);std::vector<IrFunction> ir;for(auto& f:functions)ir.push_back(StorageLowerer().run(f));return ir;};
    for(const std::string body:{"fn bad(x:int)->&int\nensures true\n{return &x}\n", "fn bad(p:&int)->&&int\nensures true\n{return &p}\n", "fn bad(dst:&mut &int)->unit\nensures true\n{let x=1\n*dst=&x\nreturn}\n"}){
        auto ir=lower("module Bad\nintent \"No forged lifetime\"\n"+body+"fn main()->int\nensures true\n{return 0}\n");ir[0].inputs={{0,1,-3,Type::reference(Type::integer,Type::Borrow::shared),{}}};ir[0].summary.returned={{0,1}};ir[0].summary.effects[RegionPath{0,1}]={{0,1}};
        bool failed=false;try{StorageVerifier().run(ir);}catch(const Diagnostic& d){if(d.code!="AI3117")throw;failed=true;}if(!failed)throw std::runtime_error("Forged region hints bypassed local escape check");++rejected;
    }
    auto ir=verified_storage(source);auto proof=storage_json(ir);for(auto& f:ir){f.inputs.clear();f.summary.returned={{999,999}};f.summary.effects[RegionPath{999,999}]={{999,999}};for(auto& loan:f.loans){loan.roots={999};loan.parents={999};loan.region={999};}}
    StorageVerifier().run(ir);if(storage_json(ir)!=proof)throw std::runtime_error("Function input/summary/loan hints trusted");
    std::cout<<"PASS "<<rejected<<" corrupted function-region representations rejected; all region hints recomputed\n";return 0;
}
}
int main(int argc,char** argv){try{if(argc==2)return aisharp::alias_reuse(argv[1]);return aisharp::region_mutations();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
