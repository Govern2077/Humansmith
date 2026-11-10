using System;
using System.Collections.Generic;
using DeformableImage;
using UnityEngine;
using UnityEngine.Rendering;

namespace FaceAnalysis
{
    [DisallowMultipleComponent]
    public sealed class FaceLandmarkVisualizer : MonoBehaviour
    {
        [Header("Data")]
        [Tooltip("A MonoBehaviour that implements IFaceLandmarkSource.")]
        [SerializeField] private MonoBehaviour landmarkSourceComponent;
        [SerializeField] private bool visible = true;
        [SerializeField] private KeyCode toggleKey = KeyCode.F7;

        [Header("Deformed Surface")]
        [SerializeField] private DeformableImageMesh targetMesh;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool drawOnDeformedSurface = true;
        [SerializeField, Range(1f, 12f)] private float markerPixelSize = 4f;
        [SerializeField, Range(1, 12)] private int landmarkStep = 1;
        [SerializeField, Min(0f)] private float cameraOffset = 0.004f;
        [SerializeField] private Color landmarkColor = new Color(0f, 0.95f, 1f, 0.9f);
        [SerializeField] private Color featureAnchorColor = new Color(1f, 0.85f, 0.1f, 1f);

        [Header("Optional Input Preview")]
        [SerializeField] private bool drawInputPreview;
        [SerializeField] private Rect previewRect = new Rect(500f, 12f, 420f, 420f);
        [SerializeField, Range(1f, 10f)] private float previewPointSize = 3f;

        private IFaceLandmarkSource landmarkSource;
        private MediaPipeStaticFaceLandmarkSource mediaPipeSource;
        private Material pointMaterial;
        private FaceLandmarkFrame boundFrame;
        private FaceAnalysisInputMode boundInputMode;
        private SurfaceBinding[] surfaceBindings = Array.Empty<SurfaceBinding>();
        private readonly List<DeformableImagePieceMover> trackedPieceMovers =
            new List<DeformableImagePieceMover>();
        private bool bindingsDirty = true;

        private struct SurfaceBinding
        {
            public bool IsValid;
            public DeformableImageMesh Mesh;
            public int A;
            public int B;
            public int C;
            public Vector3 Barycentric;
        }

        private struct SurfaceProjection
        {
            public DeformableImageMesh Mesh;
            public Vector2[] ProjectedVertices;
            public int[] Triangles;
            public bool[] VertexInFront;
        }

        private void Awake()
        {
            ResolveSource();
            EnsureMaterial();
        }

        private void OnEnable()
        {
            SubscribeToMesh();
            bindingsDirty = true;
        }

        private void OnDisable()
        {
            UnsubscribeFromMesh();
        }

        private void OnValidate()
        {
            landmarkStep = Mathf.Max(1, landmarkStep);
            ResolveSource();
            bindingsDirty = true;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                visible = !visible;
            }
        }

        private void OnDestroy()
        {
            UnsubscribeFromMesh();
            if (pointMaterial == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(pointMaterial);
            }
            else
            {
                DestroyImmediate(pointMaterial);
            }
        }

        private void OnRenderObject()
        {
            if (!Application.isPlaying
                || !visible
                || MediaPipeStaticFaceLandmarkSource.SuppressLandmarkRendering
                || !drawOnDeformedSurface
                || targetMesh == null)
            {
                return;
            }

            Camera renderCamera = Camera.current;
            if (renderCamera == null || (targetCamera != null && renderCamera != targetCamera))
            {
                return;
            }

            if (!TryGetValidFrame(out FaceLandmarkFrame frame)
                || !EnsureBindings(frame)
                || !EnsureMaterial())
            {
                return;
            }

            Vector3 cameraRight = renderCamera.transform.right;
            Vector3 cameraUp = renderCamera.transform.up;
            pointMaterial.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.QUADS);

            for (int index = 0; index < surfaceBindings.Length; index += landmarkStep)
            {
                SurfaceBinding binding = surfaceBindings[index];
                DeformableImageMesh bindingMesh = binding.Mesh;
                int vertexCount = bindingMesh != null ? bindingMesh.LogicalVertexCount : 0;
                if (!binding.IsValid
                    || bindingMesh == null
                    || binding.A < 0 || binding.B < 0 || binding.C < 0
                    || binding.A >= vertexCount || binding.B >= vertexCount || binding.C >= vertexCount)
                {
                    continue;
                }

                Vector3 localPoint = bindingMesh.GetLogicalVertex(binding.A) * binding.Barycentric.x
                    + bindingMesh.GetLogicalVertex(binding.B) * binding.Barycentric.y
                    + bindingMesh.GetLogicalVertex(binding.C) * binding.Barycentric.z;
                Vector3 worldPoint = bindingMesh.transform.TransformPoint(localPoint);
                Vector3 toCamera = renderCamera.transform.position - worldPoint;
                if (Vector3.Dot(renderCamera.transform.forward, worldPoint - renderCamera.transform.position) <= 0f)
                {
                    continue;
                }

                worldPoint += toCamera.sqrMagnitude > 0.000001f
                    ? toCamera.normalized * cameraOffset
                    : Vector3.zero;
                float halfSize = WorldSizeForPixels(renderCamera, worldPoint, markerPixelSize) * 0.5f;
                Vector3 right = cameraRight * halfSize;
                Vector3 up = cameraUp * halfSize;

                GL.Color(IsFeatureAnchor(index) ? featureAnchorColor : landmarkColor);
                GL.Vertex(worldPoint - right - up);
                GL.Vertex(worldPoint + right - up);
                GL.Vertex(worldPoint + right + up);
                GL.Vertex(worldPoint - right + up);
            }

            GL.End();
            GL.PopMatrix();
        }

        private void OnGUI()
        {
            if (!visible
                || DeformableImage.FaceRuntimeUILayout.Enabled
                || !drawInputPreview
                || mediaPipeSource == null
                || mediaPipeSource.CurrentInputTexture == null)
            {
                return;
            }

            Texture texture = mediaPipeSource.CurrentInputTexture;
            GUI.Box(previewRect, string.Empty);
            Rect available = new Rect(
                previewRect.x + 8f,
                previewRect.y + 8f,
                Mathf.Max(1f, previewRect.width - 16f),
                Mathf.Max(1f, previewRect.height - 16f));
            Rect imageRect = FitRect(available, texture.width, texture.height);
            GUI.DrawTexture(imageRect, texture, ScaleMode.StretchToFill, false);

            if (!TryGetValidFrame(out FaceLandmarkFrame frame))
            {
                return;
            }

            Color previousColor = GUI.color;
            Texture2D dotTexture = Texture2D.whiteTexture;
            Vector3[] landmarks = frame.NormalizedLandmarks;
            for (int index = 0; index < landmarks.Length; index += landmarkStep)
            {
                if (!TryGetRawTopLeftNormalized(landmarks[index], out Vector2 rawPoint))
                {
                    continue;
                }

                float size = IsFeatureAnchor(index) ? previewPointSize * 1.8f : previewPointSize;
                GUI.color = IsFeatureAnchor(index) ? featureAnchorColor : landmarkColor;
                GUI.DrawTexture(new Rect(
                    imageRect.x + rawPoint.x * imageRect.width - size * 0.5f,
                    imageRect.y + rawPoint.y * imageRect.height - size * 0.5f,
                    size,
                    size), dotTexture);
            }

            GUI.color = previousColor;
        }

        /// <summary>
        /// Converts the original MediaPipe landmarks to their current positions on the
        /// deformable skin. The original Sprite pixel scale is retained, so measurements
        /// change with local mesh deformation instead of changing with Game view resolution.
        /// </summary>
        public bool TryGetCurrentDeformedFrame(out FaceLandmarkFrame deformedFrame)
        {
            deformedFrame = null;
            if (targetMesh == null
                || !TryGetValidFrame(out FaceLandmarkFrame sourceFrame)
                || !EnsureBindings(sourceFrame))
            {
                return false;
            }

            // Measurement coordinates stay in the source image/material space. A generated
            // player face may have a different mechanical rest pose, but must not redefine
            // the normalized UV frame used by MediaPipe landmarks.
            Vector3[] restVertices = targetMesh.CopyMaterialSpaceVertices();
            if (restVertices == null || restVertices.Length == 0)
            {
                return false;
            }

            float minimumX = float.PositiveInfinity;
            float maximumX = float.NegativeInfinity;
            float minimumY = float.PositiveInfinity;
            float maximumY = float.NegativeInfinity;
            for (int index = 0; index < restVertices.Length; index++)
            {
                Vector3 rest = restVertices[index];
                minimumX = Mathf.Min(minimumX, rest.x);
                maximumX = Mathf.Max(maximumX, rest.x);
                minimumY = Mathf.Min(minimumY, rest.y);
                maximumY = Mathf.Max(maximumY, rest.y);
            }

            float restWidth = maximumX - minimumX;
            float restHeight = maximumY - minimumY;
            if (restWidth < 0.0001f || restHeight < 0.0001f)
            {
                return false;
            }

            Vector3[] deformedLandmarks = new Vector3[surfaceBindings.Length];
            Vector3 nanPoint = new Vector3(float.NaN, float.NaN, float.NaN);
            for (int index = 0; index < surfaceBindings.Length; index++)
            {
                SurfaceBinding binding = surfaceBindings[index];
                DeformableImageMesh bindingMesh = binding.Mesh;
                int vertexCount = bindingMesh != null ? bindingMesh.LogicalVertexCount : 0;
                if (!binding.IsValid
                    || bindingMesh == null
                    || binding.A < 0 || binding.B < 0 || binding.C < 0
                    || binding.A >= vertexCount || binding.B >= vertexCount || binding.C >= vertexCount)
                {
                    deformedLandmarks[index] = nanPoint;
                    continue;
                }

                Vector3 current = bindingMesh.GetLogicalVertex(binding.A) * binding.Barycentric.x
                    + bindingMesh.GetLogicalVertex(binding.B) * binding.Barycentric.y
                    + bindingMesh.GetLogicalVertex(binding.C) * binding.Barycentric.z;
                deformedLandmarks[index] = new Vector3(
                    (current.x - minimumX) / restWidth,
                    1f - (current.y - minimumY) / restHeight,
                    current.z / restWidth);
            }

            deformedFrame = new FaceLandmarkFrame(
                deformedLandmarks,
                sourceFrame.ImageWidth,
                sourceFrame.ImageHeight,
                sourceFrame.DetectionConfidence,
                sourceFrame.HasHeadPose,
                sourceFrame.HeadEulerAngles,
                sourceFrame.HasDetectionConfidence);
            return true;
        }

        /// <summary>
        /// Discards landmark-to-triangle barycentric bindings. Call this whenever the
        /// analyzed standard image changes, even when the landmark count stays at 478.
        /// Comparing only the frame size/count is not sufficient because a different
        /// face can otherwise keep the previous image's triangle attachments.
        /// </summary>
        public void InvalidateBindings()
        {
            boundFrame = null;
            bindingsDirty = true;
            surfaceBindings = Array.Empty<SurfaceBinding>();
            RefreshDetachedPieceSubscriptions();
        }

        private bool EnsureBindings(FaceLandmarkFrame frame)
        {
            FaceAnalysisInputMode inputMode = mediaPipeSource != null
                ? mediaPipeSource.InputMode
                : FaceAnalysisInputMode.StaticSprite;
            if (!bindingsDirty && ReferenceEquals(boundFrame, frame) && boundInputMode == inputMode)
            {
                return surfaceBindings.Length == frame.NormalizedLandmarks.Length;
            }

            boundFrame = frame;
            boundInputMode = inputMode;
            bindingsDirty = false;
            RefreshDetachedPieceSubscriptions();
            Vector3[] landmarks = frame.NormalizedLandmarks;
            surfaceBindings = new SurfaceBinding[landmarks.Length];

            if (inputMode == FaceAnalysisInputMode.RenderedCamera)
            {
                BuildRenderedCameraBindings(landmarks);
            }
            else
            {
                BuildSpriteBindings(landmarks);
            }

            return true;
        }

        private void BuildSpriteBindings(Vector3[] landmarks)
        {
            Sprite sourceSprite = mediaPipeSource != null ? mediaPipeSource.SourceSprite : targetMesh.FrontSprite;
            Texture sourceTexture = sourceSprite != null ? sourceSprite.texture : null;
            Rect sourceRect = sourceSprite != null
                ? sourceSprite.textureRect
                : new Rect(0f, 0f, 1f, 1f);
            List<SurfaceProjection> surfaces = BuildRestUvSurfaces();

            for (int index = 0; index < landmarks.Length; index++)
            {
                if (!TryGetRawTopLeftNormalized(landmarks[index], out Vector2 rawPoint))
                {
                    continue;
                }

                Vector2 meshUv;
                if (sourceTexture != null && sourceRect.width > 0.0001f && sourceRect.height > 0.0001f)
                {
                    float pixelX = rawPoint.x * sourceTexture.width;
                    float pixelY = (1f - rawPoint.y) * sourceTexture.height;
                    meshUv = new Vector2(
                        (pixelX - sourceRect.xMin) / sourceRect.width,
                        (pixelY - sourceRect.yMin) / sourceRect.height);
                }
                else
                {
                    meshUv = new Vector2(rawPoint.x, 1f - rawPoint.y);
                }

                for (int surfaceIndex = 0; surfaceIndex < surfaces.Count; surfaceIndex++)
                {
                    SurfaceProjection surface = surfaces[surfaceIndex];
                    SurfaceBinding binding = FindBinding(
                        surface.Mesh,
                        meshUv,
                        surface.ProjectedVertices,
                        surface.Triangles);
                    if (binding.IsValid)
                    {
                        surfaceBindings[index] = binding;
                        break;
                    }
                }
            }
        }

        private void BuildRenderedCameraBindings(Vector3[] landmarks)
        {
            Camera analysisCamera = mediaPipeSource != null && mediaPipeSource.RenderedCamera != null
                ? mediaPipeSource.RenderedCamera
                : targetCamera;
            if (analysisCamera == null)
            {
                return;
            }

            List<SurfaceProjection> surfaces = BuildCameraProjectionSurfaces(analysisCamera);

            for (int index = 0; index < landmarks.Length; index++)
            {
                if (!TryGetRawTopLeftNormalized(landmarks[index], out Vector2 rawPoint))
                {
                    continue;
                }

                Vector2 viewportPoint = new Vector2(rawPoint.x, 1f - rawPoint.y);
                for (int surfaceIndex = 0; surfaceIndex < surfaces.Count; surfaceIndex++)
                {
                    SurfaceProjection surface = surfaces[surfaceIndex];
                    SurfaceBinding binding = FindBinding(
                        surface.Mesh,
                        viewportPoint,
                        surface.ProjectedVertices,
                        surface.Triangles,
                        surface.VertexInFront);
                    if (binding.IsValid)
                    {
                        surfaceBindings[index] = binding;
                        break;
                    }
                }
            }
        }

        private List<SurfaceProjection> BuildRestUvSurfaces()
        {
            List<SurfaceProjection> result = new List<SurfaceProjection>();
            List<DeformableImageMesh> meshes = GetActiveMeasurementMeshes();
            for (int index = 0; index < meshes.Count; index++)
            {
                DeformableImageMesh mesh = meshes[index];
                int[] triangles = mesh.CopyLogicalTriangles();
                if (triangles == null || triangles.Length < 3)
                {
                    continue;
                }

                result.Add(new SurfaceProjection
                {
                    Mesh = mesh,
                    ProjectedVertices = mesh.CopyNormalizedRestUvs(),
                    Triangles = triangles,
                    VertexInFront = null
                });
            }

            return result;
        }

        private List<SurfaceProjection> BuildCameraProjectionSurfaces(Camera analysisCamera)
        {
            List<SurfaceProjection> result = new List<SurfaceProjection>();
            List<DeformableImageMesh> meshes = GetActiveMeasurementMeshes();
            for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
            {
                DeformableImageMesh mesh = meshes[meshIndex];
                int[] triangles = mesh.CopyLogicalTriangles();
                Vector3[] currentVertices = mesh.CopyCurrentVertices();
                if (triangles == null || triangles.Length < 3 || currentVertices.Length == 0)
                {
                    continue;
                }

                Vector2[] viewportVertices = new Vector2[currentVertices.Length];
                bool[] vertexInFront = new bool[currentVertices.Length];
                for (int vertex = 0; vertex < currentVertices.Length; vertex++)
                {
                    Vector3 worldPoint = mesh.transform.TransformPoint(currentVertices[vertex]);
                    Vector3 viewportPoint;
                    if (mediaPipeSource == null
                        || !mediaPipeSource.TryWorldToLastRenderedViewport(worldPoint, out viewportPoint))
                    {
                        viewportPoint = analysisCamera.WorldToViewportPoint(worldPoint);
                    }

                    viewportVertices[vertex] = new Vector2(viewportPoint.x, viewportPoint.y);
                    vertexInFront[vertex] = viewportPoint.z > 0f;
                }

                result.Add(new SurfaceProjection
                {
                    Mesh = mesh,
                    ProjectedVertices = viewportVertices,
                    Triangles = triangles,
                    VertexInFront = vertexInFront
                });
            }

            return result;
        }

        private List<DeformableImageMesh> GetActiveMeasurementMeshes()
        {
            List<DeformableImageMesh> result = new List<DeformableImageMesh> { targetMesh };
            for (int index = 0; index < trackedPieceMovers.Count; index++)
            {
                DeformableImagePieceMover mover = trackedPieceMovers[index];
                if (mover != null
                    && mover.IsReattachedAtHome
                    && mover.DeformableMesh != null)
                {
                    result.Add(mover.DeformableMesh);
                }
            }

            return result;
        }

        private bool TryGetRawTopLeftNormalized(Vector3 landmark, out Vector2 rawPoint)
        {
            rawPoint = default(Vector2);
            if (!IsFinite(landmark))
            {
                return false;
            }

            bool flipX = mediaPipeSource != null && mediaPipeSource.InputWasFlippedHorizontally;
            rawPoint = new Vector2(
                flipX ? 1f - landmark.x : landmark.x,
                landmark.y);
            return rawPoint.x >= -0.1f && rawPoint.x <= 1.1f
                && rawPoint.y >= -0.1f && rawPoint.y <= 1.1f;
        }

        private static SurfaceBinding FindBinding(
            DeformableImageMesh mesh,
            Vector2 point,
            Vector2[] projectedVertices,
            int[] triangles,
            bool[] vertexInFront = null)
        {
            const float tolerance = -0.0005f;
            for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
            {
                int a = triangles[triangle];
                int b = triangles[triangle + 1];
                int c = triangles[triangle + 2];
                if (a < 0 || b < 0 || c < 0
                    || a >= projectedVertices.Length
                    || b >= projectedVertices.Length
                    || c >= projectedVertices.Length
                    || (vertexInFront != null
                        && (!vertexInFront[a] || !vertexInFront[b] || !vertexInFront[c])))
                {
                    continue;
                }

                Vector2 pointA = projectedVertices[a];
                Vector2 pointB = projectedVertices[b];
                Vector2 pointC = projectedVertices[c];
                if (point.x < Mathf.Min(pointA.x, Mathf.Min(pointB.x, pointC.x)) + tolerance
                    || point.x > Mathf.Max(pointA.x, Mathf.Max(pointB.x, pointC.x)) - tolerance
                    || point.y < Mathf.Min(pointA.y, Mathf.Min(pointB.y, pointC.y)) + tolerance
                    || point.y > Mathf.Max(pointA.y, Mathf.Max(pointB.y, pointC.y)) - tolerance
                    || !TryGetBarycentric(
                        point,
                        pointA,
                        pointB,
                        pointC,
                        out Vector3 barycentric)
                    || barycentric.x < tolerance
                    || barycentric.y < tolerance
                    || barycentric.z < tolerance)
                {
                    continue;
                }

                float total = barycentric.x + barycentric.y + barycentric.z;
                if (Mathf.Abs(total) > 0.000001f)
                {
                    barycentric /= total;
                }

                return new SurfaceBinding
                {
                    IsValid = true,
                    Mesh = mesh,
                    A = a,
                    B = b,
                    C = c,
                    Barycentric = barycentric
                };
            }

            return default(SurfaceBinding);
        }

        private static bool TryGetBarycentric(
            Vector2 point,
            Vector2 a,
            Vector2 b,
            Vector2 c,
            out Vector3 barycentric)
        {
            Vector2 v0 = b - a;
            Vector2 v1 = c - a;
            Vector2 v2 = point - a;
            float denominator = v0.x * v1.y - v1.x * v0.y;
            if (Mathf.Abs(denominator) < 0.0000001f)
            {
                barycentric = default(Vector3);
                return false;
            }

            float inverse = 1f / denominator;
            float weightB = (v2.x * v1.y - v1.x * v2.y) * inverse;
            float weightC = (v0.x * v2.y - v2.x * v0.y) * inverse;
            barycentric = new Vector3(1f - weightB - weightC, weightB, weightC);
            return true;
        }

        private bool TryGetValidFrame(out FaceLandmarkFrame frame)
        {
            frame = null;
            if (landmarkSource == null)
            {
                ResolveSource();
            }

            return landmarkSource != null
                && landmarkSource.TryGetLatestFrame(out frame)
                && frame != null
                && frame.IsValid;
        }

        private void ResolveSource()
        {
            landmarkSource = landmarkSourceComponent as IFaceLandmarkSource;
            mediaPipeSource = landmarkSourceComponent as MediaPipeStaticFaceLandmarkSource;
        }

        private void SubscribeToMesh()
        {
            if (targetMesh != null)
            {
                targetMesh.MeshRebuilt -= HandleMeshRebuilt;
                targetMesh.MeshRebuilt += HandleMeshRebuilt;
            }

            RefreshDetachedPieceSubscriptions();
        }

        private void UnsubscribeFromMesh()
        {
            if (targetMesh != null)
            {
                targetMesh.MeshRebuilt -= HandleMeshRebuilt;
            }

            for (int index = 0; index < trackedPieceMovers.Count; index++)
            {
                DeformableImagePieceMover mover = trackedPieceMovers[index];
                if (mover == null)
                {
                    continue;
                }

                mover.ReattachmentStateChanged -= HandlePieceReattachmentStateChanged;
                if (mover.DeformableMesh != null)
                {
                    mover.DeformableMesh.MeshRebuilt -= HandleMeshRebuilt;
                }
            }

            trackedPieceMovers.Clear();
        }

        private void RefreshDetachedPieceSubscriptions()
        {
            for (int index = 0; index < trackedPieceMovers.Count; index++)
            {
                DeformableImagePieceMover previous = trackedPieceMovers[index];
                if (previous == null)
                {
                    continue;
                }

                previous.ReattachmentStateChanged -= HandlePieceReattachmentStateChanged;
                if (previous.DeformableMesh != null)
                {
                    previous.DeformableMesh.MeshRebuilt -= HandleMeshRebuilt;
                }
            }

            trackedPieceMovers.Clear();
            if (targetMesh == null)
            {
                return;
            }

            DeformableImagePieceMover[] movers =
                FindObjectsOfType<DeformableImagePieceMover>(true);
            for (int index = 0; index < movers.Length; index++)
            {
                DeformableImagePieceMover mover = movers[index];
                DeformableImageMesh pieceMesh = mover != null ? mover.DeformableMesh : null;
                if (pieceMesh == null || pieceMesh.DetachedFromMesh != targetMesh)
                {
                    continue;
                }

                trackedPieceMovers.Add(mover);
                mover.ReattachmentStateChanged -= HandlePieceReattachmentStateChanged;
                mover.ReattachmentStateChanged += HandlePieceReattachmentStateChanged;
                pieceMesh.MeshRebuilt -= HandleMeshRebuilt;
                pieceMesh.MeshRebuilt += HandleMeshRebuilt;
            }
        }

        private void HandleMeshRebuilt()
        {
            InvalidateBindings();
        }

        private void HandlePieceReattachmentStateChanged(
            DeformableImagePieceMover mover,
            bool isReattached)
        {
            bindingsDirty = true;
        }

        private bool EnsureMaterial()
        {
            if (pointMaterial != null)
            {
                return true;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                return false;
            }

            pointMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            pointMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            pointMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            pointMaterial.SetInt("_Cull", (int)CullMode.Off);
            pointMaterial.SetInt("_ZWrite", 0);
            pointMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            return true;
        }

        private static float WorldSizeForPixels(Camera camera, Vector3 worldPoint, float pixels)
        {
            float pixelHeight = Mathf.Max(1f, camera.pixelHeight);
            if (camera.orthographic)
            {
                return 2f * camera.orthographicSize * pixels / pixelHeight;
            }

            float distance = Vector3.Dot(worldPoint - camera.transform.position, camera.transform.forward);
            float viewHeight = 2f * Mathf.Max(0.01f, distance)
                * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return viewHeight * pixels / pixelHeight;
        }

        private static Rect FitRect(Rect available, float sourceWidth, float sourceHeight)
        {
            float scale = Mathf.Min(
                available.width / Mathf.Max(1f, sourceWidth),
                available.height / Mathf.Max(1f, sourceHeight));
            float width = sourceWidth * scale;
            float height = sourceHeight * scale;
            return new Rect(
                available.x + (available.width - width) * 0.5f,
                available.y + (available.height - height) * 0.5f,
                width,
                height);
        }

        private static bool IsFeatureAnchor(int index)
        {
            return index == MediaPipeFaceLandmarkIndices.ForeheadCenter
                || index == MediaPipeFaceLandmarkIndices.Chin
                || index == MediaPipeFaceLandmarkIndices.LeftCheek
                || index == MediaPipeFaceLandmarkIndices.RightCheek
                || index == MediaPipeFaceLandmarkIndices.ImageLeftEyeOuter
                || index == MediaPipeFaceLandmarkIndices.ImageLeftEyeInner
                || index == MediaPipeFaceLandmarkIndices.ImageRightEyeInner
                || index == MediaPipeFaceLandmarkIndices.ImageRightEyeOuter
                || index == MediaPipeFaceLandmarkIndices.NoseLeftWing
                || index == MediaPipeFaceLandmarkIndices.NoseRightWing
                || index == MediaPipeFaceLandmarkIndices.NoseTip
                || index == MediaPipeFaceLandmarkIndices.MouthLeftCorner
                || index == MediaPipeFaceLandmarkIndices.MouthRightCorner
                || index == MediaPipeFaceLandmarkIndices.UpperLipCenter
                || index == MediaPipeFaceLandmarkIndices.LowerLipCenter;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}
