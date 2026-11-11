
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

        // Audit output: <UnityProjectRoot>/Logs/LegacyParticles/, timestamped
        // so previous reports are never overwritten. Project root is the
        // parent of Application.dataPath (<project>/Assets).
        string reportPath;
        try
        {
            string projectRoot =
                System.IO.Directory.GetParent(Application.dataPath).FullName;
            string outputDir = System.IO.Path.Combine(
                projectRoot,
                System.IO.Path.Combine("Logs", "LegacyParticles"));

            System.IO.Directory.CreateDirectory(outputDir);

            reportPath = System.IO.Path.Combine(
                outputDir,
                "SvZ2_LegacyParticleAudit_"
                    + System.DateTime.Now.ToString("yyyy-MM-dd_HHmmss")
                    + ".txt");

            System.IO.File.WriteAllText(reportPath, report.ToString());
            Debug.Log("Legacy Particle Audit report saved to: "
                + reportPath);
        }
        catch (System.Exception e)
        {
            reportPath = null;
            Debug.LogError("Legacy Particle Audit: could not save report ("
                + e.GetType().Name + "): " + e.Message);
        }

        EditorUtility.DisplayDialog(
            "Legacy Particle Audit",
            "Prefabs scanned: " + prefabsScanned +
            "\nScenes scanned: " + scenesScanned +
            "\nLegacy components: " + legacyCount +
            (reportPath != null
                ? "\n\nReport saved to:\n" + reportPath
                : "\n\nReport could not be saved (see Console)."),
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
