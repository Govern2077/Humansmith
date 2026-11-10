using UnityEditor;
using UnityEngine;

namespace DeformableImage.Editor
{
    [CustomEditor(typeof(ColorHalftonePostProcess))]
    public sealed class ColorHalftonePostProcessEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Ordered Bayer is the reference-video path: Mosaic sampling, a rigid "
                + "repeating 8x8 threshold map, then hard color quantization. Legacy "
                + "Variable Dots and CMYK Print are separate effects.",
                MessageType.Info);

            ColorHalftonePostProcess effect = (ColorHalftonePostProcess)target;
            if (GUILayout.Button("Apply Reference Image Preset"))
            {
                Undo.RecordObject(effect, "Apply Reference Halftone Preset");
                effect.ApplyReferencePreset();
                EditorUtility.SetDirty(effect);
            }

            if (GUILayout.Button("Apply CMYK Print Preset"))
            {
                Undo.RecordObject(effect, "Apply CMYK Halftone Preset");
                effect.ApplyCmykPreset();
                EditorUtility.SetDirty(effect);
            }

            if (GUILayout.Button("Apply Video Retro Dither Preset"))
            {
                Undo.RecordObject(effect, "Apply Video Retro Dither Preset");
                effect.ApplyVideoRetroPreset();
                EditorUtility.SetDirty(effect);
            }

            if (GUILayout.Button("Apply Strict Monochrome Bayer Preset"))
            {
                Undo.RecordObject(effect, "Apply Monochrome Bayer Preset");
                effect.ApplyMonochromeBayerPreset();
                EditorUtility.SetDirty(effect);
            }
        }

        [MenuItem("GameObject/Effects/Color Halftone Post Process", false, 20)]
        private static void AddToCamera(MenuCommand command)
        {
            GameObject contextObject = command.context as GameObject;
            Camera camera = contextObject != null
                ? contextObject.GetComponent<Camera>()
                : null;
            if (camera == null)
            {
                camera = Camera.main != null
                    ? Camera.main
                    : Object.FindObjectOfType<Camera>();
            }

            if (camera == null)
            {
                Debug.LogError("No Camera exists in the current Scene.");
                return;
            }

            ColorHalftonePostProcess effect =
                camera.GetComponent<ColorHalftonePostProcess>();
            if (effect == null)
            {
                effect = Undo.AddComponent<ColorHalftonePostProcess>(camera.gameObject);
            }

            effect.ApplyReferencePreset();
            EditorUtility.SetDirty(effect);
            Selection.activeGameObject = camera.gameObject;
        }
    }
}
