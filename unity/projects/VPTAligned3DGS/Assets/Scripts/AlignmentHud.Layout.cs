using System;
using UnityEngine;
using UnityEngine.UI;

// UI の構築（World Space Canvas・行の積み上げ・ボタン・文字の欄）（AlignmentHud の一部。全体の方針は AlignmentHud.cs の class doc）。
public partial class AlignmentHud
{
    // ---------------- UI 構築（実行時） ----------------
    //
    // レイアウトは上から順に y カーソルを下ろしながら積む。各要素の高さは
    // 「想定される最大行数」で確保し、1 行想定の欄は Truncate にして隣へはみ出させない。

    private const float PanelW = 520f;
    private const float Pad    = 22f;      // 左右余白
    private const float RowGap = 14f;      // ブロック間の余白

    private void BuildUi()
    {
        bool hasExp = HasExp;
        // 副ボタンは 4 行（校正＋測量 / CP 編集＋Skip / Revert 2 種 / Splats）。
        // 空きを残すと視界を無駄に塞ぐし、余白の意味が読めなくなるので、行数ぶんだけ確保する。
        // 実験なしは 2 列 3 行（→ BuildAlignmentOnlyButtons）。400 のままだと最下段が
        // 下の罫線と診断行に重なる（従来の 2 行構成でも既に 28px 食い込んでいた）。
        // **中身を足したら必ずこの値を直す。** 行の積み上げは y カーソルで、はみ出しても
        // エラーにならず下の診断行に重なるだけなので、目視では気づきにくい。
        // 670 → 688: 準備チェックを 2 行ぶん（22 → 40px）にしたぶん背を高くした。
        // デモ（628）: 指標行の下に 罫線＋チェック行（40）＋ボタン 2 列 3 行＋全幅 1 行（実験へ戻る）。
        // 最下段の下端が上から 546px、診断行の罫線が下から 60px（= 上から 568px）なので 22px 空く。
        float h = demo != null ? 628f : hasExp ? 688f : 510f;

        var canvasGo = new GameObject("HudCanvas");
        canvasGo.transform.SetParent(transform, false);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        var crt = _canvas.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(PanelW, h);
        canvasGo.transform.localScale = Vector3.one * 0.001f;   // 1px = 1mm

        var panel = NewUi("Panel", crt, Vector2.zero, new Vector2(PanelW, h));
        panel.AddComponent<Image>().color = ColBg;

        float inner = PanelW - Pad * 2f;
        float y = h / 2f - Pad;          // 上端から下へ積む y カーソル

        // --- ヘッダ（小さく・大文字・左右振り分け）---
        y -= 16f;
        _headerText = AddText(NewUi("Header", crt, new Vector2(-inner / 4f, y), new Vector2(inner / 2f, 22f)),
                              15, TextAnchor.MiddleLeft, ColFaint, false);
        _headerRight = AddText(NewUi("HeaderR", crt, new Vector2(inner / 4f, y), new Vector2(inner / 2f, 22f)),
                               15, TextAnchor.MiddleRight, ColFaint, false);
        y -= 16f;

        y -= 8f;
        AddRule(crt, y, inner);
        y -= RowGap;

        // --- 指示（アクセントバー＋タイトル＋補足）---
        // タイトル 1 行 + 補足 **3 行**を想定して高さを確保する。補足は縦 Truncate なので、
        // 溢れた行は警告も無く消える——CP 編集の指示（`… · sticks: move the scene`）が実際に
        // 1 行まるごと落ちていた。**HUD で最も重要な文字列**なので、行数のほうを合わせる。
        // 実測（LegacyRuntime.ttf / fontSize 17 / 幅 458）: 1 行 19px・2 行 39px・3 行 59px。
        // SubH を増やさずに 4 行目を書くと消える。**補足を長くするときはここも直す。**
        const float TitleH = 40f, SubH = 60f;
        float bannerH = TitleH + SubH;
        var accentGo = NewUi("Accent", crt, new Vector2(-inner / 2f + 2f, y - bannerH / 2f), new Vector2(4f, bannerH));
        _accent = accentGo.AddComponent<Image>();
        _accent.color = ColReady;

        float textX = -inner / 2f + 18f + (inner - 18f) / 2f;
        float textW = inner - 18f;
        y -= TitleH / 2f;
        _titleText = AddText(NewUi("Title", crt, new Vector2(textX, y), new Vector2(textW, TitleH)),
                             30, TextAnchor.MiddleLeft, ColText, false);
        y -= TitleH / 2f;
        y -= SubH / 2f;
        _subText = AddText(NewUi("Sub", crt, new Vector2(textX, y), new Vector2(textW, SubH)),
                           17, TextAnchor.UpperLeft, ColMuted, true);
        y -= SubH / 2f;
        y -= RowGap;

        // --- 進捗バー（細い線）＋ ラベル ---
        const float BarH = 6f;
        _progressBarWidth = inner;
        y -= BarH / 2f;
        var barBg = NewUi("BarBg", crt, new Vector2(0, y), new Vector2(inner, BarH));
        _barBg = barBg.AddComponent<Image>();
        _barBg.color = new Color(1f, 1f, 1f, 0.10f);
        var fill = new GameObject("BarFill", typeof(RectTransform));
        _progressFillRect = fill.GetComponent<RectTransform>();
        _progressFillRect.SetParent(barBg.GetComponent<RectTransform>(), false);
        _progressFillRect.anchorMin = _progressFillRect.anchorMax = new Vector2(0f, 0.5f);
        _progressFillRect.pivot = new Vector2(0f, 0.5f);
        _progressFillRect.anchoredPosition = Vector2.zero;
        _progressFillRect.sizeDelta = new Vector2(0f, BarH);
        _progressFill = fill.AddComponent<Image>();
        _progressFill.color = ColReady;
        y -= BarH / 2f + 6f;

        y -= 11f;
        _progressText = AddText(NewUi("ProgressLabel", crt, new Vector2(0, y), new Vector2(inner, 22f)),
                                15, TextAnchor.MiddleLeft, ColMuted, false);
        y -= 11f;
        y -= RowGap;

        // --- ライブ数値（2 行想定）---
        const float MetricsH = 42f;
        y -= MetricsH / 2f;
        _metricsText = AddText(NewUi("Metrics", crt, new Vector2(0, y), new Vector2(inner, MetricsH)),
                               16, TextAnchor.UpperLeft, ColText, true);
        y -= MetricsH / 2f;

        if (demo != null) BuildDemoControls(crt, y, inner);
        else if (!hasExp) BuildAlignmentOnlyButtons(crt, y - RowGap, inner);
        else              BuildExperimentControls(crt, y, inner);
        BuildDiag(crt, h, inner);
    }

    private void BuildDiag(RectTransform crt, float h, float inner)
    {
        float y = -h / 2f + Pad + 20f;
        AddRule(crt, y + 18f, inner);
        _diagText = AddText(NewUi("Diag", crt, new Vector2(0, y - 2f), new Vector2(inner, 34f)),
                            12, TextAnchor.UpperLeft, ColFaint, false);
    }

    /// <summary>細い罫線。面で区切るより視界を邪魔しない。</summary>
    private void AddRule(RectTransform parent, float y, float width)
    {
        var go = NewUi("Rule", parent, new Vector2(0, y), new Vector2(width, 1f));
        go.AddComponent<Image>().color = ColRule;
    }

    private void AddButtonAt(RectTransform parent, Vector2 pos, Vector2 size, string label, Action action,
                             int fontSize, Func<string> dynamicLabel, Func<bool> isEnabled, Color color)
    {
        var go = NewUi("Btn", parent, pos, size);
        var img = go.AddComponent<Image>();
        img.color = color;

        // ラベルの矩形はボタンより一回り小さくする。ボタン一杯だと文字が枠線に貼り付くうえ、
        // best fit が「収まっている」と判断する幅がボタンの端までになり、隣のボタンとの
        // 隙間（SGap = 8px）まで文字が迫って 2 つのラベルが 1 続きに見える。
        var lblGo = NewUi("Label", go.GetComponent<RectTransform>(), Vector2.zero,
                          new Vector2(Mathf.Max(24f, size.x - 16f), Mathf.Max(14f, size.y - 6f)));
        var lbl = AddText(lblGo, fontSize, TextAnchor.MiddleCenter, ColText, false);
        lbl.text = label ?? dynamicLabel?.Invoke() ?? "";

        _buttons.Add(new HudButton
        {
            rect = go.GetComponent<RectTransform>(),
            image = img, label = lbl, action = action,
            dynamicLabel = dynamicLabel, isEnabled = isEnabled, baseColor = color,
        });
    }

    private static GameObject NewUi(string name, RectTransform parent, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        return go;
    }

    // best fit の下限。1px = 1mm・約 1.2m 先なので、10px = 10mm ≒ 0.5° がぎりぎり読める大きさ。
    // ここまで縮めても収まらない文字列は**書いたほうが長すぎる**ので、Truncate で切って構わない。
    private const int   MinFontPx    = 10;
    private const float MinFontScale = 0.60f;   // 1 行の欄（縮まないと行が丸ごと消える）
    private const float MinFontScaleWrap = 0.70f; // 複数行の欄（行数で吸収できるぶん控えめ）

    /// <param name="wrap">true なら複数行を想定した欄（高さも複数行ぶん確保してある）。
    /// false は 1 行の欄。**どちらも枠の外へは出さない**（下の best fit）。</param>
    private Text AddText(GameObject go, int fontSize, TextAnchor anchor, Color color, bool wrap)
    {
        var t = go.AddComponent<Text>();
        t.font = uiFont != null ? uiFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = fontSize;
        t.color = color;
        t.alignment = anchor;
        t.supportRichText = true;

        // **枠の外へ文字を出さない。**
        // かつて 1 行の欄は HorizontalWrapMode.Overflow だった。Overflow はクリップしないので、
        // 内容が想定より長いと文字がパネルの外側や隣のボタンの上へそのまま伸びる（実機で発生:
        // 長い診断行・準備チェック行・`Place virtual CP_04 on the splats` のような長い見出し）。
        // かといって Wrap だけにすると、1 行ぶんの高さしか無い欄では 2 行目が縦 Truncate で
        // **黙って消える**。そこで Wrap ＋ best fit にし、収まらないときは字を少し小さくする。
        // best fit の上限は fontSize なので、通常の長さの内容は今までどおりの大きさのまま。
        // **レイアウト（rect の幅・高さ）のほうは変えていない**ので、行の積み上げは従来どおり。
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Truncate;
        t.resizeTextForBestFit = true;
        t.resizeTextMaxSize = fontSize;
        t.resizeTextMinSize = Mathf.Min(fontSize, Mathf.Max(MinFontPx,
            Mathf.RoundToInt(fontSize * (wrap ? MinFontScaleWrap : MinFontScale))));
        return t;
    }
}
