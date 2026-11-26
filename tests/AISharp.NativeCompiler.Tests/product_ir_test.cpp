#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <functional>
#include <iostream>
namespace aisharp {
int product_ir_tests(){
    const std::string source="module ProductProof\nintent \"Reject forged product proof\"\ntype Pair={left:int,right:int}\ntype View={p:&int}\nfn get(v:&View)->&int\nensures true\n{return (*v).p}\nfn main()->int\nensures true\n{let x=42\nlet v=View(&x)\nvar p=Pair(1,2)\np.left=1\nreturn Pair(*get(&v),0).left}\n";
    using Module=std::vector<IrFunction>;using Change=std::function<void(Module&)>;
    auto place=[](Module& ir)->IrPlace&{for(auto& f:ir)for(auto& p:f.places)if(p.parent!=-1)return p;throw std::runtime_error("Missing field place");};
    auto op=[](Module& ir,const std::string& kind)->IrOp&{for(auto& f:ir)for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind==kind)return o;throw std::runtime_error("Missing product instruction");};
    std::vector<Change> changes;
    changes.push_back([&](auto& ir){place(ir).parent=place(ir).id;});
    changes.push_back([&](auto& ir){place(ir).field=999;});
    changes.push_back([&](auto& ir){place(ir).type=Type::boolean;});
    changes.push_back([&](auto& ir){place(ir).mutable_=!place(ir).mutable_;});
    changes.push_back([&](auto& ir){place(ir).constant=!place(ir).constant;});
    changes.push_back([&](auto& ir){place(ir).parameter=true;});
    changes.push_back([&](auto& ir){for(auto& f:ir)for(auto& child:f.places)if(child.parent!=-1)for(auto& p:f.places)if(p.parent==child.parent && p.field!=child.field){p.field=child.field;return;}throw std::runtime_error("Missing sibling place");});
    changes.push_back([&](auto& ir){for(auto& f:ir)for(auto it=f.places.begin();it!=f.places.end();++it)if(it->parent!=-1){f.places.erase(it);return;}});
    changes.push_back([&](auto& ir){op(ir,"aggregate-pack").operands.clear();});
    changes.push_back([&](auto& ir){op(ir,"aggregate-pack").type=Type::integer;});
    changes.push_back([&](auto& ir){op(ir,"aggregate-pack").field=0;});
    changes.push_back([&](auto& ir){op(ir,"aggregate-extract").field=999;});
    changes.push_back([&](auto& ir){op(ir,"aggregate-extract").type=Type::boolean;});
    changes.push_back([&](auto& ir){op(ir,"aggregate-extract").operands.clear();});
    changes.push_back([&](auto& ir){op(ir,"reference-field").field=999;});
    changes.push_back([&](auto& ir){auto& o=op(ir,"reference-field");o.type=Type::reference(o.type.pointee(),Type::Borrow::exclusive);});
    changes.push_back([&](auto& ir){op(ir,"reference-field").operands.clear();});
    changes.push_back([&](auto& ir){op(ir,"reference-field").type=Type::integer;});
    int rejections=0;
    for(auto& change:changes){auto ir=verified_storage(source);change(ir);bool rejected=false;try{StorageVerifier().run(ir);}catch(const Diagnostic& d){if(d.code!="AI3100")throw;rejected=true;}if(!rejected)throw std::runtime_error("Forged product storage accepted");++rejections;}
    auto ir=verified_storage(source);auto expected=storage_json(ir);
    for(auto& f:ir){f.inputs.clear();f.external_places=f.places;f.external_paths.clear();f.external_paths.emplace(999,RegionPath{999,999});f.reference_components[999][{999}]={999};f.summary.returned_components[ComponentPath{999}]={{999,999}};for(auto& l:f.loans){l.roots={999};l.parents={999};}}
    StorageVerifier().run(ir);if(storage_json(ir)!=expected)throw std::runtime_error("Forged product region hints trusted");
    std::cout<<"{\"rejections\":"<<rejections<<",\"recomputedProof\":true,\"supplementalIrTestsOnly\":true}\n";return 0;
}
}
int main(){try{return aisharp::product_ir_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
