using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CategoriesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        public CategoriesController(ApplicationDbContext context) => _context = context;

        // ─────────────────────────────────────────────────────────────────
        // GET api/categories/restaurant/{restaurantId}
        // يرجع قائمة الأقسام الخاصة بمطعم معين مع عدد منتجات كل قسم
        // ─────────────────────────────────────────────────────────────────
        [AllowAnonymous]
        [HttpGet("restaurant/{restaurantId}")]
        public async Task<IActionResult> GetByRestaurant(int restaurantId)
        {
            var exists = await _context.Restaurants
                .AnyAsync(r => r.Id == restaurantId && r.IsActive);

            if (!exists)
                return NotFound(new { message = "Restaurant not found" });

            var categories = await _context.Categories
                .Where(c => c.RestaurantId == restaurantId && c.IsActive)
                .OrderBy(c => c.SortOrder)
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.ImageUrl,
                    c.SortOrder,
                    ProductCount = c.Products.Count(p => p.IsActive)
                })
                .ToListAsync();

            return Ok(categories);
        }

        // ─────────────────────────────────────────────────────────────────
        // GET api/categories/{id}
        // ─────────────────────────────────────────────────────────────────
        [AllowAnonymous]
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var category = await _context.Categories
                .Where(c => c.Id == id && c.IsActive)
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.ImageUrl,
                    c.SortOrder,
                    c.RestaurantId,
                    ProductCount = c.Products.Count(p => p.IsActive)
                })
                .FirstOrDefaultAsync();

            if (category == null)
                return NotFound(new { message = "Category not found" });

            return Ok(category);
        }

        // ─────────────────────────────────────────────────────────────────
        // POST api/categories
        // إضافة قسم جديد لمطعم
        // ─────────────────────────────────────────────────────────────────
        [AllowAnonymous]
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateCategoryDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return BadRequest(new { message = "Category name is required" });

            var restaurantExists = await _context.Restaurants
                .AnyAsync(r => r.Id == dto.RestaurantId && r.IsActive);

            if (!restaurantExists)
                return BadRequest(new { message = "Restaurant not found" });

            // تحديد الترتيب التلقائي
            var maxSort = await _context.Categories
                .Where(c => c.RestaurantId == dto.RestaurantId && c.IsActive)
                .Select(c => (int?)c.SortOrder)
                .MaxAsync() ?? 0;

            var category = new Category
            {
                RestaurantId = dto.RestaurantId,
                Name = dto.Name.Trim(),
                ImageUrl = dto.ImageUrl,
                SortOrder = maxSort + 1,
                IsActive = true
            };

            _context.Categories.Add(category);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = category.Id },
                new { category.Id, category.Name, category.SortOrder });
        }

        // ─────────────────────────────────────────────────────────────────
        // PUT api/categories/{id}
        // تعديل بيانات قسم موجود
        // ─────────────────────────────────────────────────────────────────
        [AllowAnonymous]
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] UpdateCategoryDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return BadRequest(new { message = "Category name is required" });

            var category = await _context.Categories.FindAsync(id);
            if (category == null || !category.IsActive)
                return NotFound(new { message = "Category not found" });

            category.Name = dto.Name.Trim();
            category.ImageUrl = dto.ImageUrl;
            category.SortOrder = dto.SortOrder > 0 ? dto.SortOrder : category.SortOrder;

            await _context.SaveChangesAsync();
            return Ok(new { message = "Category updated successfully" });
        }

        // ─────────────────────────────────────────────────────────────────
        // DELETE api/categories/{id}
        // حذف ناعم للقسم (Soft Delete)
        // ─────────────────────────────────────────────────────────────────
        [AllowAnonymous]
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var category = await _context.Categories
                .Include(c => c.Products)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (category == null || !category.IsActive)
                return NotFound(new { message = "Category not found" });

            // حذف ناعم للقسم وكل منتجاته
            category.IsActive = false;
            foreach (var product in category.Products)
                product.IsActive = false;

            await _context.SaveChangesAsync();
            return Ok(new { message = "Category deleted successfully" });
        }
    }

    // ─── DTOs ───────────────────────────────────────────────────────────────────

    public class CreateCategoryDto
    {
        public int RestaurantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
    }

    public class UpdateCategoryDto
    {
        public string Name { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public int SortOrder { get; set; }
    }
}