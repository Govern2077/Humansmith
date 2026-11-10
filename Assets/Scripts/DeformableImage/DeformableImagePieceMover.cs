using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeformableImage
{
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(DeformableImageMesh))]
    public sealed class DeformableImagePieceMover : MonoBehaviour
    {
        [Header("Pointer")]
        [SerializeField] private Camera targetCamera;
        [SerializeField, Min(0.001f)] private float hitTolerance = 0.05f;
        [SerializeField, Range(0, 2)] private int dragMouseButton;
        [SerializeField] private bool bringToFrontWhenGrabbed = true;

        [Header("Lightweight 2D Cloth")]
        [SerializeField, Min(0f)] private float gravity = 4.5f;
        [SerializeField, Range(0f, 0.5f)] private float damping = 0.08f;
        [SerializeField, Range(0.05f, 1f)] private float edgeStiffness = 0.95f;
        [Tooltip("Hard limit for each material edge. 1.06 means at most 6% extension.")]
        [SerializeField, Range(1f, 1.5f)] private float maximumEdgeStretch = 1.06f;
        [Tooltip("Moves the whole cloth with large pointer motion before solving local sag. This prevents long rubber-band stretching.")]
        [SerializeField, Range(0f, 1f)] private float grabTransport = 0.65f;
        [Tooltip("Whole-piece transport used when the grabbed vertex is on the cut-piece boundary.")]
        [SerializeField, Range(0f, 1f)] private float edgeGrabTransport = 0.06f;
        [Tooltip("Only a small part of the lift depth is transported to the whole piece, allowing it to bend out of the face plane.")]
        [SerializeField, Range(0f, 1f)] private float outOfPlaneGrabTransport = 0.12f;
        [SerializeField, Range(0f, 1f)] private float edgeOutOfPlaneGrabTransport = 0.02f;
        [Tooltip("Higher values follow the mouse more quickly; lower values feel heavier.")]
        [SerializeField, Min(0.1f)] private float grabResponse = 18f;
        [Tooltip("Maximum local-space speed of the pinned material point.")]
        [SerializeField, Min(0.1f)] private float maximumGrabSpeed = 12f;
        [Tooltip("Out-of-plane lift produced by dragging across one full screen height.")]
        [SerializeField, Min(0f)] private float liftDepthScale = 2.5f;
        [SerializeField, Min(0f)] private float maximumLiftDepth = 3f;
        [SerializeField, Range(1, 16)] private int constraintIterations = 10;
        [Tooltip("Maximum approximate edge-constraint operations per physics step. Large pieces automatically use fewer iterations.")]
        [SerializeField, Min(1000)] private int solverEdgePassBudget = 60000;
        [SerializeField] private bool continueFallingAfterRelease = true;

        [Header("BoxCollider Collision")]
        [SerializeField] private bool collideWithBoxColliders = true;
        [SerializeField] private LayerMask boxCollisionLayers = ~0;
        [SerializeField] private bool collideWithTriggers;
        [SerializeField, Min(0f)] private float collisionThickness = 0.025f;
        [SerializeField, Range(0f, 1f)] private float collisionFriction = 0.4f;

        [Header("Return To Cut Position")]
        [Tooltip("Release the grabbed point this close to its original cut position to start automatic reattachment.")]
        [SerializeField, Min(0.01f)] private float returnSnapDistance = 0.6f;
        [Tooltip("Also scales the snap distance from the cut piece size, so large pieces are easier to reattach.")]
        [SerializeField, Range(0f, 0.5f)] private float returnSnapRelativeSize = 0.2f;
        [Tooltip("Return when at least this fraction of material vertices already overlaps its original location.")]
        [SerializeField, Range(0f, 1f)] private float returnCoverageRatio = 0.15f;
        [SerializeField, Min(0.1f)] private float returnSpeed = 9f;
        [SerializeField, Min(0.0001f)] private float returnCompleteDistance = 0.004f;

        private static readonly List<DeformableImagePieceMover> ActiveMovers =
            new List<DeformableImagePieceMover>();
        private static DeformableImagePieceMover capturedMover;

        private DeformableImageMesh deformableMesh;
        private DetachedSkinBoneBinding2D boneAttachment;
        private MeshRenderer meshRenderer;
        private Plane dragPlane;
        private Vector3 pointerStartLocal;
        private Vector3 pointerTargetLocal;
        private Vector3 simulatedPointerLocal;
        private Vector3 lastSimulatedPointerLocal;
        private Vector3 dragStartScreenPosition;
        private Vector3 homeGrabPoint;
        private Vector3 homeCenter;
        private float homeReferenceSize;
        private Vector3[] homeVertices = Array.Empty<Vector3>();
        private Vector3[] positions = Array.Empty<Vector3>();
        private Vector3[] previousPositions = Array.Empty<Vector3>();
        private int[] triangles = Array.Empty<int>();
        private EdgeConstraint[] edges = Array.Empty<EdgeConstraint>();
        private int[] pinnedVertices = Array.Empty<int>();
        private Vector3[] pinnedStartPositions = Array.Empty<Vector3>();
        private float[] pinnedWeights = Array.Empty<float>();
        private bool[] boundaryVertices = Array.Empty<bool>();
        private BoxCollider[] collisionBoxes = Array.Empty<BoxCollider>();
        private bool dragging;
        private bool returning;
        private bool fallingAfterRelease;
        private bool boundaryGrabActive;
        private bool attachedToBoneTarget;
        private bool reattachedAtHome = true;
        private int boundsUpdateCounter;

        public static bool IsPointerCaptured { get { return capturedMover != null; } }
        public DeformableImageMesh DeformableMesh { get { return deformableMesh; } }
        public bool IsReattachedAtHome { get { return reattachedAtHome; } }
        public event Action<DeformableImagePieceMover, bool> ReattachmentStateChanged;

        private void Awake()
        {
            CacheComponents();
            InitializeClothState();
        }

        private void OnEnable()
        {
            CacheComponents();
            if (!ActiveMovers.Contains(this))
            {
                ActiveMovers.Add(this);
            }

            deformableMesh.MeshRebuilt -= HandleMeshRebuilt;
            deformableMesh.MeshRebuilt += HandleMeshRebuilt;
            if (positions.Length != deformableMesh.LogicalVertexCount)
            {
                InitializeClothState();
            }
        }

        private void OnDisable()
        {
            ActiveMovers.Remove(this);
            if (deformableMesh != null)
            {
                deformableMesh.MeshRebuilt -= HandleMeshRebuilt;
            }

            ReleasePointerCapture();
            SubscribeToBoneAttachment(null);
        }

        private void OnValidate()
        {
            gravity = Mathf.Max(0f, gravity);
            damping = Mathf.Clamp(damping, 0f, 0.5f);
            edgeStiffness = Mathf.Clamp(edgeStiffness, 0.05f, 1f);
            maximumEdgeStretch = Mathf.Clamp(maximumEdgeStretch, 1f, 1.5f);
            grabTransport = Mathf.Clamp01(grabTransport);
            edgeGrabTransport = Mathf.Clamp01(edgeGrabTransport);
            outOfPlaneGrabTransport = Mathf.Clamp01(outOfPlaneGrabTransport);
            edgeOutOfPlaneGrabTransport = Mathf.Clamp01(edgeOutOfPlaneGrabTransport);
            grabResponse = Mathf.Max(0.1f, grabResponse);
            maximumGrabSpeed = Mathf.Max(0.1f, maximumGrabSpeed);
            liftDepthScale = Mathf.Max(0f, liftDepthScale);
            maximumLiftDepth = Mathf.Max(0f, maximumLiftDepth);
            constraintIterations = Mathf.Clamp(constraintIterations, 1, 16);
            solverEdgePassBudget = Mathf.Max(1000, solverEdgePassBudget);
            returnSnapDistance = Mathf.Max(0.01f, returnSnapDistance);
            returnSnapRelativeSize = Mathf.Clamp(returnSnapRelativeSize, 0f, 0.5f);
            returnCoverageRatio = Mathf.Clamp01(returnCoverageRatio);
            returnSpeed = Mathf.Max(0.1f, returnSpeed);
            returnCompleteDistance = Mathf.Max(0.0001f, returnCompleteDistance);
            collisionThickness = Mathf.Max(0f, collisionThickness);
            collisionFriction = Mathf.Clamp01(collisionFriction);
        }

        public void ConfigureCloth(
            float configuredGravity,
            float configuredDamping,
            float configuredStiffness,
            float configuredMaximumStretch,
            float configuredGrabTransport,
            float configuredEdgeGrabTransport,
            float configuredOutOfPlaneTransport,
            float configuredEdgeOutOfPlaneTransport,
            float configuredGrabResponse,
            float configuredMaximumGrabSpeed,
            float configuredLiftDepthScale,
            float configuredMaximumLiftDepth,
            int configuredIterations,
            int configuredSolverBudget,
            float configuredReturnDistance,
            float configuredReturnRelativeSize,
            float configuredReturnCoverage,
            float configuredReturnSpeed)
        {
            gravity = Mathf.Max(0f, configuredGravity);
            damping = Mathf.Clamp(configuredDamping, 0f, 0.5f);
            edgeStiffness = Mathf.Clamp(configuredStiffness, 0.05f, 1f);
            maximumEdgeStretch = Mathf.Clamp(configuredMaximumStretch, 1f, 1.5f);
            grabTransport = Mathf.Clamp01(configuredGrabTransport);
            edgeGrabTransport = Mathf.Clamp01(configuredEdgeGrabTransport);
            outOfPlaneGrabTransport = Mathf.Clamp01(configuredOutOfPlaneTransport);
            edgeOutOfPlaneGrabTransport = Mathf.Clamp01(configuredEdgeOutOfPlaneTransport);
            grabResponse = Mathf.Max(0.1f, configuredGrabResponse);
            maximumGrabSpeed = Mathf.Max(0.1f, configuredMaximumGrabSpeed);
            liftDepthScale = Mathf.Max(0f, configuredLiftDepthScale);
            maximumLiftDepth = Mathf.Max(0f, configuredMaximumLiftDepth);
            constraintIterations = Mathf.Clamp(configuredIterations, 1, 16);
            solverEdgePassBudget = Mathf.Max(1000, configuredSolverBudget);
            returnSnapDistance = Mathf.Max(0.01f, configuredReturnDistance);
            returnSnapRelativeSize = Mathf.Clamp(configuredReturnRelativeSize, 0f, 0.5f);
            returnCoverageRatio = Mathf.Clamp01(configuredReturnCoverage);
            returnSpeed = Mathf.Max(0.1f, configuredReturnSpeed);
        }

        public void ConfigureBoxCollision(
            bool enabled,
            LayerMask layers,
            bool includeTriggers,
            float thickness,
            float friction,
            bool fallAfterRelease)
        {
            collideWithBoxColliders = enabled;
            boxCollisionLayers = layers;
            collideWithTriggers = includeTriggers;
            collisionThickness = Mathf.Max(0f, thickness);
            collisionFriction = Mathf.Clamp01(friction);
            continueFallingAfterRelease = fallAfterRelease;
            RefreshBoxColliders();
        }

        public void ConfigureBoneAttachment(DetachedSkinBoneBinding2D binding)
        {
            SubscribeToBoneAttachment(binding);
            RefreshHomeTargetsFromBone();
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(dragMouseButton)
                && capturedMover == null
                && FindTopmostMoverUnderPointer() == this)
            {
                TryBeginDrag();
            }

            if (dragging && capturedMover == this && Input.GetMouseButton(dragMouseButton))
            {
                UpdateDrag();
            }

            if (dragging && Input.GetMouseButtonUp(dragMouseButton))
            {
                EndDrag();
            }
        }

        private void FixedUpdate()
        {
            if (positions.Length == 0
                || positions.Length != deformableMesh.LogicalVertexCount
                || (!dragging && !returning && !fallingAfterRelease))
            {
                return;
            }

            SimulateCloth(Time.fixedDeltaTime);
        }

        private void TryBeginDrag()
        {
            Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse == null)
            {
                return;
            }

            dragPlane = new Plane(transform.forward, transform.position);
            Ray ray = cameraToUse.ScreenPointToRay(Input.mousePosition);
            if (!dragPlane.Raycast(ray, out float distance))
            {
                return;
            }

            Vector3 localPoint = transform.InverseTransformPoint(ray.GetPoint(distance));
            localPoint.z = 0f;
            if (!deformableMesh.TryClassifyGrabPoint(localPoint, hitTolerance, out _))
            {
                return;
            }

            if (positions.Length != deformableMesh.LogicalVertexCount)
            {
                InitializeClothState();
            }

            returning = false;
            fallingAfterRelease = false;
            attachedToBoneTarget = false;
            dragging = true;
            capturedMover = this;
            Array.Copy(positions, previousPositions, positions.Length);
            pointerStartLocal = localPoint;
            pointerTargetLocal = localPoint;
            simulatedPointerLocal = localPoint;
            lastSimulatedPointerLocal = localPoint;
            dragStartScreenPosition = Input.mousePosition;
            SelectPinnedVertex(localPoint);
            if (bringToFrontWhenGrabbed)
            {
                BringToFront();
            }
        }

        private void UpdateDrag()
        {
            Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            Ray ray = cameraToUse.ScreenPointToRay(Input.mousePosition);
            if (dragPlane.Raycast(ray, out float distance))
            {
                pointerTargetLocal = transform.InverseTransformPoint(ray.GetPoint(distance));
                float normalizedDragDistance = Vector2.Distance(
                    Input.mousePosition,
                    dragStartScreenPosition) / Mathf.Max(1f, Screen.height);
                float liftDepth = Mathf.Min(
                    maximumLiftDepth,
                    normalizedDragDistance * liftDepthScale);
                float cameraFacingSign = Mathf.Sign(Vector3.Dot(
                    -cameraToUse.transform.forward,
                    transform.forward));
                if (Mathf.Abs(cameraFacingSign) < 0.5f)
                {
                    cameraFacingSign = -1f;
                }

                pointerTargetLocal.z = cameraFacingSign * liftDepth;
            }
        }

        private void EndDrag()
        {
            RefreshHomeTargetsFromBone();
            if (pinnedVertices.Length > 0)
            {
                returning = ShouldReturnToHome();
            }

            fallingAfterRelease = !returning && continueFallingAfterRelease;

            ReleasePointerCapture();
        }

        [ContextMenu("Return To Cut Position")]
        public void ReturnToCutPosition()
        {
            if (homeVertices.Length == 0)
            {
                return;
            }

            ReleasePointerCapture();
            fallingAfterRelease = false;
            RefreshHomeTargetsFromBone();
            returning = true;
        }

        private void SimulateCloth(float deltaTime)
        {
            float timeStep = Mathf.Clamp(deltaTime, 0.001f, 0.033f);
            float velocityScale = Mathf.Clamp01(1f - damping);
            Vector3 localGravity = transform.InverseTransformVector(Vector3.down * gravity);

            if (dragging)
            {
                float responseBlend = 1f - Mathf.Exp(-grabResponse * timeStep);
                Vector3 responseTarget = Vector3.Lerp(
                    simulatedPointerLocal,
                    pointerTargetLocal,
                    responseBlend);
                simulatedPointerLocal = Vector3.MoveTowards(
                    simulatedPointerLocal,
                    responseTarget,
                    maximumGrabSpeed * timeStep);
                Vector3 pointerStep = simulatedPointerLocal - lastSimulatedPointerLocal;
                float planarTransport = boundaryGrabActive
                    ? edgeGrabTransport
                    : grabTransport;
                float depthTransport = boundaryGrabActive
                    ? edgeOutOfPlaneGrabTransport
                    : outOfPlaneGrabTransport;
                Vector3 transportedStep = new Vector3(
                    pointerStep.x * planarTransport,
                    pointerStep.y * planarTransport,
                    pointerStep.z * depthTransport);
                if (transportedStep.sqrMagnitude > 0f)
                {
                    for (int index = 0; index < positions.Length; index++)
                    {
                        positions[index] += transportedStep;
                        previousPositions[index] += transportedStep;
                    }
                }

                lastSimulatedPointerLocal = simulatedPointerLocal;
            }

            for (int index = 0; index < positions.Length; index++)
            {
                if (dragging && IsPinned(index))
                {
                    continue;
                }

                Vector3 current = positions[index];
                Vector3 velocity = (current - previousPositions[index]) * velocityScale;
                previousPositions[index] = current;
                positions[index] = current + velocity;
                if (!returning)
                {
                    positions[index] += localGravity * (timeStep * timeStep);
                }
            }

            if (returning)
            {
                float blend = 1f - Mathf.Exp(-returnSpeed * timeStep);
                for (int index = 0; index < positions.Length; index++)
                {
                    positions[index] = Vector3.Lerp(positions[index], homeVertices[index], blend);
                }
            }

            if (!returning)
            {
                ResolveBoxCollisions();
            }

            // During reattachment every material vertex has an explicit bone-aware
            // destination. Old cloth rest lengths would fight that new jaw shape, so
            // constraints resume only after the new attached pose becomes the rest pose.
            if (!returning)
            {
                int minimumIterations = Mathf.Min(2, constraintIterations);
                int effectiveIterations = edges.Length > 0
                    ? Mathf.Clamp(
                        solverEdgePassBudget / edges.Length,
                        minimumIterations,
                        constraintIterations)
                    : 1;
                for (int iteration = 0; iteration < effectiveIterations; iteration++)
                {
                    // Pin first, then solve. The previous order pinned after the last solve,
                    // leaving the adjacent edges stretched until the next physics frame.
                    ApplyPinnedTargets();
                    SolveEdgeConstraints();
                }
            }

            if (!returning)
            {
                ResolveBoxCollisions();
            }

            boundsUpdateCounter++;
            deformableMesh.SetLogicalVertices(positions, boundsUpdateCounter % 4 == 0);
            if (returning && MaximumHomeDistance() <= returnCompleteDistance)
            {
                Array.Copy(homeVertices, positions, positions.Length);
                Array.Copy(homeVertices, previousPositions, previousPositions.Length);
                deformableMesh.SetLogicalVertices(positions, true);
                edges = BuildEdgeConstraints(homeVertices, triangles);
                returning = false;
                fallingAfterRelease = false;
                attachedToBoneTarget = boneAttachment != null
                    && boneAttachment.IsConfigured;
                SetReattachedAtHome(true);
            }
            else if (!returning && reattachedAtHome)
            {
                float detachThreshold = Mathf.Max(
                    returnCompleteDistance * 2f,
                    homeReferenceSize * 0.015f);
                if (MaximumHomeDistance() > detachThreshold)
                {
                    SetReattachedAtHome(false);
                }
            }
        }

        private void SubscribeToBoneAttachment(DetachedSkinBoneBinding2D binding)
        {
            if (boneAttachment != null)
            {
                boneAttachment.TargetsChanged -= HandleBoneAttachmentTargetsChanged;
            }

            boneAttachment = binding;
            if (boneAttachment != null)
            {
                boneAttachment.TargetsChanged -= HandleBoneAttachmentTargetsChanged;
                boneAttachment.TargetsChanged += HandleBoneAttachmentTargetsChanged;
            }
        }

        private bool RefreshHomeTargetsFromBone()
        {
            if (boneAttachment == null
                || homeVertices.Length == 0
                || !boneAttachment.WriteCurrentTargets(homeVertices))
            {
                return false;
            }

            CalculateHomePlacementMetrics();
            if (pinnedVertices.Length > 0)
            {
                homeGrabPoint = CalculatePinnedPoint(homeVertices);
            }

            return true;
        }

        private void HandleBoneAttachmentTargetsChanged()
        {
            if (!RefreshHomeTargetsFromBone()
                || !attachedToBoneTarget
                || positions.Length != homeVertices.Length)
            {
                return;
            }

            Array.Copy(homeVertices, positions, positions.Length);
            Array.Copy(homeVertices, previousPositions, previousPositions.Length);
            edges = BuildEdgeConstraints(homeVertices, triangles);
            deformableMesh.SetLogicalVertices(positions, true);
        }

        private void SolveEdgeConstraints()
        {
            for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
            {
                EdgeConstraint edge = edges[edgeIndex];
                Vector3 delta = positions[edge.B] - positions[edge.A];
                float length = delta.magnitude;
                if (length <= 0.000001f)
                {
                    continue;
                }

                float maximumLength = edge.RestLength * maximumEdgeStretch;
                bool exceedsStretchLimit = length > maximumLength;
                float targetLength = exceedsStretchLimit ? maximumLength : edge.RestLength;
                float correctionStrength = exceedsStretchLimit ? 1f : edgeStiffness;
                Vector3 correction = delta
                    * ((length - targetLength) / length)
                    * correctionStrength;
                bool lockA = dragging && IsPinned(edge.A);
                bool lockB = dragging && IsPinned(edge.B);
                if (lockA && lockB)
                {
                    continue;
                }

                if (lockA)
                {
                    positions[edge.B] -= correction;
                }
                else if (lockB)
                {
                    positions[edge.A] += correction;
                }
                else
                {
                    positions[edge.A] += correction * 0.5f;
                    positions[edge.B] -= correction * 0.5f;
                }
            }
        }

        private void ResolveBoxCollisions()
        {
            if (!collideWithBoxColliders || collisionBoxes.Length == 0)
            {
                return;
            }

            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                if (dragging && IsPinned(vertex))
                {
                    continue;
                }

                for (int boxIndex = 0; boxIndex < collisionBoxes.Length; boxIndex++)
                {
                    BoxCollider box = collisionBoxes[boxIndex];
                    if (box == null
                        || !box.enabled
                        || !box.gameObject.activeInHierarchy
                        || (!collideWithTriggers && box.isTrigger)
                        || (boxCollisionLayers.value & (1 << box.gameObject.layer)) == 0)
                    {
                        continue;
                    }

                    ResolveVertexAgainstBox(vertex, box);
                }
            }
        }

        private void ResolveVertexAgainstBox(int vertex, BoxCollider box)
        {
            Vector3 worldCurrent = transform.TransformPoint(positions[vertex]);
            Vector3 worldPrevious = transform.TransformPoint(previousPositions[vertex]);
            Vector3 current = box.transform.InverseTransformPoint(worldCurrent);
            Vector3 previous = box.transform.InverseTransformPoint(worldPrevious);
            Vector3 halfSize = box.size * 0.5f;
            Vector3 lossyScale = box.transform.lossyScale;
            Vector3 localThickness = new Vector3(
                collisionThickness / Mathf.Max(0.0001f, Mathf.Abs(lossyScale.x)),
                collisionThickness / Mathf.Max(0.0001f, Mathf.Abs(lossyScale.y)),
                collisionThickness / Mathf.Max(0.0001f, Mathf.Abs(lossyScale.z)));
            Vector3 minimum = box.center - halfSize - localThickness;
            Vector3 maximum = box.center + halfSize + localThickness;

            bool withinHorizontal = current.x >= minimum.x && current.x <= maximum.x
                && current.z >= minimum.z && current.z <= maximum.z;
            bool collided = false;

            // Continuous top-face test prevents a fast cloth point tunnelling through
            // the box between two FixedUpdate calls.
            if (withinHorizontal && previous.y >= maximum.y && current.y < maximum.y)
            {
                current.y = maximum.y;
                collided = true;
            }
            else if (current.x >= minimum.x && current.x <= maximum.x
                && current.y >= minimum.y && current.y <= maximum.y
                && current.z >= minimum.z && current.z <= maximum.z)
            {
                float nearestDistance = current.x - minimum.x;
                int nearestFace = 0;
                ConsiderCollisionFace(maximum.x - current.x, 1, ref nearestDistance, ref nearestFace);
                ConsiderCollisionFace(current.y - minimum.y, 2, ref nearestDistance, ref nearestFace);
                ConsiderCollisionFace(maximum.y - current.y, 3, ref nearestDistance, ref nearestFace);
                ConsiderCollisionFace(current.z - minimum.z, 4, ref nearestDistance, ref nearestFace);
                ConsiderCollisionFace(maximum.z - current.z, 5, ref nearestDistance, ref nearestFace);

                switch (nearestFace)
                {
                    case 0: current.x = minimum.x; break;
                    case 1: current.x = maximum.x; break;
                    case 2: current.y = minimum.y; break;
                    case 3: current.y = maximum.y; break;
                    case 4: current.z = minimum.z; break;
                    default: current.z = maximum.z; break;
                }

                collided = true;
            }

            if (!collided)
            {
                return;
            }

            Vector3 correctedWorld = box.transform.TransformPoint(current);
            Vector3 correctedLocal = transform.InverseTransformPoint(correctedWorld);
            positions[vertex] = correctedLocal;
            previousPositions[vertex] = Vector3.Lerp(
                previousPositions[vertex],
                correctedLocal,
                collisionFriction);
        }

        private static void ConsiderCollisionFace(
            float distance,
            int face,
            ref float nearestDistance,
            ref int nearestFace)
        {
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestFace = face;
            }
        }

        [ContextMenu("Refresh BoxCollider Obstacles")]
        public void RefreshBoxColliders()
        {
            collisionBoxes = collideWithBoxColliders
                ? FindObjectsOfType<BoxCollider>()
                : Array.Empty<BoxCollider>();
        }

        private void ApplyPinnedTargets()
        {
            if (!dragging)
            {
                return;
            }

            Vector3 pointerDelta = simulatedPointerLocal - pointerStartLocal;
            for (int index = 0; index < pinnedVertices.Length; index++)
            {
                int vertex = pinnedVertices[index];
                Vector3 target = pinnedStartPositions[index] + pointerDelta;
                positions[vertex] = target;
                previousPositions[vertex] = target;
            }
        }

        private void SelectPinnedVertex(Vector3 localPoint)
        {
            int nearest = FindNearestVertex(localPoint);
            pinnedVertices = nearest >= 0 ? new[] { nearest } : Array.Empty<int>();
            pinnedWeights = nearest >= 0 ? new[] { 1f } : Array.Empty<float>();
            boundaryGrabActive = nearest >= 0
                && nearest < boundaryVertices.Length
                && boundaryVertices[nearest];

            pinnedStartPositions = new Vector3[pinnedVertices.Length];
            for (int index = 0; index < pinnedVertices.Length; index++)
            {
                pinnedStartPositions[index] = positions[pinnedVertices[index]];
            }

            homeGrabPoint = CalculatePinnedPoint(homeVertices);
        }

        private int FindNearestVertex(Vector2 point)
        {
            int nearest = -1;
            float bestDistanceSquared = float.PositiveInfinity;
            for (int index = 0; index < positions.Length; index++)
            {
                float distanceSquared = ((Vector2)positions[index] - point).sqrMagnitude;
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    nearest = index;
                }
            }

            return nearest;
        }

        private Vector3 CalculatePinnedPoint(Vector3[] source)
        {
            Vector3 point = Vector3.zero;
            float totalWeight = 0f;
            for (int index = 0; index < pinnedVertices.Length; index++)
            {
                int vertex = pinnedVertices[index];
                if (vertex < 0 || vertex >= source.Length)
                {
                    continue;
                }

                float weight = index < pinnedWeights.Length ? pinnedWeights[index] : 1f;
                point += source[vertex] * weight;
                totalWeight += weight;
            }

            return totalWeight > 0.000001f ? point / totalWeight : point;
        }

        private bool IsPinned(int vertex)
        {
            for (int index = 0; index < pinnedVertices.Length; index++)
            {
                if (pinnedVertices[index] == vertex)
                {
                    return true;
                }
            }

            return false;
        }

        private float MaximumHomeDistance()
        {
            float maximum = 0f;
            for (int index = 0; index < positions.Length; index++)
            {
                maximum = Mathf.Max(maximum, Vector3.Distance(positions[index], homeVertices[index]));
            }

            return maximum;
        }

        private bool ShouldReturnToHome()
        {
            float effectiveDistance = Mathf.Max(
                returnSnapDistance,
                homeReferenceSize * returnSnapRelativeSize);
            if (Vector2.Distance(pointerTargetLocal, homeGrabPoint) <= effectiveDistance)
            {
                return true;
            }

            if (positions.Length == 0 || positions.Length != homeVertices.Length)
            {
                return false;
            }

            Vector3 minimum = positions[0];
            Vector3 maximum = positions[0];
            for (int index = 1; index < positions.Length; index++)
            {
                minimum = Vector3.Min(minimum, positions[index]);
                maximum = Vector3.Max(maximum, positions[index]);
            }

            Vector3 currentCenter = (minimum + maximum) * 0.5f;
            if (Vector2.Distance(currentCenter, homeCenter) <= effectiveDistance * 0.8f)
            {
                return true;
            }

            if (returnCoverageRatio <= 0f)
            {
                return false;
            }

            int requiredVertices = Mathf.Max(
                1,
                Mathf.CeilToInt(positions.Length * returnCoverageRatio));
            int overlappingVertices = 0;
            float vertexDistanceSquared = effectiveDistance * effectiveDistance * 0.36f;
            for (int index = 0; index < positions.Length; index++)
            {
                if ((positions[index] - homeVertices[index]).sqrMagnitude
                    <= vertexDistanceSquared)
                {
                    overlappingVertices++;
                    if (overlappingVertices >= requiredVertices)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void CalculateHomePlacementMetrics()
        {
            if (homeVertices.Length == 0)
            {
                homeCenter = Vector3.zero;
                homeReferenceSize = 0f;
                return;
            }

            Vector3 minimum = homeVertices[0];
            Vector3 maximum = homeVertices[0];
            for (int index = 1; index < homeVertices.Length; index++)
            {
                minimum = Vector3.Min(minimum, homeVertices[index]);
                maximum = Vector3.Max(maximum, homeVertices[index]);
            }

            homeCenter = (minimum + maximum) * 0.5f;
            Vector3 size = maximum - minimum;
            homeReferenceSize = Mathf.Max(size.x, size.y);
        }

        private void InitializeClothState()
        {
            CacheComponents();
            if (deformableMesh == null || deformableMesh.LogicalVertexCount == 0)
            {
                homeVertices = Array.Empty<Vector3>();
                positions = Array.Empty<Vector3>();
                previousPositions = Array.Empty<Vector3>();
                triangles = Array.Empty<int>();
                edges = Array.Empty<EdgeConstraint>();
                return;
            }

            homeVertices = deformableMesh.CopyCurrentVertices();
            positions = deformableMesh.CopyCurrentVertices();
            previousPositions = deformableMesh.CopyCurrentVertices();
            triangles = deformableMesh.CopyLogicalTriangles();
            edges = BuildEdgeConstraints(homeVertices, triangles);
            boundaryVertices = BuildBoundaryVertexMask(homeVertices.Length, triangles);
            CalculateHomePlacementMetrics();
            RefreshBoxColliders();
            returning = false;
            fallingAfterRelease = false;
            SetReattachedAtHome(true);
            ReleasePointerCapture();
        }

        private void SetReattachedAtHome(bool value)
        {
            if (reattachedAtHome == value)
            {
                return;
            }

            reattachedAtHome = value;
            ReattachmentStateChanged?.Invoke(this, value);
        }

        private static EdgeConstraint[] BuildEdgeConstraints(Vector3[] vertices, int[] meshTriangles)
        {
            List<EdgeConstraint> result = new List<EdgeConstraint>(meshTriangles.Length);
            HashSet<ulong> visited = new HashSet<ulong>();
            for (int triangle = 0; triangle + 2 < meshTriangles.Length; triangle += 3)
            {
                AddEdge(meshTriangles[triangle], meshTriangles[triangle + 1], vertices, visited, result);
                AddEdge(meshTriangles[triangle + 1], meshTriangles[triangle + 2], vertices, visited, result);
                AddEdge(meshTriangles[triangle + 2], meshTriangles[triangle], vertices, visited, result);
            }

            return result.ToArray();
        }

        private static bool[] BuildBoundaryVertexMask(int vertexCount, int[] meshTriangles)
        {
            bool[] result = new bool[Mathf.Max(0, vertexCount)];
            Dictionary<ulong, int> edgeUseCounts = new Dictionary<ulong, int>();
            for (int triangle = 0; triangle + 2 < meshTriangles.Length; triangle += 3)
            {
                CountEdgeUse(meshTriangles[triangle], meshTriangles[triangle + 1], vertexCount, edgeUseCounts);
                CountEdgeUse(meshTriangles[triangle + 1], meshTriangles[triangle + 2], vertexCount, edgeUseCounts);
                CountEdgeUse(meshTriangles[triangle + 2], meshTriangles[triangle], vertexCount, edgeUseCounts);
            }

            foreach (KeyValuePair<ulong, int> edge in edgeUseCounts)
            {
                // An edge referenced by exactly one triangle is a real material edge:
                // either the outside of the extracted piece or one side of a cut.
                if (edge.Value != 1)
                {
                    continue;
                }

                int a = (int)(edge.Key >> 32);
                int b = (int)(edge.Key & uint.MaxValue);
                result[a] = true;
                result[b] = true;
            }

            return result;
        }

        private static void CountEdgeUse(
            int a,
            int b,
            int vertexCount,
            IDictionary<ulong, int> edgeUseCounts)
        {
            if (a < 0 || b < 0 || a >= vertexCount || b >= vertexCount || a == b)
            {
                return;
            }

            int minimum = Mathf.Min(a, b);
            int maximum = Mathf.Max(a, b);
            ulong key = ((ulong)(uint)minimum << 32) | (uint)maximum;
            edgeUseCounts.TryGetValue(key, out int useCount);
            edgeUseCounts[key] = useCount + 1;
        }

        private static void AddEdge(
            int a,
            int b,
            Vector3[] vertices,
            ISet<ulong> visited,
            ICollection<EdgeConstraint> result)
        {
            if (a < 0 || b < 0 || a >= vertices.Length || b >= vertices.Length || a == b)
            {
                return;
            }

            int minimum = Mathf.Min(a, b);
            int maximum = Mathf.Max(a, b);
            ulong key = ((ulong)(uint)minimum << 32) | (uint)maximum;
            if (!visited.Add(key))
            {
                return;
            }

            result.Add(new EdgeConstraint(
                minimum,
                maximum,
                Vector3.Distance(vertices[minimum], vertices[maximum])));
        }

        private void HandleMeshRebuilt()
        {
            InitializeClothState();
        }

        private void ReleasePointerCapture()
        {
            if (capturedMover == this)
            {
                capturedMover = null;
            }

            dragging = false;
            pinnedVertices = Array.Empty<int>();
            pinnedStartPositions = Array.Empty<Vector3>();
            pinnedWeights = Array.Empty<float>();
            boundaryGrabActive = false;
        }

        private void CacheComponents()
        {
            if (deformableMesh == null)
            {
                deformableMesh = GetComponent<DeformableImageMesh>();
            }

            if (meshRenderer == null)
            {
                meshRenderer = GetComponent<MeshRenderer>();
            }

            if (boneAttachment == null)
            {
                DetachedSkinBoneBinding2D existingBinding =
                    GetComponent<DetachedSkinBoneBinding2D>();
                if (existingBinding != null)
                {
                    SubscribeToBoneAttachment(existingBinding);
                }
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }
        }

        private bool IsPointerOverPiece()
        {
            Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
            if (cameraToUse == null || deformableMesh == null)
            {
                return false;
            }

            Plane plane = new Plane(transform.forward, transform.position);
            Ray ray = cameraToUse.ScreenPointToRay(Input.mousePosition);
            if (!plane.Raycast(ray, out float distance))
            {
                return false;
            }

            Vector3 localPoint = transform.InverseTransformPoint(ray.GetPoint(distance));
            localPoint.z = 0f;
            return deformableMesh.TryClassifyGrabPoint(localPoint, hitTolerance, out _);
        }

        private static DeformableImagePieceMover FindTopmostMoverUnderPointer()
        {
            DeformableImagePieceMover best = null;
            int bestLayerValue = int.MinValue;
            int bestOrder = int.MinValue;
            for (int index = 0; index < ActiveMovers.Count; index++)
            {
                DeformableImagePieceMover candidate = ActiveMovers[index];
                if (candidate == null
                    || !candidate.isActiveAndEnabled
                    || !candidate.IsPointerOverPiece())
                {
                    continue;
                }

                candidate.CacheComponents();
                int layerValue = candidate.meshRenderer != null
                    ? SortingLayer.GetLayerValueFromID(candidate.meshRenderer.sortingLayerID)
                    : 0;
                int order = candidate.meshRenderer != null
                    ? candidate.meshRenderer.sortingOrder
                    : 0;
                if (best == null
                    || layerValue > bestLayerValue
                    || layerValue == bestLayerValue && order >= bestOrder)
                {
                    best = candidate;
                    bestLayerValue = layerValue;
                    bestOrder = order;
                }
            }

            return best;
        }

        private void BringToFront()
        {
            CacheComponents();
            if (meshRenderer == null)
            {
                return;
            }

            int maximumOrder = meshRenderer.sortingOrder;
            for (int index = 0; index < ActiveMovers.Count; index++)
            {
                MeshRenderer otherRenderer = ActiveMovers[index] != null
                    ? ActiveMovers[index].GetComponent<MeshRenderer>()
                    : null;
                if (otherRenderer != null
                    && otherRenderer.sortingLayerID == meshRenderer.sortingLayerID)
                {
                    maximumOrder = Mathf.Max(maximumOrder, otherRenderer.sortingOrder);
                }
            }

            meshRenderer.sortingOrder = maximumOrder < short.MaxValue
                ? maximumOrder + 1
                : maximumOrder;
        }

        private readonly struct EdgeConstraint
        {
            public readonly int A;
            public readonly int B;
            public readonly float RestLength;

            public EdgeConstraint(int a, int b, float restLength)
            {
                A = a;
                B = b;
                RestLength = restLength;
            }
        }
    }
}
