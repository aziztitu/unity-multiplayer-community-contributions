mergeInto(LibraryManager.library, {
  // Init
  WebRTC_InitBridge: function(bridgeGameObjectNamePtr) {
    const bridgeGameObjectName = UTF8ToString(bridgeGameObjectNamePtr);
    console.log('WebRTC_InitBridge', bridgeGameObjectName);

    window._malloc = _malloc;
    window.UnityModule = Module;
    WebRTCBridge.init(bridgeGameObjectName);
  },
  WebRTC_RegisterPeerDataCallback: function (callbackPtr) {
    console.log('WebRTC_RegisterPeerDataCallback');
    WebRTCBridge.registerPeerDataCallback(callbackPtr);
  },

  // Signalling
  WebRTC_CreateSignalClient: function (urlPtr, tokenPtr) {
    const url = UTF8ToString(urlPtr);
    const token = UTF8ToString(tokenPtr);
    console.log('WebRTC_CreateSignalClient', url);
    WebRTCBridge.createSignalClient(url, token);
  },

  WebRTC_ConnectSignalClientAsync: function (callIdPtr) {
    const callId = UTF8ToString(callIdPtr);
    console.log('WebRTC_ConnectSignalClientAsync');
    WebRTCBridge.connectSignalClientAsync(callId);
  },

  WebRTC_DisconnectSignalClient: function () {
    console.log('WebRTC_DisconnectSignalClient');
    WebRTCBridge.disconnectSignalClient();
  },

  WebRTC_SendSignal: function (eventNamePtr, payloadPtr) {
    const eventName = UTF8ToString(eventNamePtr);
    const payloadJson = UTF8ToString(payloadPtr);
    console.log('WebRTC_SendSignal', eventName, payloadJson);

    let payload = payloadJson;
    if (payloadJson.startsWith("{") || payloadJson.startsWith('[')) {
      payload = JSON.parse(payloadJson);
    }
    WebRTCBridge.sendSignal(eventName, payload);
  },

  WebRTC_ListenForSignal: function (eventNamePtr) {
    const eventName = UTF8ToString(eventNamePtr);
    console.log('WebRTC_ListenForSignal', eventName);
    WebRTCBridge.listenForSignal(eventName);
  },

  // Peer Connection

  WebRTC_CreateNewPeer: function (iceServersJsonPtr, clientId) {
    const iceServersJson = UTF8ToString(iceServersJsonPtr);
    console.log('WebRTC_CreateNewPeer', clientId);
    WebRTCBridge.createNewPeer(JSON.parse(iceServersJson), clientId);
  },

  // The two functions below run for every packet sent, so they do not log.
  WebRTC_IsDataChannelOpen: function (clientId) {
    return WebRTCBridge.isDataChannelOpen(clientId);
  },

  // channel: 0 = reliable, 1 = unreliable, 2 = unreliable sequenced (RTCDeliveryChannel in C#)
  WebRTC_Send: function (clientId, dataPtr, length, channel) {
    const bytes = new Uint8Array(Module.HEAPU8.buffer, dataPtr, length);
    return WebRTCBridge.send(clientId, bytes, channel);
  },

  WebRTC_SetRemoteDescriptionAsync: function (clientId, jsonPtr, callIdPtr) {
    const json = UTF8ToString(jsonPtr);
    const callId = UTF8ToString(callIdPtr);
    console.log('WebRTC_SetRemoteDescriptionAsync', clientId, json, callId);
    WebRTCBridge.setRemoteDescriptionAsync(clientId, JSON.parse(json), callId);
  },

  WebRTC_AddIceCandidate: function (clientId, jsonPtr) {
    const json = UTF8ToString(jsonPtr);
    console.log('WebRTC_AddIceCandidate', json);
    WebRTCBridge.addIceCandidate(clientId, JSON.parse(json));
  },

  WebRTC_GetIceConnectionState: function (clientId) {
    console.log('WebRTC_GetIceConnectionState', clientId);
    return WebRTCBridge.getIceConnectionState(clientId);
  },

  WebRTC_PrepareOfferAsync: function (clientId, callIdPtr) {
    const callId = UTF8ToString(callIdPtr);
    console.log('WebRTC_PrepareOfferAsync', clientId, callId);
    WebRTCBridge.prepareOfferAsync(clientId, callId);
  },

  WebRTC_PrepareAnswerAsync: function (clientId, callIdPtr) {
    const callId = UTF8ToString(callIdPtr);
    console.log('WebRTC_PrepareAnswerAsync', clientId, callId);
    WebRTCBridge.prepareAnswerAsync(clientId, callId);
  },

  WebRTC_Close: function (clientId) {
    console.log('WebRTC_Close', clientId);
    WebRTCBridge.close(clientId);
  },

  // Polled about once a second per peer, so it does not log.
  WebRTC_GetRTTAsync: function (clientId, callIdPtr) {
    const callId = UTF8ToString(callIdPtr);
    WebRTCBridge.getRTTAsync(clientId, callId);
  }
});
