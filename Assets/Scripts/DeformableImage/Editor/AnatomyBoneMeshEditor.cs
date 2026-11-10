using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeformableImage.Editor
{
    [CustomEditor(typeof(AnatomyBoneMesh))]
    public sealed class AnatomyBoneMeshEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            AnatomyBoneMesh boneMesh = (AnatomyBoneMesh)target;
            EditorGUILayout.Space();
            if (boneMesh.gameObject.name.IndexOf("Jaw Layer") >= 0)
            {
                EditorGUILayout.HelpBox(
                    "Pose Mesh Detail must remain above 0 for regional tooth weights; level 3 supplies "
                    + "interior vertices across the dental texture while leaving the authored outline intact. "
                    + "Mandible curve tuning: Pose Center X / Pose Half Width define the actual visible "
                    + "mandible span inside the transparent sprite. Chin Plateau is a fraction of that "
                    + "span and now directly controls the rigid rounded chin. Jaw Angle Lock controls "
                    + "where the quintic-smoothed side transition ends; Arc Roundness changes the bend "
                    + "profile; Angle Follow is the minimum downward travel at both angles. The complete "
                    + "body remains on one monotonic curve from Angle Follow to full chin travel. "
                    + "Mouth Corner Follow controls the jaw response around both mouth corners and "
                    + "the nearby upper-lip corner tissue. "
                    + "Angle Pin Center/Radius define the fixed anatomical zones and are mirrored automatically. "
                    + "Angle Inward adds horizontal convergence at the angles; Central Narrowing also narrows "
                    + "the chin and foremost teeth. Narrowing Acceleration 2 makes both effects grow quadratically "
                    + "with opening while their spatial masks remain smoothly joined. "
                    + "Dental Center X / Dental Half Width define the narrower dental arch independently "
                    + "from the complete mandible. Dental Band Half Height limits the tooth-only profile; "
                    + "Dental Curve Power 2 creates a quadratic incisor-to-molar arc, while Rear Dental Follow "
                    + "sets the remaining molar travel. Dental Outer Feather releases this rule before the "
                    + "mandibular angles. Dental Pivot Y and Perspective Compression control "
                    + "the opening foreshortening between the lower teeth and the mandibular body.",
                    MessageType.None);

            }

            EditorGUILayout.HelpBox(
                "Play mode: press 5 to toggle real closed bone cutting. "
                + "Draw a yellow closed lasso with left mouse. The retained bone is rebuilt "
                + "with new vertices/triangles and the removed fragment can be moved with left mouse.",
                MessageType.Info);
            EditorGUILayout.LabelField("Runtime Vertices", boneMesh.CurrentVertexCount.ToString());
            EditorGUILayout.LabelField("Runtime Triangles", boneMesh.CurrentTriangleCount.ToString());
            EditorGUILayout.HelpBox(
                "Skull and mandible may have different Runtime Vertices/Triangles. These "
                + "counts follow each sprite's independent alpha outline and do not control "
                + "their scale or alignment; both are mapped in the shared full-canvas UV.",
                MessageType.None);

            if (GUILayout.Button("Rebuild Bone From Sprite"))
            {
                boneMesh.RebuildFromSprite();
                EditorUtility.SetDirty(boneMesh);
            }
        }

        [MenuItem("GameObject/2D Object/Anatomy Bone Layer", false, 21)]
        private static void CreateBoneLayer(MenuCommand command)
        {
            GameObject gameObject = new GameObject("Anatomy Bone Layer");
            GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Anatomy Bone Layer");

            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = -10;
            gameObject.AddComponent<MeshFilter>();
            AnatomyBoneMesh boneMesh = gameObject.AddComponent<AnatomyBoneMesh>();
            gameObject.AddComponent<BoneCutInteractor>();

            Sprite selectedSprite = GetSelectedSprite();
            if (selectedSprite != null)
            {
                boneMesh.Configure(selectedSprite);
            }

            Selection.activeGameObject = gameObject;
        }

        private static Sprite GetSelectedSprite()
        {
            foreach (Object selected in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(selected);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (Object asset in assets)
                {
                    if (asset is Sprite sprite)
                    {
                        return sprite;
                    }
                }
            }

            return null;
        }
    }
}
