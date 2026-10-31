using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 空間アンカーによる位置合わせの永続化で使う純粋計算（MonoBehaviour から分離・テスト可能）。
///
/// splatRoot の姿勢は「アンカーから見た相対姿勢 rel」として保存し、復元時は
/// 「今のアンカー姿勢 ∘ rel」で戻す。ワールド座標そのものを保存しないのは、Quest の
/// トラッキング原点が起動・再センタリング・電源の入れ直しで変わり、数値の基準が失われるため
/// （→ Specification.md §15.2）。スケール（符号付き＝反射）はアンカーに乗らないので別に持ち、
/// ここでは姿勢（位置・回転）だけを扱う。
///
/// 一致の判定は AlignmentManager の連続一致と同じく、**スプラット境界ボックスの 8 隅が動く距離**で行う。
/// splatRoot の原点（＝ COLMAP の原点）は部屋から何 m も離れていることがあり、原点の位置だけを
/// 比べると回転のずれを見落とす。位置と角度に別々の閾値を置くより「見た目がどれだけ違うか」に
/// 直結する（Specification.md §6.3 / §15.5）。
/// </summary>
public static class AnchorMath
{
    /// <summary>アンカー姿勢と相対姿勢から、splatRoot のワールド姿勢を作る。</summary>
    public static Pose Compose(Pose anchor, Pose rel) =>
        new Pose(anchor.position + anchor.rotation * rel.position, anchor.rotation * rel.rotation);

    /// <summary>splatRoot のワールド姿勢を、アンカーから見た相対姿勢に直す（<see cref="Compose"/> の逆）。</summary>
    public static Pose Relative(Pose anchor, Pose target)
    {
        Quaternion inv = Quaternion.Inverse(anchor.rotation);
        return new Pose(inv * (target.position - anchor.position), inv * target.rotation);
    }

    /// <summary>境界ボックスの i 番目（0..7）の隅。</summary>
    public static Vector3 Corner(int i, Vector3 mn, Vector3 mx) =>
        new Vector3((i & 1) == 0 ? mn.x : mx.x,
                    (i & 2) == 0 ? mn.y : mx.y,
                    (i & 4) == 0 ? mn.z : mx.z);

    /// <summary>同じスケールの 2 つの splatRoot 姿勢で、境界ボックス 8 隅が動く最大距離[m]。
    /// scale は符号付き（負＝反射）。隅はモデル座標なので scale を掛けてから回す。</summary>
    public static float SceneShiftM(Pose a, Pose b, float scale, Vector3 mn, Vector3 mx)
    {
        float worst = 0f;
        for (int i = 0; i < 8; i++)
        {
            Vector3 c = Corner(i, mn, mx) * scale;
            float d = Vector3.Distance(a.position + a.rotation * c, b.position + b.rotation * c);
            if (d > worst) worst = d;
        }
        return worst;
    }

    /// <summary>互いに一致する最大の組（多数決）を選び、その添字を result に入れる。
    ///
    /// 各候補 i について「i から threshM 以内の候補の数」を数え、最多の i を代表に採る
    /// （同数なら添字の小さいほう）。3 個のうち 2 個が一致すれば、外れた 1 個を捨てられる。
    /// 2 個が食い違うだけでは、どちらが正しいかは決められない（→ 1 個ずつの組になる）。
    /// 毎フレーム呼ぶので、結果のリストは呼び出し側が使い回す。</summary>
    public static void LargestAgreeingSet(IList<Pose> cands, float scale, Vector3 mn, Vector3 mx,
                                          float threshM, List<int> result)
    {
        result.Clear();
        int n = cands != null ? cands.Count : 0;
        if (n == 0) return;
        int bestI = 0, bestCount = -1;
        for (int i = 0; i < n; i++)
        {
            int cnt = 0;
            for (int j = 0; j < n; j++)
                if (i == j || SceneShiftM(cands[i], cands[j], scale, mn, mx) <= threshM) cnt++;
            if (cnt > bestCount) { bestCount = cnt; bestI = i; }
        }
        for (int j = 0; j < n; j++)
            if (j == bestI || SceneShiftM(cands[bestI], cands[j], scale, mn, mx) <= threshM) result.Add(j);
    }

    /// <summary>選んだ候補の平均姿勢（位置は算術平均、回転は半球を揃えたクォータニオン平均）。
    /// 候補どうしは閾値以内で一致しているので、小角度近似の平均で十分。</summary>
    public static Pose Average(IList<Pose> poses, IList<int> idx)
    {
        if (idx == null || idx.Count == 0) return Pose.identity;
        if (idx.Count == 1) return poses[idx[0]];
        Vector3 p = Vector3.zero;
        Quaternion r0 = poses[idx[0]].rotation;
        Vector4 q = Vector4.zero;
        foreach (int i in idx)
        {
            p += poses[i].position;
            Quaternion r = poses[i].rotation;
            if (Quaternion.Dot(r, r0) < 0f) r = new Quaternion(-r.x, -r.y, -r.z, -r.w);   // 半球を揃える
            q += new Vector4(r.x, r.y, r.z, r.w);
        }
        p /= idx.Count;
        q.Normalize();
        return new Pose(p, new Quaternion(q.x, q.y, q.z, q.w));
    }

    /// <summary>基準姿勢から最も外れた候補のずれ[m]（HUD の「食い違い」表示用）。候補が無ければ負。</summary>
    public static float MaxShiftFrom(Pose reference, IList<Pose> cands, float scale, Vector3 mn, Vector3 mx)
    {
        if (cands == null || cands.Count == 0) return -1f;
        float worst = 0f;
        for (int i = 0; i < cands.Count; i++)
        {
            float d = SceneShiftM(reference, cands[i], scale, mn, mx);
            if (d > worst) worst = d;
        }
        return worst;
    }

    /// <summary>点群から互いに離れた点を最大 k 個選ぶ（最遠点サンプリング）。
    ///
    /// アンカーの置き場所を「位置合わせ中に実際に歩いた軌跡」から選ぶのに使う。歩いて見た場所は
    /// 空間マップに確実に入っており、部屋全体に散らすほど、てこの腕の誤差（アンカーの向きの
    /// 0.5° のずれが 6m 先で 5cm）を平均で抑えられる（→ Specification.md §15.5）。
    /// 始点は重心から最も遠い点（端から取ると広がりが最大になりやすい）。
    /// 次の候補が既に選んだ点から minSepM 未満しか離れていなければ、そこで打ち切る。</summary>
    public static List<Vector3> SpreadPoints(IList<Vector3> pts, int k, float minSepM)
    {
        var outp = new List<Vector3>();
        int n = pts != null ? pts.Count : 0;
        if (n == 0 || k <= 0) return outp;

        Vector3 c = Vector3.zero;
        for (int i = 0; i < n; i++) c += pts[i];
        c /= n;
        int first = 0; float far = -1f;
        for (int i = 0; i < n; i++)
        {
            float d = (pts[i] - c).sqrMagnitude;
            if (d > far) { far = d; first = i; }
        }
        outp.Add(pts[first]);

        var minD = new float[n];
        for (int i = 0; i < n; i++) minD[i] = Vector3.Distance(pts[i], pts[first]);
        while (outp.Count < k)
        {
            int best = -1; float bestD = -1f;
            for (int i = 0; i < n; i++)
                if (minD[i] > bestD) { bestD = minD[i]; best = i; }
            if (best < 0 || bestD < minSepM) break;
            outp.Add(pts[best]);
            for (int i = 0; i < n; i++)
                minD[i] = Mathf.Min(minD[i], Vector3.Distance(pts[i], pts[best]));
        }
        return outp;
    }
}
