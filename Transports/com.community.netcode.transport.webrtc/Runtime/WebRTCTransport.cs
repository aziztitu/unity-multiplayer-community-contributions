using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

namespace Netcode.Transports.WebRTC
{
    public class WebRTCTransport : NetworkTransport
    {
        internal static Func<ICustomWebRTC> NativeBackendFactory;

        internal static void RegisterNativeBackend(Func<ICustomWebRTC> factory)
        {
            NativeBackendFactory = factory;
        }

        private ICustomWebRTC webRTC;

        [Header("Signaling")]
        public string signalingUrl = "http://localhost:4000";
        public string SignalingServerAuthToken = "";
        public RTCIceServer[] customIceServers;

        [Header("Rooms")]
        public string roomId = "";

        [Header("Delivery")]
        [Tooltip("Send Netcode's unreliable deliveries on unreliable WebRTC data channels, so a lost packet never delays the ones behind it. Turn off to send everything on the single reliable, ordered channel.")]
        public bool useUnreliableChannels = true;

        [Tooltip("How often, in seconds, the round-trip time to each peer is measured.")]
        public float rttPollInterval = 1f;

        public BaseCustomRTCSignalClient signalClient { get; private set; }

        private Dictionary<ulong, BaseCustomRTCPeerConnection> peers = new();
        private Dictionary<string, ulong> socketIdToClientId = new();
        private Dictionary<ulong, List<RTCIceCandidateInit>> iceCandidateQueues = new();
        private HashSet<ulong> remoteDescriptionSet = new();

        private BaseCustomRTCPeerConnection clientPeer;

        private ulong nextClientId = 1;

        private readonly Queue<(NetworkEvent, ulong, ArraySegment<byte>)> eventQueue = new();

        private CancellationTokenSource hostIceTimeout;
        private CancellationTokenSource clientIceTimeout;

        private bool connected;

        private byte[] sendBuffer = new byte[1500];

        // last measured round-trip time per client, in milliseconds
        private readonly Dictionary<ulong, ulong> rttMs = new();
        private readonly List<ulong> rttPollScratch = new();
        private float nextRttPollTime;

        public override ulong ServerClientId => 0;

        public override void Initialize(NetworkManager networkManager = null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            webRTC = new WebGLWebRTC();
#else
            if (NativeBackendFactory == null)
            {
                Debug.LogError("[WebRTCTransport] Native WebRTC backend is not available. Install com.unity.webrtc and Socket.IO Unity.");
                return;
            }

            webRTC = NativeBackendFactory();
#endif
            webRTC.Init(this);
            Debug.Log("[WebRTCTransport] Initialized");
        }

        protected override void OnEarlyUpdate()
        {
            MainThreadDispatcher.Dispatch();
            PollRtt();
        }

        // Reading the round-trip time from WebRTC is asynchronous, but Netcode asks for it
        // synchronously (it feeds the server time sync). So measure on a timer and cache it.
        private void PollRtt()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextRttPollTime)
            {
                return;
            }

            nextRttPollTime = now + Mathf.Max(0.1f, rttPollInterval);

            if (clientPeer != null && clientPeer.IsDataChannelOpen())
            {
                _ = UpdateRtt(ServerClientId, clientPeer);
            }

            rttPollScratch.Clear();
            rttPollScratch.AddRange(peers.Keys);
            foreach (var clientId in rttPollScratch)
            {
                if (peers.TryGetValue(clientId, out var peer) && peer.IsDataChannelOpen())
                {
                    _ = UpdateRtt(clientId, peer);
                }
            }
        }

        private async Task UpdateRtt(ulong clientId, BaseCustomRTCPeerConnection peer)
        {
            try
            {
                double seconds = await peer.GetRTT();

                // the peer may have gone away while the measurement was in flight
                bool stillCurrent = clientId == ServerClientId ? clientPeer == peer : peers.TryGetValue(clientId, out var current) && current == peer;
                if (seconds >= 0 && stillCurrent)
                {
                    rttMs[clientId] = (ulong)Math.Round(seconds * 1000.0);
                }
            }
            catch (Exception)
            {
                // stats are best effort; keep the last known value
            }
        }

        public override void Shutdown()
        {
            Debug.Log("[WebRTCTransport] Shutdown");

            foreach (var p in peers.Values)
            {
                p.Close();
            }

            peers.Clear();
            clientPeer?.Close();
            signalClient?.Disconnect();

            socketIdToClientId.Clear();
            iceCandidateQueues.Clear();
            remoteDescriptionSet.Clear();
            rttMs.Clear();

            connected = false;
        }

        private RTCIceServer[] GetIceServers()
        {
            var iceServers = new List<RTCIceServer>
            {
                new RTCIceServer
                {
                    urls = new[]
                    {
                        "stun:stun.l.google.com:19302",
                        "stun:stun1.l.google.com:19302",
                        "stun:stun2.l.google.com:19302",
                        "stun:stun3.l.google.com:19302",
                        "stun:stun.ekiga.net:3478",
                        "stun:stun.iptel.org:3478"
                    }
                },
            };

            if (customIceServers != null)
            {
                iceServers.AddRange(customIceServers);
            }

            return iceServers.ToArray();
        }

        public override bool StartClient()
        {
            if (connected)
            {
                Debug.LogError("[WebRTCTransport] Already connected or is trying to connect..");
                return false;
            }

            Debug.Log("[WebRTCTransport] StartClient");
            _ = ConnectClient();
            return true;
        }

        public override bool StartServer()
        {
            if (connected)
            {
                Debug.LogError("[WebRTCTransport] Already connected or is trying to connect..");
                return false;
            }

            Debug.Log("[WebRTCTransport] StartServer");
            _ = ConnectHost();
            return true;
        }

        public override void Send(ulong clientId, ArraySegment<byte> data, NetworkDelivery delivery)
        {
            BaseCustomRTCPeerConnection targetPeer;
            if (clientId == ServerClientId)
            {
                targetPeer = clientPeer;
            }
            else if (!peers.TryGetValue(clientId, out targetPeer))
            {
                return;
            }

            if (targetPeer == null || !targetPeer.IsDataChannelOpen())
            {
                Debug.Log($"[WebRTCTransport] Cannot send data - data channel for {clientId} is not open");
                return;
            }

            var channel = GetDeliveryChannel(delivery);

            if (data.Count <= sendBuffer.Length)
            {
                Buffer.BlockCopy(data.Array, data.Offset, sendBuffer, 0, data.Count);
                targetPeer.Send(new ArraySegment<byte>(sendBuffer, 0, data.Count).ToArray(), channel);
            }
            else
            {
                var temp = new byte[data.Count];
                Buffer.BlockCopy(data.Array, data.Offset, temp, 0, data.Count);
                targetPeer.Send(temp, channel);
            }
        }

        private RTCDeliveryChannel GetDeliveryChannel(NetworkDelivery delivery)
        {
            if (!useUnreliableChannels)
            {
                return RTCDeliveryChannel.Reliable;
            }

            switch (delivery)
            {
                case NetworkDelivery.Unreliable:
                    return RTCDeliveryChannel.Unreliable;
                case NetworkDelivery.UnreliableSequenced:
                    return RTCDeliveryChannel.UnreliableSequenced;
                default:
                    // Reliable, ReliableSequenced and ReliableFragmentedSequenced: a WebRTC reliable
                    // channel is ordered and handles large messages itself.
                    return RTCDeliveryChannel.Reliable;
            }
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            Debug.Log($"[WebRTCTransport] DisconnectRemoteClient {clientId}");
            if (peers.TryGetValue(clientId, out var peer))
            {
                peer.Close();
                peers.Remove(clientId);
            }

            iceCandidateQueues.Remove(clientId);
            remoteDescriptionSet.Remove(clientId);
            rttMs.Remove(clientId);
        }

        public override void DisconnectLocalClient()
        {
            Debug.Log("[WebRTCTransport] DisconnectLocalClient");
            Shutdown();
        }

        /// <summary>
        /// Last measured round-trip time to the client in milliseconds (0 until the first measurement,
        /// about <see cref="rttPollInterval"/> seconds after connecting).
        /// </summary>
        public override ulong GetCurrentRtt(ulong clientId)
        {
            return rttMs.TryGetValue(clientId, out var rtt) ? rtt : 0;
        }

        public override NetworkEvent PollEvent(
            out ulong clientId,
            out ArraySegment<byte> payload,
            out float receiveTime)
        {
            if (eventQueue.Count > 0)
            {
                var (evt, id, data) = eventQueue.Dequeue();
                clientId = id;
                payload = data;
                receiveTime = Time.realtimeSinceStartup;
                return evt;
            }

            clientId = 0;
            payload = default;
            receiveTime = 0;
            return NetworkEvent.Nothing;
        }

        private void InitSocket()
        {
            signalClient = webRTC.CreateSignalClient(this, signalingUrl, SignalingServerAuthToken);
        }

        private async Task<bool> ConnectHost()
        {
            Debug.Log("[WebRTCTransport] Connecting as host...");

            InitSocket();
            await signalClient.Connect();

            signalClient.On("room-created", payload =>
            {
                roomId = payload.GetProperty("roomId").GetString();
                Debug.Log($"[WebRTCTransport] Room created: {roomId}");
            });

            signalClient.On("host-room-failed", payload =>
            {
                string reason = payload.GetProperty("reason").GetString();
                Debug.LogError($"[WebRTCTransport] Room creation failed: {reason}");
                Shutdown();
            });

            signalClient.On("new-client", async payload =>
            {
                string socketId = payload.GetProperty("socketId").GetString();
                Debug.Log($"[WebRTCTransport] New client socket: {socketId}");
                await HandleNewClient(socketId);
            });

            signalClient.On("answer", async payload =>
            {
                string from = payload.GetProperty("from").GetString();
                string sdp = payload.GetProperty("sdp").GetString();

                if (!socketIdToClientId.TryGetValue(from, out ulong clientId))
                {
                    Debug.LogWarning($"[WebRTCTransport] Ignoring answer from unknown socket {from}");
                    return;
                }

                Debug.Log($"[WebRTCTransport] Answer received from client {clientId}: {sdp}");

                if (peers.TryGetValue(clientId, out var peer))
                {
                    var answer = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
                    await peer.SetRemoteDescription(answer);
                    remoteDescriptionSet.Add(clientId);
                    DrainPendingRemoteIceCandidate(clientId);
                }
            });

            signalClient.On("candidate", payload =>
            {
                string from = payload.GetProperty("from").GetString();
                var candidateData = payload.GetProperty("candidate");

                if (!socketIdToClientId.TryGetValue(from, out ulong clientId))
                {
                    Debug.LogWarning($"[WebRTCTransport] Ignoring ICE candidate from unknown socket {from}");
                    return;
                }

                HandleRemoteIceCandidate(clientId, candidateData.ParseIceCandidateInit());
            });

            signalClient.On("client-disconnected", payload =>
            {
                string socketId = payload.GetProperty("socketId").GetString();

                // a socket that left before it was assigned a client id has nothing to clean up
                if (!socketIdToClientId.TryGetValue(socketId, out ulong clientId))
                {
                    return;
                }

                Debug.LogWarning($"[WebRTCTransport] Client {clientId} disconnected via socket event.");

                if (peers.ContainsKey(clientId))
                {
                    peers[clientId].Close();
                }

                eventQueue.Enqueue((NetworkEvent.Disconnect, clientId, default));

                peers.Remove(clientId);
                socketIdToClientId.Remove(socketId);
                rttMs.Remove(clientId);
            });

            await signalClient.Emit("host-room", new { roomId = roomId });

            connected = true;
            return true;
        }

        private async Task HandleNewClient(string socketId)
        {
            ulong clientId = nextClientId++;
            socketIdToClientId[socketId] = clientId;

            var peer = webRTC.CreatePeerConnection(this, GetIceServers(), clientId);

            peers[clientId] = peer;

            SetupIceStateHandlers(peer, clientId);

            peer.OnDataChannelOpen += () =>
            {
                Debug.Log($"[WebRTCTransport] DataChannel open for clientId={clientId}");
                eventQueue.Enqueue((NetworkEvent.Connect, clientId, default));
            };

            peer.OnDataChannelMessage += bytes =>
            {
                eventQueue.Enqueue((NetworkEvent.Data, clientId, new ArraySegment<byte>(bytes)));
            };

            peer.OnDataChannelError += e => Debug.LogError($"DataChannel error for client {clientId}: {e.message}");

            peer.OnIceCandidate += cand =>
            {
                Debug.Log($"New ICE: {cand.candidate}, {cand.sdpMid}, {cand.sdpMLineIndex}");
                signalClient.Emit("candidate", new
                {
                    to = socketId,
                    candidate = cand.ToObject(),
                });
            };

            var offer = await peer.PrepareOffer();

            await signalClient.Emit("offer", new { to = socketId, sdp = offer.sdp });

            _ = StartIceTimeout(peer, clientId);
        }

        private async Task<bool> ConnectClient()
        {
            Debug.Log("[WebRTCTransport] Connecting as client...");

            if (string.IsNullOrEmpty(roomId))
            {
                Debug.LogError("[WebRTCTransport] No roomId set for joining!");
                return false;
            }

            InitSocket();
            await signalClient.Connect();

            clientPeer = webRTC.CreatePeerConnection(this, GetIceServers(), 0);

            SetupIceStateHandlers(clientPeer);

            clientPeer.OnDataChannelOpen += () =>
            {
                Debug.Log("[WebRTCTransport] Client DataChannel opened");
                connected = true;
                eventQueue.Enqueue((NetworkEvent.Connect, 0, default));
            };

            clientPeer.OnDataChannelMessage += bytes =>
            {
                eventQueue.Enqueue((NetworkEvent.Data, 0, new ArraySegment<byte>(bytes)));
            };

            clientPeer.OnDataChannelError += e => Debug.LogError($"DataChannel error: {e.message}");

            clientPeer.OnIceCandidate += cand =>
            {
                Debug.Log($"New ICE: {cand.candidate}, {cand.sdpMid}, {cand.sdpMLineIndex}");

                signalClient.Emit("candidate", new
                {
                    roomId = roomId,
                    candidate = cand.ToObject(),
                });
            };

            signalClient.On("room-not-found", payload =>
            {
                string missingRoomId = payload.GetProperty("roomId").GetString();
                Debug.LogError($"[WebRTCTransport] Room not found: {missingRoomId}");
                Shutdown();
            });

            signalClient.On("offer", async payload =>
            {
                string from = payload.GetProperty("from").GetString();
                string sdp = payload.GetProperty("sdp").GetString();

                Debug.Log($"[WebRTCTransport] Offer received from server: {sdp}");

                var offer = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = sdp };
                await clientPeer.SetRemoteDescription(offer);
                remoteDescriptionSet.Add(0);
                DrainPendingRemoteIceCandidate(0);

                var answer = await clientPeer.PrepareAnswer();
                await signalClient.Emit("answer", new { to = from, sdp = answer.sdp });

                _ = StartIceTimeout(clientPeer);
            });

            signalClient.On("candidate", payload =>
            {
                var candidateData = payload.GetProperty("candidate");
                HandleRemoteIceCandidate(0, candidateData.ParseIceCandidateInit());
            });

            signalClient.On("host-disconnected", _ =>
            {
                Debug.LogWarning("[WebRTCTransport] Host disconnected.");
                clientPeer?.Close();
                eventQueue.Enqueue((NetworkEvent.Disconnect, 0, default));
            });

            Debug.Log($"[WebRTCTransport] Joining room {roomId}");
            await signalClient.Emit("join-room", new { roomId });

            connected = true;
            return true;
        }

        private void HandleRemoteIceCandidate(ulong clientId, RTCIceCandidateInit candidateInit)
        {
            if (remoteDescriptionSet.Contains(clientId))
            {
                AssignRemoteCandidateToClientId(candidateInit, clientId);
            }
            else
            {
                if (!iceCandidateQueues.ContainsKey(clientId))
                {
                    iceCandidateQueues[clientId] = new List<RTCIceCandidateInit>();
                }

                iceCandidateQueues[clientId].Add(candidateInit);
            }
        }

        private void DrainPendingRemoteIceCandidate(ulong clientId)
        {
            if (iceCandidateQueues.TryGetValue(clientId, out var queue))
            {
                foreach (var candidateInit in queue)
                {
                    AssignRemoteCandidateToClientId(candidateInit, clientId);
                }

                queue.Clear();
            }
        }

        private void AssignRemoteCandidateToClientId(RTCIceCandidateInit candidateInit, ulong clientId)
        {
            if (clientId == 0)
            {
                clientPeer?.AddIceCandidate(candidateInit);
            }
            else if (peers.TryGetValue(clientId, out var peer))
            {
                peer.AddIceCandidate(candidateInit);
            }
        }

        public BaseCustomRTCPeerConnection GetPeerFromClientId(ulong clientId)
        {
            if (clientId == 0)
            {
                return clientPeer;
            }

            if (peers.TryGetValue(clientId, out var peer))
            {
                return peer;
            }

            return null;
        }

        private void SetupIceStateHandlers(BaseCustomRTCPeerConnection peer, ulong clientId = 0)
        {
            peer.OnIceConnectionChange += state =>
            {
                Debug.Log($"[WebRTC] ICE state for {clientId}: {state}");
                if (state == RTCIceConnectionState.Connected || state == RTCIceConnectionState.Completed)
                {
                    if (clientId == 0)
                    {
                        clientIceTimeout?.Cancel();
                    }
                    else
                    {
                        hostIceTimeout?.Cancel();
                    }
                }

                if (state == RTCIceConnectionState.Failed)
                {
                    Debug.LogWarning($"[WebRTC] ICE failed for {clientId}");
                    eventQueue.Enqueue((NetworkEvent.Disconnect, clientId, default));
                    peer.Close();
                }
            };
        }

        private async Task StartIceTimeout(BaseCustomRTCPeerConnection peer, ulong clientId = 0, int timeoutMs = 5000)
        {
            var cts = new CancellationTokenSource();
            if (clientId == 0)
            {
                clientIceTimeout = cts;
            }
            else
            {
                hostIceTimeout = cts;
            }

            try
            {
                await Task.Delay(timeoutMs, cts.Token);
                var state = peer.GetIceConnectionState();
                if (state != RTCIceConnectionState.Connected &&
                    state != RTCIceConnectionState.Completed)
                {
                    Debug.LogWarning($"[WebRTC] ICE timeout for {clientId}");
                    eventQueue.Enqueue((NetworkEvent.Disconnect, clientId, default));
                    peer.Close();
                }
            }
            catch (TaskCanceledException)
            {
            }
        }
    }
}
