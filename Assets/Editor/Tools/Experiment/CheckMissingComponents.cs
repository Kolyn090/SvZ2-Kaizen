
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class CheckMissingComponents
{
    private const string Menu =
        "SvZ2 Kaizen/Validation/";

    [MenuItem(Menu + "Check Selected Missing Components", false, 30)]
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

        int count = CountMissing(go);

        EditorUtility.DisplayDialog(
            "Missing Components",
            $"Object: {go.name}\nMissing components: {count}",
            "OK");
    }

    [MenuItem(Menu + "Export Missing Component Report", false, 32)]
    public static void ExportReport()
    {
        var report = new StringBuilder();
        int objectsScanned = 0;
        int missingCount = 0;
        int resolvedCount = 0;

        report.AppendLine("SvZ2 Kaizen - Missing Component Report");
        report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine(new string('=', 65));

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;

            var yaml = ParseSceneYaml(scene.path);

            report.AppendLine();
            report.AppendLine($"SCENE: {scene.name}");
            report.AppendLine($"Path: {scene.path}");
            report.AppendLine(new string('-', 65));

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in
                         root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    objectsScanned++;

                    Component[] components = go.GetComponents<Component>();
                    var missingIndices = new List<int>();

                    for (int index = 0; index < components.Length; index++)
                    {
                        if (components[index] == null)
                            missingIndices.Add(index);
                    }

                    if (missingIndices.Count == 0)
                        continue;

                    missingCount += missingIndices.Count;

                    report.AppendLine();
                    report.AppendLine($"GameObject: {GetPath(t)}");
                    report.AppendLine($"Missing: {missingIndices.Count}");

                    string objectId = GlobalObjectId
                        .GetGlobalObjectIdSlow(go)
                        .targetObjectId.ToString();

                    yaml.TryGetValue(objectId, out var references);

                    foreach (int index in missingIndices)
                    {
                        report.AppendLine($"  Component index: {index}");

                        if (references != null &&
                            index < references.Count &&
                            references[index] != null)
                        {
                            ScriptReference reference = references[index];
                            resolvedCount++;

                            report.AppendLine($"  Script GUID: {reference.Guid}");
                            report.AppendLine($"  Script File ID: {reference.FileId}");

                            string path = AssetDatabase.GUIDToAssetPath(
                                reference.Guid);

                            report.AppendLine(
                                $"  Current asset: " +
                                (string.IsNullOrEmpty(path) ? "Not found" : path));
                        }
                        else
                        {
                            report.AppendLine(
                                "  Script GUID: Unresolved " +
                                "(prefab or unavailable YAML reference)");
                        }

                        report.AppendLine();
                    }
                }
            }
        }

        report.AppendLine(new string('=', 65));
        report.AppendLine("SUMMARY");
        report.AppendLine($"GameObjects scanned: {objectsScanned}");
        report.AppendLine($"Missing components: {missingCount}");
        report.AppendLine($"GUIDs recovered: {resolvedCount}");
        report.AppendLine($"Unresolved: {missingCount - resolvedCount}");

        string directory = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Logs"));

        Directory.CreateDirectory(directory);

        string output = Path.Combine(
            directory, "MissingComponents.log");

        File.WriteAllText(output, report.ToString(), Encoding.UTF8);

        Debug.Log($"Missing component report written to: {output}");
        EditorUtility.RevealInFinder(output);
    }

    private class ScriptReference
    {
        public string Guid;
        public string FileId;
    }

    private static Dictionary<string, List<ScriptReference>>
        ParseSceneYaml(string assetPath)
    {
        var result = new Dictionary<string, List<ScriptReference>>();

        if (string.IsNullOrEmpty(assetPath))
            return result;

        string fullPath = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", assetPath));

        if (!File.Exists(fullPath))
            return result;

        string text = File.ReadAllText(fullPath);

        // Unity text serialization uses YAML document anchors.
        var headers = Regex.Matches(
            text,
            @"(?m)^--- !u!(\d+) &(\d+)[^\r\n]*");

        var components = new Dictionary<string, ScriptReference>();

        // First pass: identify MonoBehaviour script references.
        for (int i = 0; i < headers.Count; i++)
        {
            Match header = headers[i];

            int start = header.Index + header.Length;
            int end = i + 1 < headers.Count
                ? headers[i + 1].Index
                : text.Length;

            string body = text.Substring(start, end - start);

            if (header.Groups[1].Value != "114")
                continue;

            Match script = Regex.Match(
                body,
                @"(?m)^\s*m_Script:\s*\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*\d+\}");

            if (!script.Success)
                continue;

            components[header.Groups[2].Value] = new ScriptReference
            {
                FileId = script.Groups[1].Value,
                Guid = script.Groups[2].Value
            };
        }

        // Second pass: map each GameObject to its components.
        for (int i = 0; i < headers.Count; i++)
        {
            Match header = headers[i];

            if (header.Groups[1].Value != "1")
                continue;

            int start = header.Index + header.Length;
            int end = i + 1 < headers.Count
                ? headers[i + 1].Index
                : text.Length;

            string body = text.Substring(start, end - start);

            Match section = Regex.Match(
                body,
                @"(?ms)^  m_Component:\s*\r?\n(.*?)(?=^  m_[A-Za-z]|\z)");

            if (!section.Success)
                continue;

            var refs = new List<ScriptReference>();

            foreach (Match component in Regex.Matches(
                         section.Groups[1].Value,
                         @"component:\s*\{fileID:\s*(-?\d+)\}"))
            {
                string id = component.Groups[1].Value;
                components.TryGetValue(id, out var reference);
                refs.Add(reference);
            }

            result[header.Groups[2].Value] = refs;
        }

        return result;
    }

    private static int CountMissing(GameObject go)
    {
        int count = 0;

        foreach (Component component in go.GetComponents<Component>())
        {
            if (component == null)
                count++;
        }

        return count;
    }

    private static string GetPath(Transform transform)
    {
        string path = transform.name;

        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }

        return path;
    }
}
