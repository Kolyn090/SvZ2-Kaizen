
using UnityEngine;
using UnityEditor;
using System.IO;

public static class ConvertRGBMToDLDR
{
    private const float Brightness = 0.8f;

    [MenuItem("SvZ2 Kaizen/Lighting/Convert RGBM Lightmap to iOS dLDR", false, 21)]
    public static void Convert()
    {
        Texture2D original = Selection.activeObject as Texture2D;

        if (original == null)
        {
            EditorUtility.DisplayDialog(
                "Error",
                "Please select a lightmap texture.",
                "OK"
            );
            return;
        }

        string originalPath = AssetDatabase.GetAssetPath(original);

        if (string.IsNullOrEmpty(originalPath) ||
            !originalPath.EndsWith(".asset"))
        {
            EditorUtility.DisplayDialog(
                "Error",
                "Select a lightmap .asset file.",
                "OK"
            );
            return;
        }

        try
        {
            Color32[] source = original.GetPixels32();
            Color32[] output = new Color32[source.Length];

            int clippedPixels = 0;

            for (int i = 0; i < source.Length; i++)
            {
                Color32 p = source[i];

                // RGBM decoding for Gamma color space.
                float multiplier = (p.a / 255f) * 5f * Brightness;

                float r = (p.r / 255f) * multiplier;
                float g = (p.g / 255f) * multiplier;
                float b = (p.b / 255f) * multiplier;

                // dLDR stores lighting divided by 2.
                if (r > 2f || g > 2f || b > 2f)
                    clippedPixels++;

                output[i] = new Color32(
                    (byte)Mathf.RoundToInt(
                        Mathf.Clamp01(r / 2f) * 255f),
                    (byte)Mathf.RoundToInt(
                        Mathf.Clamp01(g / 2f) * 255f),
                    (byte)Mathf.RoundToInt(
                        Mathf.Clamp01(b / 2f) * 255f),
                    255
                );
            }

            // No mipmaps for this first test.
            Texture2D converted = new Texture2D(
                original.width,
                original.height,
                TextureFormat.RGBA32,
                false
            );

            converted.SetPixels32(output);
            converted.wrapMode = original.wrapMode;
            converted.filterMode = original.filterMode;
            converted.Apply(false, false);

            string folder = Path.GetDirectoryName(originalPath);
            string newPath = AssetDatabase.GenerateUniqueAssetPath(
                folder + "/" + original.name + "_iOS_dLDR.asset"
            );

            AssetDatabase.CreateAsset(converted, newPath);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog(
                "Conversion Complete",
                "Created:\n" + newPath +
                "\n\nClipped pixels: " + clippedPixels +
                " / " + output.Length +
                "\n\nOriginal asset was not modified.",
                "OK"
            );

            Selection.activeObject = converted;
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog(
                "Conversion Failed",
                e.Message,
                "OK"
            );
        }
    }
}
