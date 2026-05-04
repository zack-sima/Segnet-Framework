using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace SegNet.Editor {

    [CanEditMultipleObjects]
    [CustomEditor(typeof(SegNet.NetworkBehaviour), true)]
    public class NetworkBehaviourEditor : UnityEditor.Editor {
        private HashSet<string> _readOnlyPropertyPaths;

        public override void OnInspectorGUI() {
            serializedObject.Update();
            EnsureReadOnlyPropertyPaths();

            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;

            while (property.NextVisible(enterChildren)) {
                bool readOnly = property.propertyPath == "m_Script" ||
                    _readOnlyPropertyPaths.Contains(property.propertyPath);

                using (new EditorGUI.DisabledScope(readOnly)) {
                    EditorGUILayout.PropertyField(property, includeChildren: true);
                }

                enterChildren = false;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void EnsureReadOnlyPropertyPaths() {
            if (_readOnlyPropertyPaths != null)
                return;

            _readOnlyPropertyPaths = new HashSet<string>();
            Type currentType = target.GetType();

            while (currentType != null && typeof(SegNet.NetworkBehaviour).IsAssignableFrom(currentType)) {
                FieldInfo[] fields = currentType.GetFields(BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                foreach (FieldInfo field in fields) {
                    if (!IsSyncField(field))
                        continue;

                    _readOnlyPropertyPaths.Add(field.Name);
                }

                currentType = currentType.BaseType;
            }
        }

        private static bool IsSyncField(FieldInfo field) {
            return Attribute.IsDefined(field, typeof(SyncVarAttribute), inherit: false) ||
                Attribute.IsDefined(field, typeof(UnreliableSyncVarAttribute), inherit: false);
        }
    }
}
