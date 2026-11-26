#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
namespace aisharp {
int core_outcome_tests(){
    int checks=0,rejections=0;auto check=[&](bool ok){if(!ok)throw std::runtime_error("Core outcome assertion failed");++checks;};auto reject=[&](auto action){bool caught=false;try{action();}catch(const Diagnostic& d){if(d.code!="AI3100")throw;caught=true;++rejections;}check(caught);};
    auto module=verified_storage(R"AI(module CoreProof
intent "Canonical core declaration origins"
fn main()->int ensures true {
let x:Result<Option<int>,bool>=Ok(Some(42))
return match x {Ok(o)=>match o {Some(n)=>n,None=>0},Err(_)=>0}}
)AI");
    auto corrupt_input=[&](auto change){auto copy=module;change(copy[0]);reject([&]{StorageVerifier().run(copy);});};
    corrupt_input([](auto& f){f.blocks[0].ops[0].at.origin=SourceOrigin::core_outcomes;});
    corrupt_input([](auto& f){f.blocks[0].ops[0].at.origin=static_cast<SourceOrigin>(99);});
    corrupt_input([](auto& f){f.declaration.origin=SourceOrigin::core_outcomes;});
    corrupt_input([](auto& f){f.places[0].at.origin=SourceOrigin::core_outcomes;});
    corrupt_input([](auto& f){f.matches[0].at.origin=SourceOrigin::core_outcomes;});
    corrupt_input([](auto& f){f.matches[0].groups[0].arms[0].value_at.origin=SourceOrigin::core_outcomes;});
    auto table=module[0].products;verify_native_product_table(table);check(table->generics.size()==2);int result=-1,instance=-1;
    for(std::size_t i=0;i<table->generics.size();++i)if(table->generics[i].name=="Result")result=static_cast<int>(i);check(result>=0);
    for(std::size_t i=0;i<table->definitions.size();++i){auto& d=table->definitions[i];auto type=Type::from_shape(table,{Type::product,static_cast<int>(i),{}});check(type.is_core_carrier(table->generics[static_cast<std::size_t>(d.generic)].name));check(!type.is_core_carrier("Other"));if(d.generic==result)instance=static_cast<int>(i);}check(instance>=0);
    auto corrupt=[&](auto change){auto copy=std::make_shared<NativeProductTable>(*table);change(*copy);reject([&]{verify_native_product_table(copy);});};
    corrupt([&](auto& t){t.generics[static_cast<std::size_t>(result)].at.origin=SourceOrigin::input;});
    corrupt([&](auto& t){++t.generics[static_cast<std::size_t>(result)].at.offset;});
    corrupt([&](auto& t){t.generics[static_cast<std::size_t>(result)].parameters[0].at.origin=SourceOrigin::input;});
    corrupt([&](auto& t){++t.generics[static_cast<std::size_t>(result)].cases[0].at.column;});
    corrupt([&](auto& t){t.generics[static_cast<std::size_t>(result)].cases[0].payload[0].type.at.origin=SourceOrigin::input;});
    corrupt([&](auto& t){t.definitions[static_cast<std::size_t>(instance)].at.origin=SourceOrigin::input;});
    corrupt([&](auto& t){t.definitions[static_cast<std::size_t>(instance)].argument_locations[0].origin=SourceOrigin::core_outcomes;});
    corrupt([&](auto& t){t.definitions[static_cast<std::size_t>(instance)].instantiation_at.origin=SourceOrigin::core_outcomes;});
    corrupt([&](auto& t){++t.definitions[static_cast<std::size_t>(instance)].fields[0].at.offset;});
    corrupt([&](auto& t){t.generics[static_cast<std::size_t>(result)].at.origin=static_cast<SourceOrigin>(99);});
    corrupt([&](auto& t){t.generics[static_cast<std::size_t>(result)].cases[0].payload[0].name="changed";});
    corrupt([&](auto& t){++t.definitions[static_cast<std::size_t>(instance)].cases[0].payload[0].at.offset;});
    auto shadow=verified_storage("module Shadow\nintent \"User types keep their identities\"\ntype Option<T>=Some(T)|None\nfn main()->int ensures true {let x:Option<int>=Some(42)\nreturn match x {Some(n)=>n,None=>0}}\n")[0].products;check(shadow->generics.size()==1);check(!Type::from_shape(shadow,{Type::product,0,{}}).is_core_carrier("Option"));
    {auto copy=std::make_shared<NativeProductTable>(*shadow);copy->generics[0].at=native_core_definitions()[0].at;reject([&]{verify_native_product_table(copy);});}
    {auto copy=std::make_shared<NativeProductTable>(*shadow);copy->definitions[0].fields[0].at.origin=SourceOrigin::core_outcomes;reject([&]{verify_native_product_table(copy);});}
    reject([&]{auto source=std::string(core_outcomes_text);source.insert(source.find("Closed"),"x");auto copy=std::make_shared<NativeProductTable>();copy->generics=Parser(source,SourceOrigin::core_outcomes).core_schema();copy->sealed=true;verify_native_product_table(copy);});
    reject([&]{Parser("module other\nintent \"Wrong module\"\ntype Option<T>=Some(T)|None\ntype Result<T,E>=Ok(T)|Err(E)\n",SourceOrigin::core_outcomes).core_schema();});
    auto coexist=verified_storage(R"AI(module Coexist
intent "Core and input identities coexist"
type Result<T,E>=Ok(T)|Err(E)
type Option<T>=Some(T)|None
fn main()->int ensures true {
let user:Result<int,bool>=Result<int,bool>.Ok(20)
let standard:std.core.Result<int,bool>=std.core.Result<int,bool>.Ok(22)
let optional:std.core.Option<int>=Some(0)
return (match user {Ok(n)=>n,Err(_)=>0})+(match standard {Ok(n)=>n,Err(_)=>0})+(match optional {Some(n)=>n,None=>0})}
)AI")[0].products;
    int user_result=-1,core_result=-1,core_instance=-1;
    for(std::size_t i=0;i<coexist->generics.size();++i){auto& g=coexist->generics[i];if(g.name=="Result"){if(g.at.origin==SourceOrigin::input)user_result=static_cast<int>(i);else core_result=static_cast<int>(i);}}
    check(user_result>=0 && core_result>=0 && user_result!=core_result);verify_native_product_table(coexist);
    for(std::size_t i=0;i<coexist->definitions.size();++i){auto& d=coexist->definitions[i];if(d.generic==core_result){core_instance=static_cast<int>(i);check(d.name=="std.core.Result<int32,bool>");check(Type::from_shape(coexist,{Type::product,static_cast<int>(i),{}}).is_core_carrier("Result"));}if(d.generic==user_result){check(d.name=="Result<int32,bool>");check(!Type::from_shape(coexist,{Type::product,static_cast<int>(i),{}}).is_core_carrier("Result"));}}check(core_instance>=0);
    auto corrupt_coexist=[&](auto change){auto copy=std::make_shared<NativeProductTable>(*coexist);change(*copy);reject([&]{verify_native_product_table(copy);});};
    corrupt_coexist([&](auto& t){t.definitions[static_cast<std::size_t>(core_instance)].name="Result<int32,bool>";});
    corrupt_coexist([&](auto& t){t.definitions[static_cast<std::size_t>(core_instance)].generic=user_result;});
    corrupt_coexist([&](auto& t){t.generics[static_cast<std::size_t>(core_result)].parameters[0].at.origin=SourceOrigin::input;});
    corrupt_coexist([&](auto& t){++t.generics[static_cast<std::size_t>(core_result)].cases[0].payload[0].type.at.offset;});
    NativeProductBuilder reverse;
    auto& canonical_result=native_core_definitions()[1];int trusted=reverse.declare_generic(canonical_result.name,canonical_result.parameters,canonical_result.at);reverse.define_generic(trusted,{},canonical_result.cases,true);check(reverse.find_generic("Result")==trusted);
    int input=reverse.declare_generic("Result",{{"T",{}},{"E",{}}},{});reverse.define_generic(input,{}, {},false);check(reverse.find_generic("Result")==input && reverse.find_generic("std.core.Result")==trusted);verify_native_product_table(reverse.seal());
    NativeProductBuilder bounded;for(int i=0;i<128;++i){int id=bounded.declare_generic("G"+std::to_string(i),{{"T",{}}},{});bounded.define_generic(id,{}, {},false);}for(auto& g:native_core_definitions()){int id=bounded.declare_generic(g.name,g.parameters,g.at);bounded.define_generic(id,g.fields,g.cases,g.union_);}
    reject([&]{bounded.declare_generic("TooMany",{{"T",{}}},{});});reject([&]{bounded.declare_generic("Extra",{{"T",native_core_definitions()[0].at}},native_core_definitions()[0].at);});auto maximum=bounded.seal();verify_native_product_table(maximum);check(maximum->generics.size()==130);
    std::cout<<"{\"supplementalCoreOutcomeTestsOnly\":true,\"recomputedProof\":true,\"checks\":"<<checks<<",\"rejections\":"<<rejections<<",\"coreSourceSha256\":\""<<core_outcomes_sha256<<"\"}\n";return 0;
}
}
int main(){try{return aisharp::core_outcome_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
