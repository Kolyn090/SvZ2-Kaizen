
using UnityEngine;
using UnityEditor;

public static class ReplaceLightmapInPlace
{
    [MenuItem("SvZ2 Kaizen/Lighting/Replace DXT5 Asset In Place", false, 28)]
    public static void Replace()
    {
        Texture2D original = null;
        Texture2D converted = null;

        foreach (Object obj in Selection.objects)
        {
            Texture2D tex = obj as Texture2D;
            if (tex == null)
                continue;

            if (tex.format == TextureFormat.DXT5)
                original = tex;
            else if (tex.format == TextureFormat.RGBA32)
                converted = tex;
        }

        if (original == null || converted == null)
        {
            EditorUtility.DisplayDialog(
                "Error",
                "Select one DXT5 and one RGBA32 texture.",
                "OK");
            return;
        }

        if (original.width != converted.width ||
            original.height != converted.height)
        {
            EditorUtility.DisplayDialog(
                "Error",
                "Texture dimensions must match.",
                "OK");
            return;
        }

        string path = AssetDatabase.GetAssetPath(original);

        if (string.IsNullOrEmpty(path) ||
            !path.EndsWith(".asset"))
        {
            EditorUtility.DisplayDialog(
                "Error",
                "Original must be a .asset texture.",
                "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog(
            "Confirm Replacement",
            "This will overwrite the original DXT5 asset:\n" +
            path +
            "\n\nMake sure you have a backup.",
            "Replace",
            "Cancel"))
            return;

        try
        {
            string originalName = original.name;

            EditorUtility.CopySerialized(converted, original);
            original.name = originalName;

            EditorUtility.SetDirty(original);
            AssetDatabase.SaveAssets();

            AssetDatabase.ImportAsset(
                path,
                ImportAssetOptions.ForceUpdate
            );

            Texture2D result =
                AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            EditorUtility.DisplayDialog(
                "Result",
                "Path: " + path +
                "\nFormat: " +
                (result != null
                    ? result.format.ToString()
                    : "Asset not found") +
                "\n\nRestart Unity and verify the format again.",
                "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog(
                "Replacement Failed",
                e.Message,
                "OK");
        }
    }
}
