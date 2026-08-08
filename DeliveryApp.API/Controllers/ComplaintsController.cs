using DeliveryApp.API.Authorization;
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ComplaintsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly INotificationDispatcher _dispatcher;

    public ComplaintsController(ApplicationDbContext context, INotificationDispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    private int GetUserId() => RestaurantOwnerAuth.GetUserId(User) ?? 0;

    // POST api/complaints — العميل بيسجل شكوى بنفسه من التطبيق
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateComplaintDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Subject) || string.IsNullOrWhiteSpace(dto.Description))
            return BadRequest(new { message = "لازم تكتب عنوان ووصف للشكوى" });

        var userId = GetUserId();
        var complaint = new Complaint
        {
            CustomerId = userId,
            OrderId = dto.OrderId,
            Subject = dto.Subject.Trim(),
            Description = dto.Description.Trim(),
            Status = "Open",
            Source = "Customer",
            CreatedAt = DateTime.UtcNow
        };
        _context.Complaints.Add(complaint);
        await _context.SaveChangesAsync();

        var customer = await _context.Users.FindAsync(userId);
        await _dispatcher.NotifyAdminsAsync(
            "شكوى جديدة من العميل 📝",
            $"{customer?.FullName}: {complaint.Subject}",
            "Complaint",
            complaint.OrderId,
            $"complaint/{complaint.Id}");

        return Ok(new { complaint.Id, complaint.Status });
    }

    // GET api/complaints/my — شكاوى العميل نفسه
    [HttpGet("my")]
    public async Task<IActionResult> GetMy()
    {
        var userId = GetUserId();
        var list = await _context.Complaints
            .Where(c => c.CustomerId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.Subject,
                c.Description,
                c.Status,
                c.Source,
                c.OrderId,
                c.AdminNote,
                c.CreatedAt,
                c.ResolvedAt
            })
            .ToListAsync();

        return Ok(list);
    }

    // GET api/complaints/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var isAdmin = User.IsInRole("Admin");
        var userId = GetUserId();

        var c = await _context.Complaints.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!isAdmin && c.CustomerId != userId) return Forbid();

        return Ok(new
        {
            c.Id,
            c.CustomerId,
            CustomerName = c.Customer?.FullName,
            CustomerPhone = c.Customer?.Phone,
            c.Subject,
            c.Description,
            c.Status,
            c.Source,
            c.OrderId,
            c.SupportSessionId,
            c.AdminNote,
            c.CreatedAt,
            c.ResolvedAt
        });
    }

    // ── Admin ────────────────────────────────────────────────────────────

    // GET api/complaints/admin — كل الشكاوى (لوحة الأدمن)
    [HttpGet("admin")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllAdmin([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var query = _context.Complaints.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(c => c.Status == status);

        var total = await query.CountAsync();
        var list = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.CustomerId,
                CustomerName = c.Customer != null ? c.Customer.FullName : null,
                c.Subject,
                c.Description,
                c.Status,
                c.Source,
                c.OrderId,
                c.SupportSessionId,
                c.AdminNote,
                c.CreatedAt,
                c.ResolvedAt
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, items = list });
    }

    // PUT api/complaints/{id}/status — الأدمن يحدث حالة الشكوى/يضيف ملاحظة
    [HttpPut("{id}/status")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateComplaintStatusDto dto)
    {
        var validStatuses = new[] { "Open", "InProgress", "Resolved", "Closed" };
        if (!validStatuses.Contains(dto.Status))
            return BadRequest(new { message = "حالة غير معروفة" });

        var c = await _context.Complaints.FindAsync(id);
        if (c == null) return NotFound();

        c.Status = dto.Status;
        if (!string.IsNullOrWhiteSpace(dto.AdminNote))
            c.AdminNote = dto.AdminNote.Trim();
        if (dto.Status is "Resolved" or "Closed")
            c.ResolvedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        await _dispatcher.NotifyUserAsync(c.CustomerId,
            "تحديث على شكواك 📝",
            $"شكوى \"{c.Subject}\" بقت: {dto.Status}",
            "ComplaintUpdate",
            c.OrderId,
            $"complaint/{c.Id}");

        return Ok(new { c.Id, c.Status });
    }
}

public class CreateComplaintDto
{
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public int? OrderId { get; set; }
}

public class UpdateComplaintStatusDto
{
    public string Status { get; set; } = "Open";
    public string? AdminNote { get; set; }
}
