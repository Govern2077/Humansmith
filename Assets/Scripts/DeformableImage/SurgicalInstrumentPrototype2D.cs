using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeformableImage
{
    /// <summary>Runtime-only game instruments: local pulls, retained retractors, layer release and spring sutures.</summary>
    [DefaultExecutionOrder(400)]
    [DisallowMultipleComponent]
    public sealed class SurgicalInstrumentPrototype2D : MonoBehaviour
    {
        private sealed class Grip
        {
            public int Vertex;
            public float[] Weights;
            public Vector3 Start, Target;
            public bool Retained, Releasing;
            public float ReleaseTime;
            public float StretchStrength = 1f;
            public LineRenderer Visual;
        }

        private sealed class Stitch
        {
            public Grip A, B;
            public float Length, Tightness = 0.65f;
            public LineRenderer Visual;
        }

        private readonly List<Grip> pulls = new List<Grip>();
        private readonly List<Stitch> stitches = new List<Stitch>();
        private DeformableImageInteractor owner;
        private DeformableImageMesh skin;
        private SoftTissueLayerCoupler2D coupler;
        private Camera view;
        private Grip held, firstStitch;
        private Vector3 pointerStart, lastTip;
        private bool dissecting;
        private Vector3[] current = Array.Empty<Vector3>(), result = Array.Empty<Vector3>(), applied = Array.Empty<Vector3>();
        private Vector3[] unconstrained = Array.Empty<Vector3>();
        private float[] separation = Array.Empty<float>();
        private bool[] anchors = Array.Empty<bool>();
        private Material lineMaterial;
        private LineRenderer preview;
        private float uvRadiusScale = 0.2f;
        private float stitchSetting = 0.65f;
        private string status = "从切口附近试用器械；皮肤原始外轮廓保持固定。";

        public int RetractorCount { get { return pulls.FindAll(p => p.Retained).Count; } }
        public int SutureCount { get { return stitches.Count; } }

        public void Configure(DeformableImageInteractor interactor, DeformableImageMesh mesh, Camera camera)
        {
            if (skin != null) { skin.MeshRebuilt -= Invalidate; skin.RestPoseChanged -= Invalidate; }
            owner = interactor; skin = mesh; view = camera;
            coupler = GetComponent<SoftTissueLayerCoupler2D>();
            skin.MeshRebuilt += Invalidate; skin.RestPoseChanged += Invalidate;
            Invalidate();
        }

        private void EnsureBuffers()
        {
            if (skin == null || current.Length == skin.LogicalVertexCount) return;
            int count = skin.LogicalVertexCount;
            current = new Vector3[count]; result = new Vector3[count]; applied = new Vector3[count];
            unconstrained = new Vector3[count];
            separation = new float[count]; anchors = skin.CopyOriginalOutlineAnchors();
            var rest = skin.CopyMaterialSpaceVertices();
            if (rest.Length > 0)
            {
                Vector3 min = rest[0], max = rest[0];
                foreach (Vector3 p in rest) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                uvRadiusScale = 1f / Mathf.Max(0.01f, Mathf.Max(max.x - min.x, max.y - min.y));
            }
        }

        private void Invalidate()
        {
            ClearVisuals(); pulls.Clear(); stitches.Clear(); held = firstStitch = null; dissecting = false;
            current = result = applied = Array.Empty<Vector3>(); separation = Array.Empty<float>();
            status = "网格／静止形状已更新，器械已清除，可重新放置。";
        }

        public void HandleInput(DeformableImageInteractor.DeformationTool tool)
        {
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
            { CancelPendingInput(); status = "已取消当前器械操作；留置器械仍保留。"; return; }
            if (Input.GetMouseButtonDown(0) && !owner.IsPointerOverToolUI()
                && !DeformableImagePieceMover.IsPointerCaptured && !AnatomyBoneFragmentMover.IsPointerCaptured
                && TryPointer(out Vector3 point))
            {
                if (tool == DeformableImageInteractor.DeformationTool.Suture) PlaceSuturePoint(point);
                else if (tool == DeformableImageInteractor.DeformationTool.Dissector)
                {
                    dissecting = skin.IsNearCutBoundary(point, 0.25f);
                    status = dissecting ? "剥离中：刷过区域逐渐解除层间联动并轻微抬起。" : "请先在切口附近按下，再沿皮肤拖动剥离。";
                }
                else BeginPull(point, tool == DeformableImageInteractor.DeformationTool.Retractor);
            }
            if (Input.GetMouseButton(0) && TryPointer(out Vector3 tip))
            {
                if (held != null) MovePull(tip);
                if (dissecting) DissectAt(tip, Time.deltaTime * 2f);
            }
            if (Input.GetMouseButtonUp(0)) { EndPull(); dissecting = false; }
        }

        private bool TryPointer(out Vector3 point)
        {
            point = default;
            Camera camera = view != null ? view : Camera.main;
            if (camera == null) return false;
            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(transform.forward, transform.position);
            if (!plane.Raycast(ray, out float distance)) return false;
            point = transform.InverseTransformPoint(ray.GetPoint(distance)); point.z = 0;
            return true;
        }

        private Grip CreateGrip(Vector3 point, bool requireCut)
        {
            EnsureBuffers();
            if (!skin.IsVisibleMaterialAtCurrentPoint(point)
                || requireCut && !skin.IsNearCutBoundary(point, 0.25f))
            { status = requireCut ? "请在切口两侧的完整皮肤上选点。" : "请在完整皮肤上选点。"; return null; }
            skin.CopyCurrentVerticesNonAlloc(current);
            float radius = Mathf.Clamp(owner.BrushRadius, 0.12f, 1.2f);
            skin.TryBuildGeodesicDistances(point, radius, out float[] distances);
            int nearest = -1; float best = radius * radius;
            for (int i = 0; i < current.Length; i++)
            {
                if (anchors[i] || distances != null && float.IsPositiveInfinity(distances[i])) continue;
                float d = ((Vector2)(current[i] - point)).sqrMagnitude;
                if (d < best) { best = d; nearest = i; }
            }
            if (nearest < 0) { status = "此处属于固定外缘或离可移动网格太远，请换一个位置。"; return null; }
            var grip = new Grip { Vertex = nearest, Weights = new float[current.Length], Start = current[nearest], Target = current[nearest] };
            for (int i = 0; i < current.Length; i++)
            {
                float distance = distances != null ? distances[i] : Vector2.Distance(current[i], point);
                float w = anchors[i] ? 0f : Mathf.Clamp01(1f - distance / radius);
                grip.Weights[i] = w * w;
            }
            grip.Weights[nearest] = 1f;
            return grip;
        }

        public bool BeginPull(Vector3 point, bool retain)
        {
            if (pulls.Count >= 8) { status = "最多同时使用 8 个皮钩／牵开器，请先移除一个。"; return false; }
            CancelPendingInput();
            Grip grip = CreateGrip(point, retain);
            if (grip == null) return false;
            held = grip; held.Retained = retain; pointerStart = point;
            held.StretchStrength = skin.ToolStretchStrengthAt(point);
            held.Visual = NewLine(retain ? new Color(0.9f, 0.72f, 0.25f) : new Color(0.8f, 0.9f, 1f), 0.014f);
            pulls.Add(held);
            status = retain ? "拖动撑开切口，松手后牵开器保持；按钮可移除。" : "拖动牵引皮肤，松手后回弹。";
            return true;
        }

        public void MovePull(Vector3 point)
        {
            if (held == null) return;
            float limit = held.Retained || skin.IsNearCutBoundary(pointerStart, 0.25f) ? 1.2f : owner.BrushRadius * 0.32f;
            held.Target = held.Start + Vector3.ClampMagnitude((point - pointerStart) * held.StretchStrength,
                limit * held.StretchStrength);
        }

        public void EndPull()
        {
            if (held == null) return;
            if (!held.Retained) { held.Releasing = true; held.ReleaseTime = Time.unscaledTime; }
            held = null;
        }

        public bool PlaceSuturePoint(Vector3 point)
        {
            if (stitches.Count >= 16) { status = "最多保留 16 针，请先拆除一针。"; return false; }
            Grip grip = CreateGrip(point, true);
            if (grip == null) return false;
            if (firstStitch == null)
            { firstStitch = grip; status = "已选第一点；点击切口另一侧，右键／Esc 取消。"; return true; }
            float length = Vector3.Distance(firstStitch.Start, grip.Start);
            if (grip.Vertex == firstStitch.Vertex || length < 0.04f || length > 1.5f)
            { status = "两点过近或过远，请在切口另一侧重新选点。"; return false; }
            stitches.Add(new Stitch { A = firstStitch, B = grip, Length = length, Tightness = stitchSetting,
                Visual = NewLine(new Color(0.08f, 0.25f, 0.5f), 0.009f) });
            firstStitch = null; status = "缝线已留置；滑杆调整最后一针的收紧程度。";
            return true;
        }

        public bool DissectAt(Vector3 point, float amount)
        {
            EnsureBuffers();
            if (!dissecting && !skin.IsNearCutBoundary(point, 0.25f)) return false;
            if (!skin.IsVisibleMaterialAtCurrentPoint(point) || !skin.TryGetNormalizedUvAtCurrentPoint(point, out Vector2 uv)) return false;
            Grip brush = CreateGrip(point, false);
            if (brush == null) return false;
            if (coupler != null) coupler.ReleaseInfluence(uv, owner.BrushRadius * uvRadiusScale, amount);
            for (int i = 0; i < separation.Length; i++)
                if (!anchors[i]) separation[i] = Mathf.Min(0.16f, separation[i] + Mathf.Max(0f, amount) * brush.Weights[i] * 0.12f);
            lastTip = current[brush.Vertex];
            return true;
        }

        public void SetLastSutureTightness(float value)
        {
            stitchSetting = Mathf.Clamp01(value);
            if (stitches.Count > 0) stitches[stitches.Count - 1].Tightness = stitchSetting;
        }

        private float LiftSign
        {
            get
            {
                Camera camera = view != null ? view : Camera.main;
                return camera == null ? -1f : (Vector3.Dot(camera.transform.position - transform.position, transform.forward) >= 0f ? 1f : -1f);
            }
        }

        private void ApplyDelta(Grip grip, Vector3 delta)
        {
            for (int i = 0; i < result.Length; i++) result[i] += delta * grip.Weights[i];
        }

        // Reconstruct the unconstrained mesh each frame. Never add the last frame's
        // pull/closure a second time, so held tools do not creep and removal is reversible.
        public void SolveConstraints()
        {
            EnsureBuffers();
            if (current.Length == 0 || !skin.CopyCurrentVerticesNonAlloc(current)) return;
            bool changed = false;
            for (int i = 0; i < current.Length; i++)
            {
                unconstrained[i] = current[i] - applied[i];
                result[i] = unconstrained[i];
                if (!anchors[i]) result[i].z += separation[i] * LiftSign;
                if (applied[i].sqrMagnitude > 1e-12f || separation[i] > 0f) changed = true;
            }
            for (int p = pulls.Count - 1; p >= 0; p--)
                if (pulls[p].Releasing && Time.unscaledTime - pulls[p].ReleaseTime >= 0.35f)
                { DestroyVisual(pulls[p].Visual); pulls.RemoveAt(p); }
            for (int iteration = 0; iteration < 6; iteration++)
            {
                foreach (Grip grip in pulls)
                {
                    float release = grip.Releasing ? Mathf.Clamp01((Time.unscaledTime - grip.ReleaseTime) / 0.35f) : 0f;
                    Vector3 baseline = current[grip.Vertex] - applied[grip.Vertex];
                    Vector3 target = Vector3.Lerp(grip.Target, baseline, release);
                    ApplyDelta(grip, Vector3.ClampMagnitude(target - result[grip.Vertex], 0.3f) * 0.65f);
                }
                foreach (Stitch stitch in stitches)
                {
                    Vector3 delta = result[stitch.B.Vertex] - result[stitch.A.Vertex];
                    float distance = delta.magnitude;
                    float desired = Mathf.Max(0.025f, stitch.Length * (1f - stitch.Tightness * 0.85f));
                    if (distance <= desired) continue; // thread pulls, never pushes tissue apart
                    Vector3 correction = delta / distance * Mathf.Min((distance - desired) * 0.25f, 0.08f);
                    ApplyDelta(stitch.A, correction); ApplyDelta(stitch.B, -correction);
                }
            }
            if (pulls.Count > 0 || stitches.Count > 0) changed = true;
            for (int i = 0; i < result.Length; i++)
            {
                if (anchors[i]) result[i] = unconstrained[i];
            }
            if (changed) skin.SetManualVertices(result);
            for (int i = 0; i < result.Length; i++) applied[i] = result[i] - unconstrained[i];
            RefreshVisuals();
        }

        private void LateUpdate()
        {
            if (owner == null || !owner.isActiveAndEnabled || skin == null) return;
            if ((BoneCutInteractor.IsAnyBoneToolActive || AnatomyBoneFragmentMover.IsPointerCaptured) && (held != null || dissecting)) CancelPendingInput();
            SolveConstraints();
        }

        public void CancelPendingInput()
        {
            if (held != null) { pulls.Remove(held); DestroyVisual(held.Visual); held = null; }
            firstStitch = null; dissecting = false;
            if (preview != null) preview.enabled = false;
        }

        public void RemoveLastRetractor()
        {
            for (int i = pulls.Count - 1; i >= 0; i--)
                if (pulls[i].Retained)
                { if (held == pulls[i]) held = null; DestroyVisual(pulls[i].Visual); pulls.RemoveAt(i); break; }
        }

        public void RemoveLastSuture()
        {
            if (stitches.Count == 0) return;
            DestroyVisual(stitches[stitches.Count - 1].Visual); stitches.RemoveAt(stitches.Count - 1);
        }

        public void ResetInstruments()
        {
            if (skin != null && skin.CopyCurrentVerticesNonAlloc(current))
            {
                for (int i = 0; i < current.Length; i++) current[i] -= applied[i];
                skin.SetManualVertices(current);
            }
            Invalidate();
            if (coupler != null) coupler.ResetReleasedInfluence();
            status = "已清除全部器械、剥离和缝线效果。";
        }

        public void DrawControls()
        {
            if (owner.ActiveTool == DeformableImageInteractor.DeformationTool.SkinHook)
                GUILayout.Label("皮钩：左键抓住皮肤拖动，松手回弹；切口附近可拉得更远。", FaceRuntimeUILayout.WrappedLabel);
            if (owner.ActiveTool == DeformableImageInteractor.DeformationTool.Retractor)
                GUILayout.Label("牵开器：在切口旁左键拖动，松手后保持撑开，可放多个。", FaceRuntimeUILayout.WrappedLabel);
            if (owner.ActiveTool == DeformableImageInteractor.DeformationTool.Dissector)
                GUILayout.Label("剥离器：从切口旁按下，再刷过皮肤；局部抬起并减少下层联动。", FaceRuntimeUILayout.WrappedLabel);
            if (owner.ActiveTool == DeformableImageInteractor.DeformationTool.Suture)
                GUILayout.Label("缝合：依次点击切口两侧；无需穿针，连接后拖动滑杆收紧。", FaceRuntimeUILayout.WrappedLabel);
            GUILayout.Label("留置牵开器 " + RetractorCount + " / 8　缝线 " + stitches.Count + " / 16", FaceRuntimeUILayout.WrappedLabel);
            GUILayout.Label("最后一针收紧：" + Mathf.RoundToInt(stitchSetting * 100f) + "%", FaceRuntimeUILayout.WrappedLabel);
            float next = GUILayout.HorizontalSlider(stitchSetting, 0f, 1f);
            if (Mathf.Abs(next - stitchSetting) > 0.0001f) SetLastSutureTightness(next);
            if (GUILayout.Button("移除最后一个牵开器", FaceRuntimeUILayout.ToolButton)) RemoveLastRetractor();
            if (GUILayout.Button("拆除最后一针", FaceRuntimeUILayout.ToolButton)) RemoveLastSuture();
            if (GUILayout.Button("清除全部器械与剥离效果", FaceRuntimeUILayout.ToolButton)) ResetInstruments();
            GUILayout.Label(status, FaceRuntimeUILayout.WrappedLabel);
        }

        private LineRenderer NewLine(Color color, float width)
        {
            if (lineMaterial == null) lineMaterial = new Material(Shader.Find("Sprites/Default")) { hideFlags = HideFlags.DontSave };
            var go = new GameObject("Prototype surgical instrument") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false; line.sharedMaterial = lineMaterial;
            line.startColor = line.endColor = color; line.startWidth = line.endWidth = width;
            line.numCapVertices = 3; line.numCornerVertices = 3; line.sortingOrder = 25;
            return line;
        }

        private void SetLine(LineRenderer line, params Vector3[] points)
        {
            Vector3 lift = Vector3.forward * (LiftSign * 0.025f);
            line.positionCount = points.Length;
            for (int i = 0; i < points.Length; i++) line.SetPosition(i, points[i] + lift);
        }

        private void RefreshVisuals()
        {
            foreach (Grip grip in pulls)
            {
                Vector3 p = result[grip.Vertex], end = grip.Target + new Vector3(0.08f, 0.35f, 0);
                SetLine(grip.Visual, p + Vector3.right * 0.04f, p, p + Vector3.up * 0.06f, end, end + Vector3.right * 0.08f);
            }
            foreach (Stitch stitch in stitches)
            {
                Vector3 a = result[stitch.A.Vertex], b = result[stitch.B.Vertex];
                SetLine(stitch.Visual, a + Vector3.up * 0.035f, a, (a + b) * 0.5f + Vector3.forward * LiftSign * 0.04f, b, b + Vector3.up * 0.035f);
            }
            bool show = firstStitch != null || dissecting;
            if (show && preview == null) preview = NewLine(new Color(0.2f, 0.95f, 0.8f), 0.012f);
            if (preview != null)
            {
                preview.enabled = show;
                if (show)
                {
                    Vector3 p = firstStitch != null ? result[firstStitch.Vertex] : lastTip;
                    SetLine(preview, p - Vector3.right * 0.04f, p + Vector3.right * 0.04f, p, p + new Vector3(0.2f, 0.3f, 0));
                }
            }
        }

        private void DestroyVisual(LineRenderer line) { if (line != null) { if (Application.isPlaying) Destroy(line.gameObject); else DestroyImmediate(line.gameObject); } }
        private void ClearVisuals()
        {
            foreach (Grip grip in pulls) DestroyVisual(grip.Visual);
            foreach (Stitch stitch in stitches) DestroyVisual(stitch.Visual);
            DestroyVisual(preview); preview = null;
        }

        private void OnDestroy()
        {
            if (skin != null) { skin.MeshRebuilt -= Invalidate; skin.RestPoseChanged -= Invalidate; }
            ClearVisuals();
            if (lineMaterial != null) { if (Application.isPlaying) Destroy(lineMaterial); else DestroyImmediate(lineMaterial); }
        }
    }
}
