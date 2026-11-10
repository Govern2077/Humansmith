using UnityEditor;
using UnityEngine;

namespace DeformableImage.Editor
{
    [CustomEditor(typeof(BoneSkinCoupler2D))]
    public sealed class BoneSkinCoupler2DEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            BoneSkinCoupler2D coupler = (BoneSkinCoupler2D)target;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Mandible attachment uses the retained bony edge, a vertical UV band and "
                + "distance falloff. The Oral Exclusion ellipse gives teeth and gingiva zero "
                + "contour weight. Lip Follow restores a separate soft-tissue link for the "
                + "lips, while Chin Cut Influence makes menton cuts reshape the chin like "
                + "jaw-angle cuts. In Play Mode, right-drag Anatomy Jaw Layer; enable Show "
                + "Weight Gizmos to inspect attached vertices.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "Upper-Lip Corner Follow: Pearl Protection keeps the central lip bead fixed; "
                + "Follow Height limits the affected band above the mouth incision; Corner Follow "
                + "sets the maximum downward response at both corners. The horizontal transition "
                + "uses the inverse of Lower Lip Horizontal Falloff, so one editable curve keeps "
                + "the upper- and lower-lip responses anatomically complementary.",
                MessageType.None);
            EditorGUILayout.HelpBox(
                "Mouth-Corner Jaw Follow builds two smooth elliptical zones from the authored "
                + "mouth-cut endpoints. Radius controls the surrounding affected tissue, Upper-Lip "
                + "Reach adds a smooth upward lobe beside each corner, and Mandible Mouth Corner Follow "
                + "on Anatomy Jaw Layer sets their shared downward response.",
                MessageType.None);
            EditorGUILayout.HelpBox(
                "Cheekbone Expansion defines a mirrored pair of muscle zones. Center/Radius "
                + "control the affected area and Follow controls their subtle outward travel "
                + "as the mandible opens.",
                MessageType.None);

            if (GUILayout.Button("Bind Automatic Jaw Weights"))
            {
                coupler.BindRestPose();
            }

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Apply Current Bone Shape"))
                {
                    coupler.ApplyCurrentBoneShape();
                }

                if (GUILayout.Button("Reset Manual Face Offset"))
                {
                    coupler.ResetFaceToBoneShape();
                }
            }

            EditorGUILayout.HelpBox(coupler.LastStatus, MessageType.None);
        }

        [MenuItem("GameObject/2D Object/Connect Selected Face To Bone", false, 22)]
        private static void ConnectSelectedLayers()
        {
            DeformableImageMesh face = null;
            AnatomyBoneMesh bone = null;
            foreach (GameObject selected in Selection.gameObjects)
            {
                if (face == null)
                {
                    face = selected.GetComponent<DeformableImageMesh>();
                }

                if (bone == null)
                {
                    bone = selected.GetComponent<AnatomyBoneMesh>();
                }
            }

            if (face == null || bone == null)
            {
                EditorUtility.DisplayDialog(
                    "Connect Face To Bone",
                    "Select both the Deformable Image face object and the Anatomy Bone Layer object.",
                    "OK");
                return;
            }

            BoneSkinCoupler2D coupler = face.GetComponent<BoneSkinCoupler2D>();
            if (coupler == null)
            {
                coupler = Undo.AddComponent<BoneSkinCoupler2D>(face.gameObject);
            }

            coupler.Configure(face, bone);
            coupler.ConfigureAsMandibleAttachment();
            EditorUtility.SetDirty(coupler);
            Selection.activeGameObject = face.gameObject;
        }
    }
}
