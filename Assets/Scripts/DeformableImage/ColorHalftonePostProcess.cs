using UnityEngine;

namespace DeformableImage
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [AddComponentMenu("Deformable Image/Color Halftone Post Process")]
    public sealed class ColorHalftonePostProcess : MonoBehaviour
    {
        public enum HalftoneMode
        {
            OrderedBayer = 0,
            LegacyVariableDots = 1,
            CmykPrint = 2
        }

        public enum OrderedColorMode
        {
            Monochrome = 0,
            RgbChannels = 1,
            Duotone = 2
        }

        private const string ShaderName = "Hidden/DeformableImage/ColorHalftonePostProcess";

        [Header("Effect")]
        [SerializeField] private bool effectEnabled = true;
        [SerializeField] private HalftoneMode mode = HalftoneMode.OrderedBayer;
        [SerializeField, Range(0f, 1f)] private float blend = 1f;

        [Header("Ordered Bayer 8x8")]
        [SerializeField] private OrderedColorMode orderedColorMode = OrderedColorMode.RgbChannels;
        [Tooltip("Mosaic sampling block size in output pixels, matching AE's Mosaic stage.")]
        [SerializeField, Range(1f, 16f)] private float ditherSourcePixelSize = 3f;
        [Tooltip("Screen size of one Bayer threshold cell. The complete pattern repeats every 8 cells.")]
        [SerializeField, Range(1f, 12f)] private float ditherPatternScale = 2f;
        [Tooltip("Available output levels per channel. 2 produces strict binary channel dithering.")]
        [SerializeField, Range(2, 8)] private int ditherLevels = 2;
        [SerializeField, Range(-0.49f, 0.49f)] private float ditherThresholdBias;
        [Tooltip("Fixed Bayer phase difference between RGB channels. 0 keeps all channels perfectly aligned.")]
        [SerializeField, Range(0, 7)] private int ditherChannelOffset;
        [SerializeField] private Color ditherDarkColor = Color.black;
        [SerializeField] private Color ditherLightColor = Color.white;

        [Header("Dot Screen")]
        [Tooltip("Distance between dot centers in output screen pixels.")]
        [SerializeField, Range(1.5f, 24f)] private float dotSpacing = 3.25f;
        [SerializeField, Range(0.1f, 1.5f)] private float dotScale = 0.82f;
        [SerializeField, Range(0.1f, 3f)] private float dotSoftness = 0.7f;
        [SerializeField, Range(-90f, 90f)] private float screenAngle;

        [Header("Reference Pop Art Color")]
        [SerializeField, Range(0.25f, 3f)] private float contrast = 1.45f;
        [SerializeField, Range(0f, 3f)] private float saturation = 1.55f;
        [SerializeField, Range(2f, 16f)] private float posterizeSteps = 6f;
        [SerializeField, Range(0f, 1f)] private float whiteThreshold = 0.72f;
        [SerializeField, Range(0f, 1f)] private float whiteBoost = 0.95f;
        [SerializeField, Range(0.05f, 4f)] private float shadowGamma = 0.8f;
        [SerializeField, Range(0f, 1f)] private float inkStrength = 0.9f;
        [SerializeField] private Color inkColor = new Color(0.015f, 0.08f, 0.28f, 1f);

        [Header("CMYK Print Angles")]
        [SerializeField, Range(-90f, 90f)] private float cyanAngle = 15f;
        [SerializeField, Range(-90f, 90f)] private float magentaAngle = 75f;
        [SerializeField, Range(-90f, 90f)] private float yellowAngle;
        [SerializeField, Range(-90f, 90f)] private float blackAngle = 45f;
        [SerializeField, Range(0f, 1f)] private float blackGeneration = 0.85f;

        [Header("Retro Dither Finish")]
        [Tooltip("Horizontal red/blue channel misregistration in output pixels.")]
        [SerializeField, Range(0f, 6f)] private float rgbSplitPixels;
        [SerializeField, Range(0f, 0.35f)] private float grainAmount;
        [SerializeField, Range(1f, 8f)] private float grainScale = 1f;
        [Tooltip("0 keeps the grain fixed. Increase for animated film grain.")]
        [SerializeField, Range(0f, 5f)] private float grainSpeed;
        [SerializeField] private Color colorTint = Color.white;
        [SerializeField, Range(0f, 0.25f)] private float blackLift;

        [SerializeField, HideInInspector] private Shader halftoneShader;
        private Material halftoneMaterial;

        public bool EffectEnabled
        {
            get { return effectEnabled; }
            set { effectEnabled = value; }
        }

        public HalftoneMode Mode
        {
            get { return mode; }
            set { mode = value; }
        }

        private void OnValidate()
        {
            blend = Mathf.Clamp01(blend);
            ditherSourcePixelSize = Mathf.Clamp(ditherSourcePixelSize, 1f, 16f);
            ditherPatternScale = Mathf.Clamp(ditherPatternScale, 1f, 12f);
            ditherLevels = Mathf.Clamp(ditherLevels, 2, 8);
            ditherThresholdBias = Mathf.Clamp(ditherThresholdBias, -0.49f, 0.49f);
            ditherChannelOffset = Mathf.Clamp(ditherChannelOffset, 0, 7);
            dotSpacing = Mathf.Clamp(dotSpacing, 1.5f, 24f);
            dotScale = Mathf.Clamp(dotScale, 0.1f, 1.5f);
            dotSoftness = Mathf.Clamp(dotSoftness, 0.1f, 3f);
            contrast = Mathf.Clamp(contrast, 0.25f, 3f);
            saturation = Mathf.Clamp(saturation, 0f, 3f);
            posterizeSteps = Mathf.Clamp(posterizeSteps, 2f, 16f);
            whiteThreshold = Mathf.Clamp01(whiteThreshold);
            whiteBoost = Mathf.Clamp01(whiteBoost);
            shadowGamma = Mathf.Clamp(shadowGamma, 0.05f, 4f);
            inkStrength = Mathf.Clamp01(inkStrength);
            blackGeneration = Mathf.Clamp01(blackGeneration);
            rgbSplitPixels = Mathf.Clamp(rgbSplitPixels, 0f, 6f);
            grainAmount = Mathf.Clamp(grainAmount, 0f, 0.35f);
            grainScale = Mathf.Clamp(grainScale, 1f, 8f);
            grainSpeed = Mathf.Clamp(grainSpeed, 0f, 5f);
            blackLift = Mathf.Clamp(blackLift, 0f, 0.25f);
        }

        private void OnDisable()
        {
            DestroyMaterial();
        }

        private void OnDestroy()
        {
            DestroyMaterial();
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (!effectEnabled || !EnsureMaterial())
            {
                Graphics.Blit(source, destination);
                return;
            }

            halftoneMaterial.SetFloat("_Mode", (float)mode);
            halftoneMaterial.SetFloat("_OrderedColorMode", (float)orderedColorMode);
            halftoneMaterial.SetFloat("_Blend", blend);
            halftoneMaterial.SetFloat("_DitherSourcePixelSize", ditherSourcePixelSize);
            halftoneMaterial.SetFloat("_DitherPatternScale", ditherPatternScale);
            halftoneMaterial.SetFloat("_DitherLevels", ditherLevels);
            halftoneMaterial.SetFloat("_DitherThresholdBias", ditherThresholdBias);
            halftoneMaterial.SetFloat("_DitherChannelOffset", ditherChannelOffset);
            halftoneMaterial.SetColor("_DitherDarkColor", ditherDarkColor);
            halftoneMaterial.SetColor("_DitherLightColor", ditherLightColor);
            halftoneMaterial.SetFloat("_DotSpacing", dotSpacing);
            halftoneMaterial.SetFloat("_DotScale", dotScale);
            halftoneMaterial.SetFloat("_DotSoftness", dotSoftness);
            halftoneMaterial.SetFloat("_ScreenAngle", screenAngle);
            halftoneMaterial.SetFloat("_Contrast", contrast);
            halftoneMaterial.SetFloat("_Saturation", saturation);
            halftoneMaterial.SetFloat("_PosterizeSteps", posterizeSteps);
            halftoneMaterial.SetFloat("_WhiteThreshold", whiteThreshold);
            halftoneMaterial.SetFloat("_WhiteBoost", whiteBoost);
            halftoneMaterial.SetFloat("_ShadowGamma", shadowGamma);
            halftoneMaterial.SetFloat("_InkStrength", inkStrength);
            halftoneMaterial.SetColor("_InkColor", inkColor);
            halftoneMaterial.SetVector(
                "_CmykAngles",
                new Vector4(cyanAngle, magentaAngle, yellowAngle, blackAngle));
            halftoneMaterial.SetFloat("_BlackGeneration", blackGeneration);
            halftoneMaterial.SetFloat("_RgbSplitPixels", rgbSplitPixels);
            halftoneMaterial.SetFloat("_GrainAmount", grainAmount);
            halftoneMaterial.SetFloat("_GrainScale", grainScale);
            halftoneMaterial.SetFloat("_GrainSpeed", grainSpeed);
            halftoneMaterial.SetColor("_ColorTint", colorTint);
            halftoneMaterial.SetFloat("_BlackLift", blackLift);
            Graphics.Blit(source, destination, halftoneMaterial);
        }

        [ContextMenu("Apply Reference Image Preset")]
        public void ApplyReferencePreset()
        {
            effectEnabled = true;
            mode = HalftoneMode.OrderedBayer;
            orderedColorMode = OrderedColorMode.RgbChannels;
            blend = 1f;
            ditherSourcePixelSize = 2f;
            ditherPatternScale = 2f;
            ditherLevels = 2;
            ditherThresholdBias = 0f;
            ditherChannelOffset = 0;
            contrast = 1.3f;
            saturation = 1.5f;
            rgbSplitPixels = 0f;
            grainAmount = 0f;
            grainScale = 1f;
            grainSpeed = 0f;
            colorTint = Color.white;
            blackLift = 0f;
        }

        [ContextMenu("Apply Video Retro Dither Preset")]
        public void ApplyVideoRetroPreset()
        {
            effectEnabled = true;
            mode = HalftoneMode.OrderedBayer;
            orderedColorMode = OrderedColorMode.RgbChannels;
            blend = 1f;
            ditherSourcePixelSize = 3f;
            ditherPatternScale = 2f;
            ditherLevels = 3;
            ditherThresholdBias = 0f;
            ditherChannelOffset = 1;
            contrast = 1.35f;
            saturation = 0.72f;
            rgbSplitPixels = 0f;
            grainAmount = 0f;
            grainScale = 1f;
            grainSpeed = 0f;
            colorTint = new Color(1f, 0.92f, 0.96f, 1f);
            blackLift = 0.018f;
        }

        [ContextMenu("Apply CMYK Print Preset")]
        public void ApplyCmykPreset()
        {
            effectEnabled = true;
            mode = HalftoneMode.CmykPrint;
            blend = 1f;
            dotSpacing = 5f;
            dotScale = 1f;
            dotSoftness = 0.65f;
            contrast = 1.15f;
            saturation = 1.2f;
            cyanAngle = 15f;
            magentaAngle = 75f;
            yellowAngle = 0f;
            blackAngle = 45f;
            blackGeneration = 0.85f;
            rgbSplitPixels = 0.65f;
            grainAmount = 0.025f;
            grainScale = 1f;
            grainSpeed = 0f;
            colorTint = Color.white;
            blackLift = 0f;
        }

        [ContextMenu("Apply Strict Monochrome Bayer Preset")]
        public void ApplyMonochromeBayerPreset()
        {
            effectEnabled = true;
            mode = HalftoneMode.OrderedBayer;
            orderedColorMode = OrderedColorMode.Monochrome;
            blend = 1f;
            ditherSourcePixelSize = 2f;
            ditherPatternScale = 2f;
            ditherLevels = 2;
            ditherThresholdBias = 0f;
            ditherChannelOffset = 0;
            contrast = 1.25f;
            saturation = 0f;
            rgbSplitPixels = 0f;
            grainAmount = 0f;
            colorTint = Color.white;
            blackLift = 0f;
        }

        private bool EnsureMaterial()
        {
            if (halftoneMaterial != null)
            {
                return true;
            }

            if (halftoneShader == null)
            {
                halftoneShader = Shader.Find(ShaderName);
            }

            if (halftoneShader == null || !halftoneShader.isSupported)
            {
                return false;
            }

            halftoneMaterial = new Material(halftoneShader)
            {
                name = "Runtime Color Halftone Post Process",
                hideFlags = HideFlags.HideAndDontSave
            };
            return true;
        }

        private void DestroyMaterial()
        {
            if (halftoneMaterial == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(halftoneMaterial);
            }
            else
            {
                DestroyImmediate(halftoneMaterial);
            }

            halftoneMaterial = null;
        }
    }
}
