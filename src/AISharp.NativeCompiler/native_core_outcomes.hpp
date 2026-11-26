#pragma once
#include <string_view>
namespace aisharp {
inline constexpr std::string_view core_outcomes_uri = "aisharp://std/core/outcomes/1";
inline constexpr std::string_view core_outcomes_sha256 = "6a8b0b154eb8e779796859c11c9e69c3bfe948dcef52cb9aba918963e034942a";
inline constexpr std::string_view core_outcomes_text = R"AISharpCore(module std.core
intent "Closed native outcome carriers"
type Option<T> = Some(T) | None
type Result<T, E> = Ok(T) | Err(E)
)AISharpCore";
}
