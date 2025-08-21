' ChatP2P.Crypto/IAead.vb
Option Strict On
Imports System

Namespace ChatP2P.Crypto
    Public Interface IAead
        ReadOnly Property Key As Byte()              ' 32 bytes
        Function Seal(nonce As Byte(), plaintext As Byte(), Optional aad As Byte() = Nothing) As Byte()
        Function Open(nonce As Byte(), ciphertext As Byte(), Optional aad As Byte() = Nothing) As Byte()
    End Interface
End Namespace
