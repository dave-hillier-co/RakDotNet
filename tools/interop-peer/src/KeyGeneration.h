#pragma once

// cat::EasyHandshake::GenerateServerKey for the security commands. It lives in its own translation unit, compiled as
// RakNet is (gnu++98), because cat's headers are not valid C++17.
namespace interop
{
    // cat::EasyHandshake::PUBLIC_KEY_BYTES and PRIVATE_KEY_BYTES (EasyHandshake.hpp:211-212).
    const int kPublicKeyBytes = 64;
    const int kPrivateKeyBytes = 32;

    // Whether RakNet was compiled with LIBCAT_SECURITY=1.
    bool SecurityCompiledIn();

    // Fills a new server (or client identity) key pair. False when built without LIBCAT_SECURITY or when cat fails.
    bool GenerateServerKey(unsigned char* publicKey, unsigned char* privateKey);
}
