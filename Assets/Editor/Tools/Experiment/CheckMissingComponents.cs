
using UnityEditor;
using UnityEngine;

public static class CheckMissingComponents
{
    [MenuItem("SvZ2 Kaizen/Validation/Check Selected Missing Components", false, 30)]
    public static void Check()
    {
        GameObject go = Selection.activeGameObject;

        if (go == null)
        {
            EditorUtility.DisplayDialog(
                "Missing Components",
                "Please select a GameObject.",
                "OK");
            return;
        }

        Component[] components = go.GetComponents<Component>();
        int missing = 0;

        foreach (Component component in components)
        {
            if (component == null)
                missing++;
        }

        EditorUtility.DisplayDialog(
            "Missing Components",
            "Object: " + go.name +
            "\nMissing components: " + missing,
            "OK");
    }
}
