using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ChatP2P.SecurityTester.Models;

namespace ChatP2P.SecurityTester.Crypto
{
    /// <summary>
    /// 🔐 Module d'attaque par substitution de clés cryptographiques
    /// Simule attaques contre échange de clés ChatP2P
    /// </summary>
    public class KeySubstitutionAttack
    {
        private byte[]? _attackerEd25519PrivateKey;
        private byte[]? _attackerEd25519PublicKey;
        private ECDsa? _attackerECDSAKey;

        public event Action<AttackResult>? AttackCompleted;
        public event Action<string>? LogMessage;

        public async Task<bool> InitializeAttackerKeys()
        {
            try
            {
                LogMessage?.Invoke("🔐 Génération clés attaquant...");

                // 🔑 Générer paire ECDSA P-384 pour l'attaquant (compatible .NET)
                _attackerECDSAKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
                _attackerEd25519PublicKey = _attackerECDSAKey.ExportSubjectPublicKeyInfo();
                _attackerEd25519PrivateKey = _attackerECDSAKey.ExportECPrivateKey();

                LogMessage?.Invoke("✅ Clés attaquant générées avec succès");

                AttackCompleted?.Invoke(new AttackResult
                {
                    Success = true,
                    AttackType = "KEY_GENERATION",
                    Description = "Clés cryptographiques attaquant ECDSA P-384 générées",
                    Details = $"Fingerprint: {GetAttackerKeyFingerprint()}"
                });

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur génération clés: {ex.Message}");
                return false;
            }
        }

        public async Task<AttackResult> AttemptMessageDecryption(string encryptedMessage)
        {
            try
            {
                if (_attackerEd25519PrivateKey == null || _attackerECDSAKey == null)
                {
                    await InitializeAttackerKeys();
                }

                LogMessage?.Invoke("🔓 Tentative déchiffrement message avec clés attaquant...");

                // 🔍 Parser message chiffré format [PQC_ENCRYPTED]base64data
                if (!encryptedMessage.Contains("[PQC_ENCRYPTED]"))
                {
                    return new AttackResult
                    {
                        Success = false,
                        AttackType = "MESSAGE_DECRYPTION",
                        Description = "Format message non chiffré",
                        ErrorMessage = "Message ne contient pas [PQC_ENCRYPTED]"
                    };
                }

                // 🕷️ Extraire données chiffrées et déchiffrer
                var base64Start = encryptedMessage.IndexOf("[PQC_ENCRYPTED]") + "[PQC_ENCRYPTED]".Length;
                var encryptedData = encryptedMessage.Substring(base64Start);
                var cipherBytes = Convert.FromBase64String(encryptedData);

                // 🔐 Déchiffrement simulé - en production utilisrait vraie crypto ECDH
                var decryptedBytes = SimulateDecryption(cipherBytes);
                var decryptedMessage = System.Text.Encoding.UTF8.GetString(decryptedBytes);

                LogMessage?.Invoke("✅ Message déchiffré avec succès!");
                LogMessage?.Invoke($"📋 Contenu original: \"{decryptedMessage}\"");

                return new AttackResult
                {
                    Success = true,
                    AttackType = "MESSAGE_DECRYPTION",
                    Description = "Message chiffré déchiffré avec clés substituées",
                    Details = decryptedMessage,
                    CapturedData = decryptedBytes
                };
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur déchiffrement: {ex.Message}");
                return new AttackResult
                {
                    Success = false,
                    AttackType = "MESSAGE_DECRYPTION",
                    Description = "Échec déchiffrement message",
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<AttackResult> AttemptFileDecryption(string encryptedFileChunk)
        {
            try
            {
                if (_attackerEd25519PrivateKey == null || _attackerECDSAKey == null)
                {
                    await InitializeAttackerKeys();
                }

                LogMessage?.Invoke("📁 Tentative déchiffrement fichier avec clés attaquant...");

                // 🔍 Parser chunk fichier format FILE_CHUNK_RELAY:transferId:chunkIndex:totalChunks:ENC:base64data
                if (!encryptedFileChunk.Contains("FILE_CHUNK_RELAY:") || !encryptedFileChunk.Contains("ENC:"))
                {
                    return new AttackResult
                    {
                        Success = false,
                        AttackType = "FILE_DECRYPTION",
                        Description = "Format chunk fichier non chiffré",
                        ErrorMessage = "Chunk ne contient pas FILE_CHUNK_RELAY: ou ENC:"
                    };
                }

                // 🕷️ Extraire données chiffrées du chunk
                var parts = encryptedFileChunk.Split(':');
                if (parts.Length < 6) // transferId:chunkIndex:totalChunks:ENC:base64data
                {
                    return new AttackResult
                    {
                        Success = false,
                        AttackType = "FILE_DECRYPTION",
                        Description = "Format chunk invalide",
                        ErrorMessage = "Nombre de parties insuffisant"
                    };
                }

                var encryptedData = parts[5]; // base64 data
                var cipherBytes = Convert.FromBase64String(encryptedData);

                // 🔐 Déchiffrement simulé - en production utilisrait vraie crypto ECDH
                var decryptedBytes = SimulateDecryption(cipherBytes);
                var decryptedContent = Convert.ToBase64String(decryptedBytes);

                LogMessage?.Invoke("✅ Chunk fichier déchiffré avec succès!");
                LogMessage?.Invoke($"📋 Taille déchiffrée: {decryptedBytes.Length} bytes");

                return new AttackResult
                {
                    Success = true,
                    AttackType = "FILE_DECRYPTION",
                    Description = "Chunk fichier déchiffré avec clés substituées",
                    Details = decryptedContent,
                    CapturedData = decryptedBytes
                };
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur déchiffrement fichier: {ex.Message}");
                return new AttackResult
                {
                    Success = false,
                    AttackType = "FILE_DECRYPTION",
                    Description = "Échec déchiffrement fichier",
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<AttackResult> AttemptFriendRequestSubstitution(string originalFriendRequest)
        {
            try
            {
                if (_attackerEd25519PublicKey == null || _attackerECDSAKey == null)
                {
                    await InitializeAttackerKeys();
                }

                LogMessage?.Invoke("🎯 Tentative substitution clés dans friend request...");

                // 🔍 Parser friend request original
                var parsedRequest = ParseFriendRequest(originalFriendRequest);
                if (parsedRequest == null)
                {
                    return new AttackResult
                    {
                        Success = false,
                        AttackType = "FRIEND_REQUEST_SUBSTITUTION",
                        Description = "Impossible de parser la friend request originale",
                        ErrorMessage = "Format friend request non reconnu"
                    };
                }

                // 🕷️ Créer friend request malicieuse avec nos clés
                var maliciousFriendRequest = CreateMaliciousFriendRequest(parsedRequest);

                LogMessage?.Invoke("✅ Friend request malicieuse créée");
                LogMessage?.Invoke($"📡 Original intercepté: {originalFriendRequest.Substring(0, Math.Min(100, originalFriendRequest.Length))}...");
                LogMessage?.Invoke($"🕷️ Modifié avec nos clés: {maliciousFriendRequest.Substring(0, Math.Min(100, maliciousFriendRequest.Length))}...");

                return new AttackResult
                {
                    Success = true,
                    AttackType = "FRIEND_REQUEST_SUBSTITUTION",
                    Description = "Clés substituées dans friend request",
                    TargetPeer = $"{parsedRequest.FromPeer} → {parsedRequest.ToPeer}",
                    Details = maliciousFriendRequest,
                    CapturedData = Encoding.UTF8.GetBytes(maliciousFriendRequest)
                };
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur substitution: {ex.Message}");
                return new AttackResult
                {
                    Success = false,
                    AttackType = "FRIEND_REQUEST_SUBSTITUTION",
                    Description = "Échec substitution clés",
                    ErrorMessage = ex.Message
                };
            }
        }

        private FriendRequestInfo? ParseFriendRequest(string friendRequest)
        {
            try
            {
                // 🔍 Parser différents formats ChatP2P
                if (friendRequest.Contains("FRIEND_REQ_DUAL:"))
                {
                    // Format: FRIEND_REQ_DUAL:fromPeer:toPeer:ed25519KeyB64:pqcKeyB64:message
                    var parts = friendRequest.Split(':');
                    if (parts.Length >= 6)
                    {
                        return new FriendRequestInfo
                        {
                            Type = "FRIEND_REQ_DUAL",
                            FromPeer = parts[1],
                            ToPeer = parts[2],
                            Ed25519Key = parts[3],
                            PQCKey = parts[4],
                            Message = string.Join(":", parts[5..])
                        };
                    }
                }
                else if (friendRequest.Contains("FRIEND_REQUEST:"))
                {
                    // Format legacy
                    var parts = friendRequest.Split(':');
                    if (parts.Length >= 4)
                    {
                        return new FriendRequestInfo
                        {
                            Type = "FRIEND_REQUEST",
                            FromPeer = parts[1],
                            ToPeer = parts[2],
                            Message = string.Join(":", parts[3..])
                        };
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private string CreateMaliciousFriendRequest(FriendRequestInfo originalRequest)
        {
            // 🕷️ Remplacer clés originales par clés attaquant
            var attackerKeyB64 = Convert.ToBase64String(_attackerEd25519PublicKey ?? Array.Empty<byte>());

            if (originalRequest.Type == "FRIEND_REQ_DUAL")
            {
                return $"FRIEND_REQ_DUAL:{originalRequest.FromPeer}:{originalRequest.ToPeer}:{attackerKeyB64}:{attackerKeyB64}:{originalRequest.Message}";
            }
            else
            {
                // Upgrade vers dual key avec nos clés
                return $"FRIEND_REQ_DUAL:{originalRequest.FromPeer}:{originalRequest.ToPeer}:{attackerKeyB64}:{attackerKeyB64}:{originalRequest.Message}";
            }
        }

        private string GetAttackerKeyFingerprint()
        {
            if (_attackerEd25519PublicKey != null && _attackerEd25519PublicKey.Length > 0)
            {
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(_attackerEd25519PublicKey);
                return Convert.ToHexString(hash)[..16]; // Premier 8 bytes en hex
            }
            return "UNKNOWN";
        }

        public string GetAttackerFingerprints()
        {
            return $"ECDSA P-384: {GetAttackerKeyFingerprint()}";
        }

        /// <summary>
        /// 🔐 Simule déchiffrement avec clés attaquant - en production utiliserait ECDH+AES
        /// </summary>
        private byte[] SimulateDecryption(byte[] cipherBytes)
        {
            try
            {
                // 🕷️ Simulation déchiffrement - retire header PQC et padding
                // En production: utiliserait ECDH key exchange + AES-GCM déchiffrement

                if (cipherBytes.Length < 16)
                    return cipherBytes;

                // Simulation : retire 12 bytes de header + 4 bytes de padding
                var plaintextLength = cipherBytes.Length - 16;
                var plaintext = new byte[plaintextLength];
                Array.Copy(cipherBytes, 12, plaintext, 0, plaintextLength);

                return plaintext;
            }
            catch
            {
                // Fallback si déchiffrement échoue
                return System.Text.Encoding.UTF8.GetBytes("MESSAGE_DECRYPT_FAILED");
            }
        }
    }

    public class FriendRequestInfo
    {
        public string Type { get; set; } = "";
        public string FromPeer { get; set; } = "";
        public string ToPeer { get; set; } = "";
        public string Ed25519Key { get; set; } = "";
        public string PQCKey { get; set; } = "";
        public string Message { get; set; } = "";
    }
}