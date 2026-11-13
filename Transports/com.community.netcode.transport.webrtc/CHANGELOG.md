# Changelog
All notable changes to this package will be documented in this file. The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)

## 1.1.0

### Added
- Unreliable delivery. Each peer connection now carries three data channels: reliable (`game`), unreliable (`game-unreliable`: unordered, no retransmits) and unreliable sequenced (`game-unreliable-sequenced`: ordered, no retransmits). Netcode's `Unreliable` and `UnreliableSequenced` deliveries are sent on the matching channel, so a lost packet no longer delays everything behind it. Until an unreliable channel has opened, its traffic falls back to the reliable channel.
- `WebRTCTransport.useUnreliableChannels` (default on) to send everything on the reliable channel as before.
- `GetCurrentRtt` now returns the measured round-trip time. It is read from the active ICE candidate pair every `rttPollInterval` seconds (default 1) and cached, which also lets Netcode's server time sync account for latency.

### Changed
- Both ends of a connection must run this version: the host creates the new channels and older clients do not expect them.
- Removed per-packet logging from the WebGL send and receive paths.
- The WebGL client sets `binaryType = 'arraybuffer'` on its data channels (Firefox defaults to Blob).

### Fixed
- Signaling messages from a socket that has no client id yet no longer throw `KeyNotFoundException`.
- The native backend now disposes the stats report after reading the round-trip time, and reads it from the nominated candidate pair rather than the first one listed.

## 1.0.0

### Added
- First version of the WebRTC transport as a Unity package.
- Native backend via `com.unity.webrtc` for Editor, Desktop, and Mobile.
- WebGL backend via browser WebRTC and a packaged JavaScript plugin (no custom WebGL template required).
- Socket.IO signaling client. Sample Node.js signaling server: https://github.com/aziztitu/webrtc-ngo-signaling
