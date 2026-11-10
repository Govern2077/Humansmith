using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DeformableImage.Editor
{
    public static class FourLayerAnatomyStackEditor
    {
        private const string FatSpritePath =
            "Assets/Image/分层测试/exec-690164de-f61b-400c-9ed8-b2e8aecdde5e.png";
        private const string MuscleSpritePath =
            "Assets/Image/分层测试/exec-83fdd012-017c-4426-a5a6-d5c31434eaaa.png";
        private const string SkullSpritePath = "Assets/Image/分层测试/头骨.PNG";
        private const string JawSpritePath = "Assets/Image/分层测试/下颚.PNG";

        [InitializeOnLoadMethod]
        private static void ScheduleTwoBoneMigration()
        {
            EditorApplication.delayCall += AutoMigrateActiveFaceScene;
        }

        private static void AutoMigrateActiveFaceScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (EditorApplication.isPlayingOrWillChangePlaymode
                || EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || scene.name != "Facetest"
                || scene.GetRootGameObjects().Any(item => item.name == "Anatomy Jaw Layer"))
            {
                return;
            }

            AnatomyBoneMesh skull = FindBone();
            GameObject muscleObject = scene.GetRootGameObjects()
                .FirstOrDefault(item => item.name == "Anatomy Muscle Layer");
            DeformableImageMesh muscle = muscleObject != null
                ? muscleObject.GetComponent<DeformableImageMesh>()
                : null;
            Sprite skullSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SkullSpritePath);
            Sprite jawSprite = AssetDatabase.LoadAssetAtPath<Sprite>(JawSpritePath);
            if (skull == null || muscle == null || skullSprite == null || jawSprite == null)
            {
                return;
            }

            AnatomyBoneMesh jaw = ConfigureTwoBoneLayers(skull, skullSprite, jawSprite);
            BoneSkinCoupler2D coupler = muscle.GetComponent<BoneSkinCoupler2D>();
            if (coupler == null)
            {
                coupler = Undo.AddComponent<BoneSkinCoupler2D>(muscle.gameObject);
            }

            coupler.Configure(muscle, jaw);
            coupler.ConfigureAsMandibleAttachment();
            EditorUtility.SetDirty(coupler);
            EditorSceneManager.MarkSceneDirty(scene);
            if (scene.IsValid() && scene.isLoaded && !string.IsNullOrEmpty(scene.path))
            {
                EditorSceneManager.SaveScene(scene);
            }
        }

        // Entry point used by headless editor validation and available for manual repair.
        [MenuItem("GameObject/2D Object/Migrate Facetest To Skull And Mandible", false, 24)]
        public static void MigrateFacetestToSkullAndMandible()
        {
            const string scenePath = "Assets/Scenes/Facetest.unity";
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.path != scenePath)
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            }

            AutoMigrateActiveFaceScene();
        }

        [MenuItem("GameObject/2D Object/Build Four-Layer Anatomy Stack", false, 23)]
        public static void BuildCurrentSceneStack()
        {
            DeformableImageMesh skin = FindSkin();
            AnatomyBoneMesh skull = FindBone();
            Sprite fatSprite = AssetDatabase.LoadAssetAtPath<Sprite>(FatSpritePath);
            Sprite muscleSprite = AssetDatabase.LoadAssetAtPath<Sprite>(MuscleSpritePath);
            Sprite skullSprite = AssetDatabase.LoadAssetAtPath<Sprite>(SkullSpritePath);
            Sprite jawSprite = AssetDatabase.LoadAssetAtPath<Sprite>(JawSpritePath);

            if (skin == null || skull == null || fatSprite == null || muscleSprite == null
                || skullSprite == null || jawSprite == null)
            {
                EditorUtility.DisplayDialog(
                    "Build Four-Layer Anatomy Stack",
                    "The current scene needs an active skin DeformableImageMesh and an "
                    + "AnatomyBoneMesh. The supplied tissue, skull and jaw sprites must "
                    + "remain in Assets/Image/分层测试.",
                    "OK");
                return;
            }

            AnatomyBoneMesh jaw = ConfigureTwoBoneLayers(skull, skullSprite, jawSprite);

            GameObject fatObject = FindOrCreateLayer("Anatomy Fat Layer", skin.transform);
            GameObject muscleObject = FindOrCreateLayer("Anatomy Muscle Layer", skin.transform);
            DeformableImageMesh fat = ConfigureTissueLayer(
                fatObject,
                fatSprite,
                skin,
                20,
                false);
            DeformableImageMesh muscle = ConfigureTissueLayer(
                muscleObject,
                muscleSprite,
                skin,
                10,
                true);

            MeshRenderer skinRenderer = skin.GetComponent<MeshRenderer>();
            if (skinRenderer != null)
            {
                Undo.RecordObject(skinRenderer, "Set Skin Layer Order");
                skinRenderer.sortingOrder = 30;
            }

            DeformableImageInteractor skinInteractor = skin.GetComponent<DeformableImageInteractor>();
            if (skinInteractor != null)
            {
                Undo.RecordObject(skinInteractor, "Route Direct Input To Muscle");
                skinInteractor.enabled = false;
            }

            BoneSkinCoupler2D oldDirectCoupler = skin.GetComponent<BoneSkinCoupler2D>();
            if (oldDirectCoupler != null)
            {
                Undo.DestroyObjectImmediate(oldDirectCoupler);
            }

            BoneSkinCoupler2D boneToMuscle = muscle.GetComponent<BoneSkinCoupler2D>();
            if (boneToMuscle == null)
            {
                boneToMuscle = Undo.AddComponent<BoneSkinCoupler2D>(muscle.gameObject);
            }

            boneToMuscle.Configure(muscle, jaw);
            boneToMuscle.ConfigureAsMandibleAttachment();

            SoftTissueLayerCoupler2D muscleToFat =
                fat.GetComponent<SoftTissueLayerCoupler2D>();
            if (muscleToFat == null)
            {
                muscleToFat = Undo.AddComponent<SoftTissueLayerCoupler2D>(fat.gameObject);
            }

            muscleToFat.Configure(muscle, fat, 1f);

            SoftTissueLayerCoupler2D fatToSkin =
                skin.GetComponent<SoftTissueLayerCoupler2D>();
            if (fatToSkin == null)
            {
                fatToSkin = Undo.AddComponent<SoftTissueLayerCoupler2D>(skin.gameObject);
            }

            fatToSkin.Configure(fat, skin, 1f);

            FacialFeaturePrecut2D precut = skin.GetComponent<FacialFeaturePrecut2D>();
            if (precut == null)
            {
                precut = Undo.AddComponent<FacialFeaturePrecut2D>(skin.gameObject);
            }

            precut.Configure(skin, fat, muscle);

            DeformableImageInteractor muscleInteractor =
                muscle.GetComponent<DeformableImageInteractor>();
            if (muscleInteractor != null)
            {
                muscleInteractor.ConfigureLayeredCutTargets(skin, fat, muscle);
                EditorUtility.SetDirty(muscleInteractor);
            }

            EditorUtility.SetDirty(boneToMuscle);
            EditorUtility.SetDirty(muscleToFat);
            EditorUtility.SetDirty(fatToSkin);
            EditorUtility.SetDirty(precut);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Selection.activeGameObject = muscle.gameObject;
            Debug.Log(
                "Anatomy stack built: Skull + Mandible -> Muscle -> Fat -> Skin. "
                + "Deformation input is routed to Muscle; Cut targets the first exposed "
                + "tissue in Skin -> Fat -> Muscle order. Teeth and gingiva do not bind muscle.",
                muscle);
        }

        [MenuItem("GameObject/2D Object/Build Four-Layer Anatomy Stack", true)]
        private static bool ValidateBuildCurrentSceneStack()
        {
            return !Application.isPlaying;
        }

        private static DeformableImageMesh FindSkin()
        {
            DeformableImageMesh selected = Selection.gameObjects
                .Select(item => item.GetComponent<DeformableImageMesh>())
                .FirstOrDefault(item => item != null);
            if (selected != null)
            {
                return selected;
            }

            DeformableImageMesh[] candidates = Object.FindObjectsOfType<DeformableImageMesh>(true);
            return candidates.FirstOrDefault(item => item.gameObject.activeInHierarchy
                    && item.name != "Anatomy Fat Layer"
                    && item.name != "Anatomy Muscle Layer")
                ?? candidates.FirstOrDefault();
        }

        private static AnatomyBoneMesh FindBone()
        {
            AnatomyBoneMesh selected = Selection.gameObjects
                .Select(item => item.GetComponent<AnatomyBoneMesh>())
                .FirstOrDefault(item => item != null
                    && item.name != "Anatomy Jaw Layer");
            return selected != null
                ? selected
                : Object.FindObjectsOfType<AnatomyBoneMesh>(true)
                    .FirstOrDefault(item => item.name == "Anatomy Skull Layer"
                        || item.name == "Anatomy Bone Layer")
                    ?? Object.FindObjectsOfType<AnatomyBoneMesh>(true)
                        .FirstOrDefault(item => item.name != "Anatomy Jaw Layer");
        }

        private static AnatomyBoneMesh ConfigureTwoBoneLayers(
            AnatomyBoneMesh skull,
            Sprite skullSprite,
            Sprite jawSprite)
        {
            Undo.RecordObject(skull.gameObject, "Convert Bone To Skull Layer");
            skull.gameObject.name = "Anatomy Skull Layer";
            Undo.RecordObject(skull, "Assign Skull Sprite");
            skull.Configure(skullSprite, skull.Height);

            MeshRenderer skullRenderer = skull.GetComponent<MeshRenderer>();
            if (skullRenderer != null)
            {
                Undo.RecordObject(skullRenderer, "Set Skull Layer Order");
                skullRenderer.sortingOrder = 0;
            }

            GameObject jawObject = SceneManager.GetActiveScene()
                .GetRootGameObjects()
                .FirstOrDefault(item => item.name == "Anatomy Jaw Layer");
            if (jawObject == null)
            {
                jawObject = new GameObject("Anatomy Jaw Layer");
                Undo.RegisterCreatedObjectUndo(jawObject, "Create Anatomy Jaw Layer");
            }

            CopyTransform(jawObject.transform, skull.transform);
            AnatomyBoneMesh jaw = jawObject.GetComponent<AnatomyBoneMesh>();
            if (jaw == null)
            {
                jaw = Undo.AddComponent<AnatomyBoneMesh>(jawObject);
            }

            Undo.RecordObject(jaw, "Assign Mandible Sprite");
            jaw.Configure(jawSprite, skull.Height);
            if (jawObject.GetComponent<BoneCutInteractor>() == null)
            {
                Undo.AddComponent<BoneCutInteractor>(jawObject);
            }

            AnatomyBoneFragmentMover mover =
                jawObject.GetComponent<AnatomyBoneFragmentMover>();
            if (mover == null)
            {
                mover = Undo.AddComponent<AnatomyBoneFragmentMover>(jawObject);
            }

            Undo.RecordObject(mover, "Configure Mandible Drag");
            mover.ConfigureAsMandibleMover(true);
            MeshRenderer jawRenderer = jaw.GetComponent<MeshRenderer>();
            if (jawRenderer != null)
            {
                Undo.RecordObject(jawRenderer, "Set Mandible Layer Order");
                jawRenderer.sortingOrder = 1;
            }

            EditorUtility.SetDirty(skull);
            EditorUtility.SetDirty(jaw);
            EditorUtility.SetDirty(mover);
            return jaw;
        }

        private static GameObject FindOrCreateLayer(string layerName, Transform skinTransform)
        {
            GameObject existing = SceneManager.GetActiveScene()
                .GetRootGameObjects()
                .FirstOrDefault(item => item.name == layerName);
            if (existing != null)
            {
                Undo.RecordObject(existing, "Update " + layerName);
                existing.SetActive(true);
                CopyTransform(existing.transform, skinTransform);
                return existing;
            }

            GameObject layer = new GameObject(layerName);
            Undo.RegisterCreatedObjectUndo(layer, "Create " + layerName);
            CopyTransform(layer.transform, skinTransform);
            return layer;
        }

        private static DeformableImageMesh ConfigureTissueLayer(
            GameObject layer,
            Sprite sprite,
            DeformableImageMesh skinTemplate,
            int sortingOrder,
            bool acceptsDirectInput)
        {
            MeshRenderer renderer = layer.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = Undo.AddComponent<MeshRenderer>(layer);
            }

            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.sortingOrder = sortingOrder;

            if (layer.GetComponent<MeshFilter>() == null)
            {
                Undo.AddComponent<MeshFilter>(layer);
            }

            DeformableImageMesh mesh = layer.GetComponent<DeformableImageMesh>();
            if (mesh == null)
            {
                mesh = Undo.AddComponent<DeformableImageMesh>(layer);
            }

            CopyGridSettings(skinTemplate, mesh);
            mesh.ConfigureSprites(sprite, sprite);

            DeformableImageInteractor interactor = layer.GetComponent<DeformableImageInteractor>();
            if (interactor == null)
            {
                interactor = Undo.AddComponent<DeformableImageInteractor>(layer);
            }

            interactor.enabled = acceptsDirectInput;

            DepthMappedSkin2D depth = layer.GetComponent<DepthMappedSkin2D>();
            if (depth != null)
            {
                Undo.DestroyObjectImmediate(depth);
            }

            if (!acceptsDirectInput)
            {
                BoneSkinCoupler2D boneCoupler = layer.GetComponent<BoneSkinCoupler2D>();
                if (boneCoupler != null)
                {
                    Undo.DestroyObjectImmediate(boneCoupler);
                }
            }

            return mesh;
        }

        private static void CopyGridSettings(
            DeformableImageMesh source,
            DeformableImageMesh destination)
        {
            SerializedObject sourceObject = new SerializedObject(source);
            SerializedObject destinationObject = new SerializedObject(destination);
            string[] properties =
            {
                "subdivisionsX",
                "subdivisionsY",
                "height",
                "respectSpritePivot",
                "recalculateNormalsWhenDeformed"
            };

            foreach (string propertyName in properties)
            {
                SerializedProperty sourceProperty = sourceObject.FindProperty(propertyName);
                SerializedProperty destinationProperty = destinationObject.FindProperty(propertyName);
                if (sourceProperty != null && destinationProperty != null)
                {
                    destinationProperty.serializedObject.CopyFromSerializedProperty(sourceProperty);
                }
            }

            destinationObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CopyTransform(Transform destination, Transform source)
        {
            Undo.RecordObject(destination, "Align Anatomy Layer");
            destination.SetParent(source.parent, false);
            destination.localPosition = source.localPosition;
            destination.localRotation = source.localRotation;
            destination.localScale = source.localScale;
        }
    }
}
