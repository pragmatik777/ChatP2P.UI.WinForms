' ChatP2P.Crypto/PQ.vb
Option Strict On
Imports System
Imports System.Security.Cryptography
Imports System.Reflection

Namespace ChatP2P.Crypto

    ''' <summary>
    ''' Génération de paires de clés PQ (réflexion si provider présent, sinon fallback TRNG).
    ''' </summary>
    Public NotInheritable Class PQ

        Public NotInheritable Class KeyPair
            Public ReadOnly PublicKey As Byte()
            Public ReadOnly PrivateKey As Byte()
            Public ReadOnly IsSimulated As Boolean  ' True si fallback (pas de lib PQ)
            Public ReadOnly Algorithm As String

            Public Sub New(pub As Byte(), priv As Byte(), algo As String, Optional simulated As Boolean = False)
                Me.PublicKey = pub
                Me.PrivateKey = priv
                Me.Algorithm = algo
                Me.IsSimulated = simulated
            End Sub
        End Class

        ' ---------- API publiques ----------
        Public Shared Function GenerateKyber() As KeyPair
            ' Essais de réflexion sur quelques types/méthodes "classiques"
            Dim kp As KeyPair = Nothing
            kp = TryReflectGen("Kyber", "GenerateKeyPair")
            If kp Is Nothing Then kp = TryReflectGen("ChatP2P.Crypto.Kyber", "GenerateKeyPair")
            If kp Is Nothing Then kp = TryReflectGen("Org.BouncyCastle.Pqc.Crypto.Crystals.Kyber.KyberKeyPairGenerator", "GenerateKeyPair") ' selon lib
            If kp IsNot Nothing Then
                Return New KeyPair(kp.PublicKey, kp.PrivateKey, "Kyber", simulated:=False)
            End If

            ' Fallback: TRNG (tailles "placeholder" pour dév; remplace quand tu pluggeras la vraie lib)
            Return Simulated("Kyber")
        End Function

        Public Shared Function GenerateDilithium() As KeyPair
            Dim kp As KeyPair = Nothing
            kp = TryReflectGen("Dilithium", "GenerateKeyPair")
            If kp Is Nothing Then kp = TryReflectGen("ChatP2P.Crypto.Dilithium", "GenerateKeyPair")
            If kp Is Nothing Then kp = TryReflectGen("Org.BouncyCastle.Pqc.Crypto.Crystals.Dilithium.DilithiumKeyPairGenerator", "GenerateKeyPair")
            If kp IsNot Nothing Then
                Return New KeyPair(kp.PublicKey, kp.PrivateKey, "Dilithium", simulated:=False)
            End If

            Return Simulated("Dilithium")
        End Function

        ' ---------- Impl détails ----------

        ''' <summary>
        ''' Essaie d’invoquer par réflexion un type/méthode qui retourne un objet avec champs/propriétés PublicKey/PrivateKey.
        ''' </summary>
        Private Shared Function TryReflectGen(tpName As String, method As String) As KeyPair
            Try
                For Each asm In AppDomain.CurrentDomain.GetAssemblies()
                    Dim tp = asm.GetType(tpName, throwOnError:=False)
                    If tp IsNot Nothing Then
                        Dim mi = tp.GetMethod(method, BindingFlags.Public Or BindingFlags.Static)
                        If mi IsNot Nothing Then
                            Dim kv = mi.Invoke(Nothing, Nothing)
                            ' ⬇️ Spécification explicite du type générique (Byte()) — corrige l’erreur BC32050
                            Dim pub As Byte() = TryGet(Of Byte())(kv, New String() {"PublicKey", "Pub", "pub"})
                            Dim priv As Byte() = TryGet(Of Byte())(kv, New String() {"PrivateKey", "Priv", "priv", "SecretKey"})
                            If pub IsNot Nothing AndAlso priv IsNot Nothing Then
                                Return New KeyPair(pub, priv, tpName, simulated:=False)
                            End If
                        End If
                    End If
                Next
            Catch
                ' ignore et retourne Nothing
            End Try
            Return Nothing
        End Function

        ''' <summary>
        ''' Récupère par réflexion un champ/propriété nommée parmi 'names', typée T.
        ''' </summary>
        Private Shared Function TryGet(Of T)(obj As Object, names As String()) As T
            If obj Is Nothing Then Return Nothing
            Dim tp = obj.GetType()
            For Each nm In names
                Dim pi = tp.GetProperty(nm, BindingFlags.Public Or BindingFlags.Instance)
                If pi IsNot Nothing Then
                    Dim v = pi.GetValue(obj, Nothing)
                    If TypeOf v Is T Then Return CType(v, T)
                End If
                Dim fi = tp.GetField(nm, BindingFlags.Public Or BindingFlags.Instance)
                If fi IsNot Nothing Then
                    Dim v = fi.GetValue(obj)
                    If TypeOf v Is T Then Return CType(v, T)
                End If
            Next
            Return Nothing
        End Function

        ''' <summary>
        ''' Génère une paire "simulée" via TRNG pour que l’appli tourne même sans provider PQ.
        ''' </summary>
        Private Shared Function Simulated(algorithm As String) As KeyPair
            ' Tailles de secours (dev). Tu pourras ajuster quand tu brancheras une vraie lib:
            ' - pub 32 bytes, priv 64 bytes suffisent pour des tests d’intégration.
            Dim pub(32 - 1) As Byte
            Dim priv(64 - 1) As Byte
            Using rng = RandomNumberGenerator.Create()
                rng.GetBytes(pub)
                rng.GetBytes(priv)
            End Using
            Return New KeyPair(pub, priv, algorithm, simulated:=True)
        End Function

    End Class

End Namespace
