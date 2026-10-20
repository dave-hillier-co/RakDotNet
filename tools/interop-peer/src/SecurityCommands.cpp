#include "SecurityCommands.h"

#include "Hex.h"
#include "KeyGeneration.h"

#include <cstdint>
#include <cstdlib>

using nlohmann::json;
using nlohmann::ordered_json;

namespace
{
    std::vector<unsigned char> Hex(const json& command, const char* field, bool required, std::size_t length)
    {
        auto it = command.find(field);
        if (it == command.end() || it->is_null())
        {
            if (required)
                throw interop::SecurityCommandError(std::string("missing field '") + field + "'");
            return {};
        }
        std::vector<unsigned char> bytes;
        if (!it->is_string() || !interop::TryFromHex(it->get<std::string>(), bytes))
            throw interop::SecurityCommandError(std::string("field '") + field + "' is not valid hex");
        if (length != 0 && bytes.size() != length)
            throw interop::SecurityCommandError(std::string("field '") + field + "' must be " + std::to_string(length) + " bytes");
        return bytes;
    }

    std::string RequiredString(const json& command, const char* field)
    {
        auto it = command.find(field);
        if (it == command.end() || !it->is_string())
            throw interop::SecurityCommandError(std::string("missing field '") + field + "'");
        return it->get<std::string>();
    }

    RakNet::RakNetGUID Guid(const json& command, const char* field)
    {
        const std::string text = RequiredString(command, field);
        if (text.empty() || text.find_first_not_of("0123456789") != std::string::npos)
            throw interop::SecurityCommandError(std::string("field '") + field + "' must be a decimal GUID string");
        return RakNet::RakNetGUID(static_cast<std::uint64_t>(std::strtoull(text.c_str(), nullptr, 10)));
    }

    RakNet::PublicKeyMode ParseMode(const std::string& name)
    {
        if (name == "INSECURE_CONNECTION")
            return RakNet::PKM_INSECURE_CONNECTION;
        if (name == "ACCEPT_ANY_PUBLIC_KEY")
            return RakNet::PKM_ACCEPT_ANY_PUBLIC_KEY;
        if (name == "USE_KNOWN_PUBLIC_KEY")
            return RakNet::PKM_USE_KNOWN_PUBLIC_KEY;
        if (name == "USE_TWO_WAY_AUTHENTICATION")
            return RakNet::PKM_USE_TWO_WAY_AUTHENTICATION;
        throw interop::SecurityCommandError("unknown publicKeyMode '" + name + "'");
    }

    std::vector<char> Chars(const std::vector<unsigned char>& bytes) { return std::vector<char>(bytes.begin(), bytes.end()); }
}

bool interop::DispatchSecurityCommand(RakNet::RakPeerInterface* peer, const std::string& cmd, const json& command, ordered_json& result)
{
    if (cmd == "generateKeys")
    {
        // EasyHandshake::GenerateServerKey, as the Encryption sample makes server and client keys.
        unsigned char publicKey[kPublicKeyBytes];
        unsigned char privateKey[kPrivateKeyBytes];
        if (!GenerateServerKey(publicKey, privateKey))
            throw SecurityCommandError("GenerateServerKey failed: this build has LIBCAT_SECURITY off");
        result["publicKey"] = ToHex(publicKey, sizeof(publicKey));
        result["privateKey"] = ToHex(privateKey, sizeof(privateKey));
    }
    else if (cmd == "initSecurity")
    {
        // InitializeSecurity succeeds only before startup (RakPeer.cpp:765-766) and with LIBCAT_SECURITY.
        std::vector<char> publicKey = Chars(Hex(command, "publicKey", true, kPublicKeyBytes));
        std::vector<char> privateKey = Chars(Hex(command, "privateKey", true, kPrivateKeyBytes));
        auto require = command.find("requireClientKey");
        bool requireClientKey = require != command.end() && require->is_boolean() && require->get<bool>();
        result["initialized"] = peer->InitializeSecurity(publicKey.data(), privateKey.data(), requireClientKey);
    }
    else if (cmd == "disableSecurity")
    {
        peer->DisableSecurity();
    }
    else if (cmd == "addSecurityException")
    {
        peer->AddToSecurityExceptionList(RequiredString(command, "ip").c_str());
    }
    else if (cmd == "removeSecurityException")
    {
        // Without ip, RemoveFromSecurityExceptionList(0) clears the list (RakPeer.cpp:860-865).
        auto it = command.find("ip");
        if (it == command.end() || it->is_null())
            peer->RemoveFromSecurityExceptionList(nullptr);
        else
            peer->RemoveFromSecurityExceptionList(RequiredString(command, "ip").c_str());
    }
    else if (cmd == "securityException")
    {
        result["listed"] = peer->IsInSecurityExceptionList(RequiredString(command, "ip").c_str());
    }
    else if (cmd == "setIncomingPassword")
    {
        std::vector<unsigned char> password = Hex(command, "data", false, 0);
        if (password.size() > 255)
            throw SecurityCommandError("the incoming password is at most 255 bytes");
        peer->SetIncomingPassword(password.empty() ? nullptr : reinterpret_cast<const char*>(password.data()), static_cast<int>(password.size()));
    }
    else if (cmd == "incomingPassword")
    {
        char password[256];
        int length = sizeof(password);
        peer->GetIncomingPassword(password, &length);
        result["data"] = ToHex(reinterpret_cast<const unsigned char*>(password), static_cast<std::size_t>(length));
    }
    else if (cmd == "unban")
    {
        peer->RemoveFromBanList(RequiredString(command, "ip").c_str());
    }
    else if (cmd == "clearBans")
    {
        peer->ClearBanList();
    }
    else if (cmd == "isBanned")
    {
        result["banned"] = peer->IsBanned(RequiredString(command, "ip").c_str());
    }
    else if (cmd == "clientPublicKey")
    {
        RakNet::SystemAddress address = peer->GetSystemAddressFromGuid(Guid(command, "target"));
        char publicKey[kPublicKeyBytes];
        if (address != RakNet::UNASSIGNED_SYSTEM_ADDRESS && peer->GetClientPublicKeyFromSystemAddress(address, publicKey))
            result["publicKey"] = ToHex(reinterpret_cast<const unsigned char*>(publicKey), sizeof(publicKey));
        else
            result["publicKey"] = nullptr;
    }
    else
    {
        return false;
    }
    return true;
}

interop::ConnectKey::ConnectKey(const json& command)
{
    auto mode = command.find("publicKeyMode");
    key_.publicKeyMode = mode == command.end() || mode->is_null() ? RakNet::PKM_INSECURE_CONNECTION : ParseMode(RequiredString(command, "publicKeyMode"));
    serverPublicKey_ = Chars(Hex(command, "serverPublicKey", false, kPublicKeyBytes));
    clientPublicKey_ = Chars(Hex(command, "clientPublicKey", false, kPublicKeyBytes));
    clientPrivateKey_ = Chars(Hex(command, "clientPrivateKey", false, kPrivateKeyBytes));
    // Missing keys stay null pointers, which GenerateConnectionRequestChallenge refuses (RakPeer.cpp:3214-3219, 3240-3241).
    key_.remoteServerPublicKey = serverPublicKey_.empty() ? nullptr : serverPublicKey_.data();
    key_.myPublicKey = clientPublicKey_.empty() ? nullptr : clientPublicKey_.data();
    key_.myPrivateKey = clientPrivateKey_.empty() ? nullptr : clientPrivateKey_.data();
}

RakNet::PublicKey* interop::ConnectKey::Get()
{
    return key_.publicKeyMode == RakNet::PKM_INSECURE_CONNECTION ? nullptr : &key_;
}
