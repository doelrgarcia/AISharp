#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
#include <stdexcept>

namespace aisharp {
int verify_int_promotion_ir() {
    const auto module=verified_storage(R"AI(module PromotionProof
intent "Verify signed promotion and fault sites"
faults unwind
fn main()->int ensures true {
let owner=buffer(1)
var narrow:int=1
var wide:long=42L
let checked_sum=wide+narrow
let wrapping=unchecked(wide+narrow)
let shifted=wide<<narrow
let bitwise=wide|narrow
return unchecked((int)(checked_sum+wrapping+shifted+bitwise))
}
)AI");
    auto find=[](IrFunction& f,const char* kind,bool wraps)->IrOp& {
        for(auto& block:f.blocks)for(auto& op:block.ops)
            if(op.kind==kind && op.type==Type::long_integer && op.wraps==wraps)return op;
        throw std::runtime_error("Missing signed operator in IR");
    };
    auto proof=module;
    auto& f=proof[0];
    auto checked=find(f,"binary:+",false).id;
    auto wrapping=find(f,"binary:+",true).id;
    auto shifted=find(f,"binary:<<",false).id;
    auto bitwise=find(f,"binary:|",false).id;
    const auto& sites=f.unwind_plan.sites;
    auto is_site=[&](int id){return std::find(sites.begin(),sites.end(),id)!=sites.end();};
    if(!is_site(checked) || is_site(wrapping) || is_site(shifted) || is_site(bitwise))
        throw std::runtime_error("Incorrect wide arithmetic fault classification");
    StorageVerifier().run(proof);
    if(storage_json(proof)!=storage_json(module))throw std::runtime_error("Operator proof changed on verification");
    int rejected=0;
    auto reject=[&](auto change){
        auto forged=module;
        change(forged[0]);
        try{StorageVerifier().run(forged);}
        catch(const Diagnostic& d){if(d.code!="AI3100")throw;++rejected;return;}
        throw std::runtime_error("Forged signed operator accepted");
    };
    reject([&](auto& fn){find(fn,"binary:+",false).type=Type::integer;});
    reject([&](auto& fn){auto& op=find(fn,"binary:+",false);op.operands[1]=find(fn,"binary:<<",false).operands[1];});
    reject([&](auto& fn){find(fn,"binary:+",false).wraps=true;});
    reject([&](auto& fn){find(fn,"binary:+",true).wraps=false;});
    reject([&](auto& fn){std::erase(fn.unwind_plan.sites,checked);});
    reject([&](auto& fn){fn.unwind_plan.sites.push_back(wrapping);});
    reject([&](auto& fn){auto& op=find(fn,"binary:<<",false);op.operands[1]=op.operands[0];});
    reject([&](auto& fn){auto& op=find(fn,"binary:<<",false);op.operands[0]=op.operands[1];});
    reject([&](auto& fn){find(fn,"binary:<<",false).type=Type::integer;});
    reject([&](auto& fn){fn.unwind_plan.sites.push_back(shifted);});
    reject([&](auto& fn){find(fn,"binary:|",false).kind="binary:&&";});
    std::cout<<rejected<<" malformed signed promotion proofs rejected"<<std::endl;
    return 0;
}
}
int main(){return aisharp::verify_int_promotion_ir();}
