#include "compiler.hpp"
#include <windows.h>
#include <bcrypt.h>
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <map>
#include <memory>
#include <sstream>
#include <string>
#include <vector>

namespace fs=std::filesystem;
namespace {
struct Handle {
    HANDLE value=INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE h=INVALID_HANDLE_VALUE):value(h){}
    ~Handle(){if(value!=INVALID_HANDLE_VALUE && value!=nullptr)CloseHandle(value);}
    Handle(const Handle&)=delete; Handle& operator=(const Handle&)=delete;
};
std::string utf8(std::wstring_view value) {
    if(value.empty())return {};
    int size=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0,nullptr,nullptr);
    if(!size)throw std::runtime_error("Invalid Unicode path.");
    std::string result(static_cast<std::size_t>(size),'\0');
    if(!WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),result.data(),size,nullptr,nullptr))throw std::runtime_error("Cannot encode path.");
    return result;
}
std::string json(std::string_view text) {
    std::string result="\"";const char* hex="0123456789abcdef";
    for(unsigned char c:text) {
        if(c=='"' || c=='\\'){result+='\\';result+=static_cast<char>(c);}
        else if(c<32){result+="\\u00";result+=hex[c>>4];result+=hex[c&15];}
        else result+=static_cast<char>(c);
    }
    return result+'"';
}
void location(std::ostream& out,aisharp::Location at,const fs::path& input){
    bool core=at.origin==aisharp::SourceOrigin::core_outcomes;if(!core && at.origin!=aisharp::SourceOrigin::input)throw std::runtime_error("Invalid diagnostic source identity.");out<<"{\"path\":"<<json(core ? std::string(aisharp::core_outcomes_uri) : utf8(input.native()))<<",\"offset\":"<<at.offset<<",\"line\":"<<at.line<<",\"column\":"<<at.column<<",\"length\":"<<at.length;if(core)out<<",\"sourceSha256\":"<<json(aisharp::core_outcomes_sha256);out<<'}';
}
std::string read(const fs::path& path) {
    Handle file(CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
    if(file.value==INVALID_HANDLE_VALUE)throw std::runtime_error("Cannot open a stable input snapshot.");
    LARGE_INTEGER size{};if(!GetFileSizeEx(file.value,&size) || size.QuadPart<0 || size.QuadPart>64LL*1024*1024)throw std::runtime_error("Native bootstrap file size bound exceeded.");
    std::string bytes(static_cast<std::size_t>(size.QuadPart),'\0');DWORD received=0;
    if(!bytes.empty() && (!ReadFile(file.value,bytes.data(),static_cast<DWORD>(bytes.size()),&received,nullptr) || received!=bytes.size()))throw std::runtime_error("Cannot read a complete input snapshot.");
    return bytes;
}
std::string sha(std::string_view data) {
    // Destructor bodies run before member destruction: the hash object's backing
    // storage must remain alive until BCryptDestroyHash has finished on all exits.
    struct HashState {
        BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;
        std::vector<unsigned char> object,digest;
        ~HashState(){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);}
    } state;
    auto& algorithm=state.algorithm;auto& hash=state.hash;auto& object=state.object;auto& digest=state.digest;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)throw std::runtime_error("SHA256 provider unavailable.");
    DWORD size=0,returned=0;
    if(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&size),sizeof(size),&returned,0)<0)throw std::runtime_error("SHA256 size unavailable.");
    object.resize(size);digest.resize(32);
    if(BCryptCreateHash(algorithm,&hash,object.data(),size,nullptr,0,0)<0 || data.size()>std::numeric_limits<ULONG>::max() || BCryptHashData(hash,reinterpret_cast<PUCHAR>(const_cast<char*>(data.data())),static_cast<ULONG>(data.size()),0)<0 || BCryptFinishHash(hash,digest.data(),static_cast<ULONG>(digest.size()),0)<0)throw std::runtime_error("SHA256 calculation failed.");
    const char* hex="0123456789abcdef";std::string result;for(auto c:digest){result+=hex[c>>4];result+=hex[c&15];}return result;
}
void write_new(const fs::path& path, std::string_view bytes) {
    Handle file(CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
    if(file.value==INVALID_HANDLE_VALUE)throw std::runtime_error("Output already exists or cannot be created.");
    DWORD written=0;if(bytes.size()>MAXDWORD || !WriteFile(file.value,bytes.data(),static_cast<DWORD>(bytes.size()),&written,nullptr) || written!=bytes.size())throw std::runtime_error("Cannot finish output file.");
}
std::wstring quote(std::wstring_view value) {
    std::wstring result=L"\"";std::size_t slashes=0;
    for(wchar_t c:value){if(c==L'\\'){++slashes;continue;}if(c==L'"')result.append(slashes*2+1,L'\\');else result.append(slashes,L'\\');slashes=0;result+=c;}
    result.append(slashes*2,L'\\');return result+L'"';
}
std::string nonce() {
    unsigned char bytes[16];if(BCryptGenRandom(nullptr,bytes,sizeof(bytes),BCRYPT_USE_SYSTEM_PREFERRED_RNG)<0)throw std::runtime_error("Random temporary identity unavailable.");
    const char* hex="0123456789abcdef";std::string value;for(auto b:bytes){value+=hex[b>>4];value+=hex[b&15];}return value;
}
struct Temps {
    fs::path directory;
    std::vector<fs::path> paths;
    ~Temps(){for(auto& p:paths){std::error_code error;fs::remove(p,error);}if(!directory.empty())RemoveDirectoryW(directory.c_str());}
};
int invoke(const fs::path& cc, const std::vector<std::wstring>& args, const fs::path& directory, const fs::path& log) {
    SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};
    Handle output(CreateFileW(log.c_str(),GENERIC_WRITE,FILE_SHARE_READ,&security,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
    Handle input(CreateFileW(L"NUL",GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
    if(output.value==INVALID_HANDLE_VALUE || input.value==INVALID_HANDLE_VALUE)throw std::runtime_error("Cannot create compiler transport.");
    Handle job(CreateJobObjectW(nullptr,nullptr));if(!job.value)throw std::runtime_error("Cannot create compiler process job.");
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_ACTIVE_PROCESS;limits.BasicLimitInformation.ActiveProcessLimit=128;
    if(!SetInformationJobObject(job.value,JobObjectExtendedLimitInformation,&limits,sizeof(limits)))throw std::runtime_error("Cannot bound compiler process lifetime.");
    std::wstring command=quote(cc.native());for(auto& arg:args)command+=L" "+quote(arg);
    STARTUPINFOW start{};start.cb=sizeof(start);start.dwFlags=STARTF_USESTDHANDLES;start.hStdInput=input.value;start.hStdOutput=output.value;start.hStdError=output.value;
    PROCESS_INFORMATION process{};
    if(!CreateProcessW(cc.c_str(),command.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW|CREATE_SUSPENDED,nullptr,directory.c_str(),&start,&process))throw std::runtime_error("Cannot start native C toolchain.");
    Handle child(process.hProcess),thread(process.hThread);
    if(!AssignProcessToJobObject(job.value,child.value)){TerminateProcess(child.value,3);WaitForSingleObject(child.value,10000);throw std::runtime_error("Cannot supervise compiler process tree.");}
    if(ResumeThread(thread.value)==static_cast<DWORD>(-1)){TerminateJobObject(job.value,3);WaitForSingleObject(child.value,10000);throw std::runtime_error("Cannot resume native compiler.");}
    auto waited=WaitForSingleObject(child.value,180000);
    if(waited!=WAIT_OBJECT_0){TerminateJobObject(job.value,3);WaitForSingleObject(child.value,10000);throw std::runtime_error("Native compiler timed out or could not be observed.");}
    DWORD code=0;if(!GetExitCodeProcess(child.value,&code))throw std::runtime_error("Cannot read native compiler exit.");
    // A failed toolchain can leave descendants holding its working directory.
    // Job termination is asynchronous; observe zero active processes before the
    // caller retires temporary files or publishes a successful artifact.
    std::vector<unsigned char> process_ids(sizeof(JOBOBJECT_BASIC_PROCESS_ID_LIST)+128*sizeof(ULONG_PTR));
    auto* ids=reinterpret_cast<JOBOBJECT_BASIC_PROCESS_ID_LIST*>(process_ids.data());
    if(!QueryInformationJobObject(job.value,JobObjectBasicProcessIdList,ids,static_cast<DWORD>(process_ids.size()),nullptr))throw std::runtime_error("Cannot identify compiler descendants.");
    std::vector<std::unique_ptr<Handle>> descendants;
    for(DWORD i=0;i<ids->NumberOfProcessIdsInList;++i){HANDLE descendant=OpenProcess(SYNCHRONIZE,FALSE,static_cast<DWORD>(ids->ProcessIdList[i]));if(descendant)descendants.push_back(std::make_unique<Handle>(descendant));else if(GetLastError()!=ERROR_INVALID_PARAMETER)throw std::runtime_error("Cannot observe a compiler descendant handle.");}
    if(!TerminateJobObject(job.value,3))throw std::runtime_error("Cannot retire compiler descendants.");
    for(auto& descendant:descendants)if(WaitForSingleObject(descendant->value,10000)!=WAIT_OBJECT_0)throw std::runtime_error("Compiler descendant did not finish teardown.");
    auto deadline=GetTickCount64()+10000;
    for(;;){JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
        if(!QueryInformationJobObject(job.value,JobObjectBasicAccountingInformation,&accounting,sizeof(accounting),nullptr))throw std::runtime_error("Cannot observe compiler descendants.");
        if(accounting.ActiveProcesses==0)break;
        if(GetTickCount64()>=deadline)throw std::runtime_error("Compiler descendants did not terminate.");Sleep(10);
    }
    return static_cast<int>(code);
}
unsigned machine(const fs::path& path) {
    auto image=read(path);
    auto u16=[&](std::size_t at){if(at+2>image.size())throw std::runtime_error("Truncated PE artifact.");return static_cast<unsigned char>(image[at])|(static_cast<unsigned>(static_cast<unsigned char>(image[at+1]))<<8);};
    auto u32=[&](std::size_t at){return u16(at)|(u16(at+2)<<16);};
    if(u16(0)!=0x5a4d)throw std::runtime_error("Toolchain artifact is not a PE executable.");auto offset=u32(0x3c);
    if(u32(offset)!=0x4550)throw std::runtime_error("Toolchain artifact has no PE signature.");
    // A generated native program must not have a CLR data directory.
    auto optional=offset+24;auto magic=u16(optional);auto data=optional+(magic==0x20b ? 112u : magic==0x10b ? 96u : 0u);
    if(data==optional || u32(data+14*8)!=0 || u32(data+14*8+4)!=0)throw std::runtime_error("Toolchain produced managed or invalid output.");
    return u16(offset+4);
}
int run(int argc,wchar_t** argv) {
    if(argc==2 && std::wstring_view(argv[1])==L"--version") {std::cout<<"{\"capability\":"<<json(aisharp::capability)<<",\"host\":\"native-cpp\",\"requiresDotnet\":false,\"ownershipTarget\":\"rust-style\",\"memoryModel\":\"owned-buffers-function-regions\",\"borrowCheckerImplemented\":true,\"borrowCheckingScope\":\"monomorphic-function-regions\",\"interproceduralReferences\":true,\"languageHeapValues\":true,\"tracingGcRequired\":false,\"productSupport\":\"native-products-cyclic-input-graphs\",\"cyclicProductInputRegions\":true,\"zeroFieldProducts\":true,\"genericTypeApplications\":true,\"genericFunctions\":false,\"payloadUnions\":true,\"borrowedPayloadMatch\":true,\"standardOutcomeCarriers\":true,\"coreSourceDocument\":"<<json(aisharp::core_outcomes_uri)<<",\"coreSourceSha256\":"<<json(aisharp::core_outcomes_sha256)<<",\"typedFailurePropagation\":true,\"declaredFailureContracts\":true,\"mustHandleOutcomes\":true,\"failureCauseFrames\":8,\"closedUnions\":true,\"valueMatch\":true,\"failureModel\":\"typed-outcomes\",\"unexpectedFaultModel\":\"declared-abort-or-unwind\",\"defaultFaultMode\":\"abort\",\"faultModes\":[\"abort\",\"unwind\"],\"nativeFaultUnwinding\":true,\"nativeDebugRuntime\":true,\"operationRecovery\":false}\n";return 0;}
    fs::path source;std::string source_hash;
    try {
        if(argc<3)throw std::runtime_error("Usage: aisharp-native <check|inspect-ir|emit-c|emit-native|build-native> <source> [--out new-file] [--bits 32|64 --cc absolute-cl.exe] [--optimize] [--debug] [--json]");
        std::wstring command=argv[1];if(command!=L"check" && command!=L"inspect-ir" && command!=L"emit-c" && command!=L"emit-native" && command!=L"build-native")throw std::runtime_error("Unknown command.");
        std::map<std::wstring,std::wstring> options;bool optimized=false,debug_runtime=false,json_flag=false;
        for(int i=3;i<argc;++i){std::wstring key=argv[i];if(key==L"--json"){if(json_flag)throw std::runtime_error("Duplicate --json.");json_flag=true;continue;}if(key==L"--debug"){if(debug_runtime)throw std::runtime_error("Duplicate --debug.");debug_runtime=true;continue;}if(key==L"--optimize"){if(optimized)throw std::runtime_error("Duplicate --optimize.");optimized=true;continue;}
            if((key!=L"--out" && key!=L"--bits" && key!=L"--cc") || i+1==argc || !options.emplace(key,argv[++i]).second)throw std::runtime_error("Unknown, duplicate or missing option.");}
        if((command==L"check" || command==L"inspect-ir") && (!options.empty() || optimized || debug_runtime))throw std::runtime_error("Check does not accept build/output options.");
        if((command==L"emit-c" || command==L"emit-native") && (options.size()!=1 || !options.contains(L"--out") || optimized || debug_runtime))throw std::runtime_error("Native emission requires only --out.");
        fs::path destination,cc;unsigned bits=0;
        if(command!=L"check" && command!=L"inspect-ir") {if(!options.contains(L"--out"))throw std::runtime_error("Explicit --out required.");destination=fs::absolute(options.at(L"--out"));if(fs::exists(destination))throw std::runtime_error("Existing output is preserved; choose a new path.");if(!fs::is_directory(destination.parent_path()))throw std::runtime_error("Output directory must already exist.");}
        if(command==L"build-native") {
            if(options.size()!=3 || !options.contains(L"--bits") || !options.contains(L"--cc"))throw std::runtime_error("Explicit --bits and --cc required.");auto width=options.at(L"--bits");if(width!=L"32" && width!=L"64")throw std::runtime_error("Target width must be 32 or 64.");bits=width==L"32" ? 32u : 64u;
            cc=options.at(L"--cc");if(!cc.is_absolute() || !fs::is_regular_file(cc) || _wcsicmp(cc.filename().c_str(),L"cl.exe")!=0)throw std::runtime_error("--cc must name an absolute installed cl.exe.");if(_wcsicmp(destination.extension().c_str(),L".exe")!=0)throw std::runtime_error("Native destination must end with .exe.");
        }
        source=fs::absolute(argv[2]);if(fs::file_size(source)>1024*1024)throw aisharp::Diagnostic("AI3100","Native bootstrap source limit is 1 MiB.",{});auto bytes=read(source);source_hash=sha(bytes);
        if(command==L"inspect-ir") {auto proof=aisharp::inspect_ir(bytes);std::cout<<"{\"success\":true,\"capability\":"<<json(aisharp::capability)<<",\"sourceSha256\":"<<json(source_hash)<<",\"ir\":"<<proof<<"}\n";std::cout.flush();return std::cout ? 0 : 74;}
        auto generated=aisharp::compile_native(bytes,bits);auto& c=generated.text;
        if(command==L"check") {std::cout<<"{\"success\":true,\"capability\":"<<json(aisharp::capability)<<",\"sourceSha256\":"<<json(source_hash)<<",\"diagnostics\":[]}\n";return 0;}
        if(command==L"emit-c" && generated.cpp)throw aisharp::Diagnostic("AI3134","Unwind mode requires C++ native emission; use emit-native or build-native.",generated.fault_mode);
        if(command==L"emit-native") {write_new(destination,c);std::cout<<"{\"success\":true,\"capability\":"<<json(aisharp::capability)<<",\"sourceSha256\":"<<json(source_hash)<<",\"nativeSourceLanguage\":"<<json(generated.cpp ? "c++20" : "c11")<<",\"nativeSourceSha256\":"<<json(sha(c))<<",\"artifact\":"<<json(utf8(destination.native()))<<"}\n";return 0;}
        if(command==L"emit-c") {write_new(destination,c);std::cout<<"{\"success\":true,\"capability\":"<<json(aisharp::capability)<<",\"sourceSha256\":"<<json(source_hash)<<",\"cSourceSha256\":"<<json(sha(c))<<",\"artifact\":"<<json(utf8(destination.native()))<<"}\n";return 0;}
        auto temporary=destination.parent_path()/fs::path("aisharp-native-host-"+nonce());
        if(!CreateDirectoryW(temporary.c_str(),nullptr))throw std::runtime_error("Cannot exclusively create compiler temporary directory.");
        auto cfile=temporary/(generated.cpp ? L"program.cpp" : L"program.c"),object=temporary/L"program.obj",executable=temporary/L"program.exe",log=temporary/L"compiler.log";
        Temps temps{temporary,{cfile,object,executable,log}};write_new(cfile,c);
        std::vector<std::wstring> args={L"/nologo",generated.cpp ? L"/TP" : L"/TC",generated.cpp ? L"/std:c++20" : L"/std:c11",L"/W4",L"/WX",L"/wd4702",debug_runtime ? L"/MTd" : L"/MT",optimized ? L"/O2" : L"/Od",cfile.native(),L"/Fo"+object.native(),L"/Fe"+executable.native(),L"/link",L"/STACK:8388608",L"/MANIFEST:EMBED",L"/MANIFESTUAC:level='asInvoker' uiAccess='false'"};
        if(generated.cpp)args.insert(args.begin()+3,L"/EHsc");
        int compiler_exit=invoke(cc,args,temporary,log);
        if(compiler_exit!=0){std::cout<<"{\"success\":false,\"compilerExit\":"<<compiler_exit<<",\"log\":"<<json(read(log))<<"}\n";return 3;}
        auto actual_machine=machine(executable);if(actual_machine!=(bits==32 ? 0x14cu : 0x8664u))throw std::runtime_error("Native toolchain architecture differs from --bits.");
        auto artifact_hash=sha(read(executable));
        if(!MoveFileW(executable.c_str(),destination.c_str()))throw std::runtime_error("Cannot publish artifact; any existing output is preserved.");
        std::cout<<"{\"success\":true,\"capability\":"<<json(aisharp::capability)<<",\"sourceSha256\":"<<json(source_hash)<<",\"cSourceSha256\":"<<json(sha(c))<<",\"nativeSourceLanguage\":"<<json(generated.cpp ? "c++20" : "c11")<<",\"nativeSourceSha256\":"<<json(sha(c))<<",\"artifactSha256\":"<<json(artifact_hash)<<",\"pointerBits\":"<<bits<<",\"optimized\":"<<(optimized ? "true" : "false")<<",\"debugRuntime\":"<<(debug_runtime ? "true" : "false")<<",\"peMachine\":"<<actual_machine<<",\"artifact\":"<<json(utf8(destination.native()))<<"}\n";return 0;
    } catch(const aisharp::Diagnostic& diagnostic) {
        auto at=diagnostic.location;
        std::cout<<"{\"success\":false,\"capability\":"<<json(aisharp::capability)<<",\"sourceSha256\":"<<(source_hash.empty() ? "null" : json(source_hash))<<",\"diagnostics\":[{\"code\":"<<json(diagnostic.code)<<",\"severity\":\"error\",\"message\":"<<json(diagnostic.what())<<",\"location\":";location(std::cout,at,source);
        if(!diagnostic.failureObligation.empty())std::cout<<",\"obligationKind\":"<<json(diagnostic.failureObligation)<<",\"expectedType\":"<<json(diagnostic.expectedType)<<",\"actualType\":"<<json(diagnostic.actualType);
        bool ownership=diagnostic.ownerSymbol>=0 || !diagnostic.ownershipObligation.empty();
        if(ownership){
            std::cout<<",\"obligationKind\":"<<json(diagnostic.ownershipObligation.empty() ? "owned-value-availability" : diagnostic.ownershipObligation);
            if(diagnostic.ownerSymbol>=0)std::cout<<",\"symbolId\":"<<diagnostic.ownerSymbol;
            if(!diagnostic.expectedType.empty())std::cout<<",\"expectedType\":"<<json(diagnostic.expectedType)<<",\"actualType\":"<<json(diagnostic.actualType);
            std::cout<<",\"possibleCycle\":"<<(diagnostic.possibleCycle ? "true" : "false");
        }
        if(ownership || !diagnostic.related.empty()){
            std::cout<<",\"relatedLocations\":[";
            bool comma=false;for(auto& related:diagnostic.related){if(comma)std::cout<<',';comma=true;std::cout<<"{\"kind\":"<<json(related.kind)<<",\"location\":";location(std::cout,related.location,source);std::cout<<'}';}std::cout<<']';
        }
        std::cout<<"}]}\n";return 2;
    } catch(const std::exception& error) {std::cout<<"{\"success\":false,\"error\":"<<json(error.what())<<"}\n";return 1;}
}
}
int wmain(int argc,wchar_t** argv) { try {int result=run(argc,argv);std::cout.flush();return std::cout ? result : 74;}catch(...){std::cout<<"{\"success\":false,\"error\":\"Native compiler host failed.\"}\n";std::cout.flush();return std::cout ? 1 : 74;} }
