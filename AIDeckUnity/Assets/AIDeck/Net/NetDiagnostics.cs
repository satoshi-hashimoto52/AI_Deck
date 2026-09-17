using System;
using System.Net.Sockets;

namespace AIDeck.Net
{
    /// <summary>
    /// Turns socket failures into one short sentence a DJ can act on.
    ///
    /// Raw socket exception text names hosts, ports and system error codes. NFR-006 keeps that
    /// out of anything broadcast, and NFR-007 says the diagnostic detail goes to the log while
    /// the user gets something useful — "the Mac is not answering" tells you to check the app
    /// is running; "SocketException 61" does not.
    /// </summary>
    public static class NetDiagnostics
    {
        public static string Describe(Exception exception)
        {
            switch (exception)
            {
                case null:
                    return "The connection was lost.";
                case SocketException socket:
                    return Describe(socket.SocketErrorCode);
                case ObjectDisposedException _:
                    return "The connection was closed.";
                case System.IO.IOException _:
                    return "The connection was interrupted.";
                default:
                    return "The connection failed.";
            }
        }

        public static string Describe(SocketError error)
        {
            switch (error)
            {
                case SocketError.ConnectionRefused:
                    return "The Mac refused the connection. Is AI Deck running on it?";
                case SocketError.TimedOut:
                    return "The Mac did not answer. Check that both devices are on the same network.";
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                    return "That address cannot be reached from this network.";
                case SocketError.AddressNotAvailable:
                    return "That address is not valid on this network.";
                case SocketError.ConnectionReset:
                case SocketError.ConnectionAborted:
                    return "The connection was dropped.";
                case SocketError.AddressAlreadyInUse:
                    return "That port is already in use by another program.";
                case SocketError.AccessDenied:
                    return "The system refused network access. Check the local network permission.";
                default:
                    return "The connection failed.";
            }
        }
    }
}
