#pragma once

#include <cstddef>
#include <string>
#include <vector>

namespace interop
{
    // Lowercase hex with no separators, as docs/interop-protocol.md requires.
    std::string ToHex(const unsigned char* data, std::size_t length);

    // Accepts upper or lower case. Returns false for odd length or non-hex characters.
    bool TryFromHex(const std::string& text, std::vector<unsigned char>& bytes);
}
