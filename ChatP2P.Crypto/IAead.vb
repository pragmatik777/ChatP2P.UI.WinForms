Option Strict On

Namespace ChatP2P.Crypto
    Public Interface IAead
        ' Chiffre: retourne ciphertext||tag
        Function Seal(nonce As Byte(), plaintext As Byte(), Optional aad As Byte() = Nothing) As Byte()
        ' Déchiffre: prend ciphertext||tag
        Function Open(nonce As Byte(), ciphertextAndTag As Byte(), Optional aad As Byte() = Nothing) As Byte()

        ReadOnly Property NonceSize As Integer ' 24 pour XChaCha20-Poly1305
        ReadOnly Property TagSize As Integer   ' 16
    End Interface
End Namespace
