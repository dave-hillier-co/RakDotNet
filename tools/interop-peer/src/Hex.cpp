#include "Hex.h"

namespace interop
{
    namespace
    {
        int NibbleValue(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'a' && c <= 'f')
                return c - 'a' + 10;
            if (c >= 'A' && c <= 'F')
                return c - 'A' + 10;
            return -1;
        }
    }

    std::string ToHex(const unsigned char* data, std::size_t length)
    {
        static const char digits[] = "0123456789abcdef";
        std::string text;
        text.reserve(length * 2);
        for (std::size_t i = 0; i < length; ++i)
        {
            text.push_back(digits[data[i] >> 4]);
            text.push_back(digits[data[i] & 0x0f]);
        }
        return text;
    }

    bool TryFromHex(const std::string& text, std::vector<unsigned char>& bytes)
    {
        bytes.clear();
        if (text.size() % 2 != 0)
            return false;
        bytes.reserve(text.size() / 2);
        for (std::size_t i = 0; i < text.size(); i += 2)
        {
            int high = NibbleValue(text[i]);
            int low = NibbleValue(text[i + 1]);
            if (high < 0 || low < 0)
            {
                bytes.clear();
                return false;
            }
            bytes.push_back(static_cast<unsigned char>((high << 4) | low));
        }
        return true;
    }
}
