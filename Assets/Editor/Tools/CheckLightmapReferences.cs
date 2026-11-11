
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using System.Text;

public static class CheckLightmapReferences
{
    [MenuItem("Tools/Check Scene Lightmap References")]
    public static void Check()
    {
        Scene scene = SceneManager.GetActiveScene();

        StringBuilder report = new StringBuilder();

        report.AppendLine("Scene: " + scene.path);
        report.AppendLine();

        LightmapData[] maps = LightmapSettings.lightmaps;

        for (int i = 0; i < maps.Length; i++)
        {
            Texture2D tex = maps[i].lightmapColor;

            if (tex == null)
                continue;

            report.AppendLine(
                "Slot " + i + ": " +
                tex.name + " (" + tex.format + ")"
            );

            report.AppendLine(
                AssetDatabase.GetAssetPath(tex)
            );
        }

        report.AppendLine();
        report.AppendLine("DXT5 scene dependencies:");

        if (!string.IsNullOrEmpty(scene.path))
        {
            string[] dependencies =
                AssetDatabase.GetDependencies(scene.path, true);

            foreach (string path in dependencies)
            {
                Texture2D tex =
                    AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (tex != null &&
                    tex.format == TextureFormat.DXT5)
                {
                    report.AppendLine(path);
                }
            }
        }

        EditorUtility.DisplayDialog(
            "Lightmap References",
            report.ToString(),
            "OK"
        );
    }
}
