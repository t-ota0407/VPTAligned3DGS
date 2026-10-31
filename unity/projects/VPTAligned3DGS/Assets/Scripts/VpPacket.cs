using System;

/// <summary>
/// Quest ⇄ PC の UDP パケット（13B ヘッダ＋ペイロード）の組み立てと読み取り。
/// **PC 側 `server/vptserver/protocol.py` と一致させること**（type 定数は `tests/test_liveness.py` が
/// このファイルと protocol.py を読んで突き合わせる。ずれると相手側で黙って捨てられる）。
///
///   [ 'V','P', type(1), reqId(4 LE), idx(2 LE), total(2 LE), len(2 LE) ] + payload[len]
///
/// 以前は UDPManager（PING / EVENT / RESET と受信）と KeyframeUploader（META / IMG）が
/// それぞれ手でヘッダを組んでいた。
/// </summary>
public static class VpPacket
{
    public const int HeaderSize = 13;

    public const byte TYPE_META   = 1;   // Quest→PC: キーフレームの META（UTF-8 JSON: 解像度・K・姿勢・時刻）
    public const byte TYPE_IMG    = 2;   // Quest→PC: JPEG のチャンク
    public const byte TYPE_RESULT = 3;   // PC→Quest: Sim(3)（UTF-8 JSON）
    public const byte TYPE_RESET  = 4;   // Quest→PC: 試行リセット（PC は新しい session_* を切る）
    public const byte TYPE_PING   = 5;   // Quest→PC: 疎通確認（reqId = seq）
    public const byte TYPE_PONG   = 6;   // PC→Quest: PING への応答（CV の状態つき）
    public const byte TYPE_EVENT  = 7;   // Quest→PC: 実験ログの 1 行（ACK 付き再送）

    /// <summary>ペイロード全体を 1 パケットにする（idx=0 / total=1）。</summary>
    public static byte[] Build(byte type, uint reqId, byte[] payload) =>
        Build(type, reqId, payload, 0, payload != null ? payload.Length : 0, 0, 1);

    /// <summary>src[offset..offset+len) をペイロードにする（JPEG をチャンクに分けて送るとき）。</summary>
    public static byte[] Build(byte type, uint reqId, byte[] src, int offset, int len, ushort idx, ushort total)
    {
        byte[] p = new byte[HeaderSize + len];
        p[0] = (byte)'V'; p[1] = (byte)'P'; p[2] = type;
        p[3] = (byte)(reqId & 0xFF); p[4] = (byte)((reqId >> 8) & 0xFF);
        p[5] = (byte)((reqId >> 16) & 0xFF); p[6] = (byte)((reqId >> 24) & 0xFF);
        p[7] = (byte)(idx & 0xFF);   p[8] = (byte)((idx >> 8) & 0xFF);
        p[9] = (byte)(total & 0xFF); p[10] = (byte)((total >> 8) & 0xFF);
        p[11] = (byte)(len & 0xFF);  p[12] = (byte)((len >> 8) & 0xFF);
        if (len > 0) Array.Copy(src, offset, p, HeaderSize, len);
        return p;
    }

    /// <summary>受信したパケットのヘッダを読む。'VP' で始まり、宣言された長さぶんのペイロードがあれば true。
    /// ペイロードは buf[HeaderSize .. HeaderSize+payloadLen)。</summary>
    public static bool TryParse(byte[] buf, out byte type, out int payloadLen)
    {
        type = 0; payloadLen = 0;
        if (buf == null || buf.Length < HeaderSize || buf[0] != (byte)'V' || buf[1] != (byte)'P') return false;
        payloadLen = buf[11] | (buf[12] << 8);
        if (buf.Length < HeaderSize + payloadLen) return false;
        type = buf[2];
        return true;
    }
}
