#pragma once

#include "RakNetTypes.h"
#include "RakPeerInterface.h"

#include <nlohmann/json.hpp>

#include <stdexcept>
#include <string>
#include <vector>

namespace interop
{
    // A security command refused its arguments; the peer replies ok:false with the message.
    struct SecurityCommandError : std::runtime_error
    {
        using std::runtime_error::runtime_error;
    };

    // Handles the security commands of docs/interop-protocol.md (generateKeys, initSecurity, disableSecurity,
    // addSecurityException, removeSecurityException, securityException, clientPublicKey) and the other functions
    // Samples/Tests/SecurityFunctionsTest.cpp exercises (setIncomingPassword, incomingPassword, unban, clearBans,
    // isBanned). Returns false for any other command.
    bool DispatchSecurityCommand(RakNet::RakPeerInterface* peer, const std::string& cmd, const nlohmann::json& command, nlohmann::ordered_json& result);

    // RakPeer::Connect's PublicKey argument, read from the optional security fields of the connect command, with the
    // key bytes it points at.
    class ConnectKey
    {
    public:
        explicit ConnectKey(const nlohmann::json& command);

        // Null for PKM_INSECURE_CONNECTION, as RakPeer allows (RakPeerInterface.h:154).
        RakNet::PublicKey* Get();

    private:
        RakNet::PublicKey key_{};
        std::vector<char> serverPublicKey_;
        std::vector<char> clientPublicKey_;
        std::vector<char> clientPrivateKey_;
    };
}
