Shader "Hidden/DeformableImage/ColorHalftonePostProcess"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Mode;
            float _OrderedColorMode;
            float _Blend;
            float _DitherSourcePixelSize;
            float _DitherPatternScale;
            float _DitherLevels;
            float _DitherThresholdBias;
            float _DitherChannelOffset;
            float4 _DitherDarkColor;
            float4 _DitherLightColor;
            float _DotSpacing;
            float _DotScale;
            float _DotSoftness;
            float _ScreenAngle;
            float _Contrast;
            float _Saturation;
            float _PosterizeSteps;
            float _WhiteThreshold;
            float _WhiteBoost;
            float _ShadowGamma;
            float _InkStrength;
            float4 _InkColor;
            float4 _CmykAngles;
            float _BlackGeneration;
            float _RgbSplitPixels;
            float _GrainAmount;
            float _GrainScale;
            float _GrainSpeed;
            float4 _ColorTint;
            float _BlackLift;

            static const float Pi = 3.14159265359;

            float2 RotateCoordinates(float2 coordinates, float angleDegrees)
            {
                float angle = angleDegrees * (Pi / 180.0);
                float sineValue = sin(angle);
                float cosineValue = cos(angle);
                return float2(
                    coordinates.x * cosineValue - coordinates.y * sineValue,
                    coordinates.x * sineValue + coordinates.y * cosineValue);
            }

            float HalftoneDot(float2 pixelPosition, float angle, float coverage)
            {
                float2 rotated = RotateCoordinates(pixelPosition, angle);
                float2 cell = frac(rotated / max(1.0, _DotSpacing)) - 0.5;
                float distanceToCenter = length(cell);
                float radius = 0.5 * sqrt(saturate(coverage)) * _DotScale;
                float antialiasWidth = max(0.0005, fwidth(distanceToCenter) * _DotSoftness);
                return smoothstep(
                    radius + antialiasWidth,
                    radius - antialiasWidth,
                    distanceToCenter);
            }

            float3 SampleSource(float2 uv)
            {
                float2 split = float2(_RgbSplitPixels * _MainTex_TexelSize.x, 0.0);
                return saturate(float3(
                    tex2D(_MainTex, saturate(uv + split)).r,
                    tex2D(_MainTex, uv).g,
                    tex2D(_MainTex, saturate(uv - split)).b));
            }

            float HashNoise(float2 coordinates)
            {
                coordinates = frac(coordinates * float2(0.1031, 0.1030));
                coordinates += dot(coordinates, coordinates.yx + 33.33);
                return frac((coordinates.x + coordinates.y) * coordinates.x);
            }

            float Bayer2Value(int x, int y)
            {
                if (y == 0)
                {
                    return x == 0 ? 0.0 : 2.0;
                }

                return x == 0 ? 3.0 : 1.0;
            }

            float Bayer8Threshold(float2 pixelPosition, float2 cellOffset)
            {
                float2 matrixCellFloat = floor(
                    pixelPosition / max(1.0, _DitherPatternScale) + cellOffset);
                int matrixX = ((int)matrixCellFloat.x) % 8;
                int matrixY = ((int)matrixCellFloat.y) % 8;
                int xBit0 = matrixX % 2;
                int yBit0 = matrixY % 2;
                int xBit1 = (matrixX / 2) % 2;
                int yBit1 = (matrixY / 2) % 2;
                int xBit2 = (matrixX / 4) % 2;
                int yBit2 = (matrixY / 4) % 2;

                // Recursive Bayer construction. The same 8x8 order is tiled forever,
                // producing the deliberately rigid cross-hatch seen in ordered dither.
                float matrixIndex = 16.0 * Bayer2Value(xBit0, yBit0)
                    + 4.0 * Bayer2Value(xBit1, yBit1)
                    + Bayer2Value(xBit2, yBit2);
                return saturate((matrixIndex + 0.5) / 64.0 + _DitherThresholdBias);
            }

            float OrderedQuantize(float value, float threshold, float levelCount)
            {
                float maximumLevel = max(1.0, floor(levelCount + 0.5) - 1.0);
                float scaledValue = saturate(value) * maximumLevel;
                float lowerLevel = floor(scaledValue);
                float upperChoice = step(threshold, frac(scaledValue));
                return min(maximumLevel, lowerLevel + upperChoice) / maximumLevel;
            }

            float3 GradePopColor(float3 sourceColor, out float luminance)
            {
                float3 contrasted = (sourceColor - 0.5) * _Contrast + 0.5;
                luminance = dot(contrasted, float3(0.2126, 0.7152, 0.0722));
                float3 saturated = lerp(luminance.xxx, contrasted, _Saturation);
                float steps = max(2.0, _PosterizeSteps);
                float3 posterized = floor(saturate(saturated) * steps + 0.5) / steps;
                float highlight = smoothstep(_WhiteThreshold, 1.0, luminance) * _WhiteBoost;
                return lerp(posterized, 1.0.xxx, saturate(highlight));
            }

            float3 RenderOrderedBayer(float2 uv, float2 pixelPosition)
            {
                float sourceScale = max(1.0, _DitherSourcePixelSize);
                float2 sourceCellCenter = (floor(pixelPosition / sourceScale) + 0.5)
                    * sourceScale;
                float2 sourceUv = saturate(sourceCellCenter * _MainTex_TexelSize.xy);
                float3 sourceColor = SampleSource(sourceUv);
                sourceColor = saturate((sourceColor - 0.5) * _Contrast + 0.5);
                float luminance = dot(sourceColor, float3(0.2126, 0.7152, 0.0722));
                sourceColor = saturate(lerp(luminance.xxx, sourceColor, _Saturation));

                float threshold = Bayer8Threshold(pixelPosition, 0.0.xx);
                if (_OrderedColorMode < 0.5)
                {
                    float monochrome = OrderedQuantize(
                        luminance,
                        threshold,
                        _DitherLevels);
                    return monochrome.xxx;
                }

                if (_OrderedColorMode < 1.5)
                {
                    // Each channel uses the same deterministic matrix, optionally with
                    // a fixed integer phase offset. This creates regular colored motifs,
                    // never stochastic colored noise.
                    float offset = floor(_DitherChannelOffset + 0.5);
                    float redThreshold = threshold;
                    float greenThreshold = Bayer8Threshold(
                        pixelPosition,
                        float2(offset, offset));
                    float blueThreshold = Bayer8Threshold(
                        pixelPosition,
                        float2(offset * 2.0, offset * 3.0));
                    return float3(
                        OrderedQuantize(sourceColor.r, redThreshold, _DitherLevels),
                        OrderedQuantize(sourceColor.g, greenThreshold, _DitherLevels),
                        OrderedQuantize(sourceColor.b, blueThreshold, _DitherLevels));
                }

                float duotoneAmount = OrderedQuantize(
                    luminance,
                    threshold,
                    _DitherLevels);
                return lerp(
                    _DitherDarkColor.rgb,
                    _DitherLightColor.rgb,
                    duotoneAmount);
            }

            float3 RenderPopArt(float2 uv, float2 pixelPosition)
            {
                // Sample once per dot cell. This reproduces AE's small block/dither base
                // instead of placing perfectly smooth dots over an untouched photograph.
                float2 cellCenterPixel = (floor(pixelPosition / _DotSpacing) + 0.5) * _DotSpacing;
                float2 sampleUv = saturate(cellCenterPixel * _MainTex_TexelSize.xy);
                float3 sourceColor = SampleSource(sampleUv);
                float luminance;
                float3 baseColor = GradePopColor(sourceColor, luminance);
                float shadowCoverage = pow(saturate(1.0 - luminance), max(0.05, _ShadowGamma));
                float dotMask = HalftoneDot(
                    pixelPosition,
                    _ScreenAngle,
                    shadowCoverage);
                dotMask *= _InkStrength;
                float3 inkedColor = baseColor * saturate(_InkColor.rgb);
                return lerp(baseColor, inkedColor, saturate(dotMask));
            }

            float3 RenderCmyk(float2 uv, float2 pixelPosition)
            {
                float2 cellCenterPixel = (floor(pixelPosition / _DotSpacing) + 0.5) * _DotSpacing;
                float2 sampleUv = saturate(cellCenterPixel * _MainTex_TexelSize.xy);
                float3 rgb = SampleSource(sampleUv);
                rgb = saturate((rgb - 0.5) * _Contrast + 0.5);
                float luminance = dot(rgb, float3(0.2126, 0.7152, 0.0722));
                rgb = saturate(lerp(luminance.xxx, rgb, _Saturation));

                float3 cmy = 1.0 - rgb;
                float black = min(cmy.x, min(cmy.y, cmy.z)) * _BlackGeneration;
                cmy = saturate((cmy - black) / max(0.0001, 1.0 - black));

                float cyanDot = HalftoneDot(pixelPosition, _CmykAngles.x, cmy.x);
                float magentaDot = HalftoneDot(pixelPosition, _CmykAngles.y, cmy.y);
                float yellowDot = HalftoneDot(pixelPosition, _CmykAngles.z, cmy.z);
                float blackDot = HalftoneDot(pixelPosition, _CmykAngles.w, black);

                float3 printed = 1.0.xxx;
                printed *= lerp(1.0.xxx, float3(0.0, 1.0, 1.0), cyanDot);
                printed *= lerp(1.0.xxx, float3(1.0, 0.0, 1.0), magentaDot);
                printed *= lerp(1.0.xxx, float3(1.0, 1.0, 0.0), yellowDot);
                printed *= lerp(1.0.xxx, 0.0.xxx, blackDot);
                return printed;
            }

            fixed4 frag(v2f_img input) : SV_Target
            {
                float2 uv = input.uv;
                float4 source = tex2D(_MainTex, uv);
                float2 pixelPosition = uv * _MainTex_TexelSize.zw;
                float3 halftone;
                if (_Mode < 0.5)
                {
                    halftone = RenderOrderedBayer(uv, pixelPosition);
                }
                else if (_Mode < 1.5)
                {
                    halftone = RenderPopArt(uv, pixelPosition);
                }
                else
                {
                    halftone = RenderCmyk(uv, pixelPosition);
                }
                float grainFrame = floor(_Time.y * _GrainSpeed * 12.0);
                float grain = HashNoise(
                    floor(pixelPosition / max(1.0, _GrainScale)) + grainFrame);
                halftone += (grain - 0.5) * _GrainAmount;
                halftone = saturate(halftone * _ColorTint.rgb + _BlackLift);
                return float4(lerp(source.rgb, halftone, saturate(_Blend)), source.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
