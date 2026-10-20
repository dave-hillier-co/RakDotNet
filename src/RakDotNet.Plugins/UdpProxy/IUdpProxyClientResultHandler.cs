using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// UDPProxyClientResultHandler (UDPProxyClient.h:27-95): the outcome of <see cref="UdpProxyClient.RequestForwarding(SystemAddress, SystemAddress, RakNetGuid, TimeSpan, BitStream?)"/>,
/// and forwarding another system set up to this one. Every method is optional. They run on the dispatch step of the
/// peer the client is attached to. The proxy coordinator passed is always the one the message came from.
/// </summary>
public interface IUdpProxyClientResultHandler
{
    /// <summary>
    /// OnForwardingSuccess: datagrams sent to <paramref name="proxyIPAddress"/>:<paramref name="proxyPort"/> now reach
    /// <paramref name="targetAddress"/>. <paramref name="sourceAddress"/> is this system's external address when the
    /// request left it unassigned.
    /// </summary>
    void OnForwardingSuccess(string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                             SystemAddress targetAddress, RakNetGuid targetGuid, UdpProxyClient proxyClient)
    {
    }

    /// <summary>
    /// OnForwardingNotification: another system set up forwarding to this one, which is <paramref name="targetAddress"/>.
    /// The plugin has already pinged the proxy, to open this system's router to it.
    /// </summary>
    void OnForwardingNotification(string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                                  SystemAddress targetAddress, RakNetGuid targetGuid, UdpProxyClient proxyClient)
    {
    }

    /// <summary>OnNoServersOnline: no UDPProxyServer is logged into the coordinator.</summary>
    void OnNoServersOnline(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid,
                           UdpProxyClient proxyClient)
    {
    }

    /// <summary>OnRecipientNotConnected: the target named by GUID is not connected to the coordinator.</summary>
    void OnRecipientNotConnected(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid,
                                 UdpProxyClient proxyClient)
    {
    }

    /// <summary>OnAllServersBusy: every server tried refused, having no forwarding entry free.</summary>
    void OnAllServersBusy(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid,
                          UdpProxyClient proxyClient)
    {
    }

    /// <summary>
    /// OnForwardingInProgress: the pair is already being forwarded, or a request for it is in progress, in which case
    /// the address is empty and the port 0.
    /// </summary>
    void OnForwardingInProgress(string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                                SystemAddress targetAddress, RakNetGuid targetGuid, UdpProxyClient proxyClient)
    {
    }
}

/// <summary>
/// UDPProxyServerResultHandler (UDPProxyServer.h:31-60): the outcome of <see cref="UdpProxyServer.LoginToCoordinator"/>.
/// Every method is optional; they run on the dispatch step, with the password that was used.
/// </summary>
public interface IUdpProxyServerResultHandler
{
    /// <summary>OnLoginSuccess: the coordinator lists this server.</summary>
    void OnLoginSuccess(string usedPassword, UdpProxyServer proxyServer)
    {
    }

    /// <summary>OnAlreadyLoggedIn: this login failed, but the server is listed as if it succeeded.</summary>
    void OnAlreadyLoggedIn(string usedPassword, UdpProxyServer proxyServer)
    {
    }

    /// <summary>OnNoPasswordSet: the coordinator has no remote login password, so takes no remote logins.</summary>
    void OnNoPasswordSet(string usedPassword, UdpProxyServer proxyServer)
    {
    }

    /// <summary>OnWrongPassword: the coordinator's password is another one.</summary>
    void OnWrongPassword(string usedPassword, UdpProxyServer proxyServer)
    {
    }
}
