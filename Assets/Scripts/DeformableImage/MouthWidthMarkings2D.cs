using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeformableImage
{
    /// <summary>Reversible mouth-width design preview. Does not modify the rest pose or cut topology.</summary>
    [DefaultExecutionOrder(500)]
    [DisallowMultipleComponent]
    public sealed class MouthWidthMarkings2D : MonoBehaviour
    {
        [SerializeField] private FacialFeaturePrecut2D facialCurves;
        [SerializeField, Range(0.75f, 1.3f)] private float targetWidthRatio = 1.15f;
        [SerializeField] private KeyCode applyKey = KeyCode.F9;
        [SerializeField] private KeyCode restoreKey = KeyCode.F10;
        [SerializeField] private bool showMarkings = true;
        [Header("Surgical Marker Appearance")]
        [SerializeField, Range(3f, 12f)] private float markerWidth = 6f;
        [SerializeField, Range(8f, 36f)] private float dashLength = 18f;
        [SerializeField] private bool contrastOutline = true;
        [SerializeField] private bool showOriginalCurve;
        private Vector2 panelScroll;
        private static readonly Color ReferenceInk = new Color(0.035f, 0.025f, 0.035f, 1f);
        private readonly List<Snapshot> layers = new List<Snapshot>();
        private readonly List<Behaviour> paused = new List<Behaviour>();
        private Vector3[] sourceLine, targetLine;
        private Vector2[] mouthUvs;
        private Vector3 center;
        private float halfWidth, coreHeight, feather;
        private bool applied;
        private string status = "点击“生成术前标记”开始；F9 可直接生成并应用。";
        private DeformableImageMesh Skin { get { return facialCurves != null ? facialCurves.SkinLayer : null; } }

        private sealed class Snapshot
        {
            public DeformableImageMesh Mesh;
            public Vector3[] Before, Target;
            public Vector2[] Uvs;
        }

        private void OnEnable() { FaceRuntimeUILayout.Enabled = true; }

        private void Start()
        {
            if (facialCurves == null) facialCurves = GetComponent<FacialFeaturePrecut2D>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(applyKey)) ApplyTarget();
            if (Input.GetKeyDown(restoreKey)) RestoreOriginal();
            foreach (Snapshot layer in layers)
                if (layer.Mesh == null || layer.Mesh.LogicalVertexCount != layer.Before.Length)
                {
                    EndPreview(false);
                    status = "Topology changed. Generate a new design.";
                    break;
                }
        }

        public void GenerateMarkings()
        {
            if (!Application.isPlaying || Skin == null)
            {
                status = "A skin layer and Play Mode are required.";
                return;
            }
            if (layers.Count > 0)
            {
                RebuildTargets();
                return;
            }
            List<Vector2> curve = facialCurves.GetSampledEditableCurve(2);
            if (curve.Count < 2 || Skin.LogicalVertexCount == 0)
            {
                status = "The mouth curve or skin mesh is not ready.";
                return;
            }
            mouthUvs = curve.ToArray();
            DeformableImageMesh[] meshes = { Skin, facialCurves.FatLayer, facialCurves.MuscleLayer };
            foreach (DeformableImageMesh mesh in meshes)
            {
                if (mesh == null || layers.Exists(item => item.Mesh == mesh)) continue;
                var snapshot = new Snapshot { Mesh = mesh, Before = mesh.CopyCurrentVertices(), Uvs = mesh.CopyNormalizedRestUvs() };
                if (snapshot.Before.Length == 0 || snapshot.Before.Length != snapshot.Uvs.Length)
                {
                    layers.Clear();
                    status = "A tissue mesh is not ready.";
                    return;
                }
                layers.Add(snapshot);
            }
            sourceLine = SampleMouth(layers[0], layers[0].Before);
            Vector3 minimum = sourceLine[0], maximum = sourceLine[0];
            foreach (Vector3 point in sourceLine) { minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point); }
            center = (minimum + maximum) * 0.5f;
            halfWidth = (maximum.x - minimum.x) * 0.5f;
            if (halfWidth < 0.0001f) { layers.Clear(); status = "Mouth width is too small."; return; }
            coreHeight = (maximum.y - minimum.y) * 0.5f + halfWidth * 0.18f;
            feather = halfWidth * 0.8f;
            // Pause only active drivers belonging to these tissues. Preserve originally disabled components.
            foreach (Snapshot layer in layers)
                foreach (Behaviour driver in layer.Mesh.GetComponents<Behaviour>())
                    if (driver is DeformableImageInteractor || driver is SoftTissueLayerCoupler2D
                        || driver is BoneSkinCoupler2D || driver is MuscleExpressionController2D)
                        Pause(driver);
            foreach (MuscleExpressionController2D driver in FindObjectsOfType<MuscleExpressionController2D>())
                if (driver.SkinLayer == Skin || driver.MuscleLayer == facialCurves.MuscleLayer) Pause(driver);
            RebuildTargets();
        }

        private void Pause(Behaviour driver)
        {
            if (driver.enabled && !paused.Contains(driver)) { paused.Add(driver); driver.enabled = false; }
        }

        private Vector3 Warp(Vector3 point)
        {
            float outsideX = Mathf.Max(0f, Mathf.Abs(point.x - center.x) - halfWidth);
            float outsideY = Mathf.Max(0f, Mathf.Abs(point.y - center.y) - coreHeight);
            float distance = Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY) / feather;
            float weight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distance));
            point.x += (point.x - center.x) * (targetWidthRatio - 1f) * weight;
            return point;
        }

        private void RebuildTargets()
        {
            if (layers.Count == 0) return;
            targetWidthRatio = Mathf.Clamp(targetWidthRatio, 0.75f, 1.3f);
            foreach (Snapshot layer in layers)
            {
                layer.Target = new Vector3[layer.Before.Length];
                for (int i = 0; i < layer.Before.Length; i++)
                {
                    Vector3 world = layer.Mesh.transform.TransformPoint(layer.Before[i]);
                    Vector3 skinPoint = Skin.transform.InverseTransformPoint(world);
                    layer.Target[i] = layer.Mesh.transform.InverseTransformPoint(Skin.transform.TransformPoint(Warp(skinPoint)));
                }
            }
            targetLine = SampleMouth(layers[0], layers[0].Target);
            if (applied) SetPose(true);
            status = "Design ready. F9 applies; F10 restores and resumes drivers.";
        }

        // UV-weighted sampling also works at the pre-cut mouth slit, where no triangle survives.
        private Vector3[] SampleMouth(Snapshot layer, Vector3[] positions)
        {
            var result = new Vector3[mouthUvs.Length];
            for (int sample = 0; sample < result.Length; sample++)
            {
                int[] nearest = { -1, -1, -1, -1 };
                float[] distances = { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
                for (int vertex = 0; vertex < layer.Uvs.Length; vertex++)
                {
                    float distance = (layer.Uvs[vertex] - mouthUvs[sample]).sqrMagnitude;
                    for (int slot = 0; slot < 4; slot++)
                        if (distance < distances[slot])
                        {
                            for (int shift = 3; shift > slot; shift--) { distances[shift] = distances[shift - 1]; nearest[shift] = nearest[shift - 1]; }
                            distances[slot] = distance; nearest[slot] = vertex; break;
                        }
                }
                float total = 0f;
                for (int slot = 0; slot < 4; slot++)
                    if (nearest[slot] >= 0)
                    {
                        float weight = 1f / Mathf.Max(0.00000001f, distances[slot]);
                        result[sample] += positions[nearest[slot]] * weight;
                        total += weight;
                    }
                result[sample] /= Mathf.Max(total, 0.000001f);
            }
            return result;
        }

        public void ApplyTarget()
        {
            if (layers.Count == 0) GenerateMarkings();
            if (layers.Count == 0) return;
            SetPose(true);
            applied = true;
            status = "Target applied (preview). F10 restores the captured face.";
        }

        private void SetPose(bool target)
        {
            foreach (Snapshot layer in layers)
                if (layer.Mesh != null && layer.Mesh.LogicalVertexCount == layer.Before.Length)
                    layer.Mesh.SetLogicalVertices(target ? layer.Target : layer.Before);
        }

        public void RestoreOriginal() { EndPreview(true); status = "Original face restored; drivers resumed."; }

        private void EndPreview(bool restore)
        {
            if (restore) SetPose(false);
            layers.Clear();
            sourceLine = targetLine = null;
            applied = false;
            foreach (Behaviour driver in paused) if (driver != null) driver.enabled = true;
            paused.Clear();
        }

        private void OnDisable() { EndPreview(true); FaceRuntimeUILayout.Enabled = false; }

        private void OnGUI()
        {
            if (!Application.isPlaying) return;
            FaceRuntimeUILayout.DrawNavigation();
            if (showMarkings && sourceLine != null && Camera.main != null)
            {
                if (showOriginalCurve) DrawCurve(applied ? targetLine : sourceLine, ReferenceInk, false);
                DrawCurve(targetLine, ReferenceInk, true);
                DrawReferenceTicks();
                DrawArrow(sourceLine[0], targetLine[0]);
                DrawArrow(sourceLine[sourceLine.Length - 1], targetLine[targetLine.Length - 1]);
            }
            if (!FaceRuntimeUILayout.Right(0)) return;
            GUILayout.BeginArea(FaceRuntimeUILayout.Panel(true, 470f), GUI.skin.box);
            panelScroll = GUILayout.BeginScrollView(panelScroll);
            GUILayout.Label("术前标记：嘴宽调整");
            GUILayout.Label("目标嘴宽：" + (targetWidthRatio * 100f).ToString("0") + "%");
            float ratio = GUILayout.HorizontalSlider(targetWidthRatio, 0.75f, 1.3f);
            if (Mathf.Abs(ratio - targetWidthRatio) > 0.0001f) { targetWidthRatio = ratio; RebuildTargets(); }
            if (GUILayout.Button("生成术前标记（暂停面部驱动）")) GenerateMarkings();
            if (GUILayout.Button("应用目标形状 [F9]")) ApplyTarget();
            if (GUILayout.Button("还原并恢复驱动 [F10]")) RestoreOriginal();
            showMarkings = GUILayout.Toggle(showMarkings, "显示黑色术前设计标记");
            showOriginalCurve = GUILayout.Toggle(showOriginalCurve, "显示原始嘴部实线参考");
            contrastOutline = GUILayout.Toggle(contrastOutline, "浅色细描边（增强复杂肤色下的可见性）");
            GUILayout.Label("标记笔粗细：" + markerWidth.ToString("0") + " px");
            markerWidth = GUILayout.HorizontalSlider(markerWidth, 3f, 12f);
            GUILayout.Label("黑色短刻线：基准；虚线：目标；箭头：调整方向。");
            GUILayout.Label("本原型未定义风险解剖或组织富余区域，因此不自动绘制红／绿色标记。");
            GUILayout.Label("参考医生个人标记习惯；目标虚线为游戏设计约定，不代表临床切口。");
            GUILayout.Label("先调节比例，再生成标记。F9 应用，F10 还原。预览期间暂停手动操作及表情。");
            GUILayout.Label(status);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private Vector2 Project(Vector3 local)
        {
            Vector3 screen = Camera.main.WorldToScreenPoint(Skin.transform.TransformPoint(local));
            return new Vector2(screen.x, Screen.height - screen.y);
        }

        private void DrawCurve(Vector3[] points, Color color, bool dashed)
        {
            // Carry dash phase across curve samples, so short segments do not become a solid line.
            float travelled = 0f;
            float period = dashLength * 1.65f;
            for (int i = 1; i < points.Length; i++)
            {
                Vector2 start = Project(points[i - 1]), end = Project(points[i]);
                float length = Vector2.Distance(start, end);
                if (!dashed) DrawLine(start, end, color);
                else
                {
                    float d = 0f;
                    while (d < length)
                    {
                        float phase = (travelled + d) % period;
                        bool ink = phase < dashLength;
                        float step = Mathf.Min(length - d, (ink ? dashLength : period) - phase);
                        if (step < 0.001f) step = Mathf.Min(0.001f, length - d);
                        if (ink) DrawLine(Vector2.Lerp(start, end, d / length), Vector2.Lerp(start, end, (d + step) / length), color);
                        d += step;
                    }
                }
                travelled += length;
            }
        }

        private void DrawReferenceTicks()
        {
            Vector2 middle = Project(center);
            float tick = markerWidth * 2.5f;
            // Short midline and bilateral corner registration marks, rather than a full face overlay.
            DrawLine(middle + Vector2.up * (tick + 14f), middle + Vector2.up * (tick + 34f), ReferenceInk);
            Vector3[] line = applied ? targetLine : sourceLine;
            foreach (int index in new[] { 0, line.Length - 1 })
            {
                Vector2 point = Project(line[index]);
                DrawLine(point - Vector2.up * tick, point + Vector2.up * tick, ReferenceInk);
            }
        }

        private void DrawArrow(Vector3 start, Vector3 end)
        {
            Vector2 a = Project(start), b = Project(end);
            if ((b - a).sqrMagnitude < 9f) return;
            Vector2 direction = (b - a).normalized;
            Vector2 normal = new Vector2(-direction.y, direction.x);
            // Lift direction arrows away from the mouth contour to keep the dashed design legible.
            Vector2 offset = new Vector2(0f, -markerWidth * 5f);
            a += offset; b += offset;
            float head = Mathf.Max(12f, markerWidth * 2.5f);
            DrawLine(a, b, ReferenceInk);
            DrawLine(b, b - direction * head + normal * head * 0.5f, ReferenceInk);
            DrawLine(b, b - direction * head - normal * head * 0.5f, ReferenceInk);
        }

        private void DrawLine(Vector2 a, Vector2 b, Color color)
        {
            Matrix4x4 matrix = GUI.matrix;
            Color previous = GUI.color;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg, a);
            float length = Vector2.Distance(a, b);
            if (contrastOutline)
            {
                GUI.color = new Color(1f, 0.96f, 0.9f, 0.85f);
                GUI.DrawTexture(new Rect(a.x - 1f, a.y - markerWidth * 0.5f - 1f, length + 2f, markerWidth + 2f), Texture2D.whiteTexture);
            }
            GUI.color = color;
            GUI.DrawTexture(new Rect(a.x, a.y - markerWidth * 0.5f, length, markerWidth), Texture2D.whiteTexture);
            GUI.matrix = matrix;
            GUI.color = previous;
        }
    }
}
