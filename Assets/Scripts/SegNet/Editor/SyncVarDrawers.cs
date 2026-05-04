using UnityEditor;
using UnityEngine;

namespace SegNet.Editor {

    [CustomPropertyDrawer(typeof(SyncVarAttribute))]
    public class SyncVarPropertyDrawer : PropertyDrawer {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            using (new EditorGUI.DisabledScope(true)) {
                EditorGUI.PropertyField(position, property, label, includeChildren: true);
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) {
            return EditorGUI.GetPropertyHeight(property, label, includeChildren: true);
        }
    }

    [CustomPropertyDrawer(typeof(UnreliableSyncVarAttribute))]
    public class UnreliableSyncVarPropertyDrawer : PropertyDrawer {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            using (new EditorGUI.DisabledScope(true)) {
                EditorGUI.PropertyField(position, property, label, includeChildren: true);
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) {
            return EditorGUI.GetPropertyHeight(property, label, includeChildren: true);
        }
    }
}
