using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeformableImage.Editor
{
    [CustomEditor(typeof(FacialFeaturePrecut2D))]
    public sealed class FacialFeaturePrecut2DEditor : UnityEditor.Editor
    {
        [InitializeOnLoadMethod]
        private static void ScheduleRequestedSceneBake()
        {
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
            EditorApplication.delayCall += BakeRequestedSceneTopology;
        }

        private static void HandlePlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.delayCall += BakeRequestedSceneTopology;
            }
        }

        private static void BakeRequestedSceneTopology()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode
                || EditorApplication.isCompiling
                || EditorApplication.isUpdating)
            {
                return;
            }

            FacialFeaturePrecut2D[] precuts =
                Object.FindObjectsOfType<FacialFeaturePrecut2D>(true);
            for (int index = 0; index < precuts.Length; index++)
            {
                FacialFeaturePrecut2D precut = precuts[index];
                if (precut == null)
                {
                    continue;
                }

                bool changed = false;
                if (precut.EditorBakeRequested)
                {
                    if (!precut.BakePrecutsIntoTopology())
                    {
                        Debug.LogError(precut.LastStatus, precut);
                        continue;
                    }

                    changed = true;
                }
                else if (precut.HasBakedTopology
                    && (!precut.FatLayer.UsesSharedBakedTopology
                        || !precut.MuscleLayer.UsesSharedBakedTopology))
                {
                    // One-time migration for scenes baked before shared topology storage.
                    // No GameObject or component is replaced; only duplicate serialized arrays
                    // on the aligned tissue layers are changed to a reference to Skin.
                    changed = precut.FatLayer.CopyBakedOpenCutTopologyFrom(precut.SkinLayer)
                        && precut.MuscleLayer.CopyBakedOpenCutTopologyFrom(precut.SkinLayer);
                }

                if (!changed)
                {
                    continue;
                }

                MarkPrecutAndLayersDirty(precut);
                UnityEngine.SceneManagement.Scene scene = precut.gameObject.scene;
                if (scene.IsValid() && scene.isLoaded && !string.IsNullOrEmpty(scene.path))
                {
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                }

                Debug.Log(precut.LastStatus, precut);
            }
        }

        public override void OnInspectorGUI()
        {
            FacialFeaturePrecut2D precut = (FacialFeaturePrecut2D)target;
            int curveHashBefore = precut.CalculateCurveHash();
            bool hadBakedTopology = precut.HasAnyBakedTopology;
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck()
                && hadBakedTopology
                && curveHashBefore != precut.CalculateCurveHash())
            {
                RecordPrecutAndLayers(precut, "Invalidate Facial Cut Topology");
                precut.InvalidateBakedTopology();
                MarkPrecutAndLayersDirty(precut);
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "The three curves are serialized in the scene before Play Mode. Editable "
                + "Curves therefore performs no runtime recognition: Play Mode loads the "
                + "already-cut topology shared by Muscle, Fat and Skin. Select this "
                + "component and drag the colored Scene handles to refine them.",
                MessageType.Info);

            if (GUILayout.Button("Pre-Generate / Reset Three Curves"))
            {
                RecordPrecutAndLayers(precut, "Reset Facial Pre-Cut Curves");
                precut.InvalidateBakedTopology();
                precut.ResetEditableCurves();
                EditorUtility.SetDirty(precut);
                SceneView.RepaintAll();
            }

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Bake Final Topology Into All 3 Layers"))
                {
                    RecordPrecutAndLayers(precut, "Bake Facial Cut Topology");
                    if (precut.BakePrecutsIntoTopology())
                    {
                        MarkPrecutAndLayersDirty(precut);
                    }
                    SceneView.RepaintAll();
                }
            }

            EditorGUILayout.HelpBox(
                precut.HasBakedTopology
                    ? "BAKED: Play Mode loads the saved topology and skips cutting."
                    : "NOT BAKED: adjust the curves, then bake once to remove Play Mode cutting cost.",
                precut.HasBakedTopology ? MessageType.Info : MessageType.Warning);

            EditorGUILayout.HelpBox(precut.LastStatus, MessageType.None);
        }

        [MenuItem("Tools/Deformable Image/Bake Facial Pre-Cut Topology In Active Scene")]
        private static void BakeActiveScenePrecut()
        {
            FacialFeaturePrecut2D precut =
                Object.FindObjectOfType<FacialFeaturePrecut2D>(true);
            if (precut == null)
            {
                EditorUtility.DisplayDialog(
                    "Bake Facial Pre-Cut Topology",
                    "No FacialFeaturePrecut2D component exists in the active scene.",
                    "OK");
                return;
            }

            RecordPrecutAndLayers(precut, "Bake Facial Cut Topology");
            if (!precut.BakePrecutsIntoTopology())
            {
                EditorUtility.DisplayDialog(
                    "Bake Facial Pre-Cut Topology",
                    precut.LastStatus,
                    "OK");
                return;
            }

            MarkPrecutAndLayersDirty(precut);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                precut.gameObject.scene);
            Selection.activeGameObject = precut.gameObject;
            SceneView.RepaintAll();
            Debug.Log(precut.LastStatus, precut);
        }

        private void OnSceneGUI()
        {
            FacialFeaturePrecut2D precut = (FacialFeaturePrecut2D)target;
            if (!precut.ShowEditorPreview || precut.SkinLayer == null)
            {
                return;
            }

            CompareFunction previousZTest = Handles.zTest;
            try
            {
                // These are authoring guides, so keep them visible over the opaque skin.
                Handles.zTest = CompareFunction.Always;
                DrawAllCurves(precut, true);
                DrawSceneInstructions();
            }
            finally
            {
                Handles.zTest = previousZTest;
            }
        }

        private static void DrawAllCurves(FacialFeaturePrecut2D precut, bool editable)
        {
            for (int curveIndex = 0; curveIndex < 3; curveIndex++)
            {
                FacialCutCurve2D curve = precut.GetEditableCurve(curveIndex);
                if (curve == null || curve.ControlPoints == null)
                {
                    continue;
                }

                DrawCurvePreview(precut, curveIndex, curve.Color);
                if (editable)
                {
                    DrawControlPoints(precut, curve, curveIndex);
                }
            }
        }

        private static void DrawCurvePreview(
            FacialFeaturePrecut2D precut,
            int curveIndex,
            Color color)
        {
            List<Vector2> sampled = precut.GetSampledEditableCurve(curveIndex);
            List<Vector3> world = new List<Vector3>(sampled.Count);
            for (int index = 0; index < sampled.Count; index++)
            {
                if (precut.TryNormalizedUvToWorld(sampled[index], out Vector3 point))
                {
                    world.Add(OffsetTowardSceneCamera(precut, point));
                }
            }

            if (world.Count >= 2)
            {
                Handles.color = new Color(0f, 0f, 0f, 0.88f);
                Handles.DrawAAPolyLine(
                    precut.EditorLineWidth + 3f,
                    world.ToArray());
                Handles.color = color;
                Handles.DrawAAPolyLine(precut.EditorLineWidth, world.ToArray());
            }
        }

        private static void DrawControlPoints(
            FacialFeaturePrecut2D precut,
            FacialCutCurve2D curve,
            int curveIndex)
        {
            List<Vector3> controlWorld = new List<Vector3>(curve.ControlPoints.Count);
            for (int pointIndex = 0; pointIndex < curve.ControlPoints.Count; pointIndex++)
            {
                if (precut.TryNormalizedUvToWorld(
                        curve.ControlPoints[pointIndex],
                        out Vector3 surfacePoint))
                {
                    controlWorld.Add(OffsetTowardSceneCamera(precut, surfacePoint));
                }
            }

            if (precut.ShowControlPolygon && controlWorld.Count >= 2)
            {
                Handles.color = new Color(curve.Color.r, curve.Color.g, curve.Color.b, 0.55f);
                for (int index = 0; index + 1 < controlWorld.Count; index++)
                {
                    Handles.DrawDottedLine(controlWorld[index], controlWorld[index + 1], 5f);
                }
            }

            for (int pointIndex = 0; pointIndex < curve.ControlPoints.Count; pointIndex++)
            {
                Vector2 uv = curve.ControlPoints[pointIndex];
                if (!precut.TryNormalizedUvToWorld(uv, out Vector3 surfaceWorld))
                {
                    continue;
                }

                Vector3 displayWorld = OffsetTowardSceneCamera(precut, surfaceWorld);
                Vector3 displayOffset = displayWorld - surfaceWorld;
                float size = HandleUtility.GetHandleSize(displayWorld) * precut.EditorHandleSize;
                Handles.color = curve.Color;
                EditorGUI.BeginChangeCheck();
                Handles.CapFunction cap = pointIndex == 0
                        || pointIndex == curve.ControlPoints.Count - 1
                    ? Handles.RectangleHandleCap
                    : Handles.DotHandleCap;
                Transform skinTransform = precut.SkinLayer.transform;
                Vector3 moved = Handles.Slider2D(
                    displayWorld,
                    skinTransform.TransformDirection(Vector3.forward),
                    skinTransform.TransformDirection(Vector3.right),
                    skinTransform.TransformDirection(Vector3.up),
                    size,
                    cap,
                    Vector2.zero);
                if (!EditorGUI.EndChangeCheck()
                    || !precut.TryWorldToNormalizedUv(
                        moved - displayOffset,
                        out Vector2 movedUv))
                {
                    continue;
                }

                RecordPrecutAndLayers(precut, "Move " + curve.Label + " Cut Point");
                precut.InvalidateBakedTopology();
                curve.ControlPoints[pointIndex] = movedUv;
                EditorUtility.SetDirty(precut);
                SceneView.RepaintAll();
            }

            if (curve.ControlPoints.Count > 0
                && precut.TryNormalizedUvToWorld(curve.ControlPoints[0], out Vector3 labelPoint))
            {
                Handles.color = curve.Color;
                Handles.Label(
                    OffsetTowardSceneCamera(precut, labelPoint),
                    curve.Label + " (" + (curveIndex + 1) + ")");
            }
        }

        private static Vector3 OffsetTowardSceneCamera(
            FacialFeaturePrecut2D precut,
            Vector3 worldPoint)
        {
            SceneView sceneView = SceneView.currentDrawingSceneView != null
                ? SceneView.currentDrawingSceneView
                : SceneView.lastActiveSceneView;
            Camera camera = sceneView != null ? sceneView.camera : null;
            if (camera == null || precut.EditorSurfaceOffset <= 0f)
            {
                return worldPoint;
            }

            Vector3 towardCamera = camera.orthographic
                ? -camera.transform.forward
                : (camera.transform.position - worldPoint).normalized;
            return worldPoint + towardCamera * precut.EditorSurfaceOffset;
        }

        private static void DrawSceneInstructions()
        {
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(12f, 12f, 340f, 82f), "Pre-Cut Curves", GUI.skin.window);
            GUILayout.Label("Square = opening endpoint; dot = curve control point");
            GUILayout.Label("Drag a handle: the final solid curve updates immediately");
            GUILayout.EndArea();
            Handles.EndGUI();
        }

        private static void RecordPrecutAndLayers(FacialFeaturePrecut2D precut, string action)
        {
            List<Object> objects = new List<Object> { precut };
            if (precut.SkinLayer != null)
            {
                objects.Add(precut.SkinLayer);
            }
            if (precut.FatLayer != null)
            {
                objects.Add(precut.FatLayer);
            }
            if (precut.MuscleLayer != null)
            {
                objects.Add(precut.MuscleLayer);
            }
            Undo.RegisterCompleteObjectUndo(objects.ToArray(), action);
        }

        private static void MarkPrecutAndLayersDirty(FacialFeaturePrecut2D precut)
        {
            EditorUtility.SetDirty(precut);
            if (precut.SkinLayer != null)
            {
                EditorUtility.SetDirty(precut.SkinLayer);
            }
            if (precut.FatLayer != null)
            {
                EditorUtility.SetDirty(precut.FatLayer);
            }
            if (precut.MuscleLayer != null)
            {
                EditorUtility.SetDirty(precut.MuscleLayer);
            }
        }
    }
}
