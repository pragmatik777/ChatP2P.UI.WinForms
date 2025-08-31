' ChatP2P.Crypto/KemPqStub.vb
Option Strict On
Option Explicit On

Imports Org.BouncyCastle.Security
Imports Org.BouncyCastle.Crypto
Imports Org.BouncyCastle.Pqc.Crypto.Frodo
Imports Org.BouncyCastle.Pqc.Crypto.Utilities

Namespace ChatP2P.Crypto

    ''' <summary>
    ''' Wrapper KEM PQ basé sur BouncyCastle 2.6.2.
    ''' Implémentation : FrodoKEM (paramètre frodokem640aes).
    ''' API exposée (inchangée) :
    '''   KeyGen() -> (pk As Byte(), sk As Byte())
    '''   Encapsulate(peerPk As Byte()) -> (cipherText As Byte(), sharedSecret As Byte())
    '''   Decapsulate(sk As Byte(), cipherText As Byte()) -> sharedSecret As Byte()
    ''' Les clés sont encodées en ASN.1 (SPKI / PKCS#8) via les factories PQC.
    ''' </summary>
    Public NotInheritable Class KemPqStub

        Private Shared ReadOnly Rng As New SecureRandom()
        Private Shared ReadOnly Params As FrodoParameters = FrodoParameters.frodokem640aes

        ' ============== API Publique ==============

        Public Shared Function KeyGen() As (pk As Byte(), sk As Byte())
            Dim gen = New FrodoKeyPairGenerator()
            gen.Init(New FrodoKeyGenerationParameters(Rng, Params))

            Dim kp = gen.GenerateKeyPair()
            Dim pub = DirectCast(kp.Public, FrodoPublicKeyParameters)
            Dim prv = DirectCast(kp.Private, FrodoPrivateKeyParameters)

            ' Encodage portable ASN.1 (SPKI / PKCS#8)
            Dim spki = PqcSubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(pub)
            Dim pk As Byte() = spki.GetDerEncoded()

            Dim pkcs8 = PqcPrivateKeyInfoFactory.CreatePrivateKeyInfo(prv)
            Dim sk As Byte() = pkcs8.GetDerEncoded()

            Return (pk, sk)
        End Function

        Public Shared Function Encapsulate(peerKemPublic As Byte()) As (cipherText As Byte(), sharedSecret As Byte())
            If peerKemPublic Is Nothing OrElse peerKemPublic.Length = 0 Then
                Throw New ArgumentException("peerKemPublic vide.")
            End If

            ' Décodage ASN.1 -> clé publique Frodo
            Dim akp As AsymmetricKeyParameter = PqcPublicKeyFactory.CreateKey(peerKemPublic)
            Dim pub = TryCast(akp, FrodoPublicKeyParameters)
            If pub Is Nothing Then
                Throw New NotSupportedException("Clé publique fournie: format PQC non Frodo.")
            End If

            Dim gen = New FrodoKEMGenerator(Rng)
            Dim sw = gen.GenerateEncapsulated(pub)

            Dim ct As Byte() = sw.GetEncapsulation()
            Dim ss As Byte() = sw.GetSecret()

            Dim disp = TryCast(sw, IDisposable)
            If disp IsNot Nothing Then disp.Dispose()

            Return (cipherText:=ct, sharedSecret:=ss)
        End Function

        Public Shared Function Decapsulate(myKemPrivate As Byte(), cipherText As Byte()) As Byte()
            If myKemPrivate Is Nothing OrElse myKemPrivate.Length = 0 Then
                Throw New ArgumentException("myKemPrivate vide.")
            End If
            If cipherText Is Nothing OrElse cipherText.Length = 0 Then
                Throw New ArgumentException("cipherText vide.")
            End If

            ' Décodage ASN.1 -> clé privée Frodo
            Dim akp As AsymmetricKeyParameter = PqcPrivateKeyFactory.CreateKey(myKemPrivate)
            Dim prv = TryCast(akp, FrodoPrivateKeyParameters)
            If prv Is Nothing Then
                Throw New NotSupportedException("Clé privée fournie: format PQC non Frodo.")
            End If

            Dim ext = New FrodoKEMExtractor(prv)
            Dim ss As Byte() = ext.ExtractSecret(cipherText)
            Return ss
        End Function

    End Class

End Namespace
