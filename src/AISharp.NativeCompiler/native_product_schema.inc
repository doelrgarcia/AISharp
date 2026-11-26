// Nominal product schemas contain indices, never owning Type handles. Guarded
// recursive references therefore cannot form a C++ shared_ptr ownership cycle.
struct NativeTypeShape {
    int primitive=2,product=-1;
    std::vector<int> references;
};
struct NativeTypeExpression { enum Kind : int { concrete, parameter, application } kind=concrete; NativeTypeShape shape; int identity=-1; std::vector<NativeTypeExpression> arguments; Location at; };
struct NativeGenericParameter { std::string name; Location at; bool notnull=false, allows_ref=false, unmanaged=false, class_constraint=false; std::vector<std::pair<std::string,Location>> base_constraints; };
struct NativeGenericField { std::string name; NativeTypeExpression type; Location at; bool readonly=false, volatile_field=false, init_only=false, required_field=false, private_field=false, protected_field=false, synthesized=false; };
struct NativeProductField { std::string name; NativeTypeShape type; Location at; bool readonly=false, volatile_field=false, init_only=false, required_field=false, private_field=false, protected_field=false, synthesized=false; };
struct NativeEnumMember { std::string name; std::int64_t value; Location at; };
struct NativeFixedBuffer { std::string name; int first_field=0; int length=0; };
struct NativeEvent { std::string name; NativeTypeShape delegate_shape; int handler_field=-1; Location at; };
struct NativeUnionCase { std::string name;Location at; std::vector<NativeProductField> payload; std::vector<int> fields; };
struct NativeGenericCase { std::string name;Location at;std::vector<NativeGenericField> payload; };
struct NativeGenericDefinition { std::string name; Location at; std::vector<NativeGenericParameter> parameters; std::vector<NativeGenericField> fields; std::vector<NativeGenericCase> cases; bool defined=false,union_=false,ref_like=false; };
struct NativeProductDefinition {
    std::string name;Location at;std::vector<NativeProductField> fields;
    bool defined=false,copy=false,drop=false,has_references=false,union_=false,payload_=false,ref_like=false,managed_pointer=false,unmanaged_pointer=false,class_=false,abstract_class=false,interface_=false;int base_class=-1;std::vector<NativeUnionCase> cases;std::vector<NativeEvent> events;std::vector<NativeFixedBuffer> fixed_buffers;NativeTypeShape managed_result;std::vector<NativeTypeShape> managed_parameters;std::string managed_helper;std::vector<std::string> managed_targets;
    int generic=-1; std::vector<NativeTypeShape> arguments; std::vector<Location> argument_locations; Location instantiation_at;
    std::size_t leaves=0;std::vector<std::vector<int>> reference_paths;
    bool enum_=false;NativeTypeShape enum_underlying;std::vector<NativeEnumMember> enum_members;
};
struct NativeProductTable {
    std::vector<NativeProductDefinition> definitions;
    std::vector<NativeGenericDefinition> generics;
    std::vector<int> layout_order;
    bool sealed=false;
};
void verify_native_core_table(const NativeProductTable& table);
#include "native_generic_schema.inc"
class NativeProductBuilder {
    std::shared_ptr<NativeProductTable> table=std::make_shared<NativeProductTable>();
    std::map<std::string,int> names,generic_names,instances;std::map<std::string,NativeTypeShape> alias_names;bool finalized=false;std::size_t generic_work=0,substitution_depth=0;
    static bool identifier(const std::string& name){auto letter=[](char c){return (c>='a' && c<='z') || (c>='A' && c<='Z') || c=='_';};if(name.empty() || !letter(name.front()))return false;return std::all_of(name.begin()+1,name.end(),[&](char c){return letter(c) || (c>='0' && c<='9');});}
    void mutable_table(){if(table->sealed || finalized)fail("AI3100","Native product schemas are immutable after validation.",{});}
public:
    int declare(std::string name,Location at){
        mutable_table();if(!identifier(name))fail("AI3106","Invalid native product type name.",at);if(table->definitions.size()>=128)fail("AI3100","Native product declaration limit exceeded.",at);
        if(generic_names.contains(name)){Diagnostic d("AI3106","Duplicate native type declaration.",at);d.related.push_back({"previous-declaration",table->generics[static_cast<std::size_t>(generic_names.at(name))].at});throw d;}
        if(names.contains(name)){Diagnostic d("AI3106","Duplicate native product type declaration.",at);d.related.push_back({"previous-declaration",table->definitions[static_cast<std::size_t>(names.at(name))].at});throw d;}
        int id=static_cast<int>(table->definitions.size());names.emplace(name,id);NativeProductDefinition definition{};definition.name=std::move(name);definition.at=at;table->definitions.push_back(std::move(definition));return id;
    }
    std::size_t count()const{return table->definitions.size();}
    int find_generic(const std::string& name)const{auto p=generic_names.find(name);if(p!=generic_names.end())return p->second;if(names.contains(name))return -1;p=generic_names.find("std.core."+name);return p==generic_names.end() ? -1 : p->second;}
#include "native_generic_builder.inc"
    int find(const std::string& name)const{auto it=names.find(name);return it==names.end() ? -1 : it->second;}
    bool is_class(int id)const{return id>=0 && static_cast<std::size_t>(id)<table->definitions.size() && table->definitions[static_cast<std::size_t>(id)].class_;}
    int base_class(int id)const{return id>=0 && static_cast<std::size_t>(id)<table->definitions.size() ? table->definitions[static_cast<std::size_t>(id)].base_class : -1;}
    bool abstract_class(int id)const{return id>=0 && static_cast<std::size_t>(id)<table->definitions.size() && table->definitions[static_cast<std::size_t>(id)].abstract_class;}
    std::size_t definition_count()const{return table->definitions.size();}
    const NativeProductDefinition& definition(int id)const{return table->definitions[static_cast<std::size_t>(id)];}
    void define_alias(const std::string& name,NativeTypeShape shape,Location at){
        if(!identifier(name))fail("AI3106","Invalid native alias name.",at);
        if(names.contains(name) || generic_names.contains(name) || alias_names.contains(name))fail("AI3107","Native alias name conflicts with an existing type or alias.",at);
        alias_names.emplace(name,shape);
    }
    std::optional<NativeTypeShape> alias(const std::string& name)const{auto found=alias_names.find(name);if(found==alias_names.end())return std::nullopt;return found->second;}
    void define(int id,std::vector<NativeProductField> fields,bool ref_like=false,bool class_=false,int base_class=-1,bool abstract_class=false,bool interface_=false){
        mutable_table();if(id<0 || static_cast<std::size_t>(id)>=table->definitions.size())fail("AI3100","Invalid native product identity.",{});auto& product=table->definitions[static_cast<std::size_t>(id)];if(product.defined)fail("AI3100","Native product was defined twice.",product.at);if(fields.size()>128)fail("AI3100","Native product field limit exceeded.",product.at);for(auto& field:fields)if(field.type.primitive==4 && field.type.product>=0 && table->definitions[static_cast<std::size_t>(field.type.product)].ref_like && !ref_like)fail("AI3107","A ref-like value cannot be stored in a non-ref-like product.",field.at);product.defined=true;product.ref_like=ref_like;product.class_=class_;product.abstract_class=abstract_class;product.interface_=interface_;product.base_class=base_class;product.fields=std::move(fields);
    }
    void attach_fixed_buffer(int id,const NativeFixedBuffer& buffer){mutable_table();table->definitions[static_cast<std::size_t>(id)].fixed_buffers.push_back(buffer);}
    void attach_event(int id,const NativeEvent& event){mutable_table();if(id<0 || static_cast<std::size_t>(id)>=table->definitions.size())fail("AI3100","Invalid event product identity.",event.at);table->definitions[static_cast<std::size_t>(id)].events.push_back(event);}
    void define_managed(int id,const std::vector<NativeTypeShape>& parameters,const NativeTypeShape& result,const std::string& helper,const std::vector<std::string>& targets,bool unmanaged){
        mutable_table();if(id<0 || static_cast<std::size_t>(id)>=table->definitions.size())fail("AI3100","Invalid managed pointer identity.",{});
        auto& definition=table->definitions[static_cast<std::size_t>(id)];definition.managed_pointer=true;definition.unmanaged_pointer=unmanaged;definition.managed_parameters=parameters;definition.managed_result=result;definition.managed_helper=helper;definition.managed_targets=targets;
    }
    void define_enum(int id,NativeTypeShape underlying,std::vector<NativeEnumMember> members){
        mutable_table();if(id<0 || static_cast<std::size_t>(id)>=table->definitions.size())fail("AI3100","Invalid native enum identity.",{});auto& type=table->definitions[static_cast<std::size_t>(id)];if(type.defined)fail("AI3100","Native enum was defined twice.",type.at);
        if(underlying.primitive!=0 && underlying.primitive!=5 && underlying.primitive!=6 && underlying.primitive!=7 && underlying.primitive!=8 && underlying.primitive!=9 && underlying.primitive!=11 && underlying.primitive!=12)fail("AI3107","An enum requires an integral underlying type.",type.at);
        if(!underlying.references.empty() || underlying.product!=-1)fail("AI3107","An enum requires an integral underlying type.",type.at);
        std::int64_t minimum=0,maximum=0;switch(underlying.primitive){case 6:maximum=255;break;case 7:minimum=-128;maximum=127;break;case 8:minimum=-32768;maximum=32767;break;case 9:maximum=65535;break;case 11:maximum=4294967295LL;break;case 12:maximum=INT64_MAX;break;default:minimum=INT32_MIN;maximum=INT32_MAX;break;}
        std::map<std::string,Location> member_names;
        for(auto& member:members){if(!identifier(member.name))fail("AI3106","Invalid native enum member name.",member.at);auto [previous,added]=member_names.emplace(member.name,member.at);if(!added){Diagnostic d("AI3106","Duplicate native enum member.",member.at);d.related.push_back({"previous-declaration",previous->second});throw d;}if(member.value<minimum || (member.value>maximum && underlying.primitive!=12))fail("AI3107","An enum member value is outside its underlying type range.",member.at);}
        type.defined=true;type.enum_=true;type.enum_underlying=underlying;type.enum_members=std::move(members);
    }
    void define_union(int id,std::vector<NativeUnionCase> cases){
        mutable_table();if(id<0 || static_cast<std::size_t>(id)>=table->definitions.size())fail("AI3100","Invalid native union identity.",{});auto& type=table->definitions[static_cast<std::size_t>(id)];if(type.defined)fail("AI3100","Native nominal type was defined twice.",type.at);if(cases.empty())fail("AI1004","A native union requires at least one case.",type.at);if(cases.size()>128)fail("AI3100","Native union case limit exceeded.",type.at);std::map<std::string,Location> case_names;
        for(auto& c:cases){if(!identifier(c.name))fail("AI3106","Invalid native union case name.",c.at);auto [previous,added]=case_names.emplace(c.name,c.at);if(!added){Diagnostic d("AI2007","Duplicate native union case.",c.at);d.related.push_back({"previous-declaration",previous->second});throw d;}}
        type.defined=true;type.union_=true;type.cases=std::move(cases);
        for(auto& c:type.cases){c.fields.clear();if(c.payload.size()>128)fail("AI3100","Native case payload parameter limit exceeded.",c.at);if(!c.payload.empty())type.payload_=true;for(std::size_t i=0;i<c.payload.size();++i){auto& item=c.payload[i];native_shape_check(item.type,*table,item.at);if(item.type.primitive==2){c.fields.push_back(-1);continue;}if(type.fields.size()>=128)fail("AI3100","Native union payload field limit exceeded.",item.at);c.fields.push_back(static_cast<int>(type.fields.size()));type.fields.push_back({"case"+std::to_string(&c-type.cases.data())+"_"+std::to_string(i),item.type,item.at});}}
    }
    std::shared_ptr<const NativeProductTable> seal(){
        mutable_table();materialize_instances();for(auto& g:table->generics)native_validate_generic(g,*table,generic_work);finalized=true;std::size_t work=0;auto charge=[&](std::size_t n=1){if(n>1000000-work)fail("AI3100","Native product schema analysis work limit exceeded.",{});work+=n;};
        for(auto& product:table->definitions){
            if(product.enum_ && (product.union_ || !product.fields.empty() || !product.cases.empty()))fail("AI3100","Invalid native enum schema.",product.at);
            if(product.union_ && ((!product.payload_ && !product.fields.empty()) || product.cases.empty() || product.cases.size()>128))fail("AI3100","Invalid native closed union schema.",product.at);if(!product.union_ && !product.cases.empty() && !product.enum_)fail("AI3100","Product schema carries union cases.",product.at);
            if(!product.defined)fail("AI3100","Native product declaration lacks its field schema.",product.at);std::map<std::string,Location> fields;
            for(auto& field:product.fields){charge(1+field.type.references.size());if(!identifier(field.name))fail("AI3106","Invalid native product field name.",field.at);auto [it,inserted]=fields.emplace(field.name,field.at);if(!inserted){Diagnostic d("AI3106","Duplicate native product field.",field.at);d.related.push_back({"previous-declaration",it->second});throw d;}
                auto& type=field.type;if(type.primitive<0 || type.primitive>native_primitive_limit || type.references.size()>128 || !std::all_of(type.references.begin(),type.references.end(),[](int mode){return mode==0 || mode==1;}))fail("AI3100","Invalid native product field type shape.",field.at);
                if(type.primitive==2)fail("AI3110","Unit cannot be stored in a native product field.",field.at);
                if(type.primitive==4 || type.primitive==21){if(type.product<0 || static_cast<std::size_t>(type.product)>=table->definitions.size())fail("AI3100","Unbound native product field type.",field.at);}else if(type.product!=-1)fail("AI3100","Primitive field carries a product identity.",field.at);
            }
        }
        std::vector<int> state(table->definitions.size(),0);
        auto visit=[&](auto&& self,int id,std::size_t depth)->void{
            charge();if(depth>128)fail("AI3100","Native product value nesting limit exceeded.",{});auto& mark=state[static_cast<std::size_t>(id)];if(mark==2)return;if(mark==1)fail("AI3118","Native product has a recursive by-value layout.",table->definitions[static_cast<std::size_t>(id)].at);mark=1;auto& product=table->definitions[static_cast<std::size_t>(id)];
            if(product.enum_){product.copy=true;product.drop=false;product.has_references=false;product.leaves=1;mark=2;table->layout_order.push_back(id);return;}
            product.copy=true;product.leaves=product.fields.empty() ? 1 : 0;
            for(auto& field:product.fields){charge(1+field.type.references.size());auto& type=field.type;bool copy,drop,refs;std::size_t leaves=1;
                if(!type.references.empty()){copy=type.references.front()==0;drop=false;refs=true;product.reference_paths.push_back({static_cast<int>(&field-product.fields.data())});}
                else if(type.primitive==4){self(self,type.product,depth+1);auto& child=table->definitions[static_cast<std::size_t>(type.product)];copy=child.copy;drop=child.drop;refs=child.has_references;leaves=child.leaves;for(auto& path:child.reference_paths){charge(1+path.size());auto nested=path;nested.insert(nested.begin(),static_cast<int>(&field-product.fields.data()));product.reference_paths.push_back(std::move(nested));}}
                else{copy=type.primitive!=3;drop=type.primitive==3;refs=false;}
                if(leaves>65536-product.leaves)fail("AI3100","Native product expanded field limit exceeded.",field.at);product.leaves+=leaves;product.copy=product.copy && copy;product.drop=product.drop || drop;product.has_references=product.has_references || refs;
            }
            mark=2;table->layout_order.push_back(id);
        };
        for(std::size_t i=0;i<table->definitions.size();++i)visit(visit,static_cast<int>(i),1);table->sealed=true;return table;
    }
};

void verify_native_product_table(const std::shared_ptr<const NativeProductTable>& table){
    if(!table || !table->sealed || table->definitions.size()>128)fail("AI3100","Unverified native product schema table.",{});
    verify_native_generic_table(*table);
    verify_native_core_table(*table);
    std::size_t text=0;NativeProductBuilder expected;
    for(auto& product:table->definitions){if(product.name.size()>1024*1024-text)fail("AI3100","Native product schema text limit exceeded.",product.at);text+=product.name.size();expected.declare("verified_"+std::to_string(expected.count()),product.at);}
    for(std::size_t id=0;id<table->definitions.size();++id){auto& product=table->definitions[id];if(!product.defined || product.fields.size()>128 || (product.union_ && ((!product.payload_ && !product.fields.empty()) || product.cases.empty() || product.cases.size()>128)) || (!product.union_ && !product.cases.empty()))fail("AI3100","Invalid native product field schema.",product.at);
        for(auto& field:product.fields){if(field.name.size()>1024*1024-text || field.type.references.size()>128)fail("AI3100","Native product schema size limit exceeded.",field.at);text+=field.name.size();}
        for(auto& c:product.cases){if(c.name.size()>1024*1024-text)fail("AI3100","Native union schema text limit exceeded.",c.at);text+=c.name.size();}
        if(product.enum_)expected.define_enum(static_cast<int>(id),product.enum_underlying,product.enum_members);
        else if(product.union_){auto cases=product.cases;for(auto& c:cases)c.fields.clear();expected.define_union(static_cast<int>(id),std::move(cases));}else expected.define(static_cast<int>(id),product.fields);
    }
    auto proof=expected.seal();if(proof->layout_order!=table->layout_order)fail("AI3100","Native product layout proof differs from its schema.",{});
    for(std::size_t id=0;id<table->definitions.size();++id){auto& a=proof->definitions[id];auto& b=table->definitions[id];if(a.payload_!=b.payload_ || a.fields.size()!=b.fields.size())fail("AI3100","Native union payload layout differs from its case schema.",b.at);for(std::size_t i=0;i<a.fields.size();++i)if(a.fields[i].name!=b.fields[i].name || a.fields[i].type.primitive!=b.fields[i].type.primitive || a.fields[i].type.product!=b.fields[i].type.product || a.fields[i].type.references!=b.fields[i].type.references)fail("AI3100","Native payload field differs from its case schema.",b.fields[i].at);for(std::size_t c=0;c<a.cases.size();++c)if(a.cases[c].fields!=b.cases[c].fields)fail("AI3100","Native case projection map differs from its schema.",b.cases[c].at);if(a.copy!=b.copy || a.drop!=b.drop || a.has_references!=b.has_references || a.leaves!=b.leaves || a.reference_paths!=b.reference_paths)fail("AI3100","Native product ownership proof differs from its field schema.",b.at);}
}
