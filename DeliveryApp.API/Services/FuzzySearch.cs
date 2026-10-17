// ═══════════════════════════════════════════════════════════════
// Services / FuzzySearch.cs
// بحث "بالتخمين": بيتحمّل أخطاء الكتابة والكلمات المتشابهة.
//   • تطبيع عربي (أ/إ/آ→ا، ى→ي، ة→ه، إزالة التشكيل).
//   • دمج الحروف المتقاربة في النطق (ث/ص→س، ذ/ظ→ز، ط→ت، ض→د، ق→ك) لمن يكتب "بالصمجي".
//   • مسافة Damerau-Levenshtein (حرف زيادة/ناقص/غلط/متبدّل) بحد يتناسب مع طول الكلمة.
// Score: رقم أقل = تطابق أفضل. null = مفيش تطابق.
// ═══════════════════════════════════════════════════════════════
using System.Text;

namespace DeliveryApp.API.Services;

public static class FuzzySearch
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if ((ch >= '\u064B' && ch <= '\u065F') || ch == '\u0670' || ch == '\u0640') continue;
            sb.Append(ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                'ء' => ' ',
                _ => char.ToLowerInvariant(ch)
            });
        }
        return sb.ToString();
    }

    // دمج الحروف المتقاربة في النطق — بنطبّقه بس على مقارنة الكلمات (مش على التطابق الحرفي)
    static string Loose(string normalized)
    {
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            sb.Append(ch switch
            {
                'ث' or 'ص' => 'س',
                'ذ' or 'ظ' => 'ز',
                'ط' => 'ت',
                'ض' => 'د',
                'ق' => 'ك',
                _ => ch
            });
        }
        return sb.ToString();
    }

    public static string[] Words(string normalized)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts.ToArray();
    }

    /// <summary>نص جاهز للمقارنة (بيتحسب مرة واحدة لكل عنصر).</summary>
    public sealed class Prepared
    {
        public string Normalized { get; }
        public string[] LooseWords { get; }
        public Prepared(string? text)
        {
            Normalized = Normalize(text);
            LooseWords = Words(Loose(Normalized));
        }
    }

    public sealed class Query
    {
        public string Normalized { get; }
        public string[] LooseTokens { get; }
        public bool IsEmpty => Normalized.Length == 0;
        public Query(string? text)
        {
            Normalized = Normalize(text);
            LooseTokens = Words(Loose(Normalized));
        }
    }

    public static int? Score(Query q, Prepared item)
    {
        if (q.IsEmpty || item.Normalized.Length == 0) return null;

        // تطابق حرفي كامل (بعد التطبيع): أفضل نتيجة
        if (item.Normalized.StartsWith(q.Normalized, StringComparison.Ordinal)) return 0;
        if (item.Normalized.Contains(q.Normalized, StringComparison.Ordinal)) return 1;

        // كل كلمة في الاستعلام لازم تتطابق (حرفيًا أو بالتخمين) مع كلمة في النص
        int total = 2;
        foreach (var t in q.LooseTokens)
        {
            int? best = null;
            foreach (var w in item.LooseWords)
            {
                var s = TokenScore(t, w);
                if (s.HasValue && (best == null || s < best)) best = s;
                if (best == 0) break;
            }
            if (best == null) return null;
            total += best.Value;
        }
        return total;
    }

    static int? TokenScore(string t, string w)
    {
        if (w.StartsWith(t, StringComparison.Ordinal)) return 0;
        if (t.Length >= 3 && w.Contains(t, StringComparison.Ordinal)) return 1;

        // الحد المسموح من الأخطاء بيزيد مع طول الكلمة (الكلمات القصيرة لازم تكون دقيقة)
        int allowed = t.Length <= 3 ? 0 : t.Length <= 6 ? 1 : 2;
        if (allowed == 0) return null;

        int d = Distance(t, w);
        // العميل لسه بيكتب؟ نقارن كمان ببداية الكلمة بنفس طول اللي كتبه
        if (w.Length > t.Length) d = Math.Min(d, Distance(t, w.Substring(0, t.Length)));

        return d <= allowed ? 2 + d : null;
    }

    // Damerau-Levenshtein (optimal string alignment)
    static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }
}
