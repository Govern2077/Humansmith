#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeformableImage.Editor
{
    public static class DragDepthRenderingValidation
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [MenuItem("Tools/Face/Validate Drag Depth Rendering")]
        public static void RunBatch()
        {
            try
            {
                Require(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "GPU rendering is required for this validation");
                CheckDrag();
                string eyelidReport = CheckLowerEyelidDrag();
                CheckRendering();
                File.WriteAllText("LowerEyelidDragValidation.txt", eyelidReport);
                File.WriteAllText("DragDepthRenderingValidation.txt", "PASS: GPU front/back overlap at equal and distinct depths; genuinely nearer back and isolated flip remain visible; transparent cut coverage; reduced 0.3-radius incision limit; plain drag/depth restored without automatic curl; depth axis/zero input/hold/cancel. Device: " + SystemInfo.graphicsDeviceName);
                CutPreviewValidation.RunBatch();
            }
            catch (Exception error)
            {
                File.WriteAllText("DragDepthRenderingValidation.txt", "FAIL: " + error);
                Debug.LogException(error); EditorApplication.Exit(1);
            }
        }

        private static void CheckDrag()
        {
            var objects = new List<UnityEngine.Object>();
            try
            {
                var texture = Solid(Color.white); objects.Add(texture);
                var sprite = Sprite.Create(texture, new Rect(0, 0, 32, 32), Vector2.one * 0.5f); objects.Add(sprite);
                var go = new GameObject("Drag depth fixture") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go);
                var skin = go.AddComponent<DeformableImageMesh>();
                Set(skin, "subdivisionsX", 64); Set(skin, "subdivisionsY", 64); skin.ConfigureSprites(sprite, sprite);
                Require(skin.ApplyOpenCut(new List<Vector2> { new Vector2(0.3f, 0.5f), new Vector2(0.7f, 0.5f) }, 0.18f), "Fixture has a physical incision");
                var tool = go.AddComponent<DeformableImageInteractor>(); Invoke(tool, "Awake");
                var baseline = skin.CopyCurrentVertices(); var uvs = skin.CopyNormalizedRestUvs();
                int centre = 0; for (int i = 1; i < uvs.Length; i++)
                    if ((uvs[i] - new Vector2(0.5f, 0.518f)).sqrMagnitude < (uvs[centre] - new Vector2(0.5f, 0.518f)).sqrMagnitude) centre = i;
                skin.TryBuildGeodesicDistances(baseline[centre], 1f, out float[] distances);
                Set(tool, "brushRadius", 1f); Set(tool, "operationStartLocalPoint", baseline[centre]);
                Set(tool, "operationStartVertices", baseline); Set(tool, "workingVertices", new Vector3[baseline.Length]);
                Set(tool, "operationGeodesicDistances", distances); Set(tool, "operationOutlineAnchors", skin.CopyOriginalOutlineAnchors());
                Set(tool, "operationTriangles", skin.CopyMechanicallyIntactTriangles()); Set(tool, "operationMayRevealBack", true);
                Invoke(tool, "BuildSafeWorkingVertices", Vector3.zero, 0f);
                var zero = Working(tool); for (int i = 0; i < zero.Length; i++) Require(zero[i] == baseline[i], "Zero drag gives no lift");
                Invoke(tool, "BuildSafeWorkingVertices", Vector3.up, 0f);
                var result = (Vector3[])Working(tool).Clone();
                Require(Mathf.Abs(Vector3.Distance(result[centre], baseline[centre]) - 0.3f) < 1e-5f,
                    "Incision grip reaches the halved travel limit");
                Require(result[centre].z < baseline[centre].z - 0.03f, "Drag lifts surface toward front camera");
                var anchors = skin.CopyOriginalOutlineAnchors();
                for (int i = 0; i < result.Length; i++)
                {
                    Require(Vector3.Distance(result[i], baseline[i]) <= 0.30001f, "Reduced incision displacement includes depth");
                    Vector3 delta = result[i] - baseline[i];
                    Require(Mathf.Abs(delta.x) < 1e-6f && delta.y >= -1e-6f
                        && Mathf.Abs(delta.z + delta.y * 0.12f) < 1e-5f, "Near-cut response is plain dragging and base lift, without automatic rotation");
                    if (anchors[i]) Require(result[i] == baseline[i], "Original outline stays anchored in 3D");
                }
                Invoke(tool, "BuildSafeWorkingVertices", Vector3.up, 0f);
                for (int i = 0; i < result.Length; i++) Require(result[i] == Working(tool)[i], "Held drag does not accumulate depth");
                skin.SetManualVertices(result); Invoke(tool, "CancelOperation");
                var cancelled = skin.CopyCurrentVertices();
                for (int i = 0; i < cancelled.Length; i++) Require((cancelled[i] - baseline[i]).sqrMagnitude < 1e-12f, "Cancel restores depth and planar shape");
                var cameraGo = new GameObject("Depth axis camera") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(cameraGo);
                var camera = cameraGo.AddComponent<Camera>(); camera.enabled = false; camera.orthographic = true;
                go.transform.rotation = Quaternion.Euler(90, 0, 0);
                camera.transform.rotation = Quaternion.Euler(90, 0, 0); camera.transform.position = Vector3.up * 5;
                Set(tool, "targetCamera", camera);
                float sign = (float)Invoke(tool, "ManualDragDepthSign", Vector3.zero);
                Require((go.transform.TransformVector(Vector3.forward * sign)).y > 0.99f, "Facetest local Z lift maps to world Y toward camera");
                camera.transform.rotation = Quaternion.Euler(-90, 0, 0);
                Require((float)Invoke(tool, "ManualDragDepthSign", Vector3.zero) > 0f, "Depth direction also handles rear camera");
            }
            finally { for (int i = objects.Count - 1; i >= 0; i--) if (objects[i] != null) UnityEngine.Object.DestroyImmediate(objects[i]); }
        }

        private static string CheckLowerEyelidDrag()
        {
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            var pixels = new Color32[256 * 256];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            texture.SetPixels32(pixels);
            texture.Apply();
            var sprite = Sprite.Create(texture, new Rect(0, 0, 256, 256), Vector2.one * 0.5f);
            var face = new GameObject("Baked lower eyelid validation") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var skin = face.AddComponent<DeformableImageMesh>();
                Set(skin, "subdivisionsX", 64);
                Set(skin, "subdivisionsY", 64);
                skin.ConfigureSprites(sprite, sprite);
                var features = face.AddComponent<FacialFeaturePrecut2D>();
                features.Configure(skin, null, null);
                for (int eye = 0; eye < 2; eye++)
                {
                    var points = features.GetEditableCurve(eye).ControlPoints;
                    points.Clear();
                    points.Add(new Vector2(eye == 0 ? 0.23f : 0.56f, 0.6f));
                    points.Add(new Vector2(eye == 0 ? 0.44f : 0.77f, 0.6f));
                }
                var tool = face.AddComponent<DeformableImageInteractor>();
                Invoke(tool, "Awake");

                skin.TryGetAuthoringSurfacePoint(new Vector2(0.335f, 0.596f), out Vector3 left);
                skin.TryGetAuthoringSurfacePoint(new Vector2(0.665f, 0.596f), out Vector3 right);
                Require(FoldAt(tool, skin, left) == null, "An uncut eye guide cannot start a fold");
                var paths = new List<IList<Vector2>>
                {
                    features.GetSampledEditableCurve(0), features.GetSampledEditableCurve(1),
                    new[] { new Vector2(0.23f, 0.46f), new Vector2(0.44f, 0.46f) }
                };
                Require(skin.BakeOpenCutTopology(paths, 0.025f), "Bake both eyes and an unrelated cut");
                skin.RebuildMesh();
                object leftFold = FoldAt(tool, skin, left);
                object rightFold = FoldAt(tool, skin, right);
                Require(leftFold != null && rightFold != null, "Both baked lower lips select an eyelid response");
                skin.TryGetAuthoringSurfacePoint(new Vector2(0.335f, 0.572f), out Vector3 farther);
                skin.TryGetAuthoringSurfacePoint(new Vector2(0.335f, 0.61f), out Vector3 upper);
                skin.TryGetAuthoringSurfacePoint(new Vector2(0.335f, 0.452f), out Vector3 otherCut);
                Require(FoldAt(tool, skin, upper) == null && FoldAt(tool, skin, otherCut) == null, "Upper lip and unrelated cuts use ordinary dragging");
                object fartherFold = FoldAt(tool, skin, farther);
                Require(fartherFold != null, "Cheek-side grab stays in the response strip");
                Require(Pull(tool, skin, left, Vector3.down * 0.045f), "Left lower lip starts at the low threshold");
                Require(!Pull(tool, skin, right, Vector3.down * 0.045f), "Right lower lip has an independently higher threshold");
                Require(Pull(tool, skin, right, Vector3.down * 0.07f), "Right lower lip starts at its threshold");
                Require(!Pull(tool, skin, farther, Vector3.down * 0.07f), "Grabbing farther from the lip raises the threshold");
                Require(!Pull(tool, skin, left, Vector3.up * 0.5f), "Upward drag does not fold the lower lid");
                var upward = (Vector3[])Working(tool).Clone();
                Set(tool, "operationLowerEyelidDrag", null);
                Set(tool, "operationDragWeights", null);
                Invoke(tool, "BuildSafeWorkingVertices", Vector3.up * 0.5f, 0f);
                for (int i = 0; i < upward.Length; i++)
                    Require((upward[i] - Working(tool)[i]).sqrMagnitude < 1e-12f, "Upward drag matches ordinary response");
                Require(!Pull(tool, skin, left, Vector3.right * 0.5f), "Tangential drag does not fold the lower lid");
                Require(Pull(tool, skin, left, Vector3.down * 0.4f), "Strong downward pull bends the lower lip");
                var before = skin.CopyCurrentVertices();
                var current = Working(tool);
                var uv = skin.CopyNormalizedRestUvs();
                var triangles = skin.CopyLogicalTriangles();
                int flipped = 0;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                    if (Mathf.Max(uv[a].y, uv[b].y, uv[c].y) < 0.6f
                        && Area(before[a], before[b], before[c]) * Area(current[a], current[b], current[c]) < -1e-9f)
                        flipped++;
                }
                Require(flipped > 0, "Lower lip produces actual back-facing triangles");
                for (int i = 0; i < current.Length; i++)
                {
                    Require(Vector3.Distance(current[i], before[i]) <= 0.22501f, "Fold obeys 0.3 times brush-radius cap");
                    if (uv[i].y > 0.6f) Require(current[i] == before[i], "Upper lip stays still during downward fold");
                }
                int backPixelsBefore = CountVisibleEyelidBack(skin, left);
                skin.SetManualVertices(current);
                int backPixelsAfter = CountVisibleEyelidBack(skin, left);
                Require(backPixelsBefore == 0 && backPixelsAfter > 10, "Lower eyelid fold visibly exposes its back in the production shader");
                Invoke(tool, "CancelOperation");
                var restored = skin.CopyCurrentVertices();
                for (int i = 0; i < restored.Length; i++)
                    Require(restored[i] == before[i], "Cancel restores the unfolded baseline");
                return "PASS: real baked eye cuts only; independent left/right thresholds; threshold rises away from lip; upward/tangential controls; "
                    + flipped + " flipped lower-lip triangles, " + backPixelsAfter + " visible back pixels; upper lip isolated, 0.3-radius cap and cancel restore.";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(face);
                UnityEngine.Object.DestroyImmediate(sprite);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static int CountVisibleEyelidBack(DeformableImageMesh skin, Vector3 lip)
        {
            const int resolution = 128;
            var renderer = skin.GetComponent<MeshRenderer>();
            var originalFront = new MaterialPropertyBlock();
            var originalBack = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(originalFront, 0);
            renderer.GetPropertyBlock(originalBack, 1);
            var back = Solid(Color.red);
            var target = new RenderTexture(resolution, resolution, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            var cameraObject = new GameObject("Lower eyelid render camera") { hideFlags = HideFlags.HideAndDontSave };
            var previous = RenderTexture.active;
            int originalLayer = skin.gameObject.layer;
            try
            {
                skin.gameObject.layer = 31;
                var properties = new MaterialPropertyBlock();
                for (int side = 0; side < 2; side++)
                {
                    renderer.GetPropertyBlock(properties, side);
                    properties.SetTexture("_MainTex", side == 0 ? Texture2D.whiteTexture : back);
                    properties.SetFloat("_AmbientStrength", 1f);
                    properties.SetFloat("_DiffuseStrength", 0f);
                    properties.SetFloat("_SpecularStrength", 0f);
                    renderer.SetPropertyBlock(properties, side);
                }
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.cullingMask = 1 << 31;
                camera.orthographic = true;
                camera.orthographicSize = 0.4f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.blue;
                camera.transform.position = new Vector3(lip.x, lip.y - 0.15f, -5f);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                int backPixelCount = 0;
                foreach (Color32 color in pixels.GetPixels32())
                    if (color.r > 100 && color.g < 30 && color.b < 30) backPixelCount++;
                return backPixelCount;
            }
            finally
            {
                RenderTexture.active = previous;
                renderer.SetPropertyBlock(originalFront, 0);
                renderer.SetPropertyBlock(originalBack, 1);
                skin.gameObject.layer = originalLayer;
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(pixels);
                UnityEngine.Object.DestroyImmediate(back);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static object FoldAt(DeformableImageInteractor tool, DeformableImageMesh skin, Vector3 point)
        {
            skin.TryBuildGeodesicDistances(point, 0.75f, out float[] distances);
            Set(tool, "operationGeodesicDistances", distances);
            return Invoke(skin, "CreateLowerEyelidDrag", point, distances);
        }

        private static bool Pull(DeformableImageInteractor tool, DeformableImageMesh skin, Vector3 point, Vector3 input)
        {
            var start = skin.CopyCurrentVertices();
            Set(tool, "operationStartLocalPoint", point);
            Set(tool, "operationStartVertices", start);
            Set(tool, "operationOutlineAnchors", skin.CopyOriginalOutlineAnchors());
            Set(tool, "operationCornerStrengths", skin.CopyManualCornerStrengths());
            Set(tool, "operationDragWeights", null);
            Set(tool, "operationTriangles", skin.CopyMechanicallyIntactTriangles());
            Set(tool, "workingVertices", new Vector3[start.Length]);
            Set(tool, "operationMayRevealBack", skin.IsNearCutBoundary(point, 0.2f));
            Set(tool, "operationLowerEyelidDrag", FoldAt(tool, skin, point));
            return (bool)Invoke(tool, "BuildWorkingVertices", input, 0f, 1f);
        }

        private static void CheckRendering()
        {
            Shader shader = Shader.Find("Deformable Image/Unlit Transparent");
            Require(shader != null && !ShaderUtil.ShaderHasError(shader), "Production shader compiles on actual graphics device");
            Shader baseline = Shader.Find("Deformable Image/Drag Depth Baseline");
            if (baseline != null) Render(baseline, 0f, true, false, "DragOverlap-Baseline.png");
            Color fixedOverlap = Render(shader, 0f, true, false, "DragOverlap-Fixed.png");
            Require(fixedOverlap.g > 0.4f && fixedOverlap.r < 0.1f, "Two fronts plus equal-depth back resolve to front");
            Color nearerBack = Render(shader, -0.1f, true, false, "DragOverlap-NearerBack.png");
            Require(nearerBack.r > 0.4f && nearerBack.g < 0.1f, "A physically nearer flipped back remains visible");
            Color isolatedBack = Render(shader, 0f, false, false, "DragOverlap-IsolatedBack.png");
            Require(isolatedBack.r > 0.4f && isolatedBack.g < 0.1f, "Isolated turned-over flap still shows its back");
            Color hole = Render(shader, 0f, true, true, "DragOverlap-TransparentCut.png");
            Require(hole.b > 0.4f && hole.r < 0.1f && hole.g < 0.1f, "Transparent cuts write no occluding depth");
            Color low = Render(shader, 0f, true, false, "DragWeight-Low.png", 0f);
            Color high = Render(shader, 0f, true, false, "DragWeight-High.png", 1f);
            Require(low.b > 0.9f && low.r < 0.2f, "Zero drag weight renders blue");
            Require(high.r > 0.9f && high.b < 0.2f, "Full drag weight renders red");
            Color heatmapHole = Render(shader, 0f, true, true, "DragWeight-Cut.png", 1f);
            Require(heatmapHole.b > 0.9f && heatmapHole.r < 0.2f, "Transparent cut stays transparent with the red weight overlay");
        }

        private static Color Render(Shader shader, float backDepth, bool includeFront, bool transparentCut, string file, float weight = -1f)
        {
            var go = new GameObject("GPU overlap fixture") { hideFlags = HideFlags.HideAndDontSave, layer = 31 };
            var cameraGo = new GameObject("GPU fixture camera") { hideFlags = HideFlags.HideAndDontSave };
            Mesh mesh = null; Material material = null; RenderTexture target = null; Texture2D readback = null, front = null, back = null;
            try
            {
                front = Solid(Color.green); back = Solid(Color.red);
                mesh = new Mesh(); var vertices = new Vector3[18];
                for (int layer = 0; layer < 3; layer++)
                {
                    float z = layer == 0 ? 0f : layer == 1 ? 0.04f : backDepth;
                    vertices[layer * 3] = new Vector3(-2, -2, z); vertices[layer * 3 + 1] = new Vector3(0, 2, z); vertices[layer * 3 + 2] = new Vector3(2, -2, z);
                }
                Array.Copy(vertices, 0, vertices, 9, 9);
                mesh.vertices = vertices; var uv = new Vector2[18]; for (int i = 0; i < uv.Length; i++) uv[i] = Vector2.one * 0.5f;
                if (weight >= 0f)
                {
                    var colors = new Color32[vertices.Length];
                    for (int i = 0; i < colors.Length; i++) colors[i] = new Color32((byte)(weight * 255f), 0, 0, 255);
                    mesh.colors32 = colors;
                }
                mesh.uv = uv; mesh.uv2 = uv; mesh.subMeshCount = 2;
                mesh.SetTriangles(includeFront ? new[] { 0, 1, 2, 3, 4, 5, 6, 8, 7 } : new[] { 6, 8, 7 }, 0);
                mesh.SetTriangles(includeFront ? new[] { 9, 11, 10, 12, 14, 13, 15, 16, 17 } : new[] { 15, 16, 17 }, 1);
                mesh.RecalculateNormals(); go.AddComponent<MeshFilter>().sharedMesh = mesh;
                material = new Material(shader); var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterials = new[] { material, material };
                for (int side = 0; side < 2; side++)
                {
                    var block = new MaterialPropertyBlock(); block.SetTexture("_MainTex", side == 0 ? front : back);
                    block.SetTexture("_CutMask", transparentCut ? Texture2D.blackTexture : Texture2D.whiteTexture);
                    block.SetFloat("_AmbientStrength", 1f); block.SetFloat("_DiffuseStrength", 0f); block.SetFloat("_SpecularStrength", 0f);
                    block.SetColor("_Color", Color.white); renderer.SetPropertyBlock(block, side);
                    if (weight >= 0f)
                    {
                        block.SetFloat("_ShowDragWeights", 1f);
                        renderer.SetPropertyBlock(block, side);
                    }
                }
                target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32); target.Create();
                var camera = cameraGo.AddComponent<Camera>(); camera.enabled = false; camera.cullingMask = 1 << 31;
                camera.orthographic = true; camera.orthographicSize = 1f; camera.nearClipPlane = 0.1f; camera.farClipPlane = 20f;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.blue;
                camera.transform.position = new Vector3(0, 0, -5); camera.targetTexture = target; camera.Render();
                readback = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                var previous = RenderTexture.active;
                try { RenderTexture.active = target; readback.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); readback.Apply(); }
                finally { RenderTexture.active = previous; }
                File.WriteAllBytes(file, readback.EncodeToPNG()); return readback.GetPixel(32, 32);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo); UnityEngine.Object.DestroyImmediate(go);
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                foreach (var item in new UnityEngine.Object[] { mesh, material, readback, front, back }) if (item != null) UnityEngine.Object.DestroyImmediate(item);
            }
        }
        private static Texture2D Solid(Color color) { var texture = new Texture2D(32, 32); var pixels = new Color[1024]; for (int i = 0; i < pixels.Length; i++) pixels[i] = color; texture.SetPixels(pixels); texture.Apply(); return texture; }
        private static float Area(Vector3 a, Vector3 b, Vector3 c) { return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x); }
        private static Vector3[] Working(object tool) { return (Vector3[])tool.GetType().GetField("workingVertices", Private).GetValue(tool); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Private).SetValue(target, value); }
        private static object Invoke(object target, string name, params object[] values) { return target.GetType().GetMethod(name, Private).Invoke(target, values); }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
#endif
