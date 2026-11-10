# Color-only wrinkle prototype

Open `Assets/Scenes/Facetest.unity` in Play Mode and select the right-side **皱纹** tab. Dynamic wrinkles are enabled by default but remain invisible on a neutral, uncompressed mesh. Enable **预览贴图位置** to check the generated mask placement independently of deformation; disable it to test strain response. If mouth-width preview froze the tools, press F10 first. Select the left tools tab and pinch the glabella or drag the two sides inward. Release/rebound or stretch the region to clear the creases on the next strain update. The maximum activation readout reflects compression, not a face-expression weight.

The overlay changes only front-skin RGB. It does not modify alpha, vertex geometry, normal maps, shadows, or expression weights. A 128x128 linear runtime texture stores local activation, updated at 20 Hz with exponential smoothing while compressed. Activation requires both negative minimum principal strain and local surface-area contraction beyond the onset threshold; the weaker signal limits intensity. Pure shear, area-preserving deformation and net expansion do not activate wrinkles. Each sample is gated by its own triangle as well as the averaged vertex field, preventing activation from compressed neighbours leaking into stretched triangles. Activation clears on the next strain update when compression ends. Translation and rotation preserve lengths and do not activate it. It is an artistic approximation, not a physical stress solver. A calibration button uses current edge lengths without changing the face rest pose.

Material UVs keep the mask attached to the skin. Mechanically intact triangle bindings are rebuilt after topology or rest-pose changes. Runtime properties preserve existing cut masks and affect only submesh 0. Cut pieces do not inherit a separate wrinkle controller in this prototype. The scene uses the full normalized skin UV region (0, 0, 1, 1); alignment is authored, not automatically detected. Strong halftone postprocessing may obscure the fine lines: compare with postprocessing disabled as well as the final style.

## Validation

Run `powershell -NoProfile -ExecutionPolicy Bypass -File Tests/WrinkleStrainChecks.ps1` for strain sign, thresholds, rigid-motion invariance, direction weights and monotonic response. Compile the runtime scripts against the installed Unity assemblies. Shader import and rendering must also be checked in Unity.

## Generated asset

- Path: `Assets/Image/Wrinkles/GlabellaWrinkleMask.png`
- Generated with the built-in image_gen tool; copied into the project without modifying the generated pixels.
- Scalar mask: black background, white/gray crease coverage; imported as a linear default texture with clamp sampling. No skin image or normal map is used.
- Prompt:

> Create a production grayscale wrinkle opacity mask texture for a real-time face game, square 1024x1024. Pure opaque BLACK background (0,0,0), with only three slender naturally curved VERTICAL glabellar frown crease strokes clustered at the center, central stroke slightly longer than side strokes. White represents crease opacity, black means no effect. Soft antialiased tapered ends, narrow irregular organic fine lines with gentle soft gray falloff immediately around each white crease core. Creases occupy central 45 percent of width and central 65 percent of height; all outer edges and broad margins pure black. Flat unlit scalar mask, NOT a face photo, no skin, no pores, no shadows, no text, no labels, no border, no lighting gradients, no colored pixels. It must look like an isolated high-quality painted alpha mask for natural frown lines, not scratches or tree branches.

## Full-face source image

Facetest.unity uses Assets/Image/Wrinkles/FullFaceWrinkles.png with source color enabled across the full skin UV region. The original PNG is unchanged. Import uses sRGB, clamp sampling and no compression to preserve fine detail. The shader ignores the black background and gates opacity by local compression. Enable texture preview to inspect alignment against the face in Unity.

### Shared skin coordinates

Full-image color wrinkles now sample input.uv, exactly like _MainTex. Previously they sampled input.cutUv, which spans the mesh rectangle and can differ from the skin Sprite texture rectangle after trimming. This caused position and scale errors for matching source layers. Grayscale localized masks retain their region mapping; compression continues to sample the normalized strain field.

## Compression-only regression coverage

The PowerShell checks include uniaxial/biaxial pulling, area-preserving shear, pulling with transverse shortening, rotated compression, net compression with transverse stretch, and degenerate metrics. The Unity validation fixture also checks a compression-to-stretch transition without rebinding (so stale activation cannot be hidden by resetting the fixture), mouth-like mixed stretch, shear rejection and vertical compression. This is an artistic net-compression criterion rather than a stress/buckling simulation.
