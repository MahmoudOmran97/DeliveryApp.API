// ═══════════════════════════════════════════════════════════════
// Controllers / SearchController.cs
// GET api/search?q=بيتزا&lat=..&lng=..&radiusKm=10&category=Restaurant
// بحث شامل (بالتخمين) بيرجّع:
//   stores   : المحلات اللي اسمها/عنوانها/وصفها بيطابق **أو** عندها منتجات بتطابق (مثلاً "بيتزا" → كل محل عنده بيتزا)
//   products : المنتجات المطابقة ومعاها بيانات المحل التابعة له
// كله جوه نطاق التوصيل (الزون) لو lat/lng اتبعتوا، والسيرفر هو اللي بيفرض السقف.
// ═══════════════════════════════════════════════════════════════
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("api/search")]
    public class SearchController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        public SearchController(ApplicationDbContext context) => _context = context;

        // ── كاش بسيط لفهرس المنتجات (60 ثانية) عشان الكتابة المتتابعة ما تضربش الداتابيز كل مرة ──
        sealed class ProductEntry
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public string? Description { get; init; }
            public decimal Price { get; init; }
            public decimal? DiscountedPrice { get; init; }
            public string? ImageUrl { get; init; }
            public string? CategoryName { get; init; }
            public int RestaurantId { get; init; }
            public FuzzySearch.Prepared NameP { get; init; } = new(null);
            public FuzzySearch.Prepared DescP { get; init; } = new(null);
        }

        static List<ProductEntry>? _productCache;
        static DateTime _productCacheAt = DateTime.MinValue;
        static readonly SemaphoreSlim _cacheLock = new(1, 1);

        async Task<List<ProductEntry>> GetProductIndexAsync()
        {
            if (_productCache != null && DateTime.UtcNow - _productCacheAt < TimeSpan.FromSeconds(60))
                return _productCache;

            await _cacheLock.WaitAsync();
            try
            {
                if (_productCache != null && DateTime.UtcNow - _productCacheAt < TimeSpan.FromSeconds(60))
                    return _productCache;

                var rows = await _context.Products
                    .Where(p => p.IsActive && p.IsAvailable && p.Category.Restaurant.IsActive)
                    .Select(p => new
                    {
                        p.Id,
                        p.Name,
                        p.Description,
                        p.Price,
                        p.DiscountedPrice,
                        p.ImageUrl,
                        CategoryName = p.Category.Name,
                        RestaurantId = p.Category.RestaurantId
                    })
                    .ToListAsync();

                _productCache = rows.Select(r => new ProductEntry
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    Price = r.Price,
                    DiscountedPrice = r.DiscountedPrice,
                    ImageUrl = r.ImageUrl,
                    CategoryName = r.CategoryName,
                    RestaurantId = r.RestaurantId,
                    NameP = new FuzzySearch.Prepared(r.Name),
                    DescP = new FuzzySearch.Prepared(r.Description)
                }).ToList();
                _productCacheAt = DateTime.UtcNow;
                return _productCache;
            }
            finally { _cacheLock.Release(); }
        }

        [HttpGet]
        public async Task<IActionResult> Search(
            [FromQuery] string? q,
            [FromQuery] double? lat,
            [FromQuery] double? lng,
            [FromQuery] double radiusKm = 10.0,
            [FromQuery] string? category = null,
            [FromQuery] int storeLimit = 15,
            [FromQuery] int productLimit = 30)
        {
            var query = new FuzzySearch.Query(q);
            if (query.IsEmpty || query.Normalized.Length < 2)
                return Ok(new { stores = new object[0], products = new object[0] });

            storeLimit = Math.Clamp(storeLimit, 1, 30);
            productLimit = Math.Clamp(productLimit, 1, 50);

            // ── المحلات (جوه الزون) ──
            var storeQuery = _context.Restaurants.Where(r => r.IsActive).AsQueryable();
            if (!string.IsNullOrWhiteSpace(category))
                storeQuery = storeQuery.Where(r => r.StoreType == category);

            var stores = await storeQuery.Select(r => new
            {
                r.Id, r.Name, r.Description, r.Address, r.Latitude, r.Longitude,
                r.ImageUrl, r.CoverImageUrl, r.Rating, r.TotalRatings,
                r.DeliveryFee, r.MinOrderAmount, r.EstimatedTime, r.IsOpen, r.StoreType
            }).ToListAsync();

            bool useLocation = lat.HasValue && lng.HasValue;
            var distances = new Dictionary<int, double>();
            var (freeR, extraFee) = useLocation
                ? await DeliveryFeeCalculator.GetSettingsAsync(_context)
                : (DeliveryFeeCalculator.DefaultFreeRadiusKm, DeliveryFeeCalculator.DefaultExtraFeePerKm);

            if (useLocation)
            {
                var (maxZoneKm, _) = await DeliveryFeeCalculator.GetZoneSettingsAsync(_context);
                radiusKm = Math.Min(radiusKm, maxZoneKm);
                stores = stores.Where(r =>
                {
                    var d = DeliveryFeeCalculator.GetDistanceKm(lat!.Value, lng!.Value, r.Latitude, r.Longitude);
                    distances[r.Id] = d;
                    return d <= radiusKm;
                }).ToList();
            }

            var storeById = stores.ToDictionary(s => s.Id);

            // ── المنتجات المطابقة (بالتخمين) جوه المحلات دي ──
            var index = await GetProductIndexAsync();
            var matchedProducts = new List<(ProductEntry P, int Score)>();
            foreach (var p in index)
            {
                if (!storeById.ContainsKey(p.RestaurantId)) continue;
                var nameScore = FuzzySearch.Score(query, p.NameP);
                var descScore = FuzzySearch.Score(query, p.DescP);
                int? best = nameScore;
                if (descScore.HasValue && (best == null || descScore.Value + 3 < best)) best = descScore.Value + 3;
                if (best.HasValue) matchedProducts.Add((p, best.Value));
            }
            matchedProducts = matchedProducts
                .OrderBy(x => x.Score).ThenBy(x => x.P.Name).ToList();

            var productsByStore = matchedProducts
                .GroupBy(x => x.P.RestaurantId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // ── ترتيب المحلات: اسم المحل أولاً، ثم العنوان/الوصف، ثم المحلات اللي عندها منتجات مطابقة ──
            var scoredStores = new List<(int Id, int Score, double Rating)>();
            foreach (var s in stores)
            {
                int? best = FuzzySearch.Score(query, new FuzzySearch.Prepared(s.Name));
                var d = FuzzySearch.Score(query, new FuzzySearch.Prepared(s.Description));
                if (d.HasValue && (best == null || d.Value + 2 < best)) best = d.Value + 2;
                var a = FuzzySearch.Score(query, new FuzzySearch.Prepared(s.Address));
                if (a.HasValue && (best == null || a.Value + 3 < best)) best = a.Value + 3;
                if (productsByStore.TryGetValue(s.Id, out var ps))
                {
                    var viaProduct = ps[0].Score + 3;
                    if (best == null || viaProduct < best) best = viaProduct;
                }
                if (best.HasValue) scoredStores.Add((s.Id, best.Value, Convert.ToDouble(s.Rating)));
            }

            var storeResults = scoredStores
                .OrderBy(x => x.Score).ThenByDescending(x => x.Rating)
                .Take(storeLimit)
                .Select(x =>
                {
                    var s = storeById[x.Id];
                    double? distKm = distances.TryGetValue(s.Id, out var dd) ? dd : (double?)null;
                    decimal fee = distKm.HasValue
                        ? DeliveryFeeCalculator.Calculate(s.DeliveryFee, distKm.Value, freeR, extraFee)
                        : s.DeliveryFee;
                    productsByStore.TryGetValue(s.Id, out var mp);
                    return new
                    {
                        s.Id, s.Name, s.Description, s.Address, s.ImageUrl, s.CoverImageUrl,
                        s.Rating, s.TotalRatings, DeliveryFee = fee, s.MinOrderAmount,
                        s.EstimatedTime, s.IsOpen, s.StoreType, DistanceKm = distKm,
                        // أول 3 منتجات مطابقة في المحل ده (عشان الشاشة تقول "فيه: بيتزا مارجريتا، ...")
                        MatchedProducts = mp?.Take(3).Select(m => m.P.Name).ToList() ?? new List<string>(),
                        MatchedProductCount = mp?.Count ?? 0
                    };
                })
                .ToList();

            var productResults = matchedProducts
                .Take(productLimit)
                .Select(x =>
                {
                    var st = storeById[x.P.RestaurantId];
                    return new
                    {
                        x.P.Id, x.P.Name, x.P.Description, x.P.Price, x.P.DiscountedPrice, x.P.ImageUrl,
                        IsAvailable = true, x.P.CategoryName,
                        RestaurantId = x.P.RestaurantId,
                        RestaurantName = st.Name,
                        RestaurantImage = st.ImageUrl,
                        RestaurantIsOpen = st.IsOpen,
                        StoreType = st.StoreType,
                        DistanceKm = distances.TryGetValue(st.Id, out var dist) ? (double?)dist : null
                    };
                })
                .ToList();

            return Ok(new { stores = storeResults, products = productResults });
        }
    }
}
