#pragma once

// RakNet's DynDNS and Rackspace classes as ServiceRedirects.cpp compiles them again, renamed InteropDynDNS and
// InteropRackspace so they cannot be confused with the copies in the RakNet library.
#define DynDNS InteropDynDNS
#define Rackspace InteropRackspace
#include "DynDNS.h"
#include "Rackspace.h"
#undef DynDNS
#undef Rackspace
