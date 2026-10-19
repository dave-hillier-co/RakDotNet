namespace RakDotNet;

/// <summary>Who runs the I/O cycle.</summary>
/// <remarks>
/// The I/O cycle, in both modes: drain commands; receive datagrams without blocking until the transport is empty or the
/// event queue is full; feed the sans-IO layers with the time from the <see cref="TimeProvider"/>; run timers; flush
/// outgoing datagrams; call <see cref="INetworkObserver.OnUpdateCycle"/>.
/// </remarks>
public enum PeerThreading : byte
{
    /// <summary>
    /// One dedicated loop thread runs the I/O cycle and the application reads <see cref="RakPeer.Events"/> from any
    /// thread. The loop waits on one reusable source signalled by the transports, by a <see cref="TimeProvider"/>
    /// timer at the next deadline, and by commands, so advancing a fake time provider wakes it.
    /// </summary>
    Background,

    /// <summary>
    /// No threads: the application calls <see cref="RakPeer.Update"/> then reads <see cref="RakPeer.Events"/>,
    /// as RAKPEER_USER_THREADED with RunUpdateCycle. Plugins then run on the application's thread.
    /// </summary>
    Manual,
}
