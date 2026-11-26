#include "../../src/AISharp.NativeCompiler/compiler.cpp"
#include <iostream>
namespace aisharp {
int borrowed_payload_tests(){
    int checks=0,rejections=0;auto check=[&](bool ok){if(!ok)throw std::runtime_error("Borrowed payload assertion failed");++checks;};auto reject=[&](auto action,const std::string& code="AI3100",const std::string& message=""){bool caught=false;try{action();}catch(const Diagnostic& d){if(d.code!=code || (!message.empty() && message!=d.what()))throw std::runtime_error("Unexpected diagnostic in rejection "+std::to_string(rejections)+": "+d.what());caught=true;++rejections;}check(caught);};
    auto module=verified_storage(R"AI(module BorrowedPayloadProof
intent "Case-proven borrowed projections"
type E=A(int)|B(int)|Empty
fn main()->int ensures true {let x:E=A(42)
return match &x {A(p)=>*p,B(p)=>*p,Empty=>0}}
)AI");
    auto& main=module[0];check(main.matches.size()==1);int subject=main.matches[0].subject,source=-1;for(auto& p:main.places)if(p.parent==-1 && p.type.is_payload_union())source=p.id;check(source>=0);check(IrPlaceGraph(main.places).at(subject).type.is_reference());
    auto corrupt=[&](auto change){auto copy=module;change(copy[0]);reject([&]{StorageVerifier().run(copy);});};
    corrupt([](auto& f){for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind=="variant-borrow" && o.field==0){o.field=1;return;}});
    corrupt([](auto& f){for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind=="variant-borrow"){o.type=Type::reference(Type::integer,Type::Borrow::exclusive);return;}});
    corrupt([&](auto& f){for(auto& p:f.places)if(p.id==subject)p.mutable_=true;});
    corrupt([](auto& f){for(auto& b:f.blocks){auto i=std::find_if(b.ops.begin(),b.ops.end(),[](auto& o){return o.kind=="variant-borrow" && o.field==0;});if(i!=b.ops.end()){auto operation=*i;b.ops.erase(i);f.blocks[static_cast<std::size_t>(f.matches[0].entry)].ops.push_back(operation);return;}}});
    corrupt([&](auto& f){for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind=="variant-tag"){o.place=source;return;}});
    corrupt([](auto& f){for(auto& b:f.blocks)for(auto& o:b.ops)if(o.kind=="variant-borrow"){o.kind="borrow-shared";return;}});
    reject([]{verified_storage(R"AI(module TagChangeEscape
intent "Mutation cannot hide a dangling return"
type E=Data(int)|Empty
fn clear(x:&mut E)->unit ensures true {*x=Empty}
fn bad(ok:&int)->&int ensures true {var x:E=Data(1)
let local=42
clear(&mut x)
return match &x {Data(_)=>ok,Empty=>&local}}
fn main()->int ensures true {let n=0
return *bad(&n)}
)AI");},"AI3117");
    reject([]{verified_storage(R"AI(module NoOwnerMove
intent "Borrowed owners retain their owner"
type E=Data(owned buffer)|Empty
fn main()->int ensures true {let x:E=Data(buffer(42))
return match &x {Data(p)=>bufferLength(*p),Empty=>0}}
)AI");},"AI3115");
    reject([]{
        auto forged=verified_storage(R"AI(module HiddenReference
intent "A saved tag cannot be invalidated through a hidden capability"
type E=A(owned buffer)|B(int)|Empty
fn main()->int ensures true {var x:E=A(buffer(42))
return match &mut x {A(p)=>bufferSize(p),B(p)=>*p,Empty=>0}}
)AI");auto& f=forged[0];auto& match=f.matches[0];auto pointer=IrPlaceGraph(f.places).at(match.subject).type;int next=0,number=-1;for(auto& b:f.blocks)for(auto& o:b.ops){next=std::max(next,o.id+1);if(o.kind=="constant" && o.type==Type::integer && o.literal==42)number=o.id;}if(number<0)throw std::runtime_error("Missing test argument");
        IrOp access{};access.id=next++;access.kind="reference-use";access.type=pointer;access.place=match.subject;access.at=match.at;
        IrOp pack{};pack.id=next++;pack.kind="variant-pack";pack.type=pointer.pointee();pack.field=1;pack.operands={number};pack.at=match.at;
        IrOp write{};write.id=next++;write.kind="reference-write";write.type=Type::unit;write.operands={access.id,pack.id};write.at=match.at;
        auto& entry=f.blocks[static_cast<std::size_t>(match.entry)];entry.ops.push_back(access);entry.ops.push_back(pack);entry.ops.push_back(write);StorageVerifier().run(forged);
    });
    reject([]{verified_storage(R"AI(module SharedNestedPermission
intent "Shared access to a mutable-reference payload cannot escalate"
type Inner=Data(int)|Empty
type Outer=Some(&mut Inner)|None
fn main()->int ensures true {var inner:Inner=Data(7)
let outer:Outer=Some(&mut inner)
return match &outer {Some(p)=>match *p {Data(q)=>(*q=9),Empty=>0},None=>0}}
)AI");},"AI3115");
    auto references=verified_storage(R"AI(module AbsentBorrowedReferences
intent "Inactive cells and reference-summary effects"
type E=Some(&int)|None
fn clear(x:&mut E)->int ensures true {*x=None
return match x {Some(p)=>**p,None=>42}}
fn main()->int ensures true {let n=1
var x:E=Some(&n)
return clear(&mut x)}
)AI");
    auto proof=storage_json(references);for(auto& f:references){f.summary={};f.loans.clear();f.reference_components.clear();}StorageVerifier().run(references);check(storage_json(references)==proof);
    reject([]{
        auto forged=verified_storage(R"AI(module GuardMutationProof
intent "Guard evaluation cannot mutate a candidate payload"
type E=Data(int)|Empty
fn main()->int ensures true {var x:E=Data(1)
return match &mut x {Data(p) when *p==1=>*p,Data(p)=>*p,Empty=>0}}
)AI");auto& f=forged[0];int next=0,number=-1;for(auto& b:f.blocks)for(auto& o:b.ops){next=std::max(next,o.id+1);if(b.id==0 && o.kind=="constant" && o.type==Type::integer && o.literal==1)number=o.id;}if(number<0)throw std::runtime_error("Missing guard test value");
        int guard=f.matches[0].groups[0].arms[0].guard_entry;auto& block=f.blocks[static_cast<std::size_t>(guard)];auto read=std::find_if(block.ops.begin(),block.ops.end(),[](auto& o){return o.kind=="reference-read";});if(read==block.ops.end())throw std::runtime_error("Missing guard read");
        IrOp write{};write.id=next;write.kind="reference-write";write.type=Type::unit;write.operands={read->operands[0],number};write.at=read->at;block.ops.insert(read,write);StorageVerifier().run(forged);
    },"AI3100","Native guard mutates or exclusively accesses a candidate payload before selection.");
    reject([]{
        auto forged=verified_storage(R"AI(module GuardCallProof
intent "A helper cannot mutate a candidate payload in a guard"
type E=Data(int)|Empty
fn touch(p:&mut int)->unit ensures true {*p=42}
fn main()->int ensures true {var x:E=Data(1)
return match &mut x {Data(p) when *p==1=>*p,Data(p)=>*p,Empty=>0}}
)AI");auto main=std::find_if(forged.begin(),forged.end(),[](auto& f){return f.entry;});auto helper=std::find_if(forged.begin(),forged.end(),[](auto& f){return !f.entry;});auto& f=*main;int next=0;for(auto& b:f.blocks)for(auto& o:b.ops)next=std::max(next,o.id+1);
        auto& block=f.blocks[static_cast<std::size_t>(f.matches[0].groups[0].arms[0].guard_entry)];auto read=std::find_if(block.ops.begin(),block.ops.end(),[](auto& o){return o.kind=="reference-read";});if(read==block.ops.end())throw std::runtime_error("Missing guard call argument");IrOp guard_pointer{};guard_pointer.id=next++;guard_pointer.kind="reborrow-exclusive";guard_pointer.type=Type::reference(Type::integer,Type::Borrow::exclusive);guard_pointer.operands={read->operands[0]};guard_pointer.at=read->at;IrOp call{};call.id=next;call.kind="call";call.type=Type::unit;call.function=helper->id;call.operands={guard_pointer.id};call.at=read->at;block.ops.insert(read,{guard_pointer,call});StorageVerifier().run(forged);
    },"AI3100","Native guard mutates or exclusively accesses a candidate payload before selection.");
    std::cout<<"{\"supplementalBorrowedPayloadTestsOnly\":true,\"recomputedProof\":true,\"checks\":"<<checks<<",\"rejections\":"<<rejections<<"}\n";return 0;
}
}
int main(){try{return aisharp::borrowed_payload_tests();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
