// Compiled with RakNet's language settings, against the RakNet build it links (LIBCAT_SECURITY 0 or 1).
#include "KeyGeneration.h"

#include "NativeFeatureIncludes.h"

#if LIBCAT_SECURITY == 1
#include "SecureHandshake.h"

typedef char PublicKeyBytesMatch[cat::EasyHandshake::PUBLIC_KEY_BYTES == interop::kPublicKeyBytes ? 1 : -1];
typedef char PrivateKeyBytesMatch[cat::EasyHandshake::PRIVATE_KEY_BYTES == interop::kPrivateKeyBytes ? 1 : -1];
#endif

bool interop::SecurityCompiledIn()
{
    return LIBCAT_SECURITY == 1;
}

bool interop::GenerateServerKey(unsigned char* publicKey, unsigned char* privateKey)
{
#if LIBCAT_SECURITY == 1
    cat::EasyHandshake handshake;
    return handshake.GenerateServerKey(publicKey, privateKey);
#else
    (void)publicKey;
    (void)privateKey;
    return false;
#endif
}
