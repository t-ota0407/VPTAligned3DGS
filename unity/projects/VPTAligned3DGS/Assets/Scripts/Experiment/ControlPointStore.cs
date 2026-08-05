using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// コントロールポイント（評価 E1 の基準点）の受け渡し形式。
///
/// 座標は必ず **splatRoot のローカル座標**（＝3DGS モデル座標）で持つ。ワールド座標で持つと
/// 位置合わせをやり直すたびに基準点が動いてしまい、評価の意味がなくなるため。
/// マーカーが splatRoot の子であればワールド姿勢は Unity が自動で追従する。
///
/// 想定ワークフロー:
///   1. Editor の Tools &gt; VPTAligned3DGS &gt; Control Points で、スプラットを見ながら CP を置く。
///   1b.（任意・位置合わせ前のみ）実機の VR 内で <see cref="ControlPointEdit"/> により置き直す。
///      マウス配置は視線方向の奥行きが取れないため、実寸で先端を当てるほうが正確。
///   2. 実機ではその位置に対応する実物を、実験前に 1 回だけコントローラ先端で測量する
///      （<see cref="ControlPointMeasure"/>）。測量値はワールド座標なのでここには保存しない
///      — ワールド原点が動けば無効になる値で、モデル座標の注釈とは寿命が違うため。
///
/// このクラスが持つのは 1（と 1b）の結果だけ。JSON はシーンをまたいで CP を持ち回るための
/// 受け渡し形式で、Editor のエクスポート／インポートに加えて、**1b の結果を実行時に書き出す**
/// 経路でも同じ形を使う（PC 側 `run.py` が `received/controlpoints_&lt;modelId&gt;.json` を、
/// 実機が `persistentDataPath` に控えを書く）。どちらも Editor の「JSON を読み込んで反映」で
/// そのままシーンへ戻せる。
/// </summary>
[Serializable]
public class ControlPointStore
{
    [Serializable]
    public class Entry
    {
        public string name;
        public float[] p;      // splatRoot ローカル座標 [x, y, z]

        public Vector3 Position => (p != null && p.Length >= 3) ? new Vector3(p[0], p[1], p[2]) : Vector3.zero;

        public static Entry From(string name, Vector3 local) =>
            new Entry { name = name, p = new[] { local.x, local.y, local.z } };
    }

    public string modelId = "";
    public string savedUtc = "";
    public List<Entry> points = new List<Entry>();

    /// <summary>実機・Editor 共通のファイル名（modelId ごとに分ける）。</summary>
    public static string FileName(string modelId)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in modelId ?? "default")
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
        return "controlpoints_" + (sb.Length > 0 ? sb.ToString() : "default") + ".json";
    }

    public static bool Save(string path, ControlPointStore store, out string error)
    {
        error = null;
        try
        {
            store.savedUtc = DateTime.UtcNow.ToString("O");
            File.WriteAllText(path, JsonUtility.ToJson(store, true));
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    public static ControlPointStore Load(string path, out string error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) { error = "ファイルがありません: " + path; return null; }
            var s = JsonUtility.FromJson<ControlPointStore>(File.ReadAllText(path));
            if (s == null || s.points == null) { error = "JSON の内容が不正です"; return null; }
            return s;
        }
        catch (Exception e) { error = e.Message; return null; }
    }
}
