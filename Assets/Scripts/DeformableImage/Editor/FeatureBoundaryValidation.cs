#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeformableImage.Editor
{
    public static class FeatureBoundaryValidation
    {
        [MenuItem("Tools/Face/Validate Feature Stretch Strength")]
        public static void Run()
        {
            RunChecks();
            Debug.Log("Feature stretch strength and single-owner UI validation passed.");
        }

        public static void RunBatch()
        {
            try
            {
                RunChecks();
                OriginalOutlineValidation.Run();
                SurgicalInstrumentsValidation.Run();
                string outline = File.ReadAllText("Temp/OriginalOutlineValidation.txt");
                string tools = File.ReadAllText("Temp/SurgicalInstrumentsValidation.txt");
                Require(outline.StartsWith("PASS:"), outline);
                Require(tools.StartsWith("PASS:"), tools);
                File.WriteAllText("FeatureBoundaryValidation.txt", "PASS: reduced eye/mouth tool strength; no corner locks or cumulative movement budget; per-stroke maximum; repeatable pulling; cancellation; hook response/release; single UI owner; original outline and surgical instrument regressions.");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                File.WriteAllText("FeatureBoundaryValidation.txt", "FAIL: " + error);
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void RunChecks()
        {
            GameObject face = null, muscle = null;
            Texture2D texture = null; Sprite sprite = null;
            try
            {
                texture = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                var pixels = new Color32[128 * 128];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
                texture.SetPixels32(pixels); texture.Apply();
                sprite = Sprite.Create(texture, new Rect(0, 0, 128, 128), Vector2.one * 0.5f);
                face = new GameObject("Feature validation skin") { hideFlags = HideFlags.HideAndDontSave };
                muscle = new GameObject("Feature validation muscle") { hideFlags = HideFlags.HideAndDontSave };
                var skin = face.AddComponent<DeformableImageMesh>(); skin.ConfigureSprites(sprite, sprite);
                var inner = muscle.AddComponent<DeformableImageMesh>(); inner.ConfigureSprites(sprite, sprite);
                var curves = face.AddComponent<FacialFeaturePrecut2D>(); curves.Configure(skin, null, inner);
                var baseline = skin.CopyCurrentVertices(); var uvs = skin.CopyNormalizedRestUvs();
                int eye = Nearest(uvs, new Vector2(0.36f, 0.58f));
                int mouth = Nearest(uvs, new Vector2(0.5f, 0.34f));
                int corner = Nearest(uvs, curves.GetEditableCurve(2).ControlPoints[0]);
                int cheek = Nearest(uvs, new Vector2(0.2f, 0.4f));
                var skinTool = face.AddComponent<DeformableImageInteractor>(); Invoke(skinTool, "Awake");
                float[] strengths = skin.CopyToolStretchStrengths();
                Require(strengths[eye] >= 0.69f && strengths[eye] < 0.78f, "Eye pull strength is reduced, not locked");
                Require(strengths[mouth] >= 0.69f && strengths[mouth] < 0.75f, "Mouth pull strength is reduced, not locked");
                Require(Mathf.Abs(strengths[corner] - strengths[mouth]) < 0.03f, "No special mouth-corner anchor");
                Require(strengths[cheek] > 0.999f, "Unrelated skin keeps original strength");
                var single = BuildPull(skinTool, skin, baseline, strengths, eye, Vector3.right * 0.2f);
                Require(Mathf.Abs(single[eye].x - baseline[eye].x - 0.2f * strengths[eye]) < 0.00001f, "Drag response uses local strength");
                var large = BuildPull(skinTool, skin, baseline, strengths, eye, Vector3.right * 100f);
                float singleMaximum = 1.2f * 1.2f * strengths[eye];
                Require(Vector3.Distance(large[eye], baseline[eye]) <= singleMaximum + 0.00001f, "Large gesture has reduced per-stroke maximum");
                Require(Vector3.Distance(large[eye], baseline[eye]) > 0.5f, "Former absolute contour lock is removed");
                skin.SetLogicalVertices(single);
                skin.SetLogicalVertices((Vector3[])baseline.Clone());
                Require((skin.CopyCurrentVertices()[eye] - baseline[eye]).sqrMagnitude < 1e-10f, "Cancellation exactly restores baseline");
                for (int step = 0; step < 20; step++)
                {
                    var current = skin.CopyCurrentVertices();
                    skin.SetLogicalVertices(BuildPull(skinTool, skin, current, strengths, eye, Vector3.right * 0.2f));
                }
                Require(Vector3.Distance(skin.CopyCurrentVertices()[eye], baseline[eye]) > 2f, "Repeated adjustments are allowed without cumulative lock");
                skin.SetLogicalVertices((Vector3[])baseline.Clone());
                var instruments = face.GetComponent<SurgicalInstrumentPrototype2D>();
                skinTool.BrushRadius = 1.2f;
                Require(instruments.BeginPull(baseline[eye], false), "Eye remains grabbable by hook");
                instruments.MovePull(baseline[eye] + Vector3.right * 0.1f); instruments.SolveConstraints();
                float hookDelta = Vector3.Distance(skin.CopyCurrentVertices()[eye], baseline[eye]);
                Require(hookDelta > 0.04f && hookDelta < 0.09f, "Hook response is weaker and still movable");
                instruments.CancelPendingInput(); instruments.SolveConstraints();
                Require((skin.CopyCurrentVertices()[eye] - baseline[eye]).sqrMagnitude < 1e-10f, "Hook release is reversible");

                // Register in reverse order: skin still owns the single panel, not both layers.
                var innerTool = muscle.AddComponent<DeformableImageInteractor>();
                Invoke(innerTool, "Awake"); Invoke(innerTool, "OnEnable");
                Invoke(skinTool, "OnEnable");
                Require(DeformableImageInteractor.PanelOwner == skinTool, "Exactly one primary skin panel");
                Require(DeformableImageInteractor.SelectedLayer == skinTool, "Initial mouse input routes only to skin");
                innerTool.enabled = false;
                Require(DeformableImageInteractor.PanelOwner == skinTool, "Disabled layer cannot take over panel");
            }
            finally
            {
                if (face != null) UnityEngine.Object.DestroyImmediate(face);
                if (muscle != null) UnityEngine.Object.DestroyImmediate(muscle);
                if (sprite != null) UnityEngine.Object.DestroyImmediate(sprite);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static int Nearest(Vector2[] uvs, Vector2 point)
        {
            int best = 0; float distance = float.PositiveInfinity;
            for (int i = 0; i < uvs.Length; i++) { float d = (uvs[i] - point).sqrMagnitude; if (d < distance) { distance = d; best = i; } }
            return best;
        }
        private static Vector3[] BuildPull(DeformableImageInteractor tool, DeformableImageMesh skin,
            Vector3[] baseline, float[] strengths, int centre, Vector3 delta)
        {
            Set(tool, "operationStartVertices", baseline);
            Set(tool, "operationStartLocalPoint", baseline[centre]);
            Set(tool, "operationOutlineAnchors", skin.CopyOriginalOutlineAnchors());
            Set(tool, "operationFeatureStrengths", strengths);
            Set(tool, "operationMayRevealBack", true);
            Set(tool, "brushRadius", 1.2f);
            Set(tool, "workingVertices", new Vector3[baseline.Length]);
            typeof(DeformableImageInteractor).GetMethod("BuildWorkingVertices", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(tool, new object[] { delta, 0f, 1f });
            return (Vector3[])typeof(DeformableImageInteractor).GetField("workingVertices", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tool);
        }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value); }
        private static void Invoke(object target, string method) { target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, null); }
        private static void Require(bool ok, string label) { if (!ok) throw new Exception(label); }
    }
}
#endif
