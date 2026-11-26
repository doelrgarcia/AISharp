// Descriptor and private verifier faults supplement real CLI regressions.
// No source-reference or borrow-checker implementation claim is made here.
#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
#include <type_traits>
namespace aisharp {
int native_type_tests() {
    static_assert(std::is_same_v<std::underlying_type_t<Type::Primitive>,int>);
    static_assert(std::is_same_v<std::underlying_type_t<Type::Borrow>,int>);
    int checks=0,rejections=0;
    auto check=[&](bool value){if(!value)throw std::runtime_error("Native type check failed");++checks;};
    auto rejected=[&](auto operation){bool caught=false;try{operation();}catch(const Diagnostic& d){if(d.code!="AI3100")throw;caught=true;}check(caught);++rejections;};
    using B=Type::Borrow;
    for(Type root:{Type::integer,Type::boolean,Type::unit,Type::buffer}) {
        check(root.valid() && !root.is_reference());
        check(root.is_copy()==(root!=Type::buffer));check(root.needs_drop()==(root==Type::buffer));
        check(root.bootstrap_supported() && root.bootstrap_supported(false)==(root!=Type::unit));
        check(root.is_scalar()==(root==Type::integer || root==Type::boolean));
        for(auto kind:{B::shared,B::exclusive}) {
            auto type=Type::reference(root,kind);
            check(type.valid() && type.reference_depth()==1 && type.pointee()==root && type.borrow_kind()==kind);
            check(type.is_copy()==(kind==B::shared));check(!type.needs_drop() && !type.is_scalar());
            check(!type.bootstrap_supported() && !type.bootstrap_supported(false));
            check(type.label()==std::string(kind==B::shared ? "shared-reference<" : "exclusive-reference<")+root.label()+">");
            check(type!=root && type!=Type::reference(root,kind==B::shared ? B::exclusive : B::shared));
            rejected([&]{ctype(type);});
            for(auto outer:{B::shared,B::exclusive}) {
                auto nested=Type::reference(type,outer);auto separately_built=Type::reference(Type::reference(root,kind),outer);
                check(nested==separately_built && nested.pointee()==type && nested.reference_depth()==2);
                check(nested.is_copy()==(outer==B::shared));check(!nested.needs_drop());
                check(nested.label()==std::string(outer==B::shared ? "shared-reference<" : "exclusive-reference<")+type.label()+">");
            }
        }
    }
    auto nested=Type(Type::buffer);
    for(std::size_t i=0;i<Type::reference_depth_limit;++i)nested=Type::reference(nested,i%2 ? B::exclusive : B::shared);
    check(nested.reference_depth()==128 && !nested.needs_drop());auto original=nested;
    Location at{77,5,9,3};bool bounded=false;
    try{Type::reference(nested,B::shared,at);}catch(const Diagnostic& d){check(d.code=="AI3100" && d.location.offset==77 && d.location.line==5 && d.location.column==9 && d.location.length==3);bounded=true;++rejections;}
    check(bounded && original==nested);
    for(std::size_t i=0;i<Type::reference_depth_limit;++i)nested=nested.pointee();
    check(nested==Type::buffer && nested.needs_drop() && !nested.is_copy());
    rejected([&]{nested.pointee();});rejected([&]{nested.borrow_kind();});
    rejected([&]{Type::reference(nested,static_cast<B>(99));});
    Type invalid(static_cast<Type::Primitive>(99));check(!invalid.valid() && !invalid.is_copy() && !invalid.needs_drop() && !invalid.bootstrap_supported());
    rejected([&]{invalid.label();});rejected([&]{ctype(invalid);});rejected([&]{Type::reference(invalid,B::shared);});
    for(int tag:{-1,std::numeric_limits<int>::min(),std::numeric_limits<int>::max()}) {
        Type bad(static_cast<Type::Primitive>(tag));
        check(!bad.valid() && !bad.is_copy() && !bad.needs_drop() && !bad.bootstrap_supported());
        rejected([&]{bad.label();});rejected([&]{ctype(bad);});rejected([&]{Type::reference(bad,B::shared);});
        rejected([&]{Type::reference(Type::integer,static_cast<B>(tag));});
    }
    const std::string source="module TypeFault\nintent \"Unproved types reject\"\nfn echo(n: int) -> int\nensures true\n{return n}\nfn main() -> int\nensures true\n{return echo(8)}\n";
    for(auto type:{Type::reference(Type::integer,B::shared),Type::reference(Type::buffer,B::exclusive),Type::reference(Type::reference(Type::boolean,B::exclusive),B::shared),invalid}) {
        auto ir=verified_storage(source);ir[0].type=type;rejected([&]{StorageVerifier().run(ir);});
        ir=verified_storage(source);ir[0].places[0].type=type;rejected([&]{StorageVerifier().run(ir);});
        ir=verified_storage(source);ir[0].blocks[0].ops[1].type=type;rejected([&]{StorageVerifier().run(ir);});
        ir=verified_storage(source);ir[0].type=type;rejected([&]{StorageEmitter().run(ir,1,native_source_sha(source));});
    }
    auto ir=verified_storage(source);auto proof=storage_json(ir);StorageVerifier().run(ir);check(storage_json(ir)==proof);
    std::cout<<"{\"checks\":"<<checks<<",\"rejections\":"<<rejections<<",\"referenceDepthBoundary\":128,\"descriptorTestsOnly\":true}\n";return 0;
}
}
int main(){try{return aisharp::native_type_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
