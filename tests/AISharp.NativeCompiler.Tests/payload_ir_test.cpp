#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <functional>
#include <iostream>
namespace aisharp {
int payload_ir_tests(){
    int checks=0,rejections=0;auto check=[&](bool ok){if(!ok)throw std::runtime_error("Payload IR assertion failed");++checks;};auto reject=[&](auto action){bool caught=false;try{action();}catch(const Diagnostic& d){if(d.code!="AI3100")throw;caught=true;++rejections;}check(caught);};
    const std::string source=R"AI(module PayloadProof
intent "Active case storage and cleanup"
type E=A(owned buffer)|B(owned buffer)|Empty
fn main()->int ensures true {
let x:E=A(buffer(42))
return match x {A(b)=>bufferLength(b),B(b)=>bufferLength(b),Empty=>0}
}
)AI";
    auto module=verified_storage(source);check(module.size()==1);auto& main=module[0];check(main.matches.size()==1);auto table=main.products;check(table->definitions[0].payload_ && !table->definitions[0].copy && table->definitions[0].drop);check(table->definitions[0].cases[0].fields==std::vector<int>{0} && table->definitions[0].cases[1].fields==std::vector<int>{1});
    IrPlaceGraph graph(main.places);int subject=main.matches[0].subject;int a=graph.child(subject,0),b=graph.child(subject,1);check(graph.overlaps(a,b));check(graph.cplace(a).find(".data.c0.m0")!=std::string::npos);
    auto corrupt=[&](auto change){auto copy=module;change(copy[0]);reject([&]{StorageVerifier().run(copy);});};
    corrupt([&](IrFunction& f){for(auto& block:f.blocks)for(auto& o:block.ops)if(o.kind=="variant-pack")o.field=127;});
    corrupt([&](IrFunction& f){for(auto& block:f.blocks)for(auto& o:block.ops)if(o.kind=="variant-pack")o.operands.clear();});
    corrupt([&](IrFunction& f){for(auto& block:f.blocks)for(auto& o:block.ops)if(o.kind=="move" && o.place==a){o.place=b;return;}});
    corrupt([&](IrFunction& f){for(auto& p:f.places)if(p.id==subject)p.mutable_=true;});
    corrupt([&](IrFunction& f){for(auto& block:f.blocks)for(auto& o:block.ops)if(o.kind=="variant-tag")o.type=Type::boolean;});
    corrupt([&](IrFunction& f){f.matches[0].subject=-1;});
    corrupt([&](IrFunction& f){f.matches.clear();});
    corrupt([&](IrFunction& f){for(auto& block:f.blocks)for(auto& o:block.ops)if(o.kind=="variant-pack"){o.kind="constant";o.operands.clear();o.field=-1;o.literal=0;return;}});
    {
        auto functions=Parser("module Forged\nintent \"Declared selectors only\"\ntype E=Data(int)|Empty\nfn main()->int ensures true {let x:E=Empty\nreturn match x {Empty=>42,_=>0}}\n").run();Binder().run(functions);
        auto& pattern=*functions[0].body->children.back()->expr->arms[0].pattern;pattern.constant->number=127;
        std::vector<IrFunction> forged;for(auto& function:functions)forged.push_back(StorageLowerer().run(function));reject([&]{StorageVerifier().run(forged);});
    }
    using Change=std::function<void(NativeProductTable&)>;std::vector<Change> changes;
    changes.push_back([](auto& t){t.definitions[0].payload_=false;});
    changes.push_back([](auto& t){t.definitions[0].cases[0].fields={1};});
    changes.push_back([](auto& t){t.definitions[0].cases[0].payload.clear();});
    changes.push_back([](auto& t){t.definitions[0].cases[0].payload[0].type.primitive=2;t.definitions[0].cases[0].payload[0].type.product=42;});
    changes.push_back([](auto& t){t.definitions[0].cases[0].payload[0].type.references={7};});
    changes.push_back([](auto& t){t.definitions[0].copy=true;});
    changes.push_back([](auto& t){t.definitions[0].drop=false;});
    for(auto& change:changes){auto copy=std::make_shared<NativeProductTable>(*table);change(*copy);reject([&]{verify_native_product_table(copy);});}
    auto original=storage_json(module);for(auto& f:module){f.summary.possible_components={{99}};f.summary.possible_tags={{{99},{127}}};f.reference_components.clear();}StorageVerifier().run(module);check(storage_json(module)==original);
    auto references=verified_storage(R"AI(module PayloadReferences
intent "Caller-dependent absent reference alternatives"
type Option<T>=Some(T)|None
fn relay(x:Option<&int>)->Option<&int> ensures true {return x}
fn main()->int ensures true {
let value=42
let empty:Option<&int>=None
let no=relay(empty)
let yes=relay(Some(&value))
return match no {Some(p)=>*p,None=>match yes {Some(p)=>*p,None=>0}}
}
)AI");
    auto reference_proof=storage_json(references);for(auto& f:references){f.summary={};f.reference_components.clear();f.loans.clear();}StorageVerifier().run(references);check(storage_json(references)==reference_proof);
    std::cout<<"{\"supplementalPayloadTestsOnly\":true,\"recomputedProof\":true,\"checks\":"<<checks<<",\"rejections\":"<<rejections<<"}\n";return 0;
}
}
int main(){try{return aisharp::payload_ir_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
