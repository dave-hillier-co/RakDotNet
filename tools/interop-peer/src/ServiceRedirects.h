#pragma once

#include <string>

namespace interop
{
    // Where the copies of RakNet's DynDNS and Rackspace in ServiceRedirects.cpp connect. They connect to fixed hosts
    // (checkip.dyndns.org and members.dyndns.org on port 80) or a fixed port (443), so each TCPInterface::Connect they
    // make goes through RedirectedHost and RedirectedPort: a host given a redirect goes to its host and port instead,
    // and any other host is left as it is.
    void SetServiceRedirect(const std::string& host, const std::string& toHost, unsigned short toPort);
    void ClearServiceRedirects();
    const char* RedirectedHost(const char* host);
    unsigned short RedirectedPort(const char* host, unsigned short port);
}
