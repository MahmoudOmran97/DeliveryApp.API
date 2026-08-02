using DeliveryApp.API.Authorization;
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DeliveryApp.API.Controllers;

// ─────────────────────────────────────────────────────────────────────────────
// شات ما قبل أوردر الروشتة: العميل بيرفع صورة الروشتة فيتعمل PrescriptionRequest،
// وبعدين هو وصاحب الصيدلية بيتفقوا على السعر عن طريق شات بسيط قبل ما يتحول
// لأوردر حقيقي. صاحب الصيدلية بيدخل بحساب Role=Restaurant وربطه بالمحل عن
// طريق Restaurants.OwnerUserId (نفس آلية RestaurantOwnerAuth الموجودة).
// ─────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PrescriptionRequestsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubService _hubService;
    private readonly IFcmService _fcm;

    public PrescriptionRequestsController(ApplicationDbContext context, IHubService hubService, IFcmService fcm)
    {
        _context = context;
        _hubService = hubService;
        _fcm = fcm;
    }

    private int GetUserId() => RestaurantOwnerAuth.GetUserId(User) ?? 0;
    private string? GetRole() => User.FindFirstValue(ClaimTypes.Role);

    // POST api/prescriptionrequests — العميل يبدأ طلب روشتة جديد بعد رفع الصورة
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePrescriptionRequestDto dto)
    {
        var userId = GetUserId();

        var restaurant = await _context.Restaurants.FirstOrDefaultAsync(r => r.Id == dto.RestaurantId && r.IsActive);
        if (restaurant == null) return BadRequest(new { message = "الصيدلية غير موجودة" });
        if (!restaurant.StoreType.Equals("Pharmacy", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "طلبات الروشتة متاحة للصيدليات بس" });

        if (string.IsNullOrWhiteSpace(dto.ImageUrl))
            return BadRequest(new { message = "لازم ترفع صورة الروشتة الأول" });

        var request = new PrescriptionRequest
        {
            CustomerId = userId,
            RestaurantId = dto.RestaurantId,
            ImageUrl = dto.ImageUrl,
            Notes = dto.Notes,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };
        _context.PrescriptionRequests.Add(request);
        await _context.SaveChangesAsync();

        // إشعار لصاحب الصيدلية إن فيه روشتة جديدة محتاجة تسعير
        if (restaurant.OwnerUserId.HasValue)
        {
            await _hubService.NotifyUserDirectly(restaurant.OwnerUserId.Value, "PrescriptionRequestReceived",
                new { request.Id, restaurant.Name });
            await _fcm.SendToUserAsync(restaurant.OwnerUserId.Value,
                "روشتة جديدة 💊", "عميل رفع روشتة جديدة وبينتظر التسعير",
                new Dictionary<string, string> { ["type"] = "PrescriptionRequest", ["prescriptionRequestId"] = request.Id.ToString() });
        }

        return Ok(new { request.Id, request.Status });
    }

    // GET api/prescriptionrequests/my — قائمة طلبات الروشتة بتاعة العميل
    [HttpGet("my")]
    public async Task<IActionResult> GetMy()
    {
        var userId = GetUserId();
        var list = await _context.PrescriptionRequests
            .Where(r => r.CustomerId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.RestaurantId,
                RestaurantName = r.Restaurant != null ? r.Restaurant.Name : null,
                r.ImageUrl,
                r.Notes,
                r.Status,
                r.AgreedPrice,
                r.OrderId,
                r.CreatedAt
            })
            .ToListAsync();

        return Ok(list);
    }

    // GET api/prescriptionrequests/restaurant — قائمة طلبات الروشتة بتاعة صيدلية صاحب الحساب
    [HttpGet("restaurant")]
    [Authorize(Roles = "Restaurant,Admin")]
    public async Task<IActionResult> GetForRestaurant()
    {
        var restaurantId = await RestaurantOwnerAuth.GetOwnerRestaurantIdAsync(User, _context);
        if (restaurantId == null) return BadRequest(new { message = "مفيش محل مرتبط بالحساب ده" });

        var list = await _context.PrescriptionRequests
            .Where(r => r.RestaurantId == restaurantId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.CustomerId,
                CustomerName = r.Customer != null ? r.Customer.FullName : null,
                r.ImageUrl,
                r.Notes,
                r.Status,
                r.AgreedPrice,
                r.CreatedAt
            })
            .ToListAsync();

        return Ok(list);
    }

    // GET api/prescriptionrequests/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var req = await _context.PrescriptionRequests.Include(r => r.Restaurant).FirstOrDefaultAsync(r => r.Id == id);
        if (req == null) return NotFound();

        var authError = await CheckAccessAsync(req);
        if (authError != null) return authError;

        return Ok(new
        {
            req.Id,
            req.CustomerId,
            req.RestaurantId,
            RestaurantName = req.Restaurant?.Name,
            req.ImageUrl,
            req.Notes,
            req.Status,
            req.AgreedPrice,
            req.OrderId,
            req.CreatedAt
        });
    }

    // GET api/prescriptionrequests/{id}/messages
    [HttpGet("{id}/messages")]
    public async Task<IActionResult> GetMessages(int id)
    {
        var req = await _context.PrescriptionRequests.FindAsync(id);
        if (req == null) return NotFound();

        var authError = await CheckAccessAsync(req);
        if (authError != null) return authError;

        var messages = await _context.PrescriptionMessages
            .Where(m => m.PrescriptionRequestId == id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.SenderRole, m.Message, m.CreatedAt })
            .ToListAsync();

        return Ok(messages);
    }

    // POST api/prescriptionrequests/{id}/messages
    [HttpPost("{id}/messages")]
    public async Task<IActionResult> SendMessage(int id, [FromBody] SendPrescriptionMessageDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Message)) return BadRequest(new { message = "اكتب رسالة" });

        var req = await _context.PrescriptionRequests.Include(r => r.Restaurant).FirstOrDefaultAsync(r => r.Id == id);
        if (req == null) return NotFound();

        var (authError, role) = await CheckAccessWithRoleAsync(req);
        if (authError != null) return authError;

        var userId = GetUserId();
        var msg = new PrescriptionMessage
        {
            PrescriptionRequestId = id,
            SenderId = userId,
            SenderRole = role!,
            Message = dto.Message.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _context.PrescriptionMessages.Add(msg);
        await _context.SaveChangesAsync();

        // إشعار للطرف التاني (عميل ↔ صيدلية) — بث فوري + push
        var recipientId = role == "Customer" ? req.Restaurant?.OwnerUserId : req.CustomerId;
        if (recipientId.HasValue)
        {
            await _hubService.NotifyUserDirectly(recipientId.Value, "PrescriptionMessageReceived",
                new { req.Id, msg.SenderRole, msg.Message, msg.CreatedAt });
            await _fcm.SendToUserAsync(recipientId.Value, "رسالة جديدة 💬", msg.Message,
                new Dictionary<string, string> { ["type"] = "PrescriptionMessage", ["prescriptionRequestId"] = req.Id.ToString() });
        }

        return Ok(new { msg.Id, msg.SenderRole, msg.Message, msg.CreatedAt });
    }

    // PUT api/prescriptionrequests/{id}/price — صاحب الصيدلية بيحدد تمن الفاتورة
    [HttpPut("{id}/price")]
    [Authorize(Roles = "Restaurant,Admin")]
    public async Task<IActionResult> SetPrice(int id, [FromBody] SetPrescriptionPriceDto dto)
    {
        if (dto.Price <= 0) return BadRequest(new { message = "السعر لازم يكون أكبر من صفر" });

        var req = await _context.PrescriptionRequests.Include(r => r.Restaurant).FirstOrDefaultAsync(r => r.Id == id);
        if (req == null) return NotFound();

        var role = GetRole();
        if (role != "Admin")
        {
            var restaurantId = await RestaurantOwnerAuth.GetOwnerRestaurantIdAsync(User, _context);
            if (restaurantId == null || restaurantId != req.RestaurantId)
                return new ObjectResult(new { message = "ليس لديك صلاحية على هذا الطلب" }) { StatusCode = 403 };
        }

        if (req.Status is "Ordered" or "Cancelled")
            return BadRequest(new { message = "الطلب ده اتقفل" });

        req.AgreedPrice = dto.Price;
        req.Status = "Priced";
        req.PricedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await _hubService.NotifyUserDirectly(req.CustomerId, "PrescriptionPriceSet",
            new { req.Id, req.AgreedPrice });
        await _fcm.SendToUserAsync(req.CustomerId, "تم تحديد تمن الروشتة 💊",
            $"صاحب الصيدلية حدد الفاتورة بـ {dto.Price:F0} جنيه — وافق عشان تكمل الأوردر",
            new Dictionary<string, string> { ["type"] = "PrescriptionPriced", ["prescriptionRequestId"] = req.Id.ToString() });

        return Ok(new { req.Id, req.Status, req.AgreedPrice });
    }

    // PUT api/prescriptionrequests/{id}/confirm — العميل يوافق على السعر
    [HttpPut("{id}/confirm")]
    public async Task<IActionResult> Confirm(int id)
    {
        var userId = GetUserId();
        var req = await _context.PrescriptionRequests.FirstOrDefaultAsync(r => r.Id == id && r.CustomerId == userId);
        if (req == null) return NotFound();

        if (req.Status != "Priced" || req.AgreedPrice is null)
            return BadRequest(new { message = "لسه مفيش سعر متحدد للروشتة دي" });

        req.Status = "Confirmed";
        req.ConfirmedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new { req.Id, req.Status, req.AgreedPrice });
    }

    // PUT api/prescriptionrequests/{id}/cancel
    [HttpPut("{id}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var req = await _context.PrescriptionRequests.FindAsync(id);
        if (req == null) return NotFound();

        var authError = await CheckAccessAsync(req);
        if (authError != null) return authError;

        if (req.Status == "Ordered") return BadRequest(new { message = "اتحول لأوردر بالفعل، مينفعش يتلغى" });

        req.Status = "Cancelled";
        await _context.SaveChangesAsync();
        return Ok(new { req.Id, req.Status });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private async Task<IActionResult?> CheckAccessAsync(PrescriptionRequest req)
    {
        var (result, _) = await CheckAccessWithRoleAsync(req);
        return result;
    }

    private async Task<(IActionResult? error, string? role)> CheckAccessWithRoleAsync(PrescriptionRequest req)
    {
        var userId = GetUserId();
        var role = GetRole();

        if (role == "Admin") return (null, "Pharmacy");

        if (req.CustomerId == userId) return (null, "Customer");

        if (role == "Restaurant")
        {
            var restaurantId = await RestaurantOwnerAuth.GetOwnerRestaurantIdAsync(User, _context);
            if (restaurantId.HasValue && restaurantId.Value == req.RestaurantId)
                return (null, "Pharmacy");
        }

        return (new ObjectResult(new { message = "ليس لديك صلاحية على هذا الطلب" }) { StatusCode = 403 }, null);
    }
}

public class CreatePrescriptionRequestDto
{
    public int RestaurantId { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class SendPrescriptionMessageDto
{
    public string Message { get; set; } = string.Empty;
}

public class SetPrescriptionPriceDto
{
    public decimal Price { get; set; }
}
