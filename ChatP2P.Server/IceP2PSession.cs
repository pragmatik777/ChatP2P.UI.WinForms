using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace ChatP2P.Server
{
    /// <summary>
    /// Session ICE + DataChannel basée sur SIPSorcery.Net.
    /// - setLocal/RemoteDescription renvoient SetDescriptionResultEnum (non-awaitable).
    /// - createOffer/createAnswer renvoient un RTCSessionDescriptionInit (sync).
    /// - DataChannel: event OnMessage(chan, proto, payload()).
    /// - Logs détaillés via événements vers l'UI.
    /// </summary>
    public class IceP2PSession : IAsyncDisposable
    {
        // ---- Events vers l'UI / orchestration ----
        public event Action<string, string>? OnLocalSdp;
        public event Action<string>? OnLocalCandidate;
        public event Action? OnConnected;
        public event Action<string>? OnClosed;

        public event Action<string>? OnTextMessage;
        public event Action<byte[]>? OnBinaryMessage;

        // ---- Logs ICE détaillés ----
        public event Action<RTCIceConnectionState>? OnIceStateChanged;
        public event Action<string>? OnNegotiationLog;

        private readonly RTCPeerConnection _pc;
        private RTCDataChannel? _dc;
        private readonly bool _isCaller;
        private readonly string _label;

        public IceP2PSession(IEnumerable<string> stunUrls, string label, bool isCaller)
        {
            _isCaller = isCaller;
            _label = string.IsNullOrWhiteSpace(label) ? "data" : label;

            // Config ICE
            var cfg = new RTCConfiguration()
            {
                iceServers = new List<RTCIceServer>()
            };
            
            if (stunUrls != null)
            {
                foreach (var u in stunUrls)
                {
                    if (!string.IsNullOrWhiteSpace(u))
                    {
                        cfg.iceServers.Add(new RTCIceServer { urls = u });
                    }
                }
            }

            _pc = new RTCPeerConnection(cfg);
            OnNegotiationLog?.Invoke($"ICE cfg: caller={_isCaller}, label='{_label}'");

            // Candidats locaux
            _pc.onicecandidate += (ic) =>
            {
                if (ic != null && !string.IsNullOrEmpty(ic.candidate))
                {
                    OnLocalCandidate?.Invoke(ic.candidate);
                    OnNegotiationLog?.Invoke($"Local candidate: {ic.candidate}");
                }
            };

            // États ICE
            _pc.oniceconnectionstatechange += (st) =>
            {
                OnIceStateChanged?.Invoke(st);
                OnNegotiationLog?.Invoke($"ICE state → {st}");
            };

            // États connexion (global)
            _pc.onconnectionstatechange += (state) =>
            {
                switch (state)
                {
                    case RTCPeerConnectionState.connected:
                        OnConnected?.Invoke();
                        break;
                    case RTCPeerConnectionState.failed:
                    case RTCPeerConnectionState.disconnected:
                    case RTCPeerConnectionState.closed:
                        OnClosed?.Invoke(state.ToString());
                        break;
                }
            };

            // DataChannel entrant (callee)
            _pc.ondatachannel += (chan) =>
            {
                _dc = chan;
                WireDataChannel(_dc);
                OnNegotiationLog?.Invoke("ondatachannel (callee) → wired");
            };

            // Caller → crée le datachannel (selon version SIPSorcery : sync ou Task)
            if (_isCaller)
            {
                dynamic created = _pc.createDataChannel(_label);
                
                // Handle both Task<RTCDataChannel> and direct RTCDataChannel returns
                if (created is Task task)
                {
                    task.Wait();
                    _dc = task.GetType().GetProperty("Result")?.GetValue(task) as RTCDataChannel;
                }
                else
                {
                    _dc = created as RTCDataChannel;
                }
                
                if (_dc != null)
                {
                    WireDataChannel(_dc);
                    OnNegotiationLog?.Invoke("createDataChannel() (caller) → wired");
                }
            }
        }

        private void WireDataChannel(RTCDataChannel dc)
        {
            if (dc == null) return;

            // Signature SIPSorcery: (RTCDataChannel, DataChannelPayloadProtocols, Byte())
            dc.onmessage += (channel, proto, payload) =>
            {
                if (payload == null) return;

                // Binaire
                OnBinaryMessage?.Invoke(payload);

                // Essai texte UTF8
                try
                {
                    var txt = Encoding.UTF8.GetString(payload);
                    if (!string.IsNullOrEmpty(txt))
                    {
                        OnTextMessage?.Invoke(txt);
                    }
                }
                catch
                {
                    // ignore si non-text
                }
            };

            dc.onopen += () => OnNegotiationLog?.Invoke("[DEBUG] DataChannel OPENED - ready for transfers");
            dc.onclose += () => OnNegotiationLog?.Invoke("DataChannel close");
        }

        /// <summary>Caller : crée l'offer et publie OnLocalSdp("offer", sdp).</summary>
        public void Start()
        {
            if (_isCaller)
            {
                OnNegotiationLog?.Invoke("createOffer()");
                var offer = _pc.createOffer(); // RTCSessionDescriptionInit
                _pc.setLocalDescription(offer); // SetDescriptionResultEnum (non-awaitable)
                OnNegotiationLog?.Invoke("localDescription set (offer)");
                OnLocalSdp?.Invoke("offer", offer.sdp);
            }
        }

        /// <summary>Applique la SDP distante. Si callee et type=offer, crée l'answer et publie OnLocalSdp("answer", sdp).</summary>
        public void SetRemoteDescription(string sdpType, string sdp)
        {
            if (string.IsNullOrWhiteSpace(sdpType) || string.IsNullOrWhiteSpace(sdp)) return;

            OnNegotiationLog?.Invoke($"setRemoteDescription({sdpType})");
            var t = string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase)
                ? RTCSdpType.offer
                : RTCSdpType.answer;

            var remote = new RTCSessionDescriptionInit { type = t, sdp = sdp };
            _pc.setRemoteDescription(remote);

            if (!_isCaller && t == RTCSdpType.offer)
            {
                OnNegotiationLog?.Invoke("createAnswer()");
                var answer = _pc.createAnswer();
                _pc.setLocalDescription(answer);
                OnNegotiationLog?.Invoke("localDescription set (answer)");
                OnLocalSdp?.Invoke("answer", answer.sdp);
            }
        }

        /// <summary>Ajoute un ICE candidate distant.</summary>
        public void AddRemoteCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            OnNegotiationLog?.Invoke($"addRemoteCandidate: {candidate}");
            _pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidate });
        }

        public bool IsOpen
        {
            get
            {
                if (_dc == null)
                {
                    OnNegotiationLog?.Invoke("[DEBUG] IsOpen: datachannel is null");
                    return false;
                }
                var state = _dc.readyState;
                OnNegotiationLog?.Invoke($"[DEBUG] IsOpen: datachannel state = {state}");
                return state == RTCDataChannelState.open;
            }
        }

        public void SendText(string text)
        {
            if (_dc == null || _dc.readyState != RTCDataChannelState.open)
            {
                throw new InvalidOperationException("DataChannel non prêt.");
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            _dc.send(bytes);
        }

        public void SendBinary(byte[] data)
        {
            if (_dc == null || _dc.readyState != RTCDataChannelState.open)
            {
                throw new InvalidOperationException("DataChannel non prêt.");
            }
            if (data == null) return;
            _dc.send(data);
        }

        // IAsyncDisposable: ValueTask, pas Async/Await
        public ValueTask DisposeAsync()
        {
            try
            {
                _dc?.close();
            }
            catch
            {
                // Ignore errors
            }
            
            try
            {
                _pc.Close("dispose");
            }
            catch
            {
                // Ignore errors
            }
            
            return ValueTask.CompletedTask;
        }
    }
}