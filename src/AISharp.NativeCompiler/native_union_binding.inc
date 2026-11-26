    Type bind_case_value(Expr& e,Constant value){e.type=value.type;e.value=value.number;if(value.type.is_payload_union()){e.kind=Expr::variant;e.field_index=static_cast<int>(value.number);e.constant.reset();}else{e.kind=Expr::literal;e.constant=value;}return e.type;}
    Type construct_variant(Expr& e,Type type,int index){
        auto& c=type.definition().cases[static_cast<std::size_t>(index)];if(e.children.size()!=c.payload.size()){Diagnostic d("AI3107","Case constructor argument count differs from its payload.",e.token.at);d.related.push_back({"case-declaration",c.at});throw d;}for(std::size_t i=0;i<c.payload.size();++i){auto expected=Type::from_shape(products,c.payload[i].type,c.payload[i].at);require_value(expr_value(e.children[i],expected),expected,e.children[i]->token.at,e.children[i].get(),"AI3107",&c.payload[i].at,"payload-declaration");}e.type=type;e.field_index=index;e.kind=Expr::variant;if(!type.is_payload_union()){e.kind=Expr::literal;e.value=index;e.constant=Constant{type,index};}return e.type;
    }
    Type construct_qualified_variant(Expr& e){
        if(!e.explicit_qualifier && bound_name(e.qualifier))fail("AI3107","A local value does not provide a static case constructor.",e.qualifier.at);auto type=Type::from_shape(products,{Type::product,e.nominal,{}},e.qualifier.at);if(!type.is_union())fail("AI3107","Case construction requires a union type.",e.token.at);for(std::size_t i=0;i<type.definition().cases.size();++i)if(type.definition().cases[i].name==canonical(e.token.text))return construct_variant(e,type,static_cast<int>(i));fail("AI3103","Native union has no case with this name.",e.token.at);
    }
    std::optional<Type> construct_named_variant(Expr& e,std::optional<Type> expected){
        std::vector<std::pair<Type,int>> candidates;if(!products)return std::nullopt;for(std::size_t id=0;id<products->definitions.size();++id){auto& d=products->definitions[id];if(!d.union_)continue;auto type=Type::from_shape(products,{Type::product,static_cast<int>(id),{}});for(std::size_t i=0;i<d.cases.size();++i)if(d.cases[i].name==canonical(e.token.text)){if(expected && type==*expected)return construct_variant(e,type,static_cast<int>(i));candidates.emplace_back(type,static_cast<int>(i));}}if(candidates.size()>1)fail("AI3103","Case constructor is ambiguous without a nominal type context.",e.token.at);if(candidates.empty())return std::nullopt;return construct_variant(e,candidates[0].first,candidates[0].second);
    }
// Closed case resolution and exhaustive, guarded value matching.
    std::set<int> guard_bindings;
    bool guarded_payload(const Expr& e)const{if(e.kind==Expr::name)return guard_bindings.contains(e.symbol);if((e.kind==Expr::field || e.kind==Expr::dereference || e.kind==Expr::overflow_context) && !e.children.empty())return guarded_payload(*e.children[0]);return false;}
    bool match_reference_place(const Expr& e)const{return e.kind==Expr::overflow_context ? match_reference_place(*e.children[0]) : stable_place(e);}
    bool shared_match_path(const Expr& e)const{if(e.kind==Expr::dereference && e.children[0]->type.is_reference() && e.children[0]->type.borrow_kind()==Type::Borrow::shared)return true;if((e.kind==Expr::dereference || e.kind==Expr::field || e.kind==Expr::overflow_context) && !e.children.empty())return shared_match_path(*e.children[0]);return false;}
    void payload_pattern(MatchArm& arm,Type input,Type reference=Type::unit){
        auto& p=*arm.pattern;std::string name;int qualifier=-1;
        if(p.kind==Expr::call){name=canonical(p.token.text);if(p.op=="variant-call"){if(!p.explicit_qualifier && bound_name(p.qualifier))fail("AI3107","A local value is not a pattern type qualifier.",p.qualifier.at);qualifier=p.nominal;}else if(p.nominal>=0)fail("AI3107","A pattern requires a case constructor.",p.token.at);}
        else if(p.kind==Expr::name)name=canonical(p.token.text);
        else if(p.kind==Expr::field && p.children[0]->kind==Expr::name){name=canonical(p.token.text);auto& base=*p.children[0];qualifier=base.nominal>=0 ? base.nominal : -1;if(qualifier<0)for(std::size_t i=0;i<products->definitions.size();++i)if(products->definitions[i].name==canonical(base.token.text))qualifier=static_cast<int>(i);if(qualifier<0)fail("AI3103","Pattern qualifier is not a declared type.",base.token.at);}
        else fail("AI3107","A payload pattern requires a declared case and positional bindings.",p.token.at);
        if(qualifier>=0 && qualifier!=input.product_id())fail("AI3107","Pattern case belongs to a different nominal union.",p.token.at);
        auto& cases=input.definition().cases;auto c=std::find_if(cases.begin(),cases.end(),[&](auto& item){return item.name==name;});if(c==cases.end())fail("AI3103","Native union has no case with this name.",p.token.at);
        std::size_t arity=p.kind==Expr::call ? p.children.size() : 0;if(arity!=c->payload.size())fail("AI3107","Pattern binding count differs from its case payload.",p.token.at);
        for(std::size_t i=0;i<arity;++i){auto& binding=*p.children[i];if(binding.kind!=Expr::name || binding.nominal>=0)fail("AI3107","A payload binding must be a name or wildcard.",binding.token.at);if(binding.token.text!="_"){auto type=Type::from_shape(products,c->payload[i].type);if(reference.is_reference() && type!=Type::unit)type=Type::reference(type,reference.borrow_kind(),binding.token.at);arm.bindings.push_back({binding.token,type,-1,c->fields[i]});}}
        p.type=input;p.constant=Constant{input,static_cast<std::int64_t>(c-cases.begin())};
    }
    std::size_t match_work=0;
    void match_charge(std::size_t n=1){if(n>16000000-match_work)fail("AI3100","Native match analysis work limit exceeded.",{});match_work+=n;}
    bool bound_name(const Token& token)const{for(auto& scope:scopes)if(scope.contains(canonical(token.text)))return true;return false;}
    std::optional<Constant> case_value(const Token& token,std::optional<Type> expected=std::nullopt){
        std::vector<Constant> found;if(!products)return std::nullopt;auto name=canonical(token.text);
        for(std::size_t id=0;id<products->definitions.size();++id){auto& type=products->definitions[id];if(!type.union_)continue;for(std::size_t c=0;c<type.cases.size();++c){match_charge();if(type.cases[c].name==name && !type.cases[c].payload.empty() && expected && expected->is_union() && expected->product_id()==static_cast<int>(id)){Diagnostic d("AI3107","A payload case requires constructor arguments.",token.at);d.related.push_back({"case-declaration",type.cases[c].at});throw d;}if(type.cases[c].name==name && type.cases[c].payload.empty()){auto t=Type::from_shape(products,{Type::product,static_cast<int>(id),{}});if(expected && expected->is_union() && t==*expected)return Constant{t,static_cast<std::int64_t>(c)};found.push_back({t,static_cast<std::int64_t>(c)});}}}
        if(found.size()>1)fail("AI3103","Union case is ambiguous without a nominal type context or qualification.",token.at);return found.empty() ? std::nullopt : std::optional<Constant>{found[0]};
    }
    std::optional<Constant> qualified_case(const Expr& e){
        if(e.kind!=Expr::field || e.children[0]->kind!=Expr::name || (e.children[0]->nominal<0 && bound_name(e.children[0]->token)) || !products)return std::nullopt;
        auto name=canonical(e.children[0]->token.text);for(std::size_t id=0;id<products->definitions.size();++id){auto& type=products->definitions[id];if((e.children[0]->nominal>=0 ? static_cast<int>(id)!=e.children[0]->nominal : type.name!=name) || !type.union_)continue;for(std::size_t c=0;c<type.cases.size();++c)if(type.cases[c].name==canonical(e.token.text)){if(!type.cases[c].payload.empty()){Diagnostic d("AI3107","A payload case requires constructor arguments.",e.token.at);d.related.push_back({"case-declaration",type.cases[c].at});throw d;}return Constant{Type::from_shape(products,{Type::product,static_cast<int>(id),{}}),static_cast<std::int64_t>(c)};}Diagnostic d("AI3103","Native union has no case with this name.",e.token.at);d.related.push_back({"type-declaration",type.at});throw d;}return std::nullopt;
    }
    std::optional<Type> type_hint(const Expr& e){
        if(e.kind==Expr::literal)return e.type;
        if(e.kind==Expr::name){if(bound_name(e.token))return lookup(e.token,false).type;return std::nullopt;}
        if(e.kind==Expr::field){if(auto c=qualified_case(e))return c->type;auto base=type_hint(*e.children[0]);if(base && base->is_product())for(std::size_t i=0;i<base->definition().fields.size();++i)if(base->definition().fields[i].name==canonical(e.token.text))return base->field_type(i);}
        if(e.kind==Expr::call){if(e.nominal>=0)return Type::from_shape(products,{Type::product,e.nominal,{}},e.token.at);auto target=functions.find(canonical(e.token.text));if(target!=functions.end())return target->second->type;}
        if(e.kind==Expr::dereference){auto type=type_hint(*e.children[0]);if(type && type->is_reference())return type->pointee();}
        if(e.kind==Expr::overflow_context)return type_hint(*e.children[0]);
        return std::nullopt;
    }
    Type construct_product(Expr& e,int id){
        auto type=Type::from_shape(products,{Type::product,id,{}},e.token.at);auto& product=type.definition();if(type.is_union())fail("AI3107","A native union value requires a declared case.",e.token.at);std::size_t visible=0;for(auto& field:product.fields)if(!field.synthesized)++visible;if(e.children.size()!=visible)fail("AI3107","Product constructor argument count differs from its fields.",e.token.at);std::vector<E> expanded;expanded.reserve(product.fields.size());std::size_t argument=0;for(std::size_t i=0;i<product.fields.size();++i){if(product.fields[i].synthesized){auto zero=std::make_unique<Expr>();zero->kind=Expr::literal;zero->token=e.token;zero->type=type.field_type(i);zero->value=0;zero->constant=Constant{zero->type,0,0};expanded.push_back(std::move(zero));}else expanded.push_back(std::move(e.children[argument++]));}e.children=std::move(expanded);for(std::size_t i=0;i<e.children.size();++i)require_value(expr_value(e.children[i],type.field_type(i)),type.field_type(i),e.children[i]->token.at,e.children[i].get(),"AI3107",&product.fields[i].at,"field-declaration");e.kind=Expr::construct;return e.type=type;
    }
    Type bind_match(Expr& e,std::optional<Type> expected){
        auto hint=type_hint(*e.children[0]);auto input=hint && hint->is_reference() && hint->pointee().is_union() && match_reference_place(*e.children[0]) ? reference_access(*e.children[0],false) : expr(*e.children[0]);if(input.is_reference() && input.pointee().is_union()){e.match_reference=input;if(input.borrow_kind()==Type::Borrow::exclusive && (guarded_payload(*e.children[0]) || shared_match_path(*e.children[0])))e.match_reference=Type::reference(input.pointee(),Type::Borrow::shared);input=input.pointee();}if(!input.is_union() && input!=Type::boolean && input!=Type::integer)fail("AI3107","A native match requires a closed union, Boolean or Int32 scrutinee.",e.children[0]->token.at);
        auto start=assigned;bool previous=reachable;std::set<std::int64_t> domain;
        if(input.is_union())for(std::size_t c=0;c<input.definition().cases.size();++c)domain.insert(static_cast<std::int64_t>(c));else if(input==Type::boolean)domain={0,1};else domain.insert(INT64_MIN);
        for(auto& arm:e.arms)if(arm.pattern){if(input.is_payload_union())payload_pattern(arm,input,e.match_reference);else {auto type=expr_value(arm.pattern,input);if(type!=input || !arm.pattern->constant)fail("AI3107","A match pattern must be a typed compile-time case or scalar constant.",arm.pattern->token.at);}domain.insert(arm.pattern->constant->number);assigned=start;}
        using State=std::set<int>;using Paths=std::map<std::int64_t,State>;Paths paths;
        if(!start.empty() && domain.size()>1000000/start.size())fail("AI3100","Native match flow fact limit exceeded.",e.token.at);
        auto actual=e.children[0]->constant ? e.children[0]->constant->number : 0;if(input==Type::integer && !domain.contains(actual))actual=INT64_MIN;
        for(auto value:domain)if(!e.children[0]->constant || actual==value){match_charge(start.size()+1);paths.emplace(value,start);}
        auto common=[&](const std::vector<State>& states){State result;if(states.empty())return result;result=states[0];match_charge(result.size()+1);for(std::size_t i=1;i<states.size();++i){match_charge(result.size()+states[i].size()+1);State next;std::set_intersection(result.begin(),result.end(),states[i].begin(),states[i].end(),std::inserter(next,next.end()));result=std::move(next);}return result;};
        std::set<std::int64_t> covered;std::optional<Type> result;std::vector<State> returns;
        for(auto& arm:e.arms){
            bool wildcard=!arm.pattern;auto value=wildcard ? 0 : arm.pattern->constant->number;Location at=wildcard ? arm.token.at : arm.pattern->token.at;
            if((wildcard && covered==domain) || (!wildcard && covered.contains(value)))fail("AI3061","Native match arm is duplicate or unreachable after unconditional coverage.",at);
            std::vector<State> selected;std::vector<std::int64_t> keys;for(auto& [key,state]:paths){match_charge(state.size()+1);if(wildcard || key==value){selected.push_back(state);keys.push_back(key);}}
            assigned=selected.empty() ? start : common(selected);auto before_guard=assigned;reachable=previous && !keys.empty();
            scopes.emplace_back();auto outer_guard_bindings=guard_bindings;for(auto& binding:arm.bindings){binding.symbol=declare(binding.token,binding.type,false);assigned.insert(binding.symbol);guard_bindings.insert(binding.symbol);}
            if(arm.guard)require_value(expr(*arm.guard),Type::boolean,arm.guard->token.at,arm.guard.get(),"AI3101");guard_bindings=outer_guard_bindings;auto after_guard=assigned;
            bool known=arm.guard && arm.guard->constant;bool can_true=!known || arm.guard->constant->number!=0;bool can_false=arm.guard && (!known || arm.guard->constant->number==0);
            bool unconditional=!arm.guard || (known && can_true);if(unconditional){if(wildcard)covered=domain;else covered.insert(value);}
            reachable=previous && !keys.empty() && can_true;auto type=expected ? expr_value(arm.value,expected) : expr(*arm.value,result);if(!result)result=type;else if(type!=*result)fail("AI3062","Native match arm result types are incompatible.",arm.value->token.at);
            for(auto& binding:arm.bindings){assigned.erase(binding.symbol);after_guard.erase(binding.symbol);}scopes.pop_back();
            if(!keys.empty() && can_true){match_charge(assigned.size()+1);returns.push_back(assigned);}assigned=after_guard;
            State added,removed;match_charge(start.size()+before_guard.size()+after_guard.size()+1);std::set_difference(after_guard.begin(),after_guard.end(),before_guard.begin(),before_guard.end(),std::inserter(added,added.end()));std::set_difference(before_guard.begin(),before_guard.end(),after_guard.begin(),after_guard.end(),std::inserter(removed,removed.end()));
            for(auto key:keys){if(!can_false)paths.erase(key);else{auto& state=paths.at(key);match_charge(state.size()+added.size()+removed.size()+1);for(int id:removed)state.erase(id);state.insert(added.begin(),added.end());}}
            std::size_t facts=0;for(auto& [key,state]:paths){(void)key;facts+=state.size();}if(facts>1000000)fail("AI3100","Native match flow fact limit exceeded.",e.token.at);
        }
        reachable=previous;if(covered!=domain)fail("AI3060","Native match does not exhaust its scrutinee type without guarded gaps.",e.token.at);assigned=returns.empty() ? start : common(returns);return e.type=*result;
    }
