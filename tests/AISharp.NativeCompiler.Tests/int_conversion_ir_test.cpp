#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
#include <stdexcept>

namespace aisharp {
int verify_int_conversion_ir() {
    const auto module=verified_storage(R"AI(module ConversionProof
intent "Verify the signed width conversion instructions"
faults unwind
fn main()->int ensures true {
let owner=buffer(1)
var narrow:int=-1
var wide:long=narrow
var out:int=(int)wide
return unchecked((i32)wide)+out
}
)AI");
    auto find=[](IrFunction& f,Type target,bool wraps)->IrOp&{
        for(auto& block:f.blocks)for(auto& op:block.ops)
            if(op.kind=="integer-convert" && op.type==target && op.wraps==wraps)return op;
        throw std::runtime_error("Missing integer conversion");
    };
    auto proof=module;
    auto& f=proof[0];
    auto widening=find(f,Type::long_integer,false).id;
    auto checked=find(f,Type::integer,false).id;
    auto wrapping=find(f,Type::integer,true).id;
    auto sites=f.unwind_plan.sites;
    auto is_fault_site=[&](int id){return std::find(sites.begin(),sites.end(),id)!=sites.end();};
    if(is_fault_site(widening) || !is_fault_site(checked) || is_fault_site(wrapping))
        throw std::runtime_error("Invalid fault modes for signed width conversions");
    StorageVerifier().run(proof);
    if(storage_json(proof)!=storage_json(module))throw std::runtime_error("Conversion proof changed on verification");
    int rejected=0;
    auto reject=[&](auto change){
        auto forged=module;
        change(forged[0]);
        try{StorageVerifier().run(forged);}
        catch(const Diagnostic& diagnostic){if(diagnostic.code!="AI3100")throw;++rejected;return;}
        throw std::runtime_error("Forged signed width conversion accepted");
    };
    reject([&](auto& fn){find(fn,Type::long_integer,false).wraps=true;});
    reject([&](auto& fn){find(fn,Type::long_integer,false).type=Type::boolean;});
    reject([&](auto& fn){find(fn,Type::integer,false).operands.clear();});
    reject([&](auto& fn){auto& op=find(fn,Type::integer,false);op.operands.push_back(op.operands[0]);});
    reject([&](auto& fn){find(fn,Type::integer,false).place=0;});
    reject([&](auto& fn){find(fn,Type::integer,false).literal=1;});
    reject([&](auto& fn){find(fn,Type::integer,false).operands[0]=find(fn,Type::long_integer,false).operands[0];});
    reject([&](auto& fn){find(fn,Type::integer,false).wraps=true;});
    reject([&](auto& fn){find(fn,Type::integer,true).wraps=false;});
    reject([&](auto& fn){std::erase(fn.unwind_plan.sites,checked);});
    reject([&](auto& fn){fn.unwind_plan.sites.push_back(widening);});
    std::cout<<rejected<<" malformed signed conversion proofs rejected\n";
    return 0;
}
}

int main(){return aisharp::verify_int_conversion_ir();}
