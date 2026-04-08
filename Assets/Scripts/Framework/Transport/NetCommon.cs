using System;

namespace SegNet {
    public enum NetRole {
        None = 0,
        Server = 1,
        Client = 2
    }

    public enum ChannelType {
        Reliable = 0,
        Unreliable = 1
    }

    public enum DisconnectReason {
        Unknown = 0,
        LocalShutdown = 1,
        RemoteShutdown = 2,
        Timeout = 3,
        TransportError = 4
    }

    public readonly struct ConnectionId : IEquatable<ConnectionId> {
        public readonly int Value;
        public static readonly ConnectionId Invalid = new ConnectionId(-1);

        public ConnectionId(int value) { Value = value; }

        public bool Equals(ConnectionId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is ConnectionId other && Equals(other);
        public override int GetHashCode() => Value;
        public override string ToString() => Value.ToString();

        public static bool operator ==(ConnectionId a, ConnectionId b) => a.Value == b.Value;
        public static bool operator !=(ConnectionId a, ConnectionId b) => a.Value != b.Value;
    }

    public interface ITransport {
        NetRole Role { get; }
        bool IsRunning { get; }
        int MaxPacketSize { get; }

        event Action<ConnectionId> OnConnected;
        event Action<ConnectionId, DisconnectReason> OnDisconnected;
        event Action<ConnectionId, ArraySegment<byte>, ChannelType> OnData;

        void Initialize();
        void Shutdown();

        // For now no parameters; implementation decides how to host/join (Steam lobby etc.)
        void StartServer();
        void StartClient();
        void Stop();

        void Poll();

        void Send(ConnectionId connection, ArraySegment<byte> payload, ChannelType channel);
        void Broadcast(ArraySegment<byte> payload, ChannelType channel);
        void Disconnect(ConnectionId connection);
    }
}
