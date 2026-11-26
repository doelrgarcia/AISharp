#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <functional>
#include <iostream>
namespace aisharp {
int union_ir_tests(){
    int checks=0,rejections=0;auto check=[&](bool ok){if(!ok)throw std::runtime_error("Native union/match assertion failed");++checks;};
    auto reject=[&](auto action,std::string code="AI3100"){bool bad=false;try{action();}catch(const Diagnostic& d){if(d.code!=code)throw;bad=true;++rejections;}check(bad);};
    NativeProductBuilder builder;int id=builder.declare("Status",{});builder.define_union(id,{{"A",{}},{"B",{}}});auto table=builder.seal();verify_native_product_table(table);auto type=Type::from_shape(table,{4,id,{}});check(type.is_union() && !type.is_product() && type.is_copy() && !type.needs_drop() && !type.contains_references() && type.definition().leaves==1 && type.definition().cases.size()==2);check(type.label()=="union<Status>");
    reject([&]{NativeProductBuilder x;int n=x.declare("Empty",{});x.define_union(n,{});},"AI1004");reject([&]{NativeProductBuilder x;int n=x.declare("Dup",{});x.define_union(n,{{"A",{}},{"A",{}}});},"AI2007");
    {NativeProductBuilder x;int n=x.declare("Limit",{});std::vector<NativeUnionCase> cases;for(int i=0;i<128;++i)cases.push_back({"C"+std::to_string(i),{}});x.define_union(n,cases);auto p=x.seal();verify_native_product_table(p);check(p->definitions[0].cases.size()==128);}
    reject([&]{NativeProductBuilder x;int n=x.declare("Limit",{});std::vector<NativeUnionCase> cases;for(int i=0;i<129;++i)cases.push_back({"C"+std::to_string(i),{}});x.define_union(n,cases);});
    for(int i=0;i<9;++i){auto forged=std::make_shared<NativeProductTable>(*table);auto& t=forged->definitions[0];if(i==0)t.union_=false;else if(i==1)t.fields.push_back({"bad",{0,-1,{}},{}});else if(i==2)t.cases.clear();else if(i==3)t.cases[1].name=t.cases[0].name;else if(i==4)t.copy=false;else if(i==5)t.drop=true;else if(i==6)t.has_references=true;else if(i==7)t.leaves=0;else forged->layout_order.clear();reject([&]{verify_native_product_table(forged);},i==3 ? "AI2007" : "AI3100");}
    const std::string source=R"AI(module UnionProof
intent "Independent closed union and match facts"
type Status=A | B
fn pick(s:Status)->int
ensures true
{var n=0
return match s {A when ((n=1)==0)=>n,A=>42,B=>0}}
fn main()->int
ensures true
{return pick(A)}
)AI";
    using Change=std::function<void(IrFunction&)>;std::vector<Change> changes;
    changes.push_back([](auto& f){for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind=="constant" && o.type.is_union()){o.literal=999;return;}});
    changes.push_back([](auto& f){f.matches[0].type=Type::boolean;});
    changes.push_back([](auto& f){f.matches[0].place=-1;});
    changes.push_back([](auto& f){f.matches[0].sources[0].guard_mode=3;});
    changes.push_back([](auto& f){f.matches[0].sources[0].pattern=f.matches[0].scrutinee;});
    changes.push_back([](auto& f){f.matches[0].sources.pop_back();});
    changes.push_back([](auto& f){f.matches[0].groups[0].selectors.clear();});
    changes.push_back([](auto& f){f.matches[0].groups[0].other=true;});
    changes.push_back([](auto& f){f.matches[0].groups[0].entry=-1;});
    changes.push_back([](auto& f){f.matches[0].dispatch[0].group=999;});
    changes.push_back([](auto& f){f.matches[0].dispatch[0].selector=999;});
    changes.push_back([](auto& f){f.matches[0].dispatch[0].condition=f.matches[0].dispatch[0].selector;});
    changes.push_back([](auto& f){f.matches[0].dispatch[0].next=f.matches[0].merge;});
    changes.push_back([](auto& f){for(auto& g:f.matches[0].groups)for(auto& a:g.arms)if(a.guard>=0){a.guard=-1;return;}});
    changes.push_back([](auto& f){for(auto& g:f.matches[0].groups)for(auto& a:g.arms)if(a.guard>=0){a.guard_entry=a.body;return;}});
    changes.push_back([](auto& f){for(auto& g:f.matches[0].groups)for(auto& a:g.arms)if(a.guard>=0){a.guard_end=a.body;return;}});
    changes.push_back([](auto& f){f.matches[0].groups[0].arms[0].source=999;});
    changes.push_back([](auto& f){f.matches[0].groups[0].arms[0].value=f.matches[0].scrutinee;});
    changes.push_back([](auto& f){f.matches[0].result=f.matches[0].scrutinee;});
    changes.push_back([](auto& f){f.matches.push_back(f.matches[0]);});
    changes.push_back([](auto& f){f.matches.clear();});
    changes.push_back([](auto& f){f.matches[0].fault=f.matches[0].merge;});
    changes.push_back([](auto& f){for(auto& g:f.matches[0].groups)if(g.arms.size()>1){std::swap(g.arms[0],g.arms[1]);return;}});
    changes.push_back([](auto& f){auto& m=f.matches[0];auto& b=f.blocks[static_cast<std::size_t>(m.merge)];for(auto it=b.ops.begin();it!=b.ops.end();++it)if(it->kind=="storage-dead" && it->place==m.place){b.ops.erase(it);return;}});
    changes.push_back([](auto& f){for(auto& g:f.matches[0].groups)for(auto& a:g.arms){for(auto& o:f.blocks[static_cast<std::size_t>(a.value_block)].ops)if(o.kind=="write" && o.place==f.matches[0].place){o.place=f.places[1].id;return;}}});
    changes.push_back([](auto& f){f.matches[0].sources[0].guard_mode=-2;});
    for(auto& change:changes){auto module=verified_storage(source);change(module[0]);reject([&]{StorageVerifier().run(module);});}
    auto module=verified_storage(source);auto expected=storage_json(module);for(auto& f:module){f.summary.returned={RegionPath{999,999}};f.input_demands[RegionPath{999,999}]={true,{{999,999}},{{999,999}}};for(auto& b:f.blocks){b.initialized_in={999};b.active_out={999};}}StorageVerifier().run(module);check(storage_json(module)==expected);
    std::cout<<"{\"checks\":"<<checks<<",\"rejections\":"<<rejections<<",\"recomputedProof\":true,\"supplementalUnionMatchTestsOnly\":true}\n";return 0;
}
}
int main(){try{return aisharp::union_ir_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
