using System;
using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    public class NetworkConnectionManager : MonoBehaviour {
        [SerializeField] private MonoBehaviour transportBehaviour; // assign SteamTransport in inspector

        private ITransport _transport;
        private readonly HashSet<ConnectionId> _connections = new HashSet<ConnectionId>();

        public bool IsServer => _transport != null && _transport.Role == NetRole.Server;
        public bool IsClient => _transport != null && _transport.Role == NetRole.Client;
        public int MaxPacketSize => _transport != null ? _transport.MaxPacketSize : 1200;

        public event Action<ConnectionId> OnClientConnected;
        public event Action<ConnectionId, DisconnectReason> OnClientDisconnected;
        public event Action<ConnectionId, ArraySegment<byte>, ChannelType> OnData;

        private void Awake() {
            _transport = transportBehaviour as ITransport;
            if (_transport == null) {
                Debug.LogError("[NetworkConnectionManager] Transport behaviour does not implement ITransport.");
                enabled = false;
                return;
            }

            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
            _transport.OnData += HandleData;
        }

        private void OnDestroy() {
            if (_transport != null) {
                _transport.OnConnected -= HandleConnected;
                _transport.OnDisconnected -= HandleDisconnected;
                _transport.OnData -= HandleData;
            }
        }

        private void Update() {
            _transport?.Poll();
        }

        private void HandleConnected(ConnectionId id) {
            _connections.Add(id);
            OnClientConnected?.Invoke(id);
        }

        private void HandleDisconnected(ConnectionId id, DisconnectReason reason) {
            _connections.Remove(id);
            OnClientDisconnected?.Invoke(id, reason);
        }

        private void HandleData(ConnectionId from, ArraySegment<byte> payload, ChannelType channel) {
            OnData?.Invoke(from, payload, channel);
        }

        public void Host() {
            _transport?.StartServer();
        }

        public void Join() {
            _transport?.StartClient();
        }

        public void StopAll() {
            _transport?.Stop();
            _connections.Clear();
        }

        public bool IsConnected() {
            if (_transport == null || !_transport.IsRunning)
                return false;

            if (_transport.Role == NetRole.Server)
                return true;

            if (_transport.Role == NetRole.Client)
                return _connections.Count > 0;

            return false;
        }

        public IReadOnlyCollection<ConnectionId> Connections => _connections;

        public void SendTo(ConnectionId target, ArraySegment<byte> payload, ChannelType channel) {
            _transport?.Send(target, payload, channel);
        }

        public void Broadcast(ArraySegment<byte> payload, ChannelType channel) {
            _transport?.Broadcast(payload, channel);
        }

        public void Disconnect(ConnectionId target) {
            _transport?.Disconnect(target);
        }
    }
}
