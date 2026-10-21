#pragma once

#include "PluginCommands.h"

#include "RakNetTypes.h"

#include <nlohmann/json.hpp>

#include <cerrno>
#include <cstdint>
#include <cstdlib>
#include <string>

namespace interop::natfields
{
    // Reading the fields of the NAT and relaying commands; a missing or bad field is a PluginCommandError.

    template <typename T>
    T Optional(const nlohmann::json& command, const char* field, T fallback)
    {
        auto it = command.find(field);
        if (it == command.end() || it->is_null())
            return fallback;
        return it->get<T>();
    }

    template <typename T>
    T Required(const nlohmann::json& command, const char* field)
    {
        auto it = command.find(field);
        if (it == command.end() || it->is_null())
            throw PluginCommandError(std::string("missing field '") + field + "'");
        return it->get<T>();
    }

    inline bool Has(const nlohmann::json& command, const char* field)
    {
        auto it = command.find(field);
        return it != command.end() && !it->is_null();
    }

    inline long long Ranged(long long value, const char* field, long long minimum, long long maximum)
    {
        if (value < minimum || value > maximum)
            throw PluginCommandError(std::string("field '") + field + "' is out of range");
        return value;
    }

    inline unsigned short PortField(const nlohmann::json& command, const char* field)
    {
        return static_cast<unsigned short>(Ranged(Required<long long>(command, field), field, 0, 65535));
    }

    // An address given as host ("127.0.0.1") and port fields.
    inline RakNet::SystemAddress AddressFields(const nlohmann::json& command)
    {
        std::string host = Optional<std::string>(command, "host", "127.0.0.1");
        unsigned short port = PortField(command, "port");
        RakNet::SystemAddress address;
        if (!address.FromStringExplicitPort(host.c_str(), port))
            throw PluginCommandError("field 'host' is not an address");
        return address;
    }

    // An address given as one "a.b.c.d:port" field.
    inline RakNet::SystemAddress AddressField(const nlohmann::json& command, const char* field)
    {
        std::string text = Required<std::string>(command, field);
        RakNet::SystemAddress address;
        if (!address.FromString(text.c_str(), ':'))
            throw PluginCommandError(std::string("field '") + field + "' is not an address");
        return address;
    }

    inline RakNet::RakNetGUID GuidField(const nlohmann::json& command, const char* field)
    {
        const nlohmann::json value = Required<nlohmann::json>(command, field);
        if (value.is_number_unsigned())
            return RakNet::RakNetGUID(value.get<std::uint64_t>());
        if (!value.is_string())
            throw PluginCommandError(std::string("field '") + field + "' must be a decimal GUID string");
        const std::string text = value.get<std::string>();
        if (text.empty() || text.find_first_not_of("0123456789") != std::string::npos)
            throw PluginCommandError(std::string("field '") + field + "' must be a decimal GUID string");
        errno = 0;
        unsigned long long g = std::strtoull(text.c_str(), nullptr, 10);
        if (errno == ERANGE)
            throw PluginCommandError(std::string("field '") + field + "' is out of range");
        return RakNet::RakNetGUID(static_cast<std::uint64_t>(g));
    }

    inline std::string AddressText(const RakNet::SystemAddress& address)
    {
        char buffer[128];
        address.ToString(true, buffer, ':');
        return buffer;
    }

    template <typename T>
    void SetIfPresent(const nlohmann::json& command, const char* field, T& target, long long minimum, long long maximum)
    {
        auto it = command.find(field);
        if (it != command.end() && !it->is_null())
            target = static_cast<T>(Ranged(it->get<long long>(), field, minimum, maximum));
    }
}
