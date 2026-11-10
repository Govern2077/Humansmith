#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeformableImage.Editor
{
    public static class WrinklePrototypeValidation
    {
        private const string Trigger = "Temp/RunWrinkleValidation";
        private const string Report = "Temp/WrinkleValidation.txt";
        [InitializeOnLoadMethod]
        private static void Schedule()
        {
            if (!File.Exists(Trigger)) return;
            EditorApplication.delayCall += () =>
            {
                File.Delete(Trigger);
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    File.WriteAllText(Report, "SKIPPED: Play Mode active. Use Tools/Face/Validate Wrinkle Prototype in Edit Mode.");
                    return;
                }
                Run();
            };
        }

        [MenuItem("Tools/Face/Validate Wrinkle Prototype")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("Run wrinkle validation in Edit Mode."); return; }
            GameObject fixture = null;
            Sprite sprite = null;
            try
            {
                Shader shader = Shader.Find("Deformable Image/Unlit Transparent");
                Require(shader != null && !ShaderUtil.ShaderHasError(shader), "Shader import");
                var mask = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Image/Wrinkles/GlabellaWrinkleMask.png");
                Require(mask != null, "Generated texture import");
                var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(mask));
                Require(!importer.sRGBTexture && importer.wrapMode == TextureWrapMode.Clamp, "Linear/clamp mask settings");
                fixture = new GameObject("Wrinkle validation fixture") { hideFlags = HideFlags.HideAndDontSave };
                var mesh = fixture.AddComponent<DeformableImageMesh>();
                sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height), Vector2.one * 0.5f);
                mesh.ConfigureSprites(sprite, sprite);
                var overlay = fixture.AddComponent<SkinWrinkleOverlay2D>();
                Invoke(overlay, "Awake");
                var fullFace = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Image/Wrinkles/FullFaceWrinkles.png");
                Require(fullFace != null, "Full-face texture import");
                var fullFaceImporter = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(fullFace));
                Require(fullFaceImporter.sRGBTexture && fullFaceImporter.wrapMode == TextureWrapMode.Clamp, "Full-face color/clamp settings");
                Set(overlay, "region", new Vector4(0, 0, 1, 1));
                Set(overlay, "wrinkleMask", fullFace);
                Set(overlay, "useSourceColor", true);
                Vector3[] baseline = mesh.CopyRestVertices();
                Require(baseline.Length > 0, "Fixture mesh");
                CheckPose(mesh, overlay, baseline, 0f, false, "Neutral");
                var compressed = (Vector3[])baseline.Clone();
                for (int i = 0; i < compressed.Length; i++) compressed[i].x *= 0.9f;
                CheckPose(mesh, overlay, compressed, 0.3f, true, "Compression activates");
                CheckPose(mesh, overlay, compressed, 0.3f, true, "Stable held compression");
                var stretched = (Vector3[])baseline.Clone();
                for (int i = 0; i < stretched.Length; i++) stretched[i].x *= 1.1f;
                CheckPose(mesh, overlay, stretched, 0f, false, "Stretch releases");
                CheckPose(mesh, overlay, compressed, 0.3f, true, "Compression before release");
                CheckPose(mesh, overlay, stretched, 0f, false, "Stretch clears prior compression", false);
                var shear = (Vector3[])baseline.Clone();
                var mouthStretch = (Vector3[])baseline.Clone();
                var verticalCompression = (Vector3[])baseline.Clone();
                for (int i = 0; i < baseline.Length; i++)
                {
                    shear[i].x += 0.3f * shear[i].y;
                    mouthStretch[i].x *= 1.2f;
                    mouthStretch[i].y *= 0.9f;
                    verticalCompression[i].y *= 0.9f;
                }
                CheckPose(mesh, overlay, shear, 0f, false, "Shear does not activate");
                CheckPose(mesh, overlay, mouthStretch, 0f, false, "Mouth stretch with transverse shortening");
                CheckPose(mesh, overlay, verticalCompression, 0.3f, true, "Vertical compression activates");
                var rigid = (Vector3[])baseline.Clone();
                for (int i = 0; i < rigid.Length; i++) rigid[i] = Quaternion.Euler(15, 20, 35) * rigid[i] + new Vector3(2, -3, 1);
                CheckPose(mesh, overlay, rigid, 0f, false, "Rigid transform");
                var block = new MaterialPropertyBlock();
                fixture.GetComponent<MeshRenderer>().GetPropertyBlock(block, 0);
                Require(block.GetTexture("_MainTex") != null && block.GetTexture("_CutMask") != null, "Existing material properties preserved");
                File.WriteAllText(Report, "PASS: shader, mask import, neutral, horizontal/vertical compression, held compression, stretch release, immediate release, shear rejection, mouth stretch rejection, rigid transform, material property preservation.");
                Debug.Log("Wrinkle prototype validation passed.");
            }
            catch (Exception error)
            {
                File.WriteAllText(Report, "FAIL: " + error);
                Debug.LogException(error);
            }
            finally
            {
                if (fixture != null) UnityEngine.Object.DestroyImmediate(fixture);
                if (sprite != null) UnityEngine.Object.DestroyImmediate(sprite);
            }
        }
        private static void CheckPose(DeformableImageMesh mesh, SkinWrinkleOverlay2D overlay, Vector3[] pose, float minimum, bool active, string label, bool rebind = true)
        {
            mesh.SetLogicalVertices(pose);
            if (rebind) Invoke(overlay, "Bind");
            Set(overlay, "lastUpdate", Time.unscaledTime - 2f);
            Set(overlay, "nextUpdate", -1f);
            Invoke(overlay, "LateUpdate");
            float activation = (float)typeof(SkinWrinkleOverlay2D).GetField("maximumActivation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
            Require(active ? activation >= minimum : activation < 0.001f, label + " activation=" + activation);
        }
        private static void Invoke(object target, string method) { target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null); }
        private static void Set(object target, string field, object value) { target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        private static void Require(bool condition, string label) { if (!condition) throw new Exception(label); }
    }
}
#endif
