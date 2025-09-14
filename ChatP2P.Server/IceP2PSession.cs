using System;
using System.Collections.Generic;
using System.Linq;
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

        // ---- Fix boucle answers multiples ----
        private bool _answerGenerated = false;

        private readonly RTCPeerConnection _pc;
        private RTCDataChannel? _dcMessages;  // Canal dédié messages texte (ordered, reliable)
        private RTCDataChannel? _dcData;      // Canal dédié transferts fichiers (unordered, performance)
        private readonly bool _isCaller;
        private readonly string _label;

        // ---- Buffer Management & Flow Control ----
        private const ulong BUFFER_THRESHOLD = 65536UL;  // 64KB buffer limit
        private const int MAX_CHUNK_SIZE = 262144;    // 256KB max chunk (SIPSorcery best practice)
        private const int MAX_RETRIES = 3;

        public IceP2PSession(IEnumerable<string> stunUrls, string label, bool isCaller)
        {
            _isCaller = isCaller;
            _label = string.IsNullOrWhiteSpace(label) ? "data" : label;

            // ✅ FIX: Configuration ICE simple selon SIPSorcery standards
            var cfg = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                    new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" }
                }
            };

            _pc = new RTCPeerConnection(cfg);
            OnNegotiationLog?.Invoke($"✅ [ICE-CONFIG] Simple configuration: caller={_isCaller}, label='{_label}'");

            // ✅ FIX: Setup ALL event handlers FIRST (SIPSorcery best practice)
            SetupEventHandlers();

            // ✅ FIX: Create DataChannels with proper async handling
            if (_isCaller)
            {
                // Defer DataChannel creation to Start() method for proper async handling
                OnNegotiationLog?.Invoke("✅ [CALLER] Event handlers setup, DataChannels will be created in Start()");
            }
        }

        /// <summary>Setup all event handlers according to SIPSorcery best practices</summary>
        private void SetupEventHandlers()
        {
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

            // ✅ FIX: Simplified connection state handling (SIPSorcery standard)
            _pc.onconnectionstatechange += (state) =>
            {
                OnNegotiationLog?.Invoke($"Connection state → {state}");
                switch (state)
                {
                    case RTCPeerConnectionState.connected:
                        OnConnected?.Invoke();
                        break;
                    case RTCPeerConnectionState.failed:
                        OnClosed?.Invoke("Connection failed");
                        break;
                    case RTCPeerConnectionState.closed:
                        OnClosed?.Invoke("Connection closed");
                        break;
                }
            };

            // DataChannels entrants (callee)
            _pc.ondatachannel += (chan) =>
            {
                OnNegotiationLog?.Invoke($"✅ [CALLEE] Received DataChannel: {chan.label}");
                if (chan.label == "messages")
                {
                    _dcMessages = chan;
                    WireMessageChannel(_dcMessages);
                }
                else if (chan.label == "data")
                {
                    _dcData = chan;
                    WireDataChannel(_dcData);
                }
            };
        }

        /// <summary>✅ FIX: Simplified message channel wiring (SIPSorcery standard)</summary>
        private void WireMessageChannel(RTCDataChannel dc)
        {
            if (dc == null) return;

            dc.onopen += () =>
            {
                OnNegotiationLog?.Invoke($"✅ [MSG-CHANNEL] Opened - {dc.readyState}");
                Console.WriteLine($"✅ [MSG-CHANNEL] Opened - {dc.readyState}");
            };

            dc.onclose += () =>
            {
                OnNegotiationLog?.Invoke($"❌ [MSG-CHANNEL] Closed - {dc.readyState}");
            };

            dc.onmessage += (channel, proto, payload) =>
            {
                if (payload != null)
                {
                    try
                    {
                        var txt = Encoding.UTF8.GetString(payload);
                        OnTextMessage?.Invoke(txt);
                        OnNegotiationLog?.Invoke($"📩 [MSG-CHANNEL] Received {txt.Length} chars");
                    }
                    catch (Exception ex)
                    {
                        OnNegotiationLog?.Invoke($"Message decode error: {ex.Message}");
                    }
                }
            };

            OnNegotiationLog?.Invoke($"✅ [MSG-CHANNEL] Wired, state: {dc.readyState}");
        }

        /// <summary>✅ FIX: Simplified data channel wiring (SIPSorcery standard)</summary>
        private void WireDataChannel(RTCDataChannel dc)
        {
            if (dc == null) return;

            dc.onopen += () =>
            {
                OnNegotiationLog?.Invoke($"✅ [DATA-CHANNEL] Opened - {dc.readyState}");
                Console.WriteLine($"✅ [DATA-CHANNEL] Opened - {dc.readyState}");
            };

            dc.onclose += () =>
            {
                OnNegotiationLog?.Invoke($"❌ [DATA-CHANNEL] Closed - {dc.readyState}");
            };

            dc.onmessage += (channel, proto, payload) =>
            {
                if (payload != null)
                {
                    OnBinaryMessage?.Invoke(payload);
                    OnNegotiationLog?.Invoke($"📦 [DATA-CHANNEL] Received {payload.Length} bytes");
                }
            };

            OnNegotiationLog?.Invoke($"✅ [DATA-CHANNEL] Wired, state: {dc.readyState}");
        }

        /// <summary>Détecte si les données sont des fichiers binaires (base64 chunks) ou des messages texte</summary>
        private bool IsBinaryFileData(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return false;
            
            // 1. Si c'est exactement 2048 bytes, c'est probablement un chunk de fichier
            if (payload.Length == 2048) return true;
            
            // 2. Si c'est un multiple de 512/1024/2048/4096, probablement un chunk
            if (payload.Length % 512 == 0 && payload.Length >= 512) return true;
            
            // 3. Vérifier si c'est du Base64 (chunks de fichier sont en base64)
            try
            {
                var text = Encoding.UTF8.GetString(payload);
                if (IsBase64String(text)) return true;
            }
            catch
            {
                // Si on ne peut pas décoder en UTF8, c'est probablement binaire
                return true;
            }
            
            // 4. Heuristique: ratio de bytes non-printables
            int nonPrintable = 0;
            for (int i = 0; i < Math.Min(payload.Length, 100); i++) // Check first 100 bytes
            {
                byte b = payload[i];
                if (b < 32 && b != 9 && b != 10 && b != 13) // Not tab, LF, CR
                {
                    nonPrintable++;
                }
            }
            
            // Si plus de 30% non-printable dans les premiers 100 bytes = binaire
            return (double)nonPrintable / Math.Min(payload.Length, 100) > 0.3;
        }
        
        /// <summary>Vérifie si la chaîne est du Base64 valide (chunks de fichier)</summary>
        private bool IsBase64String(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            
            // Base64 typique: seulement A-Z, a-z, 0-9, +, /, =
            // Et longueur multiple de 4
            if (s.Length % 4 != 0) return false;
            
            foreach (char c in s)
            {
                if (!((c >= 'A' && c <= 'Z') || 
                      (c >= 'a' && c <= 'z') || 
                      (c >= '0' && c <= '9') || 
                      c == '+' || c == '/' || c == '='))
                {
                    return false;
                }
            }
            
            return true;
        }

        /// <summary>✅ FIX: Async Start with proper DataChannel creation (SIPSorcery pattern)</summary>
        public async Task StartAsync()
        {
            if (_isCaller)
            {
                // ✅ FIX: Create DataChannels with proper async handling
                OnNegotiationLog?.Invoke("✅ [CALLER] Creating DataChannels...");

                // Canal messages texte (ordered, reliable)
                var messageConfig = new RTCDataChannelInit
                {
                    ordered = true,
                    maxRetransmits = null  // Reliable delivery
                };
                _dcMessages = await _pc.createDataChannel("messages", messageConfig);
                WireMessageChannel(_dcMessages);
                OnNegotiationLog?.Invoke($"✅ [DATACHANNEL] Messages channel created async");

                // Canal données binaires (unordered, limited retries for performance)
                var dataConfig = new RTCDataChannelInit
                {
                    ordered = false,
                    maxRetransmits = MAX_RETRIES  // Performance over reliability
                };
                _dcData = await _pc.createDataChannel("data", dataConfig);
                WireDataChannel(_dcData);
                OnNegotiationLog?.Invoke($"✅ [DATACHANNEL] Data channel created async");

                // ✅ FIX: Create offer according to SIPSorcery pattern
                OnNegotiationLog?.Invoke("✅ [OFFER] Creating offer...");
                var offer = _pc.createOffer();
                var setResult = _pc.setLocalDescription(offer);
                OnNegotiationLog?.Invoke($"✅ [OFFER] Local description set: {setResult}");
                OnLocalSdp?.Invoke("offer", offer.sdp);
            }
        }

        /// <summary>Legacy sync method for backward compatibility</summary>
        public void Start()
        {
            StartAsync().GetAwaiter().GetResult();
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

            // ✅ FIX: Éviter la génération multiple d'answers
            if (!_isCaller && t == RTCSdpType.offer)
            {
                if (_answerGenerated)
                {
                    OnNegotiationLog?.Invoke("⚠️  Answer already generated, skipping duplicate offer");
                    return;
                }

                OnNegotiationLog?.Invoke("createAnswer()");
                var answer = _pc.createAnswer();
                _pc.setLocalDescription(answer);
                OnNegotiationLog?.Invoke("localDescription set (answer)");

                _answerGenerated = true; // ✅ Marquer answer généré
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
                // Au moins un des canaux doit être connecté pour compatibilité
                var msgOpen = _dcMessages != null && _dcMessages.readyState == RTCDataChannelState.open;
                var dataOpen = _dcData != null && _dcData.readyState == RTCDataChannelState.open;
                OnNegotiationLog?.Invoke($"[DEBUG] IsOpen: messages={msgOpen}, data={dataOpen}");
                return msgOpen || dataOpen;
            }
        }

        /// <summary>Vérifie si les DataChannels sont prêts</summary>
        public bool IsReady
        {
            get
            {
                return _dcMessages != null && _dcMessages.readyState == RTCDataChannelState.open &&
                       _dcData != null && _dcData.readyState == RTCDataChannelState.open;
            }
        }

        /// <summary>Statistiques buffer pour monitoring</summary>
        public string BufferStats
        {
            get
            {
                var msgBuffer = _dcMessages?.bufferedAmount ?? 0UL;
                var dataBuffer = _dcData?.bufferedAmount ?? 0UL;
                return $"Messages: {msgBuffer}B, Data: {dataBuffer}B, Total: {msgBuffer + dataBuffer}B";
            }
        }

        /// <summary>Vérifie si les buffers sont dans un état sain</summary>
        public bool BufferHealthy
        {
            get
            {
                var msgBuffer = _dcMessages?.bufferedAmount ?? 0UL;
                var dataBuffer = _dcData?.bufferedAmount ?? 0UL;
                return msgBuffer < BUFFER_THRESHOLD && dataBuffer < BUFFER_THRESHOLD;
            }
        }

        /// <summary>Envoie message texte via canal messages avec buffer monitoring</summary>
        public void SendText(string text)
        {
            if (_dcMessages == null || _dcMessages.readyState != RTCDataChannelState.open)
            {
                throw new InvalidOperationException("Message DataChannel non prêt.");
            }

            var bytes = Encoding.UTF8.GetBytes(text);

            // Buffer monitoring (SIPSorcery best practice)
            if (_dcMessages.bufferedAmount > BUFFER_THRESHOLD)
            {
                OnNegotiationLog?.Invoke($"[BUFFER-WARNING] Message channel buffer high: {_dcMessages.bufferedAmount} bytes");
            }

            _dcMessages.send(bytes);
            OnNegotiationLog?.Invoke($"[SEND-TEXT] {bytes.Length} bytes sent, buffer: {_dcMessages.bufferedAmount}");
        }

        /// <summary>Envoie données binaires via canal data avec flow control</summary>
        public void SendBinary(byte[] data)
        {
            if (_dcData == null || _dcData.readyState != RTCDataChannelState.open)
            {
                throw new InvalidOperationException("Data DataChannel non prêt.");
            }
            if (data == null) return;

            // Vérification taille chunk (SIPSorcery best practice: < 256KB)
            if (data.Length > MAX_CHUNK_SIZE)
            {
                throw new ArgumentException($"Chunk size {data.Length} exceeds maximum {MAX_CHUNK_SIZE} bytes");
            }

            // Buffer monitoring avec flow control
            if (_dcData.bufferedAmount > BUFFER_THRESHOLD)
            {
                OnNegotiationLog?.Invoke($"[BUFFER-WARNING] Data channel buffer high: {_dcData.bufferedAmount} bytes - flow control recommended");
            }

            _dcData.send(data);
            OnNegotiationLog?.Invoke($"[SEND-BINARY] {data.Length} bytes sent, buffer: {_dcData.bufferedAmount}");
        }

        /// <summary>Safe wrapper pour SendText avec gestion d'erreurs</summary>
        public bool TrySendText(string text)
        {
            try
            {
                SendText(text);
                return true;
            }
            catch (Exception ex)
            {
                OnNegotiationLog?.Invoke($"❌ [TRY-SEND-TEXT] Failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Safe wrapper pour SendBinary avec gestion d'erreurs</summary>
        public bool TrySendBinary(byte[] data)
        {
            try
            {
                SendBinary(data);
                return true;
            }
            catch (Exception ex)
            {
                OnNegotiationLog?.Invoke($"❌ [TRY-SEND-BINARY] Failed: {ex.Message}");
                return false;
            }
        }

        // IAsyncDisposable: ValueTask, pas Async/Await
        public ValueTask DisposeAsync()
        {
            try
            {
                _dcMessages?.close();
                _dcData?.close();
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