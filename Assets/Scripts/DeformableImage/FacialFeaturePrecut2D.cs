using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeformableImage
{
    [Serializable]
    public sealed class FacialCutCurve2D
    {
        [SerializeField] private string label;
        [SerializeField] private Color color = Color.cyan;
        [SerializeField] private List<Vector2> controlPoints = new List<Vector2>();

        public string Label { get { return label; } }
        public Color Color { get { return color; } }
        public List<Vector2> ControlPoints { get { return controlPoints; } }

        public FacialCutCurve2D(string curveLabel, Color curveColor, params Vector2[] points)
        {
            label = curveLabel;
            color = curveColor;
            controlPoints = new List<Vector2>(points ?? Array.Empty<Vector2>());
        }
    }

    /// <summary>
    /// Creates the two eye slits and mouth slit from skin-space curves, then projects the
    /// same normalized cuts vertically through skin, fat and muscle physical meshes.
    /// </summary>
    [DefaultExecutionOrder(200)]
    [DisallowMultipleComponent]
    public sealed class FacialFeaturePrecut2D : MonoBehaviour
    {
        [Header("Three-Layer Cut Targets")]
        [SerializeField] private DeformableImageMesh skinLayer;
        [SerializeField] private DeformableImageMesh fatLayer;
        [SerializeField] private DeformableImageMesh muscleLayer;

        [Header("Physical Incision")]
        [Tooltip("0.005 is approximately a one-pixel slit on the supplied 1254px, height-5 layers.")]
        [SerializeField, Min(0.005f)] private float cutWidth = 0.005f;
        [SerializeField, Range(1, 12)] private int samplesPerCurveSpan = 4;

        [Header("Editable Skin-Space Curves (UV)")]
        [SerializeField] private FacialCutCurve2D leftEye;
        [SerializeField] private FacialCutCurve2D rightEye;
        [SerializeField] private FacialCutCurve2D mouth;

        [Header("Editor Curve Preview")]
        [SerializeField] private bool showEditorPreview = true;
        [SerializeField] private bool showControlPolygon = true;
        [SerializeField, Range(1f, 10f)] private float editorLineWidth = 4f;
        [SerializeField, Range(0.02f, 0.15f)] private float editorHandleSize = 0.065f;
        [SerializeField, Min(0f)] private float editorSurfaceOffset = 0.015f;

        [Header("Status")]
        [SerializeField, TextArea(2, 5)] private string lastStatus = "Pre-cuts have not been applied.";
        [SerializeField, HideInInspector] private bool editorBakeRequested;

        private bool applied;

        public DeformableImageMesh SkinLayer { get { return skinLayer; } }
        public DeformableImageMesh FatLayer { get { return fatLayer; } }
        public DeformableImageMesh MuscleLayer { get { return muscleLayer; } }
        public string LastStatus { get { return lastStatus; } }
        public bool HasApplied { get { return applied; } }
        public bool HasBakedTopology
        {
            get
            {
                return skinLayer != null && skinLayer.HasBakedOpenCutTopology
                    && fatLayer != null && fatLayer.HasBakedOpenCutTopology
                    && muscleLayer != null && muscleLayer.HasBakedOpenCutTopology;
            }
        }
        public bool HasAnyBakedTopology
        {
            get
            {
                return skinLayer != null && skinLayer.HasBakedOpenCutTopology
                    || fatLayer != null && fatLayer.HasBakedOpenCutTopology
                    || muscleLayer != null && muscleLayer.HasBakedOpenCutTopology;
            }
        }
        public float CutWidth { get { return cutWidth; } set { cutWidth = Mathf.Max(0.005f, value); } }
        public bool ShowEditorPreview { get { return showEditorPreview; } }
        public bool ShowControlPolygon { get { return showControlPolygon; } }
        public float EditorLineWidth { get { return editorLineWidth; } }
        public float EditorHandleSize { get { return editorHandleSize; } }
        public float EditorSurfaceOffset { get { return editorSurfaceOffset; } }
        public bool EditorBakeRequested { get { return editorBakeRequested; } }

        private void Reset()
        {
            skinLayer = GetComponent<DeformableImageMesh>();
            ResetEditableCurves();
        }

        private void Awake()
        {
            if (skinLayer == null)
            {
                skinLayer = GetComponent<DeformableImageMesh>();
            }

            EnsureEditableCurves();
            RegisterFeatureProtection();
        }

        private void Start()
        {
            if (HasBakedTopology)
            {
                applied = true;
                lastStatus = "Loaded editor-baked cuts; no runtime topology cutting was executed.";
            }
            else
            {
                applied = false;
                lastStatus = "Topology is not baked. Runtime cutting is intentionally disabled; "
                    + "bake the three layers in the editor.";
                Debug.LogWarning(lastStatus, this);
            }
        }

        private void OnValidate()
        {
            cutWidth = Mathf.Max(0.005f, cutWidth);
            samplesPerCurveSpan = Mathf.Clamp(samplesPerCurveSpan, 1, 12);
            editorLineWidth = Mathf.Clamp(editorLineWidth, 1f, 10f);
            editorHandleSize = Mathf.Clamp(editorHandleSize, 0.02f, 0.15f);
            editorSurfaceOffset = Mathf.Max(0f, editorSurfaceOffset);
            EnsureEditableCurves();
        }

        public void Configure(
            DeformableImageMesh skin,
            DeformableImageMesh fat,
            DeformableImageMesh muscle)
        {
            skinLayer = skin;
            fatLayer = fat;
            muscleLayer = muscle;
            EnsureEditableCurves();
            RegisterFeatureProtection();
        }

        private void RegisterFeatureProtection()
        {
            if (skinLayer != null) skinLayer.ConfigureToolFeatureResponse(this);
            if (fatLayer != null) fatLayer.ConfigureToolFeatureResponse(this);
            if (muscleLayer != null) muscleLayer.ConfigureToolFeatureResponse(this);
        }

        public FacialCutCurve2D GetEditableCurve(int index)
        {
            EnsureEditableCurves();
            switch (index)
            {
                case 0: return leftEye;
                case 1: return rightEye;
                case 2: return mouth;
                default: return null;
            }
        }

        [ContextMenu("Reset Three Editable Curves")]
        public void ResetEditableCurves()
        {
            leftEye = new FacialCutCurve2D(
                "Left Eye",
                new Color(0.1f, 0.9f, 1f),
                new Vector2(0.287f, 0.5845f),
                new Vector2(0.319f, 0.5829f),
                new Vector2(0.351f, 0.5734f),
                new Vector2(0.383f, 0.5766f),
                new Vector2(0.415f, 0.5845f),
                new Vector2(0.437f, 0.584f));
            rightEye = new FacialCutCurve2D(
                "Right Eye",
                new Color(0.2f, 1f, 0.45f),
                new Vector2(0.563f, 0.584f),
                new Vector2(0.585f, 0.5845f),
                new Vector2(0.617f, 0.5774f),
                new Vector2(0.649f, 0.5758f),
                new Vector2(0.681f, 0.5821f),
                new Vector2(0.705f, 0.5885f));
            mouth = new FacialCutCurve2D(
                "Mouth",
                new Color(1f, 0.35f, 0.75f),
                new Vector2(0.391f, 0.3341f),
                new Vector2(0.415f, 0.3325f),
                new Vector2(0.447f, 0.3381f),
                new Vector2(0.479f, 0.3397f),
                new Vector2(0.5f, 0.3397f),
                new Vector2(0.522f, 0.3405f),
                new Vector2(0.558f, 0.3373f),
                new Vector2(0.59f, 0.3341f),
                new Vector2(0.61f, 0.3365f));
            lastStatus = "Three curves were calibrated from the current skin texture and stored in the scene.";
        }

        [ContextMenu("Apply Three-Layer Pre-Cuts")]
        public bool ApplyPrecutsNow()
        {
            double startedAt = Time.realtimeSinceStartupAsDouble;
            if (!Application.isPlaying)
            {
                lastStatus = "Enter Play Mode before applying physical pre-cuts.";
                return false;
            }

            if (applied)
            {
                lastStatus = "The three pre-cuts have already been applied in this Play session.";
                return false;
            }

            EnsureEditableCurves();
            List<IList<Vector2>> paths = new List<IList<Vector2>>
            {
                SampleCurve(leftEye.ControlPoints),
                SampleCurve(rightEye.ControlPoints),
                SampleCurve(mouth.ControlPoints)
            };

            for (int index = 0; index < paths.Count; index++)
            {
                if (paths[index].Count < 2)
                {
                    lastStatus = "A pre-cut curve needs at least two valid control points.";
                    return false;
                }
            }

            // Cut from deep to superficial. Each topology change refreshes the adjacent
            // UV binding before the next outer layer is cut.
            DeformableImageMesh[] targets = { muscleLayer, fatLayer, skinLayer };
            int successfulLayers = 0;
            for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                DeformableImageMesh target = targets[targetIndex];
                if (target == null)
                {
                    lastStatus = "Assign Skin, Fat and Muscle before applying vertical pre-cuts.";
                    return false;
                }

                if (target.ApplyOpenCuts(paths, cutWidth))
                {
                    successfulLayers++;
                }
            }

            int elapsedMilliseconds = Mathf.RoundToInt(
                (float)((Time.realtimeSinceStartupAsDouble - startedAt) * 1000.0));
            applied = successfulLayers == 3;
            lastStatus = applied
                ? "Applied 3 aligned incisions through Muscle, Fat and Skin with 3 batched "
                    + "topology rebuilds in " + elapsedMilliseconds + " ms."
                : "Only " + successfulLayers + " / 3 layer batches succeeded in "
                    + elapsedMilliseconds + " ms. Check curve alignment and visible alpha.";
            if (applied)
            {
                Debug.Log(lastStatus, this);
            }
            else
            {
                Debug.LogWarning(lastStatus, this);
            }

            return applied;
        }

        [ContextMenu("Bake Three-Layer Cut Topology In Editor")]
        public bool BakePrecutsIntoTopology()
        {
            if (Application.isPlaying
                || skinLayer == null
                || fatLayer == null
                || muscleLayer == null)
            {
                lastStatus = "Assign all three tissue layers and leave Play Mode before baking.";
                return false;
            }

            EnsureEditableCurves();
            List<IList<Vector2>> paths = new List<IList<Vector2>>
            {
                SampleCurve(leftEye.ControlPoints),
                SampleCurve(rightEye.ControlPoints),
                SampleCurve(mouth.ControlPoints)
            };
            for (int index = 0; index < paths.Count; index++)
            {
                if (paths[index].Count < 2)
                {
                    lastStatus = "Every baked curve needs at least two control points.";
                    return false;
                }
            }

            double startedAt = Time.realtimeSinceStartupAsDouble;
            if (!skinLayer.BakeOpenCutTopology(paths, cutWidth)
                || !fatLayer.CopyBakedOpenCutTopologyFrom(skinLayer)
                || !muscleLayer.CopyBakedOpenCutTopologyFrom(skinLayer))
            {
                lastStatus = "Editor topology bake failed. Existing scene objects were left intact.";
                return false;
            }

            int elapsedMilliseconds = Mathf.RoundToInt(
                (float)((Time.realtimeSinceStartupAsDouble - startedAt) * 1000.0));
            applied = false;
            editorBakeRequested = false;
            lastStatus = "Baked the three openings into Skin, Fat and Muscle topology in "
                + elapsedMilliseconds + " ms. Runtime cutting is now skipped.";
            return true;
        }

        public void InvalidateBakedTopology()
        {
            if (Application.isPlaying)
            {
                return;
            }

            bool cleared = false;
            if (skinLayer != null && skinLayer.HasBakedOpenCutTopology)
            {
                skinLayer.ClearBakedOpenCutTopology();
                cleared = true;
            }
            if (fatLayer != null && fatLayer.HasBakedOpenCutTopology)
            {
                fatLayer.ClearBakedOpenCutTopology();
                cleared = true;
            }
            if (muscleLayer != null && muscleLayer.HasBakedOpenCutTopology)
            {
                muscleLayer.ClearBakedOpenCutTopology();
                cleared = true;
            }

            if (!cleared)
            {
                return;
            }

            applied = false;
            lastStatus = "Curve changed. Baked topology was cleared; bake again after editing.";
        }

        public bool TryNormalizedUvToWorld(Vector2 uv, out Vector3 worldPoint)
        {
            worldPoint = default(Vector3);
            if (skinLayer == null)
            {
                return false;
            }

            bool found = !Application.isPlaying
                ? skinLayer.TryGetAuthoringSurfacePoint(uv, out Vector3 localPoint)
                : skinLayer.TryGetCurrentSurfacePoint(uv, out localPoint);
            if (!found)
            {
                return false;
            }

            worldPoint = skinLayer.transform.TransformPoint(localPoint);
            return true;
        }

        public bool TryWorldToNormalizedUv(Vector3 worldPoint, out Vector2 uv)
        {
            uv = default(Vector2);
            if (skinLayer == null)
            {
                return false;
            }

            Vector3 localPoint = skinLayer.transform.InverseTransformPoint(worldPoint);
            bool found = !Application.isPlaying
                ? skinLayer.TryGetAuthoringNormalizedUv(localPoint, out uv)
                : skinLayer.TryGetNormalizedUvAtCurrentPoint(localPoint, out uv);
            if (found)
            {
                uv.x = Mathf.Clamp01(uv.x);
                uv.y = Mathf.Clamp01(uv.y);
                return true;
            }

            return false;
        }

        public List<Vector2> GetSampledEditableCurve(int index)
        {
            FacialCutCurve2D curve = GetEditableCurve(index);
            return curve != null
                ? SampleCurve(curve.ControlPoints)
                : new List<Vector2>();
        }

        public int CalculateCurveHash()
        {
            EnsureEditableCurves();
            unchecked
            {
                int hash = 17;
                for (int curveIndex = 0; curveIndex < 3; curveIndex++)
                {
                    List<Vector2> points = GetEditableCurve(curveIndex).ControlPoints;
                    hash = hash * 31 + points.Count;
                    for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                    {
                        hash = hash * 31 + points[pointIndex].GetHashCode();
                    }
                }

                hash = hash * 31 + cutWidth.GetHashCode();
                hash = hash * 31 + samplesPerCurveSpan;
                return hash;
            }
        }

        private List<Vector2> SampleCurve(List<Vector2> controlPoints)
        {
            List<Vector2> result = new List<Vector2>();
            if (controlPoints == null || controlPoints.Count < 2)
            {
                return result;
            }

            for (int segment = 0; segment < controlPoints.Count - 1; segment++)
            {
                Vector2 p0 = controlPoints[Mathf.Max(0, segment - 1)];
                Vector2 p1 = controlPoints[segment];
                Vector2 p2 = controlPoints[segment + 1];
                Vector2 p3 = controlPoints[Mathf.Min(controlPoints.Count - 1, segment + 2)];
                for (int sample = 0; sample < samplesPerCurveSpan; sample++)
                {
                    float t = sample / (float)samplesPerCurveSpan;
                    Vector2 point = CatmullRom(p0, p1, p2, p3, t);
                    result.Add(new Vector2(Mathf.Clamp01(point.x), Mathf.Clamp01(point.y)));
                }
            }

            Vector2 last = controlPoints[controlPoints.Count - 1];
            result.Add(new Vector2(Mathf.Clamp01(last.x), Mathf.Clamp01(last.y)));
            return result;
        }

        private static Vector2 CatmullRom(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            Vector2 p3,
            float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * ((2f * p1)
                + (-p0 + p2) * t
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private void EnsureEditableCurves()
        {
            if (leftEye == null || rightEye == null || mouth == null)
            {
                ResetEditableCurves();
            }
        }

    }
}
