Option Strict On

Namespace ChatP2P.App
    Public Module MessageProtocol
        Public Const TAG_PEERS As String = "PEERS:"
        Public Const TAG_MSG As String = "MSG:"
        Public Const TAG_PRIV As String = "PRIV:"
        Public Const TAG_NAME As String = "NAME:"

        Public Const TAG_FILEMETA As String = "FILEMETA:"
        Public Const TAG_FILECHUNK As String = "FILECHUNK:"
        Public Const TAG_FILEEND As String = "FILEEND:"
        
        ' BitTorrent-like file transfer
        Public Const TAG_BT_META As String = "BTMETA:"
        Public Const TAG_BT_CHUNK As String = "BTCHUNK:"
        Public Const TAG_BT_REQUEST As String = "BTREQ:"

        Public Const TAG_ICE_OFFER As String = "ICE_OFFER:"
        Public Const TAG_ICE_ANSWER As String = "ICE_ANSWER:"
        Public Const TAG_ICE_CAND As String = "ICE_CAND:"
    End Module
End Namespace
