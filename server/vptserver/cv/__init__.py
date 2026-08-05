"""CV（自動位置合わせ）モジュール: COLMAP 再ローカライズ → PnP → Sim(3)。

依存の重い部分（特徴抽出・マッチング: SuperPoint/LightGlue, pycolmap）は localizer 系に隔離し、
純粋数学（geometry）と COLMAP モデル読み込み（colmap_model）は numpy だけで動く（テスト可能）。
"""
__all__ = ["colmap_model", "geometry", "gates"]
