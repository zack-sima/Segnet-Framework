using System;
using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    public class NetworkConnectionManager : MonoBehaviour {
        [SerializeField] private MonoBehaviour transportBehaviour; // assign SteamTransport in inspector
        [SerializeField] private int clientTimeoutMs = 10000;

        private ITransport _transport;
        private readonly HashSet<ConnectionId> _connections = new HashSet<ConnectionId>();
        private float _lastReceiveTime;
        private bool _timeoutTriggered;

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
            CheckClientTimeout();
        }

        private void HandleConnected(ConnectionId id) {
            _connections.Add(id);
            _lastReceiveTime = Time.realtimeSinceStartup;
            _timeoutTriggered = false;
            OnClientConnected?.Invoke(id);
        }

        private void HandleDisconnected(ConnectionId id, DisconnectReason reason) {
            _connections.Remove(id);
            _timeoutTriggered = false;
            OnClientDisconnected?.Invoke(id, reason);
        }

        private void HandleData(ConnectionId from, ArraySegment<byte> payload, ChannelType channel) {
            _lastReceiveTime = Time.realtimeSinceStartup;
            OnData?.Invoke(from, payload, channel);
        }

        public void Host() {
            _timeoutTriggered = false;
            _transport?.StartServer();
        }

        public void Join() {
            _timeoutTriggered = false;
            _lastReceiveTime = Time.realtimeSinceStartup;
            _transport?.StartClient();
        }

        public void StopAll() {
            _transport?.Stop();
            _connections.Clear();
            _timeoutTriggered = false;
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

        private void CheckClientTimeout() {
            if (_timeoutTriggered || clientTimeoutMs <= 0)
                return;

            if (_transport == null || _transport.Role != NetRole.Client || _connections.Count == 0)
                return;

            float elapsedMs = (Time.realtimeSinceStartup - _lastReceiveTime) * 1000f;
            if (elapsedMs < clientTimeoutMs)
                return;

            ConnectionId timedOut = ConnectionId.Invalid;
            foreach (var connection in _connections) {
                timedOut = connection;
                break;
            }

            if (timedOut == ConnectionId.Invalid)
                return;

            Debug.LogWarning($"[NetworkConnectionManager] Client timed out after {clientTimeoutMs} ms.");

            _timeoutTriggered = true;
            _transport.Stop();
            _connections.Clear();
            OnClientDisconnected?.Invoke(timedOut, DisconnectReason.Timeout);
        }
    }
}
