// Structural value types used by binding and verified native storage.
// References describe access, never ownership of the referent's allocation.
#include "native_narrow_integer_types.inc"
#include "native_product_schema.inc"
class Type {
public:
    // Fixed underlying types make rejected numeric tag values well-defined.
    enum Primitive : int { integer, boolean, unit, buffer, product, long_integer, small_unsigned8, small_signed8, small_signed16, small_unsigned16, unsigned_integer = 11, unsigned_long_integer = 12, float32 = 13, float64 = 14, type_token = 15, decimal = 16, string = 17, args = 18, object = 19, dynamic = 20, class_ref = 21 };
    enum class Borrow : int { shared, exclusive };
    static constexpr std::size_t reference_depth_limit=128;
private:
    Primitive primitive_=unit;
    int product_=-1;
    std::shared_ptr<const NativeProductTable> products_;
    // Outermost first. A bounded, flat chain cannot contain a recursive cycle.
    std::vector<Borrow> references_;
public:
    Type()=default;
    Type(Primitive primitive):primitive_(primitive){}
    bool operator==(const Type&) const=default;
    bool valid() const {
        if(primitive_<integer || primitive_>native_primitive_limit || references_.size()>reference_depth_limit)return false;
        if(primitive_==product || primitive_==class_ref){if(!products_ || !products_->sealed || product_<0 || static_cast<std::size_t>(product_)>=products_->definitions.size())return false;}
        else if(product_!=-1 || products_)return false;
        return std::all_of(references_.begin(),references_.end(),[](Borrow kind){return kind==Borrow::shared || kind==Borrow::exclusive;});
    }
    static Type reference(Type referent,Borrow kind,Location at={}) {
        if(!referent.valid() || (kind!=Borrow::shared && kind!=Borrow::exclusive))fail("AI3100","Invalid structural native reference type.",at);
        if(referent.references_.size()>=reference_depth_limit)fail("AI3100","Native reference type depth limit exceeded.",at);
        referent.references_.insert(referent.references_.begin(),kind);return referent;
    }
    static Type from_shape(std::shared_ptr<const NativeProductTable> products,const NativeTypeShape& shape,Location at={}){
        if(shape.references.size()>reference_depth_limit)fail("AI3100","Native reference type depth limit exceeded.",at);
        Type result(static_cast<Primitive>(shape.primitive));if(shape.primitive==product || shape.primitive==class_ref){result.product_=shape.product;result.products_=std::move(products);}else if(shape.product!=-1)fail("AI3100","Primitive type carries a nominal product identity.",at);
        for(int mode:shape.references)result.references_.push_back(static_cast<Borrow>(mode));if(!result.valid())fail("AI3100","Invalid structural native product type.",at);return result;
    }
    bool is_product()const{return primitive_==product && !is_reference() && valid() && !definition().union_ && !definition().enum_;}
    bool is_class()const{return primitive_==class_ref && !is_reference() && valid();}
    bool is_enum()const{return primitive_==product && !is_reference() && valid() && definition().enum_;}
    bool is_union()const{return primitive_==product && !is_reference() && valid() && definition().union_;}
    bool is_payload_union()const{return is_union() && definition().payload_;}
    bool is_core_carrier(std::string_view name)const{if(!is_union() || definition().generic<0 || static_cast<std::size_t>(definition().generic)>=products_->generics.size())return false;auto& g=products_->generics[static_cast<std::size_t>(definition().generic)];return g.at.origin==SourceOrigin::core_outcomes && g.name==name;}
    bool is_aggregate()const{return is_product() || is_payload_union();}
    bool is_ref_like()const{return is_product() && !is_reference() && definition().ref_like;}
    int field_case(std::size_t field)const{if(!is_payload_union())return -1;for(std::size_t i=0;i<definition().cases.size();++i)for(int f:definition().cases[i].fields)if(f==static_cast<int>(field))return static_cast<int>(i);fail("AI3100","Unknown union payload projection.",{});}
    bool has_product_root()const{return primitive_==product;}
    int primitive_id()const{return static_cast<int>(primitive_);}
    Type enum_underlying()const{if(!is_enum())fail("AI3100","Expected a native enum type.",{});return Type::from_shape(products_,definition().enum_underlying);}
    int product_id()const{if(!valid() || (primitive_!=product && primitive_!=class_ref))fail("AI3100","Expected a native product type.",{});return product_;}
    const NativeProductDefinition& definition()const{if(!valid() || (primitive_!=product && primitive_!=class_ref))fail("AI3100","Expected a native product definition.",{});return products_->definitions[static_cast<std::size_t>(product_)];}
    const std::shared_ptr<const NativeProductTable>& products()const{return products_;}
    Type field_type(std::size_t index)const{if(!(is_aggregate() || is_class()))fail("AI3100","Field projection requires a product value type.",{});auto& fields=definition().fields;if(index>=fields.size())fail("AI3100","Native product field index is out of range.",{});return from_shape(products_,fields[index].type,fields[index].at);}
    bool contains_references()const{return valid() && (is_reference() || (primitive_==product && definition().has_references));}
    bool is_reference() const {return !references_.empty();}
    std::size_t reference_depth() const {return references_.size();}
    Borrow borrow_kind(Location at={}) const {
        if(!valid() || !is_reference())fail("AI3100","Expected a structural native reference type.",at);
        return references_.front();
    }
    Type pointee(Location at={}) const {
        if(!valid() || !is_reference())fail("AI3100","Expected a structural native reference type.",at);
        auto result=*this;result.references_.erase(result.references_.begin());return result;
    }
    bool is_copy() const {
        return valid() && (is_reference() ? references_.front()==Borrow::shared : (primitive_==class_ref ? true : (primitive_==product ? definition().copy : (primitive_==string || primitive_==args || primitive_==object || primitive_==dynamic) ? true : primitive_!=buffer)));
    }
    bool needs_drop() const {return valid() && !is_reference() && (primitive_==buffer || (primitive_==product && definition().drop));}
    bool is_signed_integer() const {return !is_reference() && (primitive_==integer || primitive_==long_integer);}
    const NativeNarrowInteger* narrow_integer() const {
        return is_reference() ? nullptr : native_narrow_integer(primitive_);
    }
    bool is_char() const {
        const auto* small=narrow_integer();
        return small && std::string_view(small->keyword)=="char";
    }
    bool is_uint32() const {return !is_reference() && primitive_==unsigned_integer;}
    bool is_ulong64() const {return !is_reference() && primitive_==unsigned_long_integer;}
    bool is_float32() const {return !is_reference() && primitive_==float32;}
    bool is_float64() const {return !is_reference() && primitive_==float64;}
    bool is_type_token() const {return !is_reference() && primitive_==type_token;}
    bool is_decimal() const {return !is_reference() && primitive_==decimal;}
    bool is_integer() const {return is_signed_integer() || narrow_integer()!=nullptr || is_uint32() || is_ulong64();}
    bool is_scalar() const {return is_integer() || is_float32() || is_float64() || is_type_token() || is_decimal() || is_enum() || *this==Type::boolean;}
    // Type structure alone is not loan/lifetime proof. Version4 cannot emit refs.
    bool bootstrap_supported(bool allow_unit=true) const {return valid() && primitive_!=product && !is_reference() && (allow_unit || primitive_!=unit);}
    bool local_supported(bool allow_unit=true) const {return valid() && (primitive_==product ? true : is_reference() ? primitive_!=unit : allow_unit || primitive_!=unit);}
    std::string reference_name() const {
        if(!valid() || !is_reference() || primitive_==unit)fail("AI3100","Native reference representation is unavailable.",{});
        std::string result="ai_ref_";for(auto kind:references_)result+=kind==Borrow::shared ? 's' : 'm';
        if(primitive_==product)result+=(definition().enum_ ? 'e' : 'p')+std::to_string(product_);else if(primitive_==class_ref)result+='c'+std::to_string(product_);
        else if(const auto* small=native_narrow_integer(primitive_))result+=small->reference_tag;
        else result+=primitive_==integer ? 'i' : primitive_==long_integer ? 'l' : primitive_==unsigned_integer ? 'U' : primitive_==unsigned_long_integer ? 'L' : primitive_==float32 ? 'f' : primitive_==float64 ? 'd' : primitive_==type_token ? 't' : primitive_==decimal ? 'm' : primitive_==boolean ? 'b' : 'o';
        return result;
    }
    std::string label() const {
        if(!valid())fail("AI3100","Invalid structural native type.",{});
        std::string result;
        for(auto kind:references_)result+=kind==Borrow::shared ? "shared-reference<" : "exclusive-reference<";
        if(const auto* small=native_narrow_integer(primitive_))result+=small->label;
        else result+=primitive_==string ? "string" : primitive_==args ? "args" : primitive_==object ? "object" : primitive_==dynamic ? "dynamic" : primitive_==integer ? "int32" : primitive_==long_integer ? "int64" : primitive_==unsigned_integer ? "uint32" : primitive_==unsigned_long_integer ? "uint64" : primitive_==float32 ? "float32" : primitive_==float64 ? "float64" : primitive_==type_token ? "type" : primitive_==decimal ? "decimal" : primitive_==boolean ? "bool" : primitive_==buffer ? "owned-buffer" : primitive_==class_ref ? "class<"+definition().name+">" : primitive_==product ? (definition().enum_ ? "enum<" : definition().union_ ? "union<" : "product<")+definition().name+">" : primitive_==string ? "string" : primitive_==args ? "args" : primitive_==object ? "object" : primitive_==dynamic ? "dynamic" : "unit";
        result.append(references_.size(),'>');return result;
    }
};
std::optional<Type::Primitive> native_integer_spelling(std::string_view spelling) {
    if(spelling=="int" || spelling=="i32")return Type::integer;
    if(spelling=="uint")return Type::unsigned_integer;
    if(spelling=="ulong" || spelling=="u64")return Type::unsigned_long_integer;
    if(spelling=="float")return Type::float32;
    if(spelling=="double")return Type::float64;
    if(spelling=="decimal")return Type::decimal;
    if(spelling=="long" || spelling=="i64")return Type::long_integer;
    if(const auto* small=native_narrow_integer(spelling))return static_cast<Type::Primitive>(small->primitive);
    return std::nullopt;
}
bool native_integer_widens(Type source,Type target) {
    if(source==target)return false;
    if(target.is_decimal())return source.is_integer();
    if(target.is_float64())return source.is_integer() || source.is_float32();
    if(source.is_float32())return target.is_float64();
    if(target.is_float32())return source.is_integer();
    if(!source.is_integer() || !target.is_integer())return false;
    // CLR numeric promotion: nothing widens implicitly to char, and char widens
    // only to ushort/int/long among the implemented integral types.
    if(target.is_char())return false;
    if(source.is_char()){
        const auto* ushort_small=native_narrow_integer("ushort");
        const bool to_ushort=ushort_small && target.narrow_integer() && target.narrow_integer()->primitive==ushort_small->primitive;
        return to_ushort || target==Type::integer || target==Type::long_integer || target==Type::unsigned_integer || target==Type::unsigned_long_integer;
    }
    if(target==Type::unsigned_integer)
        return source.narrow_integer() && source.narrow_integer()->minimum>=0;
    if(target==Type::unsigned_long_integer)
        return source.is_uint32() || (source.narrow_integer() && source.narrow_integer()->minimum>=0);
    if(source.is_uint32())return target==Type::long_integer;
    if(source.is_ulong64())return false;
    if(target==Type::long_integer)return true;
    auto from=source.narrow_integer();
    if(!from)return false;
    if(target==Type::integer)return true;
    auto to=target.narrow_integer();
    return to && from->minimum>=to->minimum && from->maximum<=to->maximum;
}
Type native_integer_operator_type(Type type) {
    return type.narrow_integer() ? Type::integer : type;
}
