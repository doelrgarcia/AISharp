// Parse the actual versioned core document with the same lexer/type grammar.
// Core source identities are never assigned from an input type's spelling.
const std::vector<NativeGenericDefinition>& native_core_definitions(){
    static const auto declarations=[](){
        auto result=Parser(core_outcomes_text,SourceOrigin::core_outcomes).core_schema();
        for(std::size_t i=0;i<result.size();++i){auto& g=result[i];if(!g.union_ || !g.fields.empty() || g.parameters.size()!=i+1 || g.cases.size()!=2 || g.cases[0].name!=(i==0 ? "Some" : "Ok") || g.cases[1].name!=(i==0 ? "None" : "Err"))fail("AI3100","Invalid standard outcome contract.",{});
            for(std::size_t c=0;c<2;++c){auto& payload=g.cases[c].payload;if(payload.size()!=(i==0 && c==1 ? 0u : 1u))fail("AI3100","Invalid standard outcome payload contract.",{});if(!payload.empty()){auto& e=payload[0].type;if(e.kind!=NativeTypeExpression::parameter || e.identity!=static_cast<int>(c) || !e.shape.references.empty() || !e.arguments.empty())fail("AI3100","Invalid standard outcome parameter contract.",{});}}
        }return result;
    }();return declarations;
}

void verify_native_core_table(const NativeProductTable& table){
    auto bad=[](){fail("AI3100","Native declaration source identity differs from its document.",{});};std::size_t work=0;
    auto charge=[&](){if(++work>16000000)fail("AI3100","Native source identity work limit exceeded.",{});};
    auto input=[&](Location at){charge();if(at.origin!=SourceOrigin::input)bad();};
    auto same_expr=[&](auto&& self,const NativeTypeExpression& a,const NativeTypeExpression& b)->bool{charge();if(a.kind!=b.kind || a.identity!=b.identity || a.shape.primitive!=b.shape.primitive || a.shape.product!=b.shape.product || a.shape.references!=b.shape.references || a.at!=b.at || a.arguments.size()!=b.arguments.size())return false;for(std::size_t i=0;i<a.arguments.size();++i)if(!self(self,a.arguments[i],b.arguments[i]))return false;return true;};
    auto input_expr=[&](auto&& self,const NativeTypeExpression& e)->void{input(e.at);for(auto& a:e.arguments)self(self,a);};
    for(auto& g:table.generics){charge();if(g.at.origin==SourceOrigin::core_outcomes){auto& expected=native_core_definitions();auto found=std::find_if(expected.begin(),expected.end(),[&](auto& e){return e.name==g.name;});if(found==expected.end())bad();auto& e=*found;if(!g.defined || g.at!=e.at || g.union_!=e.union_ || g.parameters.size()!=e.parameters.size() || !g.fields.empty() || g.cases.size()!=e.cases.size())bad();for(std::size_t i=0;i<g.parameters.size();++i)if(g.parameters[i].name!=e.parameters[i].name || g.parameters[i].at!=e.parameters[i].at)bad();for(std::size_t i=0;i<g.cases.size();++i){auto& a=g.cases[i];auto& b=e.cases[i];if(a.name!=b.name || a.at!=b.at || a.payload.size()!=b.payload.size())bad();for(std::size_t k=0;k<a.payload.size();++k)if(a.payload[k].name!=b.payload[k].name || a.payload[k].at!=b.payload[k].at || !same_expr(same_expr,a.payload[k].type,b.payload[k].type))bad();}}
        else{input(g.at);for(auto& p:g.parameters)input(p.at);for(auto& f:g.fields){input(f.at);input_expr(input_expr,f.type);}for(auto& c:g.cases){input(c.at);for(auto& f:c.payload){input(f.at);input_expr(input_expr,f.type);}}}}
    for(auto& d:table.definitions){charge();input(d.instantiation_at);for(auto& at:d.argument_locations)input(at);if(d.generic<0){input(d.at);for(auto& f:d.fields)input(f.at);for(auto& c:d.cases){input(c.at);for(auto& f:c.payload)input(f.at);}continue;}
        auto& g=table.generics.at(static_cast<std::size_t>(d.generic));if(d.at!=g.at || d.cases.size()!=g.cases.size() || (!g.union_ && d.fields.size()!=g.fields.size()))bad();for(std::size_t i=0;i<g.fields.size();++i)if(d.fields[i].at!=g.fields[i].at)bad();for(std::size_t i=0;i<g.cases.size();++i){auto& a=d.cases[i];auto& b=g.cases[i];if(a.at!=b.at || a.payload.size()!=b.payload.size() || a.fields.size()!=a.payload.size())bad();for(std::size_t k=0;k<a.payload.size();++k){if(a.payload[k].at!=b.payload[k].at)bad();int field=a.fields[k];if(field>=0 && (static_cast<std::size_t>(field)>=d.fields.size() || d.fields[static_cast<std::size_t>(field)].at!=b.payload[k].at))bad();}}
    }
}
