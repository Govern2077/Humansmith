#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeformableImage.Editor
{
    public static class SurgicalInstrumentsValidation
    {
        private const string Report = "Temp/SurgicalInstrumentsValidation.txt";
        private const string Trigger = "Temp/RunSurgicalInstrumentsValidation";
        private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;

        [InitializeOnLoadMethod]
        private static void Schedule()
        {
            if (File.Exists(Trigger)) EditorApplication.delayCall += Run;
        }

        [MenuItem("Tools/Face/Validate Surgical Instruments")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (File.Exists(Trigger)) File.Delete(Trigger);
            GameObject fixture = null, deeper = null;
            Texture2D texture = null;
            Sprite sprite = null;
            try
            {
                texture = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                var pixels = new Color32[128 * 128];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
                texture.SetPixels32(pixels); texture.Apply();
                sprite = Sprite.Create(texture, new Rect(0, 0, 128, 128), Vector2.one * 0.5f);
                fixture = new GameObject("Instrument validation skin") { hideFlags = HideFlags.HideAndDontSave };
                deeper = new GameObject("Instrument validation deeper layer") { hideFlags = HideFlags.HideAndDontSave };
                var mesh = fixture.AddComponent<DeformableImageMesh>(); mesh.ConfigureSprites(sprite, sprite);
                var source = deeper.AddComponent<DeformableImageMesh>(); source.ConfigureSprites(sprite, sprite);
                var link = fixture.AddComponent<SoftTissueLayerCoupler2D>(); link.Configure(source, mesh); link.BindRestPose();
                var owner = fixture.AddComponent<DeformableImageInteractor>(); owner.BrushRadius = 0.65f;
                var instruments = fixture.GetComponent<SurgicalInstrumentPrototype2D>();
                if (instruments == null) instruments = fixture.AddComponent<SurgicalInstrumentPrototype2D>();
                instruments.Configure(owner, mesh, null);
                Require(!instruments.BeginPull(Vector3.zero, true), "Retractor needs an incision");
                Require(!instruments.PlaceSuturePoint(Vector3.zero), "Suture needs an incision");
                Require(!instruments.DissectAt(Vector3.zero, 1f), "Dissector starts at an incision");
                Require(mesh.ApplyOpenCut(new List<Vector2> { new Vector2(0.35f, 0.5f), new Vector2(0.65f, 0.5f) }, 0.18f), "Fixture incision");
                // Runtime topology events rebind this automatically; this fixture runs in Edit Mode.
                link.BindRestPose();
                Vector3[] baseline = mesh.CopyCurrentVertices();
                Vector3 upper = new Vector3(0, 0.22f, 0), lower = new Vector3(0, -0.22f, 0);

                Require(instruments.BeginPull(upper, false), "Hook grab");
                instruments.MovePull(upper + new Vector3(0.2f, 0.35f, 0)); instruments.SolveConstraints();
                CheckMoved(mesh, baseline, "Hook deformation"); CheckAnchors(mesh, baseline);
                instruments.CancelPendingInput(); instruments.SolveConstraints();
                CheckRestored(mesh, baseline, "Hook cancellation restores shape");

                Require(instruments.BeginPull(upper, true), "Retractor grab");
                instruments.MovePull(upper + Vector3.up * 0.45f); instruments.EndPull(); instruments.SolveConstraints();
                Require(instruments.RetractorCount == 1, "Retractor remains after release");
                CheckMoved(mesh, baseline, "Retained retractor deformation"); CheckAnchors(mesh, baseline);
                Vector3[] held = mesh.CopyCurrentVertices();
                for (int i = 0; i < 60; i++) instruments.SolveConstraints();
                Require(MaxDelta(held, mesh.CopyCurrentVertices()) < 0.0001f, "Held tools do not accumulate deformation");
                instruments.RemoveLastRetractor(); instruments.SolveConstraints();
                CheckRestored(mesh, baseline, "Removing retractor restores shape");

                Require(instruments.PlaceSuturePoint(upper), "First suture point");
                Require(!instruments.PlaceSuturePoint(upper), "Duplicate endpoint rejected");
                Require(instruments.PlaceSuturePoint(lower), "Second suture point");
                Require(instruments.SutureCount == 1, "Stitch created");
                float openLength = SutureLength(instruments, mesh);
                instruments.SolveConstraints();
                Require(SutureLength(instruments, mesh) < openLength * 0.9f, "Thread draws incision sides together");
                CheckAnchors(mesh, baseline);
                instruments.SetLastSutureTightness(0); instruments.SolveConstraints();
                CheckRestored(mesh, baseline, "Loosening restores shape");
                instruments.RemoveLastSuture(); instruments.SolveConstraints();

                Require(instruments.DissectAt(upper, 10f), "Dissection from incision");
                instruments.SolveConstraints(); CheckMoved(mesh, baseline, "Visible separation lift"); CheckAnchors(mesh, baseline);
                var released = (float[])typeof(SoftTissueLayerCoupler2D).GetField("releasedInfluence", Fields).GetValue(link);
                float maximumRelease = 0;
                foreach (float value in released) maximumRelease = Mathf.Max(maximumRelease, value);
                Require(maximumRelease > 0.5f, "Dissection actually weakens the layer link");
                Require(released[0] == 0f, "Distant tissue retains its layer link");
                instruments.ResetInstruments(); instruments.SolveConstraints();
                CheckRestored(mesh, baseline, "Full instrument reset restores shape");
                Require(instruments.BeginPull(upper, true), "Rebind fixture grip");
                mesh.RebuildMesh();
                Require(instruments.RetractorCount == 0 && instruments.SutureCount == 0, "Topology rebuild safely removes stale constraints");
                File.WriteAllText(Report, "PASS: incision access guards; hook displacement/cancellation; retained retractor; 60-frame no-drift; original-outline anchors; suture creation/duplicate rejection/closure/loosening/removal; separation lift and local layer-link release; full reset; topology invalidation.");
                Debug.Log("Surgical instruments validation passed.");
            }
            catch (Exception error) { File.WriteAllText(Report, "FAIL: " + error); Debug.LogException(error); }
            finally
            {
                if (fixture != null) UnityEngine.Object.DestroyImmediate(fixture);
                if (deeper != null) UnityEngine.Object.DestroyImmediate(deeper);
                if (sprite != null) UnityEngine.Object.DestroyImmediate(sprite);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static float SutureLength(SurgicalInstrumentPrototype2D instruments, DeformableImageMesh mesh)
        {
            var list = (IList)typeof(SurgicalInstrumentPrototype2D).GetField("stitches", Fields).GetValue(instruments);
            object stitch = list[0], a = stitch.GetType().GetField("A").GetValue(stitch), b = stitch.GetType().GetField("B").GetValue(stitch);
            var positions = mesh.CopyCurrentVertices();
            return Vector3.Distance(positions[(int)a.GetType().GetField("Vertex").GetValue(a)], positions[(int)b.GetType().GetField("Vertex").GetValue(b)]);
        }
        private static void CheckAnchors(DeformableImageMesh mesh, Vector3[] baseline)
        {
            var anchors = mesh.CopyOriginalOutlineAnchors(); var current = mesh.CopyCurrentVertices();
            for (int i = 0; i < current.Length; i++) if (anchors[i]) Require((baseline[i] - current[i]).sqrMagnitude < 1e-12f, "Original outline stays fixed");
        }
        private static void CheckMoved(DeformableImageMesh mesh, Vector3[] baseline, string label) { Require(MaxDelta(baseline, mesh.CopyCurrentVertices()) > 0.01f, label); }
        private static void CheckRestored(DeformableImageMesh mesh, Vector3[] baseline, string label) { Require(MaxDelta(baseline, mesh.CopyCurrentVertices()) < 0.0001f, label); }
        private static float MaxDelta(Vector3[] a, Vector3[] b) { float max = 0; for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, Vector3.Distance(a[i], b[i])); return max; }
        private static void Require(bool condition, string label) { if (!condition) throw new Exception(label); }
    }
}
#endif
