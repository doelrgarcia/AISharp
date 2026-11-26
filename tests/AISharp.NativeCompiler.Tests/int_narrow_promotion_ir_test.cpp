#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
#include <stdexcept>

namespace aisharp {
int verify_narrow_promotion_ir() {
    const auto module=verified_storage(R"AI(module NarrowProof
intent "Verify narrow integer ranges, promotion and fault sites"
faults unwind
fn main()->int ensures true {
let owner=buffer(1)
var n:int=42
var byte_value:byte=(byte)n
var s:sbyte=-128
var t:short=-32768
var u:ushort=65535
var w:ushort=byte_value
return byte_value+s+t+u+w
}
)AI");
    auto find=[](IrFunction& f,const char* kind,Type type)->IrOp& {
        for(auto& block:f.blocks)for(auto& op:block.ops)
            if(op.kind==kind && op.type==type)return op;
        throw std::runtime_error("Missing narrow integer instruction");
    };
    auto proof=module;
    auto& f=proof[0];
    const auto narrow=find(f,"integer-narrow",Type::small_unsigned8).id;
    const auto widen=find(f,"integer-widen",Type::integer).id;
    const auto& sites=f.unwind_plan.sites;
    auto is_site=[&](int id){return std::find(sites.begin(),sites.end(),id)!=sites.end();};
    if(!is_site(narrow) || is_site(widen))throw std::runtime_error("Wrong narrow conversion fault classification");
    StorageVerifier().run(proof);
    if(storage_json(proof)!=storage_json(module))throw std::runtime_error("Narrow proof changed on verification");
    int rejected=0;
    auto reject=[&](auto change){
        auto forged=module;
        change(forged[0]);
        try{StorageVerifier().run(forged);}
        catch(const Diagnostic& d){if(d.code!="AI3100")throw;++rejected;return;}
        throw std::runtime_error("Forged narrow integer proof accepted");
    };
    reject([&](auto& fn){find(fn,"constant",Type::small_signed8).literal=-129;});
    reject([&](auto& fn){find(fn,"constant",Type::small_signed16).literal=32768;});
    reject([&](auto& fn){find(fn,"constant",Type::small_unsigned16).literal=65536;});
    reject([&](auto& fn){find(fn,"integer-narrow",Type::small_unsigned8).kind="integer-widen";});
    reject([&](auto& fn){find(fn,"integer-narrow",Type::small_unsigned8).type=Type::integer;});
    reject([&](auto& fn){find(fn,"integer-narrow",Type::small_unsigned8).wraps=true;});
    reject([&](auto& fn){find(fn,"integer-narrow",Type::small_unsigned8).place=0;});
    reject([&](auto& fn){find(fn,"integer-widen",Type::integer).wraps=true;});
    reject([&](auto& fn){find(fn,"integer-widen",Type::integer).kind="integer-convert";});
    reject([&](auto& fn){find(fn,"integer-widen",Type::small_unsigned16).type=Type::small_signed8;});
    reject([&](auto& fn){std::erase(fn.unwind_plan.sites,narrow);});
    reject([&](auto& fn){fn.unwind_plan.sites.push_back(widen);});
    reject([&](auto& fn){find(fn,"binary:+",Type::integer).operands[0]=find(fn,"integer-widen",Type::integer).operands[0];});
    std::cout<<rejected<<" malformed narrow promotion proofs rejected"<<std::endl;
    return 0;
}
}
int main(){return aisharp::verify_narrow_promotion_ir();}
