"""UDP チャンクを reqId ごとに集約し、揃ったら Keyframe を返す。"""
import json
from typing import Optional

from .protocol import Packet, TYPE_META, TYPE_IMG
from .keyframe import Keyframe


class Reassembler:
    def __init__(self):
        # reqId -> {"meta": dict|None, "chunks": {idx: bytes}, "total": int}
        self._pending = {}

    def clear(self):
        """未完のチャンクを全て破棄する（試行リセット時。reqId が振り直されるため）。"""
        self._pending.clear()

    def add(self, p: Packet) -> Optional[Keyframe]:
        """1 パケットを取り込み、キーフレームが完成したら返す（未完なら None）。"""
        e = self._pending.setdefault(p.req, {"meta": None, "chunks": {}, "total": 0})
        if p.type == TYPE_META:
            try:
                e["meta"] = json.loads(p.payload.decode("utf-8"))
            except Exception:
                pass
        elif p.type == TYPE_IMG:
            e["chunks"][p.idx] = p.payload
            e["total"] = p.total

        meta = e["meta"]
        if meta is None:
            return None
        total = int(meta.get("n", e["total"]))
        if total <= 0 or len(e["chunks"]) < total:
            return None
        if any(i not in e["chunks"] for i in range(total)):
            return None  # 取りこぼし（ベストエフォート）。次回キャプチャ待ち。

        data = b"".join(e["chunks"][i] for i in range(total))
        del self._pending[p.req]
        return Keyframe(p.req, meta, data)
