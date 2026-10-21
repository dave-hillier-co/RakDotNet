// Leaves every thread RakNet creates at the process's default scheduling priority.
//
// RakThread::Create (RakThread.cpp) passes its priority to pthread_attr_setschedparam, and RakNet's callers pass Windows
// thread priorities: 0, THREAD_PRIORITY_NORMAL and RakThread::Create's default, for UDPForwarder's thread
// (UDPForwarder.cpp:80) and the receive threads of NatTypeDetection's sockets (NatTypeDetectionCommon.cpp:149,
// NatTypeDetectionClient.cpp:64, NatTypeDetectionServer.cpp:85), and 1000 for RakPeer's threads on POSIX
// (RakPeer.cpp:385-397). glibc creates threads that inherit the creator's scheduling, so on Linux these values have no
// effect. macOS applies them, which runs those threads at the lowest priority: on a busy machine they run too late for
// RakNet's ping-scaled deadlines (NatTypeDetectionServer's 3 x ping + 50 ms between tests, Router2's 8 x ping + 300 ms
// mini punch) and for UDPForwarder's timeouts. Replacing the call here, as NatGateway.cpp replaces sendto and recvfrom,
// gives the native peer Linux's behaviour on both platforms without patching RakNet.

#include <pthread.h>

extern "C" int pthread_attr_setschedparam(pthread_attr_t* attributes, const struct sched_param* parameters)
{
    (void)attributes;
    (void)parameters;
    return 0;
}
