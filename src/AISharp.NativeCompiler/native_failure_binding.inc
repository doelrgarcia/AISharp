// Native declared outcomes and explicit, nominally compatible propagation.
    bool declared_failure=false;
    Type return_carrier=Type::unit,error_type=Type::unit;
    Location failure_declaration;
    static E unit_expression(Token token){auto value=std::make_unique<Expr>();value->kind=Expr::unit_value;value->token=std::move(token);value->type=Type::unit;return value;}
    static E outcome_expression(Type carrier,int which,E payload,Token token){auto value=std::make_unique<Expr>();value->kind=Expr::variant;value->token=std::move(token);value->type=carrier;value->field_index=which;if(payload)value->children.push_back(std::move(payload));return value;}
    [[noreturn]] void failure_error(std::string code,std::string message,Location at,Type actual=Type::unit){Diagnostic d(std::move(code),std::move(message),at);d.failureObligation="typed-propagation-contract";d.expectedType=return_carrier.label();d.actualType=actual.label();d.related.push_back({declared_failure ? "failure-declaration" : "return-declaration",failure_declaration});throw d;}
    Type propagate(Expr& e,std::optional<Type> expected){
        std::optional<Type> input_expected;
        bool result=return_carrier.is_core_carrier("Result"),option=return_carrier.is_core_carrier("Option");
        if(expected && (result || option))for(std::size_t i=0;i<products->definitions.size();++i){auto candidate=Type::from_shape(products,{Type::product,static_cast<int>(i),{}});if(!candidate.is_core_carrier(result ? "Result" : "Option"))continue;auto& args=candidate.definition().arguments;if(Type::from_shape(products,args[0])!=*expected)continue;if(result && Type::from_shape(products,args[1])!=Type::from_shape(products,return_carrier.definition().arguments[1]))continue;input_expected=candidate;break;}
        auto input=expr(*e.children[0],input_expected);
        if(!input.is_core_carrier("Result") && !input.is_core_carrier("Option"))failure_error("AI3130","Propagation requires a canonical Result or Option value; use an explicit borrowed match for borrowed carriers.",e.token.at,input);
        bool source_result=input.is_core_carrier("Result");
        if((source_result && !result) || (!source_result && !option))failure_error("AI3131","Propagation requires a compatible declared or explicit carrier result.",e.token.at,input);
        if(source_result && Type::from_shape(products,input.definition().arguments[1])!=Type::from_shape(products,return_carrier.definition().arguments[1]))failure_error("AI3131","Propagated errors must have the exact resolved error type and reference mode.",e.token.at,input);
        e.kind=Expr::match;e.propagation=true;e.type=Type::from_shape(products,input.definition().arguments[0]);
        for(int c=0;c<2;++c){MatchArm arm{};arm.token=e.token;arm.pattern=std::make_unique<Expr>();arm.pattern->kind=Expr::variant;arm.pattern->token=e.token;arm.pattern->type=input;arm.pattern->constant=Constant{input,c};
            E payload;
            if(!input.definition().cases[static_cast<std::size_t>(c)].payload.empty()){auto& item=input.definition().cases[static_cast<std::size_t>(c)].payload[0];NativePatternBinding binding{};binding.token=e.token;binding.type=Type::from_shape(products,item.type);binding.symbol=symbols++;binding.field=input.definition().cases[static_cast<std::size_t>(c)].fields[0];arm.bindings.push_back(binding);payload=std::make_unique<Expr>();payload->kind=Expr::name;payload->token=e.token;payload->type=binding.type;payload->symbol=binding.symbol;}
            arm.value=c==0 ? std::move(payload) : outcome_expression(return_carrier,1,std::move(payload),e.token);e.arms.push_back(std::move(arm));
        }
        return e.type;
    }
