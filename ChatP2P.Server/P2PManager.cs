using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace ChatP2P.Server
{
    public static class P2PManager
    {
        // ==== Tags de signalisation ====
        public const string TAG_ICE_OFFER = "ICE_OFFER:";
        public const string TAG_ICE_ANSWER = "ICE_ANSWER:";
        public const string TAG_ICE_CAND = "ICE_CAND:";

        // ==== Events exposés à l'UI ====
        public static event Action<string, string>? OnLog;
        public static event Action<string, bool>? OnP2PState;
        public static event Action<string, string>? OnP2PText;
        public static event Action<string, byte[]>? OnP2PBinary;

        // ==== Signaling et état ====
        private static Func<string, string, Task>? _sendSignal = null;
        private static string _localName = "Me";

        private static readonly object _gate = new();
        private static readonly Dictionary<string, IceP2PSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _startingPeers = new(StringComparer.OrdinalIgnoreCase);

        // ✅ FIX: Protection globale contre spam answers par peer
        private static readonly HashSet<string> _answersGenerated = new(StringComparer.OrdinalIgnoreCase);

        // anti-doublons de candidates
        private static readonly Dictionary<string, HashSet<string>> _seenCandOut = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> _seenCandIn = new(StringComparer.OrdinalIgnoreCase);

        // candidates reçues avant la SDP / session
        private static readonly Dictionary<string, List<string>> _pendingCands = new(StringComparer.OrdinalIgnoreCase);

        // ==== API publique ====
        public static void Init(Func<string, string, Task> sendSignal, string localDisplayName)
        {
            _sendSignal = sendSignal ?? throw new ArgumentNullException(nameof(sendSignal));
            if (!string.IsNullOrWhiteSpace(localDisplayName))
                _localName = localDisplayName;
        }

        public static void StartP2P(string peer, IEnumerable<string>? stunUrls = null)
        {
            if (string.IsNullOrWhiteSpace(peer)) return;
            
            // NOUVEAU: Utiliser configuration adaptative optimisée si stunUrls == null
            if (stunUrls == null)
            {
                OnLog?.Invoke(peer, "🚀 Using adaptive high-performance ICE configuration");
            }
            else
            {
                OnLog?.Invoke(peer, $"📡 Using custom ICE servers: {stunUrls.Count()} servers");
            }

            lock (_gate)
            {
                if (_startingPeers.Contains(peer))
                {
                    OnLog?.Invoke(peer, "Négociation déjà en cours.");
                    return;
                }
                _startingPeers.Add(peer);
            }

            try
            {
                IceP2PSession? existing = null;
                bool exists;
                lock (_gate)
                {
                    exists = _sessions.TryGetValue(peer, out existing);
                }

                if (exists && existing != null && existing.IsOpen)
                {
                    OnLog?.Invoke(peer, "Session déjà connectée.");
                    lock (_gate) { _startingPeers.Remove(peer); }
                    return;
                }

                // Reset dedup OUT/IN pour un nouveau cycle de nego
                ResetDedup(peer);

                var sess = new IceP2PSession(stunUrls, "data", isCaller: true);
                WireSessionHandlers(peer, sess);
                lock (_gate)
                {
                    _sessions[peer] = sess;
                }

                OnLog?.Invoke(peer, "Négociation démarrée vers " + peer);
                sess.Start();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(peer, "createOffer error: " + ex.Message);
                lock (_gate)
                {
                    _sessions.Remove(peer);
                    _startingPeers.Remove(peer);
                }
            }
        }

        /// <summary>Envoi rapide d'un message texte via datachannel P2P si ouvert.</summary>
        public static bool TrySendText(string peer, string text)
        {
            if (string.IsNullOrWhiteSpace(peer) || string.IsNullOrEmpty(text)) return false;
            
            IceP2PSession? s = null;
            lock (_gate)
            {
                _sessions.TryGetValue(peer, out s);
            }
            
            if (s == null || !s.IsOpen)
            {
                OnLog?.Invoke(peer, $"[DEBUG] TrySendText: datachannel not ready (s={s != null}, isOpen={s?.IsOpen ?? false})");

                // Attendre jusqu'à 2 secondes pour que le datachannel s'ouvre
                if (s != null)
                {
                    for (int i = 0; i < 20; i++) // 20 x 100ms = 2 secondes max
                    {
                        if (s.IsOpen)
                        {
                            OnLog?.Invoke(peer, $"[DEBUG] TrySendText: datachannel opened after {i * 100}ms");
                            break;
                        }
                        System.Threading.Thread.Sleep(100);
                    }

                    if (!s.IsOpen)
                    {
                        OnLog?.Invoke(peer, "[DEBUG] TrySendText: datachannel still not open after 2s, falling back to relay");
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
            
            try
            {
                s.SendText(text);
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(peer, "SendText error: " + ex.Message);
                return false;
            }
        }

        /// <summary>Envoi de données binaires via datachannel P2P si ouvert.</summary>
        public static bool TrySendBinary(string peer, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(peer) || data == null || data.Length == 0) return false;
            
            IceP2PSession? s = null;
            lock (_gate)
            {
                _sessions.TryGetValue(peer, out s);
            }
            
            if (s == null)
            {
                OnLog?.Invoke(peer, "[DEBUG] TrySendBinary: no session");
                return false;
            }

            if (!s.IsOpen)
            {
                OnLog?.Invoke(peer, "[DEBUG] TrySendBinary: datachannel not open, waiting...");
                // Attendre un peu pour le datachannel
                System.Threading.Thread.Sleep(100);
                if (!s.IsOpen)
                {
                    OnLog?.Invoke(peer, "[DEBUG] TrySendBinary: datachannel still not open after wait");
                    return false;
                }
            }

            try
            {
                s.SendBinary(data);
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(peer, "SendBinary error: " + ex.Message);
                return false;
            }
        }

        /// <summary>Vérifie si une connexion P2P est établie et prête pour un pair.</summary>
        public static bool IsP2PConnected(string peer)
        {
            if (string.IsNullOrWhiteSpace(peer)) return false;

            IceP2PSession? s = null;
            lock (_gate)
            {
                _sessions.TryGetValue(peer, out s);
            }

            if (s == null) return false;

            bool hasSession = s != null;
            bool isOpen = hasSession && s!.IsOpen;

            // ✅ DEBUG: Console output pour debugging direct
            Console.WriteLine($"🔍 [P2P-CONN] IsP2PConnected({peer}): hasSession={hasSession}, isOpen={isOpen}");
            OnLog?.Invoke(peer, $"[DEBUG] IsP2PConnected: hasSession={hasSession}, isOpen={isOpen}");

            // ✅ FIX: Utiliser même logique que TrySendText - attendre que DataChannel s'ouvre
            if (!isOpen)
            {
                Console.WriteLine($"⏳ [P2P-CONN] IsP2PConnected({peer}): waiting for datachannel to open...");
                OnLog?.Invoke(peer, "[DEBUG] IsP2PConnected: waiting for datachannel to open...");

                // Attendre jusqu'à 2 secondes pour que le datachannel s'ouvre (même logique que TrySendText)
                for (int i = 0; i < 20; i++) // 20 x 100ms = 2 secondes max
                {
                    if (s.IsOpen)
                    {
                        Console.WriteLine($"✅ [P2P-CONN] IsP2PConnected({peer}): datachannel opened after {i * 100}ms");
                        OnLog?.Invoke(peer, $"[DEBUG] IsP2PConnected: datachannel opened after {i * 100}ms");
                        return true;
                    }
                    System.Threading.Thread.Sleep(100);
                }

                Console.WriteLine($"❌ [P2P-CONN] IsP2PConnected({peer}): datachannel still not open after 2s");
                OnLog?.Invoke(peer, "[DEBUG] IsP2PConnected: datachannel still not open after 2s");
                return false; // DataChannel pas ouvert après 2s
            }

            return true; // Session existe ET DataChannel ouvert
        }

        public static void HandleOffer(string fromPeer, string sdp, IEnumerable<string>? stunUrls = null)
        {
            if (string.IsNullOrWhiteSpace(fromPeer) || string.IsNullOrWhiteSpace(sdp)) return;

            // ✅ FIX: Vérifier si answer déjà générée pour ce peer
            lock (_gate)
            {
                if (_answersGenerated.Contains(fromPeer))
                {
                    OnLog?.Invoke(fromPeer, "⚠️ GLOBAL: Answer already generated for this peer, ignoring duplicate offer");
                    return;
                }
            }

            // ✅ FIX: Permettre mode adaptatif (stunUrls = null) au lieu de forcer legacy mode
            // stunUrls ??= new string[] { "stun:stun.l.google.com:19302" }; // Ancien code

            IceP2PSession? sess = null;
            bool exists;
            lock (_gate)
            {
                exists = _sessions.TryGetValue(fromPeer, out sess);
            }

            if (!exists || sess == null)
            {
                sess = new IceP2PSession(stunUrls, "data", isCaller: false);
                WireSessionHandlers(fromPeer, sess);
                lock (_gate)
                {
                    _sessions[fromPeer] = sess;
                }
            }

            try
            {
                // ⚠️ IMPORTANT: démarrer la session côté callee
                // On le fait avant (ou juste après) l'application de l'offer.
                // Si ta classe supporte les 2, ceci est suffisant et idempotent.
                sess.Start();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(fromPeer, "Start (callee) error: " + ex.Message);
            }

            // Applique l'offer distante : la classe créera l'answer + OnLocalSdp("answer", …)
            sess.SetRemoteDescription("offer", sdp);

            // --- enregistrer fingerprint DTLS ---
            try
            {
                var fp = ExtractDtlsFingerprintFromSdp(sdp);
                if (!string.IsNullOrWhiteSpace(fp))
                    LocalDb.SetPeerDtlsFp(fromPeer, fp);
            }
            catch
            {
                // Ignore errors
            }

            // Déroule les candidates reçues avant la session
            FlushPendingCandidates(fromPeer);

            lock (_gate) { _startingPeers.Remove(fromPeer); }
        }

        public static void HandleAnswer(string fromPeer, string sdp)
        {
            if (string.IsNullOrWhiteSpace(fromPeer) || string.IsNullOrWhiteSpace(sdp)) return;

            IceP2PSession? sess = null;
            bool exists;
            lock (_gate)
            {
                exists = _sessions.TryGetValue(fromPeer, out sess);
            }
            
            if (!exists || sess == null)
            {
                OnLog?.Invoke(fromPeer, "Answer reçue mais session introuvable.");
                return;
            }

            sess.SetRemoteDescription("answer", sdp);
            
            // --- enregistrer fingerprint DTLS ---
            try
            {
                var fp = ExtractDtlsFingerprintFromSdp(sdp);
                if (!string.IsNullOrWhiteSpace(fp))
                    LocalDb.SetPeerDtlsFp(fromPeer, fp);
            }
            catch
            {
                // Ignore errors
            }

            FlushPendingCandidates(fromPeer);
            lock (_gate) { _startingPeers.Remove(fromPeer); }
        }

        public static void HandleCandidate(string fromPeer, string candidate)
        {
            if (string.IsNullOrWhiteSpace(fromPeer) || string.IsNullOrWhiteSpace(candidate)) return;

            // dedup IN
            var canon = CanonCandidate(candidate);
            lock (_gate)
            {
                if (!_seenCandIn.TryGetValue(fromPeer, out var setIn) || setIn == null)
                {
                    setIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _seenCandIn[fromPeer] = setIn;
                }
                
                if (setIn.Contains(canon))
                {
                    OnLog?.Invoke(fromPeer, "Candidate ignorée (dupli IN).");
                    return;
                }
                
                setIn.Add(canon);
                if (setIn.Count > 128)
                    setIn.Remove(setIn.First());
            }

            IceP2PSession? sess = null;
            bool exists;
            lock (_gate)
            {
                exists = _sessions.TryGetValue(fromPeer, out sess);
            }

            if (!exists || sess == null)
            {
                // queue en attendant la session
                lock (_gate)
                {
                    if (!_pendingCands.TryGetValue(fromPeer, out var list) || list == null)
                    {
                        list = new List<string>();
                        _pendingCands[fromPeer] = list;
                    }
                    list.Add(candidate);
                }
                return;
            }

            try
            {
                sess.AddRemoteCandidate(candidate);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(fromPeer, "AddRemoteCandidate error: " + ex.Message);
            }
        }

        // ======== handlers internes ========
        private static void WireSessionHandlers(string peer, IceP2PSession sess)
        {
            // 1) SDP locale (offer/answer) => signal vers l'autre
            sess.OnLocalSdp += (kind, localSdp) =>
            {
                try
                {
                    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(localSdp));
                    if (string.Equals(kind, "answer", StringComparison.OrdinalIgnoreCase))
                    {
                        // ✅ FIX: Marquer ce peer comme ayant généré une answer
                        lock (_gate)
                        {
                            _answersGenerated.Add(peer);
                            OnLog?.Invoke(peer, $"✅ GLOBAL: Answer generated and peer marked - no more answers allowed");
                        }

                        _sendSignal?.Invoke(peer, $"{TAG_ICE_ANSWER}{_localName}:{peer}:{b64}");
                    }
                    else
                    {
                        _sendSignal?.Invoke(peer, $"{TAG_ICE_OFFER}{_localName}:{peer}:{b64}");
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke(peer, "[signal] OnLocalSdp error: " + ex.Message);
                }
            };

            // 2) Candidates locales => dedup OUT + signal
            sess.OnLocalCandidate += (cand) =>
            {
                try
                {
                    var canon = CanonCandidate(cand);
                    if (IsDupCandidateOut(peer, canon)) return;
                    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cand));
                    _sendSignal?.Invoke(peer, $"{TAG_ICE_CAND}{_localName}:{peer}:{b64}");
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke(peer, "[signal] OnLocalCandidate error: " + ex.Message);
                }
            };

            // 3) Etat ICE
            sess.OnIceStateChanged += (st) =>
            {
                OnLog?.Invoke(peer, $"[DEBUG] ICE State changed to: {st}");
                switch (st)
                {
                    case RTCIceConnectionState.connected:
                        OnLog?.Invoke(peer, "[DEBUG] Triggering OnP2PState(True)");
                        OnP2PState?.Invoke(peer, true);
                        break;
                    case RTCIceConnectionState.failed:
                    case RTCIceConnectionState.disconnected:
                    case RTCIceConnectionState.closed:
                        OnLog?.Invoke(peer, $"[DEBUG] Triggering OnP2PState(False) for state: {st}");
                        OnP2PState?.Invoke(peer, false);
                        break;
                }
            };

            // 4) Messages texte P2P
            sess.OnTextMessage += (txt) =>
            {
                OnP2PText?.Invoke(peer, txt);
            };

            // 5) Messages binaires P2P (pour fichiers)
            sess.OnBinaryMessage += (data) =>
            {
                OnP2PBinary?.Invoke(peer, data);
            };

            // 6) Logs de négo détaillés
            sess.OnNegotiationLog += (l) =>
            {
                OnLog?.Invoke(peer, l);
            };
        }

        private static void ResetDedup(string peer)
        {
            lock (_gate)
            {
                _seenCandOut.Remove(peer);
                _seenCandIn.Remove(peer);

                // ✅ FIX: Reset aussi la protection answer pour permettre nouvelle négociation
                _answersGenerated.Remove(peer);
                OnLog?.Invoke(peer, "🔄 GLOBAL: Answer protection reset for new negotiation");
            }
        }

        private static void FlushPendingCandidates(string peer)
        {
            List<string>? listToApply = null;
            lock (_gate)
            {
                if (_pendingCands.TryGetValue(peer, out var list) && list != null && list.Count > 0)
                {
                    listToApply = new List<string>(list);
                    _pendingCands.Remove(peer);
                }
            }

            if (listToApply == null || listToApply.Count == 0) return;

            IceP2PSession? sess = null;
            bool exists;
            lock (_gate)
            {
                exists = _sessions.TryGetValue(peer, out sess);
            }
            
            if (sess == null)
            {
                OnLog?.Invoke(peer, "Flush pending ignoré (session absente).");
                return;
            }

            foreach (var c in listToApply)
            {
                try
                {
                    sess.AddRemoteCandidate(c);
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke(peer, "Flush cand erreur: " + ex.Message);
                }
            }
        }

        private static bool IsDupCandidateOut(string peer, string canon)
        {
            lock (_gate)
            {
                if (!_seenCandOut.TryGetValue(peer, out var setOut) || setOut == null)
                {
                    setOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _seenCandOut[peer] = setOut;
                }
                
                if (setOut.Contains(canon))
                {
                    OnLog?.Invoke(peer, "Local candidate ignorée (dupli OUT).");
                    return true;
                }
                
                setOut.Add(canon);
                if (setOut.Count > 128)
                    setOut.Remove(setOut.First());
                return false;
            }
        }

        private static string CanonCandidate(string c)
        {
            if (string.IsNullOrWhiteSpace(c)) return "";
            return c.Trim();
        }

        // ===== Extraction fingerprint DTLS depuis SDP =====
        private static string? ExtractDtlsFingerprintFromSdp(string sdp)
        {
            if (string.IsNullOrWhiteSpace(sdp)) return null;
            var t = sdp.Replace("\r", "");
            foreach (var raw in t.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("a=fingerprint:", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring("a=fingerprint:".Length).Trim();
                }
            }
            return null;
        }

        private static void SafeClose(IceP2PSession? sess)
        {
            if (sess == null) return;
            try
            {
                // Tente d'appeler DisposeAsync() si présent (IAsyncDisposable)
                try
                {
                    var mi = sess.GetType().GetMethod("DisposeAsync", Type.EmptyTypes);
                    if (mi != null)
                    {
                        // Invoke retourne un ValueTask ; on l'ignore volontairement
                        var _ignored = mi.Invoke(sess, null);
                    }
                }
                catch
                {
                    // on ignore toute erreur de réflexion
                }

                // Puis tente un Dispose() classique si dispo
                if (sess is IDisposable disp)
                {
                    disp.Dispose();
                }
            }
            catch
            {
                // on avale toute exception de fermeture
            }
        }
    }
}