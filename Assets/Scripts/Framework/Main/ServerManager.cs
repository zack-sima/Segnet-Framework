using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SegNet {

    /// <summary>
    /// The current network lifecycle state.
    /// </summary>
    public enum NetworkState {
        Offline = 0,
        Starting,
        Server,
        Host,   // server + local client
        Client,
    }

    /// <summary>
    /// Singleton entry point for the SegNet framework.
    /// Manages network lifecycle, player records, object spawning, and state replication.
    /// Persists across scene loads via DontDestroyOnLoad.
    /// </summary>
    public class ServerManager : MonoBehaviour {
        public static ServerManager Instance { get; private set; }

        [Header("Transport")]
        [SerializeField] private NetworkConnectionManager connectionManager;
        [SerializeField] private NetworkStreamManager streamManager;

        [Header("Spawning")]
        [SerializeField] private PrefabRegistry prefabRegistry;

        // ---- State ----

        public NetworkState State { get; private set; } = NetworkState.Offline;
        public bool IsServer => State == NetworkState.Server || State == NetworkState.Host;
        public bool IsClient => State == NetworkState.Client || State == NetworkState.Host;
        public bool IsHost => State == NetworkState.Host;
        public bool IsOnline => State != NetworkState.Offline && State != NetworkState.Starting;

        // ---- Message dispatcher ----

        public MessageDispatcher Messages { get; private set; }

        // ---- Connection access ----

        public IReadOnlyCollection<ConnectionId> Connections => connectionManager.Connections;

        // ---- Player registries ----

        private readonly Dictionary<int, NetworkPlayer> _players = new Dictionary<int, NetworkPlayer>();
        private readonly Dictionary<ConnectionId, NetworkPlayer> _connectionToPlayer =
            new Dictionary<ConnectionId, NetworkPlayer>();
        private int _nextPlayerId = 1;

        public IReadOnlyDictionary<int, NetworkPlayer> Players => _players;
        public NetworkPlayer LocalPlayer { get; private set; }

        // ---- Object registries ----

        private readonly Dictionary<uint, NetworkBehaviour> _networkedObjects =
            new Dictionary<uint, NetworkBehaviour>();
        private uint _nextNetworkId = 1;

        /// <summary>All networked objects (runtime + scene) keyed by NetworkId.</summary>
        public IReadOnlyDictionary<uint, NetworkBehaviour> NetworkedObjects => _networkedObjects;

        // ---- Framework callbacks ----

        public event Action<ConnectionId> OnClientConnected;
        public event Action<ConnectionId, DisconnectReason> OnClientDisconnected;
        public event Action<NetworkPlayer> OnPlayerJoined;
        public event Action<NetworkPlayer> OnPlayerLeft;
        public event Action<NetworkBehaviour> OnObjectSpawned;
        public event Action<NetworkBehaviour> OnObjectDespawned;
        public event Action OnStarted;
        public event Action OnStopped;

        // ==================================================================
        //  Unity lifecycle
        // ==================================================================

        private void Awake() {
            if (Instance != null && Instance != this) {
                Debug.LogWarning("[ServerManager] Duplicate instance destroyed.");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(transform.root.gameObject);

            ValidateReferences();

            Messages = new MessageDispatcher(streamManager);

            connectionManager.OnClientConnected += HandleClientConnected;
            connectionManager.OnClientDisconnected += HandleClientDisconnected;
        }

        private void OnDestroy() {
            if (Instance == this)
                Instance = null;

            Messages?.Dispose();

            if (connectionManager != null) {
                connectionManager.OnClientConnected -= HandleClientConnected;
                connectionManager.OnClientDisconnected -= HandleClientDisconnected;
            }
        }

        private void LateUpdate() {
            if (IsServer)
                ProcessDirtyObjects();
        }

        // ==================================================================
        //  Public API: lifecycle
        // ==================================================================

        public void StartServer() {
            if (State != NetworkState.Offline) {
                Debug.LogWarning("[ServerManager] Cannot start server: already in state " + State);
                return;
            }

            State = NetworkState.Starting;
            RegisterClientMessageHandlers();
            connectionManager.Host();
            State = NetworkState.Server;

            ActivateSceneObjects();

            Debug.Log("[ServerManager] Started as Server.");
            OnStarted?.Invoke();
        }

        public void StartHost() {
            if (State != NetworkState.Offline) {
                Debug.LogWarning("[ServerManager] Cannot start host: already in state " + State);
                return;
            }

            State = NetworkState.Starting;
            RegisterClientMessageHandlers();
            connectionManager.Host();
            State = NetworkState.Host;

            // Create host's local player
            LocalPlayer = CreatePlayer(ConnectionId.Invalid, isLocal: true, isHost: true);

            ActivateSceneObjects();

            Debug.Log($"[ServerManager] Started as Host. Local player: {LocalPlayer}");
            OnStarted?.Invoke();
        }

        public void StartClient() {
            if (State != NetworkState.Offline) {
                Debug.LogWarning("[ServerManager] Cannot start client: already in state " + State);
                return;
            }

            State = NetworkState.Starting;
            RegisterClientMessageHandlers();
            connectionManager.Join();
            State = NetworkState.Client;

            Debug.Log("[ServerManager] Started as Client.");
            OnStarted?.Invoke();
        }

        /// <summary>
        /// Stop the network session. Destroys runtime objects, deactivates scene objects.
        /// Call SceneManager.LoadScene() afterward to fully reset scene state.
        /// </summary>
        public void Stop() {
            if (State == NetworkState.Offline) return;

            NetworkState previousState = State;

            DestroyAllRuntimeObjects();
            DeactivateAllSceneObjects();
            DestroyAllPlayers();

            UnregisterClientMessageHandlers();
            connectionManager.StopAll();
            State = NetworkState.Offline;
            LocalPlayer = null;
            _nextNetworkId = 1;
            _nextPlayerId = 1;

            Debug.Log($"[ServerManager] Stopped (was {previousState}).");
            OnStopped?.Invoke();
        }

        // ==================================================================
        //  Public API: player lookup
        // ==================================================================

        public NetworkPlayer GetPlayer(int playerId) {
            _players.TryGetValue(playerId, out var player);
            return player;
        }

        public NetworkPlayer GetPlayerByConnection(ConnectionId connectionId) {
            _connectionToPlayer.TryGetValue(connectionId, out var player);
            return player;
        }

        // ==================================================================
        //  Public API: spawn / despawn (server only)
        // ==================================================================

        /// <summary>
        /// Spawn a network object from a registered prefab. Server only.
        /// Returns the root NetworkBehaviour of the spawned object.
        /// </summary>
        public NetworkBehaviour ServerSpawn(GameObject prefab, Vector3 position, Quaternion rotation,
            NetworkPlayer owner = null) {
            if (!IsServer) {
                Debug.LogError("[ServerManager] ServerSpawn can only be called on the server.");
                return null;
            }
            if (prefab == null) {
                Debug.LogError("[ServerManager] ServerSpawn: prefab is null.");
                return null;
            }
            if (prefabRegistry == null) {
                Debug.LogError("[ServerManager] ServerSpawn: no PrefabRegistry assigned.");
                return null;
            }

            ushort prefabId = prefabRegistry.GetPrefabId(prefab);
            if (prefabId == 0) {
                Debug.LogError($"[ServerManager] Prefab '{prefab.name}' not found in PrefabRegistry.");
                return null;
            }

            return ServerSpawnInternal(prefab, prefabId, position, rotation, owner);
        }

        /// <summary>Despawn a runtime-spawned network object. Server only.</summary>
        public void ServerDespawn(NetworkBehaviour obj) {
            if (!IsServer) {
                Debug.LogError("[ServerManager] ServerDespawn can only be called on the server.");
                return;
            }
            if (obj == null || !obj.IsSpawned) return;
            if (obj.IsSceneObject) {
                Debug.LogWarning("[ServerManager] Cannot despawn scene objects.");
                return;
            }

            var root = obj.Root ?? obj;
            DespawnInternal(root, sendMessage: true);
        }

        // ==================================================================
        //  Public API: object lookup
        // ==================================================================

        /// <summary>Get a networked object by its runtime NetworkId.</summary>
        public NetworkBehaviour GetNetworkObject(uint networkId) {
            _networkedObjects.TryGetValue(networkId, out var obj);
            return obj;
        }

        // ==================================================================
        //  Server: connection events → player + object lifecycle
        // ==================================================================

        private void HandleClientConnected(ConnectionId connId) {
            Debug.Log($"[ServerManager] Client connected: {connId}");
            OnClientConnected?.Invoke(connId);

            if (!IsServer) return;

            // 1. Send existing players
            foreach (var kvp in _players)
                SendPlayerJoined(connId, kvp.Value, isYou: false);

            // 2. Create new player
            var newPlayer = CreatePlayer(connId, isLocal: false, isHost: false);
            SendPlayerJoined(connId, newPlayer, isYou: true);
            BroadcastPlayerJoinedExcept(connId, newPlayer);

            // 3. Send all existing network objects (scene + runtime)
            foreach (var kvp in _networkedObjects)
                SendSpawnTo(connId, kvp.Value);
        }

        private void HandleClientDisconnected(ConnectionId connId, DisconnectReason reason) {
            Debug.Log($"[ServerManager] Client disconnected: {connId} ({reason})");
            OnClientDisconnected?.Invoke(connId, reason);

            if (!IsServer) return;

            if (_connectionToPlayer.TryGetValue(connId, out var player)) {
                // Clean up owned objects
                var owned = new List<NetworkBehaviour>(player.OwnedObjects);
                foreach (var obj in owned) {
                    if (obj == null) continue;
                    if (obj.IsSceneObject) {
                        // Scene objects: remove owner, keep alive
                        obj.OwnerPlayer = null;
                        player.RemoveOwnedObject(obj);
                    } else {
                        // Runtime objects: despawn
                        ServerDespawn(obj.Root ?? obj);
                    }
                }

                BroadcastPlayerLeft(player.PlayerId);
                DestroyPlayer(player);
            }
        }

        // ==================================================================
        //  Server: scene object activation
        // ==================================================================

        private void ActivateSceneObjects() {
            var sceneManager = NetworkSceneManager.Instance;
            if (sceneManager == null) return;

            foreach (var kvp in sceneManager.SceneObjects) {
                var behaviour = kvp.Value;
                if (behaviour == null || behaviour.IsSpawned) continue;

                uint nid = _nextNetworkId++;
                behaviour.NetworkId = nid;
                behaviour.PrefabId = 0;
                behaviour.ComponentIndex = 0;
                behaviour.Root = behaviour;
                behaviour.AllBehaviours = new[] { behaviour };
                behaviour.IsSpawned = true;

                _networkedObjects[nid] = behaviour;

                behaviour.OnNetworkSpawn();
                OnObjectSpawned?.Invoke(behaviour);
            }
        }

        private void DeactivateAllSceneObjects() {
            var toRemove = new List<uint>();
            foreach (var kvp in _networkedObjects) {
                if (kvp.Value != null && kvp.Value.IsSceneObject) {
                    kvp.Value.OnNetworkDespawn();
                    OnObjectDespawned?.Invoke(kvp.Value);
                    kvp.Value.IsSpawned = false;
                    kvp.Value.NetworkId = 0;
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (uint id in toRemove)
                _networkedObjects.Remove(id);
        }

        // ==================================================================
        //  Server: spawn / despawn internals
        // ==================================================================

        private NetworkBehaviour ServerSpawnInternal(GameObject prefab, ushort prefabId,
            Vector3 position, Quaternion rotation, NetworkPlayer owner) {

            var go = Instantiate(prefab, position, rotation);
            var behaviours = go.GetComponentsInChildren<NetworkBehaviour>();

            if (behaviours.Length == 0) {
                Debug.LogError($"[ServerManager] Spawned prefab '{prefab.name}' has no NetworkBehaviours.");
                Destroy(go);
                return null;
            }

            uint nid = _nextNetworkId++;
            var root = behaviours[0];
            root.AllBehaviours = behaviours;

            for (int i = 0; i < behaviours.Length; i++) {
                var b = behaviours[i];
                b.NetworkId = nid;
                b.PrefabId = prefabId;
                b.ComponentIndex = i;
                b.Root = root;
                b.OwnerPlayer = owner;
                b.IsSpawned = true;
            }

            if (owner != null)
                owner.AddOwnedObject(root);

            _networkedObjects[nid] = root;

            // Call spawn callbacks
            foreach (var b in behaviours)
                b.OnNetworkSpawn();
            OnObjectSpawned?.Invoke(root);

            // Send to all remote clients
            foreach (var conn in connectionManager.Connections)
                SendSpawnTo(conn, root);

            return root;
        }

        private void DespawnInternal(NetworkBehaviour root, bool sendMessage) {
            if (root == null) return;
            uint nid = root.NetworkId;

            // Notify clients
            if (sendMessage) {
                var writer = new NetworkWriter();
                writer.WriteUInt(nid);
                Messages.Broadcast(NetworkMessageType.Despawn, writer);
            }

            // Callbacks
            if (root.AllBehaviours != null) {
                foreach (var b in root.AllBehaviours)
                    b.OnNetworkDespawn();
            }
            OnObjectDespawned?.Invoke(root);

            // Clean up owner reference
            if (root.OwnerPlayer != null)
                root.OwnerPlayer.RemoveOwnedObject(root);

            _networkedObjects.Remove(nid);

            if (root.gameObject != null)
                Destroy(root.gameObject);
        }

        private void DestroyAllRuntimeObjects() {
            var toDestroy = new List<NetworkBehaviour>();
            foreach (var kvp in _networkedObjects) {
                if (kvp.Value != null && !kvp.Value.IsSceneObject)
                    toDestroy.Add(kvp.Value);
            }
            foreach (var root in toDestroy)
                DespawnInternal(root, sendMessage: false);
        }

        // ==================================================================
        //  Server: state replication (dirty flush)
        // ==================================================================

        private void ProcessDirtyObjects() {
            foreach (var kvp in _networkedObjects) {
                var root = kvp.Value;
                if (root == null || root.AllBehaviours == null) continue;

                foreach (var b in root.AllBehaviours) {
                    if (b != null && b.ConsumeDirty())
                        SendStateUpdateToAll(root.NetworkId, b.ComponentIndex, b);
                }
            }
        }

        // ==================================================================
        //  Server: send messages
        // ==================================================================

        private void SendSpawnTo(ConnectionId target, NetworkBehaviour root) {
            var writer = new NetworkWriter(256);
            WriteSpawnMessage(writer, root);
            Messages.Send(target, NetworkMessageType.Spawn, writer);
        }

        private void WriteSpawnMessage(NetworkWriter writer, NetworkBehaviour root) {
            writer.WriteUShort(root.PrefabId);
            writer.WriteUInt(root.NetworkId);
            writer.WriteInt(root.OwnerPlayer != null ? root.OwnerPlayer.PlayerId : -1);

            byte flags = 0;
            if (root.IsSceneObject) flags |= 0x01;
            writer.WriteByte(flags);

            writer.WriteUInt(root.SceneObjectId);
            writer.WriteVector3(root.transform.position);
            writer.WriteQuaternion(root.transform.rotation);
            writer.WriteVector3(root.transform.localScale);

            var behaviours = root.AllBehaviours ?? new[] { root };
            writer.WriteUShort((ushort)behaviours.Length);
            foreach (var b in behaviours) {
                var stateWriter = new NetworkWriter();
                b.OnSerialize(stateWriter, true);
                var seg = stateWriter.ToArraySegment();
                writer.WriteUShort((ushort)seg.Count);
                if (seg.Count > 0)
                    writer.WriteRawBytes(seg);
            }
        }

        private void SendStateUpdateToAll(uint networkId, int componentIndex, NetworkBehaviour behaviour) {
            var writer = new NetworkWriter();
            writer.WriteUInt(networkId);
            writer.WriteUShort((ushort)componentIndex);
            behaviour.OnSerialize(writer, false);
            Messages.Broadcast(NetworkMessageType.StateUpdate, writer);
        }

        private void SendPlayerJoined(ConnectionId target, NetworkPlayer player, bool isYou) {
            var writer = new NetworkWriter();
            writer.WriteInt(player.PlayerId);
            writer.WriteBool(isYou);
            writer.WriteBool(player.IsHost);
            Messages.Send(target, NetworkMessageType.PlayerJoined, writer);
        }

        private void BroadcastPlayerJoinedExcept(ConnectionId exclude, NetworkPlayer player) {
            var writer = new NetworkWriter();
            writer.WriteInt(player.PlayerId);
            writer.WriteBool(false);
            writer.WriteBool(player.IsHost);
            Messages.BroadcastExcept(exclude, NetworkMessageType.PlayerJoined, writer);
        }

        private void BroadcastPlayerLeft(int playerId) {
            var writer = new NetworkWriter();
            writer.WriteInt(playerId);
            Messages.Broadcast(NetworkMessageType.PlayerLeft, writer);
        }

        // ==================================================================
        //  Client: message handlers
        // ==================================================================

        private void RegisterClientMessageHandlers() {
            Messages.RegisterHandler(NetworkMessageType.PlayerJoined, OnMsg_PlayerJoined);
            Messages.RegisterHandler(NetworkMessageType.PlayerLeft, OnMsg_PlayerLeft);
            Messages.RegisterHandler(NetworkMessageType.Spawn, OnMsg_Spawn);
            Messages.RegisterHandler(NetworkMessageType.Despawn, OnMsg_Despawn);
            Messages.RegisterHandler(NetworkMessageType.StateUpdate, OnMsg_StateUpdate);
        }

        private void UnregisterClientMessageHandlers() {
            Messages.UnregisterHandler(NetworkMessageType.PlayerJoined);
            Messages.UnregisterHandler(NetworkMessageType.PlayerLeft);
            Messages.UnregisterHandler(NetworkMessageType.Spawn);
            Messages.UnregisterHandler(NetworkMessageType.Despawn);
            Messages.UnregisterHandler(NetworkMessageType.StateUpdate);
        }

        // ---- Player messages (unchanged) ----

        private void OnMsg_PlayerJoined(ConnectionId from, NetworkReader reader) {
            int playerId = reader.ReadInt();
            bool isYou = reader.ReadBool();
            bool isHost = reader.ReadBool();

            if (_players.ContainsKey(playerId)) return;

            var player = CreatePlayerRecord(playerId, ConnectionId.Invalid, isLocal: isYou, isHost: isHost);
            if (isYou)
                LocalPlayer = player;

            Debug.Log($"[ServerManager] PlayerJoined: {player} (isYou={isYou})");
        }

        private void OnMsg_PlayerLeft(ConnectionId from, NetworkReader reader) {
            int playerId = reader.ReadInt();

            if (_players.TryGetValue(playerId, out var player)) {
                if (player == LocalPlayer) LocalPlayer = null;
                DestroyPlayer(player);
                Debug.Log($"[ServerManager] PlayerLeft: playerId={playerId}");
            }
        }

        // ---- Spawn / Despawn / StateUpdate ----

        private void OnMsg_Spawn(ConnectionId from, NetworkReader reader) {
            // Host already has everything — don't double-spawn
            if (IsHost) return;

            ushort prefabId = reader.ReadUShort();
            uint networkId = reader.ReadUInt();
            int ownerPlayerId = reader.ReadInt();
            byte flags = reader.ReadByte();
            bool sceneObj = (flags & 0x01) != 0;
            uint sceneObjectId = reader.ReadUInt();
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = reader.ReadQuaternion();
            Vector3 scale = reader.ReadVector3();

            ushort behaviourCount = reader.ReadUShort();
            var stateBlocks = new byte[behaviourCount][];
            for (int i = 0; i < behaviourCount; i++) {
                ushort len = reader.ReadUShort();
                stateBlocks[i] = len > 0 ? reader.ReadRawBytes(len) : Array.Empty<byte>();
            }

            // Resolve owner
            NetworkPlayer owner = ownerPlayerId >= 0 ? GetPlayer(ownerPlayerId) : null;

            NetworkBehaviour root;

            if (sceneObj) {
                // Scene object: find existing object in scene
                var sceneManager = NetworkSceneManager.Instance;
                if (sceneManager == null) {
                    Debug.LogWarning("[ServerManager] Spawn(scene): no NetworkSceneManager in scene.");
                    return;
                }
                root = sceneManager.GetBySceneId(sceneObjectId);
                if (root == null) {
                    Debug.LogWarning($"[ServerManager] Spawn(scene): sceneObjectId {sceneObjectId} not found.");
                    return;
                }

                root.NetworkId = networkId;
                root.ComponentIndex = 0;
                root.Root = root;
                root.AllBehaviours = new[] { root };
                root.OwnerPlayer = owner;
                root.IsSpawned = true;

            } else {
                // Runtime object: instantiate from prefab registry
                if (prefabRegistry == null) {
                    Debug.LogError("[ServerManager] Spawn(runtime): no PrefabRegistry assigned.");
                    return;
                }
                var prefab = prefabRegistry.GetPrefab(prefabId);
                if (prefab == null) {
                    Debug.LogError($"[ServerManager] Spawn(runtime): prefabId {prefabId} not in registry.");
                    return;
                }

                var go = Instantiate(prefab, position, rotation);
                go.transform.localScale = scale;

                var behaviours = go.GetComponentsInChildren<NetworkBehaviour>();
                if (behaviours.Length == 0) {
                    Debug.LogError($"[ServerManager] Spawn(runtime): prefab has no NetworkBehaviours.");
                    Destroy(go);
                    return;
                }

                root = behaviours[0];
                root.AllBehaviours = behaviours;

                for (int i = 0; i < behaviours.Length; i++) {
                    var b = behaviours[i];
                    b.NetworkId = networkId;
                    b.PrefabId = prefabId;
                    b.ComponentIndex = i;
                    b.Root = root;
                    b.OwnerPlayer = owner;
                    b.IsSpawned = true;
                }
            }

            if (owner != null)
                owner.AddOwnedObject(root);

            _networkedObjects[networkId] = root;

            // Apply initial state
            var behaviourArr = root.AllBehaviours ?? new[] { root };
            for (int i = 0; i < behaviourArr.Length && i < stateBlocks.Length; i++) {
                if (stateBlocks[i].Length > 0) {
                    var stateReader = new NetworkReader(stateBlocks[i]);
                    behaviourArr[i].OnDeserialize(stateReader, true);
                }
            }

            foreach (var b in behaviourArr)
                b.OnNetworkSpawn();

            OnObjectSpawned?.Invoke(root);
        }

        private void OnMsg_Despawn(ConnectionId from, NetworkReader reader) {
            if (IsHost) return;

            uint networkId = reader.ReadUInt();

            if (!_networkedObjects.TryGetValue(networkId, out var root)) return;
            if (root.IsSceneObject) return; // scene objects don't get despawned

            if (root.AllBehaviours != null) {
                foreach (var b in root.AllBehaviours)
                    b.OnNetworkDespawn();
            }
            OnObjectDespawned?.Invoke(root);

            if (root.OwnerPlayer != null)
                root.OwnerPlayer.RemoveOwnedObject(root);

            _networkedObjects.Remove(networkId);

            if (root.gameObject != null)
                Destroy(root.gameObject);
        }

        private void OnMsg_StateUpdate(ConnectionId from, NetworkReader reader) {
            if (IsHost) return;

            uint networkId = reader.ReadUInt();
            ushort componentIndex = reader.ReadUShort();

            if (!_networkedObjects.TryGetValue(networkId, out var root)) return;

            var behaviours = root.AllBehaviours ?? new[] { root };
            if (componentIndex >= behaviours.Length) return;

            behaviours[componentIndex].OnDeserialize(reader, false);
        }

        // ==================================================================
        //  Player creation / destruction
        // ==================================================================

        private NetworkPlayer CreatePlayer(ConnectionId connId, bool isLocal, bool isHost) {
            int playerId = _nextPlayerId++;
            return CreatePlayerRecord(playerId, connId, isLocal, isHost);
        }

        private NetworkPlayer CreatePlayerRecord(int playerId, ConnectionId connId,
            bool isLocal, bool isHost) {
            var go = new GameObject($"Player_{playerId}");
            go.transform.SetParent(transform);

            var player = go.AddComponent<NetworkPlayer>();
            player.PlayerId = playerId;
            player.ConnectionId = connId;
            player.IsLocal = isLocal;
            player.IsHost = isHost;

            _players[playerId] = player;
            if (connId != ConnectionId.Invalid)
                _connectionToPlayer[connId] = player;

            OnPlayerJoined?.Invoke(player);
            return player;
        }

        private void DestroyPlayer(NetworkPlayer player) {
            OnPlayerLeft?.Invoke(player);

            _players.Remove(player.PlayerId);
            if (player.ConnectionId != ConnectionId.Invalid)
                _connectionToPlayer.Remove(player.ConnectionId);

            if (player.gameObject != null)
                Destroy(player.gameObject);
        }

        private void DestroyAllPlayers() {
            var all = new List<NetworkPlayer>(_players.Values);
            foreach (var player in all)
                DestroyPlayer(player);

            _players.Clear();
            _connectionToPlayer.Clear();
        }

        // ==================================================================
        //  Validation
        // ==================================================================

        private void ValidateReferences() {
            if (connectionManager == null)
                Debug.LogError("[ServerManager] NetworkConnectionManager reference is missing.");
            if (streamManager == null)
                Debug.LogError("[ServerManager] NetworkStreamManager reference is missing.");
            if (prefabRegistry == null)
                Debug.LogWarning("[ServerManager] PrefabRegistry not assigned. Runtime spawning will fail.");
        }
    }
}
