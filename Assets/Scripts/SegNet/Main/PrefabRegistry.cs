using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Stable registry of network prefabs. Index in the list determines the prefab ID
    /// (starting at 1). Both server and client must use the same registry asset with
    /// the same prefabs in the same order.
    ///
    /// Create via: Right-click → Create → SegNet → Prefab Registry.
    /// Assign to NetworkManager in the inspector.
    /// </summary>
    [CreateAssetMenu(fileName = "PrefabRegistry", menuName = "SegNet/Prefab Registry")]
    public class PrefabRegistry : ScriptableObject {

        [Tooltip("Network prefabs. Each must have a NetworkBehaviour on its root GameObject. " +
                 "Index determines stable prefab ID (starting at 1). Do not reorder after shipping.")]
        [SerializeField] private List<GameObject> prefabs = new List<GameObject>();

        /// <summary>
        /// Look up a prefab by its stable ID. PrefabId 1 = index 0.
        /// Returns null if not found.
        /// </summary>
        public GameObject GetPrefab(ushort prefabId) {
            int index = prefabId - 1;
            if (index < 0 || index >= prefabs.Count) return null;
            return prefabs[index];
        }

        /// <summary>
        /// Get the stable prefab ID for a given prefab. Returns 0 if not registered.
        /// </summary>
        public ushort GetPrefabId(GameObject prefab) {
            for (int i = 0; i < prefabs.Count; i++) {
                if (prefabs[i] == prefab)
                    return (ushort)(i + 1);
            }
            return 0;
        }

        public int Count => prefabs.Count;

#if UNITY_EDITOR
        private void OnValidate() {
            for (int i = 0; i < prefabs.Count; i++) {
                if (prefabs[i] != null && prefabs[i].GetComponent<NetworkBehaviour>() == null)
                    Debug.LogWarning(
                        $"[PrefabRegistry] Prefab '{prefabs[i].name}' at index {i} " +
                        "has no NetworkBehaviour on root.", this);
            }
        }
#endif
    }
}
