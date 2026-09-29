using System.Collections.Generic;

namespace Ryan6Vrc.MeshOptimizer.Liveness
{
    /// <summary>
    /// The standard MMD model morph names, which dance motions address by name. Brow, eye, mouth and other
    /// groups, as the standard model set names them.
    /// </summary>
    internal static class MmdMorphs
    {
        public static readonly HashSet<string> Names = new HashSet<string>
        {
            // Brows
            "真面目", "困る", "にこり", "怒り", "上", "下", "前", "眉頭左", "眉頭右",
            // Eyes
            "まばたき", "笑い", "ウィンク", "ウィンク右", "ウィンク２", "ｳｨﾝｸ２右", "なごみ", "はぅ", "びっくり",
            "じと目", "ｷﾘｯ", "はちゅ目", "はちゅ目縦潰れ", "はちゅ目横潰れ", "星目", "はぁと", "瞳小", "瞳縦潰れ",
            "光下", "恐ろしい子！", "ハイライト消し", "映り込み消し", "喜び", "わぉ?!", "なごみω", "下瞼上げ",
            // Mouth
            "あ", "い", "う", "え", "お", "あ２", "ん", "▲", "∧", "□", "ワ", "ω", "ω□", "にやり", "にやり２",
            "にっこり", "ぺろっ", "てへぺろ", "てへぺろ２", "口角上げ", "口角下げ", "口横広げ", "歯無し上", "歯無し下",
            // Other
            "照れ", "涙", "青ざめ", "がーん",
        };
    }
}
