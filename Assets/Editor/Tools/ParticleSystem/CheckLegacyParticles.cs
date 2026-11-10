
using UnityEngine;
using UnityEditor;

public static class CheckLegacyParticles
{
    [MenuItem("Tools/Particle System/Conversion V2/Count Selected Legacy Particles", false, 202)]
    public static void Count()
    {
        GameObject selected = Selection.activeGameObject;

        if (selected == null)
        {
            EditorUtility.DisplayDialog(
                "Legacy Particle Check",
                "Please select a GameObject in the Hierarchy.",
                "OK"
            );
            return;
        }

#pragma warning disable 0618

        ParticleEmitter[] emitters =
            selected.GetComponentsInChildren<ParticleEmitter>(true);

        ParticleAnimator[] animators =
            selected.GetComponentsInChildren<ParticleAnimator>(true);

        ParticleRenderer[] renderers =
            selected.GetComponentsInChildren<ParticleRenderer>(true);

#pragma warning restore 0618

        string result =
            "Selected: " + selected.name +
            "\n\nLegacy Particle Emitters: " + emitters.Length +
            "\nLegacy Particle Animators: " + animators.Length +
            "\nLegacy Particle Renderers: " + renderers.Length;

        EditorUtility.DisplayDialog(
            "Legacy Particle Check",
            result,
            "OK"
        );
    }
}
