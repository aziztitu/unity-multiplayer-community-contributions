import {
  DELIVERY_CHANNEL_LABELS,
  DELIVERY_CHANNEL_OPTIONS,
  DeliveryChannel,
  WebRTCBridgeTypeConversions,
} from './webrtc.bridge.types';

export class WebGLRtcPeerConnection {
  peer: RTCPeerConnection;
  /** Indexed by DeliveryChannel. */
  dataChannels: (RTCDataChannel | undefined)[] = [];

  constructor(iceServers: RTCIceServer[], private readonly clientId: number) {
    this.peer = new RTCPeerConnection({
      iceServers: iceServers,
    });

    this.peer.onicecandidate = (e) => {
      const cand = e.candidate;
      if (!cand) {
        return;
      }

      window.WebRTCBridge.notifyPeerEvent(this.clientId, 'onicecandidate', {
        candidate: cand.candidate,
        sdpMid: cand.sdpMid,
        sdpMLineIndex: cand.sdpMLineIndex,
      });
    };

    this.peer.oniceconnectionstatechange = () => {
      window.WebRTCBridge.notifyPeerEvent(this.clientId, 'oniceconnectionstatechange', {
        state: WebRTCBridgeTypeConversions.UnityRTCIceConnectionState(this.peer.iceConnectionState),
      });
    };

    console.log('Initializing Data Channel Logic...');
    this.initDataChannels();
  }

  private initDataChannels() {
    if (this.isHost()) {
      // The host side creates every channel; the client receives them through ondatachannel.
      DELIVERY_CHANNEL_LABELS.forEach((label, index) => {
        this.dataChannels[index] = this.peer.createDataChannel(label, DELIVERY_CHANNEL_OPTIONS[index]);
        this.setupDataChannelEvents(index);
      });
    } else {
      this.peer.ondatachannel = (e) => {
        const index = DELIVERY_CHANNEL_LABELS.indexOf(e.channel.label);
        console.log(
          `[WebRTCTransport] OnDataChannel available: ${e.channel.label}, ${e.channel.id}, ${e.channel.negotiated}, ${e.channel.readyState}`
        );
        if (index < 0) {
          console.warn(`[WebRTCTransport] Ignoring unknown data channel: ${e.channel.label}`);
          return;
        }

        this.dataChannels[index] = e.channel;

        // This additional check is needed on the Desktop version, but this creates a duplicate event here
        // if (e.channel.readyState == 'open') {
        //   console.log('[WebRTCTransport] Client DataChannel is already open');
        //   window.WebRTCBridge.notifyPeerEvent(this.clientId, 'datachannel.onopen');
        // }

        this.setupDataChannelEvents(index);
      };
    }
  }

  private setupDataChannelEvents(index: number) {
    const channel = this.dataChannels[index];
    if (!channel) {
      return;
    }

    // Firefox defaults to Blob; onmessage below expects an ArrayBuffer.
    channel.binaryType = 'arraybuffer';

    // The connection is established when the reliable channel opens. The unreliable channels may
    // open slightly later; until then send() falls back to the reliable one.
    if (index === DeliveryChannel.Reliable) {
      channel.onopen = () => {
        window.WebRTCBridge.notifyPeerEvent(this.clientId, 'datachannel.onopen');
      };
    }

    channel.onmessage = (messageEvent) => {
      const bytes = new Uint8Array(messageEvent.data);
      window.WebRTCBridge.forwardPeerData(this.clientId, bytes);
    };

    channel.onerror = (errorEvent) => {
      const error = errorEvent.error;
      console.error(
        `Error from data channel '${channel.label}' on client ${this.clientId}`,
        error.errorDetail,
        error.message
      );

      window.WebRTCBridge.notifyPeerEvent(this.clientId, 'datachannel.onerror', {
        message: error.message,
      });
    };
  }

  /** True once the reliable channel is open. */
  public isDataChannelOpen() {
    return this.dataChannels[DeliveryChannel.Reliable]?.readyState == 'open';
  }

  // Runs for every packet sent: no logging here.
  public send(bytes: Uint8Array, channel: DeliveryChannel = DeliveryChannel.Reliable) {
    let dataChannel = this.dataChannels[channel];
    if (dataChannel?.readyState != 'open') {
      dataChannel = this.dataChannels[DeliveryChannel.Reliable];
    }

    dataChannel?.send(bytes);
  }

  public async setRemoteDescription(description: RTCSessionDescriptionInit) {
    await this.peer.setRemoteDescription(description);
  }

  public addIceCandidate(candidate: RTCIceCandidateInit) {
    this.peer.addIceCandidate(candidate);
  }

  public getIceConnectionState() {
    return this.peer.iceConnectionState;
  }

  public async prepareOffer(): Promise<RTCSessionDescriptionInit> {
    var offer = await this.peer.createOffer();
    await this.peer.setLocalDescription(offer);
    return offer;
  }

  public async prepareAnswer(): Promise<RTCSessionDescriptionInit> {
    var answer = await this.peer.createAnswer();
    await this.peer.setLocalDescription(answer);
    return answer;
  }

  public close() {
    this.peer?.close();
    this.dataChannels.forEach((channel) => channel?.close());
  }

  /** Round-trip time in seconds, or 0 if not known yet. */
  public async getRTT(): Promise<number> {
    var statsReport = await this.peer.getStats();

    // Several candidate pairs can exist; the nominated one is the path actually in use.
    // Trust the nominated pair even when it reports 0: a LAN or same-machine connection can
    // genuinely measure as 0.
    let fallback = 0;
    let nominated: number | undefined;
    statsReport.forEach((stat: RTCStats) => {
      if (stat.type != 'candidate-pair') {
        return;
      }

      const candidatePair = stat as RTCIceCandidatePairStats;
      if (candidatePair.nominated) {
        nominated = candidatePair.currentRoundTripTime ?? 0;
      } else if (candidatePair.currentRoundTripTime) {
        fallback = candidatePair.currentRoundTripTime;
      }
    });

    return nominated ?? fallback; // 0 = not measured yet, or under a millisecond
  }

  private isHost() {
    return this.clientId !== 0;
  }
}
