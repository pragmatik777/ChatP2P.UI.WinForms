Option Strict On
Imports System.IO

Namespace ChatP2P.Core

    Public Enum FrameType As Byte
        Handshake = 0
        Data = 1
        Ack = 2
        Ctrl = 3
        KeepAlive = 4
    End Enum

    Public NotInheritable Class FrameHeader
        Public Const Magic As UInteger = &HC0A7C0DEUI
        Public Property Version As Byte = 1
        Public Property Type As FrameType
        Public Property Flags As Byte
        Public Property Reserved As Byte
        Public Property Seq As UInteger
        Public Property LenCipher As UInteger

        Public Function Serialize() As Byte()
            ' Taille fixe 16 octets
            Dim buf(15) As Byte
            WriteUInt32LE(buf, 0, Magic)
            buf(4) = Version
            buf(5) = CByte(Type)
            buf(6) = Flags
            buf(7) = Reserved
            WriteUInt32LE(buf, 8, Seq)
            WriteUInt32LE(buf, 12, LenCipher)
            Return buf
        End Function

        Public Shared Function Deserialize(buffer As Byte(), Optional offset As Integer = 0) As FrameHeader
            If buffer Is Nothing OrElse buffer.Length - offset < 16 Then
                Throw New InvalidDataException("Header too short")
            End If
            Dim magicVal = ReadUInt32LE(buffer, offset + 0)
            If magicVal <> Magic Then Throw New InvalidDataException("Bad magic")
            Dim h As New FrameHeader()
            h.Version = buffer(offset + 4)
            h.Type = CType(buffer(offset + 5), FrameType)
            h.Flags = buffer(offset + 6)
            h.Reserved = buffer(offset + 7)
            h.Seq = ReadUInt32LE(buffer, offset + 8)
            h.LenCipher = ReadUInt32LE(buffer, offset + 12)
            Return h
        End Function

        ' --- Helpers LE ---
        Private Shared Sub WriteUInt32LE(ByRef buf As Byte(), ByVal pos As Integer, ByVal value As UInteger)
            buf(pos + 0) = CByte(value And &HFFUI)
            buf(pos + 1) = CByte((value >> 8) And &HFFUI)
            buf(pos + 2) = CByte((value >> 16) And &HFFUI)
            buf(pos + 3) = CByte((value >> 24) And &HFFUI)
        End Sub

        Private Shared Function ReadUInt32LE(ByVal buf As Byte(), ByVal pos As Integer) As UInteger
            Dim b0 As UInteger = buf(pos + 0)
            Dim b1 As UInteger = CUInt(buf(pos + 1)) << 8
            Dim b2 As UInteger = CUInt(buf(pos + 2)) << 16
            Dim b3 As UInteger = CUInt(buf(pos + 3)) << 24
            Return b0 Or b1 Or b2 Or b3
        End Function

    End Class

End Namespace
