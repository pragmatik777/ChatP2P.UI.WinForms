' ChatP2P.Crypto/KeyScheduleHKDF.vb
Option Strict On
Option Explicit On

Imports System
Imports System.Security.Cryptography
Imports System.Text
Imports System.Linq

Namespace ChatP2P.Crypto
    ''' <summary>
    ''' HKDF-SHA256 minimaliste.
    ''' API d'origine conservée:
    '''   - Extract(salt, ikm)
    '''   - Expand(prk, info, length)
    '''   - DeriveKey(salt, ikm, info As String, outLen)
    ''' Ajouts pour compatibilité avec Form1:
    '''   - Derive(root As Byte(), info As String, length As Integer)
    '''   - Derive(root As Byte(), info As Byte(), length As Integer)
    ''' </summary>
    Public NotInheritable Class KeyScheduleHKDF

        ' ============== API EXISTANTE (inchangée) ==============

        Public Shared Function Extract(salt As Byte(), ikm As Byte()) As Byte()
            Dim s = If(salt, Array.Empty(Of Byte)())
            Using h As New HMACSHA256(s)
                Return h.ComputeHash(ikm)
            End Using
        End Function

        Public Shared Function Expand(prk As Byte(), info As Byte(), length As Integer) As Byte()
            If prk Is Nothing OrElse prk.Length = 0 Then Throw New ArgumentException("PRK manquant.")
            Dim blocks As New List(Of Byte)()
            Dim tPrev() As Byte = Array.Empty(Of Byte)()
            Dim i As Byte = 1
            While blocks.Count < length
                Using h As New HMACSHA256(prk)
                    Dim data As New List(Of Byte)()
                    data.AddRange(tPrev)
                    If info IsNot Nothing Then data.AddRange(info)
                    data.Add(i)
                    tPrev = h.ComputeHash(data.ToArray())
                    Dim need = Math.Min(tPrev.Length, length - blocks.Count)
                    blocks.AddRange(tPrev.Take(need))
                    i = CByte(i + 1)
                End Using
            End While
            Return blocks.ToArray()
        End Function

        Public Shared Function DeriveKey(salt As Byte(), ikm As Byte(), info As String, outLen As Integer) As Byte()
            Dim prk = Extract(salt, ikm)
            Dim inf = If(info Is Nothing, Array.Empty(Of Byte)(), Encoding.UTF8.GetBytes(info))
            Return Expand(prk, inf, outLen)
        End Function

        ' ============== SURCHARGES AJOUTÉES (utilisées par Form1) ==============

        ''' <summary>
        ''' Syntactic sugar: HKDF(root, info, L) avec salt = 0^32 (HKDF-Extract standard).
        ''' </summary>
        Public Shared Function Derive(root As Byte(), info As String, length As Integer) As Byte()
            Dim infoBytes = If(info Is Nothing, Array.Empty(Of Byte)(), Encoding.UTF8.GetBytes(info))
            Return Derive(root, infoBytes, length)
        End Function

        ''' <summary>
        ''' Variante bytes: HKDF(root, info, L) avec salt = 0^32 (HKDF-Extract standard).
        ''' </summary>
        Public Shared Function Derive(root As Byte(), info As Byte(), length As Integer) As Byte()
            ' Extract avec salt zéro (32 octets de 0) comme recommandé lorsqu'il n'y a pas de sel.
            Dim zeroSalt(31) As Byte ' 32 bytes of 0x00
            Dim prk = Extract(zeroSalt, root)
            Dim inf = If(info, Array.Empty(Of Byte)())
            Return Expand(prk, inf, length)
        End Function

    End Class
End Namespace
