#include "compiler.hpp"
#include <algorithm>
#include <bit>
#include <charconv>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <limits>
#include <iterator>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <sstream>
#include <utility>
#include <tuple>
#include <functional>
#include <vector>

#include "native_source_identity.inc"
namespace aisharp {
namespace {
struct Token { std::string text; Location at; enum Kind { id, number, string, character, symbol, end } kind; std::int64_t value=0; };
[[noreturn]] void fail(std::string code, std::string message, Location at) {
    throw Diagnostic(std::move(code), std::move(message), at);
}
#include "native_types.inc"
#pragma warning(push)
#pragma warning(disable:4505)
#define AI_DECIMAL_CODE(...) __VA_ARGS__
#include "native_decimal_ops.inc"
#undef AI_DECIMAL_CODE
#pragma warning(pop)
// Target width for the nint/nuint aliases; set by compile_native for each invocation.
unsigned native_target_bits=64;
// The natively compiled front end is itself an AI# program. Its stage1 code generator
// legitimately exceeds the original 128-function bootstrap probe budget, so the limit
// is a generous safety cap rather than a tight self-host ceiling.
unsigned constexpr native_function_limit=1024;
const std::vector<NativeGenericDefinition>& native_core_definitions();
bool letter(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_'; }
bool digit(char c) { return c >= '0' && c <= '9'; }
class Lexer {
    std::string_view source;
    std::size_t byte = 0;
    Location at;
    char peek(std::size_t n = 0) const { return byte + n < source.size() ? source[byte+n] : '\0'; }
    std::uint32_t scalar() {
        const auto first = static_cast<unsigned char>(source[byte]);
        std::size_t count = 1; std::uint32_t cp = first;
        if (first >= 0x80) {
            if (first >= 0xc2 && first <= 0xdf) { count=2; cp=first & 31u; }
            else if (first >= 0xe0 && first <= 0xef) { count=3; cp=first & 15u; }
            else if (first >= 0xf0 && first <= 0xf4) { count=4; cp=first & 7u; }
            else fail("AI3100", "Source is not valid UTF-8.", at);
            if (byte+count > source.size()) fail("AI3100", "Truncated UTF-8 sequence.", at);
            for (std::size_t i=1; i<count; ++i) {
                auto next=static_cast<unsigned char>(source[byte+i]);
                if ((next & 0xc0u) != 0x80u) fail("AI3100", "Source is not valid UTF-8.", at);
                cp=(cp<<6u) | (next & 63u);
            }
            if ((count==3 && cp<0x800) || (count==4 && cp<0x10000) || cp>0x10ffff || (cp>=0xd800 && cp<=0xdfff))
                fail("AI3100", "Noncanonical UTF-8 scalar.", at);
        }
        byte += count; at.offset += cp > 0xffff ? 2 : 1;
        if (cp == '\n') { ++at.line; at.column=1; } else at.column += cp > 0xffff ? 2 : 1;
        return cp;
    }
    void advance() { (void)scalar(); }
    static int hex_value(char c) {
        if(c>='0' && c<='9')return c-'0';
        if(c>='a' && c<='f')return c-'a'+10;
        if(c>='A' && c<='F')return c-'A'+10;
        return -1;
    }
    [[noreturn]] void char_literal_error(const char* code,const char* message,Location start) {
        while(byte<source.size() && peek()!='\'' && peek()!='\n' && peek()!='\r')advance();
        if(byte<source.size() && peek()=='\'')advance();
        start.length=at.offset-start.offset;
        fail(code,message,start);
    }
    std::uint32_t hex_escape(int digits,Location start) {
        std::uint32_t value=0;
        for(int i=0;i<digits;++i){
            if(byte>=source.size() || hex_value(peek())<0)char_literal_error("AI3137","Invalid character escape sequence.",start);
            value=value*16u+static_cast<std::uint32_t>(hex_value(peek()));
            advance();
        }
        return value;
    }
public:
    explicit Lexer(std::string_view input,SourceOrigin origin=SourceOrigin::input) : source(input) {
        at.origin=origin;
        if (source.starts_with("\xef\xbb\xbf")) byte=3;
    }
    std::vector<Token> run() {
        std::vector<Token> tokens;
        while (byte < source.size()) {
            if (peek()==' ' || peek()=='\t' || peek()=='\r' || peek()=='\n') { advance(); continue; }
            if (peek()=='/' && peek(1)=='/') { while (byte<source.size() && peek()!='\n') advance(); continue; }
            if (peek()=='/' && peek(1)=='*') {
                auto start=at; advance(); advance();
                while (byte<source.size() && !(peek()=='*' && peek(1)=='/')) advance();
                if (byte==source.size()) fail("AI3100", "Unterminated block comment.", start);
                advance(); advance(); continue;
            }
            auto start=at; auto first=byte; Token::Kind kind=Token::symbol; std::int64_t token_value=0;
            if (letter(peek()) || peek()=='@') {
                kind=Token::id;
                if (peek()=='@') { advance(); if (!letter(peek())) fail("AI3100", "Expected escaped identifier.", start); }
                do { advance(); } while (letter(peek()) || digit(peek()));
            } else if (digit(peek()) || (peek()=='.' && digit(peek(1)))) {
                kind=Token::number;
                if(peek()=='0' && (peek(1)=='x' || peek(1)=='X' || peek(1)=='b' || peek(1)=='B')){
                    do { advance(); } while (letter(peek()) || digit(peek()) || peek()=='_');
                } else {
                    while(digit(peek()) || peek()=='_') advance();
                    if(peek()=='.'){ advance(); while(digit(peek()) || peek()=='_') advance(); }
                    if(peek()=='e' || peek()=='E'){ advance(); if(peek()=='+' || peek()=='-') advance(); while(digit(peek()) || peek()=='_') advance(); }
                    while(letter(peek()) || digit(peek()) || peek()=='_') advance();
                }
            } else if (peek()=='"') {
                kind=Token::string; advance(); bool closed=false;
                while (byte<source.size()) {
                    if (peek()=='"') { advance(); closed=true; break; }
                    if (peek()=='\n' || peek()=='\r') fail("AI3100", "Intent strings must fit on one line.", start);
                    if (peek()=='\\') { advance(); if (byte==source.size()) break; if(peek()=='\n' || peek()=='\r')fail("AI3100","Intent strings must fit on one line.",start); }
                    advance();
                }
                if (!closed) fail("AI3100", "Unterminated string.", start);
            } else if (peek()=='\'') {
                kind=Token::character; advance();
                if (byte>=source.size() || peek()=='\n' || peek()=='\r')
                    char_literal_error("AI3138", "Unterminated character literal.", start);
                if (peek()=='\'')
                    char_literal_error("AI3136", "A character literal must contain exactly one UTF-16 code unit.", start);
                std::uint32_t code=0;
                if (peek()=='\\') {
                    advance();
                    if (byte>=source.size() || peek()=='\n' || peek()=='\r')
                        char_literal_error("AI3138", "Unterminated character literal.", start);
                    const char escape=peek();
                    if (escape=='u') { advance(); code=hex_escape(4,start); }
                    else if (escape=='U') {
                        advance(); auto wide=hex_escape(8,start);
                        if (wide>0xffffu || (wide>=0xd800u && wide<=0xdfffu))
                            char_literal_error("AI3136", "A character literal must contain exactly one UTF-16 code unit.", start);
                        code=wide;
                    } else if (escape=='x') {
                        advance(); std::uint32_t value=0; int digits=0;
                        while (digits<4 && byte<source.size() && hex_value(peek())>=0) {
                            value=value*16u+static_cast<std::uint32_t>(hex_value(peek())); advance(); ++digits;
                        }
                        if (!digits) char_literal_error("AI3137", "Invalid character escape sequence.", start);
                        code=value;
                    } else if (escape=='\'' || escape=='"' || escape=='\\') {
                        code=static_cast<unsigned char>(escape); advance();
                    } else if (escape=='0') { code=0; advance(); }
                    else if (escape=='a') { code=7; advance(); }
                    else if (escape=='b') { code=8; advance(); }
                    else if (escape=='f') { code=12; advance(); }
                    else if (escape=='n') { code=10; advance(); }
                    else if (escape=='r') { code=13; advance(); }
                    else if (escape=='t') { code=9; advance(); }
                    else if (escape=='v') { code=11; advance(); }
                    else char_literal_error("AI3137", "Invalid character escape sequence.", start);
                } else {
                    code=scalar();
                    if (code>0xffffu)
                        char_literal_error("AI3136", "A character literal must contain exactly one UTF-16 code unit.", start);
                }
                if (byte>=source.size() || peek()=='\n' || peek()=='\r')
                    char_literal_error("AI3138", "Unterminated character literal.", start);
                if (peek()!='\'')
                    char_literal_error("AI3136", "A character literal must contain exactly one UTF-16 code unit.", start);
                advance();
                token_value=static_cast<std::int64_t>(code);
            } else {
                bool found=false;
                for (auto op : {"=>", ">>>=", ">>>", "<<=", ">>=", "->", "==", "!=", "<=", ">=", "&&", "||", "<<", ">>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "++", "--"}) {
                    if (source.substr(byte).starts_with(op)) { for (std::size_t i=0;i<std::string_view(op).size();++i) advance(); found=true; break; }
                }
                if (!found) {
                    if (std::string_view("{}[]():;,=+-*/%!~<>&|^.?").find(peek())==std::string_view::npos)
                        fail("AI3100", "Character or identifier is outside the native bootstrap grammar.", start);
                    advance();
                }
            }
            start.length=at.offset-start.offset;
            tokens.push_back({std::string(source.substr(first,byte-first)),start,kind,token_value});
            if (tokens.size()>200000) fail("AI3100", "Native bootstrap token limit exceeded.", start);
        }
        tokens.push_back({"",at,Token::end}); return tokens;
    }
};
struct Constant { Type type; std::int64_t number; std::int64_t number2=0; };
struct Expr;
using E=std::unique_ptr<Expr>;
struct NativePatternBinding {Token token;Type type=Type::unit;int symbol=-1,field=-1;};
struct MatchArm { Token token;E pattern,guard,value;std::vector<NativePatternBinding> bindings; };
struct Expr {
    enum Kind { literal, name, unary, binary, call, assign, update, overflow_context, borrow, dereference, construct, field, match, variant, propagate, unit_value, integer_conversion, sizeof_type, typeof_type, params_pack, with_expr, default_value, initializer, is_test, nameof_sym, args_value, args_length, index, as_test, stack_alloc, class_new, class_field_assign, class_upcast, class_downcast, class_tag, fixed_pin, await_value, query_select } kind;
    Token token; Token qualifier; bool explicit_qualifier=false,propagation=false,null_literal=false; Type type=Type::unit,match_reference=Type::unit; std::int64_t value=0; int symbol=-1; std::size_t height=1;
    int field_index=-1,nominal=-1;
    NativeTypeShape sizeof_shape;
    std::string op; bool postfix=false, wraps=false, long_minimum_magnitude=false;
    std::optional<Constant> constant;
    std::vector<std::unique_ptr<Expr>> children;std::vector<MatchArm> arms;std::vector<std::pair<Token,E>> with_updates;std::string external_name;
};
struct Stmt {
    enum Kind { block, local, expression, result, failure, branch, loop, for_loop, do_loop, stop, next, label, goto_jump, try_block, yield_return, yield_break } kind;
    Token token; Location end; std::optional<NativeTypeShape> annotation; Type type=Type::unit; int symbol=-1; bool mutable_=false, constant=false, has_catch=false, has_finally=false; Token catch_name; std::unique_ptr<Expr> catch_filter;
    E expr, update;
    int scope_depth=-1;
    bool scoped_ref=false,using_resource=false,switch_control=false,lock_binding=false,static_binding=false;
    std::string op;
    std::vector<std::unique_ptr<Stmt>> children;
};
using S=std::unique_ptr<Stmt>;
struct Parameter { Token token; Type type; int symbol=-1; NativeTypeShape shape; bool scoped=false,readonly=false,params=false,in_parameter=false,out_parameter=false,mutable_parameter=false; };
struct NativeStaticBinding { int symbol; std::string name; Type type; std::int64_t value; };
struct Function { std::shared_ptr<const NativeProductTable> products;Token token; Type type,success_type,error_type; NativeTypeShape return_shape,success_shape,error_shape;bool fallible=false,unwind=false,external=false,accessor_get=false,accessor_set=false,accessor_add=false,accessor_remove=false,async_method=false,constructor=false,extension_block=false,implicit_value=false,virtual_method=false,override_method=false,abstract_method=false;int constructor_product=-1;std::string member_name;Location failure_declaration,fault_mode_location;std::string external_name,property_name; int symbol=-1; std::vector<Parameter> parameters; std::vector<NativeStaticBinding> statics; S body; };
std::string canonical(std::string text) { if (text.starts_with('@')) text.erase(0,1); return text; }
std::string native_shape_key(const NativeTypeShape& shape){if(!shape.references.empty())return "ref";if(shape.primitive==4)return "p"+std::to_string(shape.product);return std::to_string(static_cast<int>(shape.primitive));}
bool reserved(const std::string& s) {
    static const std::set<std::string> words={"int","i32","long","i64","bool","unit","void","fn","let","var","return","if","else","while","for","do","break","continue","goto","scoped","volatile","params","operator","implicit","explicit","foreach","partial","lock","with","throw","extern","new","required","nint","nuint","float","double","decimal","sizeof","typeof","true","false","default","module","intent","ensures","checked","unchecked","const","class","struct","unsafe","owned","borrowed","async","await","using","try","throw","new","fails","fail"};
    return words.contains(s) || native_narrow_integer(s)!=nullptr;
}
class Parser {
    std::vector<Token> tokens; std::size_t pos=0, depth=0;
    bool wraps=false,pattern_mode=false;bool pending_async=false;bool body_has_yield=false;std::size_t query_counter=0;std::vector<Function> pending_queries;int pending_method_flags=0;std::size_t grouping=0,match_scan_work=0,generic_scan_work=0,accessor_counter=0;std::string active_namespace_prefix;bool namespace_declared=false;struct ManagedDeclaration{std::vector<NativeTypeShape> parameters;NativeTypeShape result;Location at;bool unmanaged;};std::map<int,ManagedDeclaration> managed_declarations;
    NativeProductBuilder product_builder;
    std::shared_ptr<const NativeProductTable> products;
    std::map<std::size_t,std::size_t> product_declarations;
    struct Group { std::size_t& depth; explicit Group(std::size_t& n):depth(n){++depth;}~Group(){--depth;} };
    struct OverflowScope { bool& mode; bool previous; OverflowScope(bool& target,bool value):mode(target),previous(target){mode=value;} ~OverflowScope(){mode=previous;} };
    struct Depth { Parser& p; explicit Depth(Parser& owner) : p(owner) { if (++p.depth>128) fail("AI3100","Native bootstrap nesting limit exceeded.",p.current().at); } ~Depth(){--p.depth;} };
    const Token& current() const { return tokens[pos]; }
    bool is(std::string_view s) const { return current().text==s; }
    bool eat(std::string_view s) { if (!is(s)) return false; ++pos; return true; }
    Token take() { auto result=current(); if (result.kind!=Token::end) ++pos; return result; }
    Token expect(std::string_view s) { if (!is(s)) fail("AI3100","Expected '"+std::string(s)+"'.",current().at); return take(); }
    Token identifier() {
        auto t=take(); if (t.kind!=Token::id || (!t.text.starts_with('@') && reserved(t.text))) fail("AI3106","Expected a nonreserved identifier.",t.at); return t;
    }
#include "native_generic_parser.inc"
    void end_statement() {
        if (eat(";") || is("}") || current().kind==Token::end || current().at.line>tokens[pos-1].at.line) return;
        fail("AI3100","Expected a newline, semicolon or closing brace.",current().at);
    }
    static int precedence(const std::string& op) {
        if(op=="=" || op=="+=" || op=="-=" || op=="*=" || op=="/=" || op=="%=" || op=="&=" || op=="|=" || op=="^=" || op=="<<=" || op==">>=" || op==">>>=")return 1;
        if(op=="||")return 2; if(op=="&&")return 3; if(op=="|")return 4; if(op=="^")return 5; if(op=="&")return 6;
        if(op=="==" || op=="!=")return 7; if(op=="<" || op==">" || op=="<=" || op==">=" || op=="is" || op=="as")return 8;
        if(op=="<<" || op==">>" || op==">>>")return 9; if(op=="+" || op=="-")return 10; if(op=="*" || op=="/" || op=="%")return 11; return 0;
    }
    bool match_start(){
        if(pattern_mode || !is("match"))return false;auto next=tokens[pos+1].text;
        bool start=tokens[pos+1].kind==Token::number || (tokens[pos+1].kind==Token::id && (!reserved(next) || next=="true" || next=="false" || next=="checked" || next=="unchecked")) || next=="(" || next=="+" || next=="-" || next=="!" || next=="~" || next=="*" || next=="&";
        if(!start)return false;int parens=0;
        for(std::size_t i=pos+1;i<tokens.size();++i){if(++match_scan_work>16000000)fail("AI3100","Native match lookahead work limit exceeded.",tokens[pos].at);auto& token=tokens[i];
            if(token.text=="("){++parens;continue;}if(token.text==")"){if(parens==0)return false;--parens;continue;}if(parens)continue;
            if(token.text=="{")return true;if(token.kind==Token::end || token.text=="}" || token.text==";" || token.text=="," || token.text=="=>")return false;
            if(i>pos+1 && token.at.line>tokens[i-1].at.line && (token.text=="let" || token.text=="var" || token.text=="const" || token.text=="return" || token.text=="if" || token.text=="while" || token.text=="for" || token.text=="break" || token.text=="continue"))return false;
        }return false;
    }
    bool field_modifier_ahead(const std::string& word)const{
        if(!is(word))return false;
        std::size_t p=pos+1;
        auto modifier=[&](const std::string& text){return text=="readonly" || text=="volatile" || text=="init" || text=="required" || text=="public" || text=="private" || text=="internal" || text=="protected";};
        while(p<tokens.size() && modifier(tokens[p].text))++p;
        return p+1<tokens.size() && tokens[p].kind==Token::id && tokens[p+1].text==":";
    }
    E primary() {
        Depth guard(*this);bool matching=match_start(); auto t=take();bool global_qualified=false;if(t.text=="global" && is(":") && pos+1<tokens.size() && tokens[pos+1].text==":"){expect(":");expect(":");t=identifier();global_qualified=true;} if(t.kind==Token::id && is(".") && pos+1<tokens.size() && tokens[pos+1].kind==Token::id){std::string combined=canonical(t.text)+"_"+canonical(tokens[pos+1].text);if(product_builder.find(combined)>=0 || product_builder.find_generic(combined)>=0){pos+=2;t.text=combined;}}if(!global_qualified && t.kind==Token::id && is(".") && !active_namespace_prefix.empty()){int namespaced=product_builder.find(active_namespace_prefix+canonical(t.text));if(namespaced>=0)t.text=active_namespace_prefix+canonical(t.text);} auto e=std::make_unique<Expr>(); e->token=t;e->wraps=wraps;if(!global_qualified && t.kind==Token::id && is("(")){int namespaced=product_builder.find(canonical(t.text));if(namespaced<0)namespaced=active_namespace_prefix.empty()? -1 : product_builder.find(active_namespace_prefix+canonical(t.text));if(namespaced>=0)e->nominal=namespaced;}
        if(matching){
            e->kind=Expr::match;e->children.push_back(expression());expect("{");
            while(!is("}")){
                if(current().kind==Token::end)fail("AI1025","Unterminated native match.",t.at);MatchArm arm{};arm.token=current();
                bool negate=false;if(eat("not"))negate=true;
            E first;bool wildcard=false;if(!eat("_")){bool old_pattern=pattern_mode;pattern_mode=true;first=expression();pattern_mode=old_pattern;}else wildcard=true;
            std::string combinator;std::vector<E> terms;if(first)terms.push_back(std::move(first));
            if(!negate && !wildcard && (is("or") || is("and"))){combinator=take().text;for(;;){if(!first && wildcard)fail("AI3107","A pattern combinator requires a pattern operand.",arm.token.at);bool old_pattern=pattern_mode;pattern_mode=true;terms.push_back(expression());pattern_mode=old_pattern;if(is(combinator)){take();continue;}if(is("or") || is("and"))fail("AI3107","Mixed pattern combinators are not implemented by the native bootstrap.",current().at);break;}}
            if(negate || !combinator.empty()){
                if(e->children[0]->kind!=Expr::name)fail("AI3107","Pattern combinators require a simple identifier match subject in the native bootstrap.",arm.token.at);
                auto make_binary=[&](const std::string& op,E left,E right){auto node=std::make_unique<Expr>();node->kind=Expr::binary;node->op=op;node->token=left->token;node->children.push_back(std::move(left));node->children.push_back(std::move(right));return node;};
                auto compare=[&](E pattern){auto subject=std::make_unique<Expr>();subject->kind=Expr::name;subject->token=e->children[0]->token;return make_binary("==",std::move(subject),std::move(pattern));};
                if(negate){if(wildcard || terms.size()!=1)fail("AI3107","not requires exactly one scalar pattern operand.",arm.token.at);auto comparison=compare(std::move(terms[0]));auto node=std::make_unique<Expr>();node->kind=Expr::unary;node->op="!";node->token=comparison->token;node->children.push_back(std::move(comparison));arm.guard=std::move(node);}
                else {if(terms.empty())fail("AI3107","A pattern combinator requires pattern operands.",arm.token.at);E combined=compare(std::move(terms[0]));for(std::size_t i=1;i<terms.size();++i)combined=make_binary(combinator=="or" ? "||" : "&&",std::move(combined),compare(std::move(terms[i])));arm.guard=std::move(combined);}
                if(eat("when"))fail("AI3107","A pattern combinator cannot be combined with when in the native bootstrap.",arm.token.at);
            }else{
                if(wildcard)arm.pattern.reset();else arm.pattern=std::move(terms[0]);
                if(eat("when"))arm.guard=expression();
            }
            expect("=>");arm.value=expression();
                for(auto* part:{arm.pattern.get(),arm.guard.get(),arm.value.get()})if(part)e->height=std::max(e->height,part->height+1);
                e->arms.push_back(std::move(arm));if(e->arms.size()>256)fail("AI3100","Native match arm limit exceeded.",t.at);
                if(!eat(",") && !eat(";") && !is("}") && current().at.line==tokens[pos-1].at.line)fail("AI1025","Expected a native match arm separator.",current().at);
            }expect("}");if(e->arms.empty())fail("AI3060","A match requires an exhaustive arm set.",t.at);
        }
        else if(t.kind==Token::id && canonical(t.text)=="std" && pos+3<tokens.size() && tokens[pos].text=="." && canonical(tokens[pos+1].text)=="core" && tokens[pos+2].text=="." && tokens[pos+3].kind==Token::id && [&](){auto begin=pos;pos+=4;bool result=generic_expression();pos=begin;return result;}()){--pos;auto shape=type_shape();e->nominal=shape.product;e->kind=Expr::name;}
        else if(t.kind==Token::id && !global_qualified && (product_builder.find_generic(canonical(t.text))>=0 || (!active_namespace_prefix.empty() && product_builder.find_generic(active_namespace_prefix+canonical(t.text))>=0)) && generic_expression()){--pos;auto shape=type_shape();e->nominal=shape.product;e->kind=Expr::name;}
        else if(t.text=="(" && pos+1<tokens.size() && tokens[pos+1].text==")" &&
                (native_integer_spelling(current().text) || current().text=="nint" || current().text=="nuint" || current().text=="float" || current().text=="object" || current().text=="dynamic" || current().text=="bool" || current().text=="string" || product_builder.find(canonical(current().text))>=0 || product_builder.alias(canonical(current().text)).has_value())){
            auto target=take();expect(")");
            if(target.text=="object" || target.text=="dynamic"){e->kind=Expr::integer_conversion;e->op="object-box";e->type=target.text=="dynamic" ? Type::dynamic : Type::object;e->children.push_back(primary());}
            else{
            e->kind=Expr::integer_conversion;e->token=target;
            if(auto aliased=product_builder.alias(canonical(target.text));aliased){e->sizeof_shape=*aliased;e->op="alias-cast";}
            else if(int id=product_builder.find(canonical(target.text));id>=0){e->sizeof_shape=NativeTypeShape{Type::product,id,{}};e->op="enum-cast";}
            else if(target.text=="float")e->type=Type::float32;
            else if(target.text=="bool")e->type=Type::boolean;
            else if(target.text=="string")e->type=Type::string;
            else if(target.text=="nint")e->type=native_target_bits==32 ? Type::integer : Type::long_integer;
            else if(target.text=="nuint")e->type=native_target_bits==32 ? Type::unsigned_integer : Type::unsigned_long_integer;
            else e->type=*native_integer_spelling(target.text);
            e->children.push_back(primary());
            }
        }
        else if(t.text=="from"){auto range=identifier();expect("in");auto source=expression();if(is("join")){take();auto other_range=identifier();expect("in");auto other_source=expression();expect("on");auto left=expression();expect("equals");auto right=expression();if(left->kind!=Expr::name || canonical(left->token.text)!=canonical(range.text) || right->kind!=Expr::name || canonical(right->token.text)!=canonical(other_range.text))fail("AI3107","The bootstrap join keys must be the two range variables.",left->token.at);expect("select");auto projection=expression();e->kind=Expr::query_select;e->token=t;e->value=1;e->op="$query_join$"+std::to_string(query_counter++);e->children.push_back(std::move(source));e->children.push_back(std::move(other_source));pending_queries.push_back(make_join_helper(e->op,range,other_range,t,std::move(projection)));}else{bool descending=false;bool grouped=false;std::unique_ptr<Expr> group_key;std::string continuation;if(is("group")){grouped=true;take();auto element=expression();expect("by");auto key=expression();if(key->kind!=Expr::name || canonical(key->token.text)!=canonical(range.text))fail("AI3107","The bootstrap group key must be the range variable.",key->token.at);(void)element;expect("into");auto name=identifier();continuation=canonical(name.text);expect("select");auto projection=expression();e->kind=Expr::query_select;e->token=t;e->op="$query_group$"+std::to_string(query_counter++);e->children.push_back(std::move(source));pending_queries.push_back(make_query_helper(e->op,range,t,std::move(projection),false,true,std::move(key),continuation));}else{if(is("orderby")){take();auto key=expression();if(is("ascending"))take();else if(is("descending")){take();descending=true;}if(canonical(key->token.text)!=canonical(range.text) || key->kind!=Expr::name)fail("AI3107","The bootstrap query orderby key must be the range variable.",key->token.at);}expect("select");auto projection=expression();e->kind=Expr::query_select;e->token=t;e->op="$query_select$"+std::to_string(query_counter++);e->children.push_back(std::move(source));pending_queries.push_back(make_query_helper(e->op,range,t,std::move(projection),descending,false,nullptr,""));}}}
        else if(t.text=="await"){e->kind=Expr::await_value;e->children.push_back(primary());}
        else if(t.text=="(" ) { Group group(grouping);e=expression(); expect(")"); }
        else if(t.text=="checked" || t.text=="unchecked") {
            e->kind=Expr::overflow_context;e->op=t.text;expect("(");OverflowScope mode(wraps,t.text=="unchecked");e->children.push_back(expression());expect(")");
        }
        else if(t.text=="&" || t.text=="*") {
            e->kind=t.text=="&" ? Expr::borrow : Expr::dereference;e->op=t.text=="&" && eat("mut") ? "exclusive" : "shared";e->children.push_back(primary());
        }
        else if(t.text=="+" || t.text=="-" || t.text=="!" || t.text=="~" || t.text=="++" || t.text=="--") {
            e->kind=(t.text=="++" || t.text=="--") ? Expr::update : Expr::unary; e->op=t.text; e->children.push_back(primary());
        } else if(t.text=="sizeof") {
            expect("(");
            if(current().kind==Token::id && product_builder.find(canonical(current().text))>=0){
                auto name=take();
                e->sizeof_shape=NativeTypeShape{Type::product,product_builder.find(canonical(name.text)),{}};
            }else{
                e->sizeof_shape=product_builder.resolve(type_expression());
            }
            expect(")");
            e->kind=Expr::sizeof_type;
        } else if(t.text=="typeof") {
            expect("(");
            if(current().text=="nint" || current().text=="nuint"){
                auto name=take();
                e->op=name.text;
            }else if(current().kind==Token::id && product_builder.find(canonical(current().text))>=0){
                auto name=take();
                e->sizeof_shape=NativeTypeShape{Type::product,product_builder.find(canonical(name.text)),{}};
            }else{
                e->sizeof_shape=product_builder.resolve(type_expression());
            }
            expect(")");
            e->kind=Expr::typeof_type;
        } else if(t.text=="stackalloc") {
            auto element=take();expect("[");e->children.push_back(expression());expect("]");
            e->kind=Expr::stack_alloc;e->op=canonical(element.text);e->token=t;
        } else if(t.text=="new") {
            e->kind=Expr::unary;e->op="new";e->children.push_back(primary());
        } else if(t.text=="default") {
            e->kind=Expr::default_value;
            if(eat("(")){e->sizeof_shape=product_builder.resolve(type_expression());expect(")");e->op="typed";}
        } else if(t.kind==Token::string) {e->kind=Expr::literal;e->type=Type::string;e->op=t.text;
        } else if(t.text=="null") {
            e->kind=Expr::literal;e->type=Type::string;e->null_literal=true;
        } else if(t.text=="nameof") {
            expect("(");
            if(current().kind!=Token::id)fail("AI3106","nameof requires an identifier symbol name.",current().at);
            auto first=take();std::string symbol=canonical(first.text);
            if(eat(".")){auto member=identifier();symbol=canonical(member.text);}
            if(is("<")){int name_depth=0;for(;;){if(current().kind==Token::end)fail("AI3100","Unterminated nameof generic name.",first.at);auto part=take();if(part.text=="<")++name_depth;else if(part.text==">"){--name_depth;if(name_depth==0)break;}else if(part.text.empty() || part.kind==Token::number || part.kind==Token::string)fail("AI3107","Unbound generic nameof names must use type identifiers.",part.at);}}
            expect(")");
            e->kind=Expr::nameof_sym;e->token=first;e->type=Type::string;e->op="\""+symbol+"\"";
        } else if(t.kind==Token::character) {
            e->kind=Expr::literal;
            auto primitive=native_integer_spelling("char");
            if(!primitive)fail("AI3100","Character primitive is unavailable.",t.at);
            e->type=Type(*primitive);
            e->value=t.value;
            e->constant=Constant{e->type,e->value};
        } else if(t.kind==Token::number) {
            e->kind=Expr::literal;
            std::string_view spelling=t.text;
            const bool hex_or_binary = spelling.starts_with("0x") || spelling.starts_with("0X") ||
                spelling.starts_with("0b") || spelling.starts_with("0B");
            const bool floating_span = !hex_or_binary && (spelling.find('.')!=std::string_view::npos ||
                spelling.find('e')!=std::string_view::npos || spelling.find('E')!=std::string_view::npos);
            const char last_char = spelling.empty() ? '\0' : spelling.back();
            const bool single_suffix = !hex_or_binary && (last_char=='f' || last_char=='F');
            const bool double_suffix = !hex_or_binary && (last_char=='d' || last_char=='D');
            const bool decimal_suffix = !hex_or_binary && (last_char=='m' || last_char=='M');
            if(decimal_suffix){
                std::string_view decimal_text=spelling;decimal_text.remove_suffix(1);
                if(decimal_text.find('e')!=std::string_view::npos || decimal_text.find('E')!=std::string_view::npos)
                    fail("AI3107","A Decimal literal cannot carry an exponent in the native bootstrap.",t.at);
                std::string clean(decimal_text);clean.erase(std::remove(clean.begin(),clean.end(),'_'),clean.end());
                ai_decimal value{};
                if(clean.empty() || ai_dec_core_from_text(clean.c_str(),&value)!=0)
                    fail("AI3107","Decimal literal is outside the 96-bit System.Decimal range.",t.at);
                e->type=Type::decimal;
                e->value=static_cast<std::int64_t>((static_cast<std::uint64_t>(value.w1)<<32)|value.w0);
                e->constant=Constant{e->type,e->value,static_cast<std::int64_t>((static_cast<std::uint64_t>(value.flags)<<32)|value.w2)};
            }
            else if(floating_span || single_suffix || double_suffix){
                std::string_view digits_spelling=spelling;
                if(single_suffix || double_suffix)digits_spelling.remove_suffix(1);
                if(digits_spelling.ends_with('_'))fail("AI3107","A floating literal cannot end with a digit separator.",t.at);
                std::string clean(digits_spelling);
                clean.erase(std::remove(clean.begin(),clean.end(),'_'),clean.end());
                if(clean.empty())fail("AI3107","Invalid floating literal.",t.at);
                if(clean.front()=='.')clean.insert(clean.begin(),'0');
                if(clean.back()=='.')clean.push_back('0');
                double parsed=0;
                const auto conv=std::from_chars(clean.data(),clean.data()+clean.size(),parsed);
                if(conv.ec!=std::errc{} || conv.ptr!=clean.data()+clean.size() || !std::isfinite(parsed))
                    fail("AI3107","Floating literal is outside the Double range.",t.at);
                if(single_suffix){
                    const float value=static_cast<float>(parsed);
                    if(!std::isfinite(static_cast<double>(value)))
                        fail("AI3107","Floating literal is outside the Single range.",t.at);
                    e->type=Type::float32;
                    e->value=static_cast<std::int64_t>(std::bit_cast<std::uint32_t>(value));
                }else{
                    e->type=Type::float64;
                    e->value=static_cast<std::int64_t>(std::bit_cast<std::uint64_t>(parsed));
                }
                e->constant=Constant{e->type,e->value};
            }else{
            char suffix=0;
            if(!spelling.empty() && (spelling.back()=='u' || spelling.back()=='U')){suffix='u';spelling.remove_suffix(1);}
            else if(!spelling.empty() && (spelling.back()=='L' || spelling.back()=='l')){suffix='l';spelling.remove_suffix(1);}
            if(!spelling.empty() && (spelling.back()=='u' || spelling.back()=='U' || spelling.back()=='L' || spelling.back()=='l')){
                const char second=spelling.back();
                spelling.remove_suffix(1);
                const bool first_unsigned=suffix=='u';
                if((first_unsigned && (second=='l' || second=='L')) || (!first_unsigned && (second=='u' || second=='U')))suffix='2';
                else fail("AI3107","Literal has an unsupported suffix.",t.at);
            }
            const bool wide=suffix=='l';
            const bool ulong_literal=suffix=='2';
            e->type=ulong_literal ? Type::unsigned_long_integer : suffix=='u' ? Type::unsigned_integer : wide ? Type::long_integer : Type::integer;
            int base=10;
            // Select the radix from the source spelling before removing separators.
            if(spelling.starts_with("0x") || spelling.starts_with("0X")){base=16;spelling.remove_prefix(2);}
            else if(spelling.starts_with("0b") || spelling.starts_with("0B")){base=2;spelling.remove_prefix(2);}
            if(spelling.ends_with('_'))fail("AI3107",suffix=='u' ? "A UInt32 literal cannot end with a digit separator." : wide ? "An Int64 literal cannot end with a digit separator." : "An Int32 literal cannot end with a digit separator.",t.at);
            std::string digits(spelling);
            digits.erase(std::remove(digits.begin(),digits.end(),'_'),digits.end());
            std::uint64_t magnitude=0;
            const auto converted=std::from_chars(digits.data(),digits.data()+digits.size(),magnitude,base);
            constexpr std::uint64_t long_minimum_magnitude=std::uint64_t{1}<<63;
            const std::uint64_t limit=ulong_literal || (suffix=='u' && magnitude>4294967295ULL) ? UINT64_MAX :
                                       suffix=='u' ? 4294967295ULL : wide ? long_minimum_magnitude : 2147483648ULL;
            const char* message=ulong_literal || (suffix=='u' && magnitude>4294967295ULL) ? "Literal is outside the UInt64 range or has an unsupported suffix." :
                                suffix=='u' ? "Literal is outside the UInt32 range or has an unsupported suffix." :
                                wide ? "Literal is outside the Int64 range or has an unsupported suffix." :
                                       "Literal is outside the Int32 bootstrap range or has an unsupported suffix.";
            if(digits.empty() || converted.ec!=std::errc{} || converted.ptr!=digits.data()+digits.size() || magnitude>limit)
                fail("AI3107",message,t.at);
            e->long_minimum_magnitude=wide && magnitude==long_minimum_magnitude;
            if(suffix=='u' && magnitude>4294967295ULL)e->type=Type::unsigned_long_integer;
            if(!e->long_minimum_magnitude)e->value=static_cast<std::int64_t>(magnitude);
            }
        } else if(t.text=="true" || t.text=="false") { e->kind=Expr::literal; e->type=Type::boolean; e->value=t.text=="true"; }
        else if(t.kind==Token::id && (t.text.starts_with('@') || !reserved(t.text) || t.text=="decimal")) { e->kind=Expr::name;if(auto aliased=product_builder.alias(canonical(t.text));aliased && aliased->references.empty() && aliased->primitive==4)e->nominal=aliased->product; }
        else fail("AI3100","Expression is not implemented by the native bootstrap compiler.",t.at);
        std::optional<Location> first_member;
        for(;;){
            bool adjacent=grouping>0 || current().at.line==tokens[pos-1].at.line;
            if(adjacent && eat("(")){
                Group group(grouping);
                if(e->kind==Expr::name)e->kind=Expr::call;
                else if(e->kind==Expr::field){
                    int id=-1;
                    if(e->children[0]->kind==Expr::name){auto& root=*e->children[0];id=root.nominal>=0 ? root.nominal : product_builder.find(canonical(root.token.text));if(id>=0){e->qualifier=root.token;e->explicit_qualifier=root.nominal>=0;e->nominal=id;e->kind=Expr::call;e->op="variant-call";e->children.clear();}}
                    if(id<0){auto base=std::move(e->children[0]);e->kind=Expr::call;e->op="member-call";e->children.clear();e->children.push_back(std::move(base));}
                }
                else fail("AI3100","Only a named function or declared case can be called in this capability.",first_member.value_or(e->token.at));
                if(!is(")"))do{e->children.push_back(expression());}while(eat(","));expect(")");first_member.reset();
            }else if(adjacent && is(".")){auto dot=take();if(!first_member)first_member=dot.at;auto f=std::make_unique<Expr>();f->kind=Expr::field;f->token=take();f->wraps=wraps;f->children.push_back(std::move(e));e=std::move(f);}
            else if(e->kind==Expr::name && product_builder.find(canonical(e->token.text))>=0 && is("{")){
                take();e->kind=Expr::initializer;
                while(!is("}")){auto field=identifier();expect("=");e->with_updates.emplace_back(field,expression());if(e->with_updates.size()>128)fail("AI3100","Native initializer field limit exceeded.",field.at);if(!eat(",") && !is("}") && current().at.line==tokens[pos-1].at.line)fail("AI3100","Expected an initializer field separator.",current().at);}
                expect("}");
            }
            else if(adjacent && is("with")){
                auto with_token=take();auto node=std::make_unique<Expr>();node->kind=Expr::with_expr;node->token=with_token;node->children.push_back(std::move(e));expect("{");
                while(!is("}")){auto field=identifier();expect("=");node->with_updates.emplace_back(field,expression());if(node->with_updates.size()>128)fail("AI3100","Native with-update limit exceeded.",field.at);if(!eat(",") && !is("}") && current().at.line==tokens[pos-1].at.line)fail("AI3100","Expected a with-update separator.",current().at);}
                expect("}");e=std::move(node);
            }
            else if(adjacent && eat("[")){auto node=std::make_unique<Expr>();node->kind=Expr::index;node->token=tokens[pos-1];node->children.push_back(std::move(e));node->children.push_back(expression());expect("]");e=std::move(node);}
            else if(adjacent && is("?")){auto p=std::make_unique<Expr>();p->kind=Expr::propagate;p->token=take();first_member.reset();p->wraps=wraps;p->children.push_back(std::move(e));e=std::move(p);}
            else if(is("++") || is("--")){auto u=std::make_unique<Expr>();u->kind=Expr::update;u->token=take();u->op=u->token.text;u->postfix=true;u->wraps=wraps;u->children.push_back(std::move(e));e=std::move(u);}
            else break;
            e->height=1;for(auto& child:e->children)e->height=std::max(e->height,child->height+1);if(e->height>128)fail("AI3100","Native bootstrap expression depth limit exceeded.",e->token.at);
        }
        for(auto& child:e->children)e->height=std::max(e->height,child->height+1);if(e->height>128)fail("AI3100","Native bootstrap expression depth limit exceeded.",e->token.at);
        return e;
    }
    E expression(int min=1) {
        Depth guard(*this); auto left=primary();
        while(precedence(current().text)>=min) {
            if(grouping==0 && current().at.line>tokens[pos-1].at.line && (is("*") || is("&")))break;
            auto op=take();int rank=precedence(op.text);auto parent=std::make_unique<Expr>();parent->token=op;parent->wraps=wraps;
            if(op.text=="as"){parent->kind=Expr::as_test;parent->op=op.text;parent->children.push_back(std::move(left));parent->sizeof_shape=type_shape();parent->height=parent->children[0]->height+1;left=std::move(parent);continue;}
            parent->kind=op.text=="is" ? Expr::is_test : (rank==1 ? Expr::assign : Expr::binary);parent->op=op.text;parent->children.push_back(std::move(left));parent->children.push_back(expression(rank==1 ? rank : rank+1));parent->height=std::max(parent->children[0]->height,parent->children[1]->height)+1;if(parent->height>128)fail("AI3100","Native bootstrap expression depth limit exceeded.",op.at);left=std::move(parent);
        }
        return left;
    }
    S local() {
        auto s=std::make_unique<Stmt>();s->kind=Stmt::local;bool static_binding=false;if(is("static") && pos+1<tokens.size() && (tokens[pos+1].text=="var" || tokens[pos+1].text=="let" || tokens[pos+1].text=="const" || tokens[pos+1].text=="readonly")){take();static_binding=true;}bool readonly_binding=eat("readonly");if(eat("var"))s->mutable_=!readonly_binding;else{s->constant=eat("const");if(!s->constant)expect("let");}if(static_binding){if(s->constant || !s->mutable_)fail("AI3107","A static binding must be declared with var.",current().at);s->static_binding=true;}s->token=identifier();
        if(eat(":")){bool scoped=eat("scoped");s->annotation=type_shape();if(s->annotation->primitive==2 && s->annotation->references.empty())fail("AI3107","A local must have a value type.",s->token.at);s->scoped_ref=scoped;}if(eat("="))s->expr=expression();
        if(!s->expr && (!s->mutable_ || !s->annotation))fail("AI3100","A declaration needs an initializer or an explicit mutable type.",s->token.at);
        return s;
    }
    S statement() {
        Depth guard(*this);auto s=std::make_unique<Stmt>();s->token=current();
        if(current().kind==Token::id && pos+1<tokens.size() && tokens[pos+1].text==":" && !is(":")){
            auto name=take();expect(":");s->kind=Stmt::label;s->token=name;end_statement();
        }
        else if(eat("goto")){s->kind=Stmt::goto_jump;s->token=tokens[pos-1];auto name=identifier();s->op=canonical(name.text);end_statement();}
        else if(eat("try")){
            s->kind=Stmt::try_block;s->token=tokens[pos-1];s->children.push_back(statement());
            if(eat("catch")){s->has_catch=true;if(eat("(")){s->catch_name=identifier();expect(")");}if(eat("when")){if(!s->catch_name.text.size())fail("AI3107","A catch filter requires a catch binding.",s->token.at);s->catch_filter=expression();}s->children.push_back(statement());}
            if(eat("finally")){s->has_finally=true;s->children.push_back(statement());}
            if(!s->has_catch && !s->has_finally)fail("AI3107","A try statement requires a catch or finally clause.",s->token.at);
        }
        else if(eat("lock")){
            auto keyword=tokens[pos-1];
            expect("(");auto declaration=local();expect(")");declaration->lock_binding=true;auto body=statement();
            s->kind=Stmt::block;s->token=keyword;s->children.push_back(std::move(declaration));s->children.push_back(std::move(body));
        }
        else if(is("await")){
            fail("AI3107","async/await iteration is not implemented by the native bootstrap.",current().at);
        }
        else if(eat("foreach")){
            auto keyword=tokens[pos-1];static int foreach_counter=0;
            expect("(");
            if(eat("ref"))fail("AI3107","By-reference iteration is not implemented by the native bootstrap.",tokens[pos-1].at);
            if(is("let") || is("var"))take();
            auto element=identifier();
            expect("in");auto iterable=expression();expect(")");auto body=statement();
            const std::string suffix=std::to_string(foreach_counter++);
            auto name_expr=[&](const Token& token){auto expression=std::make_unique<Expr>();expression->kind=Expr::name;expression->token=token;return expression;};
            auto literal_expr=[&](std::int64_t value){auto expression=std::make_unique<Expr>();expression->kind=Expr::literal;expression->token=keyword;expression->type=Type::integer;expression->value=value;expression->constant=Constant{Type::integer,value};return expression;};
            auto call_expr=[&](const Token& callee,std::vector<E> arguments){auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=callee;call->children=std::move(arguments);return call;};
            const Token iter_token{"$iter"+suffix,keyword.at,Token::id};
            const Token index_token{"$index"+suffix,keyword.at,Token::id};
            auto iter_decl=std::make_unique<Stmt>();iter_decl->kind=Stmt::local;iter_decl->token=iter_token;iter_decl->expr=std::move(iterable);
            auto index_decl=std::make_unique<Stmt>();index_decl->kind=Stmt::local;index_decl->mutable_=true;index_decl->token=index_token;index_decl->expr=literal_expr(0);
            std::vector<E> size_arguments;size_arguments.push_back(name_expr(iter_token));
            auto condition=std::make_unique<Expr>();condition->kind=Expr::binary;condition->op=">";condition->token=keyword;condition->children.push_back(call_expr(Token{"bufferSize",keyword.at,Token::id},std::move(size_arguments)));condition->children.push_back(name_expr(index_token));
            std::vector<E> peek_arguments;peek_arguments.push_back(name_expr(iter_token));peek_arguments.push_back(name_expr(index_token));
            auto element_decl=std::make_unique<Stmt>();element_decl->kind=Stmt::local;element_decl->token=element;element_decl->expr=call_expr(Token{"bufferPeek",keyword.at,Token::id},std::move(peek_arguments));
            auto increment_expr=std::make_unique<Expr>();increment_expr->kind=Expr::assign;increment_expr->op="+=";increment_expr->token=index_token;increment_expr->children.push_back(name_expr(index_token));increment_expr->children.push_back(literal_expr(1));
            auto increment=std::make_unique<Stmt>();increment->kind=Stmt::expression;increment->token=index_token;increment->expr=std::move(increment_expr);
            auto loop_body=std::make_unique<Stmt>();loop_body->kind=Stmt::block;loop_body->token=keyword;loop_body->children.push_back(std::move(element_decl));loop_body->children.push_back(std::move(increment));loop_body->children.push_back(std::move(body));
            auto loop=std::make_unique<Stmt>();loop->kind=Stmt::loop;loop->token=keyword;loop->expr=std::move(condition);loop->children.push_back(std::move(loop_body));
            s->kind=Stmt::block;s->token=keyword;s->children.push_back(std::move(iter_decl));s->children.push_back(std::move(index_decl));s->children.push_back(std::move(loop));
        }
        else if(eat("switch")){
            auto keyword=tokens[pos-1];static int switch_counter=0;expect("(");auto subject=expression();expect(")");expect("{");
            struct SwitchGroup{std::vector<E> labels;std::vector<S> body;bool default_group=false;Location at{};
                explicit SwitchGroup(Location at):at(at){} };
            std::vector<SwitchGroup> groups;SwitchGroup group(keyword.at);std::map<long long,Location> literals;bool has_default=false;
            auto constant_label=[](auto&& self,const Expr& value)->bool{
                if(value.kind==Expr::literal)return value.type.is_integer() || value.type.is_char() || value.type==Type::boolean;
                if(value.kind==Expr::unary && value.op=="-" && value.children.size()==1)return self(self,*value.children[0]);
                if(value.kind==Expr::field)return true;
                return false;
            };
            auto literal=[](auto&& self,const Expr& value)->std::optional<long long>{
                if(value.kind==Expr::literal && (value.type.is_integer() || value.type.is_char()))return value.value;
                if(value.kind==Expr::literal && value.type==Type::boolean)return value.value ? 1 : 0;
                if(value.kind==Expr::unary && value.op=="-" && value.children.size()==1){auto inner=self(self,*value.children[0]);if(inner)return -*inner;}
                return std::nullopt;
            };
            auto body_terminates=[](const Stmt& statement){return statement.kind==Stmt::stop || statement.kind==Stmt::next || statement.kind==Stmt::result || statement.kind==Stmt::failure;};
            auto close_group=[&](){
                if(group.body.empty()){if(!group.default_group)fail("AI3107","A switch case cannot fall through to the switch end.",group.at);}
                else if(!group.default_group && !body_terminates(*group.body.back()))fail("AI3107","A switch case must end with break, continue, return or fail.",group.body.back()->token.at);
                groups.push_back(std::move(group));group=SwitchGroup(keyword.at);
            };
            auto finish=[&](){
                if(group.labels.empty() && !group.default_group)return;
                close_group();
            };
            while(!is("}")){
                if(current().kind==Token::end)fail("AI3100","Unterminated native switch statement.",keyword.at);
                if(eat("case")){
                    auto label=expression();expect(":");
                    if(!group.body.empty())close_group();
                    if(!constant_label(constant_label,*label))fail("AI3107","A switch case label requires a constant literal or native enum member.",label->token.at);
                    if(auto constant=literal(literal,*label);constant){auto [it,added]=literals.emplace(*constant,label->token.at);if(!added){Diagnostic d("AI3107","Duplicate native switch case value.",label->token.at);d.related.push_back({"previous-case",it->second});throw d;}}
                    if(group.labels.empty())group.at=label->token.at;
                    group.labels.push_back(std::move(label));
                }else if(eat("default")){
                    if(has_default)fail("AI3107","A switch statement can have only one default label.",tokens[pos-1].at);
                    has_default=true;expect(":");
                    if(!group.body.empty())close_group();
                    group.default_group=true;group.labels.clear();
                }else{
                    group.body.push_back(statement());
                }
            }
            expect("}");finish();
            auto tmp=std::make_unique<Stmt>();tmp->kind=Stmt::local;tmp->switch_control=true;tmp->token=Token{"$switch"+std::to_string(switch_counter++),keyword.at,Token::id};tmp->expr=std::move(subject);
            std::vector<std::unique_ptr<Stmt>> children;children.push_back(std::move(tmp));
            auto make_condition=[&](std::vector<E>& labels){
                std::unique_ptr<Expr> condition;
                for(auto& label:labels){
                    auto tmp_name=std::make_unique<Expr>();tmp_name->kind=Expr::name;tmp_name->token=children[0]->token;
                    auto comparison=std::make_unique<Expr>();comparison->kind=Expr::binary;comparison->op="==";comparison->token=label->token;comparison->children.push_back(std::move(tmp_name));comparison->children.push_back(std::move(label));
                    if(!condition)condition=std::move(comparison);
                    else{auto either=std::make_unique<Expr>();either->kind=Expr::binary;either->op="||";either->token=condition->token;either->children.push_back(std::move(condition));either->children.push_back(std::move(comparison));condition=std::move(either);}
                }
                return condition;
            };
            std::unique_ptr<Stmt> default_body;
            std::vector<std::size_t> case_groups;
            for(std::size_t index=0;index<groups.size();++index){
                auto& entry=groups[index];
                if(entry.default_group){if(!entry.body.empty() && entry.body.back()->kind==Stmt::stop)entry.body.pop_back();default_body=std::make_unique<Stmt>();default_body->kind=Stmt::block;default_body->token=keyword;default_body->children=std::move(entry.body);continue;}
                if(!entry.body.empty() && entry.body.back()->kind==Stmt::stop)entry.body.pop_back();
                case_groups.push_back(index);
            }
            if(case_groups.empty() && !default_body)fail("AI3107","A switch statement requires at least one case.",keyword.at);
            std::unique_ptr<Stmt> tail=std::move(default_body);
            for(std::size_t position=case_groups.size();position-->0;){
                auto& entry=groups[case_groups[position]];
                auto branch=std::make_unique<Stmt>();branch->kind=Stmt::branch;branch->token=keyword;branch->expr=make_condition(entry.labels);
                auto body=std::make_unique<Stmt>();body->kind=Stmt::block;body->token=keyword;body->children=std::move(entry.body);branch->children.push_back(std::move(body));
                if(tail)branch->children.push_back(std::move(tail));
                tail=std::move(branch);
            }
            children.push_back(std::move(tail));
            s->kind=Stmt::block;s->token=keyword;s->children=std::move(children);
        }

        else if(eat("using")){
            auto keyword=tokens[pos-1];static int resource_counter=0;
            if(eat("(")){
                std::vector<std::unique_ptr<Stmt>> children;
                if(is("let") || is("var") || is("const")){
                    do{auto declaration=local();declaration->using_resource=true;children.push_back(std::move(declaration));}while(eat(","));
                }else{
                    auto declaration=std::make_unique<Stmt>();declaration->kind=Stmt::local;declaration->using_resource=true;declaration->token=Token{"$using"+std::to_string(resource_counter++),keyword.at,Token::id};declaration->expr=expression();children.push_back(std::move(declaration));
                }
                expect(")");children.push_back(statement());
                s->kind=Stmt::block;s->token=keyword;s->children=std::move(children);
            }else{
                auto name=identifier();
                auto declaration=std::make_unique<Stmt>();declaration->kind=Stmt::local;declaration->using_resource=true;declaration->token=Token{"$using"+std::to_string(resource_counter++),keyword.at,Token::id};declaration->expr=std::make_unique<Expr>();declaration->expr->kind=Expr::name;declaration->expr->token=name;
                s->kind=Stmt::block;s->token=keyword;s->children.push_back(std::move(declaration));s->children.push_back(statement());
            }
        }
        else if(is("unsafe") && tokens[pos+1].text=="{"){auto keyword=take();s->kind=Stmt::block;s->token=keyword;expect("{");while(!is("}")){if(current().kind==Token::end)fail("AI3100","Unterminated unsafe block.",current().at);s->children.push_back(statement());}expect("}");}
        else if(eat("{")) { s->kind=Stmt::block;while(!is("}")){if(current().kind==Token::end)fail("AI3100","Unterminated block.",current().at);s->children.push_back(statement());}expect("}"); }
        else if((is("checked") || is("unchecked")) && tokens[pos+1].text=="{"){bool next_mode=take().text=="unchecked";OverflowScope mode(wraps,next_mode);s=statement();}
        else if(is("let") || is("var") || is("const") || is("readonly") || is("static")){s=local();end_statement();}
        else if(is("volatile"))fail("AI3107","volatile is only allowed on native product fields.",current().at);
        else if(eat("fail")){s->kind=Stmt::failure;if(!is("}") && !is(";") && current().at.line==s->token.at.line)s->expr=expression();end_statement();}
        else if(eat("throw")){s->kind=Stmt::failure;s->token=tokens[pos-1];if(is("}") || is(";") || current().at.line!=s->token.at.line)fail("AI3107","throw requires a failure value.",s->token.at);s->expr=expression();end_statement();}
        else if(eat("fixed")){expect("(");auto operand=expression();expect(")");auto pin=std::make_unique<Expr>();pin->kind=Expr::fixed_pin;pin->token=s->token;pin->children.push_back(std::move(operand));auto pin_statement=std::make_unique<Stmt>();pin_statement->kind=Stmt::expression;pin_statement->token=s->token;pin_statement->expr=std::move(pin);if(!is("{"))fail("AI3107","A fixed block requires a structured body.",current().at);s->kind=Stmt::block;s->children.push_back(statement());(void)pin_statement;}
            else if(eat("yield")){body_has_yield=true;if(eat("return")){s->kind=Stmt::yield_return;s->expr=expression();end_statement();}else if(eat("break")){s->kind=Stmt::yield_break;end_statement();}else fail("AI3107","yield requires return or break.",s->token.at);}
            else if(eat("return")){s->kind=Stmt::result;if(!is("}") && !is(";") && current().at.line==s->token.at.line)s->expr=expression();end_statement();}
        else if(eat("if")){s->kind=Stmt::branch;expect("(");s->expr=expression();expect(")");s->children.push_back(statement());if(eat("else"))s->children.push_back(statement());}
        else if(eat("while")){s->kind=Stmt::loop;expect("(");s->expr=expression();expect(")");s->children.push_back(statement());}
        else if(eat("do")){s->kind=Stmt::do_loop;s->children.push_back(statement());expect("while");expect("(");s->expr=expression();expect(")");end_statement();}
        else if(eat("for")) {
            s->kind=Stmt::for_loop;expect("(");
            if(is("let") || is("var"))s->children.push_back(local());else {auto init=std::make_unique<Stmt>();init->kind=Stmt::expression;init->token=current();if(!is(";"))init->expr=expression();s->children.push_back(std::move(init));}expect(";");
            if(!is(";"))s->expr=expression();expect(";");if(!is(")"))s->update=expression();expect(")");s->children.push_back(statement());
        } else if(eat("break")){s->kind=Stmt::stop;end_statement();}
        else if(eat("continue")){s->kind=Stmt::next;end_statement();}
        else {s->kind=Stmt::expression;s->expr=expression();end_statement();}
        s->end=tokens[pos-1].at;return s;
    }
    std::string module_header(){expect("module");auto name=canonical(identifier().text);while(eat("."))name+="."+canonical(identifier().text);end_statement();expect("intent");if(take().kind!=Token::string)fail("AI3100","Expected an intent string.",tokens[pos-1].at);end_statement();return name;}
    void rewrite_yields(Stmt& body,const Token& at){
        auto name=[&](const std::string& text){auto value=std::make_unique<Expr>();value->kind=Expr::name;value->token=Token{text,at.at,Token::id};return value;};
        auto literal=[&](std::int64_t value){auto item=std::make_unique<Expr>();item->kind=Expr::literal;item->token=Token{"$y",at.at,Token::id};item->type=Type::integer;item->value=value;item->constant=Constant{Type::integer,value,0};return item;};
        auto call=[&](const std::string& text,std::vector<std::unique_ptr<Expr>> args){auto item=std::make_unique<Expr>();item->kind=Expr::call;item->token=Token{text,at.at,Token::id};item->children=std::move(args);return item;};
        auto one=[&](std::unique_ptr<Expr> a){std::vector<std::unique_ptr<Expr>> v;v.push_back(std::move(a));return v;};
        auto three=[&](std::unique_ptr<Expr> a,std::unique_ptr<Expr> b,std::unique_ptr<Expr> c){std::vector<std::unique_ptr<Expr>> v;v.push_back(std::move(a));v.push_back(std::move(b));v.push_back(std::move(c));return v;};
        auto borrow=[&](std::unique_ptr<Expr> child,bool exclusive){auto item=std::make_unique<Expr>();item->kind=Expr::borrow;item->op=exclusive ? "exclusive" : "shared";item->token=Token{"&",at.at,Token::id};item->children.push_back(std::move(child));return item;};
        auto local=[&](const std::string& text,std::unique_ptr<Expr> value,NativeTypeShape annotation,bool mutable_){auto stmt=std::make_unique<Stmt>();stmt->kind=Stmt::local;stmt->token=Token{text,at.at,Token::id};stmt->expr=std::move(value);stmt->annotation=annotation;stmt->mutable_=mutable_;return stmt;};
        auto assign=[&](const std::string& target,std::unique_ptr<Expr> value){auto item=std::make_unique<Expr>();item->kind=Expr::assign;item->op="=";item->token=Token{"$yi",at.at,Token::id};item->children.push_back(name(target));item->children.push_back(std::move(value));auto stmt=std::make_unique<Stmt>();stmt->kind=Stmt::expression;stmt->token=Token{"$yis",at.at,Token::id};stmt->expr=std::move(item);return stmt;};
        std::function<void(Stmt&)> visit=[&](Stmt& stmt){
            if(stmt.kind==Stmt::yield_return){auto write=std::make_unique<Stmt>();write->kind=Stmt::expression;write->token=stmt.token;write->expr=call("bufferWrite",three(borrow(name("$yield_out"),true),name("$yield_index"),std::move(stmt.expr)));auto inc=assign("$yield_index",call("buffer",one(name("$yield_index")))); /* placeholder replaced below */ auto increment_value=std::make_unique<Expr>();increment_value->kind=Expr::binary;increment_value->op="+";increment_value->token=stmt.token;increment_value->children.push_back(name("$yield_index"));increment_value->children.push_back(literal(1));auto inc_stmt=assign("$yield_index",std::move(increment_value));auto block=std::make_unique<Stmt>();block->kind=Stmt::block;block->token=stmt.token;block->children.push_back(std::move(write));block->children.push_back(std::move(inc_stmt));stmt.kind=Stmt::block;stmt.children.clear();stmt.children.push_back(std::move(block->children[0]));stmt.children.push_back(std::move(block->children[1]));return;}
            if(stmt.kind==Stmt::yield_break){stmt.kind=Stmt::result;stmt.token=Token{"return",at.at,Token::id};stmt.expr=name("$yield_out");stmt.children.clear();return;}
            for(auto& child:stmt.children)if(child)visit(*child);
        };
        auto block=std::make_unique<Stmt>();block->kind=Stmt::block;block->token=at;
        block->children.push_back(local("$yield_out",call("buffer",one(literal(64))),NativeTypeShape{Type::buffer,-1,{}},true));block->children.push_back(local("$yield_index",literal(0),NativeTypeShape{Type::integer,-1,{}},true));
        if(body.kind==Stmt::block){visit(body);for(auto& child:body.children)block->children.push_back(std::move(child));}else{visit(body);block->children.push_back(std::make_unique<Stmt>(std::move(body)));}
        auto result=std::make_unique<Stmt>();result->kind=Stmt::result;result->token=Token{"return",at.at,Token::id};result->expr=name("$yield_out");block->children.push_back(std::move(result));body=std::move(*block);
    }
    void skip_product(){auto found=product_declarations.find(current().at.offset);if(found==product_declarations.end())fail("AI3100","Native product declaration lost its source identity.",current().at);auto end=std::find_if(tokens.begin(),tokens.end(),[&](auto& token){return token.at.offset==found->second;});if(end==tokens.end())fail("AI3100","Native product declaration end lost its source identity.",current().at);pos=static_cast<std::size_t>(end-tokens.begin());end_statement();}
public:
    explicit Parser(std::string_view source,SourceOrigin origin=SourceOrigin::input) : tokens(Lexer(source,origin).run()) {}
    std::vector<NativeGenericDefinition> core_schema(){
        if(module_header()!="std.core")fail("AI3100","Invalid core outcome module identity.",{});gather_products(false);while(is("type"))skip_product();if(current().kind!=Token::end || product_declarations.size()!=2 || product_builder.count()!=0)fail("AI3100","Invalid core outcome declaration document.",{});int option=product_builder.find_generic("Option"),result=product_builder.find_generic("Result");if(option<0 || result<0)fail("AI3100","Missing core outcome declaration.",{});return {product_builder.generic(option),product_builder.generic(result)};
    }
    std::vector<Function> run() {
        module_header();bool unwind=false;Location fault_mode_location;
        if(is("faults")){fault_mode_location=take().at;auto mode=take();if(mode.text!="abort" && mode.text!="unwind")fail("AI3134","Expected abort or unwind after faults.",mode.at);unwind=mode.text=="unwind";end_statement();if(is("faults"))fail("AI3134","A module has exactly one fault mode declaration.",current().at);}
        gather_products();std::vector<Function> functions;
        while(current().kind!=Token::end) {
            if(is("managed") || is("unmanaged") || is("delegate")){while(current().kind!=Token::end && current().text!=";")take();if(current().text==";")take();continue;}
            if(is("ref") && pos+1<tokens.size() && (tokens[pos+1].text=="struct" || tokens[pos+1].text=="record")){skip_product();continue;}
            if((is("abstract") && pos+1<tokens.size() && tokens[pos+1].text=="class") || is("interface")){skip_product();continue;}if(is("type") || is("enum") || is("alias") || is("partial") || is("file") || is("sealed") || is("struct") || is("record") || is("class")){skip_product();continue;}
            if(is("namespace")){take();identifier();end_statement();continue;}
            if(is("unsafe")){auto at=current().at;if(tokens[pos+1].text!="fn")fail("AI3107","unsafe is only supported on native functions and statement blocks in the bootstrap.",at);take();continue;}
            if(eat("extern")){
                Function f;f.unwind=unwind;f.fault_mode_location=fault_mode_location;f.external=true;
                expect("fn");auto name=identifier();f.token=name;f.external_name=canonical(name.text);expect("(");
                if(!is(")"))do{auto pname=identifier();expect(":");auto shape=type_shape();if(shape.primitive==2 && shape.references.empty())fail("AI3107","Unit extern parameters carry no value.",pname.at);Parameter p{};p.token=pname;p.shape=shape;f.parameters.push_back(std::move(p));}while(eat(","));
                expect(")");expect("->");f.return_shape=type_shape();f.success_shape=f.return_shape;end_statement();
                functions.push_back(std::move(f));if(functions.size()>native_function_limit)fail("AI3100","Native bootstrap function limit exceeded.",current().at);continue;
            }
            if(is("implicit") || is("explicit")){
                bool implicit_conversion=eat("implicit");if(!implicit_conversion)expect("explicit");expect("operator");
                auto keyword=tokens[pos-2];Function f;f.unwind=unwind;f.fault_mode_location=fault_mode_location;
                expect("(");
                if(is(")"))fail("AI3107","A conversion operator requires exactly one parameter.",current().at);
                auto name=identifier();expect(":");auto source=type_shape();if(source.primitive==2 && source.references.empty())fail("AI3107","Unit parameters carry no value.",name.at);Parameter parameter{};parameter.token=name;parameter.shape=source;f.parameters.push_back(std::move(parameter));
                if(eat(","))fail("AI3107","A conversion operator requires exactly one parameter.",current().at);
                expect(")");expect("->");f.return_shape=type_shape();f.success_shape=f.return_shape;expect("ensures");expect("true");if(!is("{"))fail("AI3100","Only ensures true and a structured conversion body are supported.",current().at);f.body=statement();
                const std::string prefix=implicit_conversion ? "implicit#" : "explicit#";
                f.token=Token{prefix+native_shape_key(f.parameters[0].shape)+"->"+native_shape_key(f.return_shape),keyword.at,Token::id};
                functions.push_back(std::move(f));
                if(functions.size()>native_function_limit)fail("AI3100","Native bootstrap function limit exceeded.",current().at);
                continue;
            }
            if(eat("operator")){
                auto symbol=take();const std::string op=symbol.text;
                static const std::set<std::string> binary_ops={"+","-","*","/","%","==","!=","<",">","<=",">="};
                static const std::set<std::string> unary_ops={"+","-","!"};
                if(!binary_ops.contains(op) && !unary_ops.contains(op))fail("AI3107","Unsupported native operator declaration symbol.",symbol.at);
                Function f;f.unwind=unwind;f.fault_mode_location=fault_mode_location;f.token=Token{"operator"+op,symbol.at,Token::id};
                expect("(");
                if(!is(")"))do{auto name=identifier();expect(":");auto t=type_shape();if(t.primitive==2 && t.references.empty())fail("AI3107","Unit parameters carry no value.",name.at);Parameter parameter{};parameter.token=name;parameter.shape=t;f.parameters.push_back(std::move(parameter));}while(eat(","));
                expect(")");expect("->");f.return_shape=type_shape();f.success_shape=f.return_shape;expect("ensures");expect("true");if(!is("{"))fail("AI3100","Only ensures true and a structured operator body are supported.",current().at);f.body=statement();functions.push_back(std::move(f));
                if(functions.size()>native_function_limit)fail("AI3100","Native bootstrap function limit exceeded.",current().at);
                continue;
            }
            if(eat("extension")){
                auto receiver=identifier();int product_id=product_builder.find(canonical(receiver.text));if(product_id<0)fail("AI3107","An extension block requires a declared product receiver type.",receiver.at);
                expect("{");while(!is("}")){
                    if(current().kind==Token::end)fail("AI3100","Unterminated extension block.",receiver.at);
                    expect("fn");auto name=identifier();Function f;f.unwind=unwind;f.fault_mode_location=fault_mode_location;f.extension_block=true;f.member_name=canonical(name.text);
                    f.token=Token{std::string("$extension$")+std::to_string(accessor_counter++)+"$"+f.member_name,name.at,Token::id};
                    Parameter self{};self.token=Token{"this",name.at,Token::id};self.shape=NativeTypeShape{Type::product,product_id,{}};f.parameters.push_back(std::move(self));
                    expect("(");if(!is(")"))do{auto pname=identifier();expect(":");Parameter parameter{};parameter.token=pname;parameter.shape=type_shape();f.parameters.push_back(std::move(parameter));}while(eat(","));expect(")");
                    expect("->");f.return_shape=type_shape();f.success_shape=f.return_shape;expect("ensures");expect("true");if(!is("{"))fail("AI3107","An extension member requires a structured body.",current().at);f.body=statement();functions.push_back(std::move(f));
                    if(functions.size()>native_function_limit)fail("AI3100","Native bootstrap function limit exceeded.",current().at);
                }expect("}");continue;
            }
            if(eat("constructor")){
                auto product=identifier();int product_id=product_builder.find(canonical(product.text));if(product_id<0)fail("AI3107","A constructor requires a declared product type.",product.at);
                Function f;f.unwind=unwind;f.fault_mode_location=fault_mode_location;f.constructor=true;f.constructor_product=product_id;
                f.token=Token{std::string("$constructor$")+std::to_string(accessor_counter++)+"$"+canonical(product.text),product.at,Token::id};
                Parameter self{};self.token=Token{"this",product.at,Token::id};self.shape=NativeTypeShape{product_builder.is_class(product_id) ? Type::class_ref : Type::product,product_id,{}};self.mutable_parameter=true;f.parameters.push_back(std::move(self));
                expect("(");if(!is(")"))do{auto pname=identifier();expect(":");Parameter parameter{};parameter.token=pname;parameter.shape=type_shape();f.parameters.push_back(std::move(parameter));}while(eat(","));expect(")");
                if(eat("ensures"))expect("true");f.return_shape=NativeTypeShape{product_builder.is_class(product_id) ? Type::class_ref : Type::product,product_id,{}};f.success_shape=f.return_shape;
                if(!is("{"))fail("AI3107","A constructor requires a structured body.",current().at);f.body=statement();
                auto result=std::make_unique<Stmt>();result->kind=Stmt::result;result->token=Token{"return",product.at,Token::id};auto self_name=std::make_unique<Expr>();self_name->kind=Expr::name;self_name->token=Token{"this",product.at,Token::id};result->expr=std::move(self_name);
                if(f.body->kind==Stmt::block)f.body->children.push_back(std::move(result));else{auto block=std::make_unique<Stmt>();block->kind=Stmt::block;block->token=product;block->children.push_back(std::move(f.body));block->children.push_back(std::move(result));f.body=std::move(block);}
                functions.push_back(std::move(f));if(functions.size()>native_function_limit)fail("AI3100","Native bootstrap function limit exceeded.",current().at);continue;
            }
            if(is("add") || is("remove")){
                bool adder=eat("add");if(!adder)expect("remove");auto property=identifier();Function f;f.unwind=unwind;f.fault_mode_location=fault_mode_location;f.token=Token{std::string(adder ? "$add$" : "$remove$")+std::to_string(accessor_counter++)+"$"+canonical(property.text),property.at,Token::id};f.accessor_add=adder;f.accessor_remove=!adder;f.property_name=canonical(property.text);
                expect("(");auto self=identifier();if(canonical(self.text)!="this")fail("AI3107","An event accessor requires a this receiver parameter.",self.at);expect(":");Parameter receiver{};receiver.token=self;receiver.shape=type_shape();receiver.mutable_parameter=true;f.parameters.push_back(std::move(receiver));expect(",");auto incoming=identifier();expect(":");Parameter value{};value.token=incoming;value.shape=type_shape();f.parameters.push_back(std::move(value));expect(")");f.return_shape=f.parameters[0].shape;f.success_shape=f.return_shape;expect("ensures");expect("true");if(!is("{"))fail("AI3107","An event accessor requires a structured body.",current().at);f.body=statement();functions.push_back(std::move(f));if(functions.size()>native_function_limit)fail("AI3100","Native bootstrap function limit exceeded.",current().at);continue;
            }
            if(is("get") || is("set")){
                bool getter=eat("get");if(!getter)expect("set");
                auto property=identifier();Function f;f.unwind=unwind;f.fault_mode_location=fault_mode_location;
                f.token=Token{std::string(getter ? "$get$" : "$set$")+std::to_string(accessor_counter++)+"$"+canonical(property.text),property.at,Token::id};
                f.accessor_get=getter;f.accessor_set=!getter;f.property_name=canonical(property.text);
                expect("(");auto self=identifier();if(canonical(self.text)!="this")fail("AI3107","A property accessor requires a this receiver parameter.",self.at);expect(":");Parameter receiver{};receiver.token=self;receiver.shape=type_shape();receiver.mutable_parameter=!getter;f.parameters.push_back(std::move(receiver));
                if(!getter){if(eat(",")){auto incoming=identifier();expect(":");Parameter value{};value.token=incoming;value.shape=type_shape();f.parameters.push_back(std::move(value));}else{f.implicit_value=true;Parameter value{};value.token=Token{"value",property.at,Token::id};value.shape=NativeTypeShape{Type::integer,-1,{}};f.parameters.push_back(std::move(value));}}
                expect(")");if(getter){expect("->");f.return_shape=type_shape();}
                else{f.return_shape=f.parameters[0].shape;}
                f.success_shape=f.return_shape;expect("ensures");expect("true");if(!is("{"))fail("AI3107","A property accessor requires a structured body.",current().at);f.body=statement();functions.push_back(std::move(f));
                if(functions.size()>native_function_limit)fail("AI3100","Native bootstrap function limit exceeded.",current().at);continue;
            }
            if(is("async")){if(pending_async)fail("AI3107","A member cannot repeat async.",current().at);pending_async=true;take();continue;}
            if(is("virtual") || is("override") || is("abstract")){if(pending_method_flags)fail("AI3107","A member cannot repeat virtual, override or abstract modifiers.",current().at);if(is("virtual"))pending_method_flags=1;else if(is("override"))pending_method_flags=2;else pending_method_flags=3;take();continue;}
            expect("fn");Function f;f.unwind=unwind;f.fault_mode_location=fault_mode_location;f.token=identifier();f.async_method=pending_async;pending_async=false;f.abstract_method=pending_method_flags==3;f.virtual_method=(pending_method_flags&1)!=0 || f.abstract_method;f.override_method=pending_method_flags==2;pending_method_flags=0;expect("(");
            if(!is(")"))do {if(eat("volatile"))fail("AI3107","volatile is only allowed on native product fields.",tokens[pos-1].at);bool readonly_parameter=false,params_parameter=false,in_parameter=false,out_parameter=false;for(;;){if(eat("readonly")){if(readonly_parameter||params_parameter||in_parameter||out_parameter)fail("AI3107","A parameter cannot repeat or combine readonly, params, in with out.",current().at);readonly_parameter=true;}else if(eat("params")){if(params_parameter||readonly_parameter||in_parameter||out_parameter)fail("AI3107","A parameter cannot repeat or combine readonly, params, in with out.",current().at);params_parameter=true;}else if(is("in") && pos+1<tokens.size() && tokens[pos+1].kind==Token::id && ((pos+2<tokens.size() && tokens[pos+2].text==":") || tokens[pos+1].text=="readonly" || tokens[pos+1].text=="params" || tokens[pos+1].text=="in" || tokens[pos+1].text=="out")){take();if(params_parameter||readonly_parameter||in_parameter||out_parameter)fail("AI3107","A parameter cannot repeat or combine readonly, params, in with out.",current().at);in_parameter=true;}else if(is("out") && pos+1<tokens.size() && tokens[pos+1].kind==Token::id && ((pos+2<tokens.size() && tokens[pos+2].text==":") || tokens[pos+1].text=="readonly" || tokens[pos+1].text=="params" || tokens[pos+1].text=="in" || tokens[pos+1].text=="out")){take();if(params_parameter||readonly_parameter||in_parameter||out_parameter)fail("AI3107","A parameter cannot repeat or combine readonly, params, in with out.",current().at);out_parameter=true;}else break;}auto name=identifier();expect(":");bool scoped=eat("scoped");auto t=type_shape();if(t.primitive==2 && t.references.empty())fail("AI3107","Unit parameters carry no value.",name.at);Parameter parameter{};parameter.token=name;parameter.shape=t;parameter.scoped=scoped;parameter.readonly=readonly_parameter;parameter.params=params_parameter;parameter.in_parameter=in_parameter;parameter.out_parameter=out_parameter;f.parameters.push_back(std::move(parameter));}while(eat(","));expect(")");expect("->");f.return_shape=type_shape();f.success_shape=f.return_shape;if(is("fails")){f.fallible=true;f.failure_declaration=take().at;auto error_at=current().at;f.error_shape=type_shape();auto id=product_builder.find_generic("std.core.Result");if(id<0)fail("AI3100","Canonical failure carrier was not resolved.",f.failure_declaration);f.return_shape={Type::product,product_builder.instantiate(id,{f.success_shape,f.error_shape},f.token.at,{f.token.at,error_at}),{}};}if(f.abstract_method){if(!eat(";"))fail("AI3107","An abstract member declaration requires a semicolon body terminator.",current().at);functions.push_back(std::move(f));if(functions.size()>native_function_limit)fail("AI3100","Native bootstrap function limit exceeded.",current().at);continue;}
            expect("ensures");expect("true");
            if(!is("{"))fail("AI3100","Only ensures true and a structured function body are supported.",current().at);body_has_yield=false;f.body=statement();if(body_has_yield){if(f.return_shape.primitive!=Type::buffer)fail("AI3107","A yielding function must return owned buffer in the native bootstrap.",f.token.at);rewrite_yields(*f.body,f.token);body_has_yield=false;}functions.push_back(std::move(f));
            if(functions.size()>native_function_limit)fail("AI3100","Native bootstrap function limit exceeded.",current().at);
        }
        for(auto& query:pending_queries)functions.push_back(std::move(query));
        resolve_implicit_setters(functions);generate_virtual_helpers(functions);generate_managed_helpers(functions);generate_event_helpers(functions);complete_types(functions);
        for(auto& f:functions)if(f.external){
            static const std::map<std::string,std::pair<std::vector<Type>,Type>> allowed={
                {"abs",{{Type::integer},Type::integer}},
                {"llabs",{{Type::long_integer},Type::long_integer}},{"fabs",{{Type::float64},Type::float64}},
                {"fabsf",{{Type::float32},Type::float32}},{"sqrt",{{Type::float64},Type::float64}},
                {"sqrtf",{{Type::float32},Type::float32}},{"rand",{{},Type::integer}},
                // Recovery boundary builtins. The names are reserved to the
                // compiler-synthesized recovery wrapper; a user extern cannot
                // grant itself these signals. Each maps to the emitted runtime,
                // so no foreign prototype is required.
                {"ai_now_ms",{{},Type::unsigned_long_integer}},
                {"ai_check_cancelled",{{},Type::unit}},
                {"ai_check_deadline",{{Type::unsigned_long_integer},Type::unit}},
                {"ai_sleep_ms",{{Type::integer},Type::unit}},
                {"ai_cancel_request",{{},Type::unit}},
                {"ai_cancelled",{{},Type::boolean}}};
            auto found=allowed.find(f.external_name);if(found==allowed.end())fail("AI3107","extern binds only to a documented C runtime scalar function.",f.token.at);
            if(f.fallible)fail("AI3107","extern declarations cannot declare failure contracts.",f.token.at);
            if(f.parameters.size()!=found->second.first.size())fail("AI3107","extern signature differs from the runtime function.",f.token.at);
            for(std::size_t i=0;i<f.parameters.size();++i)if(f.parameters[i].type!=found->second.first[i])fail("AI3107","extern parameter types differ from the runtime function.",f.parameters[i].token.at);
            if(f.type!=found->second.second)fail("AI3107","extern return type differs from the runtime function.",f.token.at);
        }return functions;
    }
};
#include "native_core_outcomes.inc"
struct Binding { Type type; int id; bool mutable_; std::optional<Constant> constant; };
std::int64_t wrapped(std::int64_t value) {
    return std::bit_cast<std::int32_t>(static_cast<std::uint32_t>(value));
}
std::int64_t constant_integer(std::int64_t value, const Expr& e) {
    if(e.wraps)return wrapped(value);
    if(value < -2147483648LL || value > 2147483647LL)fail("AI3111","Constant arithmetic overflows Int32; use an unchecked context.",e.token.at);
    return value;
}
#define AI_INT64_CODE(...) __VA_ARGS__
#include "native_int64_ops.inc"
#undef AI_INT64_CODE
#define AI_NARROW_CODE(...) __VA_ARGS__
#include "native_narrow_integer_ops.inc"
#undef AI_NARROW_CODE

struct NativeInt64Operator {
    std::string_view token;
    ai_i64_operation id;
    const char* emitted_id;
};
const NativeInt64Operator* native_int64_operator(std::string_view token) {
    static constexpr NativeInt64Operator operators[]={
        {"+",ai_i64_add,"ai_i64_add"}, {"-",ai_i64_sub,"ai_i64_sub"},
        {"*",ai_i64_mul,"ai_i64_mul"}, {"/",ai_i64_div,"ai_i64_div"},
        {"%",ai_i64_rem,"ai_i64_rem"}, {"&",ai_i64_and,"ai_i64_and"},
        {"|",ai_i64_or,"ai_i64_or"}, {"^",ai_i64_xor,"ai_i64_xor"},
        {"<<",ai_i64_shl,"ai_i64_shl"}, {">>",ai_i64_shr,"ai_i64_shr"},
        {">>>",ai_i64_ushr,"ai_i64_ushr"},
    };
    for(const auto& op:operators)if(op.token==token)return &op;
    return nullptr;
}
bool native_integer_shift(std::string_view operation) {
    return operation=="<<" || operation==">>" || operation==">>>";
}
std::optional<std::int64_t> native_int32_shift(std::string_view operation,std::int64_t value,std::int64_t count) {
    const auto distance=static_cast<std::uint32_t>(count)&31u;
    const auto bits=static_cast<std::uint32_t>(value);
    if(operation=="<<")return wrapped(bits<<distance);
    if(operation==">>>")return wrapped(bits>>distance);
    if(operation==">>"){
        auto shifted=bits>>distance;
        if(value<0 && distance)shifted|=UINT32_MAX<<(32u-distance);
        return wrapped(shifted);
    }
    return std::nullopt;
}
ai_decimal decimal_from_constant(const Constant& value) {
    ai_decimal result{};
    result.w0=static_cast<std::uint32_t>(value.number);
    result.w1=static_cast<std::uint32_t>(static_cast<std::uint64_t>(value.number)>>32);
    result.w2=static_cast<std::uint32_t>(value.number2);
    result.flags=static_cast<std::uint32_t>(static_cast<std::uint64_t>(value.number2)>>32);
    return result;
}
Constant constant_from_decimal(ai_decimal value) {
    return Constant{Type::decimal,static_cast<std::int64_t>((static_cast<std::uint64_t>(value.w1)<<32)|value.w0),static_cast<std::int64_t>((static_cast<std::uint64_t>(value.flags)<<32)|value.w2)};
}
std::int64_t decimal_to_integer_constant(ai_decimal value,Type target,Location at) {
    std::uint64_t magnitude=0;int overflow=0;ai_dec_to_u64(value,&magnitude,&overflow);
    const bool negative=ai_dec_negative(value)!=0;
    if(overflow)fail("AI3111","Decimal value exceeds the destination integer range.",at);
    if(target.narrow_integer() || target.is_char()){
        const auto* small=target.narrow_integer();
        const std::int64_t minimum=small ? small->minimum : 0;
        const std::int64_t maximum=small ? small->maximum : 65535;
        if(negative){ if(magnitude>static_cast<std::uint64_t>(-minimum))fail("AI3111","Decimal value exceeds the destination integer range.",at); return -static_cast<std::int64_t>(magnitude);}
        if(magnitude>static_cast<std::uint64_t>(maximum))fail("AI3111","Decimal value exceeds the destination integer range.",at);
        return static_cast<std::int64_t>(magnitude);
    }
    if(target==Type::integer){
        if(negative){ if(magnitude>2147483648ULL)fail("AI3111","Decimal value exceeds the destination integer range.",at); return magnitude==2147483648ULL ? INT32_MIN : -static_cast<std::int64_t>(magnitude);}
        if(magnitude>2147483647ULL)fail("AI3111","Decimal value exceeds the destination integer range.",at);
        return static_cast<std::int64_t>(magnitude);
    }
    if(target==Type::long_integer){
        if(negative){ if(magnitude>9223372036854775808ULL)fail("AI3111","Decimal value exceeds the destination integer range.",at); return magnitude==9223372036854775808ULL ? INT64_MIN : -static_cast<std::int64_t>(magnitude);}
        if(magnitude>static_cast<std::uint64_t>(INT64_MAX))fail("AI3111","Decimal value exceeds the destination integer range.",at);
        return static_cast<std::int64_t>(magnitude);
    }
    if(target.is_uint32()){
        if((negative && magnitude) || magnitude>4294967295ULL)fail("AI3111","Decimal value exceeds the destination integer range.",at);
        return static_cast<std::int64_t>(magnitude);
    }
    if(target.is_ulong64()){
        if(negative && magnitude)fail("AI3111","Decimal value exceeds the destination integer range.",at);
        return static_cast<std::int64_t>(magnitude);
    }
    fail("AI3100","Unsupported decimal integer conversion target.",at);
}
Constant fold_unary(const Expr& e, Constant operand) {
    if(e.op=="!")return {e.type,!operand.number};
    if(e.type.is_decimal()){
        ai_decimal value=decimal_from_constant(operand);
        if(e.op=="-"){value.flags^=0x80000000u;return constant_from_decimal(value);}
        return operand;
    }
    if(e.type.is_float64()){
        const double value=std::bit_cast<double>(static_cast<std::uint64_t>(operand.number));
        if(e.op=="-")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint64_t>(-value))};
        return {e.type,operand.number};
    }
    if(e.type.is_float32()){
        const float value=std::bit_cast<float>(static_cast<std::uint32_t>(operand.number));
        if(e.op=="-")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint32_t>(-value))};
        return {e.type,operand.number};
    }
    if(e.op=="-"){
        if(e.type==Type::long_integer){
            if(operand.number==INT64_MIN){
                if(!e.wraps)fail("AI3111","Constant arithmetic overflows Int64; use an unchecked context.",e.token.at);
                return operand;
            }
            return {e.type,-operand.number};
        }
        return {e.type,constant_integer(-operand.number,e)};
    }
    if(e.op=="~"){
        if(e.type.is_ulong64())return {e.type,static_cast<std::int64_t>(~static_cast<std::uint64_t>(operand.number))};
        if(e.type.is_uint32())return {e.type,static_cast<std::int64_t>(static_cast<std::uint32_t>(~static_cast<std::uint32_t>(operand.number)))};
        if(e.type==Type::long_integer)return {e.type,ai_i64_bits(~static_cast<std::uint64_t>(operand.number))};
        return {e.type,std::bit_cast<std::int32_t>(~static_cast<std::uint32_t>(operand.number))};
    }
    return {e.type,operand.number};
}
Constant fold_binary(const Expr& e, Constant left, Constant right) {
    auto a=left.number,b=right.number;auto result=std::int64_t{0};
    if(e.type==Type::long_integer){
        // CLR folds a constant remainder of the minimum value by -1 to zero;
        // only the dynamic operation faults.
        if(e.op=="%" && a==INT64_MIN && b==-1)return {e.type,0};
        const auto* operation=native_int64_operator(e.op);
        if(!operation)fail("AI3100","Unknown Int64 constant operator.",e.token.at);
        const auto folded=ai_i64_binary(operation->id,a,b,e.wraps);
        if(folded.error==ai_i64_zero || (folded.error==ai_i64_overflow && e.op=="/"))
            fail("AI3111","Constant division or remainder faults.",e.token.at);
        if(folded.error==ai_i64_overflow)
            fail("AI3111","Constant arithmetic overflows Int64; use an unchecked context.",e.token.at);
        return {e.type,folded.value};
    }
    if(left.type.is_decimal()){
        const ai_decimal da=decimal_from_constant(left),db=decimal_from_constant(right);ai_decimal decimal_result{};
        if(e.op=="+"){if(ai_dec_core_add(da,db,&decimal_result))fail("AI3111","Constant Decimal arithmetic overflows.",e.token.at);return constant_from_decimal(decimal_result);}
        if(e.op=="-"){if(ai_dec_core_sub(da,db,&decimal_result))fail("AI3111","Constant Decimal arithmetic overflows.",e.token.at);return constant_from_decimal(decimal_result);}
        if(e.op=="*"){if(ai_dec_core_mul(da,db,&decimal_result))fail("AI3111","Constant Decimal arithmetic overflows.",e.token.at);return constant_from_decimal(decimal_result);}
        if(e.op=="/"){int error=ai_dec_core_div(da,db,&decimal_result);if(error)fail("AI3111","Constant Decimal division faults.",e.token.at);return constant_from_decimal(decimal_result);}
        const int comparison=ai_dec_cmp(da,db);
        if(e.op=="==")return {Type::boolean,comparison==0};
        if(e.op=="!=")return {Type::boolean,comparison!=0};
        if(e.op=="<")return {Type::boolean,comparison<0};
        if(e.op==">")return {Type::boolean,comparison>0};
        if(e.op=="<=")return {Type::boolean,comparison<=0};
        if(e.op==">=")return {Type::boolean,comparison>=0};
        fail("AI3100","Unknown Decimal constant operator.",e.token.at);
    }
    if(left.type.is_float64()){
        const double da=std::bit_cast<double>(static_cast<std::uint64_t>(a)),db=std::bit_cast<double>(static_cast<std::uint64_t>(b));
        if(e.op=="+")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint64_t>(da+db))};
        if(e.op=="-")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint64_t>(da-db))};
        if(e.op=="*")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint64_t>(da*db))};
        if(e.op=="/")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint64_t>(da/db))};
        if(e.op=="==")return {Type::boolean,da==db};
        if(e.op=="!=")return {Type::boolean,da!=db};
        if(e.op=="<")return {Type::boolean,da<db};
        if(e.op==">")return {Type::boolean,da>db};
        if(e.op=="<=")return {Type::boolean,da<=db};
        if(e.op==">=")return {Type::boolean,da>=db};
        fail("AI3100","Unknown Double constant operator.",e.token.at);
    }
    if(left.type.is_float32()){
        const float fa=std::bit_cast<float>(static_cast<std::uint32_t>(a)),fb=std::bit_cast<float>(static_cast<std::uint32_t>(b));
        if(e.op=="+")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint32_t>(fa+fb))};
        if(e.op=="-")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint32_t>(fa-fb))};
        if(e.op=="*")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint32_t>(fa*fb))};
        if(e.op=="/")return {e.type,static_cast<std::int64_t>(std::bit_cast<std::uint32_t>(fa/fb))};
        if(e.op=="==")return {Type::boolean,fa==fb};
        if(e.op=="!=")return {Type::boolean,fa!=fb};
        if(e.op=="<")return {Type::boolean,fa<fb};
        if(e.op==">")return {Type::boolean,fa>fb};
        if(e.op=="<=")return {Type::boolean,fa<=fb};
        if(e.op==">=")return {Type::boolean,fa>=fb};
        fail("AI3100","Unknown Single constant operator.",e.token.at);
    }
    if(left.type.is_ulong64()){
        const std::uint64_t ua=static_cast<std::uint64_t>(a),ub=static_cast<std::uint64_t>(b);
        if(e.op=="+"){const std::uint64_t sum=ua+ub;if(!e.wraps && sum<ua)fail("AI3111","Constant arithmetic overflows UInt64; use an unchecked context.",e.token.at);return {e.type,static_cast<std::int64_t>(sum)};}
        if(e.op=="-"){if(!e.wraps && ua<ub)fail("AI3111","Constant arithmetic underflows UInt64; use an unchecked context.",e.token.at);return {e.type,static_cast<std::int64_t>(ua-ub)};}
        if(e.op=="*"){if(!e.wraps && ua!=0 && ub>UINT64_MAX/ua)fail("AI3111","Constant arithmetic overflows UInt64; use an unchecked context.",e.token.at);return {e.type,static_cast<std::int64_t>(ua*ub)};}
        if(e.op=="/" || e.op=="%"){if(ub==0)fail("AI3111","Constant division or remainder faults.",e.token.at);return {e.type,static_cast<std::int64_t>(e.op=="/" ? ua/ub : ua%ub)};}
        if(e.op=="&")return {e.type,static_cast<std::int64_t>(ua&ub)};
        if(e.op=="|")return {e.type,static_cast<std::int64_t>(ua|ub)};
        if(e.op=="^")return {e.type,static_cast<std::int64_t>(ua^ub)};
        if(e.op=="<<")return {e.type,static_cast<std::int64_t>(ua<<(ub&63u))};
        if(e.op==">>" || e.op==">>>")return {e.type,static_cast<std::int64_t>(ua>>(ub&63u))};
        if(e.op=="==")return {Type::boolean,ua==ub};
        if(e.op=="!=")return {Type::boolean,ua!=ub};
        if(e.op=="<")return {Type::boolean,ua<ub};
        if(e.op==">")return {Type::boolean,ua>ub};
        if(e.op=="<=")return {Type::boolean,ua<=ub};
        if(e.op==">=")return {Type::boolean,ua>=ub};
        fail("AI3100","Unknown UInt64 constant operator.",e.token.at);
    }
    if(left.type.is_uint32()){
        const std::uint64_t ua=static_cast<std::uint32_t>(a),ub=static_cast<std::uint32_t>(b);
        auto bound=[&](std::uint64_t value){if(!e.wraps && value>4294967295ULL)fail("AI3111","Constant arithmetic overflows UInt32; use an unchecked context.",e.token.at);return static_cast<std::int64_t>(value & 4294967295ULL);};
        if(e.op=="+")return {e.type,bound(ua+ub)};
        if(e.op=="-")return {e.type,bound(ua>=ub ? ua-ub : ua+4294967296ULL-ub)};
        if(e.op=="*")return {e.type,bound(ua*ub)};
        if(e.op=="/" || e.op=="%"){if(ub==0)fail("AI3111","Constant division or remainder faults.",e.token.at);return {e.type,static_cast<std::int64_t>(e.op=="/" ? ua/ub : ua%ub)};}
        if(e.op=="&")return {e.type,static_cast<std::int64_t>(ua&ub)};
        if(e.op=="|")return {e.type,static_cast<std::int64_t>(ua|ub)};
        if(e.op=="^")return {e.type,static_cast<std::int64_t>(ua^ub)};
        if(e.op=="<<")return {e.type,static_cast<std::int64_t>((ua<<(ub&31u))&4294967295ULL)};
        if(e.op==">>" || e.op==">>>")return {e.type,static_cast<std::int64_t>(ua>>(ub&31u))};
        if(e.op=="==")return {Type::boolean,ua==ub};
        if(e.op=="!=")return {Type::boolean,ua!=ub};
        if(e.op=="<")return {Type::boolean,ua<ub};
        if(e.op==">")return {Type::boolean,ua>ub};
        if(e.op=="<=")return {Type::boolean,ua<=ub};
        if(e.op==">=")return {Type::boolean,ua>=ub};
        fail("AI3100","Unknown UInt32 constant operator.",e.token.at);
    }
    if(e.op=="+")result=constant_integer(a+b,e);
    else if(e.op=="-")result=constant_integer(a-b,e);
    else if(e.op=="*")result=constant_integer(a*b,e);
    else if(e.op=="/" || e.op=="%") {
        if(b==0 || (e.op=="/" && a==-2147483648LL && b==-1))fail("AI3111","Constant division or remainder faults.",e.token.at);
        result=e.op=="/" ? a/b : a%b;
    } else if(e.op=="==")result=a==b;
    else if(e.op=="!=")result=a!=b;
    else if(e.op=="<")result=a<b;
    else if(e.op==">")result=a>b;
    else if(e.op=="<=")result=a<=b;
    else if(e.op==">=")result=a>=b;
    else if(e.op=="&&")result=a && b;
    else if(e.op=="||")result=a || b;
    else if(e.op=="&")result=wrapped(static_cast<std::uint32_t>(a)&static_cast<std::uint32_t>(b));
    else if(e.op=="|")result=wrapped(static_cast<std::uint32_t>(a)|static_cast<std::uint32_t>(b));
    else if(e.op=="^")result=wrapped(static_cast<std::uint32_t>(a)^static_cast<std::uint32_t>(b));
    else if(auto shifted=native_int32_shift(e.op,a,b))result=*shifted;
    else fail("AI3100","Unknown constant operator.",e.token.at);
    return {e.type,result};
}
class Binder {
    std::shared_ptr<const NativeProductTable> products;
    std::map<std::string,Function*> functions;
    std::vector<std::map<std::string,Binding>> scopes;
    std::set<int> assigned,out_required,out_written;std::map<int,Token> out_tokens;int active_drop_for=-1;std::set<int> static_symbols;std::set<int> stack_symbols;bool stack_borrow_allowed=false,stack_alloc_allowed=false;Function* current_function=nullptr;std::map<std::pair<int,std::string>,Function*> property_getters,property_setters,event_adders,event_removers,extension_methods;std::map<int,Function*> constructors;
    std::vector<std::vector<std::set<int>>> continues;
    std::vector<bool> breaks;
    bool reachable=true;
    int symbols=0, loops=0;
    Type result_type=Type::unit;
    Location result_declaration;
    std::map<int,Location> owner_declarations;
    void require(Type actual, Type expected, Location at, std::string code="AI3107") {
        if(actual!=expected)fail(std::move(code),"Value has an incompatible type.",at);
    }
    static std::string type_label(const Type& type){return type.label();}
    static void related(Diagnostic& d,std::string kind,Location at){
        for(auto& entry:d.related)if(entry.kind==kind && entry.location==at)return;
        d.related.push_back({std::move(kind),at});
    }
    bool relate_owner(Diagnostic& d,const Expr& e){
        if(e.type==Type::buffer){
            if(e.kind==Expr::assign || e.kind==Expr::update){bool found=false;for(auto& child:e.children)if(child->type==Type::buffer)found=relate_owner(d,*child) || found;return found;}
            if(e.kind==Expr::name){auto declaration=owner_declarations.find(e.symbol);if(declaration!=owner_declarations.end()){related(d,"declaration",declaration->second);if(d.ownerSymbol<0)d.ownerSymbol=e.symbol;}related(d,"owner-use",e.token.at);}
            else if(e.kind==Expr::overflow_context)return relate_owner(d,*e.children[0]);
            else related(d,"owner-producer",e.token.at);
            return true;
        }
        bool found=false;for(auto& child:e.children)if(child->type==Type::buffer)found=relate_owner(d,*child) || found;return found;
    }
    void reference_related(Diagnostic& d,const Expr& e){
        if(e.kind==Expr::name && e.symbol>=0){auto found=owner_declarations.find(e.symbol);if(found!=owner_declarations.end()){related(d,e.type.is_reference() ? "reference-declaration" : "storage-declaration",found->second);related(d,e.type.is_reference() ? "reference-use" : "storage-use",e.token.at);if(d.ownerSymbol<0)d.ownerSymbol=e.symbol;}}
        else {if(e.kind==Expr::borrow)related(d,"borrow-origin",e.token.at);for(auto& child:e.children)reference_related(d,*child);}
    }
    [[noreturn]] void reference_failure(std::string code,std::string message,const Expr& e){Diagnostic d(std::move(code),std::move(message),e.token.at);d.ownershipObligation="reference-access-mode";reference_related(d,e);throw d;}
    void require_value(Type actual,Type expected,Location at,const Expr* expression,std::string code="AI3107",const Location* context=nullptr,const char* context_kind="destination-declaration"){
        if(actual==expected)return;
        Diagnostic d(std::move(code),"Value has an incompatible type.",at);
        if(actual.is_reference() || expected.is_reference()){
            d.ownershipObligation="reference-value-type";d.expectedType=type_label(expected);d.actualType=type_label(actual);
            if(expression)reference_related(d,*expression);if(context)related(d,context_kind,*context);
        }else if(actual==Type::buffer || expected==Type::buffer){
            d.ownershipObligation="owned-value-type";d.expectedType=type_label(expected);d.actualType=type_label(actual);
            if(expression){if(actual==Type::buffer)relate_owner(d,*expression);else related(d,"value-producer",expression->token.at);}
            if(context)related(d,context_kind,*context);
        }
        if(context && (actual.has_product_root() || expected.has_product_root() || std::string_view(context_kind)=="field-declaration" || std::string_view(context_kind)=="payload-declaration"))related(d,context_kind,*context);
        throw d;
    }
    Binding lookup(const Token& token, bool read) {
        auto name=canonical(token.text);
        for(auto i=scopes.rbegin();i!=scopes.rend();++i) {
            auto found=i->find(name);
            if(found!=i->end()) {auto b=found->second;if(read && reachable && b.type.is_scalar() && !assigned.contains(b.id))fail("AI3102","Local is not definitely assigned.",token.at);return b;}
        }
        fail("AI3103","Name is not declared in this scope.",token.at);
    }
    int declare(const Token& token, Type type, bool mutable_, std::optional<Constant> constant=std::nullopt) {
        auto name=canonical(token.text);
        for(auto& scope:scopes)if(scope.contains(name)){
            auto previous=scope.at(name);Diagnostic d("AI3106","A local with this name is already active.",token.at);
            if(type==Type::buffer || previous.type==Type::buffer){d.ownershipObligation="owned-storage-declaration";if(previous.type==Type::buffer)d.ownerSymbol=previous.id;related(d,"previous-declaration",owner_declarations.at(previous.id));}
            throw d;
        }
        int id=symbols++;owner_declarations.emplace(id,token.at);scopes.back().emplace(name,Binding{type,id,mutable_,constant});return id;
    }
    int operator_function(const std::string& op,Type operand,std::size_t arity,Type& result,Location at){
        const std::string name="operator"+op;
        auto found=functions.find(name);
        if(found==functions.end())fail("AI3107","No native operator is declared for this operation.",at);
        auto& target=*found->second;
        if(target.parameters.size()!=arity)fail("AI3107","Native operator declaration has the wrong arity.",at);
        for(auto& parameter:target.parameters)if(parameter.type!=operand)fail("AI3107","Native operator operand types do not match its declaration.",at);
        if((op=="==" || op=="!=") && !functions.contains(std::string("operator")+(op=="==" ? "!=" : "==")))fail("AI3107","Equality operators must be declared as a matching pair.",at);
        result=target.type;return target.symbol;
    }
    E clone_place(const Expr& source){
        auto copy=std::make_unique<Expr>();copy->kind=source.kind;copy->token=source.token;copy->qualifier=source.qualifier;copy->explicit_qualifier=source.explicit_qualifier;copy->propagation=source.propagation;copy->null_literal=source.null_literal;copy->type=source.type;copy->match_reference=source.match_reference;copy->value=source.value;copy->symbol=source.symbol;copy->height=source.height;copy->field_index=source.field_index;copy->nominal=source.nominal;copy->sizeof_shape=source.sizeof_shape;copy->op=source.op;copy->wraps=source.wraps;copy->constant=source.constant;
        for(auto& child:source.children)copy->children.push_back(clone_place(*child));
        return copy;
    }
    Type product_operator_call(Expr& e,int symbol,const std::string& op,Type result){
        e.kind=Expr::call;e.token=Token{"operator"+op,e.token.at,Token::id};e.symbol=symbol;e.op.clear();e.type=result;e.constant.reset();return e.type;
    }
    Type binary(const std::string& op, Type left, Type right, Location at) {
        if(native_integer_shift(op) && (left.is_signed_integer() || left.is_uint32() || left.is_ulong64())){
            require(right,Type::integer,at);
            return left;
        }
        require(right,left,at);
        if(left.is_decimal()){
            if(op=="==" || op=="!=" || op=="<" || op==">" || op=="<=" || op==">=")return Type::boolean;
            if(op=="+" || op=="-" || op=="*" || op=="/")return left;
            fail("AI3107","Decimal does not support this operator.",at);
        }
        if(left.is_product())fail("AI3107","Native product operators require a declared semantic contract.",at);
        if(left==Type::buffer)fail("AI3107","An owned buffer has no implicit copy, comparison or arithmetic operator.",at);
        if(left.is_reference())fail("AI3107","A reference has no scalar operator.",at);
        if(left.is_payload_union())fail("AI3107","Payload union equality requires a verified value equality implementation.",at);
        if(op=="==" || op=="!=") {if(left==Type::unit)fail("AI3107","Unit is not a comparable value.",at);return Type::boolean;}
        if(left.is_payload_union())fail("AI3107","Payload union equality requires a verified value equality implementation.",at);
        if(left.is_union())fail("AI3107","Native union cases support equality and inequality only.",at);
        if(left==Type::long_integer){
            if(op=="<" || op==">" || op=="<=" || op==">=")return Type::boolean;
            if(native_int64_operator(op))return left;
            fail("AI3107","Int64 is not a Boolean condition.",at);
        }
        if(left.is_float64()){
            if(right!=left)fail("AI3107","Double operations need matching operand types.",at);
            if(op=="<" || op==">" || op=="<=" || op==">=")return Type::boolean;
            if(op=="+" || op=="-" || op=="*" || op=="/")return left;
            fail("AI3107","Double does not support this operator.",at);
        }
        if(left.is_float32()){
            if(right!=left)fail("AI3107","Single operations need matching operand types.",at);
            if(op=="<" || op==">" || op=="<=" || op==">=")return Type::boolean;
            if(op=="+" || op=="-" || op=="*" || op=="/")return left;
            fail("AI3107","Single does not support this operator.",at);
        }
        if(left.is_ulong64()){
            if(right!=left)fail("AI3107","UInt64 operations need matching operand types.",at);
            if(op=="<" || op==">" || op=="<=" || op==">=")return Type::boolean;
            if(op=="+" || op=="-" || op=="*" || op=="/" || op=="%" || op=="&" || op=="|" || op=="^")return left;
            fail("AI3107","UInt64 is not a Boolean condition.",at);
        }
        if(left.is_uint32()){
            if(right!=left)fail("AI3107","UInt32 operations need matching operand types.",at);
            if(op=="<" || op==">" || op=="<=" || op==">=")return Type::boolean;
            if(op=="+" || op=="-" || op=="*" || op=="/" || op=="%" || op=="&" || op=="|" || op=="^")return left;
            fail("AI3107","UInt32 is not a Boolean condition.",at);
        }
        if(op=="&&" || op=="||"){require(left,Type::boolean,at);return Type::boolean;}
        if(op=="&" || op=="|" || op=="^") {if(left==Type::unit)fail("AI3107","Bit operations need integer or Boolean operands.",at);return left;}
        require(left,Type::integer,at);
        if(op=="<" || op==">" || op=="<=" || op==">=")return Type::boolean;
        return Type::integer;
    }
    bool borrowed_place(const Expr& e)const{return e.kind==Expr::dereference || (e.kind==Expr::field && borrowed_place(*e.children[0]));}
    bool stable_place(const Expr& e)const{return (e.kind==Expr::name && e.nominal<0) || e.kind==Expr::dereference || (e.kind==Expr::field && stable_place(*e.children[0]));}
    Type member_type(Expr& e,Type base){
        if(!base.is_product())reference_failure("AI3107","Member access requires a native product value.",e);auto& fields=base.definition().fields;auto name=canonical(e.token.text);for(std::size_t i=0;i<fields.size();++i)if(fields[i].name==name){e.field_index=static_cast<int>(i);if(fields[i].protected_field)fail("AI3107","A protected field has no derived-type access in the native bootstrap and is construction-only.",e.token.at);if(fields[i].private_field && active_drop_for!=base.product_id())fail("AI3107","A private field is accessible only inside its product destructor or construction.",e.token.at);return e.type=base.field_type(i);}Diagnostic d("AI3103","Native product has no field with this name.",e.token.at);d.related.push_back({"type-declaration",base.definition().at});throw d;
    }
    Type contained_access(Expr& e,bool mutation){
        if(e.kind==Expr::name && e.nominal>=0)reference_failure("AI3115","A type application is not storage.",e);
        if(e.kind==Expr::name){auto binding=lookup(e.token,true);e.symbol=binding.id;return e.type=binding.type;}
        if(e.kind==Expr::field){auto base=contained_access(*e.children[0],mutation);auto type=member_type(e,base);if(mutation && base.definition().fields[static_cast<std::size_t>(e.field_index)].readonly)fail("AI3107","Cannot mutably access a readonly field.",e.token.at);return type;}
        if(e.kind==Expr::dereference){auto parent=reference_access(*e.children[0],mutation);if(mutation && parent.borrow_kind()!=Type::Borrow::exclusive)reference_failure("AI3115","Exclusive field access cannot pass through a shared reference.",e);return e.type=parent.pointee();}
        reference_failure("AI3115","A contained reference requires stable field storage.",e);
    }
    Type reference_access(Expr& e,bool mutation) {
        if(e.kind==Expr::name && e.nominal>=0)reference_failure("AI3115","A type application is not storage.",e);
        if(e.kind==Expr::overflow_context){e.type=reference_access(*e.children[0],mutation);return e.type;}
        if(e.kind==Expr::name){auto b=lookup(e.token,true);e.symbol=b.id;e.type=b.type;}
        else if(e.kind==Expr::field){e.type=stable_place(e) ? member_type(e,contained_access(*e.children[0],mutation)) : expr(e);}
        else if(e.kind==Expr::dereference){auto parent=reference_access(*e.children[0],mutation);if(mutation && parent.borrow_kind()!=Type::Borrow::exclusive)reference_failure("AI3115","Exclusive access cannot pass through a shared reference.",e);e.type=parent.pointee();}
        else {e.type=expr(e);}
        if(mutation && guarded_payload(e))reference_failure("AI3115","A guard cannot mutate a payload before arm selection.",e);if(!e.type.is_reference())reference_failure("AI3115","Dereference requires a reference value.",e);
        return e.type;
    }
    Type place_type(Expr& e,bool mutation,bool borrow=false) {
        if(e.kind==Expr::name && e.nominal>=0)reference_failure("AI3115","A type application is not storage.",e);
        if(e.kind==Expr::field){auto base=place_type(*e.children[0],mutation,borrow);auto type=member_type(e,base);auto& field=base.definition().fields[static_cast<std::size_t>(e.field_index)];if(borrow && field.volatile_field)fail("AI3107","A volatile field cannot be passed by reference.",e.token.at);if(mutation && field.readonly)fail("AI3107","Cannot mutate a readonly field.",e.token.at);if(mutation && field.init_only)fail("AI3107","Cannot mutate an init-only field outside construction.",e.token.at);return type;}
        if(e.kind==Expr::name){auto b=lookup(e.token,true);e.symbol=b.id;e.type=b.type;e.constant.reset();if(mutation && !b.mutable_)reference_failure("AI3105","Exclusive access requires mutable storage.",e);return e.type;}
        if(e.kind==Expr::dereference){auto pointer=reference_access(*e.children[0],mutation);if(mutation && pointer.borrow_kind()!=Type::Borrow::exclusive)reference_failure("AI3115","Cannot write through a shared reference.",e);return e.type=pointer.pointee();}
        reference_failure("AI3115","A borrow needs stable local or dereferenced storage.",e);
    }
#include "native_union_binding.inc"
#include "native_failure_binding.inc"
    Type expr(Expr& e,std::optional<Type> expected=std::nullopt) {
        try{auto bound=expr_impl(e,expected);if(e.kind==Expr::name && stack_symbols.contains(e.symbol) && !stack_borrow_allowed)fail("AI3107","A stackalloc buffer cannot escape its lexical local.",e.token.at);return bound;}catch(Diagnostic& d){
            if(d.ownershipObligation=="owned-value-type" && e.kind==Expr::assign && e.children[0]->kind==Expr::name && e.children[0]->type==Type::buffer && d.location.offset==e.children[1]->token.at.offset && d.expectedType=="owned-buffer"){auto& target=*e.children[0];related(d,"destination-declaration",owner_declarations.at(target.symbol));if(d.ownerSymbol<0)d.ownerSymbol=target.symbol;}
            bool local_operation=(e.kind==Expr::binary && d.location.offset==e.token.at.offset) ||
                ((e.kind==Expr::assign || e.kind==Expr::update) && (d.location.offset==e.token.at.offset || d.location.offset==e.children[0]->token.at.offset));
            if(d.ownershipObligation.empty() && local_operation && (d.code=="AI3107" || d.code=="AI3105") && relate_owner(d,e)){
                d.ownershipObligation=d.code=="AI3105" ? "owned-storage-mutation" : "owned-value-operation";
            }
            throw;
        }
    }
    static bool implicit_integer_value(const Expr& value,Type target) {
        if(value.type==target || native_integer_widens(value.type,target))return true;
        if(target.is_uint32() && value.type==Type::integer && value.constant &&
           value.constant->number>=0 && value.constant->number<=4294967295LL)return true;
        if(target.is_ulong64() && value.type==Type::integer && value.constant && value.constant->number>=0)return true;
        if(target.is_float32() && value.type.is_integer())return true;
        if(target.is_enum() && value.type==Type::integer && value.constant && value.constant->number==0)return true;
        if(target.is_decimal() && value.type.is_integer())return true;
        if(target.is_float64() && (value.type.is_integer() || value.type.is_float32()))return true;
        const auto* small=target.narrow_integer();
        return small && !target.is_char() && value.type==Type::integer && value.constant &&
               value.constant->number>=small->minimum && value.constant->number<=small->maximum;
    }
    static void convert_integer_value(E& value,Type target) {
        if(value->type==target)return;
        auto conversion=std::make_unique<Expr>();
        conversion->kind=Expr::integer_conversion;
        conversion->token=value->token;
        conversion->type=target;
        if(value->constant)conversion->constant=Constant{target,value->constant->number};
        conversion->children.push_back(std::move(value));
        value=std::move(conversion);
    }
    static std::string native_type_key(Type type){if(type.is_reference())return "ref";if(type.has_product_root())return "p"+std::to_string(type.product_id());return std::to_string(type.primitive_id());}
    Function* conversion_function(Type source,Type target,bool allow_implicit,bool allow_explicit){
        auto lookup=[&](bool implicit_conversion)->Function*{const std::string name=(implicit_conversion ? "implicit#" : "explicit#")+native_type_key(source)+"->"+native_type_key(target);auto found=functions.find(name);return found==functions.end() ? nullptr : found->second;};
        if(allow_implicit)if(auto* target_function=lookup(true))return target_function;
        if(allow_explicit)if(auto* target_function=lookup(false))return target_function;
        return nullptr;
    }
    bool implicit_user_conversion(E& value,Type target){
        if(value->type==target)return false;
        auto* target_function=conversion_function(value->type,target,true,false);
        if(!target_function)return false;
        auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=Token{target_function->token.text,value->token.at,Token::id};call->symbol=target_function->symbol;call->type=target_function->type;call->children.push_back(std::move(value));value=std::move(call);return true;
    }
    Type expr_value(E& value,std::optional<Type> expected) {
        auto actual=expr(*value,expected);
        if(expected && actual!=*expected && implicit_integer_value(*value,*expected)){
            convert_integer_value(value,*expected);
            return *expected;
        }
        if(expected && actual!=*expected && implicit_user_conversion(value,*expected))return *expected;
        if(expected && actual!=*expected && expected->is_class() && actual.is_class()){int cursor=actual.product_id();std::size_t guard=0;bool derives=false;while(cursor>=0 && guard++<32){if(cursor==expected->product_id()){derives=true;break;}cursor=products->definitions[static_cast<std::size_t>(cursor)].base_class;}if(derives){auto wrapper=std::make_unique<std::remove_reference_t<decltype(*value)>>();wrapper->kind=Expr::class_upcast;wrapper->token=value->token;wrapper->nominal=expected->product_id();wrapper->type=*expected;wrapper->children.push_back(std::move(value));value=std::move(wrapper);return *expected;}}
        return actual;
    }
    Type assignment_operand(Expr& e,Type destination) {
        auto operation=std::string_view(e.op).substr(0,e.op.size()-1);
        auto& value=e.children[1];
        const bool shift=native_integer_shift(operation);
        if(!shift && (e.op=="=" || !destination.narrow_integer()))return expr_value(value,destination);
        auto actual=expr(*value,destination);
        if(!shift && !implicit_integer_value(*value,destination))
            fail("AI3107","Compound assignment requires an operand compatible with its destination.",e.token.at);
        auto promoted=native_integer_operator_type(actual);
        if(promoted!=actual)convert_integer_value(value,promoted);
        return promoted;
    }
    std::int64_t fold_double_to_integer(double v,Type target,bool wraps,Location at){
        if(target.narrow_integer() || target.is_char()){
            const auto* small=target.narrow_integer();
            const std::int64_t minimum=small ? static_cast<std::int64_t>(small->minimum) : 0;
            const std::int64_t maximum=small ? static_cast<std::int64_t>(small->maximum) : 65535;
            const double dminimum=static_cast<double>(minimum),dmaximum=static_cast<double>(maximum);
            if(wraps){
                if(!(v==v))return 0;
                const std::int64_t converted=v>=2147483648.0 ? INT32_MAX : v<-2147483648.0 ? INT32_MIN : static_cast<std::int64_t>(v);
                return ai_small_wrap(converted,static_cast<std::int32_t>(minimum),static_cast<std::int32_t>(maximum));
            }
            if(!(v>=dminimum && v<=dmaximum))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(v);
        }
        if(target==Type::integer){
            if(wraps){if(!(v==v))return 0;if(v>=2147483648.0)return INT32_MAX;if(v<-2147483648.0)return INT32_MIN;return static_cast<std::int64_t>(v);}
            if(!(v>=-2147483648.0 && v<=2147483647.0))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(v);
        }
        if(target==Type::long_integer){
            if(wraps){if(!(v==v))return 0;if(v>=9223372036854775808.0)return INT64_MAX;if(v<-9223372036854775808.0)return INT64_MIN;return static_cast<std::int64_t>(v);}
            if(!(v>=-9223372036854775808.0 && v<9223372036854775808.0))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(v);
        }
        if(target==Type::unsigned_integer){
            if(wraps){if(!(v==v) || v<=0.0)return 0;if(v>=4294967296.0)return UINT32_MAX;return static_cast<std::int64_t>(static_cast<std::uint32_t>(v));}
            if(!(v>=0.0 && v<=4294967295.0))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(static_cast<std::uint32_t>(v));
        }
        if(target==Type::unsigned_long_integer){
            if(wraps){if(!(v==v) || v<=0.0)return 0;if(v>=18446744073709551616.0)return static_cast<std::int64_t>(UINT64_MAX);return static_cast<std::int64_t>(static_cast<std::uint64_t>(v));}
            if(!(v>=0.0 && v<18446744073709551616.0))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(static_cast<std::uint64_t>(v));
        }
        fail("AI3111","Unsupported floating constant conversion.",at);
    }
    std::int64_t fold_float_to_integer(float value,Type target,bool wraps,Location at){
        const double v=static_cast<double>(value);
        if(target.narrow_integer() || target.is_char()){
            const auto* small=target.narrow_integer();
            const std::int64_t minimum=small ? static_cast<std::int64_t>(small->minimum) : 0;
            const std::int64_t maximum=small ? static_cast<std::int64_t>(small->maximum) : 65535;
            const double dminimum=static_cast<double>(minimum),dmaximum=static_cast<double>(maximum);
            if(wraps){
                if(!(value==value))return 0;
                const std::int64_t converted=v>=2147483648.0 ? INT32_MAX : v<-2147483648.0 ? INT32_MIN : static_cast<std::int64_t>(v);
                return ai_small_wrap(converted,static_cast<std::int32_t>(minimum),static_cast<std::int32_t>(maximum));
            }
            if(!(v>=dminimum && v<=dmaximum))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(v);
        }
        if(target==Type::integer){
            if(wraps){if(!(value==value))return 0;if(v>=2147483648.0)return INT32_MAX;if(v<-2147483648.0)return INT32_MIN;return static_cast<std::int64_t>(v);}
            if(!(v>=-2147483648.0 && v<=2147483647.0))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(v);
        }
        if(target==Type::long_integer){
            if(wraps){if(!(value==value))return 0;if(v>=9223372036854775808.0)return INT64_MAX;if(v<-9223372036854775808.0)return INT64_MIN;return static_cast<std::int64_t>(v);}
            if(!(v>=-9223372036854775808.0 && v<9223372036854775808.0))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(v);
        }
        if(target==Type::unsigned_integer){
            if(wraps){if(!(value==value) || v<=0.0)return 0;if(v>=4294967296.0)return UINT32_MAX;return static_cast<std::int64_t>(static_cast<std::uint32_t>(v));}
            if(!(v>=0.0 && v<=4294967295.0))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(static_cast<std::uint32_t>(v));
        }
        if(target==Type::unsigned_long_integer){
            if(wraps){if(!(value==value) || v<=0.0)return 0;if(v>=18446744073709551616.0)return static_cast<std::int64_t>(UINT64_MAX);return static_cast<std::int64_t>(static_cast<std::uint64_t>(v));}
            if(!(v>=0.0 && v<18446744073709551616.0))fail("AI3111","Constant floating conversion exceeds the destination range.",at);
            return static_cast<std::int64_t>(static_cast<std::uint64_t>(v));
        }
        fail("AI3111","Unsupported floating constant conversion.",at);
    }
    Type bind_default(Expr& e,std::optional<Type> expected) {
        Type type;
        if(e.op=="typed") type=Type::from_shape(products,e.sizeof_shape,e.token.at);
        else { if(!expected)fail("AI3107","A default literal requires a target type.",e.token.at); type=*expected; }
        if(type.is_reference() || type.is_product() || type.is_union() || type==Type::buffer || type.is_type_token())
            fail("AI3107","default of aggregate or reference types is not implemented by the native bootstrap.",e.token.at);
        if(!(type.is_integer() || type.is_char() || type==Type::boolean || type.is_float32() || type.is_float64() || type.is_decimal() || type.is_enum()))
            fail("AI3107","default requires a supported value type.",e.token.at);
        e.kind=Expr::literal;e.type=type;e.value=0;e.op.clear();e.constant=Constant{type,0,0};
        return type;
    }
    Type bind_as(Expr& e){
        auto source=expr(*e.children[0]);
        auto target=Type::from_shape(products,e.sizeof_shape,e.token.at);
        if(target==Type::string && source==Type::object){e.op="as-string";e.type=target;return target;}
        if(target==Type::string && source==Type::string){e.op="as-identity";e.type=target;return target;}
        if(target==Type::object && source==Type::object){e.op="as-identity";e.type=target;return target;}
        fail("AI3107","as supports only object-to-string, string-to-string and object-to-object reference conversions.",e.token.at);
    }
    Type bind_index(Expr& e){
        if(e.children[0]->kind==Expr::field){auto holder=expr(*e.children[0]->children[0]);if(holder.is_product()){auto& definition=holder.definition();auto name=canonical(e.children[0]->token.text);auto found=std::find_if(definition.fixed_buffers.begin(),definition.fixed_buffers.end(),[&](auto& b){return b.name==name;});if(found!=definition.fixed_buffers.end()){auto index=expr_value(e.children[1],Type::integer);if(index!=Type::integer || !e.children[1]->constant)fail("AI3107","A fixed buffer index must be a constant Int32.",e.children[1]->token.at);auto position=static_cast<int>(e.children[1]->value);if(position<0 || position>=found->length)fail("AI3107","Fixed buffer index is outside its declared length.",e.children[1]->token.at);auto field_value=std::make_unique<Expr>();field_value->kind=Expr::field;field_value->token=Token{found->name+std::to_string(position),e.token.at,Token::id};field_value->children.push_back(std::move(e.children[0]->children[0]));e=std::move(*field_value);return expr(e);}}}
        auto base=expr(*e.children[0]);
        if(base!=Type::args)fail("AI3107","Native indexing is only implemented for the implicit args parameter.",e.token.at);
        auto index=expr_value(e.children[1],Type::integer);if(index!=Type::integer)fail("AI3107","args indexing requires an Int32 index.",e.children[1]->token.at);
        e.type=Type::string;return e.type;
    }
    Type bind_args_length(Expr& e){e.type=Type::integer;return e.type;}
    Type bind_nameof(Expr& e){
        auto name=canonical(e.token.text);
        bool known=bound_name(e.token);
        if(!known && products){for(auto& definition:products->definitions)if(definition.name==name){known=true;break;}if(!known)for(auto& generic:products->generics)if(generic.name==name){known=true;break;}}
        if(!known)known=functions.find(name)!=functions.end();
        if(!known)fail("AI3103","nameof requires a bound symbol or declared type name.",e.token.at);
        e.type=Type::string;return e.type;
    }
    Type bind_is_test(Expr& e){
        if(e.children.size()!=2)fail("AI3100","Malformed native is-test.",e.token.at);
        auto subject=expr(*e.children[0]);
        if(!subject.is_union() || subject.is_payload_union())fail("AI3107","The bootstrap is-test requires a closed non-payload native union value.",e.token.at);
        if(!subject.is_copy())fail("AI3107","The bootstrap is-test requires a copyable native union value.",e.token.at);
        auto& right=*e.children[1];
        if(right.kind!=Expr::name)fail("AI3107","The bootstrap is-test requires a declared union case name.",right.token.at);
        auto name=canonical(right.token.text);auto& definition=subject.definition();
        int index=-1;for(std::size_t i=0;i<definition.cases.size();++i)if(definition.cases[i].name==name){index=static_cast<int>(i);break;}
        if(index<0)fail("AI3107","The is-test names a case that the native union does not declare.",right.token.at);
        e.symbol=index;e.type=Type::boolean;return e.type;
    }
    Type bind_initializer(Expr& e){
        const std::string name=canonical(e.token.text);
        int id=-1;
        if(products)for(std::size_t i=0;i<products->definitions.size();++i){auto& definition=products->definitions[i];if(definition.generic<0 && definition.name==name){id=static_cast<int>(i);break;}}
        if(id<0)fail("AI3107","An initializer requires a declared non-generic native product type.",e.token.at);
        auto type=Type::from_shape(products,{Type::product,id,{}},e.token.at);auto& definition=type.definition();
        std::vector<int> source(definition.fields.size(),-1);std::set<int> seen;int position=0;
        for(auto& update:e.with_updates){
            auto field_name=canonical(update.first.text);int index=-1;
            for(std::size_t i=0;i<definition.fields.size();++i)if(definition.fields[i].name==field_name){index=static_cast<int>(i);break;}
            if(index<0)fail("AI3107","The initializer names a field that the native product does not declare.",update.first.at);
            if(!seen.insert(index).second)fail("AI3107","The initializer sets a field more than once.",update.first.at);
            source[static_cast<std::size_t>(index)]=position++;
        }
        std::vector<E> fields;fields.reserve(definition.fields.size());
        for(std::size_t index=0;index<definition.fields.size();++index){
            auto& field=definition.fields[index];
            if(source[index]>=0){fields.push_back(std::move(e.with_updates[static_cast<std::size_t>(source[index])].second));continue;}
            if(field.required_field)fail("AI3107","A required native product field must be set by the initializer.",field.at);
            auto zero=std::make_unique<Expr>();zero->kind=Expr::literal;zero->token=e.token;zero->type=type.field_type(index);zero->value=0;zero->constant=Constant{zero->type,0,0};
            if(!(zero->type.is_integer() || zero->type.is_char() || zero->type==Type::boolean || zero->type.is_float32() || zero->type.is_float64() || zero->type.is_decimal() || zero->type.is_enum()))
                fail("AI3107","An initializer may omit only scalar value fields.",field.at);
            fields.push_back(std::move(zero));
        }
        e.kind=Expr::construct;e.with_updates.clear();e.children=std::move(fields);e.nominal=id;
        return construct_product(e,id);
    }
    Type bind_with(Expr& e){
        if(e.children.size()!=1)fail("AI3100","Malformed native with expression.",e.token.at);
        auto subject=expr(*e.children[0]);
        if(!subject.is_product())fail("AI3107","with requires a native product value.",e.token.at);
        if(subject.is_union())fail("AI3107","with does not support native union values.",e.token.at);
        if(!subject.is_copy())fail("AI3107","with requires a copyable native product.",e.token.at);
        auto& definition=subject.definition();std::set<std::string> updated;
        for(auto& update:e.with_updates){
            auto name=canonical(update.first.text);auto found=std::find_if(definition.fields.begin(),definition.fields.end(),[&](auto& field){return field.name==name;});
            if(found==definition.fields.end())fail("AI3107","with names a field that the native product does not declare.",update.first.at);
            auto index=static_cast<std::size_t>(found-definition.fields.begin());
            if(!updated.insert(name).second)fail("AI3107","with updates a field more than once.",update.first.at);
            if(found->protected_field)fail("AI3107","with cannot update a protected field in the native bootstrap.",update.first.at);
            if(found->private_field && active_drop_for!=subject.product_id())fail("AI3107","with cannot update a private field outside its product destructor.",update.first.at);
            if(found->readonly)fail("AI3107","with cannot update a readonly field.",update.first.at);
            if(found->volatile_field)fail("AI3107","with cannot update a volatile field.",update.first.at);
            auto field_type=subject.field_type(index);auto value=expr_value(update.second,field_type);require_value(value,field_type,update.second->token.at,update.second.get());
        }
        return e.type=subject;
    }
    Type bind_params_pack(Expr& e) {
        if(e.children.size()>256)fail("AI3100","A native params expansion exceeds its argument limit.",e.token.at);
        for(auto& argument:e.children){auto type=expr_value(argument,Type::integer);require_value(type,Type::integer,argument->token.at,argument.get());}
        return e.type=Type::buffer;
    }
    Type bind_integer_conversion(Expr& e) {
        auto& value=*e.children[0];
        if(e.op=="object-box"){
            auto source=expr(value);
            if(!(source.is_scalar() || source==Type::string))fail("AI3107","Native object boxing supports scalar and string values.",e.token.at);
            e.field_index=source.primitive_id();return e.type;
        }
        auto object_hint=type_hint(value);
        if(object_hint && (*object_hint==Type::object || *object_hint==Type::dynamic) && e.op!="object-box"){
            auto source=expr(value);
            auto target=e.type;
            if(!(target.is_scalar() || target==Type::string))fail("AI3107","Native object unboxing supports scalar and string targets.",e.token.at);
            e.op="object-unbox";e.field_index=target.primitive_id();e.type=target;return e.type;
        }
        if(e.op=="enum-cast"){
            auto type=Type::from_shape(products,e.sizeof_shape,e.token.at);
            if(!type.is_enum() && !type.is_product())fail("AI3107","A cast target must name a native enum, product or numeric type.",e.token.at);
            e.type=type;e.op.clear();
        }
        if(e.op=="alias-cast"){
            auto type=Type::from_shape(products,e.sizeof_shape,e.token.at);
            if(type.is_product() && !type.is_enum())fail("AI3107","A cast target alias must name a scalar or enum type.",e.token.at);
            e.type=type;e.op.clear();
        }
        auto source=expr(value);
        if(source!=e.type && (source.is_product() || e.type.is_product())){
            auto* target_function=conversion_function(source,e.type,true,true);
            if(!target_function)fail("AI3107","No explicit or implicit native conversion operator is declared.",e.token.at);
            e.kind=Expr::call;e.token=Token{target_function->token.text,e.token.at,Token::id};e.symbol=target_function->symbol;e.op.clear();e.type=target_function->type;
            return e.type;
        }
        if(source.is_decimal() || e.type.is_decimal()){
            if(e.type.is_decimal()){
                if(value.constant){
                    ai_decimal decimal{};int error=0;
                    if(source.is_integer()){
                        const bool unsigned_source=source.is_uint32() || source.is_ulong64();
                        error=unsigned_source ? ai_dec_core_from_u64(static_cast<std::uint64_t>(value.constant->number),&decimal) : ai_dec_core_from_i64(value.constant->number,&decimal);
                    }else if(source.is_float32()){
                        error=ai_dec_core_from_float(std::bit_cast<float>(static_cast<std::uint32_t>(value.constant->number)),&decimal);
                    }else if(source.is_float64()){
                        error=ai_dec_core_from_double(std::bit_cast<double>(static_cast<std::uint64_t>(value.constant->number)),&decimal);
                    }else fail("AI3107","An integer, Single or Double value can convert explicitly to Decimal.",e.token.at);
                    if(error)fail("AI3111","Constant conversion to Decimal exceeds the System.Decimal range.",e.token.at);
                    e.constant=constant_from_decimal(decimal);
                }
                return e.type;
            }
            if(!source.is_decimal())fail("AI3107","A Decimal conversion requires a Decimal operand.",e.token.at);
            if(value.constant){
                const ai_decimal decimal=decimal_from_constant(*value.constant);
                if(e.type.is_integer())e.constant=Constant{e.type,decimal_to_integer_constant(decimal,e.type,e.token.at)};
                else if(e.type.is_float32()){double value_double=0;ai_dec_to_double_value(decimal,&value_double);const float converted=static_cast<float>(value_double);e.constant=Constant{e.type,static_cast<std::int64_t>(std::bit_cast<std::uint32_t>(converted))};}
                else if(e.type.is_float64()){double value_double=0;ai_dec_to_double_value(decimal,&value_double);e.constant=Constant{e.type,static_cast<std::int64_t>(std::bit_cast<std::uint64_t>(value_double))};}
                else fail("AI3107","Decimal converts only to integral, Single or Double types.",e.token.at);
            }
            return e.type;
        }
        if(source.is_enum() || e.type.is_enum()){
            if(source.is_enum() && e.type.is_enum()){
                if(source!=e.type)fail("AI3107","A direct conversion between different enums is not implemented.",e.token.at);
                return e.type;
            }
            if(source.is_enum()){
                auto underlying=source.enum_underlying();
                if(underlying!=e.type)fail("AI3107","Only conversions between an enum and its underlying type are implemented.",e.token.at);
                if(value.constant)e.constant=Constant{e.type,value.constant->number};
                return e.type;
            }
            if(!source.is_integer())fail("AI3107","An enum conversion requires an integer operand.",e.token.at);
            auto underlying=e.type.enum_underlying();
            const bool zero=source==Type::integer && value.constant && value.constant->number==0;
            if(source!=underlying && !zero)fail("AI3107","Only conversions between an enum and its underlying type are implemented.",e.token.at);
            if(value.constant)e.constant=Constant{e.type,value.constant->number};
            return e.type;
        }
        if(source.is_float32() || source.is_float64() || e.type.is_float32() || e.type.is_float64()){
            const bool source_numeric=source.is_integer() || source.is_float32() || source.is_float64();
            const bool target_numeric=e.type.is_integer() || e.type.is_float32() || e.type.is_float64();
            if(!source_numeric)fail("AI3107","A floating cast requires a numeric operand.",e.token.at);
            if(!target_numeric)fail("AI3107","A floating cast requires a numeric destination.",e.token.at);
            if(source==e.type)return e.type;
            if(value.constant){
                auto number=value.constant->number;
                const double converted=source.is_float64() ? std::bit_cast<double>(static_cast<std::uint64_t>(number)) :
                    source.is_float32() ? static_cast<double>(std::bit_cast<float>(static_cast<std::uint32_t>(number))) :
                    static_cast<double>(number);
                if(e.type.is_float32()){
                    const float single=static_cast<float>(converted);
                    number=static_cast<std::int64_t>(std::bit_cast<std::uint32_t>(single));
                }else if(e.type.is_float64()){
                    number=static_cast<std::int64_t>(std::bit_cast<std::uint64_t>(converted));
                }else{
                    number=fold_double_to_integer(converted,e.type,e.wraps,e.token.at);
                }
                e.constant=Constant{e.type,number};
            }
            return e.type;
        }
        if(!source.is_integer())
            fail("AI3107","An integer cast requires an integer operand.",e.token.at);
        if(value.constant){
            auto number=value.constant->number;
            if(e.type.is_ulong64()){
                if(source.is_ulong64() || source.is_uint32() || (source.narrow_integer() && source.narrow_integer()->minimum>=0) || source.is_char()){
                    if(number<0)fail("AI3111","Constant integer conversion exceeds the destination range.",e.token.at);
                }else if(number<0 && !e.wraps){
                    fail("AI3111","Constant integer conversion exceeds the destination range.",e.token.at);
                }
                number=static_cast<std::int64_t>(static_cast<std::uint64_t>(number));
            }else if(e.type.is_uint32()){
                if(number<0 || number>4294967295LL){
                    if(!e.wraps)fail("AI3111","Constant integer conversion exceeds the destination range.",e.token.at);
                    number=static_cast<std::int64_t>(static_cast<std::uint32_t>(number));
                }
            }else if(source.is_ulong64() && (e.type==Type::integer || e.type==Type::long_integer || e.type.is_uint32())){
                const std::uint64_t raw=static_cast<std::uint64_t>(number);
                const std::uint64_t maximum=e.type==Type::integer ? INT32_MAX : e.type.is_uint32() ? UINT32_MAX : INT64_MAX;
                if(raw>maximum){
                    if(!e.wraps)fail("AI3111","Constant integer conversion exceeds the destination range.",e.token.at);
                }
                number=e.type==Type::integer ? static_cast<std::int64_t>(static_cast<std::int32_t>(raw)) :
                       e.type.is_uint32() ? static_cast<std::int64_t>(static_cast<std::uint32_t>(raw)) :
                       static_cast<std::int64_t>(raw);
            }else if(source.is_uint32() && e.type==Type::integer){
                if(number>2147483647LL){
                    if(!e.wraps)fail("AI3111","Constant integer conversion exceeds the destination range.",e.token.at);
                    number=constant_integer(number,e);
                }
            }else if(source==Type::long_integer && e.type==Type::integer)number=constant_integer(number,e);
            else if(const auto* small=e.type.narrow_integer();small && (number<small->minimum || number>small->maximum)){
                if(!e.wraps)fail("AI3111","Constant integer conversion exceeds the destination range.",e.token.at);
                number=ai_small_wrap(number,small->minimum,small->maximum);
            }
            e.constant=Constant{e.type,number};
        }
        return e.type;
    }
    std::pair<std::size_t,std::size_t> sizeof_layout(Type type,Location at,int depth) {
        if(depth>64)fail("AI3107","sizeof nesting exceeds the native limit.",at);
        if(type.is_reference())fail("AI3107","sizeof requires an unmanaged value type, not a reference.",at);
        if(type.is_union())fail("AI3107","sizeof is unavailable for native union types.",at);
        if(type.is_product()){
            auto& definition=type.definition();
            if(definition.generic>=0)fail("AI3107","sizeof is unavailable for generic product types.",at);
            std::size_t offset=0,alignment=1;
            for(std::size_t i=0;i<definition.fields.size();++i){
                auto field=type.field_type(i);
                auto [size,field_alignment]=sizeof_layout(field,at,depth+1);
                if(field_alignment>8)field_alignment=8;
                offset=(offset+field_alignment-1)/field_alignment*field_alignment;
                offset+=size;
                if(field_alignment>alignment)alignment=field_alignment;
            }
            if(alignment>8)alignment=8;
            return {((offset+alignment-1)/alignment)*alignment,alignment};
        }
        if(type.is_enum())return sizeof_layout(type.enum_underlying(),at,depth+1);
        if(type.is_decimal())return {16,16};
        if(type==Type::boolean)return {1,1};
        if(type.is_float32())return {4,4};
        if(type.is_float64())return {8,8};
        if(type==Type::integer)return {4,4};
        if(type==Type::long_integer)return {8,8};
        if(type.is_uint32())return {4,4};
        if(type.is_ulong64())return {8,8};
        if(auto small=type.narrow_integer();small){
            const int size=(small->minimum==0 && small->maximum==255) || (small->minimum==-128) ? 1 : 2;
            return {static_cast<std::size_t>(size),static_cast<std::size_t>(size)};
        }
        fail("AI3107","sizeof is unavailable for this native type.",at);
    }
    Type expr_typeof(Expr& e) {
        if(!e.op.empty()){
            const std::int64_t id=e.op=="nint" ? 200 : 201;
            e.kind=Expr::literal;e.type=Type::type_token;e.value=id;e.constant=Constant{e.type,e.value};
            return e.type;
        }
        if(e.sizeof_shape.product<0 && e.sizeof_shape.primitive==Type::product)
            fail("AI3107","typeof requires a fully defined native product type.",e.token.at);
        auto type=Type::from_shape(products,e.sizeof_shape,e.token.at);
        if(type.is_reference())fail("AI3107","typeof cannot name a reference type in the native bootstrap.",e.token.at);
        std::int64_t id=0;
        if(type.has_product_root())id=1000+type.product_id();
        else if(type==Type::integer)id=0;
        else if(type==Type::boolean)id=1;
        else if(type==Type::unit)id=2;
        else if(type==Type::buffer)id=3;
        else if(type==Type::long_integer)id=5;
        else if(auto small=type.narrow_integer();small)id=small->primitive;
        else if(type.is_uint32())id=11;
        else if(type.is_ulong64())id=12;
        else if(type.is_float32())id=13;
        else if(type.is_float64())id=14;
        else if(type.is_decimal())id=16;
        else fail("AI3107","typeof is unavailable for this native type.",e.token.at);
        e.kind=Expr::literal;e.type=Type::type_token;e.value=id;e.constant=Constant{e.type,e.value};
        return e.type;
    }
    Type expr_sizeof(Expr& e) {
        if(e.sizeof_shape.product<0 && e.sizeof_shape.primitive==Type::product)
            fail("AI3107","sizeof requires a fully defined native product type.",e.token.at);
        auto type=Type::from_shape(products,e.sizeof_shape,e.token.at);
        auto [size,alignment]=sizeof_layout(type,e.token.at,0);
        (void)alignment;
        e.kind=Expr::literal;e.type=Type::integer;e.value=static_cast<std::int64_t>(size);e.constant=Constant{e.type,e.value};
        return e.type;
    }
    Type bind_stack_alloc(Expr& e) {
        if(!stack_alloc_allowed)fail("AI3107","stackalloc is supported only as a direct local initializer.",e.token.at);
        if(e.op!="int" && e.op!="i32")fail("AI3107","The bootstrap stackalloc supports only unmanaged int32 elements.",e.token.at);
        if(e.children.size()!=1)fail("AI3100","Malformed stackalloc expression.",e.token.at);
        auto count=expr_value(e.children[0],Type::integer);
        if(count!=Type::integer)fail("AI3107","The stackalloc element count must be Int32.",e.children[0]->token.at);
        if(!e.children[0]->constant || e.children[0]->constant->number<=0)fail("AI3107","The stackalloc element count must be a positive constant.",e.children[0]->token.at);
        const std::int64_t bytes=e.children[0]->constant->number*4;
        if(bytes>4096)fail("AI3107","The bootstrap stackalloc limit is 1024 int32 elements.",e.children[0]->token.at);
        e.value=bytes;return e.type=Type::buffer;
    }
    bool managed_conversion(Expr& e,int product_id){
        auto type=Type::from_shape(products,{Type::product,product_id,{}},e.token.at);
        if(!type.is_product() || !type.definition().managed_pointer)return false;
        auto& definition=type.definition();
        if(e.children.size()!=1 || e.children[0]->kind!=Expr::name)fail("AI3107","A managed function pointer conversion requires a function name.",e.token.at);
        const auto target_name=canonical(e.children[0]->token.text);auto target=std::find(definition.managed_targets.begin(),definition.managed_targets.end(),target_name);
        if(target==definition.managed_targets.end())fail("AI3107","Function signature is incompatible with the managed function pointer type.",e.children[0]->token.at);
        auto index_literal=std::make_unique<Expr>();index_literal->kind=Expr::literal;index_literal->token=e.children[0]->token;index_literal->type=Type::integer;index_literal->value=static_cast<std::int64_t>(target-definition.managed_targets.begin());index_literal->constant=Constant{Type::integer,index_literal->value,0};
        e.kind=Expr::construct;e.nominal=product_id;e.children.clear();e.children.push_back(std::move(index_literal));return true;
    }
    Type bind_class_downcast(Expr& e){if(e.children.size()!=1 || e.nominal<0)fail("AI3100","Malformed class downcast.",e.token.at);auto source=expr(*e.children[0]);if(!source.is_class())fail("AI3107","A class downcast requires a class receiver.",e.token.at);int cursor=e.nominal;std::size_t guard=0;bool derives=false;while(cursor>=0 && guard++<32){if(cursor==source.product_id()){derives=true;break;}cursor=products->definitions[static_cast<std::size_t>(cursor)].base_class;}if(!derives)fail("AI3107","Downcast target does not derive from the source class.",e.token.at);return e.type=Type::from_shape(products,{Type::class_ref,e.nominal,{}},e.token.at);}
    Type bind_query_select(Expr& e){if(e.children.empty() || e.children.size()>2)fail("AI3100","Malformed query expression.",e.token.at);auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=Token{e.op,e.token.at,Token::id};for(auto& child:e.children){auto source=expr_value(child,Type::buffer);if(source!=Type::buffer)fail("AI3107","Query source must be an owned buffer in the native bootstrap.",child->token.at);auto borrow=std::make_unique<Expr>();borrow->kind=Expr::borrow;borrow->op="shared";borrow->token=e.token;borrow->children.push_back(std::move(child));call->children.push_back(std::move(borrow));}e=std::move(*call);return expr(e);}
    Type bind_await_value(Expr& e){if(e.children.size()!=1)fail("AI3100","await requires an operand.",e.token.at);e.type=expr(*e.children[0]);return e.type;}
    Type bind_fixed_pin(Expr& e){if(e.children.size()!=1)fail("AI3100","Malformed fixed block operand.",e.token.at);auto type=expr(*e.children[0]);if(type!=Type::buffer && !(type.is_reference() && type.pointee()==Type::buffer))fail("AI3107","A fixed block requires an owned buffer operand.",e.token.at);return e.type=type;}
    Type bind_class_tag(Expr& e){if(e.children.size()!=1)fail("AI3100","Malformed class tag query.",e.token.at);auto value=expr(*e.children[0]);if(!value.is_class())fail("AI3107","Class tag requires a class receiver.",e.token.at);return e.type=Type::integer;}
    Type bind_class_upcast(Expr& e){if(e.children.size()!=1 || e.nominal<0)fail("AI3100","Malformed class upcast.",e.token.at);auto source=expr(*e.children[0]);if(!source.is_class())fail("AI3107","base requires a class instance receiver.",e.token.at);int cursor=source.product_id();std::size_t guard=0;bool derives=false;while(cursor>=0 && guard++<32){if(cursor==e.nominal){derives=true;break;}cursor=products->definitions[static_cast<std::size_t>(cursor)].base_class;}if(!derives)fail("AI3107","Receiver is not derived from the requested base class.",e.token.at);return e.type=Type::from_shape(products,{Type::class_ref,e.nominal,{}},e.token.at);}
    Type bind_class_new(Expr& e){auto type=Type::from_shape(products,{Type::class_ref,e.nominal,{}},e.token.at);for(std::size_t i=0;i<e.children.size();++i){auto expected=type.field_type(i);require_value(expr_value(e.children[i],expected),expected,e.children[i]->token.at,e.children[i].get());}return e.type=type;}
    Type bind_class_field_assign(Expr& e){auto type=Type::from_shape(products,{Type::class_ref,e.nominal,{}},e.token.at);auto field_type=type.field_type(static_cast<std::size_t>(e.field_index));auto handle=expr(*e.children[0]);if(handle!=type)fail("AI3107","Class field assignment requires a matching class receiver.",e.token.at);require_value(expr_value(e.children[1],field_type),field_type,e.children[1]->token.at,e.children[1].get());e.type=Type::unit;return e.type;}
    bool class_field_info(const Type& type,const std::string& name,int& owner,int& index){
        int current=type.product_id();std::size_t guard=0;
        while(current>=0 && guard++<32){auto& definition=products->definitions[static_cast<std::size_t>(current)];for(std::size_t i=0;i<definition.fields.size();++i)if(definition.fields[i].name==name){owner=current;index=static_cast<int>(i);return true;}current=definition.base_class;}
        return false;
    }
    Type expr_impl(Expr& e,std::optional<Type> expected) {
        if(e.kind==Expr::name && e.token.text=="field" && current_function && (current_function->accessor_get || current_function->accessor_set)){
            auto base=std::make_unique<Expr>();base->kind=Expr::name;base->token=Token{"this",e.token.at,Token::id};
            e.kind=Expr::field;e.token=Token{"_ai_field_"+current_function->property_name,e.token.at,Token::id};e.children.clear();e.children.push_back(std::move(base));return expr(e,expected);
        }
        if(e.kind==Expr::assign && e.children[0]->kind==Expr::name && e.children[0]->token.text=="field" && current_function && (current_function->accessor_get || current_function->accessor_set)){
            auto base=std::make_unique<Expr>();base->kind=Expr::name;base->token=Token{"this",e.children[0]->token.at,Token::id};
            auto target=std::make_unique<Expr>();target->kind=Expr::field;target->token=Token{"_ai_field_"+current_function->property_name,e.children[0]->token.at,Token::id};target->children.push_back(std::move(base));
            e.children[0]=std::move(target);
        }
        if(e.kind==Expr::field){
            auto base=expr(*e.children[0]);
            if(base.is_class()){
                int owner=-1,index=-1;if(!class_field_info(base,canonical(e.token.text),owner,index))fail("AI3103","Class has no field with this name.",e.token.at);
                auto owner_type=Type::from_shape(products,{Type::class_ref,owner,{}},e.token.at);
                e.op="class-field";e.field_index=index;e.nominal=owner;e.type=owner_type.field_type(static_cast<std::size_t>(index));return e.type;
            }
            if(base.is_product() && !base.is_reference()){auto found=property_getters.find({base.product_id(),canonical(e.token.text)});if(found!=property_getters.end()){auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=found->second->token;call->children.push_back(std::move(e.children[0]));e=std::move(*call);return expr(e,expected);}}
        }
        if(e.kind==Expr::assign && e.op=="=" && e.children[0]->kind==Expr::field){
            auto base=expr(*e.children[0]->children[0]);
            if(base.is_product() && !base.is_reference()){auto found=property_setters.find({base.product_id(),canonical(e.children[0]->token.text)});if(found!=property_setters.end()){
                if(e.children[0]->children[0]->kind!=Expr::name)fail("AI3107","A struct property assignment requires a simple local receiver.",e.children[0]->children[0]->token.at);
                auto target=std::make_unique<Expr>();target->kind=Expr::name;target->token=e.children[0]->children[0]->token;
                auto receiver=std::move(e.children[0]->children[0]);auto value=std::move(e.children[1]);
                auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=found->second->token;call->children.push_back(std::move(receiver));call->children.push_back(std::move(value));
                auto assign=std::make_unique<Expr>();assign->kind=Expr::assign;assign->op="=";assign->token=e.token;assign->children.push_back(std::move(target));assign->children.push_back(std::move(call));
                e=std::move(*assign);return expr(e,expected);
            }}
        }
        if(e.kind==Expr::assign && (e.op=="+=" || e.op=="-=") && e.children[0]->kind==Expr::field){
            auto base=expr(*e.children[0]->children[0]);if(base.is_class()){auto& definition=base.definition();auto event_name=canonical(e.children[0]->token.text);auto event=std::find_if(definition.events.begin(),definition.events.end(),[&](auto& ev){return ev.name==event_name;});if(event!=definition.events.end()){int class_id=base.product_id();auto accessor=(e.op=="+=" ? event_adders : event_removers).find({class_id,event_name});auto call=std::make_unique<Expr>();call->kind=Expr::call;if(accessor!=(e.op=="+=" ? event_adders : event_removers).end()){call->token=accessor->second->token;call->token.at=e.token.at;call->children.push_back(std::move(e.children[0]->children[0]));call->children.push_back(std::move(e.children[1]));}else{call->token=Token{"$event_"+std::string(e.op=="+=" ? "add" : "remove")+"$"+std::to_string(class_id)+"$"+event_name,e.token.at,Token::id};call->children.push_back(std::move(e.children[0]->children[0]));call->children.push_back(std::move(e.children[1]));}e=std::move(*call);return expr(e,expected);}}
        }
        if(e.kind==Expr::assign && e.op=="=" && e.children[0]->kind==Expr::field){
            auto base=expr(*e.children[0]->children[0]);
            if(base.is_class()){
                int owner=-1,index=-1;if(!class_field_info(base,canonical(e.children[0]->token.text),owner,index))fail("AI3103","Class has no field with this name.",e.children[0]->token.at);
                auto owner_type=Type::from_shape(products,{Type::class_ref,owner,{}},e.children[0]->token.at);auto field_type=owner_type.field_type(static_cast<std::size_t>(index));require_value(expr_value(e.children[1],field_type),field_type,e.children[1]->token.at,e.children[1].get());auto rhs=std::move(e.children[1]);
                e.kind=Expr::class_field_assign;e.nominal=owner;e.field_index=index;e.op.clear();auto receiver=std::move(e.children[0]->children[0]);e.children.clear();e.children.push_back(std::move(receiver));e.children.push_back(std::move(rhs));e.type=Type::unit;return e.type;
            }
        }
        switch(e.kind) {
        case Expr::integer_conversion:return bind_integer_conversion(e);
        case Expr::sizeof_type:return expr_sizeof(e);
        case Expr::typeof_type:return expr_typeof(e);
        case Expr::params_pack:return bind_params_pack(e);
        case Expr::with_expr:return bind_with(e);
        case Expr::initializer:return bind_initializer(e);
        case Expr::is_test:return bind_is_test(e);
        case Expr::nameof_sym:return bind_nameof(e);
        case Expr::index:return bind_index(e);
        case Expr::as_test:return bind_as(e);
        case Expr::stack_alloc:return bind_stack_alloc(e);
        case Expr::class_new:return bind_class_new(e);
        case Expr::class_field_assign:return bind_class_field_assign(e);
        case Expr::class_upcast:return bind_class_upcast(e);
        case Expr::class_tag:return bind_class_tag(e);
        case Expr::fixed_pin:return bind_fixed_pin(e);
        case Expr::await_value:return bind_await_value(e);
        case Expr::query_select:return bind_query_select(e);
        case Expr::class_downcast:return bind_class_downcast(e);
        case Expr::args_length:return bind_args_length(e);
        case Expr::default_value:return bind_default(e,std::move(expected));
        case Expr::literal:
            if(e.type==Type::string)return e.type;
            if(e.long_minimum_magnitude)fail("AI3107","Positive literal is outside Int64.",e.token.at);
            if(e.type==Type::integer && e.value>2147483647LL)fail("AI3107","Positive literal is outside Int32.",e.token.at);
            if(e.type.is_uint32() && (e.value<0 || e.value>4294967295LL))fail("AI3107","Positive literal is outside UInt32.",e.token.at);
            e.constant=Constant{e.type,e.value,e.type.is_decimal()&&e.constant ? e.constant->number2 : 0};
            return e.type;
        case Expr::name: {if(canonical(e.token.text)=="args" && !bound_name(e.token)){if(!current_function || canonical(current_function->token.text)!="main")fail("AI3107","args is only available as the implicit top-level parameter of main.",e.token.at);e.kind=Expr::args_value;e.type=Type::args;return e.type;}if(e.nominal>=0)fail("AI3107","A generic type application is not a value.",e.token.at);if(!bound_name(e.token))if(auto value=case_value(e.token,expected)){return bind_case_value(e,*value);}auto b=lookup(e.token,true);e.symbol=b.id;e.type=b.type;if(guard_bindings.contains(b.id) && !b.type.is_copy())reference_failure("AI3115","A guard cannot consume a payload before arm selection.",e);e.symbol=b.id;e.constant=b.constant;if(!b.type.is_copy())assigned.erase(b.id);return e.type=b.type;}
        case Expr::unit_value:return e.type=Type::unit;
        case Expr::propagate:return propagate(e,expected);
        case Expr::variant:return e.type;
        case Expr::match:return bind_match(e,expected);
        case Expr::field:{if(canonical(e.token.text)=="length" && e.children[0]->kind==Expr::name){bool is_args_base=canonical(e.children[0]->token.text)=="args" && !bound_name(e.children[0]->token);if(!is_args_base && bound_name(e.children[0]->token))is_args_base=lookup(e.children[0]->token,false).type==Type::args;if(is_args_base){auto base=expr(*e.children[0]);if(base!=Type::args)fail("AI3107","length is only implemented for the implicit args parameter.",e.token.at);auto name=std::make_unique<Expr>();name->kind=Expr::args_length;name->token=e.children[0]->token;name->children.push_back(std::move(e.children[0]));e=std::move(*name);e.type=Type::integer;return e.type;}}if(e.children[0]->kind==Expr::name && !bound_name(e.children[0]->token) && canonical(e.children[0]->token.text)=="decimal"){
            auto member=canonical(e.token.text);ai_decimal constant{};bool found=true;
            if(member=="MaxValue"){if(ai_dec_core_from_text("79228162514264337593543950335",&constant))fail("AI3100","Native Decimal MaxValue constant failed.",e.token.at);}
            else if(member=="MinValue"){if(ai_dec_core_from_text("79228162514264337593543950335",&constant))fail("AI3100","Native Decimal MinValue constant failed.",e.token.at);constant.flags^=0x80000000u;}
            else if(member=="Zero")constant=ai_dec_make(0,0,0,0);
            else if(member=="One")constant=ai_dec_make(1,0,0,0);
            else found=false;
            if(found){e.kind=Expr::literal;e.type=Type::decimal;e.value=static_cast<std::int64_t>((static_cast<std::uint64_t>(constant.w1)<<32)|constant.w0);e.constant=Constant{e.type,e.value,static_cast<std::int64_t>((static_cast<std::uint64_t>(constant.flags)<<32)|constant.w2)};return e.type;}
        }if(e.children[0]->kind==Expr::name && !bound_name(e.children[0]->token) && products){auto type_name=canonical(e.children[0]->token.text);for(std::size_t id=0;id<products->definitions.size();++id){auto& definition=products->definitions[id];if(!definition.enum_ || definition.generic>=0 || definition.name!=type_name)continue;auto member=canonical(e.token.text);for(auto& item:definition.enum_members)if(item.name==member){e.kind=Expr::literal;e.type=Type::from_shape(products,{Type::product,static_cast<int>(id),{}},e.token.at);e.value=item.value;e.constant=Constant{e.type,e.value};return e.type;}fail("AI3107","Unknown native enum member.",e.token.at);}}if(auto value=qualified_case(e)){e.children.clear();return bind_case_value(e,*value);}auto base=stable_place(*e.children[0]) ? place_type(*e.children[0],false) : expr(*e.children[0]);auto result=member_type(e,base);if(!result.is_copy() && guarded_payload(e))reference_failure("AI3115","A guard cannot consume a nested payload before arm selection.",e);if(!result.is_copy() && borrowed_place(e))reference_failure("AI3115","Cannot move a non-Copy field out of borrowed storage.",e);if(!stable_place(e))e.op="temporary";return result;}
        case Expr::construct:fail("AI3100","Unbound product constructor node.",e.token.at);
        case Expr::borrow: {bool previous=stack_borrow_allowed;stack_borrow_allowed=true;auto target=place_type(*e.children[0],e.op=="exclusive",true);stack_borrow_allowed=previous;return e.type=Type::reference(target,e.op=="exclusive" ? Type::Borrow::exclusive : Type::Borrow::shared,e.token.at);}
        case Expr::dereference: {auto pointer=reference_access(*e.children[0],false);e.type=pointer.pointee();if(!e.type.is_copy())reference_failure("AI3115","Cannot move a non-Copy value out of borrowed storage.",e);return e.type;}
        case Expr::overflow_context:
            e.type=expr(*e.children[0]);e.constant=e.children[0]->constant;return e.type;
        case Expr::call: {
            if(e.op=="member-call" && e.children[0]->kind==Expr::name && canonical(e.children[0]->token.text)=="base"){
                if(!current_function || current_function->parameters.empty())fail("AI3107","base is available only inside instance members.",e.token.at);
                auto receiver=current_function->parameters[0].type;if(!receiver.is_class())fail("AI3107","base requires a class instance member.",e.token.at);
                int base_class=receiver.definition().base_class;if(base_class<0)fail("AI3107","Class has no base class.",e.token.at);
                auto this_name=std::make_unique<Expr>();this_name->kind=Expr::name;this_name->token=Token{"this",e.token.at,Token::id};auto this_value=std::make_unique<Expr>();this_value->kind=Expr::class_upcast;this_value->token=Token{"base",e.token.at,Token::id};this_value->nominal=receiver.definition().base_class;this_value->children.push_back(std::move(this_name));
                if(canonical(e.token.text)=="new"){
                    auto found_constructor=constructors.find(base_class);if(found_constructor==constructors.end())fail("AI3107","Base class has no constructor.",e.token.at);
                    auto* function=found_constructor->second;auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=function->token;call->token.at=e.token.at;call->children.push_back(std::move(this_value));for(auto& argument:e.children)if(argument.get()!=e.children[0].get())call->children.push_back(std::move(argument));e=std::move(*call);return expr(e,expected);
                }
                int current=base_class;Function* found=nullptr;while(current>=0){auto exact=extension_methods.find({current,canonical(e.token.text)});if(exact!=extension_methods.end()){found=exact->second;break;}current=products->definitions[static_cast<std::size_t>(current)].base_class;}
                if(!found)fail("AI3103","Base class has no member function with this name.",e.token.at);
                auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=found->token;call->token.at=e.token.at;call->children.push_back(std::move(this_value));for(auto& argument:e.children)if(argument.get()!=e.children[0].get())call->children.push_back(std::move(argument));e=std::move(*call);return expr(e,expected);
            }
            if(e.op=="member-call"){
                const Location call_site=e.token.at;
                auto receiver_type=expr(*e.children[0]);
                if(!(receiver_type.is_product() || receiver_type.is_class()) || receiver_type.is_reference())fail("AI3107","Member invocation requires a struct or class receiver.",e.children[0]->token.at);
                auto found=extension_methods.end();int member_class=receiver_type.product_id();std::size_t member_guard=0;while(member_class>=0 && member_guard++<32){found=extension_methods.find({member_class,canonical(e.token.text)});if(found!=extension_methods.end())break;member_class=products->definitions[static_cast<std::size_t>(member_class)].base_class;}
                if(found==extension_methods.end())fail("AI3103","Member function is not declared for this struct or class.",e.token.at);
                int expected_receiver=found->second->parameters[0].type.product_id();if(receiver_type.product_id()!=expected_receiver){auto base_receiver=std::make_unique<Expr>();base_receiver->kind=Expr::class_upcast;base_receiver->token=e.children[0]->token;base_receiver->nominal=expected_receiver;base_receiver->children.push_back(std::move(e.children[0]));e.children[0]=std::move(base_receiver);}
                int virtual_class=-1;Function* virtual_declaration=nullptr;int cursor=receiver_type.product_id();std::size_t virtual_guard=0;const auto method_name=canonical(e.token.text);while(cursor>=0 && virtual_guard++<32){auto candidate=extension_methods.find({cursor,method_name});if(candidate!=extension_methods.end() && candidate->second->virtual_method){virtual_class=cursor;virtual_declaration=candidate->second;break;}cursor=products->definitions[static_cast<std::size_t>(cursor)].base_class;}
                if(virtual_class>=0 && virtual_declaration->abstract_method){Function* override_function=nullptr;int concrete=receiver_type.product_id();std::size_t concrete_guard=0;while(concrete>=0 && concrete_guard++<32){auto candidate=extension_methods.find({concrete,method_name});if(candidate!=extension_methods.end() && candidate->second->override_method){override_function=candidate->second;break;}concrete=products->definitions[static_cast<std::size_t>(concrete)].base_class;}if(override_function){auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=override_function->token;call->token.at=e.token.at;int override_receiver=override_function->parameters[0].type.product_id();auto receiver_value=std::move(e.children[0]);if(receiver_type.product_id()!=override_receiver){auto upcast=std::make_unique<Expr>();upcast->kind=Expr::class_upcast;upcast->token=receiver_value->token;upcast->nominal=override_receiver;upcast->children.push_back(std::move(receiver_value));receiver_value=std::move(upcast);}call->children.push_back(std::move(receiver_value));for(std::size_t i=1;i<e.children.size();++i)call->children.push_back(std::move(e.children[i]));e=std::move(*call);return expr(e,expected);}}
                if(virtual_class>=0){e.token=Token{"$virtual$"+std::to_string(virtual_class)+"$"+method_name,e.token.at,Token::id};e.op.clear();if(receiver_type.product_id()!=virtual_class){auto base_receiver=std::make_unique<Expr>();base_receiver->kind=Expr::class_upcast;base_receiver->token=e.children[0]->token;base_receiver->nominal=virtual_class;base_receiver->children.push_back(std::move(e.children[0]));e.children[0]=std::move(base_receiver);}}
                else e.token=found->second->token;e.token.at=call_site;
            }
            for(auto& scope:scopes)if(scope.contains(canonical(e.token.text))){auto binding=scope.at(canonical(e.token.text));if(binding.type.is_product() && !binding.type.is_reference() && binding.type.definition().managed_pointer){auto& definition=binding.type.definition();auto base=std::make_unique<Expr>();base->kind=Expr::name;base->token=e.token;auto field=std::make_unique<Expr>();field->kind=Expr::field;field->token=Token{"id",e.token.at,Token::id};field->children.push_back(std::move(base));e.children.insert(e.children.begin(),std::move(field));e.token=Token{definition.managed_helper,e.token.at,Token::id};e.op.clear();}break;}
            if(current_function && !current_function->parameters.empty() && current_function->parameters[0].type.is_class()){auto& definition=current_function->parameters[0].type.definition();auto event_name=canonical(e.token.text);auto event=std::find_if(definition.events.begin(),definition.events.end(),[&](auto& ev){return ev.name==event_name;});if(event!=definition.events.end()){auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=Token{"$event_invoke$"+std::to_string(definition.name.empty()?0:current_function->parameters[0].type.product_id())+"$"+event_name,e.token.at,Token::id};auto this_value=std::make_unique<Expr>();this_value->kind=Expr::name;this_value->token=Token{"this",e.token.at,Token::id};call->children.push_back(std::move(this_value));for(auto& argument:e.children)call->children.push_back(std::move(argument));e=std::move(*call);return expr(e,expected);}}
            if(e.op=="variant-call")return construct_qualified_variant(e);
            if(e.nominal>=0){if(managed_conversion(e,e.nominal))return construct_product(e,e.nominal);return construct_product(e,e.nominal);}
            for(auto& scope:scopes)if(scope.contains(canonical(e.token.text))){
                auto binding=scope.at(canonical(e.token.text));Diagnostic d("AI3107","A local value is not callable.",e.token.at);
                if(binding.type==Type::buffer){d.ownershipObligation="owned-callability";d.ownerSymbol=binding.id;related(d,"declaration",owner_declarations.at(binding.id));related(d,"owner-use",e.token.at);}
                throw d;
            }
            auto found=functions.find(canonical(e.token.text));
            if(found==functions.end()){
                auto name=canonical(e.token.text);if(auto type=construct_named_variant(e,expected))return *type;std::vector<Type> parameters;Type result;
                if(products){auto product=std::find_if(products->definitions.begin(),products->definitions.end(),[&](auto& p){return p.generic<0 && p.name==name;});if(product!=products->definitions.end()){
                    int product_id=static_cast<int>(product-products->definitions.begin());
                    if(managed_conversion(e,product_id))return construct_product(e,product_id);
                    auto constructor=constructors.find(product_id);
                    if(constructor!=constructors.end()){
                        auto* function=constructor->second;if(e.children.size()+1!=function->parameters.size())fail("AI3107","Constructor argument count differs from its declaration.",e.token.at);
                        auto& definition=products->definitions[static_cast<std::size_t>(product_id)];auto zero=std::make_unique<Expr>();zero->kind=Expr::construct;zero->token=Token{"$zero",product->at,Token::id};zero->nominal=product_id;
                        for(std::size_t i=0;i<definition.fields.size();++i){auto type=Type::from_shape(products,{Type::product,product_id,{}},product->at).field_type(i);if(!(type.is_integer() || type.is_char() || type==Type::boolean || type.is_float32() || type.is_float64() || type.is_decimal() || type.is_enum()))fail("AI3107","A constructor product currently supports only scalar fields.",definition.fields[i].at);auto value=std::make_unique<Expr>();value->kind=Expr::literal;value->token=Token{"$zero",product->at,Token::id};value->type=type;value->value=0;value->constant=Constant{type,0,0};zero->children.push_back(std::move(value));}
                        construct_product(*zero,product_id);
                        std::vector<E> arguments;arguments.push_back(std::move(zero));for(auto& child:e.children)arguments.push_back(std::move(child));
                        e.kind=Expr::call;e.token=function->token;e.op.clear();e.nominal=-1;e.children=std::move(arguments);return expr(e,expected);
                    }
                    return construct_product(e,product_id);
                }
                    for(std::size_t id=0;id<products->generics.size();++id)if(products->generics[id].name==name){if(expected && expected->has_product_root() && !expected->is_reference() && expected->definition().generic==static_cast<int>(id))return construct_product(e,expected->product_id());Diagnostic d("AI3107","Generic constructor requires an explicit application or concrete expected type.",e.token.at);d.related.push_back({"type-declaration",products->generics[id].at});throw d;}
                }
                if(name=="bufferPeek" || name=="bufferSize" || name=="bufferWrite"){
                    if(e.children.empty())fail("AI3107","Intrinsic argument count differs from its declaration.",e.token.at);
                    auto pointer=reference_access(*e.children[0],name=="bufferWrite");
                    if(pointer.pointee()!=Type::buffer || (name=="bufferWrite" && pointer.borrow_kind()!=Type::Borrow::exclusive))fail("AI3115","Buffer access needs the corresponding owned-buffer reference mode.",e.children[0]->token.at);
                    std::size_t count=name=="bufferSize" ? 1 : name=="bufferPeek" ? 2 : 3;if(e.children.size()!=count)fail("AI3107","Intrinsic argument count differs from its declaration.",e.token.at);
                    for(std::size_t i=1;i<count;++i)require_value(expr_value(e.children[i],Type::integer),Type::integer,e.children[i]->token.at,e.children[i].get());
                    e.symbol=name=="bufferSize" ? -6 : name=="bufferPeek" ? -7 : -8;return e.type=name=="bufferWrite" ? Type::unit : Type::integer;
                }
                if(name=="buffer"){e.symbol=-2;parameters={Type::integer};result=Type::buffer;}
                else if(name=="bufferSet"){e.symbol=-3;parameters={Type::buffer,Type::integer,Type::integer};result=Type::buffer;}
                else if(name=="bufferGet"){e.symbol=-4;parameters={Type::buffer,Type::integer};result=Type::integer;}
                else if(name=="bufferLength"){e.symbol=-5;parameters={Type::buffer};result=Type::integer;}
                else if(name=="fileRead"){e.symbol=-9;parameters={Type::string};result=Type::buffer;}
                else if(name=="fileWrite"){e.symbol=-10;parameters={Type::string,Type::buffer};result=Type::unit;}
                else if(name=="stringBytes"){e.symbol=-11;parameters={Type::string};result=Type::buffer;}
                else if(name=="intText"){e.symbol=-12;parameters={Type::integer};result=Type::buffer;}
                else fail("AI3103","Function is not declared.",e.token.at);
                if(e.children.size()!=parameters.size())fail("AI3107","Intrinsic argument count differs from its declaration.",e.token.at);
                bool previous_stack=stack_borrow_allowed;if(name=="bufferLength")stack_borrow_allowed=true;
                for(std::size_t i=0;i<parameters.size();++i)require_value(expr_value(e.children[i],parameters[i]),parameters[i],e.children[i]->token.at,e.children[i].get());
                stack_borrow_allowed=previous_stack;
                return e.type=result;
            }
            auto& target=*found->second;e.symbol=target.symbol;e.external_name=target.external_name;
            const bool has_params=!target.parameters.empty() && target.parameters.back().params;
            if(has_params){
                if(target.parameters.back().type!=Type::buffer)fail("AI3107","A params parameter must have the owned buffer type.",target.parameters.back().token.at);
                const std::size_t fixed=target.parameters.size()-1;
                if(e.children.size()<fixed)fail("AI3107","Function argument count differs from its declaration.",e.token.at);
                for(std::size_t i=0;i<fixed;++i)require_value(expr_value(e.children[i],target.parameters[i].type),target.parameters[i].type,e.children[i]->token.at,e.children[i].get(),"AI3107",&target.parameters[i].token.at,"parameter-declaration");
                auto pack=std::make_unique<Expr>();pack->kind=Expr::params_pack;pack->token=e.token;
                for(std::size_t i=fixed;i<e.children.size();++i)pack->children.push_back(std::move(e.children[i]));
                e.children.resize(fixed);
                e.children.push_back(std::move(pack));
                require_value(expr_value(e.children[fixed],Type::buffer),Type::buffer,e.children[fixed]->token.at,e.children[fixed].get());
            }else{
                if(e.children.size()!=target.parameters.size())fail("AI3107","Function argument count differs from its declaration.",e.token.at);
                for(std::size_t i=0;i<e.children.size();++i){
                auto& parameter=target.parameters[i];
                if(parameter.in_parameter || parameter.out_parameter){
                    auto target_value=parameter.type.pointee();
                    auto value_type=expr(*e.children[i]);
                    require_value(value_type,target_value,e.children[i]->token.at,e.children[i].get(),"AI3107",&parameter.token.at,"parameter-declaration");
                    auto borrow=std::make_unique<Expr>();borrow->kind=Expr::borrow;borrow->op=parameter.out_parameter ? "exclusive" : "shared";borrow->token=e.children[i]->token;borrow->children.push_back(std::move(e.children[i]));e.children[i]=std::move(borrow);
                    require_value(expr_value(e.children[i],parameter.type),parameter.type,e.children[i]->token.at,e.children[i].get(),"AI3107",&parameter.token.at,"parameter-declaration");
                }else require_value(expr_value(e.children[i],parameter.type),parameter.type,e.children[i]->token.at,e.children[i].get(),"AI3107",&parameter.token.at,"parameter-declaration");
            }
            }
            return e.type=target.type;
        }
        case Expr::unary: {
            if(e.op=="new"){
                if(e.children.size()!=1)fail("AI3100","Malformed new expression.",e.token.at);
                auto& target=*e.children[0];
                if(target.kind==Expr::call && products){auto name=canonical(target.token.text);auto product=std::find_if(products->definitions.begin(),products->definitions.end(),[&](auto& p){return p.generic<0 && p.name==name;});if(product!=products->definitions.end() && product->class_){if(product->abstract_class)fail("AI3107","An abstract class cannot be instantiated.",e.token.at);int product_id=static_cast<int>(product-products->definitions.begin());auto class_type=Type::from_shape(products,{Type::class_ref,product_id,{}},e.token.at);auto constructor=constructors.find(product_id);
                    if(constructor!=constructors.end()){
                        auto* function=constructor->second;if(target.children.size()+1!=function->parameters.size())fail("AI3107","Class constructor argument count differs from its declaration.",target.token.at);
                        auto node=std::make_unique<Expr>();node->kind=Expr::class_new;node->token=e.token;node->nominal=product_id;
                        for(std::size_t i=0;i<product->fields.size();++i){auto class_field_type=class_type.field_type(i);auto zero=std::make_unique<Expr>();zero->kind=Expr::literal;zero->token=e.token;zero->value=0;if(class_field_type.is_integer())zero->type=Type::integer;else if(class_field_type==Type::boolean)zero->type=Type::boolean;else if(class_field_type==Type::long_integer)zero->type=Type::long_integer;else if(class_field_type.is_class())zero->type=class_field_type;else fail("AI3107","Class constructor lowering currently supports scalar or class fields.",product->fields[i].at);zero->constant=Constant{zero->type,0,0};node->children.push_back(std::move(zero));}
                        node->type=class_type;auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=function->token;call->children.push_back(std::move(node));for(auto& child:target.children)call->children.push_back(std::move(child));e=std::move(*call);return expr(e,expected);
                    }
                    if(target.children.size()!=product->fields.size())fail("AI3107","Class constructor argument count differs from its fields.",target.token.at);auto node=std::make_unique<Expr>();node->kind=Expr::class_new;node->token=e.token;node->nominal=product_id;for(std::size_t i=0;i<target.children.size();++i){auto class_field_type=class_type.field_type(i);require_value(expr_value(target.children[i],class_field_type),class_field_type,target.children[i]->token.at,target.children[i].get());node->children.push_back(std::move(target.children[i]));}node->type=class_type;e=std::move(*node);return class_type;}}
                auto type=expr(*e.children[0]);
                auto& operand=*e.children[0];
                if(!((operand.kind==Expr::construct && operand.type.is_product() && !operand.type.is_union()) || (operand.kind==Expr::call && operand.symbol==-2)))fail("AI3107","new is supported only for native product construction and owned-buffer allocation in the bootstrap.",e.token.at);
                e.type=type;return type;
            }
            if(e.op=="-" && e.children[0]->kind==Expr::literal && e.children[0]->type==Type::integer && e.children[0]->value==2147483648LL) {
                e.kind=Expr::literal;e.value=-2147483648LL;e.type=Type::integer;e.children.clear();e.constant=Constant{e.type,e.value};return e.type;
            }
            if(e.op=="-" && e.children[0]->kind==Expr::literal && e.children[0]->long_minimum_magnitude){
                e.kind=Expr::literal;e.value=INT64_MIN;e.type=Type::long_integer;e.children.clear();e.constant=Constant{e.type,e.value};return e.type;
            }
            auto operand=expr(*e.children[0]);
            if(operand.is_product()){
                if(!(e.op=="+" || e.op=="-" || e.op=="!"))fail("AI3107","Native product unary operators support only +, - and !.",e.token.at);
                if(operand.is_union())fail("AI3107","Native union values do not overload unary operators.",e.token.at);
                Type result;int symbol=operator_function(e.op,operand,1,result,e.token.at);
                return product_operator_call(e,symbol,e.op,result);
            }
            if(e.op!="!" && operand.narrow_integer()){
                operand=Type::integer;
                convert_integer_value(e.children[0],operand);
            }
            if(operand.is_uint32() && e.op=="-"){
                operand=Type::long_integer;
                convert_integer_value(e.children[0],operand);
            }
            if(operand.is_decimal()){
                if(e.op!="+" && e.op!="-")fail("AI3107","Decimal supports only unary + and -.",e.token.at);
                e.type=operand;
                if(e.children[0]->constant)e.constant=fold_unary(e,*e.children[0]->constant);
                return e.type;
            }
            if(operand.is_float64()){
                if(e.op!="+" && e.op!="-")fail("AI3107","Double supports only unary + and -.",e.token.at);
                e.type=operand;
                if(e.children[0]->constant)e.constant=fold_unary(e,*e.children[0]->constant);
                return e.type;
            }
            if(operand.is_float32()){
                if(e.op!="+" && e.op!="-")fail("AI3107","Single supports only unary + and -.",e.token.at);
                e.type=operand;
                if(e.children[0]->constant)e.constant=fold_unary(e,*e.children[0]->constant);
                return e.type;
            }
            if(operand.is_ulong64() && e.op=="-")
                fail("AI3107","Unary minus is not defined for UInt64.",e.token.at);
            if(e.op=="!")
                require_value(operand,Type::boolean,e.token.at,e.children[0].get());
            else if(operand!=Type::long_integer && !operand.is_uint32() && !operand.is_ulong64())
                require_value(operand,Type::integer,e.token.at,e.children[0].get());
            e.type=operand;
            if(e.children[0]->constant)e.constant=fold_unary(e,*e.children[0]->constant);
            return e.type;
        }
        case Expr::binary: {
            auto left=expr(*e.children[0],type_hint(*e.children[1]));auto before=assigned;bool previous=reachable;
            bool logical=e.op=="&&" || e.op=="||";
            if(logical && e.children[0]->constant && (e.op=="&&" ? !e.children[0]->constant->number : e.children[0]->constant->number))reachable=false;
            auto right=expr(*e.children[1],left);reachable=previous;
            if(e.op=="&&" || e.op=="||") {
                bool guaranteed=e.children[0]->constant && e.children[0]->type==Type::boolean && (e.op=="&&" ? e.children[0]->constant->number!=0 : e.children[0]->constant->number==0);
                if(!guaranteed)assigned=std::move(before);
            }
            if(!logical){
                if(left.is_class() || right.is_class()){
                    if(left!=right || (e.op!="==" && e.op!="!="))fail("AI3107","Native class values support only identity equality and inequality.",e.token.at);
                    e.type=Type::boolean;return e.type;
                }
                if(left.is_product() || right.is_product()){
                    if(!left.is_product() || left!=right || left.is_union())fail("AI3107","Native product operators need matching non-union product operands.",e.token.at);
                    Type result;int symbol=operator_function(e.op,left,2,result,e.token.at);
                    return product_operator_call(e,symbol,e.op,result);
                }
                if(left.is_enum() || right.is_enum()){
                    if(left!=right)fail("AI3107","Enum operations need matching enum operand types.",e.token.at);
                    if(!(e.op=="==" || e.op=="!=" || e.op=="<" || e.op==">" || e.op=="<=" || e.op==">="))
                        fail("AI3107","Enum types support only comparison operators.",e.token.at);
                    e.type=Type::boolean;
                    return e.type;
                }
                if(left==Type::dynamic || right==Type::dynamic){
                if(left!=right)fail("AI3107","Dynamic operations require both operands to be dynamic in the bootstrap.",e.token.at);
                if(!(e.op=="+" || e.op=="-" || e.op=="*" || e.op=="/" || e.op=="%" || e.op=="==" || e.op=="!=" || e.op=="<" || e.op==">" || e.op=="<=" || e.op==">="))fail("AI3107","Dynamic operators support arithmetic, comparison and equality.",e.token.at);
                e.type=(e.op=="==" || e.op=="!=" || e.op=="<" || e.op==">" || e.op=="<=" || e.op==">=") ? Type::boolean : Type::dynamic;return e.type;
            }
            if(left==Type::object || right==Type::object){if(left!=right || (e.op!="==" && e.op!="!="))fail("AI3107","Native object supports only equality and inequality operators.",e.token.at);e.type=Type::boolean;return e.type;}
            if(left==Type::args || right==Type::args)fail("AI3107","The implicit args parameter supports only length and indexing.",e.token.at);
            if(left==Type::string || right==Type::string){
                if(left!=right)fail("AI3107","String operators need matching string operands.",e.token.at);
                if(e.op=="==" || e.op=="!="){e.type=Type::boolean;return e.type;}
                if(e.op=="+"){e.type=Type::string;return e.type;}
                fail("AI3107","String operators support only equality, inequality and concatenation.",e.token.at);
            }
            if(left.is_type_token() || right.is_type_token()){
                    if(left!=right || (e.op!="==" && e.op!="!="))
                        fail("AI3107","Type identities support only equality and inequality.",e.token.at);
                    e.type=Type::boolean;
                    return e.type;
                }
                if(native_integer_shift(e.op)){
                    if(left.narrow_integer()){left=Type::integer;convert_integer_value(e.children[0],left);}
                    if(right.narrow_integer()){right=Type::integer;convert_integer_value(e.children[1],right);}
                } else if(left.is_decimal() || right.is_decimal()){
                    if(!left.is_decimal()){
                        if(!left.is_integer())fail("AI3107","Decimal operations need numeric operands.",e.token.at);
                        left=Type::decimal;convert_integer_value(e.children[0],left);
                    }
                    if(!right.is_decimal()){
                        if(!right.is_integer())fail("AI3107","Decimal operations need numeric operands.",e.token.at);
                        right=Type::decimal;convert_integer_value(e.children[1],right);
                    }
                } else if(left.is_float64() || right.is_float64()){
                    if(!left.is_float64()){
                        if(!(left.is_integer() || left.is_float32()))fail("AI3107","Double operations need numeric operands.",e.token.at);
                        left=Type::float64;convert_integer_value(e.children[0],left);
                    }
                    if(!right.is_float64()){
                        if(!(right.is_integer() || right.is_float32()))fail("AI3107","Double operations need numeric operands.",e.token.at);
                        right=Type::float64;convert_integer_value(e.children[1],right);
                    }
                } else if(left.is_float32() || right.is_float32()){
                    if(!left.is_float32()){
                        if(!left.is_integer())fail("AI3107","Single operations need numeric operands.",e.token.at);
                        left=Type::float32;convert_integer_value(e.children[0],left);
                    }
                    if(!right.is_float32()){
                        if(!right.is_integer())fail("AI3107","Single operations need numeric operands.",e.token.at);
                        right=Type::float32;convert_integer_value(e.children[1],right);
                    }
                } else if((left.is_uint32() || right.is_uint32() || left.is_ulong64() || right.is_ulong64() || left==Type::long_integer || right==Type::long_integer) && left.is_integer() && right.is_integer()){
                    if(left.is_ulong64() || right.is_ulong64()){
                        auto promote_to_ulong=[&](Type& type,E& child){
                            if(type.is_ulong64())return;
                            if(type==Type::integer){
                                if(child->constant && child->constant->number>=0){type=Type::unsigned_long_integer;convert_integer_value(child,type);return;}
                                fail("AI3107","UInt64 operations need an unsigned or non-negative constant operand.",e.token.at);
                            }
                            if(type.is_uint32() || (type.narrow_integer() && (type.is_char() || type.narrow_integer()->minimum>=0))){
                                type=Type::unsigned_long_integer;convert_integer_value(child,type);return;
                            }
                            fail("AI3107","UInt64 operations need an unsigned or non-negative constant operand.",e.token.at);
                        };
                        if(left.is_ulong64())promote_to_ulong(right,e.children[1]);
                        else promote_to_ulong(left,e.children[0]);
                    }else{
                    auto force_long=[&](){if(left!=Type::long_integer){left=Type::long_integer;convert_integer_value(e.children[0],left);}if(right!=Type::long_integer){right=Type::long_integer;convert_integer_value(e.children[1],right);}};
                    auto promote_other=[&](Type& type,E& child){
                        if(type==Type::long_integer || type.is_uint32())return;
                        if(type==Type::integer){
                            if(child->constant && child->constant->number>=0 && child->constant->number<=4294967295LL){type=Type::unsigned_integer;convert_integer_value(child,type);}
                            else {type=Type::long_integer;convert_integer_value(child,type);}
                            return;
                        }
                        if(type.narrow_integer()){
                            const auto* small=type.narrow_integer();
                            if(type.is_char() || small->minimum>=0){type=Type::unsigned_integer;convert_integer_value(child,type);}
                            else {type=Type::long_integer;convert_integer_value(child,type);}
                            return;
                        }
                        type=Type::long_integer;convert_integer_value(child,type);
                    };
                    if(left==Type::long_integer || right==Type::long_integer){force_long();}
                    else {promote_other(left,e.children[0]);promote_other(right,e.children[1]);if(left==Type::long_integer || right==Type::long_integer)force_long();}
                    }
                } else {
                    if(left.narrow_integer()){left=Type::integer;convert_integer_value(e.children[0],left);}
                    if(right.narrow_integer()){right=Type::integer;convert_integer_value(e.children[1],right);}
                    if(left==Type::integer && right==Type::long_integer){left=Type::long_integer;convert_integer_value(e.children[0],left);}
                    else if(left==Type::long_integer && right==Type::integer){right=Type::long_integer;convert_integer_value(e.children[1],right);}
                }
            }
            e.type=binary(e.op,left,right,e.token.at);
            if(e.children[0]->constant && e.children[1]->constant)e.constant=fold_binary(e,*e.children[0]->constant,*e.children[1]->constant);
            return e.type;
        }
        case Expr::assign: case Expr::update: {
            auto& target=*e.children[0];
            if(target.kind==Expr::dereference || target.kind==Expr::field){auto type=place_type(target,true);if(target.kind==Expr::dereference && target.children[0]->kind==Expr::name && out_required.contains(target.children[0]->symbol))out_written.insert(target.children[0]->symbol);e.type=type;if(e.kind==Expr::update){if(!type.is_integer())require_value(type,Type::integer,e.token.at,&target);}else {if(e.op!="=" && type.is_product() && !type.is_union()){auto rhs_type=expr_value(e.children[1],type);require_value(rhs_type,type,e.children[1]->token.at,e.children[1].get());const std::string base_op=e.op.substr(0,e.op.size()-1);Type result;int symbol=operator_function(base_op,type,2,result,e.token.at);auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=Token{"operator"+base_op,e.token.at,Token::id};call->symbol=symbol;call->type=result;call->children.push_back(clone_place(target));call->children.push_back(std::move(e.children[1]));e.children[1]=std::move(call);e.op="=";e.type=type;return e.type;}auto right=assignment_operand(e,type);if(e.op=="=")require_value(right,type,e.children[1]->token.at,e.children[1].get());else {auto promoted=native_integer_operator_type(type);require(binary(e.op.substr(0,e.op.size()-1),promoted,right,e.token.at),promoted,e.token.at);}}if(!type.is_copy() || type.is_reference())e.type=Type::unit;return e.type;}
            if(target.kind!=Expr::name)fail("AI3105","Only mutable local storage can be updated.",target.token.at);
            auto b=lookup(target.token,e.kind==Expr::update || e.op!="=");
            target.symbol=b.id;target.type=b.type;e.symbol=b.id;e.type=b.type;
            if(!b.mutable_)fail("AI3105","Cannot modify an immutable value.",target.token.at);
            if(e.kind==Expr::update){if(!b.type.is_integer())require_value(b.type,Type::integer,e.token.at,&target);}
            else {if(e.op!="=" && b.type.is_product() && !b.type.is_union()){auto rhs_type=expr_value(e.children[1],b.type);require_value(rhs_type,b.type,e.children[1]->token.at,e.children[1].get());const std::string base_op=e.op.substr(0,e.op.size()-1);Type result;int symbol=operator_function(base_op,b.type,2,result,e.token.at);auto call=std::make_unique<Expr>();call->kind=Expr::call;call->token=Token{"operator"+base_op,e.token.at,Token::id};call->symbol=symbol;call->type=result;call->children.push_back(clone_place(target));call->children.push_back(std::move(e.children[1]));e.children[1]=std::move(call);e.op="=";e.type=b.type;assigned.insert(b.id);return e.type;}auto right=assignment_operand(e,b.type);if(e.op=="=")require_value(right,b.type,e.children[1]->token.at,e.children[1].get(),"AI3107",&target.token.at,"destination-use");else {auto promoted=native_integer_operator_type(b.type);require(binary(e.op.substr(0,e.op.size()-1),promoted,right,e.token.at),promoted,e.token.at);}}
            assigned.insert(b.id);if(!b.type.is_copy() || b.type.is_reference())return e.type=Type::unit;return e.type;
        }
        }
        fail("AI3100","Unknown expression node.",e.token.at);
    }
    bool scoped(Stmt& s) {scopes.emplace_back();bool reaching=stmt(s);for(auto& [name,b]:scopes.back()){(void)name;assigned.erase(b.id);}scopes.pop_back();return reaching;}
    bool stmt(Stmt& s) {
        try{return stmt_impl(s);}catch(Diagnostic& d){
            bool local_binding=s.kind==Stmt::local && s.expr &&
                ((d.code=="AI3113" && s.expr->type==Type::buffer && d.location.offset==s.expr->token.at.offset) ||
                 (d.code=="AI3107" && d.location.offset==s.token.at.offset));
            if(d.ownershipObligation.empty() && local_binding && relate_owner(d,*s.expr)){
                d.ownershipObligation="owned-value-binding";related(d,s.kind==Stmt::local ? "destination-declaration" : "statement",s.token.at);
            }
            throw;
        }
    }
    struct LabelInfo { int id=0,depth=0;std::set<int> merged;std::vector<std::set<int>> incoming;std::vector<std::set<int>> scope_incoming;bool has_incoming=false; };
    std::map<std::string,LabelInfo> labels;std::map<int,LabelInfo*> labels_by_id;int next_label=1,block_depth=0;int try_counter=0;
    bool label_incoming(int id)const{auto it=labels_by_id.find(id);return it!=labels_by_id.end() && it->second->has_incoming;}
    void collect_labels(Stmt& s,int depth) {
        if(s.kind==Stmt::label){
            auto name=canonical(s.token.text);
            if(labels.contains(name))fail("AI3106","A label with this name is already active.",s.token.at);
            int id=next_label++;labels[name].id=id;labels[name].depth=depth;labels_by_id[id]=&labels[name];s.symbol=id;s.scope_depth=depth;return;
        }
        for(auto& child:s.children)collect_labels(*child,depth+(s.kind==Stmt::block ? 1 : 0));
    }
    bool stmt_try(Stmt& s) {
        auto at=s.token.at;int id=try_counter++;
        auto tok=[&](const std::string& name){return Token{name,at,Token::id};};
        auto name=[&](const std::string& suffix){return std::string("$try")+std::to_string(id)+"_"+suffix;};
        const std::string reason_name=name("reason"),catch_label=name("catch"),finally_label=name("finally");
        Stmt& body=*s.children[0];
        Stmt* catch_body=s.has_catch ? s.children[1].get() : nullptr;
        Stmt* finally_body=s.has_finally ? s.children[s.has_catch ? 2 : 1].get() : nullptr;
        std::function<void(const Expr&)> check_expr;
        check_expr=[&](const Expr& e){
            if(e.kind==Expr::propagate)fail("AI3107","Propagation operators inside try are not implemented by the native bootstrap.",e.token.at);
            for(auto& child:e.children)check_expr(*child);
            for(auto& arm:e.arms){if(arm.pattern)check_expr(*arm.pattern);if(arm.guard)check_expr(*arm.guard);if(arm.value)check_expr(*arm.value);}
        };
        bool used_return=false,used_fail=false,used_break=false,used_continue=false;
        std::function<void(const Stmt&,int,bool)> scan;
        scan=[&](const Stmt& st,int depth,bool handler){
            if(st.kind==Stmt::try_block)fail("AI3107","Nested try statements are not implemented by the native bootstrap.",st.token.at);
            if(handler && st.kind==Stmt::local)fail("AI3107","A catch handler cannot declare native locals in the bootstrap restriction.",st.token.at);
            if(!handler && st.kind==Stmt::local)fail("AI3107","A try body cannot declare native locals in the bootstrap restriction.",st.token.at);
            if(st.kind==Stmt::result)used_return=true;
            if(st.kind==Stmt::failure)used_fail=true;
            if(st.kind==Stmt::stop && depth==0)used_break=true;
            if(st.kind==Stmt::next && depth==0)used_continue=true;
            if(st.expr)check_expr(*st.expr);if(st.update)check_expr(*st.update);if(st.catch_filter)check_expr(*st.catch_filter);
            for(auto& child:st.children)scan(*child,depth+(st.kind==Stmt::loop || st.kind==Stmt::for_loop || st.kind==Stmt::do_loop ? 1 : 0),handler);
        };
        scan(body,0,false);
        if(catch_body)scan(*catch_body,0,true);
        if(finally_body){
            std::function<void(const Stmt&,int)> final_scan=[&](const Stmt& st,int depth){
                if(st.kind==Stmt::result || st.kind==Stmt::failure)fail("AI3107","A finally block cannot return or fail in the native bootstrap.",st.token.at);
                if((st.kind==Stmt::stop || st.kind==Stmt::next) && depth==0)fail("AI3107","A finally block cannot break or continue outside its own loops in the native bootstrap.",st.token.at);
                if(st.expr)check_expr(*st.expr);if(st.update)check_expr(*st.update);if(st.catch_filter)check_expr(*st.catch_filter);
                for(auto& child:st.children)final_scan(*child,depth+(st.kind==Stmt::loop || st.kind==Stmt::for_loop || st.kind==Stmt::do_loop ? 1 : 0));
            };
            final_scan(*finally_body,0);
        }
        if(used_return && result_type!=Type::unit && !result_type.is_copy())fail("AI3107","Return values with drop semantics inside try are not implemented by the native bootstrap.",s.token.at);
        auto hidden_scalar=[](const Type& type){return type==Type::unit || type.is_integer() || type==Type::boolean || type.is_char() || type.is_enum();};
        if(used_return && result_type!=Type::unit && !hidden_scalar(result_type))fail("AI3107","The native try implementation supports only scalar return values.",s.token.at);
        if(!(error_type==Type::unit) && (s.has_catch || used_fail) && !hidden_scalar(error_type))fail("AI3107","The native try implementation supports only scalar failure types.",s.token.at);
        if(s.has_catch && s.catch_name.text.size() && error_type==Type::unit)fail("AI3107","A catch binding requires a non-unit failure type.",s.catch_name.at);
        auto make_local=[&](const std::string& local_name,const Type& type){auto st=std::make_unique<Stmt>();st->kind=Stmt::local;st->token=tok(local_name);st->type=type;st->mutable_=true;st->static_binding=true;auto value=std::make_unique<Expr>();value->kind=Expr::literal;value->token=tok("$try_zero");value->type=type;value->value=0;value->constant=Constant{type,0};st->expr=std::move(value);return st;};
        auto make_plain_local=[&](const std::string& local_name,const Type& type){auto st=std::make_unique<Stmt>();st->kind=Stmt::local;st->token=tok(local_name);st->type=type;st->mutable_=true;return st;};
        auto make_name=[&](const std::string& local_name){auto e=std::make_unique<Expr>();e->kind=Expr::name;e->token=tok(local_name);return e;};
        auto make_int=[&](std::int64_t value){auto e=std::make_unique<Expr>();e->kind=Expr::literal;e->token=tok("$try_int");e->type=Type::integer;e->value=value;e->constant=Constant{Type::integer,value};return e;};
        auto assign=[&](const std::string& local_name,std::unique_ptr<Expr> value){auto target=make_name(local_name);auto e=std::make_unique<Expr>();e->kind=Expr::assign;e->op="=";e->token=tok("$try");e->children.push_back(std::move(target));e->children.push_back(std::move(value));auto st=std::make_unique<Stmt>();st->kind=Stmt::expression;st->token=tok("$try");st->expr=std::move(e);return st;};
        auto assign_reason=[&](std::int64_t value){return assign(reason_name,make_int(value));};
        auto goto_stmt=[&](const std::string& label){auto st=std::make_unique<Stmt>();st->kind=Stmt::goto_jump;st->token=tok(label);st->op=label;return st;};
        auto make_block=[&](std::vector<S> children){auto st=std::make_unique<Stmt>();st->kind=Stmt::block;st->token=tok("$try");st->children=std::move(children);return st;};
        auto transform=[&](auto&& self,S st,int depth,bool handler)->S{
            switch(st->kind){
            case Stmt::result: {
                std::vector<S> parts;
                if(st->expr){if(result_type==Type::unit){auto value=std::make_unique<Stmt>();value->kind=Stmt::expression;value->token=tok("$try");value->expr=std::move(st->expr);parts.push_back(std::move(value));}else parts.push_back(assign(name("ret"),std::move(st->expr)));}
                parts.push_back(assign_reason(1));parts.push_back(goto_stmt(finally_label));return make_block(std::move(parts));
            }
            case Stmt::failure: {
                std::vector<S> parts;
                if(!(error_type==Type::unit))parts.push_back(assign(name("err"),st->expr ? std::move(st->expr) : make_name("$invalid")));
                parts.push_back(assign_reason(4));parts.push_back(goto_stmt((s.has_catch && !handler) ? catch_label : finally_label));return make_block(std::move(parts));
            }
            case Stmt::stop: case Stmt::next: {
                if(depth!=0)return st;
                std::vector<S> parts;parts.push_back(assign_reason(st->kind==Stmt::stop ? 2 : 3));parts.push_back(goto_stmt(finally_label));return make_block(std::move(parts));
            }
            case Stmt::branch: case Stmt::block: {
                for(auto& child:st->children)child=self(self,std::move(child),depth,handler);return st;
            }
            case Stmt::loop: case Stmt::for_loop: case Stmt::do_loop: {
                for(auto& child:st->children)child=self(self,std::move(child),depth+1,handler);return st;
            }
            case Stmt::try_block: fail("AI3107","Nested try statements are not implemented by the native bootstrap.",st->token.at);
            default: return st;
            }
        };
        std::function<bool(const Stmt&)> completes;
        completes=[&](const Stmt& st)->bool{
            switch(st.kind){
            case Stmt::result: case Stmt::failure: case Stmt::goto_jump: case Stmt::stop: case Stmt::next: return false;
            case Stmt::block: {bool any=true;for(auto& child:st.children)any=completes(*child);return any;}
            case Stmt::branch: {if(st.children.empty())return true;bool yes=completes(*st.children[0]);return yes || (st.children.size()>1 && completes(*st.children[1]));}
            default: return true;
            }
        };
        auto adjust_depth=[&](auto&& self,S& st)->void{if(st->kind==Stmt::label){auto found=labels_by_id.find(st->symbol);if(found!=labels_by_id.end())++found->second->depth;}for(auto& child:st->children)self(self,child);};
        for(auto& child:s.children)adjust_depth(adjust_depth,child);
        S transformed_body=transform(transform,std::move(s.children[0]),0,false);
        S transformed_catch=catch_body ? transform(transform,std::move(s.children[1]),0,true) : nullptr;
        S finally_block=s.has_finally ? std::move(s.children[s.has_catch ? 2 : 1]) : nullptr;
        bool body_completes=completes(*transformed_body);
        bool catch_completes=transformed_catch ? completes(*transformed_catch) : false;
        bool finally_completes=finally_block ? completes(*finally_block) : true;
        bool try_completes=(body_completes || (s.has_catch && catch_completes)) && finally_completes;
        std::vector<S> block_children;
        block_children.push_back(make_local(reason_name,Type::integer));
        if(used_return && result_type!=Type::unit)block_children.push_back(make_local(name("ret"),result_type));
        if(!(error_type==Type::unit) && (s.has_catch || used_fail))block_children.push_back(make_local(name("err"),error_type));
        if(s.has_catch && s.catch_name.text.size())block_children.push_back(make_plain_local(s.catch_name.text,error_type));
        block_children.push_back(std::move(transformed_body));
        block_children.push_back(assign_reason(0));block_children.push_back(goto_stmt(finally_label));
        if(s.has_catch){
            auto catch_label_stmt=std::make_unique<Stmt>();catch_label_stmt->kind=Stmt::label;catch_label_stmt->token=tok(catch_label);collect_labels(*catch_label_stmt,block_depth+1);block_children.push_back(std::move(catch_label_stmt));
            std::vector<S> catch_children;
            if(s.catch_name.text.size())catch_children.push_back(assign(s.catch_name.text,make_name(name("err"))));
            if(s.catch_filter){
                const std::string handler_label=name("handler");
                auto filter=std::make_unique<Stmt>();filter->kind=Stmt::branch;filter->token=tok("$try");filter->expr=std::move(s.catch_filter);filter->children.push_back(goto_stmt(handler_label));
                std::vector<S> miss;miss.push_back(assign_reason(4));miss.push_back(goto_stmt(finally_label));filter->children.push_back(make_block(std::move(miss)));catch_children.push_back(std::move(filter));
                auto handler_label_stmt=std::make_unique<Stmt>();handler_label_stmt->kind=Stmt::label;handler_label_stmt->token=tok(handler_label);collect_labels(*handler_label_stmt,block_depth+2);catch_children.push_back(std::move(handler_label_stmt));
            }
            catch_children.push_back(std::move(transformed_catch));
            block_children.push_back(make_block(std::move(catch_children)));
            block_children.push_back(assign_reason(0));block_children.push_back(goto_stmt(finally_label));
        }
        {
            auto finally_label_stmt=std::make_unique<Stmt>();finally_label_stmt->kind=Stmt::label;finally_label_stmt->token=tok(finally_label);collect_labels(*finally_label_stmt,block_depth+1);block_children.push_back(std::move(finally_label_stmt));
            if(finally_block)block_children.push_back(std::move(finally_block));
        }
        auto dispatch=[&](int value,S action){auto condition=std::make_unique<Expr>();condition->kind=Expr::binary;condition->op="==";condition->token=tok("$try");condition->children.push_back(make_name(reason_name));condition->children.push_back(make_int(value));auto branch=std::make_unique<Stmt>();branch->kind=Stmt::branch;branch->token=tok("$try");branch->expr=std::move(condition);branch->children.push_back(std::move(action));return branch;};
        if(used_return){auto result=std::make_unique<Stmt>();result->kind=Stmt::result;result->token=tok("$try");if(result_type!=Type::unit)result->expr=make_name(name("ret"));block_children.push_back(dispatch(1,std::move(result)));}
        if(used_break){auto stop=std::make_unique<Stmt>();stop->kind=Stmt::stop;stop->token=tok("$try");block_children.push_back(dispatch(2,std::move(stop)));}
        if(used_continue){auto next=std::make_unique<Stmt>();next->kind=Stmt::next;next->token=tok("$try");block_children.push_back(dispatch(3,std::move(next)));}
        if(used_fail){auto failure=std::make_unique<Stmt>();failure->kind=Stmt::failure;failure->token=tok("$try");if(!(error_type==Type::unit))failure->expr=make_name(name("err"));block_children.push_back(dispatch(4,std::move(failure)));}
        if(!try_completes){
            if(used_return){auto fallback=std::make_unique<Stmt>();fallback->kind=Stmt::result;fallback->token=tok("$try");if(result_type!=Type::unit)fallback->expr=make_name(name("ret"));block_children.push_back(std::move(fallback));}
            else if(used_fail){auto fallback=std::make_unique<Stmt>();fallback->kind=Stmt::failure;fallback->token=tok("$try");if(!(error_type==Type::unit))fallback->expr=make_name(name("err"));block_children.push_back(std::move(fallback));}
            else if(used_break){auto fallback=std::make_unique<Stmt>();fallback->kind=Stmt::stop;fallback->token=tok("$try");block_children.push_back(std::move(fallback));}
            else if(used_continue){auto fallback=std::make_unique<Stmt>();fallback->kind=Stmt::next;fallback->token=tok("$try");block_children.push_back(std::move(fallback));}
        }
        S result=make_block(std::move(block_children));
        s=std::move(*result);
        return stmt(s);
    }

    bool stmt_label(Stmt& s) {
        auto it=labels_by_id.find(s.symbol);if(it==labels_by_id.end())fail("AI3100","Unbound label statement.",s.token.at);
        auto& info=*it->second;
        std::set<int> scope_ids;for(auto& [name,b]:scopes[static_cast<std::size_t>(info.depth)])(void)name,scope_ids.insert(b.id);
        for(auto& snapshot:info.scope_incoming)for(int id:scope_ids)if(!snapshot.contains(id))
            fail("AI3107","Cannot jump over a native local declaration.",s.token.at);
        std::set<int> merged;bool has=false;
        if(reachable){merged=assigned;has=true;}
        for(auto& incoming:info.incoming){
            if(!has){merged=incoming;has=true;continue;}
            std::set<int> common;std::set_intersection(merged.begin(),merged.end(),incoming.begin(),incoming.end(),std::inserter(common,common.begin()));merged=std::move(common);
        }
        if(has){assigned=merged;reachable=true;}
        return reachable;
    }
    bool stmt_goto(Stmt& s) {
        auto found=labels.find(s.op);if(found==labels.end())fail("AI3103","Goto target label is not declared.",s.token.at);
        auto& info=found->second;if(info.depth>block_depth)fail("AI3107","Cannot jump into a nested scope.",s.token.at);
        s.symbol=info.id;s.scope_depth=info.depth;
        if(reachable){
            info.incoming.push_back(assigned);
            std::set<int> scope_ids;for(auto& [name,b]:scopes[static_cast<std::size_t>(info.depth)])(void)name,scope_ids.insert(b.id);
            info.scope_incoming.push_back(std::move(scope_ids));
            info.has_incoming=true;
        }
        reachable=false;return false;
    }
    std::set<int> scoped_symbols;
    bool scoped_escape(const Expr& e)const {
        if(e.kind==Expr::name && scoped_symbols.contains(e.symbol))return true;
        for(auto& child:e.children)if(scoped_escape(*child))return true;
        for(auto& arm:e.arms){if(arm.pattern && scoped_escape(*arm.pattern))return true;if(arm.guard && scoped_escape(*arm.guard))return true;if(arm.value && scoped_escape(*arm.value))return true;}
        return false;
    }
    bool stmt_impl(Stmt& s) {
        switch(s.kind) {
        case Stmt::block: {
            bool reaching=true;scopes.emplace_back();++block_depth;
            for(auto& child:s.children){
                auto before=assigned;bool previous=reachable;bool label=child->kind==Stmt::label;
                bool incoming_label=label && label_incoming(child->symbol);
                reachable=reachable && reaching;
                bool child_reaches=stmt(*child);
                if(incoming_label){previous=true;child_reaches=true;}
                reachable=previous;
                if(!reaching && !incoming_label)assigned=std::move(before);
                reaching=incoming_label ? true : (reaching && child_reaches);
            }
            --block_depth;for(auto& [name,b]:scopes.back()){(void)name;assigned.erase(b.id);}scopes.pop_back();return reaching;
        }
        case Stmt::local: {
            Type initial=Type::unit;if(s.expr){bool previous_stack=stack_alloc_allowed;stack_alloc_allowed=s.expr->kind==Expr::stack_alloc;initial=expr_value(s.expr,s.type==Type::unit ? std::nullopt : std::optional<Type>{s.type});stack_alloc_allowed=previous_stack;}
            if(s.type==Type::unit)s.type=initial;
            if(s.type==Type::unit)fail("AI3107","Unit cannot be stored in a local.",s.token.at);
            if(s.expr)require_value(initial,s.type,s.expr->token.at,s.expr.get(),"AI3107",&s.token.at);
            if(s.constant && !s.expr->constant)fail("AI3113","A const initializer must be a compile-time constant.",s.expr->token.at);
            if(s.using_resource && !s.type.needs_drop())fail("AI3107","using requires an owned disposable value.",s.token.at);
        if(s.switch_control && !(s.type.is_integer() || s.type.is_char() || s.type.is_enum() || s.type==Type::boolean))fail("AI3107","A switch statement requires an integral, character, bool or enum control expression.",s.token.at);
        if(s.lock_binding && (!s.type.is_reference() || s.type.borrow_kind()!=Type::Borrow::exclusive))fail("AI3107","A lock binding requires an exclusive reference.",s.token.at);
            if(s.static_binding){
                if(!s.expr || !s.expr->constant)fail("AI3107","A static binding requires a compile-time constant initializer.",s.token.at);
                if(!(s.type.is_integer() || s.type.is_char() || s.type==Type::boolean || s.type.is_enum()))fail("AI3107","A static binding requires a scalar integer, character, bool or enum type.",s.token.at);
            }
            s.symbol=declare(s.token,s.type,s.mutable_ && !s.using_resource,s.constant ? s.expr->constant : std::nullopt);
        if(s.expr && s.expr->kind==Expr::stack_alloc)stack_symbols.insert(s.symbol);
            if(s.static_binding){static_symbols.insert(s.symbol);if(!current_function)fail("AI3100","Static binding has no owning function.",s.token.at);current_function->statics.push_back({s.symbol,"ai_static_"+std::to_string(s.symbol),s.type,s.expr->constant->number});}
            if(s.scoped_ref){if(!s.type.is_reference())fail("AI3107","scoped requires a reference type.",s.token.at);scoped_symbols.insert(s.symbol);}
            if(s.expr)assigned.insert(s.symbol);return true;
        }
        case Stmt::expression: if(s.expr)expr(*s.expr);return true;
        case Stmt::failure:
            if(!declared_failure)failure_error("AI3132","A fail statement requires an explicit fails contract.",s.token.at);
            {auto actual=s.expr ? expr_value(s.expr,error_type) : Type::unit;if(actual!=error_type){Diagnostic d("AI3131","The failure value has an incompatible declared error type.",s.token.at);d.failureObligation="declared-error-type";d.expectedType=error_type.label();d.actualType=actual.label();d.related.push_back({"failure-declaration",failure_declaration});throw d;}}
            s.kind=Stmt::result;s.expr=outcome_expression(return_carrier,1,s.expr ? std::move(s.expr) : unit_expression(s.token),s.token);return false;
        case Stmt::result:
            if(result_type.is_ref_like())fail("AI3107","A ref-like value cannot escape through a function return.",s.token.at);
            require_value(s.expr ? expr_value(s.expr,result_type) : Type::unit,result_type,s.token.at,s.expr.get(),"AI3020",&result_declaration,"return-declaration");
            if(s.expr && (s.expr->type.is_reference() || s.expr->type.contains_references()) && scoped_escape(*s.expr))
                fail("AI3117","A scoped reference escapes the region allowed for it.",s.expr->token.at);
            if(declared_failure)s.expr=outcome_expression(return_carrier,0,s.expr ? std::move(s.expr) : unit_expression(s.token),s.token);return false;
        case Stmt::branch: {
            require_value(expr(*s.expr),Type::boolean,s.expr->token.at,s.expr.get(),"AI3101");auto before=assigned;
            bool known=s.expr->constant && s.expr->type==Type::boolean;bool value=known && s.expr->constant->number!=0;bool previous=reachable;
            reachable=previous && (!known || value);bool yes=scoped(*s.children[0]);auto yes_set=assigned;assigned=before;
            reachable=previous && (!known || !value);bool no=s.children.size()==2 ? scoped(*s.children[1]) : true;auto no_set=assigned;reachable=previous;
            if(known){assigned=value ? std::move(yes_set) : std::move(no_set);return value ? yes : no;}
            if(!yes && no)assigned=std::move(no_set);else if(yes && !no)assigned=std::move(yes_set);else {
                assigned.clear();std::set_intersection(yes_set.begin(),yes_set.end(),no_set.begin(),no_set.end(),std::inserter(assigned,assigned.end()));
            }
            return yes || no;
        }
        case Stmt::loop: case Stmt::for_loop: {
            scopes.emplace_back();if(s.kind==Stmt::for_loop)stmt(*s.children[0]);
            if(s.expr)require_value(expr(*s.expr),Type::boolean,s.expr->token.at,s.expr.get(),"AI3101");auto before=assigned;
            bool previous=reachable;if(s.expr && s.expr->constant && !s.expr->constant->number)reachable=false;
            ++loops;continues.emplace_back();breaks.push_back(false);bool body_reaches=scoped(*s.children.back());
            if(body_reaches)continues.back().push_back(assigned);
            if(continues.back().empty())assigned=before;else {assigned=continues.back()[0];for(auto& path:continues.back()){std::set<int> common;std::set_intersection(assigned.begin(),assigned.end(),path.begin(),path.end(),std::inserter(common,common.end()));assigned=std::move(common);}}
            if(continues.back().empty())reachable=false;
            if(s.update)expr(*s.update);reachable=previous;bool has_break=breaks.back();breaks.pop_back();continues.pop_back();--loops;assigned=std::move(before);
            for(auto& [name,b]:scopes.back()){(void)name;assigned.erase(b.id);}scopes.pop_back();
            return !((!s.expr || (s.expr->constant && s.expr->type==Type::boolean && s.expr->constant->number)) && !has_break);
        }
        case Stmt::do_loop: {
            scopes.emplace_back();
            auto before=assigned;
            bool previous=reachable;
            ++loops;continues.emplace_back();breaks.push_back(false);
            bool body_reaches=scoped(*s.children[0]);
            if(body_reaches)continues.back().push_back(assigned);
            require_value(expr(*s.expr),Type::boolean,s.expr->token.at,s.expr.get(),"AI3101");
            bool has_break=breaks.back();
            bool infinite=s.expr->constant && s.expr->type==Type::boolean && s.expr->constant->number!=0;
            bool condition_reached=body_reaches || !continues.back().empty();
            breaks.pop_back();continues.pop_back();--loops;
            assigned=std::move(before);
            for(auto& [name,b]:scopes.back()){(void)name;assigned.erase(b.id);}scopes.pop_back();
            reachable=previous && condition_reached && (!infinite || has_break);
            return reachable;
        }
        case Stmt::stop: case Stmt::next:
            if(loops==0)fail("AI3104","Break or continue requires an enclosing loop.",s.token.at);
            if(reachable){if(s.kind==Stmt::next)continues.back().push_back(assigned);else breaks.back()=true;}return false;
        case Stmt::label: return stmt_label(s);
        case Stmt::goto_jump: return stmt_goto(s);
        case Stmt::try_block: return stmt_try(s);
        }
        fail("AI3100","Unknown statement node.",s.token.at);
    }
public:
    void run(std::vector<Function>& list) {
        if(!list.empty())products=list[0].products;
        int id=0;for(auto& f:list){f.symbol=id++;if(!functions.emplace(canonical(f.token.text),&f).second)fail("AI3106","Duplicate function declaration.",f.token.at);if((f.accessor_get || f.accessor_set) && !f.parameters.empty() && f.parameters[0].type.is_product()){auto key=std::make_pair(f.parameters[0].type.product_id(),f.property_name);(f.accessor_get ? property_getters : property_setters).emplace(key,&f);}if((f.accessor_add || f.accessor_remove) && !f.parameters.empty() && f.parameters[0].type.is_class()){auto key=std::make_pair(f.parameters[0].type.product_id(),f.property_name);(f.accessor_add ? event_adders : event_removers).emplace(key,&f);}if(!f.accessor_get && !f.accessor_set && !f.constructor && f.parameters.size()>=1 && canonical(f.parameters[0].token.text)=="this" && (f.parameters[0].type.is_product() || f.parameters[0].type.is_class())){auto key=std::make_pair(f.parameters[0].type.product_id(),f.member_name.empty() ? canonical(f.token.text) : f.member_name);if(!extension_methods.emplace(key,&f).second)fail("AI3106","Duplicate struct member function.",f.token.at);}if(f.constructor && f.constructor_product>=0)constructors[f.constructor_product]=&f;}
        auto entry=functions.find("main");if(entry==functions.end())fail("AI3100","An entry function named main is required.",{});
        if(!entry->second->parameters.empty() || ((entry->second->fallible ? entry->second->success_type : entry->second->type)!=Type::integer && (entry->second->fallible ? entry->second->success_type : entry->second->type)!=Type::boolean))fail("AI3100","Bootstrap main needs no parameters and an int or bool result.",entry->second->token.at);
        if(entry->second->external)fail("AI3100","The entry function cannot be an extern declaration.",entry->second->token.at);
        for(auto& f:list)for(auto& p:f.parameters)if(p.in_parameter || p.out_parameter){
            if(p.scoped || p.readonly || p.params)fail("AI3107","An in/out parameter cannot combine scoped, readonly or params.",p.token.at);
            if(p.type.is_reference() || p.type.is_product() || p.type.is_union() || p.type==Type::buffer || p.type.is_type_token() || !p.type.bootstrap_supported())
                fail("AI3107","An in/out parameter requires a bootstrap scalar value type.",p.token.at);
            p.type=Type::reference(p.type,p.in_parameter ? Type::Borrow::shared : Type::Borrow::exclusive);p.readonly=true;
        }
        for(auto& f:list){if(f.external || f.abstract_method)continue;current_function=&f;active_drop_for=-1;if(f.token.text.starts_with("$drop")){try{active_drop_for=std::stoi(f.token.text.substr(5));}catch(...){active_drop_for=-1;}}scopes.clear();scopes.emplace_back();assigned.clear();out_required.clear();out_written.clear();out_tokens.clear();labels.clear();labels_by_id.clear();next_label=1;block_depth=0;collect_labels(*f.body,0);declared_failure=f.fallible;result_type=f.fallible ? f.success_type : f.type;return_carrier=f.type;error_type=f.error_type;result_declaration=f.token.at;failure_declaration=f.fallible ? f.failure_declaration : f.token.at;
            scoped_symbols.clear();
            for(std::size_t pi=0;pi<f.parameters.size();++pi){auto& p=f.parameters[pi];if(p.params && (pi+1!=f.parameters.size() || p.type!=Type::buffer))fail("AI3107","A params parameter must be last and have the owned buffer type.",p.token.at);}
            if(f.token.text.starts_with("implicit#") || f.token.text.starts_with("explicit#")){
        if(f.parameters.size()!=1)fail("AI3107","A native conversion operator requires one parameter.",f.token.at);
        auto source=f.parameters[0].type,target=f.success_type;
        if(source.is_reference() || target.is_reference() || source==Type::buffer || target==Type::buffer || source==Type::unit || target==Type::unit)fail("AI3107","Native conversion operators require value types.",f.token.at);
        if(source==target)fail("AI3107","A native conversion operator cannot be the identity conversion.",f.token.at);
        if((source.is_product() && source.is_union()) || (target.is_product() && target.is_union()))fail("AI3107","Native conversion operators do not support union values.",f.token.at);
    }
    if(f.token.text.starts_with("operator")){
                const std::string op=f.token.text.substr(8);
                static const std::set<std::string> binary_only={"*","/","%","==","!=","<",">","<=",">="};
                if(f.parameters.size()==1 && binary_only.contains(op))fail("AI3107","Native binary-only operators require two parameters.",f.token.at);
                if(f.parameters.size()==2 && op=="!")fail("AI3107","Native logical negation is unary.",f.token.at);
                if(f.parameters.size()==2){
                    if(!f.parameters[0].type.is_product() || f.parameters[0].type.is_union() || f.parameters[0].type!=f.parameters[1].type)fail("AI3107","Native binary operator parameters must be the same non-union product type.",f.token.at);
                    if(op=="==" || op=="!=" || op=="<" || op==">" || op=="<=" || op==">="){if(f.success_type!=Type::boolean)fail("AI3107","Native comparison operators must return bool.",f.token.at);}
                    else if(f.success_type!=f.parameters[0].type)fail("AI3107","Native arithmetic operators must return their product type.",f.token.at);
                }else if(f.parameters.size()==1){
                    if(!f.parameters[0].type.is_product() || f.parameters[0].type.is_union())fail("AI3107","Native unary operator parameters must be a non-union product type.",f.token.at);
                    if(op=="!"){if(f.success_type!=Type::boolean)fail("AI3107","Native logical negation must return bool.",f.token.at);}
                    else if(f.success_type!=f.parameters[0].type)fail("AI3107","Native unary operators must return their product type.",f.token.at);
                }else fail("AI3107","Native operator declarations take one or two parameters.",f.token.at);
            }
            for(auto& p:f.parameters){p.symbol=declare(p.token,p.type,p.mutable_parameter);if(p.scoped){if(!p.type.is_reference())fail("AI3107","scoped requires a reference parameter type.",p.token.at);scoped_symbols.insert(p.symbol);}assigned.insert(p.symbol);if(p.out_parameter){out_required.insert(p.symbol);out_tokens.emplace(p.symbol,p.token);}}
            bool reaching=stmt(*f.body);if(!out_required.empty())for(int symbol:out_required)if(!out_written.contains(symbol))fail("AI3107","An out parameter must be assigned by the function.",out_tokens.at(symbol).at);if(reaching && f.fallible && result_type==Type::unit){auto finish=std::make_unique<Stmt>();finish->kind=Stmt::result;finish->token=Token{"return",f.body->end,Token::id};finish->expr=outcome_expression(f.type,0,unit_expression(finish->token),finish->token);f.body->children.push_back(std::move(finish));reaching=false;}if(reaching && result_type!=Type::unit){if(f.type==Type::buffer)require_value(Type::unit,f.type,f.token.at,nullptr,"AI3020",&f.token.at,"return-declaration");fail("AI3020","Function must return a value on every reaching path.",f.token.at);}
        }
    }
};
const char* ctype(const Type& type) {
    if(type.is_enum())return ctype(type.enum_underlying());
    if(type.is_decimal())return "ai_decimal";
    if(!type.bootstrap_supported())fail("AI3100","Native type has no qualified backend representation.",{});
    if(const auto* small=type.narrow_integer())return small->storage;
    return type==Type::dynamic ? "ai_object" : type==Type::object ? "ai_object" : type==Type::args ? "ai_args" : type==Type::string ? "ai_string" : type==Type::integer ? "int32_t" : type==Type::long_integer ? "int64_t" : type.is_uint32() ? "uint32_t" : type.is_ulong64() ? "uint64_t" : type.is_float32() ? "float" : type.is_float64() ? "double" : type.is_type_token() ? "uint32_t" : type==Type::boolean ? "bool" : type==Type::buffer ? "ai_owner" : "void";
}
#include "storage_ir.inc"
#include "native_recovery.inc"

}
NativeSource compile_native(std::string_view source,unsigned target_bits) {
    native_target_bits=target_bits==32 ? 32 : 64;
    // Recovery is a checked source lowering; a source without the gate and the
    // clause is passed through byte for byte, so self-host output is unchanged.
    auto recovery=rewrite_recovery(source);
    try {
        auto ir=verified_storage(recovery.text);auto entry=std::find_if(ir.begin(),ir.end(),[](auto& f){return f.entry;});
        if(entry==ir.end())fail("AI3100","Validated entry disappeared before emission.",{});
        // The program keeps the authored source identity even though the lowered
        // unit is what the emitter sees.
        return {StorageEmitter().run(ir,entry->id,native_source_sha(source)),entry->unwind,entry->fault_mode_location};
    } catch(Diagnostic diagnostic) {
        // Lowered positions map back to the authored file for every diagnostic.
        remap_recovery_diagnostic(recovery,source,diagnostic);
        throw diagnostic;
    }
}
NativeSource compile_native(std::string_view source) {return compile_native(source,64);}
std::string compile(std::string_view source) {return compile_native(source).text;}
std::string inspect_ir(std::string_view source) {
    auto recovery=rewrite_recovery(source);
    try {
        return storage_json(verified_storage(recovery.text));
    } catch(Diagnostic diagnostic) {
        remap_recovery_diagnostic(recovery,source,diagnostic);
        throw diagnostic;
    }
}
}
