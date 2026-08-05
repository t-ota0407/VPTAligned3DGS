"""COLMAP↔Unity の姿勢/相似変換ユーティリティ（純粋計算・numpy のみ・テスト可能）。

座標系の前提（Specification §8 / ImplementationSpec §2 と一致。aras-p ソースで確認済み）:
- aras-p UnityGaussianSplatting は PLY の点座標を **verbatim（軸反転なし）** で読み込む
  （GaussianSplatAssetCreator → GaussianFileReader: 位置に符号反転なし。回転のみ
   NormalizeSwizzleRotation で表示補正、位置は不変）。
- ゆえに 3DGS モデル座標 m の数値 = COLMAP 世界座標 c の数値（同一）。
- COLMAP c は右手系、Unity world w は左手系。右手系の数値を左手系で解釈すると鏡像になるため、
  m→w の整合には **反射（負の一様スケール）** が要る。実機の手動合わせでも scale が負値
  （例 -0.957）で成立しており一致する。
- よって本モジュールは反射を許す相似で当てはめ、Unity の localScale を **符号付き（負を許容）**
  にして反射を表現する。

Sim(3) は **カメラ中心**だけで解く（中心は世界座標上の点でカメラ軸規約に依らないため、
OpenCV/Unity のカメラ y 軸向きの違いを気にせず扱える）。
"""
import numpy as np


def colmap_quat_to_R(qw, qx, qy, qz):
    """COLMAP クォータニオン(qw,qx,qy,qz) → 回転行列 R（X_cam = R·X_world + t）。"""
    q = np.array([qw, qx, qy, qz], float)
    q /= np.linalg.norm(q)
    w, x, y, z = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w),     2 * (x * z + y * w)],
        [2 * (x * y + z * w),     1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w),     2 * (y * z + x * w),     1 - 2 * (x * x + y * y)],
    ])


def camera_center(R, t):
    """world→cam (R,t) からカメラ中心（world 座標）= -R^T t。"""
    return -np.asarray(R, float).T @ np.asarray(t, float)


def R_to_quat(R):
    """proper rotation 行列 → クォータニオン (x, y, z, w)（Shepperd 法, Unity 順）。"""
    m = np.asarray(R, float)
    tr = m[0, 0] + m[1, 1] + m[2, 2]
    if tr > 0:
        S = np.sqrt(tr + 1.0) * 2
        w = 0.25 * S
        x = (m[2, 1] - m[1, 2]) / S
        y = (m[0, 2] - m[2, 0]) / S
        z = (m[1, 0] - m[0, 1]) / S
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        S = np.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w = (m[2, 1] - m[1, 2]) / S; x = 0.25 * S
        y = (m[0, 1] + m[1, 0]) / S; z = (m[0, 2] + m[2, 0]) / S
    elif m[1, 1] > m[2, 2]:
        S = np.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w = (m[0, 2] - m[2, 0]) / S; x = (m[0, 1] + m[1, 0]) / S
        y = 0.25 * S; z = (m[1, 2] + m[2, 1]) / S
    else:
        S = np.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w = (m[1, 0] - m[0, 1]) / S; x = (m[0, 2] + m[2, 0]) / S
        y = (m[1, 2] + m[2, 1]) / S; z = 0.25 * S
    q = np.array([x, y, z, w], float)
    return q / np.linalg.norm(q)


def umeyama(src, dst, with_reflection=True, handedness=None):
    """相似変換当てはめ（Umeyama 1991）。dst ≈ s · Q @ src + t。

    handedness で直交因子 Q の行列式を拘束する:
      None …… 自由（det は ±1。SVD が残差最小のものを選ぶ）
      +1   …… 真の回転のみ（反射を許さない。= 旧 with_reflection=False）
      -1   …… 反射のみ（必ず鏡像。本パイプラインの既定）

    **なぜ -1 を強制できるのか（最重要）**:
      COLMAP は右手系、Unity は左手系で、aras-p は PLY の座標を軸反転せず読む。
      つまり右手系の数値を左手系で解釈するので、実空間と合わせるには必ず反射が要る。
      符号は座標規約から決まる定数であって、推定すべき未知数ではない
      （Specification §8 / CLAUDE.md §6）。

    **自由推定が危険な理由**:
      3 点は必ず同一平面上に乗る。反射を許すと「その平面に関する鏡映を掛けた解」が
      **完全に同じ残差**を与えるため（鏡映は平面上の点を動かさない）、2 解を残差で
      区別できない。SVD の選択は実質コインフリップになる。数値検証では 3 点で 56%、
      共面 4 点でも大半が誤り、非共面 4 点以上で初めて 0% になった。

    返り値: s(>0), Q(3x3 直交), t(3,)。
    """
    src = np.asarray(src, float)
    dst = np.asarray(dst, float)
    n, d = src.shape
    mu_s = src.mean(0)
    mu_d = dst.mean(0)
    sc = src - mu_s
    dc = dst - mu_d
    var_s = (sc ** 2).sum() / n
    Sigma = (dc.T @ sc) / n
    U, D, Vt = np.linalg.svd(Sigma)

    # 旧引数 with_reflection=False は handedness=+1 と同義（後方互換）
    if handedness is None and not with_reflection:
        handedness = +1

    S = np.ones(d)
    if handedness is not None:
        det_uv = np.linalg.det(U) * np.linalg.det(Vt)
        # 最小特異値に対応する軸の符号を反転すると det(Q) が反転する。
        # 残差の増分が最小になる反転なので、拘束下での最適解になる。
        if np.sign(det_uv) != np.sign(handedness):
            S[-1] = -1.0

    Q = (U * S) @ Vt
    s = float((D * S).sum() / var_s)
    t = mu_d - s * (Q @ mu_s)
    return s, Q, t


def center_conditioning(centers):
    """カメラ中心の配置がどれだけ「立体的か」を測る（純粋計算・テスト可能）。

    反射を固定しても直らない別の退化がある: **一直線に歩いた場合**。
    そのときは直線まわりの回転が拘束されず、残差が小さいまま解が回ってしまう。
    主成分方向の広がりを見れば、共線・共面・十分に立体、を区別できる。

    返り値 dict:
      extent1/2/3 … 主成分方向の広がり[m]（大きい順。max-min で測る）
      baseline    … 最大の 2 点間距離[m]
    """
    c = np.asarray(centers, float)
    if len(c) < 2:
        return dict(extent1=0.0, extent2=0.0, extent3=0.0, baseline=0.0)
    d = c - c.mean(0)
    # 主成分方向へ射影して、その軸上での広がり（max-min）を取る。
    # 特異値そのものより「何メートル散らばっているか」のほうが閾値を決めやすい。
    _, _, Vt = np.linalg.svd(d, full_matrices=False)
    proj = d @ Vt.T
    ext = proj.max(0) - proj.min(0)
    ext = np.pad(ext, (0, max(0, 3 - len(ext))))[:3]
    diff = c[:, None, :] - c[None, :, :]
    baseline = float(np.sqrt((diff ** 2).sum(-1)).max())
    return dict(extent1=float(ext[0]), extent2=float(ext[1]), extent3=float(ext[2]),
                baseline=baseline)


def solve_sim3_to_unity(centers_c, centers_w, handedness=None):
    """COLMAP 中心 {C_c} → Unity world 中心 {C_w} の相似を解き、
    Unity Transform 用の (scale_signed, R(3x3 proper), t, quat(x,y,z,w)) を返す。
    反射が要る場合は scale を負にし R を det=+1 に保つ（鏡像を負スケールで表現）。

    handedness=-1 を渡すと必ず負スケール（鏡像）の解になる。→ umeyama の説明を参照。"""
    s, Q, t = umeyama(centers_c, centers_w, handedness=handedness)
    if np.linalg.det(Q) < 0:        # 反射が必要 → 負スケールで表現
        sigma = -s
        R = -Q                       # det(-Q)=+1（proper rotation）
    else:
        sigma = s
        R = Q
    return sigma, R, t, R_to_quat(R)


def residuals(sigma, R, t, centers_c, centers_w):
    """各対応の world 残差（メートル）。"""
    c = np.asarray(centers_c, float)
    w = np.asarray(centers_w, float)
    pred = sigma * (R @ c.T).T + t
    return np.linalg.norm(pred - w, axis=1)


def solve_sim3_ransac(centers_c, centers_w, thresh_m=0.10, iters=500, seed=0,
                      handedness=None):
    """外れ値に頑健な Sim(3) 推定。3点で相似を当て、world 残差 thresh_m 以内を
    インライアとして最良を採用 → インライアで再当てはめ。

    handedness は umeyama にそのまま渡す（-1 で反射固定。既定 None は自由推定）。

    注意: **n < 5 では RANSAC を回さず全点で 1 回当てはめる**（3 点抽出に意味がないため）。
    つまり最小枚数を 4 以下に設定すると外れ値除去が一切効かない。運用の既定は 5 以上にする
    （`localize.watch` の min / cv.json の "min"）。

    返り値: dict(sigma, R, t, quat, inliers(bool[]), rmse_m, cond)。"""
    c = np.asarray(centers_c, float)
    w = np.asarray(centers_w, float)
    n = len(c)
    if n < 3:
        raise ValueError("need >=3 correspondences (got %d)" % n)

    def finalize(mask):
        sigma, R, t, q = solve_sim3_to_unity(c[mask], w[mask], handedness=handedness)
        res = residuals(sigma, R, t, c, w)
        inl = res < thresh_m
        if inl.sum() >= 3:           # インライアで締め直し
            sigma, R, t, q = solve_sim3_to_unity(c[inl], w[inl], handedness=handedness)
            res = residuals(sigma, R, t, c, w)
            inl = res < thresh_m
        rmse = float(np.sqrt((res[inl] ** 2).mean())) if inl.any() else float("inf")
        # 配置の立体性は「解が信用できるか」の判断材料なので一緒に返す
        cond = center_conditioning(w[inl] if inl.sum() >= 2 else w)
        return dict(sigma=sigma, R=R, t=t, quat=q, inliers=inl, rmse_m=rmse, cond=cond)

    if n < 5:                        # 少数は全点で1回（RANSAC は効かない）
        return finalize(np.ones(n, bool))

    rng = np.random.default_rng(seed)
    best_inl = None
    best_cnt = -1
    for _ in range(iters):
        idx = rng.choice(n, 3, replace=False)
        try:
            s, Q, t = umeyama(c[idx], w[idx], handedness=handedness)
        except np.linalg.LinAlgError:
            continue
        res = np.linalg.norm((s * (Q @ c.T).T + t) - w, axis=1)
        inl = res < thresh_m
        if inl.sum() > best_cnt:
            best_cnt = int(inl.sum())
            best_inl = inl
    if best_inl is None or best_inl.sum() < 3:
        return finalize(np.ones(n, bool))
    return finalize(best_inl)
