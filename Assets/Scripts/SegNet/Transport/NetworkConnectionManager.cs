using System;
using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    public class NetworkConnectionManager : MonoBehaviour {
        private MonoBehaviour transportBehaviour;
        private int clientTimeoutMs = 10000;

        private ITransport _transport;
        private readonly HashSet<ConnectionId> _connections = new HashSet<ConnectionId>();
        private float _lastReceiveTime;
        private bool _timeoutTriggered;

        public bool IsServer => _transport != null && _transport.Role == NetRole.Server;
        public bool IsClient => _transport != null && _transport.Role == NetRole.Client;
        public int MaxPacketSize => _transport != null ? _transport.MaxPacketSize : 1200;
        public ITransport ActiveTransport => _transport;
        public long TotalBytesIn { get; private set; }
        public long TotalBytesOut { get; private set; }

        public event Action<ConnectionId> OnClientConnected;
        public event Action<ConnectionId, DisconnectReason> OnClientDisconnected;
        public event Action<ConnectionId, ArraySegment<byte>, ChannelType> OnData;
        public event Action<int> OnBytesIn;
        public event Action<int> OnBytesOut;

        private void Awake() {
            if (transportBehaviour != null)
                SetTransport(transportBehaviour);
            else
                Debug.LogWarning("[NetworkConnectionManager] No transport assigned yet.");
        }

        private void OnDestroy() {
            DetachTransport();
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
            TrackBytesIn(payload.Count);
            OnData?.Invoke(from, payload, channel);
        }

        public void Configure(int timeoutMs) {
            clientTimeoutMs = timeoutMs;
        }

        public bool SetTransport(MonoBehaviour transport) {
            return SetTransport(transport as ITransport, transport);
        }

        public bool SetTransport(ITransport transport) {
            return SetTransport(transport, transport as MonoBehaviour);
        }

        private bool SetTransport(ITransport transport, MonoBehaviour behaviour) {
            if (transport == null) {
                Debug.LogError("[NetworkConnectionManager] Transport behaviour does not implement ITransport.");
                return false;
            }

            if (_transport == transport)
                return true;

            if (_transport != null && _transport.IsRunning) {
                Debug.LogWarning("[NetworkConnectionManager] Stopping active transport before swap.");
                StopAll();
            }

            DetachTransport();

            transportBehaviour = behaviour;
            _transport = transport;
            _transport.Initialize();
            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
            _transport.OnData += HandleData;

            _connections.Clear();
            _timeoutTriggered = false;
            _lastReceiveTime = Time.realtimeSinceStartup;

            Debug.Log($"[NetworkConnectionManager] Active transport: {transport.GetType().Name}");
            return true;
        }

        public void ResetByteCounters() {
            TotalBytesIn = 0;
            TotalBytesOut = 0;
        }

        public bool Host() {
            _timeoutTriggered = false;
            if (_transport == null) {
                Debug.LogError("[NetworkConnectionManager] Cannot host: no active transport.");
                return false;
            }

            _transport.StartServer();
            return _transport.IsRunning && _transport.Role == NetRole.Server;
        }

        public bool Join() {
            _timeoutTriggered = false;
            _lastReceiveTime = Time.realtimeSinceStartup;
            if (_transport == null) {
                Debug.LogError("[NetworkConnectionManager] Cannot join: no active transport.");
                return false;
            }

            _transport.StartClient();
            return _transport.IsRunning && _transport.Role == NetRole.Client;
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
            if (_transport == null) return;

            _transport.Send(target, payload, channel);
            TrackBytesOut(payload.Count);
        }

        public void Broadcast(ArraySegment<byte> payload, ChannelType channel) {
            if (_transport == null) return;

            _transport.Broadcast(payload, channel);
            TrackBytesOut(payload.Count * _connections.Count);
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

        private void DetachTransport() {
            if (_transport == null)
                return;

            _transport.OnConnected -= HandleConnected;
            _transport.OnDisconnected -= HandleDisconnected;
            _transport.OnData -= HandleData;
            _transport = null;
        }

        private void TrackBytesIn(int byteCount) {
            if (byteCount <= 0) return;
            TotalBytesIn += byteCount;
            OnBytesIn?.Invoke(byteCount);
        }

        private void TrackBytesOut(int byteCount) {
            if (byteCount <= 0) return;
            TotalBytesOut += byteCount;
            OnBytesOut?.Invoke(byteCount);
        }
    }
}
