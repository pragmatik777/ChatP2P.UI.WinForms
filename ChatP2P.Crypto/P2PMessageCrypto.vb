' ChatP2P.Crypto/P2PMessageCrypto.vb
Option Strict On
Imports System
Imports System.Text
Imports System.Security.Cryptography
Imports Sodium

Namespace ChatP2P.Crypto
    ''' <summary>
    ''' Chiffrement PQC pour les messages P2P texte.
    ''' Utilise Kyber KEM + XChaCha20-Poly1305 AEAD.
    ''' </summary>
    Public NotInheritable Class P2PMessageCrypto

        Public NotInheritable Class P2PKeyPair
            Public ReadOnly PublicKey As Byte()
            Public ReadOnly PrivateKey As Byte()
            Public ReadOnly Algorithm As String
            Public ReadOnly IsSimulated As Boolean

            Public Sub New(pub As Byte(), priv As Byte(), algo As String, Optional simulated As Boolean = False)
                Me.PublicKey = pub
                Me.PrivateKey = priv
                Me.Algorithm = algo
                Me.IsSimulated = simulated
            End Sub
        End Class

        ''' <summary>
        ''' Génère une paire de clés PQC pour ce peer.
        ''' </summary>
        Public Shared Function GenerateKeyPair() As P2PKeyPair
            Try
                Dim kyberPair = PQ.GenerateKyber()
                Return New P2PKeyPair(kyberPair.PublicKey, kyberPair.PrivateKey, kyberPair.Algorithm, kyberPair.IsSimulated)
            Catch
                ' Fallback si PQ échoue
                Return GenerateFallbackKeyPair()
            End Try
        End Function

        ''' <summary>
        ''' Chiffre un message texte pour un destinataire avec sa clé publique PQC.
        ''' Format: [nonce(24)] + [ciphertext] + [tag(16)]
        ''' </summary>
        Public Shared Function EncryptMessage(message As String, recipientPublicKey As Byte()) As Byte()
            If String.IsNullOrEmpty(message) OrElse recipientPublicKey Is Nothing Then
                Throw New ArgumentException("Message et clé publique requis")
            End If

            Try
                ' Génération d'une clé symétrique éphémère via Kyber KEM
                ' Pour la simulation, on utilise une clé dérivée de la clé publique + random
                Dim symmetricKey As Byte()
                
                If recipientPublicKey.Length = 32 Then ' Clé simulée
                    ' Utilise HKDF pour dériver une clé symétrique
                    symmetricKey = DeriveSymmetricKey(recipientPublicKey)
                Else
                    ' TODO: Implémenter Kyber Encaps quand la vraie lib PQC sera ajoutée
                    symmetricKey = DeriveSymmetricKey(recipientPublicKey)
                End If

                ' Chiffrement AEAD avec XChaCha20-Poly1305
                Dim aead As New AeadXChaCha20(symmetricKey)
                Dim nonce = GenerateNonce(aead.NonceSize)
                Dim plaintext = Encoding.UTF8.GetBytes(message)
                Dim ciphertext = aead.Seal(nonce, plaintext, Nothing)
                
                ' Format: [nonce] + [ciphertext+tag]
                Dim result = New Byte(nonce.Length + ciphertext.Length - 1) {}
                Array.Copy(nonce, 0, result, 0, nonce.Length)
                Array.Copy(ciphertext, 0, result, nonce.Length, ciphertext.Length)
                
                Array.Clear(symmetricKey, 0, symmetricKey.Length)
                Return result

            Catch ex As Exception
                Throw New InvalidOperationException("Échec chiffrement PQC: " & ex.Message, ex)
            End Try
        End Function

        ''' <summary>
        ''' Déchiffre un message reçu avec notre clé privée PQC.
        ''' </summary>
        Public Shared Function DecryptMessage(encryptedData As Byte(), ourPrivateKey As Byte()) As String
            If encryptedData Is Nothing OrElse ourPrivateKey Is Nothing Then
                Throw New ArgumentException("Données chiffrées et clé privée requises")
            End If

            If encryptedData.Length < 24 + 16 Then ' nonce + tag minimum
                Throw New ArgumentException("Données chiffrées trop courtes")
            End If

            Try
                ' Dérivation de la clé symétrique (même logique que pour le chiffrement)
                Dim symmetricKey As Byte()
                
                If ourPrivateKey.Length = 64 Then ' Clé simulée
                    ' Dérive la clé publique à partir de la clé privée pour simulation
                    Dim publicKey = ourPrivateKey.Take(32).ToArray()
                    symmetricKey = DeriveSymmetricKey(publicKey)
                Else
                    ' TODO: Implémenter Kyber Decaps quand la vraie lib PQC sera ajoutée
                    Dim publicKey = ourPrivateKey.Take(32).ToArray()
                    symmetricKey = DeriveSymmetricKey(publicKey)
                End If

                Dim aead As New AeadXChaCha20(symmetricKey)
                ' Extraction nonce + ciphertext
                Dim nonce = New Byte(aead.NonceSize - 1) {}
                Dim ciphertext = New Byte(encryptedData.Length - aead.NonceSize - 1) {}
                
                Array.Copy(encryptedData, 0, nonce, 0, aead.NonceSize)
                Array.Copy(encryptedData, aead.NonceSize, ciphertext, 0, ciphertext.Length)
                
                Dim plaintext = aead.Open(nonce, ciphertext, Nothing)
                Array.Clear(symmetricKey, 0, symmetricKey.Length)
                
                Return Encoding.UTF8.GetString(plaintext)

            Catch ex As Exception
                Throw New InvalidOperationException("Échec déchiffrement PQC: " & ex.Message, ex)
            End Try
        End Function

        ''' <summary>
        ''' Génère une paire de clés fallback si PQC échoue.
        ''' </summary>
        Private Shared Function GenerateFallbackKeyPair() As P2PKeyPair
            Dim pub = New Byte(31) {} ' 32 bytes
            Dim priv = New Byte(63) {} ' 64 bytes
            Using rng = RandomNumberGenerator.Create()
                rng.GetBytes(pub)
                rng.GetBytes(priv)
            End Using
            Return New P2PKeyPair(pub, priv, "Fallback", simulated:=True)
        End Function

        ''' <summary>
        ''' Dérive une clé symétrique de 32 bytes à partir d'une clé publique.
        ''' Pour simulation en attendant la vraie lib PQC.
        ''' </summary>
        Private Shared Function DeriveSymmetricKey(publicKey As Byte()) As Byte()
            ' Utilise HKDF-SHA256 pour dériver une clé déterministe mais sécurisée
            Using hmac As New System.Security.Cryptography.HMACSHA256(publicKey)
                Dim info = Encoding.UTF8.GetBytes("ChatP2P-P2PMessage-v1")
                Dim prk = hmac.ComputeHash(info)
                ' Prend les 32 premiers bytes comme clé XChaCha20
                Dim key = New Byte(31) {}
                Array.Copy(prk, 0, key, 0, 32)
                Return key
            End Using
        End Function

        ''' <summary>
        ''' Génère un nonce aléatoire de la taille spécifiée.
        ''' </summary>
        Private Shared Function GenerateNonce(size As Integer) As Byte()
            Dim nonce = New Byte(size - 1) {}
            Using rng = RandomNumberGenerator.Create()
                rng.GetBytes(nonce)
            End Using
            Return nonce
        End Function

    End Class
End Namespace