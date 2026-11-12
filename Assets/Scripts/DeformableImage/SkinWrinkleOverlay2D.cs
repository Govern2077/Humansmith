using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeformableImage
{
    /// <summary>Color-only UV wrinkle mask driven by directional skin strain; no expression channels or normal maps.</summary>
    [DefaultExecutionOrder(900)]
    [RequireComponent(typeof(DeformableImageMesh))]
    [DisallowMultipleComponent]
    public sealed class SkinWrinkleOverlay2D : MonoBehaviour
    {
        private const int Resolution = 128;
        [SerializeField] private Texture2D wrinkleMask;
        [SerializeField] private bool useSourceColor;
        [SerializeField] private bool effectEnabled = true;
        [SerializeField, Range(0f, 1f)] private float opacity = 0.8f;
        [SerializeField] private Color wrinkleTint = new Color(0.19f, 0.09f, 0.065f, 1f);
        [Tooltip("Normalized skin UV: x, y, width, height. The texture follows the material, not screen coordinates.")]
        [SerializeField] private Vector4 region = new Vector4(0.42f, 0.565f, 0.16f, 0.19f);
        [SerializeField, Range(0f, 0.1f)] private float onsetCompression = 0.008f;
        [SerializeField, Range(0.02f, 0.4f)] private float fullCompression = 0.12f;
        [SerializeField, Range(1f, 30f)] private float response = 12f;
        [SerializeField] private bool previewTexture;
        private DeformableImageMesh skin;
        private MeshRenderer skinRenderer;
        private Texture2D strainTexture;
        private MaterialPropertyBlock properties;
        private Vector3[] rest, current;
        private float[] strain, weights, smoothed, triangleStrain;
        private Color32[] pixels;
        private Triangle[] triangles;
        private Sample[] samples;
        private bool bindingDirty = true;
        private float nextUpdate, lastUpdate, maximumActivation;
        private Vector2 scroll;
        private string status = "Waiting for skin mesh.";

        private struct Triangle { public int A, B, C; public float AA, AB, BB, Weight; }
        private struct Sample { public int A, B, C, TriangleIndex; public Vector3 Weights; public bool Valid; }

        private void Awake()
        {
            skin = GetComponent<DeformableImageMesh>();
            skinRenderer = GetComponent<MeshRenderer>();
            properties = new MaterialPropertyBlock();
        }

        private void OnEnable()
        {
            if (skin == null) skin = GetComponent<DeformableImageMesh>();
            skin.MeshRebuilt += InvalidateBinding;
            skin.RestPoseChanged += InvalidateBinding;
            bindingDirty = true;
        }

        private void InvalidateBinding() { bindingDirty = true; }

        private void OnValidate()
        {
            region.z = Mathf.Max(0.01f, region.z);
            region.w = Mathf.Max(0.01f, region.w);
            fullCompression = Mathf.Max(onsetCompression + 0.001f, fullCompression);
            bindingDirty = true;
        }

        private void Bind()
        {
            rest = skin.CopyRestVertices();
            Vector2[] uvs = skin.CopyNormalizedRestUvs();
            int[] indices = skin.CopyMechanicallyIntactTriangles();
            if (rest.Length == 0 || rest.Length != uvs.Length) return;
            current = new Vector3[rest.Length];
            strain = new float[rest.Length]; weights = new float[rest.Length];
            var triangleList = new List<Triangle>();
            samples = new Sample[Resolution * Resolution];
            smoothed = new float[samples.Length]; pixels = new Color32[samples.Length];
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                Vector2 ua = uvs[a], ub = uvs[b], uc = uvs[c];
                float minX = Mathf.Min(ua.x, Mathf.Min(ub.x, uc.x)), maxX = Mathf.Max(ua.x, Mathf.Max(ub.x, uc.x));
                float minY = Mathf.Min(ua.y, Mathf.Min(ub.y, uc.y)), maxY = Mathf.Max(ua.y, Mathf.Max(ub.y, uc.y));
                if (maxX < region.x || minX > region.x + region.z || maxY < region.y || minY > region.y + region.w) continue;
                Triangle triangle = MakeTriangle(a, b, c, rest);
                if (triangle.Weight <= 0f) continue;
                triangleList.Add(triangle);
                weights[a] += triangle.Weight; weights[b] += triangle.Weight; weights[c] += triangle.Weight;
                int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - region.x) / region.z * Resolution), 0, Resolution - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((maxX - region.x) / region.z * Resolution), 0, Resolution - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt((minY - region.y) / region.w * Resolution), 0, Resolution - 1);
                int y1 = Mathf.Clamp(Mathf.CeilToInt((maxY - region.y) / region.w * Resolution), 0, Resolution - 1);
                float denominator = (ub.y - uc.y) * (ua.x - uc.x) + (uc.x - ub.x) * (ua.y - uc.y);
                if (Mathf.Abs(denominator) < 0.000000001f) continue;
                for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                {
                    Vector2 p = new Vector2(region.x + (x + 0.5f) / Resolution * region.z, region.y + (y + 0.5f) / Resolution * region.w);
                    float wa = ((ub.y - uc.y) * (p.x - uc.x) + (uc.x - ub.x) * (p.y - uc.y)) / denominator;
                    float wb = ((uc.y - ua.y) * (p.x - uc.x) + (ua.x - uc.x) * (p.y - uc.y)) / denominator;
                    float wc = 1f - wa - wb;
                    if (wa >= -0.00001f && wb >= -0.00001f && wc >= -0.00001f)
                        samples[y * Resolution + x] = new Sample { A = a, B = b, C = c, TriangleIndex = triangleList.Count - 1, Weights = new Vector3(wa, wb, wc), Valid = true };
                }
            }
            triangles = triangleList.ToArray();
            triangleStrain = new float[triangles.Length];
            if (strainTexture == null)
            {
                strainTexture = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false, true)
                { name = "Runtime wrinkle compression", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            }
            strainTexture.SetPixels32(pixels); strainTexture.Apply(false, false);
            bindingDirty = false;
            lastUpdate = Time.unscaledTime;
            status = "已绑定 " + triangles.Length + " 个皮肤三角形；仅局部方向缩短且面积收缩时触发，拉伸和纯剪切不触发。";
        }

        private static Triangle MakeTriangle(int a, int b, int c, Vector3[] positions)
        {
            Vector3 ab = positions[b] - positions[a], ac = positions[c] - positions[a];
            float aa = Vector3.Dot(ab, ab), cross = Vector3.Dot(ab, ac), bb = Vector3.Dot(ac, ac);
            double determinant = (double)aa * bb - (double)cross * cross;
            return new Triangle { A = a, B = b, C = c, AA = aa, AB = cross, BB = bb,
                Weight = determinant > 1e-20 ? (float)Math.Sqrt(determinant) : 0f };
        }

        private void LateUpdate()
        {
            if (bindingDirty) Bind();
            if (bindingDirty || skinRenderer == null) return;
            if (Time.unscaledTime >= nextUpdate)
            {
                float dt = Mathf.Max(0f, Time.unscaledTime - lastUpdate);
                lastUpdate = Time.unscaledTime; nextUpdate = lastUpdate + 1f / 20f;
                if (!skin.CopyCurrentVerticesNonAlloc(current)) { bindingDirty = true; return; }
                Array.Clear(strain, 0, strain.Length);
                for (int t = 0; t < triangles.Length; t++)
                {
                    Triangle triangle = triangles[t];
                    Vector3 ab = current[triangle.B] - current[triangle.A], ac = current[triangle.C] - current[triangle.A];
                    triangleStrain[t] = WrinkleStrainMath.CompressionOnlyStrain(triangle.AA, triangle.AB, triangle.BB,
                        Vector3.Dot(ab, ab), Vector3.Dot(ab, ac), Vector3.Dot(ac, ac));
                    float value = triangleStrain[t] * triangle.Weight;
                    strain[triangle.A] += value; strain[triangle.B] += value; strain[triangle.C] += value;
                }
                for (int i = 0; i < strain.Length; i++) strain[i] /= Mathf.Max(0.000001f, weights[i]);
                float blend = 1f - Mathf.Exp(-response * dt);
                maximumActivation = 0f;
                for (int i = 0; i < samples.Length; i++)
                {
                    Sample sample = samples[i];
                    float value = sample.Valid ? sample.Weights.x * strain[sample.A] + sample.Weights.y * strain[sample.B] + sample.Weights.z * strain[sample.C] : 0f;
                    // Vertex averaging must not spread a compressed neighbour onto
                    // a triangle which is itself neutral, sheared or stretched.
                    if (sample.Valid) value = Mathf.Max(value, triangleStrain[sample.TriangleIndex]);
                    float target = sample.Valid ? WrinkleStrainMath.Activation(value, onsetCompression, fullCompression) : 0f;
                    // Retain smooth appearance while compressed, but no stale
                    // activation once the surface returns to tension/neutral.
                    smoothed[i] = target > 0f ? Mathf.Lerp(smoothed[i], target, blend) : 0f;
                    maximumActivation = Mathf.Max(maximumActivation, smoothed[i]);
                    byte intensity = (byte)Mathf.RoundToInt(smoothed[i] * 255f);
                    pixels[i] = new Color32(intensity, intensity, intensity, 255);
                }
                strainTexture.SetPixels32(pixels); strainTexture.Apply(false, false);
            }
            ApplyProperties(effectEnabled ? opacity : 0f);
        }

        private void ApplyProperties(float strength)
        {
            if (skinRenderer == null || properties == null) return;
            // Preserve the mesh's per-submesh textures and cut masks. Only the front skin receives wrinkles.
            skinRenderer.GetPropertyBlock(properties, 0);
            properties.SetTexture("_WrinkleMask", wrinkleMask != null ? wrinkleMask : Texture2D.blackTexture);
            properties.SetTexture("_WrinkleStrain", strainTexture != null ? strainTexture : Texture2D.blackTexture);
            properties.SetVector("_WrinkleRegion", region);
            properties.SetColor("_WrinkleTint", wrinkleTint);
            properties.SetFloat("_WrinkleOpacity", strength);
            properties.SetFloat("_WrinklePreview", previewTexture ? 1f : 0f);
            properties.SetFloat("_WrinkleUseSourceColor", useSourceColor ? 1f : 0f);
            skinRenderer.SetPropertyBlock(properties, 0);
        }

        private void OnDisable()
        {
            if (skin != null) { skin.MeshRebuilt -= InvalidateBinding; skin.RestPoseChanged -= InvalidateBinding; }
            ApplyProperties(0f);
        }

        private void OnDestroy()
        {
            if (strainTexture == null) return;
            if (Application.isPlaying) Destroy(strainTexture);
            else DestroyImmediate(strainTexture);
        }

        private void OnGUI()
        {
            if (!Application.isPlaying || !FaceRuntimeUILayout.Right(3)) return;
            GUILayout.BeginArea(FaceRuntimeUILayout.Panel(true, 430f), GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("皱纹实验：局部压缩／仅颜色叠加");
            GUILayout.Label(wrinkleMask != null ? "贴图：" + wrinkleMask.name : "尚未指定皱纹贴图");
            effectEnabled = GUILayout.Toggle(effectEnabled, "启用动态皱纹");
            GUILayout.Label("皱纹最大浓度：" + opacity.ToString("0.00"));
            opacity = GUILayout.HorizontalSlider(opacity, 0f, 1f);
            previewTexture = GUILayout.Toggle(previewTexture, "预览贴图位置（忽略压缩，仅作显示测试）");
            useSourceColor = GUILayout.Toggle(useSourceColor, "使用彩色贴图原色（黑底自动忽略）");
            GUILayout.Label("区域最大激活：" + maximumActivation.ToString("0.00"));
            GUILayout.Label("先关闭贴图预览；在左侧“操作工具”面板点击“挤压”，再在面部按住左键向上拖动。仅受压位置的纹路显现，拉伸或还原后清除。");
            GUILayout.Label("如果术前标记预览暂停了工具，请先按 F10 还原。中性脸默认不添加动态皱纹。");
            if (GUILayout.Button("以当前静止形状校准（清除激活）")) CalibrateCurrentShape();
            GUILayout.Label(status);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }

        public void CalibrateCurrentShape()
        {
            Bind();
            if (bindingDirty) return;
            Vector3[] captured = skin.CopyCurrentVertices();
            for (int i = 0; i < triangles.Length; i++)
            {
                Triangle triangle = triangles[i];
                // Keep the raster/vertex averaging weights stable while rebasing the metric.
                Triangle calibrated = MakeTriangle(triangle.A, triangle.B, triangle.C, captured);
                if (calibrated.Weight > 0f) { calibrated.Weight = triangle.Weight; triangles[i] = calibrated; }
            }
            status = "Calibrated to the current skin shape; no rest pose or geometry was modified.";
        }
    }
}
