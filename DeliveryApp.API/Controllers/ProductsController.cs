using DeliveryApp.API.Models;
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
        [HttpGet("search")]
        public async Task<IActionResult> Search(
            [FromQuery] string q,
            [FromQuery] int? restaurantId,
            [FromQuery] decimal? maxPrice,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _context.Products
                .Where(p => p.IsActive && p.IsAvailable)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(p => p.Name.Contains(q) || p.Description!.Contains(q));

            if (restaurantId.HasValue)
                query = query.Where(p => p.Category.RestaurantId == restaurantId.Value);

            if (maxPrice.HasValue)
                query = query.Where(p => (p.DiscountedPrice ?? p.Price) <= maxPrice.Value);

            var total = await query.CountAsync();
            var products = await query
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Price,
                    p.DiscountedPrice,
                    p.ImageUrl,
                    p.IsAvailable,
                    CategoryName = p.Category.Name,
                    RestaurantId = p.Category.RestaurantId,
                    RestaurantName = p.Category.Restaurant.Name
                })
                .ToListAsync();

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
                        .Select(v => new { v.Id, v.Name, v.Price, v.SortOrder }).ToList()
                })
                .FirstOrDefaultAsync();

            if (product == null) return NotFound(new { message = "Product not found" });
            return Ok(product);
        }

        // POST api/products  [Admin or Restaurant Desktop]
        [AllowAnonymous]
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateProductDto dto)
        {
            var categoryExists = await _context.Categories.AnyAsync(c => c.Id == dto.CategoryId);
            if (!categoryExists) return BadRequest(new { message = "Category not found" });

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
        [AllowAnonymous]
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] CreateProductDto dto)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.Name = dto.Name; product.Description = dto.Description;
            product.Price = dto.Price; product.DiscountedPrice = dto.DiscountedPrice;
            product.ImageUrl = dto.ImageUrl; product.PreparationTime = dto.PreparationTime;
            product.Calories = dto.Calories;
            await _context.SaveChangesAsync();
            return Ok(new { message = "Updated successfully" });
        }

        // PUT api/products/{id}/toggle-availability  [Admin or Restaurant Desktop]
        [AllowAnonymous]
        [HttpPut("{id}/toggle-availability")]
        public async Task<IActionResult> ToggleAvailability(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.IsAvailable = !product.IsAvailable;
            await _context.SaveChangesAsync();
            return Ok(new { message = product.IsAvailable ? "Product is now available" : "Product is now unavailable", product.IsAvailable });
        }

        // DELETE api/products/{id}  [Admin or Restaurant Desktop]
        [AllowAnonymous]
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();
            product.IsActive = false; // Soft delete
            await _context.SaveChangesAsync();
            return Ok(new { message = "Product deleted" });
        }

        // POST api/products/{id}/variants
        [AllowAnonymous]
        [HttpPost("{id:int}/variants")]
        public async Task<IActionResult> SetVariants(int id, [FromBody] List<VariantDto>? variants)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();

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