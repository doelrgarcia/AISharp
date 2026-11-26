"""Explicit developer regeneration; native build/runtime do not require Python."""
from pathlib import Path
import hashlib

core = Path(__file__).resolve().parent
source = (core / 'outcomes.aisharp').read_bytes()
assert b'\r' not in source and not source.startswith(b'\xef\xbb\xbf')
text = source.decode('utf-8')
assert ')AISharpCore"' not in text
template = (core / 'native_core_outcomes.hpp.in').read_text(encoding='utf-8')
generated = template.replace('@AISHARP_CORE_HASH@', hashlib.sha256(source).hexdigest())
generated = generated.replace('@AISHARP_CORE_TEXT@', text)
target = core.parent / 'native_core_outcomes.hpp'
target.write_text(generated, encoding='utf-8', newline='\n')
print('Generated', target)
