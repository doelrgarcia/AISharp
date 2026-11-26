#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <functional>
#include <iostream>
namespace aisharp {
int generic_schema_tests(){
    int checks=0,rejections=0;auto check=[&](bool ok){if(!ok)throw std::runtime_error("Generic schema assertion failed");++checks;};auto reject=[&](auto action,const std::string& code="AI3100"){bool caught=false;try{action();}catch(const Diagnostic& d){if(d.code!=code)throw;caught=true;++rejections;}check(caught);};
    const std::string source=R"AI(module GenericSchema
intent "Independent concrete substitutions"
type Box<T>={value:T}
type Holder<T>={inner:Box<T>}
type Tag<T>=A|B
fn relay(x:Box<&int>)->Box<&int>
ensures true
{return x}
fn main()->int
ensures true
{let t:Tag<int>=A
let p=Holder<int>(Box<int>(42))
let b=Box<owned buffer>(buffer(1))
return p.inner.value+bufferLength(b.value)-1}
)AI";
    auto module=verified_storage(source);auto table=module[0].products;verify_native_product_table(table);check(table->generics.size()==3 && table->definitions.size()==5);
    int integer=-1,owner=-1,reference=-1,tag=-1;for(std::size_t i=0;i<table->definitions.size();++i){auto& d=table->definitions[i];if(d.generic==0 && d.arguments[0].primitive==0){if(d.arguments[0].references.empty())integer=static_cast<int>(i);else reference=static_cast<int>(i);}if(d.generic==0 && d.arguments[0].primitive==3)owner=static_cast<int>(i);if(d.generic==2)tag=static_cast<int>(i);}check(integer>=0 && owner>=0 && reference>=0 && tag>=0);check(table->definitions[static_cast<std::size_t>(integer)].copy && !table->definitions[static_cast<std::size_t>(integer)].drop);check(!table->definitions[static_cast<std::size_t>(owner)].copy && table->definitions[static_cast<std::size_t>(owner)].drop);check(table->definitions[static_cast<std::size_t>(reference)].has_references);
    using Change=std::function<void(NativeProductTable&)>;std::vector<std::pair<Change,std::string>> changes;
    auto mutation=[&](Change action,std::string code="AI3100"){changes.emplace_back(std::move(action),std::move(code));};
    mutation([](auto& t){t.generics[0].parameters[0].name="bad!";},"AI3106");
    mutation([](auto& t){t.generics[0].parameters.clear();});
    mutation([](auto& t){t.generics[0].parameters.push_back(t.generics[0].parameters[0]);},"AI3106");
    mutation([](auto& t){t.generics[0].fields[0].type.identity=999;});
    mutation([](auto& t){t.generics[1].fields[0].type.arguments.clear();},"AI3107");
    mutation([](auto& t){t.generics[0].fields[0].type.kind=static_cast<NativeTypeExpression::Kind>(999);});
    mutation([](auto& t){t.generics[0].fields[0].type.shape.references={2};});
    mutation([](auto& t){t.generics[0].union_=true;});
    mutation([](auto& t){t.generics[2].cases[1].name=t.generics[2].cases[0].name;},"AI2007");
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(integer)].generic=999;});
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(integer)].arguments.clear();});
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(integer)].name="Box<fake>";});
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(integer)].fields[0].type.primitive=1;});
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(integer)].arguments[0].primitive=999;});
    mutation([&](auto& t){t.definitions.push_back(t.definitions[static_cast<std::size_t>(integer)]);});
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(integer)].generic=-1;});
    mutation([&](auto& t){auto& d=t.definitions[static_cast<std::size_t>(integer)];d.generic=-1;d.arguments.clear();d.argument_locations.clear();},"AI3106");
    mutation([](auto& t){t.generics.clear();});
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(tag)].cases[0].name="Wrong";});
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(owner)].drop=false;});
    mutation([](auto& t){t.generics.resize(129);});
    mutation([](auto& t){auto& e=t.generics[0].fields[0].type;e.kind=NativeTypeExpression::concrete;e.identity=-1;e.shape.primitive=4;e.shape.product=999;});
    mutation([](auto& t){t.generics[0].fields[0].type.shape.product=0;});
    mutation([&](auto& t){auto& a=t.definitions[static_cast<std::size_t>(reference)].arguments[0];a.primitive=2;});
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(reference)].has_references=false;});
    mutation([](auto& t){t.generics[0].fields.push_back(t.generics[0].fields[0]);},"AI3106");
    mutation([&](auto& t){t.definitions[static_cast<std::size_t>(integer)].argument_locations.clear();});
    for(auto& [change,code]:changes){auto forged=std::make_shared<NativeProductTable>(*table);change(*forged);reject([&]{verify_native_product_table(forged);},code);}
    auto expected=storage_json(module);for(auto& f:module){f.summary.returned={RegionPath{999,999}};f.input_demands[RegionPath{999,999}]={true,{{999,999}},{{999,999}}};for(auto& b:f.blocks){b.initialized_in={999};b.active_out={999};}}StorageVerifier().run(module);check(storage_json(module)==expected);
    NativeTypeExpression parameter{};parameter.kind=NativeTypeExpression::parameter;parameter.identity=0;
    {NativeProductBuilder b;int g=b.declare_generic("Box",{{"T",{}}},{});b.define_generic(g,{{"value",parameter,{}}},{},false);int a=b.instantiate(g,{{0,-1,{}}},{});check(b.instantiate(g,{{0,-1,{}}},{})==a);reject([&]{b.declare_generic("Other",{{"U",{}}},{});});auto sealed=b.seal();verify_native_product_table(sealed);}
    reject([&]{NativeProductBuilder b;int g=b.declare_generic("Box",{{"T",{}}},{});b.define_generic(g,{{"value",parameter,{}}},{},false);b.instantiate(g,{{2,-1,{}}},{});b.seal();},"AI3110");
    reject([&]{NativeProductBuilder b;int g=b.declare_generic("Grow",{{"T",{}}},{});NativeTypeExpression nested{};nested.kind=NativeTypeExpression::application;nested.identity=g;nested.arguments={parameter};NativeTypeExpression outer{};outer.kind=NativeTypeExpression::application;outer.identity=g;outer.arguments={nested};outer.shape.references={0};b.define_generic(g,{{"next",outer,{}}},{},false);b.instantiate(g,{{0,-1,{}}},{});b.seal();});
    {NativeProductBuilder b;int named=b.declare("int32",{});b.define_union(named,{{"A",{}}});int g=b.declare_generic("Box",{{"T",{}}},{});b.define_generic(g,{{"value",parameter,{}}},{},false);int primitive=b.instantiate(g,{{0,-1,{}}},{}),nominal=b.instantiate(g,{{4,named,{}}},{});auto sealed=b.seal();verify_native_product_table(sealed);check(primitive!=nominal && sealed->definitions[static_cast<std::size_t>(primitive)].name!=sealed->definitions[static_cast<std::size_t>(nominal)].name);}
    std::weak_ptr<const NativeProductTable> weak;{NativeProductBuilder b;int g=b.declare_generic("Node",{{"T",{}}},{});NativeTypeExpression next{};next.kind=NativeTypeExpression::application;next.identity=g;next.arguments={parameter};next.shape.references={0};b.define_generic(g,{{"next",next,{}}},{},false);int id=b.instantiate(g,{{0,-1,{}}},{});auto sealed=b.seal();verify_native_product_table(sealed);weak=sealed;auto type=Type::from_shape(sealed,{4,id,{}});check(type.field_type(0).pointee()==type);}check(weak.expired());
    std::cout<<"{\"checks\":"<<checks<<",\"rejections\":"<<rejections<<",\"recomputedProof\":true,\"supplementalGenericSchemaTestsOnly\":true}\n";return 0;
}
}
int main(){try{return aisharp::generic_schema_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
