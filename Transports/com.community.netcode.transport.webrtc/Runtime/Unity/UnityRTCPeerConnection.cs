using System.Linq;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using NativeIceCandidateInit = Unity.WebRTC.RTCIceCandidateInit;
using NativePeerConnection = Unity.WebRTC.RTCPeerConnection;
using NativeSessionDescription = Unity.WebRTC.RTCSessionDescription;
using NativeSdpType = Unity.WebRTC.RTCSdpType;
using NativeIceServer = Unity.WebRTC.RTCIceServer;
using NativeIceCredentialType = Unity.WebRTC.RTCIceCredentialType;
using NativeDataChannelState = Unity.WebRTC.RTCDataChannelState;

namespace Netcode.Transports.WebRTC
{
    public class UnityRTCPeerConnection : BaseCustomRTCPeerConnection
    {
        private NativePeerConnection peer;
        // indexed by RTCDeliveryChannel
        private readonly RTCDataChannel[] dataChannels = new RTCDataChannel[RTCDeliveryChannels.Count];

        public UnityRTCPeerConnection(MonoBehaviour context, RTCIceServer[] iceServers, ulong clientId)
            : base(context, iceServers, clientId)
        {
            var config = CreateRtcConfiguration(iceServers);
            peer = new NativePeerConnection(ref config);
            peer.OnIceCandidate = cand =>
            {
                TriggerOnIceCandidate(new RTCIceCandidateInit
                {
                    candidate = cand.Candidate,
                    sdpMid = cand.SdpMid,
                    sdpMLineIndex = cand.SdpMLineIndex,
                });
            };
            peer.OnIceConnectionChange += state =>
            {
                TriggerOnIceConnectionChange((RTCIceConnectionState)state);
            };

            InitDataChannel();
        }

        private void InitDataChannel()
        {
            if (IsHost)
            {
                // The host side creates every channel; the client receives them through OnDataChannel.
                dataChannels[(int)RTCDeliveryChannel.Reliable] =
                    peer.CreateDataChannel(RTCDeliveryChannels.Labels[(int)RTCDeliveryChannel.Reliable]);

                // No retransmits: a lost packet is simply gone, and nothing queues up behind it.
                dataChannels[(int)RTCDeliveryChannel.Unreliable] = peer.CreateDataChannel(
                    RTCDeliveryChannels.Labels[(int)RTCDeliveryChannel.Unreliable],
                    new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });

                // Ordered with no retransmits: late packets are dropped rather than delivered out of order.
                dataChannels[(int)RTCDeliveryChannel.UnreliableSequenced] = peer.CreateDataChannel(
                    RTCDeliveryChannels.Labels[(int)RTCDeliveryChannel.UnreliableSequenced],
                    new RTCDataChannelInit { ordered = true, maxRetransmits = 0 });

                for (int i = 0; i < dataChannels.Length; i++)
                {
                    SetupDataChannelEvents(i);
                }
            }
            else
            {
                peer.OnDataChannel = channel =>
                {
                    int index = RTCDeliveryChannels.IndexOfLabel(channel.Label);
                    Debug.Log($"[WebRTCTransport] OnDataChannel available: {channel.Label}, {channel.Id}, {channel.Negotiated}, {channel.ReadyState}");
                    if (index < 0)
                    {
                        Debug.LogWarning($"[WebRTCTransport] Ignoring unknown data channel: {channel.Label}");
                        return;
                    }

                    dataChannels[index] = channel;
                    if (index == (int)RTCDeliveryChannel.Reliable && channel.ReadyState == NativeDataChannelState.Open)
                    {
                        Debug.Log("[WebRTCTransport] Client DataChannel is already open");
                        TriggerOnDataChannelOpen();
                    }

                    SetupDataChannelEvents(index);
                };
            }
        }

        private void SetupDataChannelEvents(int index)
        {
            var channel = dataChannels[index];

            // The connection is established when the reliable channel opens. The unreliable
            // channels may open slightly later; until then Send falls back to the reliable one.
            if (index == (int)RTCDeliveryChannel.Reliable)
            {
                channel.OnOpen += () =>
                {
                    TriggerOnDataChannelOpen();
                };
            }

            channel.OnMessage = bytes =>
            {
                TriggerOnDataChannelMessage(bytes);
            };

            channel.OnError = error =>
            {
                TriggerOnDataChannelError(new RTCError
                {
                    errorType = (RTCErrorType)error.errorType,
                    message = error.message,
                });
            };
        }

        private static bool IsOpen(RTCDataChannel channel)
        {
            return channel != null && channel.ReadyState == NativeDataChannelState.Open;
        }

        public override bool IsDataChannelOpen()
        {
            return IsOpen(dataChannels[(int)RTCDeliveryChannel.Reliable]);
        }

        public override void Send(byte[] data, RTCDeliveryChannel deliveryChannel)
        {
            var channel = dataChannels[(int)deliveryChannel];
            if (!IsOpen(channel))
            {
                channel = dataChannels[(int)RTCDeliveryChannel.Reliable];
            }

            channel.Send(data);
        }

        public override async Task SetRemoteDescription(RTCSessionDescription description)
        {
            var unityDescription = new NativeSessionDescription
            {
                sdp = description.sdp,
                type = (NativeSdpType)description.type
            };
            await peer.SetRemoteDescriptionAsync(ref unityDescription, ctx);
        }

        public override void AddIceCandidate(RTCIceCandidateInit cand)
        {
            var candidate = new RTCIceCandidate(new NativeIceCandidateInit
            {
                candidate = cand.candidate,
                sdpMid = cand.sdpMid,
                sdpMLineIndex = cand.sdpMLineIndex,
            });
            peer.AddIceCandidate(candidate);
        }

        public override RTCIceConnectionState GetIceConnectionState()
        {
            return (RTCIceConnectionState)peer.IceConnectionState;
        }

        public override async Task<RTCSessionDescription> PrepareOffer()
        {
            var offer = await peer.CreateOfferAsync(ctx);
            await peer.SetLocalDescriptionAsync(ref offer, ctx);

            return new RTCSessionDescription
            {
                sdp = offer.sdp,
                type = (RTCSdpType)offer.type
            };
        }

        public override async Task<RTCSessionDescription> PrepareAnswer()
        {
            var answer = await peer.CreateAnswerAsync(ctx);
            await peer.SetLocalDescriptionAsync(ref answer, ctx);

            return new RTCSessionDescription
            {
                sdp = answer.sdp,
                type = (RTCSdpType)answer.type
            };
        }

        public override void Close()
        {
            peer?.Close();
            foreach (var channel in dataChannels)
            {
                channel?.Close();
            }
        }

        public override async Task<double> GetRTT()
        {
            var statsReport = await peer.GetStatsAsync(ctx);
            if (statsReport == null)
            {
                return 0;
            }

            try
            {
                // Several candidate pairs can exist; the nominated one is the path actually in use.
                double fallback = 0;
                foreach (var stat in statsReport.Stats.Values)
                {
                    if (stat.Type != RTCStatsType.CandidatePair)
                    {
                        continue;
                    }

                    var candidatePair = stat as RTCIceCandidatePairStats;
                    if (candidatePair == null)
                    {
                        continue;
                    }

                    // Trust the nominated pair even when it reports 0: WebRTC measures in whole
                    // milliseconds, so a LAN or same-machine connection really does read as 0.
                    if (candidatePair.nominated)
                    {
                        return candidatePair.currentRoundTripTime;
                    }

                    if (candidatePair.currentRoundTripTime > 0)
                    {
                        fallback = candidatePair.currentRoundTripTime;
                    }
                }

                return fallback;
            }
            finally
            {
                statsReport.Dispose();
            }
        }

        private RTCConfiguration CreateRtcConfiguration(RTCIceServer[] iceServers)
        {
            NativeIceServer[] unityIceServers = iceServers.Select(iceServer =>
            {
                return new NativeIceServer
                {
                    username = iceServer.username,
                    credential = iceServer.credential,
                    credentialType = (NativeIceCredentialType)iceServer.credentialType,
                    urls = iceServer.urls
                };
            }).ToArray();

            return new RTCConfiguration
            {
                iceServers = unityIceServers,
            };
        }
    }
}
