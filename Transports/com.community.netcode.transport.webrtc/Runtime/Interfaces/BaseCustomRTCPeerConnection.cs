using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Netcode.Transports.WebRTC
{
    /// <summary>
    /// The data channels every peer connection carries. Each one maps to a WebRTC data channel
    /// with different delivery guarantees, so unreliable traffic (frequent state updates) is never
    /// held up behind a lost reliable packet.
    /// </summary>
    public enum RTCDeliveryChannel
    {
        /// <summary>Ordered and retransmitted until delivered.</summary>
        Reliable = 0,
        /// <summary>Sent once; may be lost or arrive out of order.</summary>
        Unreliable = 1,
        /// <summary>Sent once; may be lost, but never delivered out of order.</summary>
        UnreliableSequenced = 2,
    }

    public static class RTCDeliveryChannels
    {
        public const int Count = 3;

        /// <summary>Data channel labels, indexed by <see cref="RTCDeliveryChannel"/>. Must match the WebGL client.</summary>
        public static readonly string[] Labels = { "game", "game-unreliable", "game-unreliable-sequenced" };

        public static int IndexOfLabel(string label)
        {
            for (int i = 0; i < Labels.Length; i++)
            {
                if (Labels[i] == label)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    public abstract class BaseCustomRTCPeerConnection
    {
        protected MonoBehaviour ctx;
        protected RTCIceServer[] availableIceServers;
        public readonly ulong ClientId;
        public bool IsHost => ClientId != 0;

        public event Action OnDataChannelOpen;
        public event Action<byte[]> OnDataChannelMessage;
        public event Action<RTCError> OnDataChannelError;
        public event Action<RTCIceCandidateInit> OnIceCandidate;
        public event Action<RTCIceConnectionState> OnIceConnectionChange;

        public BaseCustomRTCPeerConnection(MonoBehaviour context, RTCIceServer[] iceServers, ulong clientId)
        {
            ctx = context;
            availableIceServers = iceServers;
            ClientId = clientId;
        }

        /// <summary>True once the reliable channel is open, which is when the connection counts as established.</summary>
        public abstract bool IsDataChannelOpen();

        /// <summary>
        /// Sends on the given channel. If that channel is not open yet (the unreliable channels can
        /// open a moment after the reliable one), the data goes out on the reliable channel instead.
        /// </summary>
        public abstract void Send(byte[] data, RTCDeliveryChannel channel);

        public void Send(byte[] data)
        {
            Send(data, RTCDeliveryChannel.Reliable);
        }

        /// <summary>Current round-trip time to the remote peer in seconds, or 0 if not known yet.</summary>
        public abstract Task<double> GetRTT();
        public abstract Task SetRemoteDescription(RTCSessionDescription description);
        public abstract void AddIceCandidate(RTCIceCandidateInit iceCandidateInit);
        public abstract RTCIceConnectionState GetIceConnectionState();
        public abstract Task<RTCSessionDescription> PrepareOffer();
        public abstract Task<RTCSessionDescription> PrepareAnswer();
        public abstract void Close();

        protected void TriggerOnDataChannelOpen()
        {
            OnDataChannelOpen?.Invoke();
        }

        protected void TriggerOnDataChannelMessage(byte[] bytes)
        {
            OnDataChannelMessage?.Invoke(bytes);
        }

        protected void TriggerOnDataChannelError(RTCError error)
        {
            OnDataChannelError?.Invoke(error);
        }

        protected void TriggerOnIceCandidate(RTCIceCandidateInit iceCandidateInit)
        {
            OnIceCandidate?.Invoke(iceCandidateInit);
        }

        protected void TriggerOnIceConnectionChange(RTCIceConnectionState state)
        {
            OnIceConnectionChange?.Invoke(state);
        }
    }
}
