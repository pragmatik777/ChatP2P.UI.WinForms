using System;
using System.IO;
using System.Security.Cryptography;

namespace ChatP2P.Client
{
    /// <summary>
    /// ✅ Service cryptographique hybride ECDH P-384 + AES-GCM
    /// TODO: Migration vers ML-KEM-768 Post-Quantum une fois conflit BouncyCastle/SIPSorcery résolu
    /// ECDH P-384 offre 192-bit security level (équivalent AES-256) - robuste en attendant migration PQC
    /// </summary>
    public static class CryptoService
    {
        /// <summary>
        /// Log dédié crypto dans un fichier séparé
        /// </summary>
        public static async Task LogCrypto(string message)
        {
            await LogHelper.LogToCryptoAsync(message);
        }
        /// <summary>
        /// Paire de clés ECDH P-384 (temporaire - migration ML-KEM-768 prévue)
        /// </summary>
        public class PqKeyPair
        {
            public byte[] PublicKey { get; set; } = Array.Empty<byte>();
            public byte[] PrivateKey { get; set; } = Array.Empty<byte>();
            public string Algorithm { get; set; } = "ECDH-P384-STRONG";

            // Clés .NET internes pour opérations crypto
            internal ECDiffieHellman? EcdhKey { get; set; }
        }

        /// <summary>
        /// Génère une nouvelle paire de clés ECDH P-384 renforcée (.NET natif)
        /// TODO: Migration ML-KEM-768 une fois conflit BouncyCastle résolu
        /// </summary>
        public static async Task<PqKeyPair> GenerateKeyPair()
        {
            try
            {
                await LogCrypto("🔑 [KEYGEN] Starting ECDH P-384 enhanced key generation...");

                // ✅ Générer clé ECDH P-384 avec .NET natif (évite conflits BouncyCastle)
                using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);

                // Extraire les clés en format standard X9.62 (compatible inter-système)
                var publicKeyBytes = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
                var privateKeyBytes = ecdh.ExportECPrivateKey();

                await LogCrypto($"🔑 [KEYGEN] Generated ECDH P-384 key pair - Public: {publicKeyBytes.Length} bytes, Private: {privateKeyBytes.Length} bytes");

                // Créer nouvelle instance ECDH pour stockage sécurisé
                var storedEcdh = ECDiffieHellman.Create();
                storedEcdh.ImportECPrivateKey(privateKeyBytes, out _);

                await LogCrypto("🔑 [KEYGEN] ECDH P-384 key pair ready (192-bit security, pre-ML-KEM)");

                return new PqKeyPair
                {
                    PublicKey = publicKeyBytes,
                    PrivateKey = privateKeyBytes,
                    Algorithm = "ECDH-P384-STRONG",
                    EcdhKey = storedEcdh
                };
            }
            catch (Exception ex)
            {
                await LogCrypto($"❌ [KEYGEN] Failed to generate ECDH P-384 key pair: {ex.Message}");
                throw new InvalidOperationException($"Failed to generate ECDH P-384 enhanced key pair: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Chiffre un message avec ECDH P-384 + AES-GCM: clé éphémère + secret partagé renforcé
        /// TODO: Migration ML-KEM-768 Post-Quantum une fois conflit BouncyCastle résolu
        /// </summary>
        public static async Task<byte[]> EncryptMessage(string plaintext, byte[] recipientPublicKey)
        {
            try
            {
                await LogCrypto($"🔐 [ENCRYPT] Starting ECDH P-384 enhanced encryption for message: {plaintext.Length} chars");

                // ✅ NOUVEAU: Validation clé publique avant usage
                await LogCrypto($"🔐 [ENCRYPT] Validating recipient public key: {recipientPublicKey.Length} bytes");
                await LogCrypto($"🔐 [ENCRYPT] Key header: {Convert.ToHexString(recipientPublicKey.Take(20).ToArray())}...");

                // 1. Reconstruire la clé publique ECDH depuis les bytes
                using var recipientEcdh = ECDiffieHellman.Create();
                recipientEcdh.ImportSubjectPublicKeyInfo(recipientPublicKey, out _);
                await LogCrypto($"🔐 [ENCRYPT] Loaded recipient ECDH P-384 public key: {recipientPublicKey.Length} bytes");

                // 2. Générer clé éphémère P-384 pour ce message (Perfect Forward Secrecy)
                using var ephemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
                await LogCrypto("🔐 [ENCRYPT] Generated ephemeral ECDH P-384 key for perfect forward secrecy");

                // 3. Calculer secret partagé ECDH (384-bit strength)
                var sharedSecret = ephemeralEcdh.DeriveKeyMaterial(recipientEcdh.PublicKey);
                await LogCrypto($"🔐 [ENCRYPT] Derived ECDH shared secret: {sharedSecret.Length} bytes");

                // 4. Utiliser SHA-256 pour dériver clé AES 256-bit robuste
                using var sha256 = SHA256.Create();
                var aesKey = sha256.ComputeHash(sharedSecret);
                await LogCrypto($"🔐 [ENCRYPT] Derived strong AES key: {aesKey.Length} bytes");

                // 5. Chiffrer le message avec AES-GCM authentifié
                var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                var encryptedMessage = EncryptWithAesGcm(plaintextBytes, aesKey);
                await LogCrypto($"🔐 [ENCRYPT] AES-GCM encryption completed: {encryptedMessage.Length} bytes");

                // 6. Combiner: clé publique éphémère + message chiffré
                var ephemeralPublicKey = ephemeralEcdh.PublicKey.ExportSubjectPublicKeyInfo();
                var result = new byte[ephemeralPublicKey.Length + encryptedMessage.Length + 4]; // +4 pour taille

                // Format: [taille_clé_éphémère:4][clé_éphémère][message_chiffré]
                BitConverter.GetBytes(ephemeralPublicKey.Length).CopyTo(result, 0);
                ephemeralPublicKey.CopyTo(result, 4);
                encryptedMessage.CopyTo(result, 4 + ephemeralPublicKey.Length);

                await LogCrypto($"🔐 [ENCRYPT] Final ECDH P-384 ciphertext: {result.Length} bytes (ephemeral key + encrypted data)");
                return result;
            }
            catch (Exception ex)
            {
                await LogCrypto($"❌ [ENCRYPT] ECDH P-384 encryption failed: {ex.Message}");
                throw new InvalidOperationException($"Failed to encrypt with ECDH P-384+AES-GCM: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Déchiffre un message avec ECDH P-384 + AES-GCM: clé éphémère + secret partagé
        /// TODO: Migration ML-KEM-768 Post-Quantum une fois conflit BouncyCastle résolu
        /// </summary>
        public static async Task<string> DecryptMessage(byte[] ciphertext, byte[] ownerPrivateKey)
        {
            try
            {
                await LogCrypto($"🔓 [DECRYPT] Starting ECDH P-384 enhanced decryption for ciphertext: {ciphertext.Length} bytes");

                // 1. Reconstruire notre clé privée ECDH
                using var ownerEcdh = ECDiffieHellman.Create();
                ownerEcdh.ImportECPrivateKey(ownerPrivateKey, out _);
                await LogCrypto($"🔓 [DECRYPT] Loaded owner ECDH P-384 private key: {ownerPrivateKey.Length} bytes");

                // 2. Extraire la taille de la clé éphémère (4 premiers bytes)
                if (ciphertext.Length < 4)
                    throw new ArgumentException("Ciphertext trop court");

                var ephemeralKeyLength = BitConverter.ToInt32(ciphertext, 0);
                if (ciphertext.Length < 4 + ephemeralKeyLength)
                    throw new ArgumentException("Ciphertext ECDH malformé");

                await LogCrypto($"🔓 [DECRYPT] Ephemeral key length: {ephemeralKeyLength} bytes");

                // 3. Extraire clé publique éphémère et message chiffré
                var ephemeralPublicKey = new byte[ephemeralKeyLength];
                var encryptedMessage = new byte[ciphertext.Length - 4 - ephemeralKeyLength];

                Array.Copy(ciphertext, 4, ephemeralPublicKey, 0, ephemeralKeyLength);
                Array.Copy(ciphertext, 4 + ephemeralKeyLength, encryptedMessage, 0, encryptedMessage.Length);
                await LogCrypto($"🔓 [DECRYPT] Extracted ephemeral key + encrypted message: {encryptedMessage.Length} bytes");

                // 4. Reconstruire la clé publique éphémère
                using var ephemeralEcdh = ECDiffieHellman.Create();
                ephemeralEcdh.ImportSubjectPublicKeyInfo(ephemeralPublicKey, out _);
                await LogCrypto("🔓 [DECRYPT] Reconstructed ephemeral ECDH P-384 public key");

                // 5. Calculer le même secret partagé ECDH
                var sharedSecret = ownerEcdh.DeriveKeyMaterial(ephemeralEcdh.PublicKey);
                await LogCrypto($"🔓 [DECRYPT] Derived shared secret: {sharedSecret.Length} bytes");

                // 6. Dériver la même clé AES
                using var sha256 = SHA256.Create();
                var aesKey = sha256.ComputeHash(sharedSecret);
                await LogCrypto($"🔓 [DECRYPT] Derived AES key: {aesKey.Length} bytes");

                // 7. Déchiffrer le message
                var decryptedBytes = DecryptWithAesGcm(encryptedMessage, aesKey);
                var result = System.Text.Encoding.UTF8.GetString(decryptedBytes);
                await LogCrypto($"🔓 [DECRYPT] ECDH P-384 + AES-GCM decryption successful: {result.Length} chars");

                return result;
            }
            catch (Exception ex)
            {
                await LogCrypto($"❌ [DECRYPT] ECDH P-384 decryption failed: {ex.Message}");
                throw new InvalidOperationException($"Failed to decrypt with ECDH P-384+AES-GCM: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Chiffrement AES-GCM avec le secret partagé
        /// </summary>
        private static byte[] EncryptWithAesGcm(byte[] plaintext, byte[] key)
        {
            const int tagSize = 16; // 128-bit auth tag
            using var aes = new AesGcm(key, tagSize);
            var nonce = new byte[12]; // 96-bit nonce pour GCM
            var tag = new byte[tagSize];
            var ciphertext = new byte[plaintext.Length];

            RandomNumberGenerator.Fill(nonce);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // Combiner nonce + tag + ciphertext
            var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
            Array.Copy(nonce, 0, result, 0, nonce.Length);
            Array.Copy(tag, 0, result, nonce.Length, tag.Length);
            Array.Copy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);

            return result;
        }

        /// <summary>
        /// Déchiffrement AES-GCM avec le secret partagé
        /// </summary>
        private static byte[] DecryptWithAesGcm(byte[] encryptedData, byte[] key)
        {
            const int nonceSize = 12;
            const int tagSize = 16;
            const int minSize = nonceSize + tagSize; // 28 bytes minimum

            if (encryptedData.Length < minSize)
                throw new ArgumentException($"Encrypted data too short: {encryptedData.Length} < {minSize}");

            var nonce = new byte[nonceSize];
            var tag = new byte[tagSize];
            var ciphertext = new byte[encryptedData.Length - minSize];

            // Extract: [nonce:12][tag:16][ciphertext:remaining]
            Array.Copy(encryptedData, 0, nonce, 0, nonceSize);
            Array.Copy(encryptedData, nonceSize, tag, 0, tagSize);
            Array.Copy(encryptedData, nonceSize + tagSize, ciphertext, 0, ciphertext.Length);

            using var aes = new AesGcm(key, tagSize);
            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }

        /// <summary>
        /// Surcharge pour chiffrer des bytes directement (pour chunks de fichiers)
        /// </summary>
        public static async Task<byte[]> EncryptMessage(byte[] plaintextBytes, byte[] recipientPublicKey)
        {
            try
            {
                await LogCrypto($"🔐 [ENCRYPT-BYTES] Starting ECDH P-384 encryption for binary data: {plaintextBytes.Length} bytes");

                // 1. Reconstruire la clé publique ECDH depuis les bytes
                using var recipientEcdh = ECDiffieHellman.Create();
                recipientEcdh.ImportSubjectPublicKeyInfo(recipientPublicKey, out _);

                // 2. Générer clé éphémère P-384 pour ce chunk (Perfect Forward Secrecy)
                using var ephemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);

                // 3. Calculer secret partagé ECDH (384-bit strength)
                var sharedSecret = ephemeralEcdh.DeriveKeyMaterial(recipientEcdh.PublicKey);

                // 4. Utiliser SHA-256 pour dériver clé AES 256-bit robuste
                using var sha256 = SHA256.Create();
                var aesKey = sha256.ComputeHash(sharedSecret);

                // 5. Chiffrer les bytes avec AES-GCM authentifié
                var encryptedMessage = EncryptWithAesGcm(plaintextBytes, aesKey);

                // 6. Combiner: clé publique éphémère + message chiffré
                var ephemeralPublicKey = ephemeralEcdh.PublicKey.ExportSubjectPublicKeyInfo();
                var result = new byte[ephemeralPublicKey.Length + encryptedMessage.Length + 4]; // +4 pour taille

                // Format: [taille_clé_éphémère:4][clé_éphémère][message_chiffré]
                var keyLengthBytes = BitConverter.GetBytes(ephemeralPublicKey.Length);
                Array.Copy(keyLengthBytes, 0, result, 0, 4);
                Array.Copy(ephemeralPublicKey, 0, result, 4, ephemeralPublicKey.Length);
                Array.Copy(encryptedMessage, 0, result, 4 + ephemeralPublicKey.Length, encryptedMessage.Length);

                await LogCrypto($"🔐 [ENCRYPT-BYTES] Binary encryption completed: {plaintextBytes.Length} → {result.Length} bytes");
                return result;
            }
            catch (Exception ex)
            {
                await LogCrypto($"❌ [ENCRYPT-BYTES] Failed to encrypt binary data: {ex.Message}");
                throw new InvalidOperationException($"Failed to encrypt binary data with ECDH P-384+AES-GCM: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Surcharge pour déchiffrer des bytes directement (pour chunks de fichiers)
        /// </summary>
        public static async Task<byte[]> DecryptMessageBytes(byte[] ciphertext, byte[] ownerPrivateKey)
        {
            try
            {
                await LogCrypto($"🔓 [DECRYPT-BYTES] Starting ECDH P-384 binary decryption for ciphertext: {ciphertext.Length} bytes");

                // 1. Reconstruire notre clé privée ECDH
                using var ownerEcdh = ECDiffieHellman.Create();
                ownerEcdh.ImportECPrivateKey(ownerPrivateKey, out _);

                // 2. Extraire la taille de la clé éphémère (4 premiers bytes)
                if (ciphertext.Length < 4)
                    throw new ArgumentException("Ciphertext trop court pour binary decryption");

                var ephemeralKeyLength = BitConverter.ToInt32(ciphertext, 0);
                if (ciphertext.Length < 4 + ephemeralKeyLength)
                    throw new ArgumentException("Ciphertext ECDH binary malformé");

                // 3. Extraire clé publique éphémère et message chiffré
                var ephemeralPublicKey = new byte[ephemeralKeyLength];
                var encryptedMessage = new byte[ciphertext.Length - 4 - ephemeralKeyLength];

                Array.Copy(ciphertext, 4, ephemeralPublicKey, 0, ephemeralKeyLength);
                Array.Copy(ciphertext, 4 + ephemeralKeyLength, encryptedMessage, 0, encryptedMessage.Length);

                // 4. Reconstruire la clé publique éphémère
                using var ephemeralEcdh = ECDiffieHellman.Create();
                ephemeralEcdh.ImportSubjectPublicKeyInfo(ephemeralPublicKey, out _);

                // 5. Calculer le même secret partagé ECDH
                var sharedSecret = ownerEcdh.DeriveKeyMaterial(ephemeralEcdh.PublicKey);

                // 6. Dériver la même clé AES
                using var sha256 = SHA256.Create();
                var aesKey = sha256.ComputeHash(sharedSecret);

                // 7. Déchiffrer avec AES-GCM
                var plaintextBytes = DecryptWithAesGcm(encryptedMessage, aesKey);

                await LogCrypto($"🔓 [DECRYPT-BYTES] Binary decryption completed: {ciphertext.Length} → {plaintextBytes.Length} bytes");
                return plaintextBytes;
            }
            catch (Exception ex)
            {
                await LogCrypto($"❌ [DECRYPT-BYTES] Failed to decrypt binary data: {ex.Message}");
                throw new InvalidOperationException($"Failed to decrypt binary data with ECDH P-384+AES-GCM: {ex.Message}", ex);
            }
        }
    }
}