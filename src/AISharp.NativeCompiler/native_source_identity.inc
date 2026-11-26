#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <bcrypt.h>
#pragma comment(lib,"bcrypt.lib")
namespace {
std::string native_source_sha(std::string_view data) {
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
}
