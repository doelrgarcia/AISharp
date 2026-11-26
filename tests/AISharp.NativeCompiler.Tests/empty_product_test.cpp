#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
namespace aisharp {
int empty_product_tests(){
    int checks=0,rejections=0;auto check=[&](bool ok){if(!ok)throw std::runtime_error("Empty product proof assertion failed");++checks;};
    auto reject=[&](auto action,std::string code="AI3100"){bool bad=false;try{action();}catch(const Diagnostic& d){if(d.code!=code)throw;bad=true;++rejections;}check(bad);};
    NativeProductBuilder b;int empty=b.declare("Empty",{});b.define(empty,{});auto table=b.seal();verify_native_product_table(table);auto t=Type::from_shape(table,{4,empty,{}});check(t.is_copy() && !t.needs_drop() && !t.contains_references() && t.definition().leaves==1 && t.definition().fields.empty());
    for(int i=0;i<7;++i){auto forged=std::make_shared<NativeProductTable>(*table);auto& e=forged->definitions[0];if(i==0)e.leaves=0;else if(i==1)e.copy=false;else if(i==2)e.drop=true;else if(i==3)e.has_references=true;else if(i==4)e.reference_paths={{0}};else if(i==5)forged->layout_order.clear();else e.fields.push_back({"bad",{2,-1,{}},{}});reject([&]{verify_native_product_table(forged);},i==6 ? "AI3110" : "AI3100");}
    auto expansion=[&](int last){NativeProductBuilder x;int previous=x.declare("E",{});x.define(previous,{});for(int level=0;level<3;++level){int id=x.declare("T"+std::to_string(level),{});std::vector<NativeProductField> fields;for(int i=0;i<(level==2 ? last : 128);++i)fields.push_back({"f"+std::to_string(i),{4,previous,{}},{}});x.define(id,std::move(fields));previous=id;}return x.seal();};auto edge=expansion(4);check(edge->definitions.back().leaves==65536);reject([&]{expansion(5);});
    {NativeProductBuilder x;int previous=x.declare("E",{});x.define(previous,{});for(int i=1;i<128;++i){int id=x.declare("T"+std::to_string(i),{});x.define(id,{{"e",{4,previous,{}},{}}});previous=id;}auto deep=x.seal();verify_native_product_table(deep);check(deep->definitions.size()==128 && deep->definitions.back().leaves==1);reject([&]{x.declare("Extra",{});});}
    const std::string source=R"AI(module EmptyProof
intent "Recompute empty value obligations"
type Empty={}
type Pair={a:Empty,b:Empty}
fn roundtrip(p:Empty)->Empty
ensures true
{return p}
fn main()->int
ensures true
{var p:Pair
p.a=Empty()
p.b=Empty()
let q=p
let e=roundtrip(q.a)
return 42}
)AI";
    auto make=[&]{return verified_storage(source);};auto original=make();auto expected=storage_json(original);
    {auto f=make();bool changed=false;for(auto& b1:f.back().blocks)for(auto& o:b1.ops)if(!changed && o.kind=="aggregate-pack" && o.operands.empty()){o.operands={o.id};changed=true;}check(changed);reject([&]{StorageVerifier().run(f);});}
    {auto f=make();bool changed=false;for(auto& b1:f.back().blocks)for(auto& o:b1.ops)if(!changed && o.kind=="aggregate-pack" && o.operands.empty()){o.type=Type::unit;changed=true;}check(changed);reject([&]{StorageVerifier().run(f);});}
    {auto f=make();auto& places=f.back().places;auto p=std::find_if(places.begin(),places.end(),[](auto& p){return p.type.is_product() && p.type.definition().fields.empty();});auto bad=*p;bad.id=999;bad.parent=p->id;bad.field=0;places.push_back(bad);reject([&]{StorageVerifier().run(f);});}
    {auto f=make();bool removed=false;for(auto& b1:f.back().blocks)for(auto i=b1.ops.begin();i!=b1.ops.end();++i)if(i->kind=="write" && !removed){b1.ops.erase(i);removed=true;break;}check(removed);reject([&]{StorageVerifier().run(f);},"AI3102");}
    {auto f=make();for(auto& function:f){for(auto& block:function.blocks){block.initialized_in={999};block.maybe_initialized_out={999};}function.summary.returned={RegionPath{999,999}};function.loans.push_back({});}StorageVerifier().run(f);check(storage_json(f)==expected);}
    std::cout<<"{\"checks\":"<<checks<<",\"rejections\":"<<rejections<<",\"recomputedProof\":true,\"supplementalEmptyProductTestsOnly\":true}\n";return 0;
}
}
int main(){try{return aisharp::empty_product_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
