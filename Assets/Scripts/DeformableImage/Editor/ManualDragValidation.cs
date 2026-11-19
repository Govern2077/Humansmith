#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeformableImage.Editor
{
    public static class ManualDragValidation
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [MenuItem("Tools/Face/Validate Manual Drag")]
        public static void Run()
        {
            RunChecks();
            Debug.Log("Manual drag falloff, soft eye corners and physical incision validation passed.");
        }

        public static void RunBatch()
        {
            try
            {
                RunChecks();
                File.WriteAllText("ManualDragValidation.txt", "PASS: restored broad cubic falloff (50% at half radius, 15.625% at three-quarter radius); nonzero soft eye corners; preserved mouth/instrument response; material-space resistance and cache invalidation; physical upper-lid pull without lower-lid transfer; restored incision freedom (20 raw-versus-runtime comparisons); original interior orientation guard retained; cancellation and outer anchors; tissue/instrument regressions follow.");
                ManualTissueTransferValidation.RunBatch();
            }
            catch (Exception error)
            {
                File.WriteAllText("ManualDragValidation.txt", "FAIL: " + error);
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void RunChecks()
        {
            Require(FeatureBoundaryMath.ManualDragFalloff(0.1f) > 0.9f, "Smooth broad near grip, without the old sharp central peak");
            Require(Mathf.Abs(FeatureBoundaryMath.ManualDragFalloff(0.5f) - 0.5f) < 1e-6f, "Restored 50% half-radius force");
            Require(Mathf.Abs(FeatureBoundaryMath.ManualDragFalloff(0.75f) - 0.15625f) < 1e-6f, "Restored broad outer tail");
            Require(FeatureBoundaryMath.ManualDragFalloff(-1) == 1 && FeatureBoundaryMath.ManualDragFalloff(2) == 0, "Compact support");
            const float h = 0.001f;
            Require((1f - FeatureBoundaryMath.ManualDragFalloff(h)) / h < 0.004f, "No sharp slope at grip centre");
            Require(FeatureBoundaryMath.ManualDragFalloff(1f - h) / h < 0.004f, "No sharp support edge");
            float previous = 1f;
            for (int i = 0; i <= 1000; i++)
            {
                float t = i / 1000f, w = FeatureBoundaryMath.ManualDragFalloff(t);
                Require(w >= 0 && w <= previous + 1e-6f, "Monotonic bounded force");
                Require(w + 1e-6f >= (1 - t) * (1 - t), "Restored broad falloff versus the original sharp quadratic");
                previous = w;
            }

            GameObject face = null; Texture2D texture = null; Sprite sprite = null;
            try
            {
                texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                var pixels = new Color32[256 * 256];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
                texture.SetPixels32(pixels); texture.Apply();
                sprite = Sprite.Create(texture, new Rect(0, 0, 256, 256), Vector2.one * 0.5f);
                face = new GameObject("Manual drag validation") { hideFlags = HideFlags.HideAndDontSave };
                var mesh = face.AddComponent<DeformableImageMesh>();
                Set(mesh, "subdivisionsX", 64); Set(mesh, "subdivisionsY", 64);
                mesh.ConfigureSprites(sprite, sprite);
                var curves = face.AddComponent<FacialFeaturePrecut2D>(); curves.Configure(mesh, null, null);
                SetEye(curves, 0, 0.25f, 0.4375f); SetEye(curves, 1, 0.5625f, 0.75f);
                var beforeCut = mesh.CopyManualCornerStrengths();
                var uvs = mesh.CopyNormalizedRestUvs();
                foreach (float x in new[] { 0.25f, 0.4375f, 0.5625f, 0.75f })
                {
                    float s = beforeCut[Nearest(uvs, new Vector2(x, 0.625f))];
                    Require(s > 0.049f && s < 0.051f, "All four eye corners retain a very small nonzero weight");
                }
                int mouth = Nearest(uvs, curves.GetEditableCurve(2).ControlPoints[0]);
                Require(beforeCut[mouth] > 0.29f && beforeCut[mouth] < 0.5f, "Mouth corner has a soft, nonzero response");
                Require(beforeCut[Nearest(uvs, new Vector2(0.5f, 0.34f))] == 1f, "Middle of the lip has no extra anatomical attenuation");
                Require(beforeCut[Nearest(uvs, new Vector2(0.34375f, 0.625f))] == 1f, "Upper lid centre remains free");
                Require(Mathf.Abs(mesh.CopyToolStretchStrengths()[Nearest(uvs, new Vector2(0.25f, 0.625f))] - 0.7f) < 1e-5f, "General instrument response unchanged");

                // Editing authored curves must invalidate the shared cache without rebuilding the mesh.
                SetEye(curves, 0, 0.125f, 0.3125f);
                Require(mesh.CopyManualCornerStrengths()[Nearest(uvs, new Vector2(0.125f, 0.625f))] < 0.051f, "Curve edits refresh resistance");
                SetEye(curves, 0, 0.25f, 0.4375f);
                Require(mesh.ApplyOpenCut(new List<Vector2> { new Vector2(0.25f, 0.625f), new Vector2(0.4375f, 0.625f) }, 0.025f), "Physical upper-lid incision");
                var baseline = mesh.CopyCurrentVertices(); uvs = mesh.CopyNormalizedRestUvs();
                var strengths = mesh.CopyManualCornerStrengths();
                Require(strengths.Length == baseline.Length, "Topology refresh keeps all vertices covered");
                var tool = face.AddComponent<DeformableImageInteractor>();
                Invoke(tool, "Awake");
                int centre = Nearest(uvs, new Vector2(0.34375f, 0.640625f));
                int shoulder = Nearest(uvs, new Vector2(0.3125f, 0.640625f));
                int corner = Nearest(uvs, new Vector2(0.25f, 0.640625f));
                int lower = Nearest(uvs, new Vector2(0.34375f, 0.609375f));
                int brow = Nearest(uvs, new Vector2(0.34375f, 0.765625f));
                const float radius = 0.8f;
                Require(mesh.TryBuildGeodesicDistances(baseline[centre], radius, out float[] distances), "Runtime cut-aware brush distances");
                Set(tool, "operationStartVertices", baseline);
                Set(tool, "operationStartLocalPoint", baseline[centre]);
                Set(tool, "operationOutlineAnchors", mesh.CopyOriginalOutlineAnchors());
                Set(tool, "operationFeatureStrengths", mesh.CopyToolStretchStrengths());
                Set(tool, "operationCornerStrengths", strengths);
                Set(tool, "operationGeodesicDistances", distances);
                Set(tool, "operationTriangles", mesh.CopyMechanicallyIntactTriangles());
                Set(tool, "operationMayRevealBack", true);
                Set(tool, "brushRadius", radius);
                Set(tool, "workingVertices", new Vector3[baseline.Length]);
                BuildSafe(tool, Vector3.up * 0.25f);
                var result = (Vector3[])typeof(DeformableImageInteractor).GetField("workingVertices", Private).GetValue(tool);
                float centreMove = result[centre].y - baseline[centre].y;
                float shoulderMove = result[shoulder].y - baseline[shoulder].y;
                Require(centreMove > 0.16f, "Upper-lid centre opens");
                float legacyShoulder = 0.25f * mesh.CopyToolStretchStrengths()[shoulder] * Mathf.Pow(1 - distances[shoulder] / radius, 2);
                Require(shoulderMove > legacyShoulder * 1.08f, "Near-centre lid remains broader than the original sharp kernel");
                float cornerMove = result[corner].y - baseline[corner].y;
                Require(cornerMove > centreMove * 0.005f && cornerMove < centreMove * 0.25f, "Eye corner follows weakly with nonzero displacement");
                float browMove = result[brow].y - baseline[brow].y;
                Require(browMove > centreMove * 0.1f && browMove < centreMove * 0.3f, "Distant response follows the brush curve");
                Require((result[lower] - baseline[lower]).sqrMagnitude < 1e-12f, "Force does not jump through incision to lower lid");
                var anchors = mesh.CopyOriginalOutlineAnchors();
                for (int i = 0; i < anchors.Length; i++) if (anchors[i]) Require(result[i] == baseline[i], "Original outline remains fixed");
                mesh.SetManualVertices(result);
                var heldStrengths = mesh.CopyManualCornerStrengths();
                for (int i = 0; i < strengths.Length; i++) Require(strengths[i] == heldStrengths[i], "Resistance stays on material after deformation");
                Invoke(tool, "CancelOperation");
                var restored = mesh.CopyCurrentVertices();
                for (int i = 0; i < baseline.Length; i++) Require((restored[i] - baseline[i]).sqrMagnitude < 1e-12f, "Cancellation restores exact baseline");

                // Requested rollback: incision mode must match the weighted candidate,
                // without the additional orientation/compression limiter.
                Set(tool, "operationStartVertices", baseline);
                Set(tool, "operationStartLocalPoint", baseline[centre]);
                Set(tool, "operationOutlineAnchors", anchors);
                Set(tool, "operationFeatureStrengths", mesh.CopyToolStretchStrengths());
                Set(tool, "operationCornerStrengths", strengths);
                Set(tool, "operationGeodesicDistances", distances);
                Set(tool, "operationTriangles", mesh.CopyMechanicallyIntactTriangles());
                Set(tool, "operationMayRevealBack", true);
                Set(tool, "workingVertices", new Vector3[baseline.Length]);
                foreach (var direction in new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right, new Vector3(1, 1).normalized })
                {
                    foreach (float amount in new[] { 0.1f, 0.5f, 2f, 100f })
                    {
                        BuildSafe(tool, direction * amount);
                        result = (Vector3[])Working(tool).Clone();
                        typeof(DeformableImageInteractor).GetMethod("BuildWorkingVertices", Private)
                            .Invoke(tool, new object[] { direction * amount, 0f, 1f });
                        var raw = Working(tool);
                        for (int i = 0; i < raw.Length; i++)
                            Require((result[i] - raw[i]).sqrMagnitude < 1e-12f, "Incision drag is not reduced by the withdrawn geometry limiter");
                        Require(Vector3.Distance(result[centre], baseline[centre]) > 0.001f, "Incision still permits a useful nonzero pull");
                        Require(Vector3.Distance(result[brow], baseline[brow]) < Vector3.Distance(result[centre], baseline[centre]) * 0.3f, "Restored far field remains weaker than the grip");
                        Require((result[lower] - baseline[lower]).sqrMagnitude < 1e-12f, "Large pulls do not cross the incision");
                        for (int i = 0; i < anchors.Length; i++) if (anchors[i])
                            Require(result[i] == baseline[i], "Outer anchors survive rollback");
                    }
                }
                Set(tool, "operationMayRevealBack", false);
                Set(tool, "operationDragWeights", null);
                float horizontal = 0f;
                for (int step = 0; step < 8; step++)
                {
                    float angle = step * Mathf.PI / 4f;
                    Vector3 direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle));
                    BuildSafe(tool, direction * 100f);
                    CheckInteriorOrientation(mesh, baseline, Working(tool), mesh.CopyMechanicallyIntactTriangles());
                    float amount = Vector2.Distance(Working(tool)[centre], baseline[centre]);
                    if (step == 0) horizontal = amount;
                    Require(amount > 0.02f && Mathf.Abs(amount - horizontal) < 1e-5f,
                        "Interior drag remains mobile and has equal travel in all directions: " + amount);
                }
                // A change from another pose writer survives both moving the mouse
                // again and cancelling this gesture.
                Set(tool, "previousDragPose", (Vector3[])baseline.Clone());
                Set(tool, "dragFramePositions", new Vector3[baseline.Length]);
                BuildSafe(tool, Vector3.up * 0.06f);
                Invoke(tool, "ApplyManualDragDelta");
                var animated = mesh.CopyCurrentVertices();
                Vector3 externalDelta = Vector3.right * 0.013f;
                animated[centre] += externalDelta;
                mesh.SetLogicalVertices(animated);
                BuildSafe(tool, Vector3.down * 0.04f);
                Invoke(tool, "ApplyManualDragDelta");
                Require((mesh.GetLogicalVertex(centre) - Working(tool)[centre] - externalDelta).sqrMagnitude < 1e-10f,
                    "Manual drag adds its change without overwriting an expression");
                Invoke(tool, "CancelOperation");
                Require((mesh.GetLogicalVertex(centre) - baseline[centre] - externalDelta).sqrMagnitude < 1e-10f,
                    "Cancelling removes only the hand's displacement");

                mesh.SetLogicalVertices(baseline);
                int interior = Nearest(uvs, new Vector2(0.34375f, 0.6875f));
                Require(!mesh.IsNearCutBoundary(baseline[interior], 0.2f)
                    && mesh.TryBuildGeodesicDistances(baseline[interior], radius, out _), "Choose a real interior grab just outside incision mode");
                mesh.TryBuildGeodesicDistances(baseline[interior], radius, out distances);
                Set(tool, "operationStartVertices", baseline);
                Set(tool, "operationStartLocalPoint", baseline[interior]);
                Set(tool, "operationOutlineAnchors", anchors);
                Set(tool, "operationCornerStrengths", strengths);
                Set(tool, "operationGeodesicDistances", distances);
                Set(tool, "operationTriangles", mesh.CopyMechanicallyIntactTriangles());
                Set(tool, "workingVertices", new Vector3[baseline.Length]);
                BuildSafe(tool, Vector3.up * 0.4f);
                float interiorTravel = Vector2.Distance(Working(tool)[interior], baseline[interior]);
                BuildSafe(tool, Vector3.down * 0.4f);
                Require(interiorTravel > 0.04f && Mathf.Abs(Vector2.Distance(Working(tool)[interior], baseline[interior]) - interiorTravel) < 1e-5f,
                    "Dragging just beyond the cut range works in both directions: " + interiorTravel);
                File.WriteAllText("ManualDragDirectionValidation.txt", "PASS: eight directions on a thin cut-mesh stress case move "
                    + horizontal.ToString("F4") + " local units equally without inverted interior triangles. "
                    + "Ordinary grab beyond the incision range moves " + interiorTravel.ToString("F4") + " equally up and down. External pose and cancel checked.");
                CheckMaterialDragAndDisplay(tool, mesh, baseline);
                CheckCurvedEyeCut(tool, mesh, curves);
            }
            finally
            {
                if (face != null) UnityEngine.Object.DestroyImmediate(face);
                if (sprite != null) UnityEngine.Object.DestroyImmediate(sprite);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void CheckCurvedEyeCut(DeformableImageInteractor tool, DeformableImageMesh mesh, FacialFeaturePrecut2D curves)
        {
            var points = curves.GetEditableCurve(0).ControlPoints;
            points.Clear();
            points.AddRange(new[]
            {
                new Vector2(0.32436338f, 0.5821309f), new Vector2(0.35131478f, 0.5731249f),
                new Vector2(0.38367984f, 0.5720489f), new Vector2(0.4099308f, 0.57633746f),
                new Vector2(0.44074526f, 0.582877f), new Vector2(0.4539108f, 0.5837037f)
            });
            var path = curves.GetSampledEditableCurve(0);
            Require(mesh.BakeOpenCutTopology(new List<IList<Vector2>> { path }, 0.005f), "Bake a narrow curved eye cut");
            var baseline = mesh.CopyCurrentVertices();
            var indices = mesh.CopyLogicalTriangles();
            var uvs = mesh.CopyNormalizedRestUvs();
            Vector2 middle = path[path.Count / 2];
            Vector2 tip = path[0];
            foreach (float side in new[] { -1f, 1f })
            {
                int nearLip = Nearest(uvs, middle + Vector2.up * (side * 0.001f));
                int farLip = Nearest(uvs, middle - Vector2.up * (side * 0.001f));
                int corner = Nearest(uvs, tip);
                Set(tool, "workingVertices", new Vector3[baseline.Length]);
                PrepareRayGrab(tool, mesh, middle + Vector2.up * (side * 0.012f));
                BuildSafe(tool, Vector3.up * side);
                var pulled = Working(tool);
                Require(Vector3.Distance(pulled[nearLip], baseline[nearLip]) > 0.12f, "Grabbed eye lip follows the drag");
                Require(pulled[farLip] == baseline[farLip], "Opposite central eye lip stays separated in either drag direction");
                float cornerTravel = Vector3.Distance(pulled[corner], baseline[corner]);
                Require(cornerTravel > 0.001f && cornerTravel < 0.015f, "Shared eye corner follows very slightly");
                CheckOppositeLipFollow(mesh, path, baseline, pulled, side);

                PrepareRayGrab(tool, mesh, path[path.Count - 6] + Vector2.up * (side * 0.002f));
                BuildSafe(tool, Vector3.up * side);
                pulled = Working(tool);
                for (int i = 0; i < indices.Length; i++)
                {
                    int a = indices[i], b = indices[i - i % 3 + (i + 1) % 3];
                    float edgeLength = Vector2.Distance(baseline[b], baseline[a]);
                    if (edgeLength < 0.00002f) continue;
                    Vector3 relativeMovement = (pulled[b] - baseline[b]) - (pulled[a] - baseline[a]);
                    Require(relativeMovement.magnitude < edgeLength * 3f,
                        "Intact edges around the curved incision and tip move continuously without stretched bridges");
                }
            }
        }

        private static void CheckOppositeLipFollow(DeformableImageMesh mesh, List<Vector2> path,
            Vector3[] baseline, Vector3[] pulled, float dragSide)
        {
            float start = path[0].x, end = path[path.Count - 1].x;
            const float lipOffset = 0.0015f;
            foreach (bool fromLeft in new[] { true, false })
            {
                float cornerTravel = SampleTravel(0.02f);
                Require(cornerTravel > 0.001f && cornerTravel < 0.015f, "Opposite lip follows very slightly at the eye corner");
                Require(SampleTravel(0.06f) < cornerTravel * 0.65f, "Opposite corner movement falls off quickly");
                foreach (float distanceFromTip in new[] { 0.14f, 0.26f, 0.38f, 0.5f })
                {
                    float travel = SampleTravel(distanceFromTip);
                    Require(travel < 0.000001f, $"Opposite eyelid stays still at {distanceFromTip:P0} from its tip, travel {travel:F7}");
                }

                float SampleTravel(float distanceFromTip)
                {
                    float x = Mathf.Lerp(start, end, fromLeft ? distanceFromTip : 1f - distanceFromTip);
                    int next = path.FindIndex(point => point.x >= x);
                    Vector2 a = path[next - 1], b = path[next];
                    float y = Mathf.Lerp(a.y, b.y, Mathf.InverseLerp(a.x, b.x, x));
                    var sample = new Vector2(x, y - dragSide * lipOffset);
                    Require(mesh.TryGetCurrentSurfacePoint(sample, out Vector3 rest), "Sample opposite curved lip");
                    mesh.SetLogicalVertices(pulled);
                    Require(mesh.TryGetCurrentSurfacePoint(sample, out Vector3 current), "Sample dragged opposite curved lip");
                    mesh.SetLogicalVertices(baseline);
                    return Vector3.Distance(rest, current);
                }
            }
        }

        private static void CheckMaterialDragAndDisplay(DeformableImageInteractor tool, DeformableImageMesh mesh, Vector3[] baseline)
        {
            var probe = new Vector2(0.3417f, 0.5493f);
            Require(mesh.TryGetCurrentSurfacePoint(probe, out Vector3 originalPoint), "Normal cheek sample has surface");
            Vector3[] squeezed = (Vector3[])baseline.Clone();
            for (int i = 0; i < squeezed.Length; i++)
            {
                squeezed[i].y = originalPoint.y + (squeezed[i].y - originalPoint.y) * 0.13f;
                squeezed[i].z -= 0.12f;
            }

            float neutral = PrepareRayGrab(tool, mesh, probe);
            var expectedWeights = (float[])Get(tool, "operationDragWeights");
            mesh.SetLogicalVertices(squeezed);
            float posed = PrepareRayGrab(tool, mesh, probe);
            var posedWeights = (float[])Get(tool, "operationDragWeights");
            Require(Mathf.Abs(neutral - posed) < 0.0001f, "Same material cheek keeps its travel after pose compression");
            for (int i = 0; i < posedWeights.Length; i++)
                Require(Mathf.Abs(posedWeights[i] - expectedWeights[i]) < 0.0001f, "Same material brush keeps its full field");

            mesh.SetLogicalVertices(baseline);
            Set(tool, "operationActive", true);
            Invoke(tool, "ToggleDragWeightDisplay");
            var renderer = mesh.GetComponent<MeshRenderer>();
            var properties = new MaterialPropertyBlock();
            var meshColors = mesh.GetComponent<MeshFilter>().sharedMesh.colors32;
            for (int i = 0; i < posedWeights.Length; i++)
                Require(meshColors[i].r == Mathf.RoundToInt(posedWeights[i] * 255f)
                    && meshColors[i].r == meshColors[i + posedWeights.Length].r, "Displayed weights match both sides of the actual brush");
            renderer.GetPropertyBlock(properties, 0);
            Require(properties.GetFloat("_ShowDragWeights") == 1f, "Weight display activates");
            Invoke(tool, "ClearOperationState");
            int cheek = Nearest(mesh.CopyNormalizedRestUvs(), probe);
            Require(mesh.GetComponent<MeshFilter>().sharedMesh.colors32[cheek].r == 255,
                "Releasing restores the anatomical weight view");
            Invoke(tool, "ToggleDragWeightDisplay");
            renderer.GetPropertyBlock(properties, 0);
            Require(properties.GetFloat("_ShowDragWeights") == 0f, "Weight display toggles off");
            File.WriteAllText("ManualDragMaterialValidation.txt", "PASS: identical cheek grip weights and travel limits before/after compression; limit="
                + neutral.ToString("F4") + ". Active and idle display, both sides, and off toggle checked.");
        }

        private static float PrepareRayGrab(DeformableImageInteractor tool, DeformableImageMesh mesh, Vector2 uv)
        {
            Require(mesh.TryGetCurrentSurfacePoint(uv, out Vector3 surface), "Resolve current point of same material");
            var ray = new Ray(new Vector3(surface.x, surface.y, -10f), Vector3.forward);
            object[] pick = { ray, null };
            Require((bool)typeof(DeformableImageMesh).GetMethod("TryPickManualDragSurface", Private).Invoke(mesh, pick), "Pick visible cheek by ray");
            Set(tool, "operationStartVertices", mesh.CopyCurrentVertices());
            Set(tool, "operationOutlineAnchors", mesh.CopyOriginalOutlineAnchors());
            Set(tool, "operationCornerStrengths", mesh.CopyManualCornerStrengths());
            Set(tool, "operationTriangles", mesh.CopyMechanicallyIntactTriangles());
            typeof(DeformableImageInteractor).GetMethod("PrepareManualDragMaterial", Private).Invoke(tool, new[] { pick[1] });
            Invoke(tool, "PrepareManualDrag");
            return (float)Get(tool, "operationDragLimit");
        }

        private static object Get(object target, string field)
        {
            return target.GetType().GetField(field, Private).GetValue(target);
        }

        private static void SetEye(FacialFeaturePrecut2D curves, int index, float start, float end)
        {
            var points = curves.GetEditableCurve(index).ControlPoints;
            points.Clear(); points.Add(new Vector2(start, 0.625f)); points.Add(new Vector2(end, 0.625f));
        }
        private static int Nearest(Vector2[] uvs, Vector2 point)
        {
            int best = 0; float distance = float.PositiveInfinity;
            for (int i = 0; i < uvs.Length; i++) { float d = (uvs[i] - point).sqrMagnitude; if (d < distance) { distance = d; best = i; } }
            return best;
        }
        private static void BuildSafe(DeformableImageInteractor tool, Vector3 delta)
        {
            typeof(DeformableImageInteractor).GetMethod("BuildSafeWorkingVertices", Private).Invoke(tool, new object[] { delta, 0f });
        }
        private static Vector3[] Working(DeformableImageInteractor tool)
        {
            return (Vector3[])typeof(DeformableImageInteractor).GetField("workingVertices", Private).GetValue(tool);
        }
        private static void CheckInteriorOrientation(DeformableImageMesh mesh, Vector3[] baseline, Vector3[] current, int[] triangles)
        {
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
                Vector2 a = baseline[ib] - baseline[ia], b = baseline[ic] - baseline[ia];
                Vector2 p = current[ib] - current[ia], q = current[ic] - current[ia];
                float oldArea = a.x * b.y - a.y * b.x;
                if (Mathf.Abs(oldArea) < 1e-7f) continue;
                float newArea = p.x * q.y - p.y * q.x;
                if (newArea / oldArea >= 0.0199f) continue;
                float longestSquared = Mathf.Max(a.sqrMagnitude, Mathf.Max(b.sqrMagnitude, (b - a).sqrMagnitude));
                Vector3 center = (baseline[ia] + baseline[ib] + baseline[ic]) / 3f;
                Require(Mathf.Abs(oldArea) <= longestSquared * 0.12f && mesh.IsNearCutBoundary(center, 0.055f)
                    && Mathf.Abs(newArea) < longestSquared * 0.10f,
                    "Only small clipped strips immediately beside the cut may alter winding");
            }
        }
        private static void Set(object target, string field, object value) { target.GetType().GetField(field, Private).SetValue(target, value); }
        private static void Invoke(object target, string method) { target.GetType().GetMethod(method, Private).Invoke(target, null); }
        private static void Require(bool ok, string label) { if (!ok) throw new Exception(label); }
    }
}
#endif
