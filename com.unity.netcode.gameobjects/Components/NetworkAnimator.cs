#if COM_UNITY_MODULES_ANIMATION
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.Netcode.Components
{
    /// <summary>
    /// NetworkAnimator enables remote synchronization of <see cref="UnityEngine.Animator"/> state for on network objects.
    /// </summary>
    [AddComponentMenu("Netcode/" + nameof(NetworkAnimator))]
    [RequireComponent(typeof(Animator))]
    public class NetworkAnimator : NetworkBehaviour
    {
        internal struct AnimationMessage : INetworkSerializable
        {
            // state hash per layer.  if non-zero, then Play() this animation, skipping transitions
            internal int StateHash;
            internal float NormalizedTime;
            internal int Layer;
            internal float Weight;
            internal byte[] Parameters;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref StateHash);
                serializer.SerializeValue(ref NormalizedTime);
                serializer.SerializeValue(ref Layer);
                serializer.SerializeValue(ref Weight);
                serializer.SerializeValue(ref Parameters);
            }
        }

        internal struct AnimationTriggerMessage : INetworkSerializable
        {
            internal int Hash;
            internal bool Reset;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Hash);
                serializer.SerializeValue(ref Reset);
            }
        }

        [Tooltip("When true the owner drives the animations and when false the server drives the animations.")]
        public bool OwnerAuthoritative;
        [SerializeField] private Animator m_Animator;

        public Animator Animator
        {
            get { return m_Animator; }
            set
            {
                m_Animator = value;
            }
        }

        private bool m_SendMessagesAllowed = false;

        // Animators only support up to 32 params
        private const int k_MaxAnimationParams = 32;

        private int[] m_TransitionHash;
        private int[] m_AnimationHash;
        private float[] m_LayerWeights;

        private unsafe struct AnimatorParamCache
        {
            internal int Hash;
            internal int Type;
            internal fixed byte Value[4]; // this is a max size of 4 bytes
        }

        // 128 bytes per Animator
        private FastBufferWriter m_ParameterWriter = new FastBufferWriter(k_MaxAnimationParams * sizeof(float), Allocator.Persistent);

        private NativeArray<AnimatorParamCache> m_CachedAnimatorParameters;

        // We cache these values because UnsafeUtility.EnumToInt uses direct IL that allows a non-boxing conversion
        private struct AnimationParamEnumWrapper
        {
            internal static readonly int AnimatorControllerParameterInt;
            internal static readonly int AnimatorControllerParameterFloat;
            internal static readonly int AnimatorControllerParameterBool;

            static AnimationParamEnumWrapper()
            {
                AnimatorControllerParameterInt = UnsafeUtility.EnumToInt(AnimatorControllerParameterType.Int);
                AnimatorControllerParameterFloat = UnsafeUtility.EnumToInt(AnimatorControllerParameterType.Float);
                AnimatorControllerParameterBool = UnsafeUtility.EnumToInt(AnimatorControllerParameterType.Bool);
            }
        }

        private void CleanUp()
        {
            if (IsServer)
            {
                NetworkManager.OnClientConnectedCallback -= NetworkManager_OnClientConnectedCallback;
            }
            m_SendMessagesAllowed = false;
            if (m_CachedAnimatorParameters != null && m_CachedAnimatorParameters.IsCreated)
            {
                m_CachedAnimatorParameters.Dispose();
            }
            if (m_ParameterWriter.IsInitialized)
            {
                m_ParameterWriter.Dispose();
            }
        }

        public override void OnDestroy()
        {
            CleanUp();
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            if (OwnerAuthoritative && IsOwner || IsServer)
            {
                m_SendMessagesAllowed = true;
                int layers = m_Animator.layerCount;

                m_TransitionHash = new int[layers];
                m_AnimationHash = new int[layers];
                m_LayerWeights = new float[layers];
            }
            if (IsServer)
            {
                NetworkManager.OnClientConnectedCallback += NetworkManager_OnClientConnectedCallback;
            }

            var parameters = m_Animator.parameters;
            m_CachedAnimatorParameters = new NativeArray<AnimatorParamCache>(parameters.Length, Allocator.Persistent);

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];

                if (m_Animator.IsParameterControlledByCurve(parameter.nameHash))
                {
                    // we are ignoring parameters that are controlled by animation curves - syncing the layer
                    //  states indirectly syncs the values that are driven by the animation curves
                    continue;
                }

                var cacheParam = new AnimatorParamCache
                {
                    Type = UnsafeUtility.EnumToInt(parameter.type),
                    Hash = parameter.nameHash
                };

                unsafe
                {
                    switch (parameter.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            var value = m_Animator.GetFloat(cacheParam.Hash);
                            UnsafeUtility.WriteArrayElement(cacheParam.Value, 0, value);
                            break;
                        case AnimatorControllerParameterType.Int:
                            var valueInt = m_Animator.GetInteger(cacheParam.Hash);
                            UnsafeUtility.WriteArrayElement(cacheParam.Value, 0, valueInt);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            var valueBool = m_Animator.GetBool(cacheParam.Hash);
                            UnsafeUtility.WriteArrayElement(cacheParam.Value, 0, valueBool);
                            break;
                        case AnimatorControllerParameterType.Trigger:
                        default:
                            break;
                    }
                }

                m_CachedAnimatorParameters[i] = cacheParam;
            }
        }

        private void NetworkManager_OnClientConnectedCallback(ulong playerId)
        {
            ServerUpdateNewPlayer(playerId);
        }

        public override void OnNetworkDespawn()
        {
            CleanUp();
        }
        private static byte[] s_EmptyArray = new byte[] { };
        private void ServerUpdateNewPlayer(ulong playerId)
        {
            if (!IsServer)
            {
                Debug.LogError("Only the server can update newly joining players!");
                return;
            }

            if (playerId == NetworkManager.LocalClientId)
            {
                return;
            }

            var clientRpcParams = new ClientRpcParams();
            clientRpcParams.Send = new ClientRpcSendParams();
            clientRpcParams.Send.TargetClientIds = new List<ulong>() { playerId };

            for (int layer = 0; layer < m_Animator.layerCount; layer++)
            {
                AnimatorStateInfo st = m_Animator.GetCurrentAnimatorStateInfo(layer);
                var animMsg = new AnimationMessage
                {
                    StateHash = st.fullPathHash,
                    NormalizedTime = st.normalizedTime,
                    Layer = layer,
                    Weight = m_LayerWeights[layer],
                    Parameters = s_EmptyArray
                };

                // We only need to send the parameters once
                if (layer == 0)
                {
                    m_ParameterWriter.Seek(0);
                    m_ParameterWriter.Truncate();

                    WriteParameters(m_ParameterWriter);
                    animMsg.Parameters = m_ParameterWriter.ToArray();
                }
                // Server always send via client RPC
                SendAnimStateClientRpc(animMsg, clientRpcParams);
            }
        }

        private void FixedUpdate()
        {
            if (!m_SendMessagesAllowed || !m_Animator || !m_Animator.enabled)
            {
                return;
            }

            for (int layer = 0; layer < m_Animator.layerCount; layer++)
            {
                int stateHash;
                float normalizedTime;
                if (!CheckAnimStateChanged(out stateHash, out normalizedTime, layer))
                {
                    continue;
                }

                var animMsg = new AnimationMessage
                {
                    StateHash = stateHash,
                    NormalizedTime = normalizedTime,
                    Layer = layer,
                    Weight = m_LayerWeights[layer],
                    Parameters = s_EmptyArray
                };
                // We only need to send the parameters once
                if (layer == 0)
                {
                    m_ParameterWriter.Seek(0);
                    m_ParameterWriter.Truncate();

                    WriteParameters(m_ParameterWriter);
                    animMsg.Parameters = m_ParameterWriter.ToArray();
                }

                if (!IsServer && OwnerAuthoritative)
                {
                    SendAnimStateServerRpc(animMsg);
                }
                else
                {
                    SendAnimStateClientRpc(animMsg);
                }
            }
        }

        unsafe private bool CheckAnimStateChanged(out int stateHash, out float normalizedTime, int layer)
        {
            bool shouldUpdate = false;
            stateHash = 0;
            normalizedTime = 0;

            float layerWeightNow = m_Animator.GetLayerWeight(layer);

            if (!Mathf.Approximately(layerWeightNow, m_LayerWeights[layer]))
            {
                m_LayerWeights[layer] = layerWeightNow;
                shouldUpdate = true;
            }
            if (m_Animator.IsInTransition(layer))
            {
                AnimatorTransitionInfo tt = m_Animator.GetAnimatorTransitionInfo(layer);
                if (tt.fullPathHash != m_TransitionHash[layer])
                {
                    // first time in this transition for this layer
                    m_TransitionHash[layer] = tt.fullPathHash;
                    m_AnimationHash[layer] = 0;
                    shouldUpdate = true;
                }
            }
            else
            {
                AnimatorStateInfo st = m_Animator.GetCurrentAnimatorStateInfo(layer);
                if (st.fullPathHash != m_AnimationHash[layer])
                {
                    // first time in this animation state
                    if (m_AnimationHash[layer] != 0)
                    {
                        // came from another animation directly - from Play()
                        stateHash = st.fullPathHash;
                        normalizedTime = st.normalizedTime;
                    }
                    m_TransitionHash[layer] = 0;
                    m_AnimationHash[layer] = st.fullPathHash;
                    shouldUpdate = true;
                }
            }

            // Whether the layer has changed or not, we always check to see if parameters have changed and send updates if they have.
            for (int i = 0; i < m_CachedAnimatorParameters.Length; i++)
            {
                ref var cacheValue = ref UnsafeUtility.ArrayElementAsRef<AnimatorParamCache>(m_CachedAnimatorParameters.GetUnsafePtr(), i);
                var hash = cacheValue.Hash;
                if (cacheValue.Type == AnimationParamEnumWrapper.AnimatorControllerParameterInt)
                {
                    var valueInt = m_Animator.GetInteger(hash);
                    var currentValue = valueInt;
                    fixed (void* value = cacheValue.Value)
                    {
                        currentValue = UnsafeUtility.ReadArrayElement<int>(value, 0);
                    }
                    if (currentValue != valueInt)
                    {
                        shouldUpdate = true;
                        break;
                    }
                }
                else if (cacheValue.Type == AnimationParamEnumWrapper.AnimatorControllerParameterBool)
                {
                    var valueBool = m_Animator.GetBool(hash);
                    var currentValue = valueBool;
                    fixed (void* value = cacheValue.Value)
                    {
                        currentValue = UnsafeUtility.ReadArrayElement<bool>(value, 0);
                    }
                    if (currentValue != valueBool)
                    {
                        shouldUpdate = true;
                        break;
                    }
                }
                else if (cacheValue.Type == AnimationParamEnumWrapper.AnimatorControllerParameterFloat)
                {
                    var valueFloat = m_Animator.GetFloat(hash);
                    var currentValue = valueFloat;
                    fixed (void* value = cacheValue.Value)
                    {
                        currentValue = UnsafeUtility.ReadArrayElement<float>(value, 0);
                    }
                    if (currentValue != valueFloat)
                    {
                        shouldUpdate = true;
                        break;
                    }
                }
            }

            return shouldUpdate;
        }

        /* $AS TODO: Right now we are not checking for changed values this is because
        the read side of this function doesn't have similar logic which would cause
        an overflow read because it doesn't know if the value is there or not. So
        there needs to be logic to track which indexes changed in order for there
        to be proper value change checking. Will revist in 1.1.0.
        */
        private unsafe void WriteParameters(FastBufferWriter writer)
        {
            for (int i = 0; i < m_CachedAnimatorParameters.Length; i++)
            {
                ref var cacheValue = ref UnsafeUtility.ArrayElementAsRef<AnimatorParamCache>(m_CachedAnimatorParameters.GetUnsafePtr(), i);
                var hash = cacheValue.Hash;

                if (cacheValue.Type == AnimationParamEnumWrapper.AnimatorControllerParameterInt)
                {
                    var valueInt = m_Animator.GetInteger(hash);
                    fixed (void* value = cacheValue.Value)
                    {
                        UnsafeUtility.WriteArrayElement(value, 0, valueInt);
                        BytePacker.WriteValuePacked(writer, (uint)valueInt);
                    }
                }
                else if (cacheValue.Type == AnimationParamEnumWrapper.AnimatorControllerParameterBool)
                {
                    var valueBool = m_Animator.GetBool(hash);
                    fixed (void* value = cacheValue.Value)
                    {
                        UnsafeUtility.WriteArrayElement(value, 0, valueBool);
                        writer.WriteValueSafe(valueBool);
                    }
                }
                else if (cacheValue.Type == AnimationParamEnumWrapper.AnimatorControllerParameterFloat)
                {
                    var valueFloat = m_Animator.GetFloat(hash);
                    fixed (void* value = cacheValue.Value)
                    {
                        UnsafeUtility.WriteArrayElement(value, 0, valueFloat);
                        writer.WriteValueSafe(valueFloat);
                    }
                }
            }
        }

        private unsafe void ReadParameters(FastBufferReader reader)
        {
            for (int i = 0; i < m_CachedAnimatorParameters.Length; i++)
            {
                ref var cacheValue = ref UnsafeUtility.ArrayElementAsRef<AnimatorParamCache>(m_CachedAnimatorParameters.GetUnsafePtr(), i);
                var hash = cacheValue.Hash;

                if (cacheValue.Type == AnimationParamEnumWrapper.AnimatorControllerParameterInt)
                {
                    ByteUnpacker.ReadValuePacked(reader, out uint newValue);
                    m_Animator.SetInteger(hash, (int)newValue);
                    fixed (void* value = cacheValue.Value)
                    {
                        UnsafeUtility.WriteArrayElement(value, 0, newValue);
                    }
                }
                else if (cacheValue.Type == AnimationParamEnumWrapper.AnimatorControllerParameterBool)
                {
                    reader.ReadValueSafe(out bool newBoolValue);
                    m_Animator.SetBool(hash, newBoolValue);
                    fixed (void* value = cacheValue.Value)
                    {
                        UnsafeUtility.WriteArrayElement(value, 0, newBoolValue);
                    }
                }
                else if (cacheValue.Type == AnimationParamEnumWrapper.AnimatorControllerParameterFloat)
                {
                    reader.ReadValueSafe(out float newFloatValue);
                    m_Animator.SetFloat(hash, newFloatValue);
                    fixed (void* value = cacheValue.Value)
                    {
                        UnsafeUtility.WriteArrayElement(value, 0, newFloatValue);
                    }
                }
            }
        }

        private unsafe void UpdateAnimationState(AnimationMessage animSnapshot)
        {
            if (animSnapshot.StateHash != 0)
            {
                m_Animator.Play(animSnapshot.StateHash, animSnapshot.Layer, animSnapshot.NormalizedTime);
            }
            m_Animator.SetLayerWeight(animSnapshot.Layer, animSnapshot.Weight);

            if (animSnapshot.Parameters != null && animSnapshot.Parameters.Length != 0)
            {
                // We use a fixed value here to avoid the copy of data from the byte buffer since we own the data
                fixed (byte* parameters = animSnapshot.Parameters)
                {
                    var reader = new FastBufferReader(parameters, Allocator.None, animSnapshot.Parameters.Length);
                    ReadParameters(reader);
                }
            }
        }

        [ServerRpc]
        private unsafe void SendAnimStateServerRpc(AnimationMessage animSnapshot, ServerRpcParams serverRpcParams = default)
        {
            if (OwnerAuthoritative && OwnerClientId == serverRpcParams.Receive.SenderClientId)
            {
                UpdateAnimationState(animSnapshot);
                var clientRpcParams = new ClientRpcParams();
                clientRpcParams.Send = new ClientRpcSendParams();
                var clientIds = new List<ulong>(NetworkManager.ConnectedClientsIds);
                clientIds.Remove(serverRpcParams.Receive.SenderClientId);
                clientRpcParams.Send.TargetClientIds = clientIds;
                SendAnimStateClientRpc(animSnapshot, clientRpcParams);
            }
        }

        /// <summary>
        /// Internally-called RPC client receiving function to update some animation parameters on a client when
        ///   the server wants to update them
        /// </summary>
        /// <param name="animSnapshot">the payload containing the parameters to apply</param>
        /// <param name="clientRpcParams">unused</param>
        [ClientRpc]
        private unsafe void SendAnimStateClientRpc(AnimationMessage animSnapshot, ClientRpcParams clientRpcParams = default)
        {
            if ((!IsOwner && OwnerAuthoritative) || !OwnerAuthoritative)
            {
                UpdateAnimationState(animSnapshot);
            }
        }

        /// <summary>
        /// Internally-called RPC client receiving function to update a trigger when the server wants to forward
        ///   a trigger for a client to play / reset
        /// </summary>
        /// <param name="animSnapshot">the payload containing the trigger data to apply</param>
        /// <param name="clientRpcParams">unused</param>
        [ClientRpc]
        private void SendAnimTriggerClientRpc(AnimationTriggerMessage animSnapshot, ClientRpcParams clientRpcParams = default)
        {
            if ((!IsOwner && OwnerAuthoritative) || !OwnerAuthoritative)
            {
                if (animSnapshot.Reset)
                {
                    m_Animator.ResetTrigger(animSnapshot.Hash);
                }
                else
                {
                    m_Animator.SetTrigger(animSnapshot.Hash);
                }
            }
        }

        [ServerRpc]
        private void SendAnimTriggerServerRpc(AnimationTriggerMessage animSnapshot, ServerRpcParams serverRpcParams = default)
        {
            if (OwnerAuthoritative && OwnerClientId == serverRpcParams.Receive.SenderClientId)
            {
                if (animSnapshot.Reset)
                {
                    m_Animator.ResetTrigger(animSnapshot.Hash);
                }
                else
                {
                    m_Animator.SetTrigger(animSnapshot.Hash);
                }
                var clientRpcParams = new ClientRpcParams();
                clientRpcParams.Send = new ClientRpcSendParams();
                var clientIds = new List<ulong>(NetworkManager.ConnectedClientsIds);
                clientIds.Remove(serverRpcParams.Receive.SenderClientId);
                clientRpcParams.Send.TargetClientIds = clientIds;
                SendAnimTriggerClientRpc(animSnapshot, clientRpcParams);
            }
        }

        /// <summary>
        /// Sets the trigger for the associated animation
        ///  Note, triggers are special vs other kinds of parameters.  For all the other parameters we watch for changes
        ///  in FixedUpdate and users can just set them normally off of Animator. But because triggers are transitory
        ///  and likely to come and go between FixedUpdate calls, we require users to set them here to guarantee us to
        ///  catch it...then we forward it to the Animator component
        /// </summary>
        /// <param name="triggerName">The string name of the trigger to activate</param>
        public void SetTrigger(string triggerName)
        {
            SetTrigger(Animator.StringToHash(triggerName));
        }

        /// <inheritdoc cref="SetTrigger(string)" />
        /// <param name="hash">The hash for the trigger to activate</param>
        /// <param name="reset">If true, resets the trigger</param>
        public void SetTrigger(int hash, bool reset = false)
        {
            var animMsg = new AnimationTriggerMessage();
            animMsg.Hash = hash;
            animMsg.Reset = reset;

            if (IsServer && !OwnerAuthoritative || OwnerAuthoritative && IsOwner)
            {
                //  trigger the animation locally on the server...
                if (reset)
                {
                    m_Animator.ResetTrigger(hash);
                }
                else
                {
                    m_Animator.SetTrigger(hash);
                }

                // Owner authoritative sends the update to the server
                // the server then updates the remaining clients with the changes
                if (!IsServer)
                {
                    SendAnimTriggerServerRpc(animMsg);
                }
                else
                {
                    SendAnimTriggerClientRpc(animMsg);
                }
            }
            else
            {
                if (!IsServer && !OwnerAuthoritative)
                {
                    Debug.LogWarning("Trying to call NetworkAnimator.SetTrigger on a client when in server authoritative mode ...ignoring");
                }
                else if (OwnerAuthoritative && !IsOwner)
                {
                    Debug.LogWarning("Trying to call NetworkAnimator.SetTrigger on a non-owner when in owner authoritative mode...ignoring");
                }
            }
        }

        /// <summary>
        /// Resets the trigger for the associated animation.  See <see cref="SetTrigger(string)">SetTrigger</see> for more on how triggers are special
        /// </summary>
        /// <param name="triggerName">The string name of the trigger to reset</param>
        public void ResetTrigger(string triggerName)
        {
            ResetTrigger(Animator.StringToHash(triggerName));
        }

        /// <inheritdoc cref="ResetTrigger(string)" path="summary" />
        /// <param name="hash">The hash for the trigger to activate</param>
        public void ResetTrigger(int hash)
        {
            SetTrigger(hash, true);
        }
    }

}
#endif // COM_UNITY_MODULES_ANIMATION
