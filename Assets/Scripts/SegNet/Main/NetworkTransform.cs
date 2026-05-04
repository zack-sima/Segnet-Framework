using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Replicates transform position, rotation, and/or scale.
    ///
    /// ServerToClients:
    ///   - Server observes local transform changes and replicates them to clients.
    ///
    /// LocalClientToServer:
    ///   - Owner client moves locally first, then sends snapshots to the server.
    ///   - Server optionally validates movement speed and broadcasts authoritative state back.
    ///   - Client reconciles toward received authority updates.
    /// </summary>
    public class NetworkTransform : NetworkBehaviour {
        private enum SyncDirection {
            LocalClientToServer = 0,
            ServerToClients = 1,
        }

        private enum InterpolationMethod {
            None = 0,
            Linear = 1,
            Smooth = 2,
        }

        private const uint SyncTransformRpcId = 0x4E54; // "NT"
        private const byte PositionMask = 0x01;
        private const byte RotationMask = 0x02;
        private const byte ScaleMask = 0x04;
        private const float DefaultInterpolationSeconds = 0.05f;

        [Header("Direction")]
        [SerializeField] private SyncDirection syncDirection = SyncDirection.ServerToClients;

        [Header("Sync Toggles")]
        [SerializeField] private bool syncPosition = true;
        [SerializeField] private bool syncRotation = true;
        [SerializeField] private bool syncScale = false;

        [Header("Send")]
        [Tooltip("Minimum time between transform sends. 0 sends as often as changes are detected.")]
        [SerializeField] private float sendRateLimitMs = 0f;

        [Header("Client Interpolation")]
        [SerializeField] private InterpolationMethod interpolationMethod = InterpolationMethod.Linear;

        [Header("Local Client To Server")]
        [Tooltip("Maximum allowed client position velocity on the server. 0 disables the check.")]
        [Range(0f, 100f)]
        [SerializeField] private float maxServerVelocity = 0f;
        [Tooltip("When validating owner movement, allow up to this much movement time before rubber-banding.")]
        [SerializeField] private float maxRubberBandToleranceMs = 1000f;

        private Vector3 _observedPosition;
        private Quaternion _observedRotation;
        private Vector3 _observedScale;

        private bool _hasPendingSend;
        private bool _hasPendingServerBroadcast;
        private float _lastSendAt = float.NegativeInfinity;
        private float _lastServerBroadcastAt = float.NegativeInfinity;
        private float _lastServerAcceptedAt;

        private bool _hasInterpolationTarget;
        private byte _interpolationMask;
        private float _interpolationStartAt;
        private float _interpolationDuration = DefaultInterpolationSeconds;
        private float _lastReceiveAt;
        private Vector3 _interpolationStartPosition;
        private Quaternion _interpolationStartRotation;
        private Vector3 _interpolationStartScale;
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private Vector3 _targetScale;
        private Vector3 _positionSmoothVelocity;
        private Vector3 _scaleSmoothVelocity;
        private bool _hasAuthoritativeState;
        private Vector3 _authoritativePosition;
        private Quaternion _authoritativeRotation;
        private Vector3 _authoritativeScale;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterRpcHandler() {
            RpcRegistry.Register(SyncTransformRpcId, DispatchSyncTransformRpc);
        }

        private void OnValidate() {
            sendRateLimitMs = Mathf.Max(0f, sendRateLimitMs);
            maxServerVelocity = Mathf.Max(0f, maxServerVelocity);
            maxRubberBandToleranceMs = Mathf.Max(0f, maxRubberBandToleranceMs);
        }

        public override void OnNetworkSpawn() {
            SnapshotObservedState();
            ResetSendState();
            ResetInterpolationState();
            _lastReceiveAt = 0f;
            _lastServerAcceptedAt = Time.realtimeSinceStartup;
            CacheAuthoritativeStateFromCurrentTransform();
        }

        private void Update() {
            if (!IsSpawned || !_hasInterpolationTarget)
                return;

            if (syncDirection == SyncDirection.LocalClientToServer && IsOwner)
                return;

            if (!IsClient && !ShouldHostVisualInterpolateRemoteOwner())
                return;

            ApplyInterpolation();
        }

        private void LateUpdate() {
            if (!IsSpawned)
                return;

            if (syncDirection == SyncDirection.ServerToClients) {
                if (IsServer)
                    DetectAndFlushChanges(sendToServer: false);
                return;
            }

            if (IsOwner)
                DetectAndFlushChanges(sendToServer: true);

            if (IsServer) {
                if (!ShouldHostVisualInterpolateRemoteOwner() && DetectLocalChanges())
                    _hasPendingServerBroadcast = true;

                FlushPendingServerBroadcast();
            }
        }

        private void DetectAndFlushChanges(bool sendToServer) {
            if (DetectLocalChanges())
                _hasPendingSend = true;

            if (!_hasPendingSend || !CanSendNow())
                return;

            if (sendToServer)
                SendTransformToServer();
            else
                SetDirty();

            _hasPendingSend = false;
            _lastSendAt = Time.realtimeSinceStartup;
        }

        private bool DetectLocalChanges() {
            bool changed = false;

            if (syncPosition && transform.position != _observedPosition) {
                _observedPosition = transform.position;
                changed = true;
            }
            if (syncRotation && transform.rotation != _observedRotation) {
                _observedRotation = transform.rotation;
                changed = true;
            }
            if (syncScale && transform.localScale != _observedScale) {
                _observedScale = transform.localScale;
                changed = true;
            }

            return changed;
        }

        private bool CanSendNow() {
            if (sendRateLimitMs <= 0f)
                return true;

            return Time.realtimeSinceStartup - _lastSendAt >= sendRateLimitMs / 1000f;
        }

        private bool CanBroadcastNow() {
            if (sendRateLimitMs <= 0f)
                return true;

            return Time.realtimeSinceStartup - _lastServerBroadcastAt >= sendRateLimitMs / 1000f;
        }

        private void FlushPendingServerBroadcast() {
            if (!_hasPendingServerBroadcast || !CanBroadcastNow())
                return;

            SetDirty();
            _hasPendingServerBroadcast = false;
            _lastServerBroadcastAt = Time.realtimeSinceStartup;
        }

        private void SendTransformToServer() {
            var writer = NetworkWriter.Get();
            WriteTransformPayload(writer);

            if (IsHost) {
                ApplyClientTransformPayload(new NetworkReader(writer.ToArraySegment()));
                NetworkWriter.Return(writer);
                return;
            }

            SendRpcInternal(
                SyncTransformRpcId,
                RpcDirection.LocalClientToServer,
                ChannelType.Unreliable,
                writer);
            NetworkWriter.Return(writer);
        }

        private static void DispatchSyncTransformRpc(NetworkBehaviour target, NetworkReader reader) {
            var networkTransform = target as NetworkTransform;
            if (networkTransform == null) {
                Debug.LogWarning("[NetworkTransform] RPC target is not a NetworkTransform.");
                return;
            }

            networkTransform.ApplyClientTransformPayload(reader);
        }

        public override void OnSerialize(NetworkWriter writer, bool initialState) {
            WriteTransformPayload(writer, useAuthoritativeState: syncDirection == SyncDirection.LocalClientToServer &&
                IsServer && _hasAuthoritativeState);
        }

        public override void OnDeserialize(NetworkReader reader, bool initialState) {
            ReadTransformPayload(reader, out byte mask, out Vector3 position,
                out Quaternion rotation, out Vector3 scale);

            if (!initialState &&
                syncDirection == SyncDirection.LocalClientToServer &&
                IsOwner) {
                if (ShouldRubberBandOwner(mask, position)) {
                    ApplyImmediate(mask, position, rotation, scale);
                    ResetInterpolationState();
                }
                return;
            }

            if (initialState || interpolationMethod == InterpolationMethod.None) {
                ApplyImmediate(mask, position, rotation, scale);
                ResetInterpolationState();
                return;
            }

            BeginInterpolation(mask, position, rotation, scale);
        }

        private bool ShouldRubberBandOwner(byte mask, Vector3 authoritativePosition) {
            if ((mask & PositionMask) == 0)
                return false;

            if (maxServerVelocity <= 0f)
                return false;

            float toleranceSeconds = Mathf.Max(0f, maxRubberBandToleranceMs) / 1000f;
            float maxDistance = maxServerVelocity * toleranceSeconds;
            return Vector3.Distance(transform.position, authoritativePosition) > maxDistance + 0.001f;
        }

        private void WriteTransformPayload(NetworkWriter writer, bool useAuthoritativeState = false) {
            byte mask = 0;
            if (syncPosition) mask |= PositionMask;
            if (syncRotation) mask |= RotationMask;
            if (syncScale) mask |= ScaleMask;
            writer.WriteByte(mask);

            Vector3 position = useAuthoritativeState ? _authoritativePosition : transform.position;
            Quaternion rotation = useAuthoritativeState ? _authoritativeRotation : transform.rotation;
            Vector3 scale = useAuthoritativeState ? _authoritativeScale : transform.localScale;

            if ((mask & PositionMask) != 0)
                writer.WriteVector3(position);
            if ((mask & RotationMask) != 0)
                writer.WriteQuaternion(rotation);
            if ((mask & ScaleMask) != 0)
                writer.WriteVector3(scale);
        }

        private void ReadTransformPayload(NetworkReader reader, out byte mask, out Vector3 position,
            out Quaternion rotation, out Vector3 scale) {
            mask = reader.ReadByte();
            position = transform.position;
            rotation = transform.rotation;
            scale = transform.localScale;

            if ((mask & PositionMask) != 0)
                position = reader.ReadVector3();
            if ((mask & RotationMask) != 0)
                rotation = reader.ReadQuaternion();
            if ((mask & ScaleMask) != 0)
                scale = reader.ReadVector3();
        }

        private void ApplyClientTransformPayload(NetworkReader reader) {
            ReadTransformPayload(reader, out byte mask, out Vector3 position,
                out Quaternion rotation, out Vector3 scale);

            ApplyClientTransformPayload(mask, position, rotation, scale);
        }

        private void ApplyClientTransformPayload(byte mask, Vector3 position,
            Quaternion rotation, Vector3 scale) {
            byte appliedMask = mask;

            if ((mask & PositionMask) != 0 && !AcceptIncomingClientPosition(position))
                appliedMask = (byte)(appliedMask & ~PositionMask);

            if (appliedMask != 0) {
                CacheAuthoritativeState(appliedMask, position, rotation, scale);

                if (ShouldHostVisualInterpolateRemoteOwner())
                    BeginInterpolation(appliedMask, position, rotation, scale);
                else
                    ApplyImmediate(appliedMask, position, rotation, scale);
            }

            if (IsServer)
                _hasPendingServerBroadcast = true;
        }

        private bool AcceptIncomingClientPosition(Vector3 incomingPosition) {
            if (maxServerVelocity <= 0f)
                return true;

            float toleranceSeconds = maxRubberBandToleranceMs > 0f
                ? maxRubberBandToleranceMs / 1000f
                : Mathf.Max(Time.realtimeSinceStartup - _lastServerAcceptedAt, 0f);
            float maxDistance = maxServerVelocity * toleranceSeconds;

            if (Vector3.Distance(transform.position, incomingPosition) > maxDistance + 0.001f)
                return false;

            _lastServerAcceptedAt = Time.realtimeSinceStartup;
            return true;
        }

        private void BeginInterpolation(byte mask, Vector3 position,
            Quaternion rotation, Vector3 scale) {
            _hasInterpolationTarget = true;
            _interpolationMask = mask;
            _interpolationStartAt = Time.realtimeSinceStartup;
            _interpolationDuration = GetInterpolationDuration();
            _lastReceiveAt = _interpolationStartAt;

            _interpolationStartPosition = transform.position;
            _interpolationStartRotation = transform.rotation;
            _interpolationStartScale = transform.localScale;
            _targetPosition = position;
            _targetRotation = rotation;
            _targetScale = scale;
        }

        private float GetInterpolationDuration() {
            if (sendRateLimitMs > 0f)
                return Mathf.Max(sendRateLimitMs / 1000f, 0.01f);

            if (_lastReceiveAt > 0f) {
                float observedInterval = Time.realtimeSinceStartup - _lastReceiveAt;
                if (observedInterval > 0f)
                    return Mathf.Max(observedInterval, 0.01f);
            }

            return DefaultInterpolationSeconds;
        }

        private void ApplyInterpolation() {
            switch (interpolationMethod) {
                case InterpolationMethod.Linear:
                    ApplyLinearInterpolation();
                    break;
                case InterpolationMethod.Smooth:
                    ApplySmoothInterpolation();
                    break;
                default:
                    ApplyImmediate(_interpolationMask, _targetPosition, _targetRotation, _targetScale);
                    ResetInterpolationState();
                    break;
            }
        }

        private void ApplyLinearInterpolation() {
            float elapsed = Time.realtimeSinceStartup - _interpolationStartAt;
            float t = _interpolationDuration <= 0f
                ? 1f
                : Mathf.Clamp01(elapsed / _interpolationDuration);

            if ((_interpolationMask & PositionMask) != 0)
                transform.position = Vector3.Lerp(_interpolationStartPosition, _targetPosition, t);
            if ((_interpolationMask & RotationMask) != 0)
                transform.rotation = Quaternion.Slerp(_interpolationStartRotation, _targetRotation, t);
            if ((_interpolationMask & ScaleMask) != 0)
                transform.localScale = Vector3.Lerp(_interpolationStartScale, _targetScale, t);

            SnapshotObservedState();

            if (t >= 1f) {
                ApplyImmediate(_interpolationMask, _targetPosition, _targetRotation, _targetScale);
                ResetInterpolationState();
            }
        }

        private void ApplySmoothInterpolation() {
            float smoothTime = Mathf.Max(_interpolationDuration, 0.01f);
            float rotationLerp = 1f - Mathf.Exp(-Time.deltaTime / smoothTime);
            bool done = true;

            if ((_interpolationMask & PositionMask) != 0) {
                transform.position = Vector3.SmoothDamp(transform.position, _targetPosition,
                    ref _positionSmoothVelocity, smoothTime);
                if ((transform.position - _targetPosition).sqrMagnitude > 0.0001f)
                    done = false;
            }

            if ((_interpolationMask & RotationMask) != 0) {
                transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, rotationLerp);
                if (Quaternion.Angle(transform.rotation, _targetRotation) > 0.1f)
                    done = false;
            }

            if ((_interpolationMask & ScaleMask) != 0) {
                transform.localScale = Vector3.SmoothDamp(transform.localScale, _targetScale,
                    ref _scaleSmoothVelocity, smoothTime);
                if ((transform.localScale - _targetScale).sqrMagnitude > 0.0001f)
                    done = false;
            }

            SnapshotObservedState();

            if (done) {
                ApplyImmediate(_interpolationMask, _targetPosition, _targetRotation, _targetScale);
                ResetInterpolationState();
            }
        }

        private void ApplyImmediate(byte mask, Vector3 position, Quaternion rotation, Vector3 scale) {
            if ((mask & PositionMask) != 0)
                transform.position = position;
            if ((mask & RotationMask) != 0)
                transform.rotation = rotation;
            if ((mask & ScaleMask) != 0)
                transform.localScale = scale;

            SnapshotObservedState();
        }

        private void SnapshotObservedState() {
            _observedPosition = transform.position;
            _observedRotation = transform.rotation;
            _observedScale = transform.localScale;
        }

        private bool ShouldHostVisualInterpolateRemoteOwner() {
            return syncDirection == SyncDirection.LocalClientToServer &&
                IsHost &&
                HasOwner &&
                !IsOwner;
        }

        private void CacheAuthoritativeState(byte mask, Vector3 position,
            Quaternion rotation, Vector3 scale) {
            _hasAuthoritativeState = true;

            if ((mask & PositionMask) != 0)
                _authoritativePosition = position;
            else
                _authoritativePosition = transform.position;

            if ((mask & RotationMask) != 0)
                _authoritativeRotation = rotation;
            else
                _authoritativeRotation = transform.rotation;

            if ((mask & ScaleMask) != 0)
                _authoritativeScale = scale;
            else
                _authoritativeScale = transform.localScale;
        }

        private void CacheAuthoritativeStateFromCurrentTransform() {
            _hasAuthoritativeState = true;
            _authoritativePosition = transform.position;
            _authoritativeRotation = transform.rotation;
            _authoritativeScale = transform.localScale;
        }

        private void ResetSendState() {
            _hasPendingSend = false;
            _hasPendingServerBroadcast = false;
            _lastSendAt = float.NegativeInfinity;
            _lastServerBroadcastAt = float.NegativeInfinity;
        }

        private void ResetInterpolationState() {
            _hasInterpolationTarget = false;
            _interpolationMask = 0;
            _positionSmoothVelocity = Vector3.zero;
            _scaleSmoothVelocity = Vector3.zero;
        }
    }
}
