using UnityEditor;
using UnityEngine;

public static class ScriptIconAssigner {
    [MenuItem("Tools/Icons/Assign Selected Script Icon")]
    private static void AssignSelectedScriptIcon() {
        Object selected = Selection.activeObject;
        if (selected == null) {
            Debug.LogWarning("Select a MonoScript first.");
            return;
        }

        MonoScript monoScript = selected as MonoScript;
        if (monoScript == null) {
            Debug.LogWarning("Selected object is not a MonoScript.");
            return;
        }

        Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>(
            "Assets/Scripts/SegNet/Editor/segnet_icon.png"
        );

        if (icon == null) {
            Debug.LogError("Could not load icon texture at the given path.");
            return;
        }

        MonoImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(monoScript)) as MonoImporter;
        if (importer == null) {
            Debug.LogError("Could not get MonoImporter for script.");
            return;
        }

        importer.SetIcon(icon);
        importer.SaveAndReimport();

        Debug.Log($"Assigned icon to script: {monoScript.name}");
    }

    [MenuItem("Tools/Icons/Clear Selected Script Icon")]
    private static void ClearSelectedScriptIcon() {
        Object selected = Selection.activeObject;
        if (selected == null) {
            Debug.LogWarning("Select a MonoScript first.");
            return;
        }

        MonoScript monoScript = selected as MonoScript;
        if (monoScript == null) {
            Debug.LogWarning("Selected object is not a MonoScript.");
            return;
        }

        MonoImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(monoScript)) as MonoImporter;
        if (importer == null) {
            Debug.LogError("Could not get MonoImporter for script.");
            return;
        }

        importer.SetIcon(null);
        importer.SaveAndReimport();

        Debug.Log($"Cleared icon from script: {monoScript.name}");
    }
}