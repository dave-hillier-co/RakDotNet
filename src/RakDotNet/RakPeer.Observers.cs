using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet;

public sealed partial class RakPeer
{
    /// <summary>
    /// The reliability-layer hooks of every connection, raised inside the I/O cycle on the low-level observers and then on
    /// the plugins that implement <see cref="INetworkObserver"/> (RakPeer's pluginListNTS), as the datagram hooks are.
    /// </summary>
    private sealed class NetworkObserverFanOut(RakPeer peer) : IPeerObserver
    {
        public bool IsObserving => peer._lowLevel.Observers.Current.Length != 0 || peer._plugins.Observers.Length != 0;

        public void OnFrame(IPEndPoint remote, in SessionFrame frame)
        {
            SystemAddress address = remote;
            var info = new FrameInfo(frame, address, peer._byAddress.GetValueOrDefault(address));
            foreach (var observer in peer._lowLevel.Observers.Current)
                observer.OnFrame(info);
            foreach (var observer in peer._plugins.Observers)
                observer.OnFrame(info);
        }

        public void OnAck(IPEndPoint remote, UInt24 messageNumber, long nowUs)
        {
            SystemAddress address = remote;
            var timeMs = (ulong)(nowUs / 1000);
            foreach (var observer in peer._lowLevel.Observers.Current)
                observer.OnAck(messageNumber.Value, address, timeMs);
            foreach (var observer in peer._plugins.Observers)
                observer.OnAck(messageNumber.Value, address, timeMs);
        }

        public void OnNotification(IPEndPoint remote, string message, int bitLength, bool isError)
        {
            SystemAddress address = remote;
            foreach (var observer in peer._lowLevel.Observers.Current)
                observer.OnReliabilityNotification(message, bitLength, address, isError);
            foreach (var observer in peer._plugins.Observers)
                observer.OnReliabilityNotification(message, bitLength, address, isError);
        }
    }
}
