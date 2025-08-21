' ChatP2P.Crypto/KeyScheduleHKDF.vb
Option Strict On
Imports System
Imports System.Security.Cryptography
Imports System.Text

Namespace ChatP2P.Crypto
    Public NotInheritable Class KeyScheduleHKDF
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
    End Class
End Namespace
