using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeformableImage.Editor
{
    [CustomEditor(typeof(DeformableImageMesh))]
    public sealed class DeformableImageMeshEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            DeformableImageMesh component = (DeformableImageMesh)target;
            EditorGUILayout.Space();

            if (component.FrontSprite == null)
            {
                EditorGUILayout.HelpBox("Assign at least a Front Sprite, then rebuild the mesh.", MessageType.Warning);
            }
            else if (!component.FrontSprite.texture.isReadable)
            {
                EditorGUILayout.HelpBox(
                    "Front Sprite Read/Write is disabled. Edge grabbing will use a less accurate geometry fallback.",
                    MessageType.Warning);
                if (GUILayout.Button("Enable Front Sprite Read/Write"))
                {
                    EnableReadWrite(component.FrontSprite);
                }
            }

            EditorGUILayout.HelpBox(
                "Play mode controls:\n" +
                "1 = Drag, 2 = Inflate, 3 = Pinch, 4 = Cut\n" +
                "Mouse wheel = Brush radius; in Cut mode = transparent cut width\n" +
                "Middle mouse while dragging = Pin; release without pin = Rebound\n" +
                "Left mouse on a closed cut piece = Pin and lift one local cloth vertex\n" +
                "Release the pinned point near its cut position = Auto unfold and return\n" +
                "R = Reset deformation, Esc = Cancel current operation\n" +
                "Interior grabs stay front-facing; visible-edge grabs may fold and reveal the back.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Current Physical Topology", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Logical Vertices", component.LogicalVertexCount.ToString());
            EditorGUILayout.LabelField("Logical Triangles", component.LogicalTriangleCount.ToString());

            string debugButtonText = component.MeshDebugVisible
                ? "Hide Mesh Debug"
                : "Show Mesh Debug";
            if (GUILayout.Button(debugButtonText))
            {
                Undo.RecordObject(component, debugButtonText);
                component.MeshDebugVisible = !component.MeshDebugVisible;
                EditorUtility.SetDirty(component);
                SceneView.RepaintAll();
            }

            if (component.MeshDebugVisible)
            {
                EditorGUILayout.HelpBox(
                    "Mesh debug colors:\n" +
                    "Cyan = triangle edges\n" +
                    "Yellow = original outer boundary\n" +
                    "Red = physical cut boundary\n" +
                    "White = vertices (optional)\n" +
                    "Enable the Gizmos button in Scene/Game view to see them.",
                    MessageType.Info);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild Mesh"))
                {
                    component.RebuildMesh();
                    EditorUtility.SetDirty(component);
                }

                if (GUILayout.Button("Reset Deformation"))
                {
                    component.ResetDeformation();
                }
            }
        }

        [MenuItem("GameObject/2D Object/Deformable Double-Sided Image", false, 20)]
        private static void CreateDeformableImage(MenuCommand command)
        {
            GameObject gameObject = new GameObject("Deformable Double-Sided Image");
            GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Deformable Image");

            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            gameObject.AddComponent<MeshFilter>();

            DeformableImageMesh mesh = gameObject.AddComponent<DeformableImageMesh>();
            gameObject.AddComponent<DeformableImageInteractor>();

            List<Sprite> selectedSprites = GetSelectedSprites();
            if (selectedSprites.Count > 0)
            {
                Sprite front = selectedSprites[0];
                Sprite back = selectedSprites.Count > 1 ? selectedSprites[1] : selectedSprites[0];
                mesh.ConfigureSprites(front, back);
            }

            Selection.activeGameObject = gameObject;
        }

        private static List<Sprite> GetSelectedSprites()
        {
            List<Sprite> sprites = new List<Sprite>();
            HashSet<string> visitedPaths = new HashSet<string>();

            foreach (Object selected in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(selected);
                if (string.IsNullOrEmpty(path) || !visitedPaths.Add(path))
                {
                    continue;
                }

                Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (Object asset in assets)
                {
                    if (asset is Sprite sprite)
                    {
                        sprites.Add(sprite);
                        break;
                    }
                }
            }

            return sprites;
        }

        private static void EnableReadWrite(Sprite sprite)
        {
            string path = AssetDatabase.GetAssetPath(sprite.texture);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null || importer.isReadable)
            {
                return;
            }

            importer.isReadable = true;
            importer.SaveAndReimport();
        }
    }
}
