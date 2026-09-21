using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Services;

// ═════════════════════════════════════════════════════════════════════════
// نظام استنتاج "المناطق" (Zones) للمحلات تلقائيًا من الإحداثيات (Lat/Lng) —
// من غير أي قائمة مدن ثابتة في الكود. مفيد لما التطبيق يبقى شغال في أكتر من
// مركز/مدينة (مركز إدفو، كوم امبو، أسوان، ...) وعايزين الدريفر يفلتر الطلبات
// المتاحة بالمنطقة.
//
// الفكرة: تجميع (Clustering) بسيط بالمسافة — كل محل بينضم لأقرب "مجموعة"
// لو مركزها (centroid) في حدود ZoneRadiusKm، وإلا بيبدأ مجموعة جديدة. المدن
// المصرية الصغيرة عادة متباعدة بعشرات الكيلومترات، فالعتبة دي كافية تفصل
// بينها مع إنها تجمع محلات نفس المدينة مع بعض حتى لو منتشرة شوية.
//
// تسمية المنطقة: بناخد أكتر كلمة متكررة في عناوين محلات المجموعة (بعد تقسيم
// العنوان لكلمات) — غالبًا هتطلع اسم المدينة/الحي لو موجود في العنوان الحر.
// لو مفيش تكرار واضح، بنسمّيها نسبة لاسم أول محل في المجموعة.
// ═════════════════════════════════════════════════════════════════════════

public record RestaurantZone(int ZoneId, string ZoneName);

public interface IRestaurantZoneService
{
    /// <summary>Restaurant.Id → (ZoneId, ZoneName) لكل المحلات النشطة.</summary>
    Task<Dictionary<int, RestaurantZone>> GetRestaurantZonesAsync();
}

public class RestaurantZoneService : IRestaurantZoneService
{
    private readonly ApplicationDbContext _context;

    private const double ZoneRadiusKm = 15.0;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    // الكاش static عشان يتشارك بين كل الـ requests (الخدمة نفسها Scoped بسبب DbContext)
    // ومنعملش clustering على كل الأدمن/محلات في كل ريكوست.
    private static readonly object _cacheLock = new();
    private static Dictionary<int, RestaurantZone>? _cache;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    public RestaurantZoneService(ApplicationDbContext context) => _context = context;

    public async Task<Dictionary<int, RestaurantZone>> GetRestaurantZonesAsync()
    {
        lock (_cacheLock)
        {
            if (_cache != null && DateTime.UtcNow - _cachedAtUtc < CacheDuration)
                return _cache;
        }

        var restaurants = await _context.Restaurants
            .Where(r => r.IsActive)
            .Select(r => new { r.Id, r.Latitude, r.Longitude, r.Address, r.Name })
            .ToListAsync();

        var clusters = new List<ZoneCluster>();

        foreach (var r in restaurants)
        {
            ZoneCluster? best = null;
            var bestDist = double.MaxValue;

            foreach (var c in clusters)
            {
                var d = HaversineKm(c.CentroidLat, c.CentroidLng, r.Latitude, r.Longitude);
                if (d <= ZoneRadiusKm && d < bestDist)
                {
                    best = c;
                    bestDist = d;
                }
            }

            if (best == null)
            {
                best = new ZoneCluster();
                clusters.Add(best);
            }

            best.Members.Add(new ZoneMember(r.Id, r.Latitude, r.Longitude, r.Address, r.Name));
            best.CentroidLat = best.Members.Average(m => m.Lat);
            best.CentroidLng = best.Members.Average(m => m.Lng);
        }

        // أكبر منطقة (أكتر عدد محلات) بتاخد ZoneId=1 وهكذا — ترتيب ثابت وواضح للدريفر
        var result = new Dictionary<int, RestaurantZone>();
        var zoneId = 1;
        foreach (var cluster in clusters.OrderByDescending(c => c.Members.Count))
        {
            var name = InferZoneName(cluster.Members);
            foreach (var m in cluster.Members)
                result[m.Id] = new RestaurantZone(zoneId, name);
            zoneId++;
        }

        lock (_cacheLock)
        {
            _cache = result;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return result;
    }

    private static readonly char[] AddressSeparators = { ',', '،', '-', '–', '/', '|', '.', '\n', '\r' };

    private static string InferZoneName(List<ZoneMember> members)
    {
        var freq = new Dictionary<string, int>();

        foreach (var m in members)
        {
            if (string.IsNullOrWhiteSpace(m.Address)) continue;

            // نقسّم العنوان لكلمات (بعد فواصل شائعة + مسافات)، ونتجاهل كلمات قصيرة جدًا
            // (أرقام عمارة/شقة أو حروف مفردة) عشان الاسم اللي يطلع يبقى ذو معنى.
            var tokens = m.Address
                .Split(AddressSeparators.Append(' ').ToArray(), StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length >= 3 && !t.Any(char.IsDigit))
                .Distinct(); // مرة واحدة لكل محل عشان محل واحد بعنوان طويل ميسيطرش على العد

            foreach (var t in tokens)
                freq[t] = freq.GetValueOrDefault(t) + 1;
        }

        if (freq.Count > 0)
        {
            var top = freq.OrderByDescending(kv => kv.Value)
                          .ThenByDescending(kv => kv.Key.Length)
                          .First();
            if (top.Value >= 2 || members.Count == 1)
                return top.Key;
        }

        // مفيش كلمة متكررة واضحة → نسمّي المنطقة نسبة لأقرب محل لمركزها
        var representative = members.First();
        return $"منطقة {representative.Name}";
    }

    private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
        => 111.045 * Math.Sqrt(
            Math.Pow(lat1 - lat2, 2) +
            Math.Pow((lng1 - lng2) * Math.Cos(lat1 * Math.PI / 180.0), 2));

    private record ZoneMember(int Id, double Lat, double Lng, string? Address, string Name);

    private class ZoneCluster
    {
        public double CentroidLat;
        public double CentroidLng;
        public List<ZoneMember> Members { get; } = new();
    }
}
