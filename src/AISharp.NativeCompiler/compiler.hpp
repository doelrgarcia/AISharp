#pragma once
#include <cstddef>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>
#include "native_core_outcomes.hpp"

namespace aisharp {
inline constexpr std::string_view capability = "aisharp.native-bootstrap/14";
enum class SourceOrigin { input, core_outcomes };
struct Location { std::size_t offset = 0, line = 1, column = 1, length = 0; SourceOrigin origin=SourceOrigin::input; bool operator==(const Location&) const=default; };
struct Diagnostic : std::runtime_error {
    struct Related { std::string kind; Location location; };
    std::string code;
    Location location;
    int ownerSymbol=-1;
    bool possibleCycle=false;
    std::string ownershipObligation, failureObligation, expectedType, actualType;
    std::vector<Related> related;
    Diagnostic(std::string code_, std::string message, Location at)
        : std::runtime_error(std::move(message)), code(std::move(code_)), location(at) {}
};
// Validates UTF-8, parses the declared bootstrap grammar, binds/types/checks flow,
// then emits checked native backend source. No managed process or CLR API.
struct NativeSource {std::string text;bool cpp=false;Location fault_mode;};
NativeSource compile_native(std::string_view source);
NativeSource compile_native(std::string_view source, unsigned target_bits);
std::string compile(std::string_view source);
// Stable typed storage/control-flow proof; excludes runtime/literal payloads.
std::string inspect_ir(std::string_view source);
}
