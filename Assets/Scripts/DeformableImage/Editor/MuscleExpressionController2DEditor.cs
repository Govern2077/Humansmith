using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DeformableImage.Editor
{
    [CustomEditor(typeof(MuscleExpressionController2D))]
    public sealed class MuscleExpressionController2DEditor : UnityEditor.Editor
    {
        private const string SceneChannelSessionKey =
            "DeformableImage.MuscleExpressionController2D.SceneChannel";
        private const string SymmetryAxisSessionKey =
            "DeformableImage.MuscleExpressionController2D.SymmetryAxis";
        private const string SymmetryDirectionSessionKey =
            "DeformableImage.MuscleExpressionController2D.SymmetryDirection";

        private enum MuscleZoneChannel
        {
            [InspectorName("关闭调试 / None")]
            None,
            [InspectorName("眨眼 / Eyelids")]
            EyesBlink,
            [InspectorName("开心 / Smile")]
            Smile,
            [InspectorName("悲伤 / Sadness")]
            Sadness,
            [InspectorName("愤怒 / Anger")]
            Anger,
            [InspectorName("痛苦 / Pain")]
            Pain,
            [InspectorName("口型 1 / Pucker U")]
            Speech1_PuckerU,
            [InspectorName("口型 2 / Open A-E")]
            Speech2_OpenTeeth,
            [InspectorName("口型 3 / Narrow E")]
            Speech3_NarrowTeethE,
            [InspectorName("口型 4 / Wide Ah")]
            Speech4_WideAh,
            [InspectorName("口型 5 / Round O")]
            Speech5_RoundO
        }

        private enum SymmetryCopyDirection
        {
            [InspectorName("左侧 → 右侧")]
            LeftToRight,
            [InspectorName("右侧 → 左侧")]
            RightToLeft
        }

        private enum SymmetryLabelSide
        {
            None,
            Left,
            Right
        }

        private enum ZoneSymmetryKind
        {
            Invalid,
            Paired,
            Singleton
        }

        private MuscleZoneChannel sceneZoneChannel = MuscleZoneChannel.None;
        private int selectedFineTuneZone;
        private float previewWeight = 1f;
        private bool showQuickTuner = true;
        private bool showSymmetryTools = true;
        private bool showAdvancedSettings;
        private float symmetryAxisUv = 0.5f;
        private SymmetryCopyDirection symmetryDirection =
            SymmetryCopyDirection.LeftToRight;
        private static MuscleExpressionController2D pendingBakeController;
        private static bool expressionBakePending;

        private void OnEnable()
        {
            int savedChannel = SessionState.GetInt(
                SceneChannelSessionKey,
                (int)MuscleZoneChannel.None);
            sceneZoneChannel = System.Enum.IsDefined(
                typeof(MuscleZoneChannel),
                savedChannel)
                ? (MuscleZoneChannel)savedChannel
                : MuscleZoneChannel.None;
            symmetryAxisUv = Mathf.Clamp01(SessionState.GetFloat(
                SymmetryAxisSessionKey,
                0.5f));
            int savedDirection = SessionState.GetInt(
                SymmetryDirectionSessionKey,
                (int)SymmetryCopyDirection.LeftToRight);
            symmetryDirection = System.Enum.IsDefined(
                typeof(SymmetryCopyDirection),
                savedDirection)
                ? (SymmetryCopyDirection)savedDirection
                : SymmetryCopyDirection.LeftToRight;
        }

        private void OnDisable()
        {
            SessionState.SetInt(SceneChannelSessionKey, (int)sceneZoneChannel);
            SessionState.SetFloat(SymmetryAxisSessionKey, symmetryAxisUv);
            SessionState.SetInt(
                SymmetryDirectionSessionKey,
                (int)symmetryDirection);
            TryFlushPendingExpressionBake();
        }

        [InitializeOnLoadMethod]
        private static void ScheduleSceneSetup()
        {
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
            EditorApplication.delayCall += SetupCurrentSceneExpression;
        }

        private static void HandlePlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.delayCall += SetupCurrentSceneExpression;
            }
        }

        private static void SetupCurrentSceneExpression()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode
                || EditorApplication.isCompiling
                || EditorApplication.isUpdating)
            {
                return;
            }

            FacialFeaturePrecut2D curves =
                Object.FindObjectOfType<FacialFeaturePrecut2D>(true);
            if (curves == null || curves.MuscleLayer == null || curves.SkinLayer == null)
            {
                return;
            }

            MuscleExpressionController2D controller =
                curves.MuscleLayer.GetComponent<MuscleExpressionController2D>();
            bool changed = false;
            if (controller == null)
            {
                controller = curves.MuscleLayer.gameObject
                    .AddComponent<MuscleExpressionController2D>();
                changed = true;
            }

            controller.Configure(curves.MuscleLayer, curves.SkinLayer, curves);
            if (controller.NeedsRebake)
            {
                changed |= controller.BakeExpressionPoses();
            }

            if (!changed)
            {
                return;
            }

            EditorUtility.SetDirty(controller);
            Scene scene = controller.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded && !string.IsNullOrEmpty(scene.path))
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log(controller.BakeStatus, controller);
        }

        public override void OnInspectorGUI()
        {
            MuscleExpressionController2D controller =
                (MuscleExpressionController2D)target;
            EditorGUILayout.LabelField("Scene 表情调试", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            MuscleZoneChannel selectedChannel =
                (MuscleZoneChannel)EditorGUILayout.EnumPopup(
                "调试表情",
                sceneZoneChannel);
            if (EditorGUI.EndChangeCheck())
            {
                sceneZoneChannel = selectedChannel;
                SessionState.SetInt(SceneChannelSessionKey, (int)sceneZoneChannel);
                selectedFineTuneZone = 0;
                SceneView.RepaintAll();
            }

            EditorGUILayout.HelpBox(
                sceneZoneChannel == MuscleZoneChannel.None
                    ? "当前已关闭 Scene 调试。选择一个表情或口型后，只显示该通道的曲线、影响区和手柄。"
                    : "Scene 视图当前只显示：" + GetChannelDisplayName(sceneZoneChannel)
                        + "。切换通道会立即隐藏其它表情的调试细节。",
                sceneZoneChannel == MuscleZoneChannel.None
                    ? MessageType.None
                    : MessageType.Info);

            DrawRandomTwitchControls(controller);
            DrawQuickExpressionTuner(controller);
            DrawSymmetryTools(controller);

            EditorGUILayout.Space();
            showAdvancedSettings = EditorGUILayout.BeginFoldoutHeaderGroup(
                showAdvancedSettings,
                "高级设置 / 全部参数");
            if (showAdvancedSettings)
            {
                int poseHashBefore = controller.CalculatePoseHash();
                EditorGUI.BeginChangeCheck();
                DrawDefaultInspector();
                bool poseChanged = EditorGUI.EndChangeCheck()
                    && poseHashBefore != controller.CalculatePoseHash();
                if (poseChanged && !Application.isPlaying)
                {
                    QueueExpressionBake(controller);
                    EditorUtility.SetDirty(controller);
                    EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
                    SceneView.RepaintAll();
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.HelpBox(
                "快捷键：F1 归零；F2 眨眼；F3 开心；F4 悲伤；F5 愤怒；F6 痛苦；"
                + "F7 开关随机肌肉抽搐；Z/X/C/V/B 对应五种互斥口型。"
                + "彩色方块调中心，青色点调范围，"
                + "黄色球调拉力，口型的橙色方块调下巴开合。",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Rebake All Facial Expression Poses"))
                {
                    Undo.RecordObject(controller, "Bake Facial Feature Expressions");
                    if (controller.BakeExpressionPoses())
                    {
                        EditorUtility.SetDirty(controller);
                        EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
                    }
                }

                if (GUILayout.Button("Reset All Smooth Target Curves"))
                {
                    Undo.RecordObject(controller, "Reset Expression Target Curves");
                    controller.ResetExpressionTargetCurves();
                    controller.BakeExpressionPoses();
                    EditorUtility.SetDirty(controller);
                    EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
                    SceneView.RepaintAll();
                }

                if (GUILayout.Button("Reset 5 Speech Poses To Photo References"))
                {
                    Undo.RecordObject(controller, "Reset Five Speech Mouth Poses");
                    controller.ResetSpeechMouthPoses();
                    controller.BakeExpressionPoses();
                    EditorUtility.SetDirty(controller);
                    EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
                    SceneView.RepaintAll();
                }
            }

            EditorGUILayout.HelpBox(
                AreAllChannelsBaked(controller)
                    ? controller.BakeStatus
                    : "One or more expression channels are not baked yet.",
                AreAllChannelsBaked(controller)
                    ? MessageType.None
                    : MessageType.Warning);
            FlushPendingExpressionBakeIfIdle(controller);
        }

        private static void DrawRandomTwitchControls(
            MuscleExpressionController2D controller)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("运行时随机肌肉抽搐", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "随机模式会暂时忽略开心、悲伤、愤怒和痛苦模板，改用单侧局部肌肉脉冲。"
                + "眼睛开合和口型也会停用，只保留非对称肌肉抽搐。",
                MessageType.None);
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                Color previousColor = GUI.backgroundColor;
                GUI.backgroundColor = controller.RandomTwitching
                    ? new Color(1f, 0.45f, 0.32f)
                    : new Color(0.45f, 1f, 0.55f);
                if (GUILayout.Button(
                        controller.RandomTwitching
                            ? "停止随机抽搐（F7）"
                            : "开始随机抽搐（F7）",
                        GUILayout.Height(28f)))
                {
                    controller.ToggleRandomTwitching();
                }

                GUI.backgroundColor = previousColor;
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.LabelField(
                    "进入 Play Mode 后可点击按钮，或在 Game 窗口按 F7。",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawQuickExpressionTuner(
            MuscleExpressionController2D controller)
        {
            EditorGUILayout.Space();
            showQuickTuner = EditorGUILayout.BeginFoldoutHeaderGroup(
                showQuickTuner,
                "Quick Expression Tuner");
            if (!showQuickTuner)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EditorGUILayout.HelpBox(
                "先选择 Scene Muscle Channel，再在这里精调当前表情。"
                + "Scene 视图只显示当前通道的高对比度手柄；数值修改后会立即重新烘焙。"
                + "进入 Play Mode 后可用 Preview Weight 单独预览，不影响其它预设数据。",
                MessageType.Info);

            if (sceneZoneChannel == MuscleZoneChannel.None)
            {
                EditorGUILayout.LabelField(
                    "未选择调试表情，Scene 调试绘制保持关闭。",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            IList<FacialMuscleInfluenceZone2D> zones = GetSelectedZones(
                controller,
                sceneZoneChannel);
            SpeechMouthPose2D speechPose = GetSelectedSpeechPose(
                controller,
                sceneZoneChannel);
            if (speechPose != null)
            {
                EditorGUILayout.HelpBox(
                    "Reference target: "
                    + GetSpeechReferenceGuide(sceneZoneChannel),
                    MessageType.None);
            }

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                EditorGUI.BeginChangeCheck();
                float jawDrop = speechPose != null
                    ? EditorGUILayout.Slider(
                        new GUIContent("Jaw Drop UV", "下巴中央向下移动量。"),
                        speechPose.JawDropUv,
                        0f,
                        0.12f)
                    : 0f;
                float sideFollow = speechPose != null
                    ? EditorGUILayout.Slider(
                        new GUIContent("Jaw Side Follow", "左右下颚体跟随比例；接近 0 时两侧基本固定。"),
                        speechPose.JawSideFollow,
                        0f,
                        0.35f)
                    : 0f;
                float focusPower = speechPose != null
                    ? EditorGUILayout.Slider(
                        new GUIContent("Chin Focus", "越大越集中在下巴中央。"),
                        speechPose.JawChinFocusPower,
                        0.5f,
                        12f)
                    : 0f;
                float defaultWeight = speechPose != null
                    ? EditorGUILayout.Slider(
                        "Default Mouth Weight",
                        speechPose.DefaultWeight,
                        0f,
                        1.5f)
                    : 0f;

                FacialMuscleInfluenceZone2D selectedZone = null;
                Vector2 center = Vector2.zero;
                Vector2 radius = Vector2.one;
                Vector2 displacement = Vector2.zero;
                float strength = 1f;
                if (zones != null && zones.Count > 0)
                {
                    selectedFineTuneZone = Mathf.Clamp(
                        selectedFineTuneZone,
                        0,
                        zones.Count - 1);
                    string[] zoneLabels = new string[zones.Count];
                    for (int index = 0; index < zones.Count; index++)
                    {
                        zoneLabels[index] = (index + 1) + "  "
                            + (zones[index] != null ? zones[index].Label : "Missing Zone");
                    }

                    selectedFineTuneZone = EditorGUILayout.Popup(
                        "Fine Tune Zone",
                        selectedFineTuneZone,
                        zoneLabels);
                    selectedZone = zones[selectedFineTuneZone];
                    if (selectedZone != null)
                    {
                        center = EditorGUILayout.Vector2Field(
                            "Center UV",
                            selectedZone.CenterUv);
                        radius = EditorGUILayout.Vector2Field(
                            "XY Radius UV",
                            selectedZone.RadiusUv);
                        displacement = EditorGUILayout.Vector2Field(
                            "Pull / Displacement UV",
                            selectedZone.DisplacementUv);
                        strength = EditorGUILayout.Slider(
                            "Strength",
                            selectedZone.Strength,
                            0f,
                            2f);
                    }
                }

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(controller, "Fine Tune Facial Expression");
                    if (speechPose != null)
                    {
                        speechPose.JawDropUv = jawDrop;
                        speechPose.JawSideFollow = sideFollow;
                        speechPose.JawChinFocusPower = focusPower;
                        speechPose.DefaultWeight = defaultWeight;
                    }

                    if (selectedZone != null)
                    {
                        selectedZone.CenterUv = center;
                        selectedZone.RadiusUv = radius;
                        selectedZone.DisplacementUv = displacement;
                        selectedZone.Strength = strength;
                    }

                    QueueExpressionBake(controller);
                    EditorUtility.SetDirty(controller);
                    EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
                    SceneView.RepaintAll();
                }
            }

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                previewWeight = EditorGUILayout.Slider(
                    "Preview Weight",
                    previewWeight,
                    0f,
                    1.5f);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Solo Preview Selected"))
                {
                    ApplySoloPreview(controller, sceneZoneChannel, previewWeight);
                }

                if (GUILayout.Button("Neutral Preview"))
                {
                    controller.ResetAllExpressionWeights(true);
                }

                EditorGUILayout.EndHorizontal();
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.LabelField(
                    "预览按钮在 Play Mode 可用；编辑数值请在 Edit Mode 完成。",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawSymmetryTools(
            MuscleExpressionController2D controller)
        {
            EditorGUILayout.Space();
            showSymmetryTools = EditorGUILayout.BeginFoldoutHeaderGroup(
                showSymmetryTools,
                "对称编辑 / Symmetry");
            if (!showSymmetryTools)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EditorGUI.BeginChangeCheck();
            float newAxis = EditorGUILayout.Slider(
                new GUIContent(
                    "对称轴 UV X",
                    "0.5 是图像正中央。Scene 中的紫色竖线和菱形手柄表示当前对称轴。"),
                symmetryAxisUv,
                0.25f,
                0.75f);
            SymmetryCopyDirection newDirection =
                (SymmetryCopyDirection)EditorGUILayout.EnumPopup(
                    "复制方向",
                    symmetryDirection);
            if (EditorGUI.EndChangeCheck())
            {
                symmetryAxisUv = newAxis;
                symmetryDirection = newDirection;
                SaveSymmetrySessionState();
                SceneView.RepaintAll();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("轴线回到 0.5"))
            {
                symmetryAxisUv = 0.5f;
                SaveSymmetrySessionState();
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            IList<FacialMuscleInfluenceZone2D> zones = GetSelectedZones(
                controller,
                sceneZoneChannel);
            bool hasZones = zones != null && zones.Count > 0;
            bool hasCurve = sceneZoneChannel == MuscleZoneChannel.EyesBlink
                || sceneZoneChannel == MuscleZoneChannel.Smile;
            string directionLabel = symmetryDirection
                == SymmetryCopyDirection.LeftToRight
                ? "左 → 右"
                : "右 → 左";

            using (new EditorGUI.DisabledScope(
                Application.isPlaying || sceneZoneChannel == MuscleZoneChannel.None))
            {
                if (hasZones)
                {
                    selectedFineTuneZone = Mathf.Clamp(
                        selectedFineTuneZone,
                        0,
                        zones.Count - 1);
                    int strictPairCount = CountStrictZonePairs(
                        zones,
                        symmetryAxisUv);
                    int singletonCount = CountSingletonZones(
                        zones,
                        symmetryAxisUv);
                    int skippedCount = Mathf.Max(
                        0,
                        zones.Count - strictPairCount * 2 - singletonCount);
                    EditorGUILayout.LabelField(
                        "严格配对：" + strictPairCount + " 对 | 单节点："
                        + singletonCount + " 个 | 跳过：" + skippedCount + " 个",
                        EditorStyles.miniBoldLabel);
                    ZoneSymmetryKind selectedKind = GetZoneSymmetryKind(
                        zones,
                        selectedFineTuneZone,
                        symmetryAxisUv,
                        out _);
                    using (new EditorGUI.DisabledScope(
                        selectedKind == ZoneSymmetryKind.Invalid))
                    {
                        if (GUILayout.Button(
                            selectedKind == ZoneSymmetryKind.Singleton
                                ? "将 Fine Tune Zone 选中单节点移到对称轴"
                                : "仅对称 Fine Tune Zone 选中节点（"
                                    + directionLabel + "）"))
                        {
                            SymmetrizeSelectedZone(controller, zones);
                        }
                    }

                    using (new EditorGUI.DisabledScope(
                        strictPairCount == 0 && singletonCount == 0))
                    {
                        if (GUILayout.Button(
                            "对称全部：成对复制 + 单节点居中（"
                            + directionLabel + "）"))
                        {
                            SymmetrizeAllZones(controller, zones);
                        }
                    }
                }

                if (hasCurve && GUILayout.Button(
                        "对称当前通道目标曲线（" + directionLabel + "）"))
                {
                    MirrorSelectedTargetCurve(controller);
                }
            }

            if (!hasZones && !hasCurve)
            {
                EditorGUILayout.HelpBox(
                    "当前通道没有可对称的数据。请先选择眨眼、开心、情绪或口型。",
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "只有名称严格成对的 Left/Right 节点才会参与对称，并且要求左右各恰好一个。"
                    + "名称唯一的单节点会移动到对称轴；重名或无法唯一判断的节点仍会跳过。"
                    + "操作支持 Undo。",
                    MessageType.None);
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private static SpeechMouthPose2D GetSelectedSpeechPose(
            MuscleExpressionController2D controller,
            MuscleZoneChannel channel)
        {
            int poseIndex = GetSpeechPoseIndex(channel);
            IList<SpeechMouthPose2D> poses = controller.SpeechMouthPoses;
            return poseIndex >= 0 && poseIndex < poses.Count
                ? poses[poseIndex]
                : null;
        }

        private void SaveSymmetrySessionState()
        {
            SessionState.SetFloat(SymmetryAxisSessionKey, symmetryAxisUv);
            SessionState.SetInt(
                SymmetryDirectionSessionKey,
                (int)symmetryDirection);
        }

        private void SymmetrizeSelectedZone(
            MuscleExpressionController2D controller,
            IList<FacialMuscleInfluenceZone2D> zones)
        {
            ZoneSymmetryKind kind = GetZoneSymmetryKind(
                zones,
                selectedFineTuneZone,
                symmetryAxisUv,
                out _);
            if (kind == ZoneSymmetryKind.Singleton)
            {
                Undo.RecordObject(controller, "Center Singleton Facial Muscle Zone");
                MoveZoneCenterToAxis(
                    zones[selectedFineTuneZone],
                    symmetryAxisUv);
                CommitSymmetryChange(controller);
                return;
            }

            if (kind != ZoneSymmetryKind.Paired
                || !TryResolveSelectedSymmetryPair(
                    zones,
                    selectedFineTuneZone,
                    symmetryAxisUv,
                    symmetryDirection,
                    out int sourceIndex,
                    out int targetIndex))
            {
                return;
            }

            Undo.RecordObject(controller, "Mirror Selected Facial Muscle Zone");
            CopyMirroredZone(
                zones[sourceIndex],
                zones[targetIndex],
                symmetryAxisUv);
            CommitSymmetryChange(controller);
        }

        private void SymmetrizeAllZones(
            MuscleExpressionController2D controller,
            IList<FacialMuscleInfluenceZone2D> zones)
        {
            if (zones == null || zones.Count == 0)
            {
                return;
            }

            Undo.RecordObject(controller, "Symmetrize Facial Muscle Zones");
            bool[] claimedTargets = new bool[zones.Count];
            int mirroredCount = 0;
            int centeredCount = 0;
            for (int zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
            {
                FacialMuscleInfluenceZone2D zone = zones[zoneIndex];
                ZoneSymmetryKind kind = GetZoneSymmetryKind(
                    zones,
                    zoneIndex,
                    symmetryAxisUv,
                    out _);
                if (kind == ZoneSymmetryKind.Singleton)
                {
                    MoveZoneCenterToAxis(zone, symmetryAxisUv);
                    centeredCount++;
                    continue;
                }

                if (kind != ZoneSymmetryKind.Paired
                    || !TryGetLabelSide(zone.Label, out SymmetryLabelSide side)
                    || !IsSourceLabelSide(side, symmetryDirection))
                {
                    continue;
                }

                int targetIndex = FindMirroredZoneIndex(
                    zones,
                    zoneIndex,
                    symmetryAxisUv,
                    claimedTargets);
                if (targetIndex < 0)
                {
                    continue;
                }

                CopyMirroredZone(zone, zones[targetIndex], symmetryAxisUv);
                claimedTargets[targetIndex] = true;
                mirroredCount++;
            }

            if (mirroredCount > 0 || centeredCount > 0)
            {
                CommitSymmetryChange(controller);
            }
        }

        private void MirrorSelectedTargetCurve(
            MuscleExpressionController2D controller)
        {
            Undo.RecordObject(controller, "Mirror Facial Expression Target Curve");
            bool changed = false;
            if (sceneZoneChannel == MuscleZoneChannel.EyesBlink)
            {
                FacialCutCurve2D first = controller.GetOpenEyelidTarget(0);
                FacialCutCurve2D second = controller.GetOpenEyelidTarget(1);
                if (first != null && second != null)
                {
                    bool firstIsLeft = GetAverageCurveX(first) <= GetAverageCurveX(second);
                    FacialCutCurve2D left = firstIsLeft ? first : second;
                    FacialCutCurve2D right = firstIsLeft ? second : first;
                    FacialCutCurve2D source = symmetryDirection
                        == SymmetryCopyDirection.LeftToRight ? left : right;
                    FacialCutCurve2D target = symmetryDirection
                        == SymmetryCopyDirection.LeftToRight ? right : left;
                    changed = CopyMirroredCurve(source, target, symmetryAxisUv);
                }
            }
            else if (sceneZoneChannel == MuscleZoneChannel.Smile)
            {
                changed = MirrorCurveHalf(
                    controller.GetSmileTarget(),
                    symmetryAxisUv,
                    symmetryDirection);
            }

            if (changed)
            {
                CommitSymmetryChange(controller);
            }
        }

        private static bool TryResolveSelectedSymmetryPair(
            IList<FacialMuscleInfluenceZone2D> zones,
            int selectedIndex,
            float axisUv,
            SymmetryCopyDirection direction,
            out int sourceIndex,
            out int targetIndex)
        {
            sourceIndex = -1;
            targetIndex = -1;
            if (zones == null || selectedIndex < 0 || selectedIndex >= zones.Count
                || zones[selectedIndex] == null)
            {
                return false;
            }

            FacialMuscleInfluenceZone2D selected = zones[selectedIndex];
            if (!TryGetLabelSide(selected.Label, out SymmetryLabelSide selectedSide))
            {
                return false;
            }

            if (IsSourceLabelSide(selectedSide, direction))
            {
                sourceIndex = selectedIndex;
                targetIndex = FindMirroredZoneIndex(
                    zones,
                    sourceIndex,
                    axisUv,
                    null);
            }
            else
            {
                targetIndex = selectedIndex;
                sourceIndex = FindMirroredZoneIndex(
                    zones,
                    targetIndex,
                    axisUv,
                    null);
            }

            return sourceIndex >= 0 && targetIndex >= 0;
        }

        private static int CountStrictZonePairs(
            IList<FacialMuscleInfluenceZone2D> zones,
            float axisUv)
        {
            if (zones == null)
            {
                return 0;
            }

            int count = 0;
            for (int index = 0; index < zones.Count; index++)
            {
                FacialMuscleInfluenceZone2D zone = zones[index];
                if (zone != null
                    && TryGetLabelSide(zone.Label, out SymmetryLabelSide side)
                    && side == SymmetryLabelSide.Left
                    && FindMirroredZoneIndex(zones, index, axisUv, null) >= 0)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountSingletonZones(
            IList<FacialMuscleInfluenceZone2D> zones,
            float axisUv)
        {
            if (zones == null)
            {
                return 0;
            }

            int count = 0;
            for (int index = 0; index < zones.Count; index++)
            {
                if (GetZoneSymmetryKind(
                        zones,
                        index,
                        axisUv,
                        out _) == ZoneSymmetryKind.Singleton)
                {
                    count++;
                }
            }

            return count;
        }

        private static ZoneSymmetryKind GetZoneSymmetryKind(
            IList<FacialMuscleInfluenceZone2D> zones,
            int zoneIndex,
            float axisUv,
            out int counterpartIndex)
        {
            counterpartIndex = -1;
            if (zones == null || zoneIndex < 0 || zoneIndex >= zones.Count
                || zones[zoneIndex] == null)
            {
                return ZoneSymmetryKind.Invalid;
            }

            counterpartIndex = FindMirroredZoneIndex(
                zones,
                zoneIndex,
                axisUv,
                null);
            if (counterpartIndex >= 0)
            {
                return ZoneSymmetryKind.Paired;
            }

            string groupKey = GetSingletonGroupKey(zones[zoneIndex].Label);
            if (string.IsNullOrEmpty(groupKey))
            {
                return ZoneSymmetryKind.Invalid;
            }

            int matchingCount = 0;
            for (int index = 0; index < zones.Count; index++)
            {
                if (zones[index] != null
                    && GetSingletonGroupKey(zones[index].Label) == groupKey)
                {
                    matchingCount++;
                }
            }

            return matchingCount == 1
                ? ZoneSymmetryKind.Singleton
                : ZoneSymmetryKind.Invalid;
        }

        private static int FindMirroredZoneIndex(
            IList<FacialMuscleInfluenceZone2D> zones,
            int sourceIndex,
            float axisUv,
            bool[] excluded)
        {
            if (zones == null || sourceIndex < 0 || sourceIndex >= zones.Count
                || zones[sourceIndex] == null)
            {
                return -1;
            }

            FacialMuscleInfluenceZone2D source = zones[sourceIndex];
            if (!TryGetLabelSide(source.Label, out SymmetryLabelSide sourceSide))
            {
                return -1;
            }

            string sourceKey = GetSymmetryLabelKey(source.Label);
            if (string.IsNullOrEmpty(sourceKey))
            {
                return -1;
            }

            int leftCount = 0;
            int rightCount = 0;
            int leftIndex = -1;
            int rightIndex = -1;

            for (int index = 0; index < zones.Count; index++)
            {
                FacialMuscleInfluenceZone2D candidate = zones[index];
                if (candidate == null
                    || GetSymmetryLabelKey(candidate.Label) != sourceKey
                    || !TryGetLabelSide(
                        candidate.Label,
                        out SymmetryLabelSide candidateSide))
                {
                    continue;
                }

                if (candidateSide == SymmetryLabelSide.Left)
                {
                    leftCount++;
                    leftIndex = index;
                }
                else if (candidateSide == SymmetryLabelSide.Right)
                {
                    rightCount++;
                    rightIndex = index;
                }
            }

            if (leftCount != 1 || rightCount != 1
                || leftIndex < 0 || rightIndex < 0
                || zones[leftIndex].CenterUv.x >= axisUv
                || zones[rightIndex].CenterUv.x <= axisUv)
            {
                return -1;
            }

            int counterpartIndex = sourceSide == SymmetryLabelSide.Left
                ? rightIndex
                : leftIndex;
            if (counterpartIndex == sourceIndex
                || excluded != null
                    && counterpartIndex < excluded.Length
                    && excluded[counterpartIndex])
            {
                return -1;
            }

            return counterpartIndex;
        }

        private static void CopyMirroredZone(
            FacialMuscleInfluenceZone2D source,
            FacialMuscleInfluenceZone2D target,
            float axisUv)
        {
            target.CenterUv = new Vector2(
                MirrorX(source.CenterUv.x, axisUv),
                source.CenterUv.y);
            target.RadiusUv = source.RadiusUv;
            target.DisplacementUv = new Vector2(
                -source.DisplacementUv.x,
                source.DisplacementUv.y);
            target.Strength = source.Strength;
        }

        private static void MoveZoneCenterToAxis(
            FacialMuscleInfluenceZone2D zone,
            float axisUv)
        {
            if (zone == null)
            {
                return;
            }

            zone.CenterUv = new Vector2(axisUv, zone.CenterUv.y);
        }

        private static bool CopyMirroredCurve(
            FacialCutCurve2D source,
            FacialCutCurve2D target,
            float axisUv)
        {
            if (source == null || target == null || source.ControlPoints == null
                || source.ControlPoints.Count == 0 || target.ControlPoints == null)
            {
                return false;
            }

            Vector2[] mirrored = new Vector2[source.ControlPoints.Count];
            for (int index = 0; index < mirrored.Length; index++)
            {
                Vector2 point = source.ControlPoints[mirrored.Length - 1 - index];
                mirrored[index] = new Vector2(
                    MirrorX(point.x, axisUv),
                    point.y);
            }

            target.ControlPoints.Clear();
            target.ControlPoints.AddRange(mirrored);
            return true;
        }

        private static bool MirrorCurveHalf(
            FacialCutCurve2D curve,
            float axisUv,
            SymmetryCopyDirection direction)
        {
            if (curve == null || curve.ControlPoints == null
                || curve.ControlPoints.Count < 2)
            {
                return false;
            }

            List<Vector2> points = curve.ControlPoints;
            bool changed = false;
            for (int firstIndex = 0;
                firstIndex < points.Count / 2;
                firstIndex++)
            {
                int secondIndex = points.Count - 1 - firstIndex;
                Vector2 first = points[firstIndex];
                Vector2 second = points[secondIndex];
                int leftIndex;
                int rightIndex;
                if (first.x < axisUv && second.x > axisUv)
                {
                    leftIndex = firstIndex;
                    rightIndex = secondIndex;
                }
                else if (second.x < axisUv && first.x > axisUv)
                {
                    leftIndex = secondIndex;
                    rightIndex = firstIndex;
                }
                else
                {
                    continue;
                }

                int sourceIndex = direction == SymmetryCopyDirection.LeftToRight
                    ? leftIndex
                    : rightIndex;
                int targetIndex = direction == SymmetryCopyDirection.LeftToRight
                    ? rightIndex
                    : leftIndex;
                Vector2 source = points[sourceIndex];
                points[targetIndex] = new Vector2(
                    MirrorX(source.x, axisUv),
                    source.y);
                changed = true;
            }

            if (points.Count % 2 == 1)
            {
                int centerIndex = points.Count / 2;
                Vector2 center = points[centerIndex];
                points[centerIndex] = new Vector2(axisUv, center.y);
                changed = true;
            }

            return changed;
        }

        private static float GetAverageCurveX(FacialCutCurve2D curve)
        {
            if (curve == null || curve.ControlPoints == null
                || curve.ControlPoints.Count == 0)
            {
                return 0.5f;
            }

            float total = 0f;
            for (int index = 0; index < curve.ControlPoints.Count; index++)
            {
                total += curve.ControlPoints[index].x;
            }

            return total / curve.ControlPoints.Count;
        }

        private static float MirrorX(float x, float axisUv)
        {
            return Mathf.Clamp01(axisUv * 2f - x);
        }

        private static bool IsSourceLabelSide(
            SymmetryLabelSide side,
            SymmetryCopyDirection direction)
        {
            return direction == SymmetryCopyDirection.LeftToRight
                ? side == SymmetryLabelSide.Left
                : side == SymmetryLabelSide.Right;
        }

        private static bool TryGetLabelSide(
            string label,
            out SymmetryLabelSide side)
        {
            return TryGetLabelSideAndKey(label, out side, out _);
        }

        private static bool TryGetLabelSideAndKey(
            string label,
            out SymmetryLabelSide side,
            out string key)
        {
            string normalized = (label ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace("-", " ")
                .Replace("_", " ")
                .Replace("/", " ");
            string[] tokens = normalized.Split(
                new[] { ' ', '\t' },
                System.StringSplitOptions.RemoveEmptyEntries);
            int leftCount = 0;
            int rightCount = 0;
            List<string> keyTokens = new List<string>(tokens.Length);
            for (int index = 0; index < tokens.Length; index++)
            {
                if (tokens[index] == "left")
                {
                    leftCount++;
                }
                else if (tokens[index] == "right")
                {
                    rightCount++;
                }
                else
                {
                    keyTokens.Add(tokens[index]);
                }
            }

            if (leftCount + rightCount != 1 || keyTokens.Count == 0)
            {
                side = SymmetryLabelSide.None;
                key = string.Empty;
                return false;
            }

            side = leftCount == 1
                ? SymmetryLabelSide.Left
                : SymmetryLabelSide.Right;
            key = string.Join("|", keyTokens);
            return true;
        }

        private static string GetSymmetryLabelKey(string label)
        {
            return TryGetLabelSideAndKey(label, out _, out string key)
                ? key
                : string.Empty;
        }

        private static string GetSingletonGroupKey(string label)
        {
            if (TryGetLabelSideAndKey(label, out _, out string pairedKey))
            {
                return "paired:" + pairedKey;
            }

            string normalized = (label ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace("-", " ")
                .Replace("_", " ")
                .Replace("/", " ");
            string[] tokens = normalized.Split(
                new[] { ' ', '\t' },
                System.StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0
                ? "single:" + string.Join("|", tokens)
                : string.Empty;
        }

        private static void CommitSymmetryChange(
            MuscleExpressionController2D controller)
        {
            QueueExpressionBake(controller);
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            SceneView.RepaintAll();
        }

        private static int GetSpeechPoseIndex(MuscleZoneChannel channel)
        {
            switch (channel)
            {
                case MuscleZoneChannel.Speech1_PuckerU:
                    return 0;
                case MuscleZoneChannel.Speech2_OpenTeeth:
                    return 1;
                case MuscleZoneChannel.Speech3_NarrowTeethE:
                    return 2;
                case MuscleZoneChannel.Speech4_WideAh:
                    return 3;
                case MuscleZoneChannel.Speech5_RoundO:
                    return 4;
                default:
                    return -1;
            }
        }

        private static void ApplySoloPreview(
            MuscleExpressionController2D controller,
            MuscleZoneChannel channel,
            float weight)
        {
            controller.ResetAllExpressionWeights(true);
            switch (channel)
            {
                case MuscleZoneChannel.EyesBlink:
                    controller.SetEyeWeight(weight, true);
                    break;
                case MuscleZoneChannel.Smile:
                    controller.SetSmileWeight(weight, true);
                    break;
                case MuscleZoneChannel.Sadness:
                    controller.SetSadnessWeight(weight, true);
                    break;
                case MuscleZoneChannel.Anger:
                    controller.SetAngerWeight(weight, true);
                    break;
                case MuscleZoneChannel.Pain:
                    controller.SetPainWeight(weight, true);
                    break;
                default:
                    int poseIndex = GetSpeechPoseIndex(channel);
                    if (poseIndex >= 0)
                    {
                        controller.SetSpeechPose(poseIndex, weight, true);
                    }

                    break;
            }
        }

        private void OnSceneGUI()
        {
            if (sceneZoneChannel == MuscleZoneChannel.None)
            {
                return;
            }

            MuscleExpressionController2D controller =
                (MuscleExpressionController2D)target;
            FacialFeaturePrecut2D facial = controller.FacialCurves;
            if (facial == null || facial.SkinLayer == null)
            {
                return;
            }

            CompareFunction previousZTest = Handles.zTest;
            try
            {
                Handles.zTest = CompareFunction.Always;
                if (showSymmetryTools)
                {
                    DrawSymmetryAxis(facial);
                }

                switch (sceneZoneChannel)
                {
                    case MuscleZoneChannel.EyesBlink:
                        for (int eye = 0; eye < 2; eye++)
                        {
                            DrawSourceCurve(facial, eye);
                            DrawTargetCurve(controller, facial, eye);
                        }
                        break;
                    case MuscleZoneChannel.Smile:
                        DrawSourceCurve(facial, 2);
                        DrawSmileTarget(controller, facial);
                        break;
                    case MuscleZoneChannel.Speech1_PuckerU:
                    case MuscleZoneChannel.Speech2_OpenTeeth:
                    case MuscleZoneChannel.Speech3_NarrowTeethE:
                    case MuscleZoneChannel.Speech4_WideAh:
                    case MuscleZoneChannel.Speech5_RoundO:
                        DrawSourceCurve(facial, 2);
                        break;
                }

                IList<FacialMuscleInfluenceZone2D> zones = GetSelectedZones(
                    controller,
                    sceneZoneChannel);
                Color zoneColor = GetChannelColor(sceneZoneChannel);
                DrawInfluenceZones(
                    controller,
                    facial,
                    zones,
                    zoneColor,
                    sceneZoneChannel.ToString());
                SpeechMouthPose2D speechPose = GetSelectedSpeechPose(
                    controller,
                    sceneZoneChannel);
                if (speechPose != null)
                {
                    DrawSpeechJawDropHandle(controller, facial, speechPose);
                }

                Handles.BeginGUI();
                GUILayout.BeginArea(
                    new Rect(12f, 12f, 640f, speechPose != null ? 148f : 124f),
                    "单通道表情调试",
                    GUI.skin.window);
                GUILayout.Label("当前：" + GetChannelDisplayName(sceneZoneChannel)
                    + "（其它表情调试已隐藏）");
                GUILayout.Label("灰线 = 中性切口；彩色线 = 目标曲线");
                GUILayout.Label("彩色方块 = 中心 | 青色 = X/Y 范围 | 黄色 = 拉力 | 橙色 = 下巴开合");
                if (showSymmetryTools)
                {
                    GUILayout.Label("紫色竖线 = 对称轴 | "
                        + (symmetryDirection == SymmetryCopyDirection.LeftToRight
                            ? "左侧 → 右侧"
                            : "右侧 → 左侧"));
                }
                if (speechPose != null)
                {
                    GUILayout.Label("Reference target: "
                        + GetSpeechReferenceGuide(sceneZoneChannel));
                }
                GUILayout.EndArea();
                Handles.EndGUI();
            }
            finally
            {
                Handles.zTest = previousZTest;
            }

            FlushPendingExpressionBakeIfIdle(controller);
        }

        private void DrawSymmetryAxis(FacialFeaturePrecut2D facial)
        {
            Color axisColor = new Color(0.95f, 0.2f, 1f, 1f);
            DrawUvPolyline(
                facial,
                new[]
                {
                    new Vector2(symmetryAxisUv, 0.04f),
                    new Vector2(symmetryAxisUv, 0.96f)
                },
                axisColor,
                2f);

            Vector2 handleUv = new Vector2(symmetryAxisUv, 0.9f);
            if (TryMoveUvHandle(
                    facial,
                    handleUv,
                    axisColor,
                    Handles.RectangleHandleCap,
                    0.075f,
                    out Vector2 movedUv))
            {
                symmetryAxisUv = Mathf.Clamp(movedUv.x, 0.25f, 0.75f);
                SaveSymmetrySessionState();
                SceneView.RepaintAll();
            }

            if (Event.current.type == EventType.Repaint
                && facial.TryNormalizedUvToWorld(
                    new Vector2(symmetryAxisUv, 0.94f),
                    out Vector3 labelWorld))
            {
                GUIStyle style = new GUIStyle(EditorStyles.boldLabel);
                style.normal.textColor = axisColor;
                Handles.Label(
                    OffsetTowardCamera(labelWorld),
                    "Symmetry X " + symmetryAxisUv.ToString("0.000"),
                    style);
            }
        }

        private static void DrawSpeechJawDropHandle(
            MuscleExpressionController2D controller,
            FacialFeaturePrecut2D facial,
            SpeechMouthPose2D pose)
        {
            Vector2 originUv = new Vector2(0.72f, 0.34f);
            Vector2 handleUv = originUv + Vector2.down * pose.JawDropUv;
            DrawUvPolyline(
                facial,
                new[] { originUv, handleUv },
                new Color(1f, 0.48f, 0.04f, 1f),
                3f);

            if (TryMoveUvHandle(
                    facial,
                    handleUv,
                    new Color(1f, 0.48f, 0.04f, 1f),
                    Handles.CubeHandleCap,
                    0.085f,
                    out Vector2 movedHandle))
            {
                RecordZoneChange(controller, "Edit Speech Jaw Drop");
                pose.JawDropUv = Mathf.Clamp(
                    originUv.y - movedHandle.y,
                    0f,
                    0.12f);
            }

            if (Event.current.type == EventType.Repaint
                && facial.TryNormalizedUvToWorld(handleUv, out Vector3 handleWorld))
            {
                GUIStyle labelStyle = new GUIStyle(EditorStyles.boldLabel);
                labelStyle.normal.textColor = new Color(1f, 0.68f, 0.18f, 1f);
                Handles.Label(
                    OffsetTowardCamera(handleWorld),
                    "Jaw Drop  " + pose.JawDropUv.ToString("0.000"),
                    labelStyle);
            }
        }

        private static string GetSpeechReferenceGuide(MuscleZoneChannel channel)
        {
            switch (channel)
            {
                case MuscleZoneChannel.Speech1_PuckerU:
                    return "U / tiny center slit, teeth fully covered";
                case MuscleZoneChannel.Speech2_OpenTeeth:
                    return "A-E / medium opening, both dental rows visible";
                case MuscleZoneChannel.Speech3_NarrowTeethE:
                    return "E / shallow and wide, upper teeth visible, lower teeth covered";
                case MuscleZoneChannel.Speech4_WideAh:
                    return "Ah / largest oval opening, both dental rows at the rim";
                case MuscleZoneChannel.Speech5_RoundO:
                    return "O / round opening, gathered lips cover the teeth";
                default:
                    return string.Empty;
            }
        }

        private static string GetChannelDisplayName(MuscleZoneChannel channel)
        {
            switch (channel)
            {
                case MuscleZoneChannel.EyesBlink:
                    return "眨眼 / Eyelids";
                case MuscleZoneChannel.Smile:
                    return "开心 / Smile";
                case MuscleZoneChannel.Sadness:
                    return "悲伤 / Sadness";
                case MuscleZoneChannel.Anger:
                    return "愤怒 / Anger";
                case MuscleZoneChannel.Pain:
                    return "痛苦 / Pain";
                case MuscleZoneChannel.Speech1_PuckerU:
                    return "口型 1 / Pucker U";
                case MuscleZoneChannel.Speech2_OpenTeeth:
                    return "口型 2 / Open A-E";
                case MuscleZoneChannel.Speech3_NarrowTeethE:
                    return "口型 3 / Narrow E";
                case MuscleZoneChannel.Speech4_WideAh:
                    return "口型 4 / Wide Ah";
                case MuscleZoneChannel.Speech5_RoundO:
                    return "口型 5 / Round O";
                default:
                    return "关闭调试";
            }
        }

        private static void DrawSourceCurve(FacialFeaturePrecut2D facial, int eye)
        {
            DrawUvPolyline(
                facial,
                facial.GetSampledEditableCurve(eye),
                new Color(0.2f, 0.2f, 0.2f, 0.85f),
                3f);
        }

        private static void DrawTargetCurve(
            MuscleExpressionController2D controller,
            FacialFeaturePrecut2D facial,
            int eye)
        {
            FacialCutCurve2D targetCurve = controller.GetOpenEyelidTarget(eye);
            DrawEditableTargetCurve(
                controller,
                facial,
                targetCurve,
                controller.GetSampledOpenEyelidTarget(eye));
        }

        private static void DrawSmileTarget(
            MuscleExpressionController2D controller,
            FacialFeaturePrecut2D facial)
        {
            DrawEditableTargetCurve(
                controller,
                facial,
                controller.GetSmileTarget(),
                controller.GetSampledSmileTarget());
        }

        private static void DrawEditableTargetCurve(
            MuscleExpressionController2D controller,
            FacialFeaturePrecut2D facial,
            FacialCutCurve2D targetCurve,
            IList<Vector2> sampledCurve)
        {
            if (targetCurve == null || targetCurve.ControlPoints == null)
            {
                return;
            }

            DrawUvPolyline(
                facial,
                sampledCurve,
                targetCurve.Color,
                5f);

            for (int index = 0; index < targetCurve.ControlPoints.Count; index++)
            {
                if (!facial.TryNormalizedUvToWorld(
                        targetCurve.ControlPoints[index],
                        out Vector3 surfaceWorld))
                {
                    continue;
                }

                Vector3 displayWorld = OffsetTowardCamera(surfaceWorld);
                Vector3 displayOffset = displayWorld - surfaceWorld;
                float size = HandleUtility.GetHandleSize(displayWorld) * 0.07f;
                Handles.color = Color.black;
                Handles.DotHandleCap(
                    0,
                    displayWorld,
                    Quaternion.identity,
                    size * 1.65f,
                    EventType.Repaint);
                Handles.color = targetCurve.Color;
                EditorGUI.BeginChangeCheck();
                Transform skinTransform = facial.SkinLayer.transform;
                Vector3 moved = Handles.Slider2D(
                    displayWorld,
                    skinTransform.TransformDirection(Vector3.forward),
                    skinTransform.TransformDirection(Vector3.right),
                    skinTransform.TransformDirection(Vector3.up),
                    size,
                    index == 0 || index == targetCurve.ControlPoints.Count - 1
                        ? Handles.RectangleHandleCap
                        : Handles.DotHandleCap,
                    Vector2.zero);
                if (!EditorGUI.EndChangeCheck()
                    || !facial.TryWorldToNormalizedUv(
                        moved - displayOffset,
                        out Vector2 movedUv))
                {
                    continue;
                }

                Undo.RecordObject(controller, "Move Open Eyelid Target Point");
                targetCurve.ControlPoints[index] = new Vector2(
                    Mathf.Clamp01(movedUv.x),
                    Mathf.Clamp01(movedUv.y));
                QueueExpressionBake(controller);
                EditorUtility.SetDirty(controller);
                EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
                SceneView.RepaintAll();
            }
        }

        private static bool AreAllChannelsBaked(
            MuscleExpressionController2D controller)
        {
            return controller.HasBakedOpenEyesPose
                && controller.HasBakedSmilePose
                && controller.HasBakedSadnessPose
                && controller.HasBakedAngerPose
                && controller.HasBakedPainPose
                && controller.HasBakedSpeechPoses;
        }

        private static IList<FacialMuscleInfluenceZone2D> GetSelectedZones(
            MuscleExpressionController2D controller,
            MuscleZoneChannel channel)
        {
            switch (channel)
            {
                case MuscleZoneChannel.None:
                case MuscleZoneChannel.EyesBlink:
                    return null;
                case MuscleZoneChannel.Sadness:
                    return controller.SadnessMuscleZones;
                case MuscleZoneChannel.Anger:
                    return controller.AngerMuscleZones;
                case MuscleZoneChannel.Pain:
                    return controller.PainMuscleZones;
                case MuscleZoneChannel.Speech1_PuckerU:
                    return GetSpeechZones(controller, 0);
                case MuscleZoneChannel.Speech2_OpenTeeth:
                    return GetSpeechZones(controller, 1);
                case MuscleZoneChannel.Speech3_NarrowTeethE:
                    return GetSpeechZones(controller, 2);
                case MuscleZoneChannel.Speech4_WideAh:
                    return GetSpeechZones(controller, 3);
                case MuscleZoneChannel.Speech5_RoundO:
                    return GetSpeechZones(controller, 4);
                case MuscleZoneChannel.Smile:
                    return controller.SmileMuscleZones;
                default:
                    return null;
            }
        }

        private static IList<FacialMuscleInfluenceZone2D> GetSpeechZones(
            MuscleExpressionController2D controller,
            int poseIndex)
        {
            IList<SpeechMouthPose2D> poses = controller.SpeechMouthPoses;
            return poseIndex >= 0 && poseIndex < poses.Count
                && poses[poseIndex] != null
                    ? poses[poseIndex].MuscleZones
                    : null;
        }

        private static Color GetChannelColor(MuscleZoneChannel channel)
        {
            switch (channel)
            {
                case MuscleZoneChannel.Sadness:
                    return new Color(0.05f, 0.5f, 1f, 1f);
                case MuscleZoneChannel.Anger:
                    return new Color(1f, 0.18f, 0.02f, 1f);
                case MuscleZoneChannel.Pain:
                    return new Color(0.72f, 0.12f, 1f, 1f);
                case MuscleZoneChannel.Speech1_PuckerU:
                    return new Color(0.1f, 1f, 0.18f, 1f);
                case MuscleZoneChannel.Speech2_OpenTeeth:
                    return new Color(1f, 0.48f, 0.03f, 1f);
                case MuscleZoneChannel.Speech3_NarrowTeethE:
                    return new Color(0f, 0.9f, 0.88f, 1f);
                case MuscleZoneChannel.Speech4_WideAh:
                    return new Color(1f, 0.05f, 0.08f, 1f);
                case MuscleZoneChannel.Speech5_RoundO:
                    return new Color(1f, 0.92f, 0.02f, 1f);
                default:
                    return new Color(1f, 0.05f, 0.72f, 1f);
            }
        }

        private static void DrawInfluenceZones(
            MuscleExpressionController2D controller,
            FacialFeaturePrecut2D facial,
            IList<FacialMuscleInfluenceZone2D> zones,
            Color zoneColor,
            string channelLabel)
        {
            if (zones == null)
            {
                return;
            }

                const int ellipseSamples = 24;
            for (int zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
            {
                FacialMuscleInfluenceZone2D zone = zones[zoneIndex];
                if (Event.current.type == EventType.Repaint)
                {
                    List<Vector2> ellipse = new List<Vector2>(ellipseSamples + 1);
                    for (int sample = 0; sample <= ellipseSamples; sample++)
                    {
                        float angle = sample / (float)ellipseSamples * Mathf.PI * 2f;
                        ellipse.Add(zone.CenterUv + new Vector2(
                            Mathf.Cos(angle) * zone.RadiusUv.x,
                            Mathf.Sin(angle) * zone.RadiusUv.y));
                    }

                    DrawUvPolyline(facial, ellipse, zoneColor, 1.5f);
                }

                if (facial.TryNormalizedUvToWorld(zone.CenterUv, out Vector3 center)
                    && facial.TryNormalizedUvToWorld(
                        zone.CenterUv + zone.DisplacementUv * zone.Strength,
                        out Vector3 destination))
                {
                    Vector3 displayCenter = OffsetTowardCamera(center);
                    Vector3 displayDestination = OffsetTowardCamera(destination);
                    if (Event.current.type == EventType.Repaint)
                    {
                        Handles.color = Color.black;
                        Handles.DrawLine(
                            displayCenter,
                            displayDestination,
                            6f);
                        Handles.color = new Color(1f, 0.92f, 0.05f, 1f);
                        Handles.DrawLine(displayCenter, displayDestination, 3f);
                    }

                    DrawZoneHandles(
                        controller,
                        facial,
                        zone,
                        zoneIndex,
                        zoneColor,
                        channelLabel);

                    if (Event.current.type == EventType.Repaint)
                    {
                        GUIStyle labelStyle = new GUIStyle(EditorStyles.boldLabel);
                        labelStyle.normal.textColor = Color.white;
                        Handles.Label(
                            displayCenter,
                            (zoneIndex + 1) + "  " + zone.Label,
                            labelStyle);
                    }
                }
            }
        }

        private static void DrawZoneHandles(
            MuscleExpressionController2D controller,
            FacialFeaturePrecut2D facial,
            FacialMuscleInfluenceZone2D zone,
            int zoneIndex,
            Color centerColor,
            string channelLabel)
        {
            Color radiusColor = new Color(0.05f, 0.95f, 1f, 1f);
            Color directionColor = new Color(1f, 0.92f, 0.05f, 1f);

            if (TryMoveUvHandle(
                    facial,
                    zone.CenterUv,
                    centerColor,
                    Handles.RectangleHandleCap,
                    0.075f,
                    out Vector2 movedCenter))
            {
                RecordZoneChange(controller, "Move " + channelLabel + " Zone Center");
                zone.CenterUv = movedCenter;
            }

            Vector2 xRadiusUv = zone.CenterUv + Vector2.right * zone.RadiusUv.x;
            if (TryMoveUvHandle(
                    facial,
                    xRadiusUv,
                    radiusColor,
                    Handles.DotHandleCap,
                    0.058f,
                    out Vector2 movedXRadius))
            {
                RecordZoneChange(controller, "Resize " + channelLabel + " Zone X");
                zone.RadiusUv = new Vector2(
                    Mathf.Abs(movedXRadius.x - zone.CenterUv.x),
                    zone.RadiusUv.y);
            }

            Vector2 yRadiusUv = zone.CenterUv + Vector2.up * zone.RadiusUv.y;
            if (TryMoveUvHandle(
                    facial,
                    yRadiusUv,
                    radiusColor,
                    Handles.DotHandleCap,
                    0.058f,
                    out Vector2 movedYRadius))
            {
                RecordZoneChange(controller, "Resize " + channelLabel + " Zone Y");
                zone.RadiusUv = new Vector2(
                    zone.RadiusUv.x,
                    Mathf.Abs(movedYRadius.y - zone.CenterUv.y));
            }

            Vector2 directionUv = zone.CenterUv
                + zone.DisplacementUv * zone.Strength;
            if (TryMoveUvHandle(
                    facial,
                    directionUv,
                    directionColor,
                    Handles.SphereHandleCap,
                    0.068f,
                    out Vector2 movedDirection))
            {
                RecordZoneChange(controller, "Edit " + channelLabel + " Zone Pull");
                zone.DisplacementUv = zone.Strength > 0.0001f
                    ? (movedDirection - zone.CenterUv) / zone.Strength
                    : movedDirection - zone.CenterUv;
            }
        }

        private static bool TryMoveUvHandle(
            FacialFeaturePrecut2D facial,
            Vector2 uv,
            Color color,
            Handles.CapFunction cap,
            float relativeSize,
            out Vector2 movedUv)
        {
            movedUv = uv;
            if (!facial.TryNormalizedUvToWorld(uv, out Vector3 surfaceWorld))
            {
                return false;
            }

            Vector3 displayWorld = OffsetTowardCamera(surfaceWorld);
            Vector3 displayOffset = displayWorld - surfaceWorld;
            float size = HandleUtility.GetHandleSize(displayWorld) * relativeSize;
            Handles.color = Color.black;
            Handles.DotHandleCap(
                0,
                displayWorld,
                Quaternion.identity,
                size * 1.65f,
                EventType.Repaint);
            Handles.color = color;
            EditorGUI.BeginChangeCheck();
            Transform skinTransform = facial.SkinLayer.transform;
            Vector3 moved = Handles.Slider2D(
                displayWorld,
                skinTransform.TransformDirection(Vector3.forward),
                skinTransform.TransformDirection(Vector3.right),
                skinTransform.TransformDirection(Vector3.up),
                size,
                cap,
                Vector2.zero);
            return EditorGUI.EndChangeCheck()
                && facial.TryWorldToNormalizedUv(
                    moved - displayOffset,
                    out movedUv);
        }

        private static void RecordZoneChange(
            MuscleExpressionController2D controller,
            string action)
        {
            Undo.RecordObject(controller, action);
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            QueueExpressionBake(controller);
        }

        private static void QueueExpressionBake(
            MuscleExpressionController2D controller)
        {
            if (controller == null || Application.isPlaying)
            {
                return;
            }

            pendingBakeController = controller;
            expressionBakePending = true;
            EditorApplication.update -= TryFlushPendingExpressionBake;
            EditorApplication.update += TryFlushPendingExpressionBake;
        }

        private static void FlushPendingExpressionBakeIfIdle(
            MuscleExpressionController2D controller)
        {
            if (!expressionBakePending
                || pendingBakeController != controller
                || GUIUtility.hotControl != 0)
            {
                return;
            }

            TryFlushPendingExpressionBake();
        }

        private static void TryFlushPendingExpressionBake()
        {
            if (!expressionBakePending || pendingBakeController == null)
            {
                expressionBakePending = false;
                pendingBakeController = null;
                EditorApplication.update -= TryFlushPendingExpressionBake;
                return;
            }

            if (GUIUtility.hotControl != 0
                || EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            MuscleExpressionController2D controller = pendingBakeController;
            expressionBakePending = false;
            pendingBakeController = null;
            EditorApplication.update -= TryFlushPendingExpressionBake;

            if (controller.BakeExpressionPoses())
            {
                EditorUtility.SetDirty(controller);
                Scene scene = controller.gameObject.scene;
                if (scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                }
            }

            SceneView.RepaintAll();
        }

        private static void DrawUvPolyline(
            FacialFeaturePrecut2D facial,
            IList<Vector2> points,
            Color color,
            float width)
        {
            if (Event.current.type != EventType.Repaint
                || points == null
                || points.Count < 2)
            {
                return;
            }

            List<Vector3> world = new List<Vector3>(points.Count);
            for (int index = 0; index < points.Count; index++)
            {
                if (facial.TryNormalizedUvToWorld(points[index], out Vector3 point))
                {
                    world.Add(OffsetTowardCamera(point));
                }
            }

            if (world.Count >= 2)
            {
                Handles.color = new Color(0f, 0f, 0f, 0.96f);
                Handles.DrawAAPolyLine(width + 4f, world.ToArray());
                Handles.color = color;
                Handles.DrawAAPolyLine(width, world.ToArray());
            }
        }

        private static Vector3 OffsetTowardCamera(Vector3 point)
        {
            Camera camera = SceneView.currentDrawingSceneView != null
                ? SceneView.currentDrawingSceneView.camera
                : null;
            if (camera == null)
            {
                return point;
            }

            Vector3 direction = camera.orthographic
                ? -camera.transform.forward
                : (camera.transform.position - point).normalized;
            return point + direction * 0.02f;
        }
    }
}
