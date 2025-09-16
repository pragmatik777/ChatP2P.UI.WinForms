using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatP2P.Client
{
    /// <summary>
    /// Tunnel PQC sécurisé pour protéger l'échange initial de clés via relay server
    /// Utilise ECDH P-384 + AES-GCM pour chiffrer les friend requests
    /// </summary>
    public class SecureRelayTunnel
    {
        private readonly string _localDisplayName;
        private byte[]? _tunnelPrivateKey;
        private byte[]? _tunnelPublicKey;
        private readonly Dictionary<string, byte[]> _peerPublicKeys = new();
        private bool _isSecureChannelEstablished = false;

        // Callback pour envoyer des messages via RelayClient
        private Func<string, Task>? _sendMessageCallback;

        // Événement pour notifier l'interface d'une friend request sécurisée reçue
        public event Action<string, string, string, string>? SecureFriendRequestReceived;

        public SecureRelayTunnel(string localDisplayName)
        {
            _localDisplayName = localDisplayName;
        }

        public void SetSendMessageCallback(Func<string, Task> callback)
        {
            _sendMessageCallback = callback;
        }

        public bool IsSecureChannelEstablished => _isSecureChannelEstablished;

        /// <summary>
        /// Étape 1: Initialiser notre tunnel P2P (générer nos clés)
        /// </summary>
        public async Task<bool> EstablishSecureChannelAsync(NetworkStream relayStream)
        {
            try
            {
                await LogTunnel("🔐 [SECURE-TUNNEL] Initializing P2P tunnel keys...");

                // 1. Générer notre paire de clés pour les communications P2P
                var keyPair = await CryptoService.GenerateKeyPair();
                _tunnelPrivateKey = keyPair.PrivateKey;
                _tunnelPublicKey = keyPair.PublicKey;

                await LogTunnel($"🔑 [SECURE-TUNNEL] Generated tunnel keys for {_localDisplayName}");
                await LogTunnel($"🔑 [SECURE-TUNNEL] Public key: {Convert.ToBase64String(_tunnelPublicKey).Substring(0, 40)}...");

                _isSecureChannelEstablished = true;
                await LogTunnel("✅ [SECURE-TUNNEL] P2P tunnel ready for key exchange");

                return true;
            }
            catch (Exception ex)
            {
                await LogTunnel($"❌ [SECURE-TUNNEL] Tunnel initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Étape 2: Envoyer friend request chiffrée P2P via tunnel sécurisé
        /// </summary>
        public async Task<bool> SendSecureFriendRequestAsync(NetworkStream relayStream, string toPeer, string ed25519Key, string pqcKey, string message)
        {
            if (!_isSecureChannelEstablished || _tunnelPrivateKey == null || _tunnelPublicKey == null)
            {
                await LogTunnel("❌ [SECURE-TUNNEL] P2P tunnel not ready");
                return false;
            }

            try
            {
                // 1. D'abord envoyer notre clé publique au destinataire via le serveur (en clair)
                await LogTunnel($"🔑 [SECURE-TUNNEL] Sending public key exchange to {toPeer}...");

                var keyExchange = new
                {
                    type = "TUNNEL_KEY_EXCHANGE",
                    fromPeer = _localDisplayName,
                    toPeer = toPeer,
                    publicKey = Convert.ToBase64String(_tunnelPublicKey)
                };

                var keyExchangeJson = JsonSerializer.Serialize(keyExchange);

                using var writer = new StreamWriter(relayStream, Encoding.UTF8, leaveOpen: true);
                await writer.WriteLineAsync($"?{keyExchangeJson}");
                await writer.FlushAsync();

                // 2. Attendre la clé publique du destinataire (sera traitée par HandlePeerKeyExchange)
                await LogTunnel("⏳ [SECURE-TUNNEL] Waiting for peer's public key...");

                // Attendre un peu pour que l'échange de clés se fasse
                var maxWait = 50; // 5 secondes max
                while (!_peerPublicKeys.ContainsKey(toPeer) && maxWait > 0)
                {
                    await Task.Delay(100);
                    maxWait--;
                }

                if (!_peerPublicKeys.ContainsKey(toPeer))
                {
                    await LogTunnel($"❌ [SECURE-TUNNEL] Failed to receive public key from {toPeer}");
                    return false;
                }

                // 3. Maintenant chiffrer avec la clé publique du destinataire
                await LogTunnel($"🔐 [SECURE-TUNNEL] Encrypting friend request with {toPeer}'s public key...");

                var friendRequest = new
                {
                    type = "SECURE_FRIEND_REQUEST",
                    fromPeer = _localDisplayName,
                    toPeer = toPeer,
                    ed25519Key = ed25519Key,
                    pqcKey = pqcKey,
                    message = message,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                var plaintext = JsonSerializer.Serialize(friendRequest);
                var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

                // Chiffrer avec la clé publique du destinataire
                var peerPublicKey = _peerPublicKeys[toPeer];
                var encryptedBytes = await CryptoService.EncryptMessage(plaintextBytes, peerPublicKey);

                // 4. Envoyer la friend request chiffrée
                var tunnelMessage = new
                {
                    type = "SECURE_TUNNEL_MESSAGE",
                    fromPeer = _localDisplayName,
                    toPeer = toPeer,
                    encryptedData = Convert.ToBase64String(encryptedBytes)
                };

                var tunnelJson = JsonSerializer.Serialize(tunnelMessage);

                await writer.WriteLineAsync($"?{tunnelJson}");
                await writer.FlushAsync();

                await LogTunnel($"✅ [SECURE-TUNNEL] Secure friend request sent to {toPeer} ({encryptedBytes.Length} bytes encrypted)");
                return true;
            }
            catch (Exception ex)
            {
                await LogTunnel($"❌ [SECURE-TUNNEL] Failed to send secure friend request: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Étape 3: Recevoir et déchiffrer friend request via tunnel sécurisé P2P
        /// </summary>
        public async Task<(bool success, string fromPeer, string ed25519Key, string pqcKey, string message)> ReceiveSecureFriendRequestAsync(byte[] encryptedData, string fromPeer)
        {
            if (!_isSecureChannelEstablished || _tunnelPrivateKey == null)
            {
                await LogTunnel("❌ [SECURE-TUNNEL] P2P tunnel not ready for receiving");
                return (false, "", "", "", "");
            }

            if (!_peerPublicKeys.ContainsKey(fromPeer))
            {
                await LogTunnel($"❌ [SECURE-TUNNEL] No public key available for {fromPeer}");
                return (false, "", "", "", "");
            }

            try
            {
                await LogTunnel($"🔓 [SECURE-TUNNEL] Decrypting received friend request ({encryptedData.Length} bytes) from {fromPeer}...");

                // 1. Déchiffrer avec notre clé privée (l'expéditeur a chiffré avec notre clé publique)
                var decryptedBytes = await CryptoService.DecryptMessageBytes(encryptedData, _tunnelPrivateKey);
                var decryptedJson = Encoding.UTF8.GetString(decryptedBytes);

                // 2. Parser friend request
                var friendRequest = JsonSerializer.Deserialize<JsonElement>(decryptedJson);
                var type = friendRequest.GetProperty("type").GetString();

                if (type == "SECURE_FRIEND_REQUEST")
                {
                    var senderPeer = friendRequest.GetProperty("fromPeer").GetString()!;
                    var ed25519Key = friendRequest.GetProperty("ed25519Key").GetString()!;
                    var pqcKey = friendRequest.GetProperty("pqcKey").GetString()!;
                    var message = friendRequest.GetProperty("message").GetString()!;

                    await LogTunnel($"✅ [SECURE-TUNNEL] Decrypted friend request from {senderPeer}");
                    return (true, senderPeer, ed25519Key, pqcKey, message);
                }

                await LogTunnel("❌ [SECURE-TUNNEL] Invalid decrypted message type");
                return (false, "", "", "", "");
            }
            catch (Exception ex)
            {
                await LogTunnel($"❌ [SECURE-TUNNEL] Failed to decrypt friend request: {ex.Message}");
                return (false, "", "", "", "");
            }
        }

        /// <summary>
        /// NOUVEAU: Gestion des messages de tunnel relayés par le serveur
        /// </summary>
        public async Task HandleRelayedTunnelMessage(string jsonMessage)
        {
            try
            {
                await LogTunnel($"🔐 [TUNNEL-RELAY] Handling relayed tunnel message from server");

                var tunnelData = JsonSerializer.Deserialize<JsonElement>(jsonMessage);
                var messageType = tunnelData.GetProperty("type").GetString();

                switch (messageType)
                {
                    case "TUNNEL_KEY_EXCHANGE":
                        await HandlePeerKeyExchange(tunnelData);
                        break;

                    case "SECURE_TUNNEL_MESSAGE":
                        await HandleEncryptedTunnelMessage(tunnelData);
                        break;

                    default:
                        await LogTunnel($"❌ [TUNNEL-RELAY] Unknown tunnel message type: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                await LogTunnel($"❌ [TUNNEL-RELAY] Error handling relayed tunnel message: {ex.Message}");
            }
        }

        /// <summary>
        /// Gérer l'échange de clés publiques avec un peer
        /// </summary>
        private async Task HandlePeerKeyExchange(JsonElement keyData)
        {
            try
            {
                var fromPeer = keyData.GetProperty("fromPeer").GetString();
                var toPeer = keyData.GetProperty("toPeer").GetString();
                var publicKeyB64 = keyData.GetProperty("publicKey").GetString();

                // Vérifier que ce message nous est destiné
                if (toPeer != _localDisplayName)
                {
                    await LogTunnel($"🔑 [KEY-EXCHANGE] Key exchange not for us (to: {toPeer})");
                    return;
                }

                await LogTunnel($"🔑 [KEY-EXCHANGE] Received public key from {fromPeer}");

                // Vérifier si on a déjà cette clé exacte (éviter boucle infinie)
                var publicKey = Convert.FromBase64String(publicKeyB64!);
                var hadSameKey = false;

                if (_peerPublicKeys.ContainsKey(fromPeer!))
                {
                    var existingKey = _peerPublicKeys[fromPeer!];
                    hadSameKey = existingKey.SequenceEqual(publicKey);
                }

                // Stocker la clé publique du peer seulement si différente
                if (!hadSameKey)
                {
                    _peerPublicKeys[fromPeer!] = publicKey;
                    await LogTunnel($"✅ [KEY-EXCHANGE] Stored new public key for {fromPeer}");
                }
                else
                {
                    await LogTunnel($"🔄 [KEY-EXCHANGE] Same key already stored for {fromPeer}");
                }

                // NOUVEAU: Envoyer automatiquement notre clé publique en réponse SEULEMENT si on n'avait pas la même clé
                if (_tunnelPublicKey != null && !hadSameKey)
                {
                    await LogTunnel($"🔑 [KEY-EXCHANGE] Sending our public key to {fromPeer} (first time)");

                    var responseKeyExchange = new
                    {
                        type = "TUNNEL_KEY_EXCHANGE",
                        fromPeer = _localDisplayName,
                        toPeer = fromPeer,
                        publicKey = Convert.ToBase64String(_tunnelPublicKey)
                    };

                    var responseJson = JsonSerializer.Serialize(responseKeyExchange);

                    // Envoyer via le callback au RelayClient
                    if (_sendMessageCallback != null)
                    {
                        await _sendMessageCallback($"?{responseJson}");
                        await LogTunnel($"✅ [KEY-EXCHANGE] Response sent to {fromPeer}");
                    }
                    else
                    {
                        await LogTunnel($"❌ [KEY-EXCHANGE] No callback available to send response");
                    }
                }
                else
                {
                    await LogTunnel($"🔄 [KEY-EXCHANGE] Same key already known for {fromPeer}, no response needed");
                }
            }
            catch (Exception ex)
            {
                await LogTunnel($"❌ [KEY-EXCHANGE] Error handling key exchange: {ex.Message}");
            }
        }

        /// <summary>
        /// Gérer les messages chiffrés du tunnel
        /// </summary>
        private async Task HandleEncryptedTunnelMessage(JsonElement tunnelData)
        {
            try
            {
                var encryptedDataB64 = tunnelData.GetProperty("encryptedData").GetString();
                var fromPeer = tunnelData.GetProperty("fromPeer").GetString();
                var encryptedBytes = Convert.FromBase64String(encryptedDataB64!);

                await LogTunnel($"🔓 [TUNNEL-RELAY] Decrypting message from {fromPeer} ({encryptedBytes.Length} bytes)");

                // Vérifier qu'on a la clé publique de l'expéditeur
                if (!_peerPublicKeys.ContainsKey(fromPeer!))
                {
                    await LogTunnel($"❌ [TUNNEL-RELAY] No public key available for {fromPeer}");
                    return;
                }

                // Déchiffrer avec la clé publique de l'expéditeur
                var (success, sender, ed25519Key, pqcKey, message) = await ReceiveSecureFriendRequestAsync(encryptedBytes, fromPeer!);

                if (success)
                {
                    await LogTunnel($"✅ [TUNNEL-RELAY] Successfully decrypted friend request from {sender}");

                    Console.WriteLine($"🎉 [TUNNEL-RELAY] Decrypted friend request: {sender} → {_localDisplayName}");
                    Console.WriteLine($"📋 [TUNNEL-RELAY] Ed25519: {ed25519Key.Substring(0, 20)}...");
                    Console.WriteLine($"📋 [TUNNEL-RELAY] PQC: {pqcKey.Substring(0, 20)}...");
                    Console.WriteLine($"💬 [TUNNEL-RELAY] Message: {message}");

                    // Déclencher l'événement pour notifier l'interface utilisateur
                    SecureFriendRequestReceived?.Invoke(sender, _localDisplayName, ed25519Key, message);
                    await LogTunnel($"📢 [TUNNEL-RELAY] UI notified of secure friend request from {sender}");
                }
                else
                {
                    await LogTunnel($"❌ [TUNNEL-RELAY] Failed to decrypt tunnel message from {fromPeer}");
                }
            }
            catch (Exception ex)
            {
                await LogTunnel($"❌ [TUNNEL-RELAY] Error handling encrypted tunnel message: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset tunnel - force nouveau handshake
        /// </summary>
        public void ResetTunnel()
        {
            _isSecureChannelEstablished = false;
            _tunnelPrivateKey = null;
            _tunnelPublicKey = null;
            _peerPublicKeys.Clear();
        }

        private async Task LogTunnel(string message)
        {
            try
            {
                var logDir = @"C:\Users\User\Desktop\ChatP2P_Logs";
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir, "secure_tunnel.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(logFile, logEntry);
                Console.WriteLine(message);
            }
            catch { /* Ignore log errors */ }
        }
    }
}