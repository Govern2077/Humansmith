#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeformableImage.Editor
{
    public static class ManualTissueTransferValidation
    {
        [MenuItem("Tools/Face/Validate Manual Tissue Transfer")]
        public static void Run() { RunChecks(); Debug.Log("Manual tissue transfer validation passed."); }

        public static void RunBatch()
        {
            try
            {
                RunChecks();
                File.WriteAllText("ManualTissueTransferValidation.txt", "PASS: skin-to-fat-to-muscle; muscle-to-fat-to-skin; middle-layer transfer both ways; preserved depth gaps/outer anchors; no double application or 100-frame drift; return to baseline; local dissection blocks inward propagation; disabled links; differing grids; rebuilt physical cuts.");
                FeatureBoundaryValidation.RunBatch();
            }
            catch (Exception error)
            {
                File.WriteAllText("ManualTissueTransferValidation.txt", "FAIL: " + error);
                Debug.LogException(error); EditorApplication.Exit(1);
            }
        }

        private static void RunChecks()
        {
            var objects = new List<GameObject>(); Texture2D texture = null; Sprite sprite = null;
            try
            {
                texture = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                var pixels = new Color32[128 * 128];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
                texture.SetPixels32(pixels); texture.Apply();
                sprite = Sprite.Create(texture, new Rect(0, 0, 128, 128), Vector2.one * 0.5f);
                var meshes = new DeformableImageMesh[3];
                for (int layer = 0; layer < 3; layer++)
                {
                    var go = new GameObject("Manual link fixture " + layer) { hideFlags = HideFlags.HideAndDontSave };
                    objects.Add(go); go.transform.position = Vector3.forward * layer * 0.15f;
                    meshes[layer] = go.AddComponent<DeformableImageMesh>(); meshes[layer].ConfigureSprites(sprite, sprite);
                }
                var fatToSkin = objects[0].AddComponent<SoftTissueLayerCoupler2D>(); fatToSkin.Configure(meshes[1], meshes[0]); fatToSkin.BindRestPose();
                var muscleToFat = objects[1].AddComponent<SoftTissueLayerCoupler2D>(); muscleToFat.Configure(meshes[2], meshes[1]); muscleToFat.BindRestPose();
                var baseline = Capture(meshes);
                var pulled = Pull(meshes[0], new Vector3(0.22f, 0.13f, 0.04f));
                meshes[0].SetManualVertices(pulled);
                CheckAlignedDelta(meshes, baseline, "Inward skin drag"); CheckAnchors(meshes, baseline);
                var held = Capture(meshes);
                for (int i = 0; i < 100; i++) { muscleToFat.ApplyCurrentSourceShape(); fatToSkin.ApplyCurrentSourceShape(); }
                CheckSame(held, Capture(meshes), "No forward feedback or held drift");
                meshes[0].SetManualVertices((Vector3[])baseline[0].Clone());
                CheckSame(baseline, Capture(meshes), "Rebound/cancel restores all layers");

                meshes[2].SetManualVertices(Pull(meshes[2], new Vector3(-0.18f, 0.1f, 0)));
                CheckAlignedDelta(meshes, baseline, "Outward muscle drag");
                meshes[2].SetManualVertices((Vector3[])baseline[2].Clone());
                CheckSame(baseline, Capture(meshes), "Muscle release restores all layers");
                meshes[1].SetManualVertices(Pull(meshes[1], new Vector3(0.1f, -0.15f, 0)));
                CheckAlignedDelta(meshes, baseline, "Middle layer drives both neighbours");
                meshes[1].SetManualVertices((Vector3[])baseline[1].Clone());
                CheckSame(baseline, Capture(meshes), "Middle-layer release");

                fatToSkin.ReleaseInfluence(new Vector2(0.5f, 0.5f), 0.2f, 100f);
                meshes[0].SetManualVertices(Pull(meshes[0], Vector3.right * 0.2f));
                int centre = Nearest(meshes[0], new Vector2(0.5f, 0.5f));
                Require((meshes[0].CopyCurrentVertices()[centre] - baseline[0][centre]).magnitude > 0.15f, "Separated skin still moves");
                Require((meshes[1].CopyCurrentVertices()[centre] - baseline[1][centre]).magnitude < 0.0001f, "Separated interface blocks fat follow");
                Require((meshes[2].CopyCurrentVertices()[centre] - baseline[2][centre]).magnitude < 0.0001f, "Separated interface blocks muscle follow");
                meshes[0].SetManualVertices((Vector3[])baseline[0].Clone()); fatToSkin.ResetReleasedInfluence();
                CheckSame(baseline, Capture(meshes), "Separated-region release stays reversible");

                muscleToFat.enabled = false;
                meshes[0].SetManualVertices(Pull(meshes[0], Vector3.right * 0.1f));
                Require(MaxDelta(meshes[1].CopyCurrentVertices(), baseline[1]) > 0.05f, "Enabled adjacent link follows");
                Require(MaxDelta(meshes[2].CopyCurrentVertices(), baseline[2]) < 0.0001f, "Disabled link stays disconnected");
                meshes[0].SetManualVertices((Vector3[])baseline[0].Clone()); muscleToFat.enabled = true;

                // Different vertex counts must use UV bindings rather than array indices.
                typeof(DeformableImageMesh).GetField("subdivisionsX", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(meshes[2], 16);
                typeof(DeformableImageMesh).GetField("subdivisionsY", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(meshes[2], 16);
                meshes[2].RebuildMesh(); muscleToFat.BindRestPose(); fatToSkin.BindRestPose();
                var coarseBaseline = meshes[2].CopyCurrentVertices();
                meshes[0].SetManualVertices(Pull(meshes[0], Vector3.right * 0.2f));
                Require(MaxDelta(meshes[2].CopyCurrentVertices(), coarseBaseline) > 0.1f, "Coarser muscle follows skin");
                meshes[0].SetManualVertices((Vector3[])baseline[0].Clone());
                Require(MaxDelta(meshes[2].CopyCurrentVertices(), coarseBaseline) < 0.0001f, "Coarse-grid release");

                // Rebuild all three layers with a real incision and rebuild the UV maps.
                foreach (var mesh in meshes)
                    Require(mesh.ApplyOpenCut(new List<Vector2> { new Vector2(0.35f, 0.5f), new Vector2(0.65f, 0.5f) }, 0.18f), "Physical cut fixture");
                muscleToFat.BindRestPose(); fatToSkin.BindRestPose();
                var cutBaseline = Capture(meshes);
                var upper = meshes[0].CopyCurrentVertices(); var anchors = meshes[0].CopyOriginalOutlineAnchors();
                for (int i = 0; i < upper.Length; i++)
                    if (!anchors[i] && upper[i].y > 0.12f && upper[i].y < 0.8f && Mathf.Abs(upper[i].x) < 0.8f) upper[i].x += 0.1f;
                meshes[0].SetManualVertices(upper);
                Require(MaxDelta(meshes[1].CopyCurrentVertices(), cutBaseline[1]) > 0.05f, "Fat follows cut skin after topology rebuild");
                Require(MaxDelta(meshes[2].CopyCurrentVertices(), cutBaseline[2]) > 0.03f, "Muscle follows after topology rebuild");
                CheckAnchors(meshes, cutBaseline);
                meshes[0].SetManualVertices((Vector3[])cutBaseline[0].Clone());
                CheckSame(cutBaseline, Capture(meshes), "Cut topology release restores layers");
            }
            finally
            {
                foreach (var go in objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
                if (sprite != null) UnityEngine.Object.DestroyImmediate(sprite);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static Vector3[] Pull(DeformableImageMesh mesh, Vector3 delta)
        {
            var positions = mesh.CopyCurrentVertices(); var anchors = mesh.CopyOriginalOutlineAnchors();
            for (int i = 0; i < positions.Length; i++)
            {
                if (anchors[i]) continue;
                float w = Mathf.Clamp01(1f - ((Vector2)positions[i]).magnitude / 0.8f);
                positions[i] += delta * w;
            }
            return positions;
        }
        private static Vector3[][] Capture(DeformableImageMesh[] meshes) { var all = new Vector3[meshes.Length][]; for (int i = 0; i < meshes.Length; i++) all[i] = meshes[i].CopyCurrentVertices(); return all; }
        private static void CheckAlignedDelta(DeformableImageMesh[] meshes, Vector3[][] baseline, string label)
        {
            var now = Capture(meshes); Require(MaxDelta(now[0], baseline[0]) > 0.05f, label + " moves skin");
            for (int layer = 1; layer < meshes.Length; layer++)
                for (int i = 0; i < now[layer].Length; i++)
                {
                    Vector3 a = meshes[0].transform.TransformVector(now[0][i] - baseline[0][i]);
                    Vector3 b = meshes[layer].transform.TransformVector(now[layer][i] - baseline[layer][i]);
                    Require((a - b).magnitude < 0.0001f, label + " keeps world-space alignment");
                }
        }
        private static void CheckAnchors(DeformableImageMesh[] meshes, Vector3[][] baseline)
        {
            for (int layer = 0; layer < meshes.Length; layer++)
            {
                var positions = meshes[layer].CopyCurrentVertices(); var anchors = meshes[layer].CopyOriginalOutlineAnchors();
                for (int i = 0; i < anchors.Length; i++) if (anchors[i]) Require((positions[i] - baseline[layer][i]).sqrMagnitude < 1e-10f, "Original outlines remain fixed");
            }
        }
        private static int Nearest(DeformableImageMesh mesh, Vector2 point) { var uv = mesh.CopyNormalizedRestUvs(); int best = 0; float d = float.PositiveInfinity; for (int i = 0; i < uv.Length; i++) { float next = (uv[i] - point).sqrMagnitude; if (next < d) { d = next; best = i; } } return best; }
        private static float MaxDelta(Vector3[] a, Vector3[] b) { float max = 0; for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, Vector3.Distance(a[i], b[i])); return max; }
        private static void CheckSame(Vector3[][] a, Vector3[][] b, string label) { for (int layer = 0; layer < a.Length; layer++) Require(MaxDelta(a[layer], b[layer]) < 0.0001f, label); }
        private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    }
}
#endif
