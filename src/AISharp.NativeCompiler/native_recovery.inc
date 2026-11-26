// Recovery front end: module-gated recovery policies and the `recovers` clause.
//
// The surface is deliberately lowered before the ordinary parser: a checked
// source-to-source rewrite turns
//
//   recovery policy sync:
//       attempts 3
//       deadline 2000ms
//       ...
//   fn readLine(path:string)->Result<int, IoError> recovers sync { ... }
//
// into ordinary AI#. The declared body becomes one attempt function, and a
// synthesized wrapper applies the policy: it captures the monotonic deadline,
// checks cancellation and deadline at every boundary checkpoint, classifies a
// retryable failure, releases the attempt scope (the attempt returns before any
// backoff), and returns Result<T, RecoveryFailure<E>>. Nothing new enters the
// storage IR, so cleanup, fault propagation, and the self-host fixed point keep
// using the already verified mechanisms.
//
// Sources without a recovery policy or a recovers clause are returned unchanged
// byte for byte. That property is what keeps the self-host evidence stable.
namespace recovery_lowering {

struct RTok {
    enum Kind { identifier_token, number_token, text_token, punctuation_token } kind = punctuation_token;
    std::string text;
    std::size_t offset = 0, length = 0;
};

bool word_char(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'; }
bool starts_identifier(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_'; }

std::vector<RTok> lex(std::string_view source) {
    std::vector<RTok> tokens;
    std::size_t i = 0;
    while (i < source.size()) {
        const char c = source[i];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v') { ++i; continue; }
        if (c == '/' && i + 1 < source.size() && source[i + 1] == '/') { while (i < source.size() && source[i] != '\n') ++i; continue; }
        if (c == '/' && i + 1 < source.size() && source[i + 1] == '*') {
            i += 2;
            while (i + 1 < source.size() && !(source[i] == '*' && source[i + 1] == '/')) ++i;
            i = i + 1 < source.size() ? i + 2 : source.size();
            continue;
        }
        if (c == '"' || c == '\'') {
            const char quote = c; const std::size_t start = i++;
            while (i < source.size() && source[i] != quote) { if (source[i] == '\\' && i + 1 < source.size()) ++i; ++i; }
            if (i < source.size()) ++i;
            RTok token; token.kind = RTok::text_token; token.text = std::string(source.substr(start, i - start)); token.offset = start; token.length = i - start;
            tokens.push_back(std::move(token));
            continue;
        }
        if (starts_identifier(c)) {
            const std::size_t start = i;
            while (i < source.size() && word_char(source[i])) ++i;
            RTok token; token.kind = RTok::identifier_token; token.text = std::string(source.substr(start, i - start)); token.offset = start; token.length = i - start;
            tokens.push_back(std::move(token));
            continue;
        }
        if (c >= '0' && c <= '9') {
            const std::size_t start = i;
            while (i < source.size() && source[i] >= '0' && source[i] <= '9') ++i;
            RTok token; token.kind = RTok::number_token; token.text = std::string(source.substr(start, i - start)); token.offset = start; token.length = i - start;
            tokens.push_back(std::move(token));
            continue;
        }
        if (c == '.' && i + 1 < source.size() && source[i + 1] == '.') {
            RTok token; token.kind = RTok::punctuation_token; token.text = ".."; token.offset = i; token.length = 2;
            tokens.push_back(std::move(token)); i += 2; continue;
        }
        if (c == '-' && i + 1 < source.size() && source[i + 1] == '>') {
            RTok token; token.kind = RTok::punctuation_token; token.text = "->"; token.offset = i; token.length = 2;
            tokens.push_back(std::move(token)); i += 2; continue;
        }
        RTok token; token.kind = RTok::punctuation_token; token.text = std::string(1, c); token.offset = i; token.length = 1;
        tokens.push_back(std::move(token)); ++i;
    }
    return tokens;
}

Location at(std::string_view source, const RTok& token) {
    Location location; location.offset = token.offset; location.length = token.length; location.line = 1; location.column = 1;
    for (std::size_t i = 0; i < token.offset && i < source.size(); ++i) {
        if (source[i] == '\n') { ++location.line; location.column = 1; } else ++location.column;
    }
    return location;
}

std::string_view trim(std::string_view text) {
    std::size_t begin = 0, end = text.size();
    while (begin < end && (text[begin] == ' ' || text[begin] == '\t' || text[begin] == '\r' || text[begin] == '\n')) ++begin;
    while (end > begin && (text[end - 1] == ' ' || text[end - 1] == '\t' || text[end - 1] == '\r' || text[end - 1] == '\n')) --end;
    return text.substr(begin, end - begin);
}

// Split at top-level commas: generic arguments and parameter types may nest.
std::vector<std::string> split_arguments(std::string_view text) {
    std::vector<std::string> parts; std::size_t start = 0; int angles = 0, parens = 0, brackets = 0, braces = 0;
    for (std::size_t i = 0; i < text.size(); ++i) {
        const char c = text[i];
        if (c == '<') ++angles; else if (c == '>') { if (angles > 0) --angles; }
        else if (c == '(') ++parens; else if (c == ')') { if (parens > 0) --parens; }
        else if (c == '[') ++brackets; else if (c == ']') { if (brackets > 0) --brackets; }
        else if (c == '{') ++braces; else if (c == '}') { if (braces > 0) --braces; }
        else if (c == ',' && angles == 0 && parens == 0 && brackets == 0 && braces == 0) {
            parts.push_back(std::string(trim(text.substr(start, i - start)))); start = i + 1;
        }
    }
    parts.push_back(std::string(trim(text.substr(start))));
    return parts;
}

std::string join(const std::vector<std::string>& values) {
    std::string result;
    for (std::size_t i = 0; i < values.size(); ++i) { if (i) result += ", "; result += values[i]; }
    return result;
}

struct Policy {
    std::string name;
    Location location;
    long long attempts = 1, deadline = -1, attempt_limit = -1, backoff_min = 0, backoff_max = 0;
    bool replay_safe = false;
    bool seen_attempts = false, seen_deadline = false, seen_limit = false, seen_backoff = false, seen_retry = false, seen_replay = false;
    std::vector<std::string> retry;
    std::size_t block_start = 0, block_end = 0;
};

struct Case { std::string name; bool owning = false; bool payload_empty = false; };
struct Union { std::vector<Case> cases; bool generic = false; bool product = false; bool known = false; };

struct Parameter { std::string text; std::string name; };

struct Function {
    std::string name;
    Location location;
    std::size_t name_offset = 0, name_length = 0;
    std::size_t recovers_start = 0, recovers_end = 0;
    std::size_t body_open = 0, body_close = 0;
    std::string returns, parameters_text, argument_names, policy;
    std::vector<Parameter> parameters;
    bool recursive = false, unsupported_parameters = false, has_ensures = false;
};

struct Edit {
    std::size_t start = 0, end = 0;
    std::string text;
    bool blank = false;  // replace with spaces, preserving newlines
};

const std::set<std::string>& top_level_declarations() {
    static const std::set<std::string> names = {
        "fn", "type", "recovery", "extern", "enum", "namespace", "module", "managed", "unmanaged",
        "delegate", "class", "struct", "record", "interface", "partial", "alias", "sealed", "file",
        "abstract", "constructor", "extension", "operator", "get", "set", "add", "remove", "intent", "faults"
    };
    return names;
}

bool is_number(const RTok& token) { return token.kind == RTok::number_token; }

class Rewriter {
public:
    Rewriter(std::string_view source_, std::vector<RTok> tokens_) : source(source_), tokens(std::move(tokens_)) {}

    std::string run(bool& active, std::vector<std::size_t>& mapping) {
        active = false;
        bool interesting = false;
        for (const auto& token : tokens) if (token.kind == RTok::identifier_token && (token.text == "recovers" || token.text == "recovery")) { interesting = true; break; }
        if (!interesting) return std::string(source);
        scan();
        validate();
        active = true;
        return apply(mapping);
    }

private:
    std::string_view source;
    std::vector<RTok> tokens;
    bool unwinding = false;
    std::size_t insert_at = 0;
    std::map<std::string, Policy> policies;
    std::map<std::string, Union> unions;
    std::vector<Function> functions;
    std::set<std::string> extern_names;
    std::set<std::string> identifiers;

    [[noreturn]] void bad(std::string code, std::string message, const RTok& token) {
        fail(std::move(code), std::move(message), at(source, token));
    }

    std::size_t line_end(std::size_t offset) const {
        std::size_t end = offset;
        while (end < source.size() && source[end] != '\n') ++end;
        return end < source.size() ? end + 1 : source.size();
    }

    long long number(const RTok& token) {
        if (!is_number(token)) bad("AI3147", "A recovery policy limit must be a non-negative integer constant.", token);
        long long value = 0;
        for (char c : token.text) {
            if (value > (long long{1} << 40)) bad("AI3147", "A recovery policy limit is out of range.", token);
            value = value * 10 + (c - '0');
        }
        return value;
    }

    std::size_t skip_balanced(std::size_t i, char open, char close) const {
        int depth = 0;
        for (; i < tokens.size(); ++i) {
            if (tokens[i].text.size() == 1 && tokens[i].text[0] == open) ++depth;
            else if (tokens[i].text.size() == 1 && tokens[i].text[0] == close) { if (--depth == 0) return i + 1; }
        }
        return tokens.size();
    }

    bool declaration_ahead(std::size_t i) const {
        return i < tokens.size() && tokens[i].kind == RTok::identifier_token && top_level_declarations().contains(tokens[i].text);
    }

    void scan() {
        if (!tokens.empty() && tokens[0].text == "module") {
            // Insert synthesized declarations after the intent line, or after the
            // module fault-mode line when one is present.
            std::size_t i = 1;
            while (i < tokens.size() && !(tokens[i].kind == RTok::identifier_token && tokens[i].text == "intent")) ++i;
            insert_at = i < tokens.size() ? line_end(tokens[i].offset + tokens[i].length) : 0;
        }
        int depth = 0;
        std::size_t i = 0;
        while (i < tokens.size()) {
            const RTok& token = tokens[i];
            if (token.kind == RTok::identifier_token) identifiers.insert(token.text);
            if (depth == 0 && token.kind == RTok::identifier_token) {
                if (token.text == "faults" && i + 1 < tokens.size() && (tokens[i + 1].text == "abort" || tokens[i + 1].text == "unwind")) {
                    unwinding = tokens[i + 1].text == "unwind";
                    insert_at = line_end(tokens[i + 1].offset + tokens[i + 1].length);
                    i += 2;
                    continue;
                }
                if (token.text == "recovery" && i + 1 < tokens.size() && tokens[i + 1].text == "policy") { i = parse_policy(i); continue; }
                if (token.text == "type" || token.text == "struct" || token.text == "record") { i = parse_type(i); continue; }
                if (token.text == "fn") { i = parse_function(i); continue; }
                if (token.text == "extern") {
                    if (i + 2 < tokens.size() && tokens[i + 1].text == "fn") extern_names.insert(tokens[i + 2].text);
                    const std::size_t end = line_end(tokens[i].offset);
                    while (i < tokens.size() && tokens[i].offset < end) ++i;
                    continue;
                }
                if (token.text == "namespace") {
                    // Synthesized types stay inside a file-scoped namespace so an
                    // unprefixed RecoveryFailure resolves for the user module.
                    if (i + 1 < tokens.size() && tokens[i + 1].kind == RTok::identifier_token) insert_at = line_end(tokens[i + 1].offset + tokens[i + 1].length);
                    ++i;
                    continue;
                }
            }
            if (token.text == "{") ++depth;
            else if (token.text == "}") { if (depth > 0) --depth; }
            ++i;
        }
    }

    std::size_t parse_policy(std::size_t i) {
        Policy policy;
        policy.location = at(source, tokens[i]);
        if (i + 2 >= tokens.size() || tokens[i + 2].kind != RTok::identifier_token) bad("AI3141", "A recovery policy requires a name.", tokens[i]);
        policy.name = tokens[i + 2].text;
        if (i + 3 >= tokens.size() || tokens[i + 3].text != ":") bad("AI3141", "A recovery policy requires a colon.", tokens[i + 2]);
        policy.block_start = tokens[i].offset;
        if (policies.contains(policy.name)) bad("AI3141", "A recovery policy is declared twice.", tokens[i + 2]);
        std::size_t k = i + 3;
        while (k < tokens.size()) {
            if (declaration_ahead(k)) break;
            const RTok& token = tokens[k];
            if (token.kind != RTok::identifier_token) { ++k; continue; }
            if (token.text == "attempts") {
                if (policy.seen_attempts) bad("AI3141", "A recovery policy repeats attempts.", token);
                if (k + 1 >= tokens.size() || !is_number(tokens[k + 1])) bad("AI3141", "attempts requires an integer constant.", token);
                policy.seen_attempts = true; policy.attempts = number(tokens[k + 1]); k += 2; continue;
            }
            if (token.text == "deadline" || token.text == "attemptLimit") {
                bool deadline = token.text == "deadline";
                bool& seen = deadline ? policy.seen_deadline : policy.seen_limit;
                if (seen) bad("AI3141", "A recovery policy repeats " + token.text + ".", token);
                if (k + 2 >= tokens.size() || !is_number(tokens[k + 1]) || tokens[k + 2].text != "ms") bad("AI3141", token.text + " requires a millisecond constant such as 2000ms.", token);
                seen = true;
                (deadline ? policy.deadline : policy.attempt_limit) = number(tokens[k + 1]);
                k += 3; continue;
            }
            if (token.text == "backoff") {
                if (policy.seen_backoff) bad("AI3141", "A recovery policy repeats backoff.", token);
                if (k + 5 >= tokens.size() || !is_number(tokens[k + 1]) || tokens[k + 2].text != "ms" || tokens[k + 3].text != ".." || !is_number(tokens[k + 4]) || tokens[k + 5].text != "ms") bad("AI3141", "backoff requires two millisecond constants such as 50ms..500ms.", token);
                policy.seen_backoff = true; policy.backoff_min = number(tokens[k + 1]); policy.backoff_max = number(tokens[k + 4]);
                k += 6; continue;
            }
            if (token.text == "retry") {
                if (policy.seen_retry) bad("AI3141", "A recovery policy repeats retry.", token);
                if (k + 2 >= tokens.size() || tokens[k + 1].text != "on" || tokens[k + 2].kind != RTok::identifier_token) bad("AI3141", "retry requires an `on` list of error cases.", token);
                policy.seen_retry = true; k += 2;
                for (;;) {
                    if (k >= tokens.size() || tokens[k].kind != RTok::identifier_token) bad("AI3141", "Expected an error case name in retry on.", token);
                    policy.retry.push_back(tokens[k].text); ++k;
                    if (k < tokens.size() && tokens[k].text == ",") { ++k; continue; }
                    break;
                }
                continue;
            }
            if (token.text == "replay") {
                if (policy.seen_replay) bad("AI3141", "A recovery policy repeats replay.", token);
                if (k + 1 >= tokens.size() || (tokens[k + 1].text != "safe" && tokens[k + 1].text != "none")) bad("AI3141", "replay must be safe or none.", token);
                policy.seen_replay = true; policy.replay_safe = tokens[k + 1].text == "safe"; k += 2; continue;
            }
            bad("AI3141", "Unknown recovery policy setting `" + token.text + "`.", token);
        }
        policy.block_end = k < tokens.size() ? tokens[k].offset : source.size();
        if (!policy.seen_attempts) policy.attempts = 1;
        if (!policy.seen_deadline) bad("AI3141", "A recovery policy requires a deadline.", tokens[i + 2]);
        if (!policy.seen_limit) policy.attempt_limit = policy.deadline;
        if (policy.attempts < 1 || policy.attempts > 16) bad("AI3141", "attempts must be between 1 and 16.", tokens[i + 2]);
        if (policy.deadline < 0) bad("AI3141", "deadline must be non-negative.", tokens[i + 2]);
        if (policy.attempt_limit < 0 || policy.attempt_limit > policy.deadline) bad("AI3141", "attemptLimit must not exceed deadline.", tokens[i + 2]);
        if (policy.backoff_min < 0 || policy.backoff_max < policy.backoff_min) bad("AI3141", "backoff requires min <= max.", tokens[i + 2]);
        if (!policy.replay_safe && policy.attempts > 1) bad("AI3141", "replay none forbids more than one attempt.", tokens[i + 2]);
        policies.emplace(policy.name, std::move(policy));
        return k;
    }

    std::size_t parse_type(std::size_t i) {
        std::size_t k = i + 1;
        if (k >= tokens.size() || tokens[k].kind != RTok::identifier_token) return k;
        const std::string name = tokens[k].text;
        ++k;
        Union definition;
        if (k < tokens.size() && tokens[k].text == "<") { definition.generic = true; k = skip_balanced(k, '<', '>'); }
        if (k >= tokens.size() || tokens[k].text != "=") { return k; }
        ++k;
        if (k < tokens.size() && tokens[k].text == "{") { definition.product = true; k = skip_balanced(k, '{', '}'); }
        else {
            while (k < tokens.size()) {
                if (tokens[k].kind != RTok::identifier_token) break;
                if (declaration_ahead(k)) break;
                Case item; item.name = tokens[k].text; ++k;
                item.payload_empty = true;
                if (k < tokens.size() && tokens[k].text == "(") {
                    item.payload_empty = false;
                    std::size_t begin = k + 1;
                    k = skip_balanced(k, '(', ')');
                    std::string payload;
                    for (std::size_t p = begin; p + 1 < k; ++p) payload += tokens[p].text;
                    bool scalar_payload = true;
                    static const std::set<std::string> copyable = {
                        "int", "bool", "char", "byte", "sbyte", "short", "ushort", "long", "ulong", "uint",
                        "float", "double", "nint", "nuint", "string", "int32", "int64", "u8", "i8", "u16", "i16"
                    };
                    for (std::size_t p = begin; p + 1 < k; ++p) if (tokens[p].kind == RTok::identifier_token && !copyable.contains(tokens[p].text)) scalar_payload = false;
                    item.owning = !payload.empty() && !scalar_payload;
                }
                definition.cases.push_back(std::move(item));
                if (k < tokens.size() && tokens[k].text == "|") { ++k; continue; }
                break;
            }
            definition.known = !definition.cases.empty();
        }
        unions[name] = std::move(definition);
        return k;
    }

    std::size_t parse_function(std::size_t i) {
        if (i + 1 >= tokens.size() || tokens[i + 1].kind != RTok::identifier_token) return i + 1;
        Function function;
        function.name = tokens[i + 1].text;
        function.name_offset = tokens[i + 1].offset;
        function.name_length = tokens[i + 1].length;
        function.location = at(source, tokens[i + 1]);
        std::size_t k = i + 2;
        if (k >= tokens.size() || tokens[k].text != "(") return k;
        const std::size_t open = k;
        k = skip_balanced(k, '(', ')');
        if (k == tokens.size()) return k;
        const std::size_t close = k - 1;
        function.parameters_text = std::string(trim(source.substr(tokens[open].offset + 1, tokens[close].offset - tokens[open].offset - 1)));
        if (!function.parameters_text.empty()) {
            for (const auto& text : split_arguments(function.parameters_text)) {
                Parameter parameter; parameter.text = text;
                std::size_t colon = std::string::npos; int angles = 0, parens = 0;
                for (std::size_t p = 0; p < text.size(); ++p) {
                    const char c = text[p];
                    if (c == '<') ++angles; else if (c == '>') { if (angles > 0) --angles; }
                    else if (c == '(') ++parens; else if (c == ')') { if (parens > 0) --parens; }
                    else if (c == ':' && angles == 0 && parens == 0) { colon = p; break; }
                }
                if (colon == std::string::npos) continue;
                std::string head = std::string(trim(std::string_view(text).substr(0, colon)));
                for (char c : head) if (!word_char(c)) { function.unsupported_parameters = true; break; }
                if (head.empty()) function.unsupported_parameters = true;
                parameter.name = head;
                if (head == "out" || head == "params" || head == "scoped" || head == "in" || head == "readonly") function.unsupported_parameters = true;
                function.parameters.push_back(std::move(parameter));
            }
            function.argument_names = join([&]() { std::vector<std::string> names; for (const auto& parameter : function.parameters) names.push_back(parameter.name); return names; }());
        }
        if (k < tokens.size() && tokens[k].text == "->") {
            ++k;
            const std::size_t begin = tokens[k].offset;
            std::size_t end = begin;
            while (k < tokens.size()) {
                if (tokens[k].kind == RTok::identifier_token && (tokens[k].text == "ensures" || tokens[k].text == "recovers")) break;
                if (tokens[k].text == "{") break;
                end = tokens[k].offset + tokens[k].length; ++k;
            }
            function.returns = std::string(trim(source.substr(begin, end - begin)));
        }
        if (k < tokens.size() && tokens[k].text == "recovers") {
            function.recovers_start = tokens[k].offset;
            if (k + 1 >= tokens.size() || tokens[k + 1].kind != RTok::identifier_token) bad("AI3142", "recovers requires a declared recovery policy name.", tokens[k]);
            function.policy = tokens[k + 1].text;
            function.recovers_end = tokens[k + 1].offset + tokens[k + 1].length;
            k += 2;
            if (k < tokens.size() && tokens[k].text == "ensures") function.has_ensures = true;
        }
        while (k < tokens.size() && tokens[k].text != "{") ++k;
        if (k >= tokens.size()) return k;
        function.body_open = k;
        k = skip_balanced(k, '{', '}');
        function.body_close = k > 0 ? k - 1 : k;
        // Recursion would call the wrapper from the attempt with the attempt's
        // declared error type; v1 rejects it instead of guessing the intent.
        for (std::size_t p = i + 2; p + 1 < k; ++p) {
            if (tokens[p].kind == RTok::identifier_token && tokens[p].text == function.name && tokens[p + 1].text == "(") function.recursive = true;
        }
        if (!function.policy.empty()) functions.push_back(std::move(function));
        return k;
    }

    void validate() {
        for (const auto& function : functions) {
            const RTok name_token{RTok::identifier_token, function.name, function.name_offset, function.name_length};
            if (!unwinding) bad("AI3142", "recovers requires the module fault mode `faults unwind` so cancellation and deadline cleanup run.", name_token);
            auto policy = policies.find(function.policy);
            if (policy == policies.end()) bad("AI3143", "recovers names an undeclared recovery policy `" + function.policy + "`.", name_token);
            std::string first, second;
            if (!result_type(function.returns, first, second)) bad("AI3144", "recovers requires a function declared to return Result<T, E> with a closed error union.", name_token);
            auto error = unions.find(second);
            if (error == unions.end() || !error->second.known || error->second.generic || error->second.product) bad("AI3144", "recovers requires a declared closed union error type; `" + second + "` is not one.", name_token);
            if (function.recursive) bad("AI3146", "A recovers function cannot call itself; recursion would cross the boundary with the wrong declared type.", name_token);
            if (function.unsupported_parameters) bad("AI3146", "recovers does not yet support out, params, scoped, in or readonly parameters.", name_token);
            if (first == "unit") bad("AI3146", "recovers does not yet support a unit success payload.", name_token);
            if (seed_case(second).empty()) bad("AI3145", "A recovery boundary needs a payload-free case of `" + second + "` to seed the exhausted outcome.", name_token);
            const bool retries = !policy->second.retry.empty();
            if (retries) {
                for (const auto& case_name : policy->second.retry) {
                    bool found = false;
                    for (const auto& item : error->second.cases) if (item.name == case_name) { found = true; break; }
                    if (!found) bad("AI3145", "retry names `" + case_name + "`, which is not a case of `" + second + "`.", name_token);
                }
                for (const auto& item : error->second.cases) if (item.owning) bad("AI3145", "A retried error type must be non-owning: case `" + item.name + "` owns a payload.", name_token);
                if (policy->second.attempts > 1 && !policy->second.replay_safe) bad("AI3145", "Replay must be declared safe before retrying.", name_token);
            }
        }
        for (const auto& token : tokens) if (token.kind == RTok::identifier_token && token.text.starts_with("__aisharp_recovery_")) bad("AI3148", "The reserved recovery identifier prefix is already in use.", token);
    }

    static bool result_type(const std::string& text, std::string& success, std::string& error) {
        const std::string_view view = trim(text);
        const std::size_t open = view.find('<');
        if (open == std::string_view::npos || trim(view.substr(0, open)) != "Result") return false;
        const std::size_t close = view.rfind('>');
        if (close == std::string_view::npos || close <= open || !trim(view.substr(close + 1)).empty()) return false;
        const std::string inner(view.substr(open + 1, close - open - 1));
        std::vector<std::string> parts = split_arguments(inner);
        if (parts.size() != 2 || parts[0].empty() || parts[1].empty()) return false;
        success = parts[0]; error = parts[1];
        return true;
    }

    std::string attempt_name(const Function& function, std::size_t index) const {
        return "__aisharp_recovery_attempt_" + std::to_string(index) + "_" + function.name;
    }
    std::string helper_name(const char* role, const Function& function, std::size_t index) const {
        return "__aisharp_recovery_" + std::string(role) + "_" + std::to_string(index) + "_" + function.name;
    }

    std::string seed_case(const std::string& error) const {
        for (const auto& item : unions.at(error).cases) if (item.payload_empty) return item.name;
        return std::string();
    }

    std::string append_block(const Function& function, std::size_t index) {
        const auto& policy = policies.at(function.policy);
        std::string success, error;
        result_type(function.returns, success, error);
        const std::string outcome = "Result<" + success + ", RecoveryFailure<" + error + ">>";
        const std::string option = "Option<" + success + ">";
        const std::string attempt = attempt_name(function, index);
        const std::string keep_ok = helper_name("keep_ok", function, index);
        const std::string keep_error = helper_name("keep_error", function, index);
        const std::string retry = helper_name("retry", function, index);
        const std::string body = helper_name("body", function, index);
        const std::string arguments = function.argument_names;
        const std::string call_arguments = arguments.empty() ? "" : arguments + ", ";
        const std::string parameter_prefix = function.parameters_text.empty() ? "" : function.parameters_text + ", ";
        const bool retries = !policy.retry.empty();
        const std::string seed = seed_case(error);
        std::string text;
        text += "\nfn " + keep_ok + "(v:" + success + ",out:&mut " + option + ")->unit\nensures true\n{*out=Some(v)}\n";
        text += "fn " + keep_error + "(e:" + error + ",last:&mut " + error + ")->unit\nensures true\n{*last=e}\n";
        if (retries) {
            std::string arms;
            for (std::size_t c = 0; c < policy.retry.size(); ++c) { if (c) arms += ","; arms += policy.retry[c] + "=>true"; }
            const bool exhaustive = policy.retry.size() == unions.at(error).cases.size();
            if (!exhaustive) arms += ",_=>false";
            text += "fn " + retry + "(e:" + error + ")->bool\nensures true\n{return match e {" + arms + "}}\n";
        }
        text += "fn " + body + "(" + parameter_prefix + "deadline:ulong)->" + outcome + "\nensures true\n{\n";
        text += "var __aisharp_recovery_tries:int=0\n";
        text += "var __aisharp_recovery_out:" + option + "=None\n";
        text += "var __aisharp_recovery_last:" + error + "=" + seed + "\n";
        text += "while(true)\n{\n";
        text += "__aisharp_recovery_tries=__aisharp_recovery_tries+1\n";
        text += "ai_check_cancelled()\nai_check_deadline(deadline)\n";
        text += "let __aisharp_recovery_result:" + function.returns + "=" + attempt + "(" + arguments + ")\n";
        text += "ai_check_cancelled()\nai_check_deadline(deadline)\n";
        text += "match __aisharp_recovery_result {Ok(__aisharp_recovery_value)=>" + keep_ok + "(__aisharp_recovery_value,&mut __aisharp_recovery_out),Err(__aisharp_recovery_cause)=>" + keep_error + "(__aisharp_recovery_cause,&mut __aisharp_recovery_last)}\n";
        text += "let __aisharp_recovery_done:bool=match &__aisharp_recovery_out {Some(__aisharp_recovery_value)=>true,None=>false}\n";
        text += "if(__aisharp_recovery_done){return match __aisharp_recovery_out {Some(__aisharp_recovery_value)=>Ok(__aisharp_recovery_value),None=>Err(Error(__aisharp_recovery_last))}}\n";
        if (retries) {
            text += "if(!" + retry + "(__aisharp_recovery_last)){return Err(Error(__aisharp_recovery_last))}\n";
            text += "if(__aisharp_recovery_tries>=" + std::to_string(policy.attempts) + "){return Err(Exhausted(__aisharp_recovery_tries,__aisharp_recovery_last))}\n";
            text += "ai_check_cancelled()\nai_check_deadline(deadline)\n";
            std::string backoff;
            if (policy.backoff_max > policy.backoff_min && policy.attempts > 1) {
                const long long step = (policy.backoff_max - policy.backoff_min) / (policy.attempts - 1);
                backoff = std::to_string(policy.backoff_min) + "+(__aisharp_recovery_tries-1)*" + std::to_string(step);
            } else backoff = std::to_string(policy.backoff_min);
            text += "ai_sleep_ms(" + backoff + ")\nai_check_cancelled()\nai_check_deadline(deadline)\n";
        } else {
            text += "return Err(Error(__aisharp_recovery_last))\n";
        }
        text += "}\nreturn Err(Error(" + seed + "))\n}\n";
        text += "fn " + function.name + "(" + function.parameters_text + ")->" + outcome + "\nensures true\n{\nreturn " + body + "(" + call_arguments + "ai_now_ms()+" + std::to_string(policy.deadline) + ")\n}\n";
        return text;
    }

    std::string apply(std::vector<std::size_t>& mapping) {
        std::vector<Edit> edits;
        for (const auto& policy : policies) {
            Edit edit; edit.start = policy.second.block_start; edit.end = policy.second.block_end; edit.blank = true;
            edits.push_back(std::move(edit));
        }
        for (std::size_t index = 0; index < functions.size(); ++index) {
            const auto& function = functions[index];
            {
                Edit edit; edit.start = function.name_offset; edit.end = function.name_offset + function.name_length;
                edit.text = attempt_name(function, index);
                edits.push_back(std::move(edit));
            }
            {
                Edit edit; edit.start = function.recovers_start; edit.end = function.recovers_end;
                if (function.has_ensures) edit.blank = true; else edit.text = "ensures true";
                edits.push_back(std::move(edit));
            }
        }
        // Cancellation surface inside a recovers body: `cancel cancellation` is a
        // statement and `cancellation.cancelled` is readable.
        for (std::size_t index = 0; index < functions.size(); ++index) {
            const auto& function = functions[index];
            for (std::size_t p = function.body_open + 1; p + 1 < function.body_close && p + 1 < tokens.size(); ++p) {
                if (tokens[p].kind == RTok::identifier_token && tokens[p].text == "cancel" && tokens[p + 1].text == "cancellation") {
                    Edit edit; edit.start = tokens[p].offset; edit.end = tokens[p + 1].offset + tokens[p + 1].length; edit.text = "ai_cancel_request()";
                    edits.push_back(std::move(edit));
                }
                if (tokens[p].kind == RTok::identifier_token && tokens[p].text == "cancellation" && tokens[p + 1].text == "." && p + 2 < tokens.size() && tokens[p + 2].text == "cancelled") {
                    Edit edit; edit.start = tokens[p].offset; edit.end = tokens[p + 2].offset + tokens[p + 2].length; edit.text = "ai_cancelled()";
                    edits.push_back(std::move(edit));
                }
            }
        }
        // Synthesized helper functions go after the user module in one edit so
        // insertion order cannot depend on the sort implementation.
        std::string appended;
        for (std::size_t index = 0; index < functions.size(); ++index) appended += append_block(functions[index], index);
        if (!appended.empty()) { Edit edit; edit.start = edit.end = source.size(); edit.text = std::move(appended); edits.push_back(std::move(edit)); }
        std::string injected;
        if (!unions.contains("RecoveryFailure") && !has_identifier("RecoveryFailure")) injected += "type RecoveryFailure<E>=Error(E)|Exhausted(int,E)|Uncertain(string,int,E)\n";
        if (!extern_names.contains("ai_now_ms")) injected += "extern fn ai_now_ms()->ulong;\n";
        if (!extern_names.contains("ai_check_cancelled")) injected += "extern fn ai_check_cancelled()->unit;\n";
        if (!extern_names.contains("ai_check_deadline")) injected += "extern fn ai_check_deadline(deadline_ms:ulong)->unit;\n";
        if (!extern_names.contains("ai_sleep_ms")) injected += "extern fn ai_sleep_ms(milliseconds:int)->unit;\n";
        if (!extern_names.contains("ai_cancel_request")) injected += "extern fn ai_cancel_request()->unit;\n";
        if (!extern_names.contains("ai_cancelled")) injected += "extern fn ai_cancelled()->bool;\n";
        {
            Edit edit; edit.start = edit.end = insert_at; edit.text = injected;
            edits.push_back(std::move(edit));
        }
        std::stable_sort(edits.begin(), edits.end(), [](const Edit& a, const Edit& b) { if (a.start != b.start) return a.start < b.start; return a.end < b.end; });
        std::string result;
        result.reserve(source.size() + 2048);
        mapping.clear();
        std::size_t cursor = 0;
        auto copy=[&](std::size_t from,std::size_t to){
            for(std::size_t i=from;i<to && i<source.size();++i){result.push_back(source[i]);mapping.push_back(i);}
        };
        auto generated=[&](const std::string& value,std::size_t anchor){
            for(char c:value){result.push_back(c);mapping.push_back(anchor);}
        };
        for (const auto& edit : edits) {
            if (edit.start > source.size() || edit.end > source.size() || edit.start > edit.end || edit.start < cursor) fail("AI3140", "Recovery lowering produced an invalid source edit.", {});
            copy(cursor, edit.start);
            if (edit.blank) {
                for(std::size_t i=edit.start;i<edit.end;++i){result.push_back(source[i]=='\n' ? '\n' : ' ');mapping.push_back(i);}
            } else {
                generated(edit.text, edit.start);
            }
            cursor=edit.end;
        }
        copy(cursor, source.size());
        mapping.push_back(source.size());
        return result;
    }

    bool has_identifier(const std::string& name) const { return identifiers.contains(name); }
};

}  // namespace recovery_lowering

struct RecoveryLowering {
    std::string text;
    bool active = false;
    std::vector<std::size_t> map;  // lowered byte -> original byte
};

inline std::size_t utf16_units_of_prefix(std::string_view text,std::size_t bytes) {
    std::size_t units=0;
    for(std::size_t i=0;i<bytes && i<text.size();) {
        unsigned char c=static_cast<unsigned char>(text[i]);
        std::size_t count=1; std::uint32_t cp=c;
        if(c>=0xc2 && c<=0xdf){count=2;cp=c&0x1fu;}
        else if(c>=0xe0 && c<=0xef){count=3;cp=c&0x0fu;}
        else if(c>=0xf0 && c<=0xf4){count=4;cp=c&0x07u;}
        for(std::size_t k=1;k<count && i+k<text.size();++k)cp=(cp<<6u)|(static_cast<unsigned char>(text[i+k])&0x3fu);
        if(i+count>text.size())count=1;
        units += cp>0xffff ? 2 : 1;
        i+=count;
    }
    return units;
}

inline std::size_t byte_of_utf16_offset(std::string_view text,std::size_t offset) {
    std::size_t units=0;
    for(std::size_t i=0;i<text.size();) {
        unsigned char c=static_cast<unsigned char>(text[i]);
        std::size_t count=1; std::uint32_t cp=c;
        if(c>=0xc2 && c<=0xdf){count=2;cp=c&0x1fu;}
        else if(c>=0xe0 && c<=0xef){count=3;cp=c&0x0fu;}
        else if(c>=0xf0 && c<=0xf4){count=4;cp=c&0x07u;}
        for(std::size_t k=1;k<count && i+k<text.size();++k)cp=(cp<<6u)|(static_cast<unsigned char>(text[i+k])&0x3fu);
        if(i+count>text.size())count=1;
        std::size_t width=cp>0xffff ? 2 : 1;
        if(units+width>offset)return i;
        units+=width;
        if(units==offset)return i+count;
        i+=count;
    }
    return text.size();
}

inline void line_column(std::string_view text,std::size_t bytes,std::size_t& line,std::size_t& column) {
    line=1;column=1;
    for(std::size_t i=0;i<bytes && i<text.size();) {
        unsigned char c=static_cast<unsigned char>(text[i]);
        std::size_t count=1; std::uint32_t cp=c;
        if(c>=0xc2 && c<=0xdf){count=2;cp=c&0x1fu;}
        else if(c>=0xe0 && c<=0xef){count=3;cp=c&0x0fu;}
        else if(c>=0xf0 && c<=0xf4){count=4;cp=c&0x07u;}
        for(std::size_t k=1;k<count && i+k<text.size();++k)cp=(cp<<6u)|(static_cast<unsigned char>(text[i+k])&0x3fu);
        if(i+count>text.size())count=1;
        if(cp=='\n'){++line;column=1;}else column += cp>0xffff ? 2 : 1;
        i+=count;
    }
}

inline Location remap_location(const RecoveryLowering& lowering,std::string_view original,const Location& at) {
    if(!lowering.active || at.origin!=SourceOrigin::input || lowering.map.empty())return at;
    const std::size_t out_start=byte_of_utf16_offset(lowering.text,at.offset);
    const std::size_t out_end=byte_of_utf16_offset(lowering.text,at.offset+at.length);
    const std::size_t original_start=lowering.map[std::min(out_start,lowering.map.size()-1)];
    const std::size_t original_end=lowering.map[std::min(out_end,lowering.map.size()-1)];
    Location mapped=at;
    mapped.offset=utf16_units_of_prefix(original,original_start);
    const std::size_t mapped_end=utf16_units_of_prefix(original,original_end);
    mapped.length=mapped_end>mapped.offset ? mapped_end-mapped.offset : 0;
    line_column(original,original_start,mapped.line,mapped.column);
    return mapped;
}

inline void remap_diagnostic(const RecoveryLowering& lowering,std::string_view original,Diagnostic& diagnostic) {
    diagnostic.location=remap_location(lowering,original,diagnostic.location);
    for(auto& related:diagnostic.related)related.location=remap_location(lowering,original,related.location);
}

inline void remap_recovery_diagnostic(const RecoveryLowering& lowering,std::string_view original,Diagnostic& diagnostic) {
    remap_diagnostic(lowering,original,diagnostic);
}

RecoveryLowering rewrite_recovery(std::string_view source) {
    RecoveryLowering lowering;
    lowering.text = recovery_lowering::Rewriter(source, recovery_lowering::lex(source)).run(lowering.active,lowering.map);
    return lowering;
}
