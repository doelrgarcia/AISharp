#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
namespace aisharp {
int product_schema_tests(){
    int checks=0,rejections=0;auto check=[&](bool value){if(!value)throw std::runtime_error("Native product schema assertion failed");++checks;};
    auto rejects=[&](auto&& action,const std::string& code="AI3100"){bool failed=false;try{action();}catch(const Diagnostic& d){if(d.code!=code)throw;failed=true;++rejections;}check(failed);};
    NativeProductBuilder builder;int owned=builder.declare("Owned",{}),copy=builder.declare("Copy",{}),node=builder.declare("Node",{}),exclusive=builder.declare("Exclusive",{}),nested=builder.declare("Nested",{});
    builder.define(owned,{{"first",{3,-1,{}},{}},{"last",{0,-1,{}},{}}});builder.define(copy,{{"first",{0,-1,{}},{}},{"last",{1,-1,{}},{}}});builder.define(node,{{"next",{4,node,{0}},{}},{"value",{0,-1,{}},{}}});builder.define(exclusive,{{"handle",{4,owned,{1}},{}}});builder.define(nested,{{"owned",{4,owned,{}},{}},{"copy",{4,copy,{}},{}}});auto table=builder.seal();
    auto type=[&](int id){return Type::from_shape(table,{4,id,{}});};auto a=type(owned),b=type(copy),n=type(node),m=type(exclusive),outer=type(nested);
    check(a.valid() && !a.is_copy() && a.needs_drop() && !a.contains_references());check(b.valid() && b.is_copy() && !b.needs_drop() && !b.contains_references());check(n.valid() && n.is_copy() && !n.needs_drop() && n.contains_references());check(m.valid() && !m.is_copy() && !m.needs_drop() && m.contains_references());check(outer.valid() && !outer.is_copy() && outer.needs_drop() && !outer.contains_references());
    check(a!=b && a==type(owned));check(a.field_type(0)==Type::buffer && a.field_type(1)==Type::integer);check(outer.field_type(0)==a && outer.field_type(1)==b);check(n.field_type(0)==Type::reference(n,Type::Borrow::shared));check(Type::reference(a,Type::Borrow::shared).is_copy());check(!Type::reference(a,Type::Borrow::exclusive).is_copy());check(!Type::reference(a,Type::Borrow::shared).needs_drop());check(a.label()=="product<Owned>");check(table->definitions[static_cast<std::size_t>(nested)].leaves==4);check(table->layout_order.size()==5);
    rejects([&]{builder.declare("Late",{});});rejects([&]{builder.define(copy,{});});rejects([&]{builder.seal();});rejects([&]{a.field_type(2);});rejects([&]{Type::reference(a,Type::Borrow::shared).field_type(0);});rejects([&]{Type::from_shape(table,{4,999,{}});});rejects([&]{Type::from_shape(table,{0,copy,{}});});rejects([&]{Type::from_shape(table,{0,-1,{99}});});
    rejects([&]{NativeProductBuilder x;int id=x.declare("Loop",{});x.define(id,{{"self",{4,id,{}},{}}});x.seal();},"AI3118");
    rejects([&]{NativeProductBuilder x;int a1=x.declare("A",{}),b1=x.declare("B",{});x.define(a1,{{"b",{4,b1,{}},{}}});x.define(b1,{{"a",{4,a1,{}},{}}});x.seal();},"AI3118");
    rejects([&]{NativeProductBuilder x;int id=x.declare("Unit",{});x.define(id,{{"unit",{2,-1,{}},{}}});x.seal();},"AI3110");
    rejects([&]{NativeProductBuilder x;int id=x.declare("Dup",{});x.define(id,{{"x",{0,-1,{}},{}},{"x",{1,-1,{}},{}}});x.seal();},"AI3106");
    rejects([&]{NativeProductBuilder x;x.declare("Dup",{});x.declare("Dup",{});},"AI3106");
    {NativeProductBuilder x;int id=x.declare("Empty",{});x.define(id,{});auto empty=x.seal();verify_native_product_table(empty);check(empty->definitions[0].copy && !empty->definitions[0].drop && !empty->definitions[0].has_references && empty->definitions[0].leaves==1 && empty->definitions[0].fields.empty());}
    rejects([&]{NativeProductBuilder x;x.declare("Missing",{});x.seal();});
    rejects([&]{NativeProductBuilder x;int id=x.declare("Foreign",{});x.define(id,{{"foreign",{4,999,{}},{}}});x.seal();});
    rejects([&]{NativeProductBuilder x;int previous=-1;for(int i=0;i<5;++i){int id=x.declare("T"+std::to_string(i),{});std::vector<NativeProductField> fields;for(int j=0;j<128;++j)fields.push_back({"f"+std::to_string(j),previous<0 ? NativeTypeShape{0,-1,{}} : NativeTypeShape{4,previous,{}},{}});x.define(id,std::move(fields));previous=id;}x.seal();});
    verify_native_product_table(table);
    for(int mutation=0;mutation<6;++mutation){auto forged=std::make_shared<NativeProductTable>(*table);if(mutation==0)forged->definitions[0].copy=true;else if(mutation==1)forged->definitions[0].drop=false;else if(mutation==2)forged->definitions[2].has_references=false;else if(mutation==3)forged->definitions[0].leaves=999;else if(mutation==4)forged->layout_order.clear();else forged->definitions[2].reference_paths.clear();rejects([&]{verify_native_product_table(forged);});}
    std::weak_ptr<const NativeProductTable> weak;{NativeProductBuilder x;int id=x.declare("Recursive",{});x.define(id,{{"next",{4,id,{0}},{}}});auto schema=x.seal();weak=schema;auto recursive=Type::from_shape(schema,{4,id,{}});check(recursive.field_type(0).pointee()==recursive);}check(weak.expired());
    std::cout<<"{\"checks\":"<<checks<<",\"rejections\":"<<rejections<<",\"supplementalSchemaTestsOnly\":true}\n";return 0;
}
}
int main(){try{return aisharp::product_schema_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
