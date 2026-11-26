// Native variant layout and cleanup use only the selected alternative.
    void emit_schema(const std::shared_ptr<const NativeProductTable>& table,int id){
        auto type=Type::from_shape(table,{Type::product,id,{}});auto& d=type.definition();auto name=storage_ctype(type);
        if(type.is_union() && !type.is_payload_union()){out<<"_Static_assert(sizeof("<<name<<")==4, \"Native closed union tag layout\");\n";return;}
        out<<"struct "<<name<<" {\n";
        if(type.is_payload_union()){
            out<<"int32_t tag;\n";if(type.is_core_carrier("Result") || type.is_core_carrier("Option"))out<<"ai_cause cause;\n";out<<"union {\n";
            for(std::size_t c=0;c<d.cases.size();++c){out<<"struct {\n";bool stored=false;for(int field:d.cases[c].fields)if(field>=0){stored=true;out<<storage_ctype(type.field_type(static_cast<std::size_t>(field)))<<" m"<<field<<";\n";}if(!stored)out<<"unsigned char ai_empty_cell;\n";out<<"} c"<<c<<";\n";}out<<"} data;\n";
        }else{if(d.fields.empty())out<<"unsigned char ai_empty_cell;\n";for(std::size_t i=0;i<d.fields.size();++i)out<<(d.fields[i].volatile_field ? "volatile " : "")<<storage_ctype(type.field_type(i))<<" m"<<i<<";\n";}
        out<<"};\n";
        if(type.is_product() && d.fields.empty())out<<"_Static_assert(sizeof("<<name<<")==1, \"Native empty product cell layout\");\n";
        out<<name<<' '<<name<<"_empty(void) { "<<name<<" value={0}; ";if(type.is_payload_union())out<<"value.tag=-1; ";
        // Nested variants also need the moved/uninitialized sentinel, not case zero.
        if(type.is_product())for(std::size_t i=0;i<d.fields.size();++i)if(type.field_type(i).is_aggregate())out<<"value.m"<<i<<" = "<<empty(type.field_type(i))<<"; ";
        out<<"return value; }\nvoid "<<name<<"_drop("<<name<<"* value) { (void)value;\n";
        if(type.is_payload_union()){
            out<<"switch(value->tag) {\ncase -1: break;\n";
            for(std::size_t c=0;c<d.cases.size();++c){out<<"case "<<c<<":\n";for(int field:d.cases[c].fields)if(field>=0)drop("value->data.c"+std::to_string(c)+".m"+std::to_string(field),type.field_type(static_cast<std::size_t>(field)));out<<"break;\n";}
            out<<"default: ai_fail(\"AISharp.InvalidVariantException\");\n}\nvalue->tag=-1;\n";
        }else for(std::size_t i=0;i<d.fields.size();++i)drop("value->m"+std::to_string(i),type.field_type(i));
        out<<"}\n";
    }
