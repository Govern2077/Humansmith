#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeformableImage.Editor
{
    public static class OriginalOutlineValidation
    {
        private const string Trigger = "Temp/RunOriginalOutlineValidation";
        private const string Report = "Temp/OriginalOutlineValidation.txt";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [InitializeOnLoadMethod]
        private static void Schedule()
        {
            if (!File.Exists(Trigger)) return;
            EditorApplication.delayCall += Run;
        }

        [MenuItem("Tools/Face/Validate Original Outline")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (File.Exists(Trigger)) File.Delete(Trigger);
            GameObject fixture = null;
            Texture2D texture = null;
            Sprite sprite = null;
            try
            {
                texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                var pixels = new Color32[64 * 64];
                for (int y = 8; y < 56; y++) for (int x = 8; x < 56; x++)
                    pixels[y * 64 + x] = new Color32(255, 255, 255, 255);
                texture.SetPixels32(pixels); texture.Apply();
                sprite = Sprite.Create(texture, new Rect(0, 0, 64, 64), Vector2.one * 0.5f);
                fixture = new GameObject("Original outline validation") { hideFlags = HideFlags.HideAndDontSave };
                var mesh = fixture.AddComponent<DeformableImageMesh>();
                mesh.ConfigureSprites(sprite, sprite);
                var tool = fixture.AddComponent<DeformableImageInteractor>();
                Require(!mesh.IsNearCutBoundary(Vector3.zero, 0.2f), "Uncut surface is not an incision");
                CheckTools(mesh, tool);
                // Wider than one texel in this deliberately tiny test texture.
                Require(mesh.ApplyOpenCut(new List<Vector2> { new Vector2(0.4f, 0.5f), new Vector2(0.6f, 0.5f) }, 0.24f), "Apply incision");
                Require(mesh.IsNearCutBoundary(new Vector3(0, 0.2f, 0), 0.2f), "Incision retains unrestricted mode");
                CheckTools(mesh, tool);
                // A custom/depth-fitted pose is the operation baseline, not the flat sprite.
                var posed = mesh.CopyCurrentVertices();
                for (int i = 0; i < posed.Length; i++) posed[i] += new Vector3(0.1f, -0.2f, 0.3f);
                mesh.SetLogicalVertices(posed);
                CheckTools(mesh, tool);
                File.WriteAllText(Report, "PASS: drag/inflate/pinch keep original outline fixed before and after physical cutting; interior vertices move; incision classification; posed baseline preserved.");
                Debug.Log("Original outline validation passed.");
            }
            catch (Exception error)
            {
                File.WriteAllText(Report, "FAIL: " + error);
                Debug.LogException(error);
            }
            finally
            {
                if (fixture != null) UnityEngine.Object.DestroyImmediate(fixture);
                if (sprite != null) UnityEngine.Object.DestroyImmediate(sprite);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void CheckTools(DeformableImageMesh mesh, DeformableImageInteractor tool)
        {
            Vector3[] baseline = mesh.CopyCurrentVertices();
            bool[] anchors = mesh.CopyOriginalOutlineAnchors();
            int anchored = 0;
            foreach (bool anchor in anchors) if (anchor) anchored++;
            Require(anchored > 0 && anchored < baseline.Length, "Outline supports selected without freezing entire surface");
            Set(tool, "operationStartVertices", baseline);
            Set(tool, "operationOutlineAnchors", anchors);
            Set(tool, "workingVertices", new Vector3[baseline.Length]);
            Set(tool, "operationStartLocalPoint", Vector3.zero);
            Set(tool, "brushRadius", 10f);
            Set(tool, "operationMayRevealBack", true);
            foreach (var mode in new[] { DeformableImageInteractor.DeformationTool.Drag,
                DeformableImageInteractor.DeformationTool.Inflate, DeformableImageInteractor.DeformationTool.Pinch })
            {
                tool.ActiveTool = mode;
                typeof(DeformableImageInteractor).GetMethod("BuildWorkingVertices", Private)
                    .Invoke(tool, new object[] { new Vector3(5, 3, 1), mode == DeformableImageInteractor.DeformationTool.Pinch ? -0.5f : 0.5f, 1f });
                var result = (Vector3[])typeof(DeformableImageInteractor).GetField("workingVertices", Private).GetValue(tool);
                bool moved = false;
                for (int i = 0; i < result.Length; i++)
                {
                    float delta = (result[i] - baseline[i]).sqrMagnitude;
                    if (anchors[i]) Require(delta < 1e-12f, mode + " fixed outline");
                    else if (delta > 0.001f) moved = true;
                }
                Require(moved, mode + " movable interior/cut");
            }
        }

        private static void Set(object target, string field, object value) { target.GetType().GetField(field, Private).SetValue(target, value); }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
#endif
