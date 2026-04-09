using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Per-scene singleton that collects scene-placed NetworkBehaviours.
    /// Destroyed on scene reload.
    ///
    /// Scene objects self-register via NetworkBehaviour.Awake(). If this manager
    /// awakens after some behaviours, it drains their pending registrations.
    ///
    /// Place one of these in every scene that contains networked scene objects.
    /// </summary>
    public class NetworkSceneManager : MonoBehaviour {

        public static NetworkSceneManager Instance { get; private set; }

        private readonly Dictionary<uint, NetworkBehaviour> _sceneObjects =
            new Dictionary<uint, NetworkBehaviour>();

        /// <summary>All registered scene objects keyed by deterministic SceneObjectId.</summary>
        public IReadOnlyDictionary<uint, NetworkBehaviour> SceneObjects => _sceneObjects;

        private void Awake() {
            if (Instance != null && Instance != this) {
                Debug.LogWarning("[NetworkSceneManager] Duplicate instance in scene — destroying.");
                Destroy(this);
                return;
            }
            Instance = this;

            // Drain behaviours that registered before this manager was ready
            foreach (var obj in NetworkBehaviour.DrainPendingSceneRegistrations())
                RegisterSceneObject(obj);
        }

        private void OnDestroy() {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>Register a scene-placed NetworkBehaviour. Called by NetworkBehaviour.Awake.</summary>
        public void RegisterSceneObject(NetworkBehaviour behaviour) {
            if (behaviour == null || !behaviour.IsSceneObject) return;

            uint sceneId = ComputeSceneObjectId(behaviour);
            behaviour.SceneObjectId = sceneId;

            if (_sceneObjects.ContainsKey(sceneId)) {
                Debug.LogWarning(
                    $"[NetworkSceneManager] Duplicate sceneObjectId {sceneId} " +
                    $"for '{behaviour.name}'. Rename objects to disambiguate.");
                return;
            }

            _sceneObjects[sceneId] = behaviour;
        }

        /// <summary>Look up a scene object by its deterministic SceneObjectId.</summary>
        public NetworkBehaviour GetBySceneId(uint sceneObjectId) {
            _sceneObjects.TryGetValue(sceneObjectId, out var obj);
            return obj;
        }

        // ---- Deterministic scene-object ID ----

        /// <summary>
        /// Produces a stable uint from the object's hierarchy path.
        /// If multiple NetworkBehaviours exist on the same GameObject, the type name
        /// and sibling index are appended to disambiguate.
        /// </summary>
        private static uint ComputeSceneObjectId(NetworkBehaviour behaviour) {
            string path = GetHierarchyPath(behaviour.transform);

            var siblings = behaviour.GetComponents<NetworkBehaviour>();
            if (siblings.Length > 1) {
                int idx = System.Array.IndexOf(siblings, behaviour);
                path += $"/{behaviour.GetType().Name}#{idx}";
            }

            return FNVHash(path);
        }

        private static string GetHierarchyPath(Transform t) {
            string path = t.name;
            while (t.parent != null) {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        private static uint FNVHash(string str) {
            uint hash = 2166136261u;
            foreach (char c in str) {
                hash ^= c;
                hash *= 16777619u;
            }
            return hash;
        }
    }
}
