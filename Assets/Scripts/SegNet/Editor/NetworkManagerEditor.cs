using SegNet;
using UnityEditor;

namespace SegNet.Editor {

    [CustomEditor(typeof(BaseNetworkManager), true)]
    public class NetworkManagerEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            serializedObject.Update();

            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;

            while (property.NextVisible(enterChildren)) {
                bool readOnly = property.propertyPath == "m_Script" ||
                    property.propertyPath == "kbIn" ||
                    property.propertyPath == "kbOut";

                using (new EditorGUI.DisabledScope(readOnly)) {
                    EditorGUILayout.PropertyField(property, includeChildren: true);
                }

                enterChildren = false;
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
