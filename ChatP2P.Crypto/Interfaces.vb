Option Strict On

Namespace ChatP2P.Crypto



    Public Interface IKexClassic
        ReadOnly Property Name As String
        Function GenerateKeyPair() As (Priv As Byte(), Pub As Byte())
        Function Derive(ownPriv As Byte(), peerPub As Byte()) As Byte()
    End Interface

    Public Interface IKemPq
        ReadOnly Property Name As String
        Function KeyGen() As (Priv As Byte(), Pub As Byte())
        Function Encaps(pubKey As Byte()) As (CipherText As Byte(), SharedSecret As Byte())
        Function Decaps(privKey As Byte(), cipherText As Byte()) As Byte()
    End Interface

    Public Interface IKeySchedule
        ' Retourne un dictionnaire {label -> clé 32B}
        Function Derive(transcriptHash As Byte(), ss1 As Byte(), ss2 As Byte(), labels As String()) As Dictionary(Of String, Byte())
    End Interface

End Namespace
