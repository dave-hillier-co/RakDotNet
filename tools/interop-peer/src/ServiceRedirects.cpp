#include "ServiceRedirects.h"

#include <map>
#include <mutex>
#include <utility>

namespace interop
{
    namespace
    {
        std::mutex& RedirectMutex()
        {
            static std::mutex mutex;
            return mutex;
        }

        std::map<std::string, std::pair<std::string, unsigned short>>& Redirects()
        {
            static std::map<std::string, std::pair<std::string, unsigned short>> redirects;
            return redirects;
        }
    }

    void SetServiceRedirect(const std::string& host, const std::string& toHost, unsigned short toPort)
    {
        std::lock_guard<std::mutex> lock(RedirectMutex());
        Redirects()[host] = std::make_pair(toHost, toPort);
    }

    void ClearServiceRedirects()
    {
        std::lock_guard<std::mutex> lock(RedirectMutex());
        Redirects().clear();
    }

    // Entries are never erased while the peer runs, so the returned pointer stays valid.
    const char* RedirectedHost(const char* host)
    {
        std::lock_guard<std::mutex> lock(RedirectMutex());
        auto it = Redirects().find(host);
        return it == Redirects().end() ? host : it->second.first.c_str();
    }

    unsigned short RedirectedPort(const char* host, unsigned short port)
    {
        std::lock_guard<std::mutex> lock(RedirectMutex());
        auto it = Redirects().find(host);
        return it == Redirects().end() ? port : it->second.second;
    }
}

// RakNet's DynDNS.cpp and Rackspace.cpp compiled unchanged, but with every TCPInterface::Connect they make passed
// through the redirects above, and their names changed so these copies are distinct from the library's. The headers
// they include come first, so the Connect macro reaches only the two sources. RakNet's source directory is a system
// include path, so its warnings stay quiet as they do in the library.
#include "Base64Encoder.h"
#include "GetTime.h"
#include "NativeFeatureIncludes.h"
#include "RakNetSocket2.h"
#include "RakString.h"
#include "TCPInterface.h"

#include "RedirectedServices.h"

#define Connect(host, port, ...) Connect(::interop::RedirectedHost(host), ::interop::RedirectedPort(host, port), __VA_ARGS__)

#define DynDNS InteropDynDNS
#define DynDnsResult InteropDynDnsResult
#define resultTable interopDynDnsResultTable
#include "DynDNS.cpp"
#undef resultTable
#undef DynDnsResult
#undef DynDNS

#define Rackspace InteropRackspace
#include "Rackspace.cpp"
#undef Rackspace

#undef Connect
