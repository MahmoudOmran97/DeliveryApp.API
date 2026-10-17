using DeliveryApp.API.Authorization;
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProductsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        public ProductsController(ApplicationDbContext context) => _context = context;

        // GET api/products/mystore?categoryId=5  — منتجات قسم معين لصاحب المحل نفسه (بيشمل المنتجات المتاحة وغير المتاحة)
        [Authorize(Roles = "Restaurant,Admin")]
        [HttpGet("mystore")]
        public async Task<IActionResult> GetMyStoreProducts([FromQuery] int categoryId)
        {
            var category = await _context.Categories.FindAsync(categoryId);
            if (category == null || !category.IsActive) return NotFound(new { message = "Category not found" });

            var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, category.RestaurantId, _context);
            if (authError != null) return authError;

            var products = await _context.Products
                .Where(p => p.CategoryId == categoryId && p.IsActive)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Description,
                    p.Price,
                    p.DiscountedPrice,
                    p.ImageUrl,
                    p.PreparationTime,
                    p.Calories,
                    p.IsAvailable,
                    Category = new { p.Category.Id, p.Category.Name },
                    CategoryName = p.Category.Name,
                    RestaurantId = p.Category.RestaurantId,
                    RestaurantName = p.Category.Restaurant.Name,
                    Variants = p.Variants.Where(v => v.IsActive).OrderBy(v => v.SortOrder)
                        .Select(v => new { v.Id, v.Name, v.Price, v.SortOrder }).ToList(),
                    OptionGroups = p.OptionGroups.Where(g => g.IsActive).OrderBy(g => g.SortOrder)
                        .Select(g => new
                        {
                            g.Id, g.Name, g.MinSelect, g.MaxSelect, g.SortOrder,
                            Items = g.Items.Where(i => i.IsActive).OrderBy(i => i.SortOrder)
                                .Select(i => new { i.Id, i.Name, i.ExtraPrice, i.SortOrder }).ToList()
                        }).ToList()
                })
                .ToListAsync();

            return Ok(products);
        }

        // GET api/products/admin  — كل المنتجات (لوحة صاحب المنصة)
        [Authorize(Roles = "Admin")]
        [HttpGet("admin")]
        public async Task<IActionResult> GetAllAdmin(
            [FromQuery] string? q,
            [FromQuery] int? restaurantId,
            [FromQuery] int? categoryId,
            [FromQuery] bool? isAvailable,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            var query = _context.Products
                .Where(p => p.IsActive)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(p => p.Name.Contains(q) || (p.Description != null && p.Description.Contains(q)));

            if (restaurantId.HasValue)
                query = query.Where(p => p.Category.RestaurantId == restaurantId.Value);

            if (categoryId.HasValue)
                query = query.Where(p => p.CategoryId == categoryId.Value);

            if (isAvailable.HasValue)
                query = query.Where(p => p.IsAvailable == isAvailable.Value);

            var total = await query.CountAsync();
            var products = await query
                .OrderByDescending(p => p.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Description,
                    p.Price,
                    p.DiscountedPrice,
                    p.ImageUrl,
                    p.PreparationTime,
                    p.Calories,
                    p.IsAvailable,
                    CategoryName = p.Category.Name,
                    RestaurantId = p.Category.RestaurantId,
                    RestaurantName = p.Category.Restaurant.Name
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = products });
        }

        // GET api/products/search?q=kofta&restaurantId=1
        //   اختياري: lat / lng / radiusKm → بيرجّع بس منتجات المحلات اللي جوه نطاق التوصيل (الزون)
        //            category            → نوع المحل (Restaurant / Pharmacy / Supermarket / Grocery ...)
        //   كل بارامتر جديد اختياري، فالنداء القديم (q بس) بيشتغل زي ما هو.
        [HttpGet("search")]
        public async Task<IActionResult> Search(
            [FromQuery] string? q,
            [FromQuery] int? restaurantId,
            [FromQuery] decimal? maxPrice,
            [FromQuery] double? lat,
            [FromQuery] double? lng,
            [FromQuery] double radiusKm = 10.0,
            [FromQuery] string? category = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 50) pageSize = 50;

            var query = _context.Products
                .Where(p => p.IsActive && p.IsAvailable && p.Category.Restaurant.IsActive)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(p => p.Name.Contains(term) || (p.Description != null && p.Description.Contains(term)));
            }

            if (restaurantId.HasValue)
                query = query.Where(p => p.Category.RestaurantId == restaurantId.Value);

            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(p => p.Category.Restaurant.StoreType == category);

            if (maxPrice.HasValue)
                query = query.Where(p => (p.DiscountedPrice ?? p.Price) <= maxPrice.Value);

            // ✅ الزون: لو معانا موقع العميل نفلتر المحلات بالمسافة (Haversine) والسيرفر هو اللي بيفرض
            // السقف الحقيقي (MaxDeliveryZoneKm) زي ما بيحصل في /api/restaurants بالظبط.
            var distances = new Dictionary<int, double>();
            if (lat.HasValue && lng.HasValue)
            {
                var (maxZoneKm, _) = await DeliveryFeeCalculator.GetZoneSettingsAsync(_context);
                radiusKm = Math.Min(radiusKm, maxZoneKm);

                var stores = await _context.Restaurants
                    .Where(r => r.IsActive)
                    .Select(r => new { r.Id, r.Latitude, r.Longitude })
                    .ToListAsync();

                foreach (var r in stores)
                {
                    var d = DeliveryFeeCalculator.GetDistanceKm(lat.Value, lng.Value, r.Latitude, r.Longitude);
                    if (d <= radiusKm) distances[r.Id] = d;
                }

                var inZoneIds = distances.Keys.ToList();
                query = query.Where(p => inZoneIds.Contains(p.Category.RestaurantId));
            }

            var total = await query.CountAsync();
            var rows = await query
                .OrderBy(p => p.Name)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Description,
                    p.Price,
                    p.DiscountedPrice,
                    p.ImageUrl,
                    p.IsAvailable,
                    CategoryName = p.Category.Name,
                    RestaurantId = p.Category.RestaurantId,
                    RestaurantName = p.Category.Restaurant.Name,
                    RestaurantImage = p.Category.Restaurant.ImageUrl,
                    RestaurantIsOpen = p.Category.Restaurant.IsOpen,
                    StoreType = p.Category.Restaurant.StoreType
                })
                .ToListAsync();

            var products = rows.Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.Price,
                p.DiscountedPrice,
                p.ImageUrl,
                p.IsAvailable,
                p.CategoryName,
                p.RestaurantId,
                p.RestaurantName,
                p.RestaurantImage,
                p.RestaurantIsOpen,
                p.StoreType,
                DistanceKm = distances.TryGetValue(p.RestaurantId, out var dist) ? (double?)dist : null
            }).ToList();

            return Ok(new { total, page, pageSize, data = products });
        }

        // GET api/products/{id}
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var product = await _context.Products
                .Where(p => p.Id == id && p.IsActive)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Description,
                    p.Price,
                    p.DiscountedPrice,
                    p.ImageUrl,
                    p.PreparationTime,
                    p.Calories,
                    p.IsAvailable,
                    Category = new { p.Category.Id, p.Category.Name },
                    RestaurantId = p.Category.RestaurantId,
                    Variants = p.Variants.Where(v => v.IsActive).OrderBy(v => v.SortOrder)
                        .Select(v => new { v.Id, v.Name, v.Price, v.SortOrder }).ToList(),
                    OptionGroups = p.OptionGroups.Where(g => g.IsActive).OrderBy(g => g.SortOrder)
                        .Select(g => new
                        {
                            g.Id, g.Name, g.MinSelect, g.MaxSelect, g.SortOrder,
                            Items = g.Items.Where(i => i.IsActive).OrderBy(i => i.SortOrder)
                                .Select(i => new { i.Id, i.Name, i.ExtraPrice, i.SortOrder }).ToList()
                        }).ToList()
                })
                .FirstOrDefaultAsync();

            if (product == null) return NotFound(new { message = "Product not found" });
            return Ok(product);
        }

        // POST api/products  [Admin or Restaurant Desktop]
        [Authorize(Roles = "Restaurant,Admin")]
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateProductDto dto)
        {
            var category = await _context.Categories.FirstOrDefaultAsync(c => c.Id == dto.CategoryId);
            if (category == null) return BadRequest(new { message = "Category not found" });

            var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, category.RestaurantId, _context);
            if (authError != null) return authError;

            var product = new Product
            {
                CategoryId = dto.CategoryId,
                Name = dto.Name,
                Description = dto.Description,
                Price = dto.Price,
                DiscountedPrice = dto.DiscountedPrice,
                ImageUrl = dto.ImageUrl,
                PreparationTime = dto.PreparationTime,
                Calories = dto.Calories,
                IsAvailable = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.Products.Add(product);
            await _context.SaveChangesAsync();
            return CreatedAtAction(nameof(GetById), new { id = product.Id }, new { product.Id, product.Name });
        }

        // PUT api/products/{id}  [Admin or Restaurant Desktop]
        [Authorize(Roles = "Restaurant,Admin")]
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] CreateProductDto dto)
        {
            var product = await _context.Products.Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, product.Category.RestaurantId, _context);
            if (authError != null) return authError;

            // ✅ FIX: تغيير القسم (Category) كان بيتتجاهل تماماً — لازم نتأكد إن القسم الجديد
            // موجود وتابع لنفس المحل (عشان محدش ينقل منتج لمحل تاني) وبعدين نحفظه.
            if (dto.CategoryId > 0 && dto.CategoryId != product.CategoryId)
            {
                var newCategory = await _context.Categories
                    .FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.IsActive);
                if (newCategory == null)
                    return BadRequest(new { message = "Category not found" });
                if (newCategory.RestaurantId != product.Category.RestaurantId)
                    return BadRequest(new { message = "لا يمكن نقل المنتج لقسم تابع لمحل آخر" });

                product.CategoryId = newCategory.Id;
            }

            product.Name = dto.Name; product.Description = dto.Description;
            product.Price = dto.Price; product.DiscountedPrice = dto.DiscountedPrice;
            product.ImageUrl = dto.ImageUrl; product.PreparationTime = dto.PreparationTime;
            product.Calories = dto.Calories;
            await _context.SaveChangesAsync();
            return Ok(new { message = "Updated successfully" });
        }

        // PUT api/products/{id}/toggle-availability  [Admin or Restaurant Desktop]
        [Authorize(Roles = "Restaurant,Admin")]
        [HttpPut("{id}/toggle-availability")]
        public async Task<IActionResult> ToggleAvailability(int id)
        {
            var product = await _context.Products.Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, product.Category.RestaurantId, _context);
            if (authError != null) return authError;

            product.IsAvailable = !product.IsAvailable;
            await _context.SaveChangesAsync();
            return Ok(new { message = product.IsAvailable ? "Product is now available" : "Product is now unavailable", product.IsAvailable });
        }

        // DELETE api/products/{id}  [Admin or Restaurant Desktop]
        [Authorize(Roles = "Restaurant,Admin")]
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var product = await _context.Products.Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, product.Category.RestaurantId, _context);
            if (authError != null) return authError;

            product.IsActive = false; // Soft delete
            await _context.SaveChangesAsync();
            return Ok(new { message = "Product deleted" });
        }

        // POST api/products/{id}/variants
        [Authorize(Roles = "Restaurant,Admin")]
        [HttpPost("{id:int}/variants")]
        public async Task<IActionResult> SetVariants(int id, [FromBody] List<VariantDto>? variants)
        {
            var product = await _context.Products.Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, product.Category.RestaurantId, _context);
            if (authError != null) return authError;

            variants ??= new List<VariantDto>();

            var existing = await _context.ProductVariants.Where(v => v.ProductId == id).ToListAsync();
            _context.ProductVariants.RemoveRange(existing);

            foreach (var (v, i) in variants.Select((v, i) => (v, i)))
            {
                _context.ProductVariants.Add(new ProductVariant
                {
                    ProductId = id,
                    Name = v.Name,
                    Price = v.Price,
                    SortOrder = v.SortOrder > 0 ? v.SortOrder : i,
                    IsActive = true
                });
            }
            await _context.SaveChangesAsync();
            return Ok(new { message = "Variants saved" });
        }

        // POST api/products/{id}/option-groups
        // بيستبدل كل مجموعات الاختيارات بتاعة المنتج (اختار 2 صوص من 8 ...). قائمة فاضية = مسح الكل.
        // الأوردرات القديمة مش بتتأثر لأنها بتخزن نص الاختيارات مش الـ Ids.
        [Authorize(Roles = "Restaurant,Admin")]
        [HttpPost("{id:int}/option-groups")]
        public async Task<IActionResult> SetOptionGroups(int id, [FromBody] List<OptionGroupDto>? groups)
        {
            var product = await _context.Products.Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, product.Category.RestaurantId, _context);
            if (authError != null) return authError;

            groups ??= new List<OptionGroupDto>();

            // ── تحقق + تنضيف ──
            var cleaned = new List<OptionGroupDto>();
            foreach (var g in groups)
            {
                var name = (g.Name ?? "").Trim();
                var items = (g.Items ?? new List<OptionItemDto>())
                    .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                    .Select(i => new OptionItemDto
                    {
                        Name = i.Name.Trim(),
                        ExtraPrice = i.ExtraPrice < 0 ? 0 : i.ExtraPrice,
                        SortOrder = i.SortOrder
                    })
                    .ToList();

                if (name.Length == 0 && items.Count == 0) continue; // صف فاضي
                if (name.Length == 0) return BadRequest(new { message = "كل مجموعة اختيارات لازم يكون لها اسم" });
                if (items.Count == 0) return BadRequest(new { message = $"المجموعة '{name}' محتاجة اختيار واحد على الأقل" });

                var min = Math.Max(0, g.MinSelect);
                var max = g.MaxSelect <= 0 ? items.Count : g.MaxSelect;
                if (max > items.Count) max = items.Count;
                if (min > max) min = max;

                cleaned.Add(new OptionGroupDto
                {
                    Name = name.Length > 150 ? name[..150] : name,
                    MinSelect = min,
                    MaxSelect = max,
                    SortOrder = g.SortOrder,
                    Items = items
                });
            }

            var existingGroups = await _context.ProductOptionGroups
                .Include(g => g.Items)
                .Where(g => g.ProductId == id)
                .ToListAsync();
            _context.ProductOptionItems.RemoveRange(existingGroups.SelectMany(g => g.Items));
            _context.ProductOptionGroups.RemoveRange(existingGroups);

            foreach (var (g, gi) in cleaned.Select((g, i) => (g, i)))
            {
                var group = new ProductOptionGroup
                {
                    ProductId = id,
                    Name = g.Name,
                    MinSelect = g.MinSelect,
                    MaxSelect = g.MaxSelect,
                    SortOrder = gi,
                    IsActive = true
                };
                foreach (var (it, ii) in g.Items.Select((it, i) => (it, i)))
                {
                    group.Items.Add(new ProductOptionItem
                    {
                        Name = it.Name.Length > 100 ? it.Name[..100] : it.Name,
                        ExtraPrice = it.ExtraPrice,
                        SortOrder = ii,
                        IsActive = true
                    });
                }
                _context.ProductOptionGroups.Add(group);
            }

            await _context.SaveChangesAsync();
            return Ok(new { message = "Option groups saved" });
        }
    }

    public class OptionGroupDto
    {
        public string Name { get; set; } = string.Empty;
        public int MinSelect { get; set; }
        public int MaxSelect { get; set; } = 1;
        public int SortOrder { get; set; }
        public List<OptionItemDto> Items { get; set; } = new();
    }

    public class OptionItemDto
    {
        public string Name { get; set; } = string.Empty;
        public decimal ExtraPrice { get; set; }
        public int SortOrder { get; set; }
    }

    public class VariantDto
    {
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public int SortOrder { get; set; }
    }

    public class CreateProductDto
    {
        public int CategoryId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal Price { get; set; }
        public decimal? DiscountedPrice { get; set; }
        public string? ImageUrl { get; set; }
        public int PreparationTime { get; set; } = 15;
        public int? Calories { get; set; }
    }
}