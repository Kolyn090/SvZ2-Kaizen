
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

public static class CheckLegacyParticlesAll
{
    [MenuItem("Tools/Particle System/Conversion V2/Scan All Legacy Particles", false, 201)]
    public static void ScanAll()
    {
        string[] guids = AssetDatabase.FindAssets("t:Prefab t:Scene");
        List<string> results = new List<string>();
        int prefabsScanned = 0;
        int scenesScanned = 0;
        int legacyCount = 0;

        // Preserve the currently open scene.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        SceneSetup[] originalSetup =
            EditorSceneManager.GetSceneManagerSetup();

        try
        {
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                if (path.EndsWith(".prefab"))
                {
                    GameObject prefab =
                        AssetDatabase.LoadAssetAtPath<GameObject>(path);

                    if (prefab == null)
                        continue;

                    prefabsScanned++;
                    legacyCount += CountLegacy(prefab, path, results);
                }
                else if (path.EndsWith(".unity"))
                {
                    Scene scene =
                        EditorSceneManager.OpenScene(
                            path, OpenSceneMode.Single);

                    scenesScanned++;

                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        legacyCount += CountLegacy(root, path, results);
                    }
                }
            }
        }
        finally
        {
            EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
        }

        StringBuilder report = new StringBuilder();
        report.AppendLine("LEGACY PARTICLE AUDIT");
        report.AppendLine("Prefabs scanned: " + prefabsScanned);
        report.AppendLine("Scenes scanned: " + scenesScanned);
        report.AppendLine("Legacy components: " + legacyCount);
        report.AppendLine();

        foreach (string result in results)
            report.AppendLine(result);

        string reportPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SvZ2_LegacyParticleAudit.txt");

        System.IO.File.WriteAllText(reportPath, report.ToString());

        EditorUtility.DisplayDialog(
            "Legacy Particle Audit",
            "Prefabs scanned: " + prefabsScanned +
            "\nScenes scanned: " + scenesScanned +
            "\nLegacy components: " + legacyCount +
            "\n\nReport saved to:\n" + reportPath,
            "OK"
        );
    }

    private static int CountLegacy(
        GameObject root,
        string assetPath,
        List<string> results)
    {
        int count = 0;

#pragma warning disable 0618
        ParticleEmitter[] emitters =
            root.GetComponentsInChildren<ParticleEmitter>(true);

        ParticleAnimator[] animators =
            root.GetComponentsInChildren<ParticleAnimator>(true);

        ParticleRenderer[] renderers =
            root.GetComponentsInChildren<ParticleRenderer>(true);
#pragma warning restore 0618

        foreach (ParticleEmitter c in emitters)
        {
            results.Add(assetPath + " | Emitter | " + c.name);
            count++;
        }

        foreach (ParticleAnimator c in animators)
        {
            results.Add(assetPath + " | Animator | " + c.name);
            count++;
        }

        foreach (ParticleRenderer c in renderers)
        {
            results.Add(assetPath + " | Renderer | " + c.name);
            count++;
        }

        return count;
    }
}
