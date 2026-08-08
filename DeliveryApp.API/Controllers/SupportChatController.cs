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
public class SupportChatController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IAiSupportService _ai;
    private readonly IHubService _hub;
    private readonly IFcmService _fcm;
    private readonly INotificationDispatcher _dispatcher;

    public SupportChatController(ApplicationDbContext context, IAiSupportService ai, IHubService hub,
        IFcmService fcm, INotificationDispatcher dispatcher)
    {
        _context = context;
        _ai = ai;
        _hub = hub;
        _fcm = fcm;
        _dispatcher = dispatcher;
    }

    private int GetUserId() => RestaurantOwnerAuth.GetUserId(User) ?? 0;

    // GET api/supportchat/session — بيرجع (أو يعمل) شات الدعم المفتوح بتاع العميل
    [HttpGet("session")]
    public async Task<IActionResult> GetOrCreateSession()
    {
        var userId = GetUserId();
        var session = await _context.SupportSessions
            .Where(s => s.CustomerId == userId && s.Status != "Closed")
            .OrderByDescending(s => s.LastMessageAt)
            .FirstOrDefaultAsync();

        if (session == null)
        {
            session = new SupportSession { CustomerId = userId, Status = "AI", CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow };
            _context.SupportSessions.Add(session);
            await _context.SaveChangesAsync();
        }

        var messages = await _context.SupportMessages
            .Where(m => m.SessionId == session.Id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.SenderRole, m.Message, m.CreatedAt })
            .ToListAsync();

        return Ok(new { session.Id, session.Status, messages });
    }

    // POST api/supportchat/{sessionId}/messages — العميل بيبعت رسالة
    [HttpPost("{sessionId}/messages")]
    public async Task<IActionResult> SendMessage(int sessionId, [FromBody] SendSupportMessageDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Message)) return BadRequest(new { message = "اكتب رسالة" });

        var userId = GetUserId();
        var session = await _context.SupportSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.CustomerId == userId);
        if (session == null) return NotFound();

        var customerMsg = new SupportMessage
        {
            SessionId = sessionId,
            SenderRole = "Customer",
            Message = dto.Message.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _context.SupportMessages.Add(customerMsg);
        session.LastMessageAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // لو الشات اتحول لأدمن بالفعل، الرسالة بس بتتحفظ وبتتبعت للأدمن لحظيًا —
        // الـ AI مابيردش تاني عشان مايتقاطعش مع الأدمن
        if (session.Status == "Escalated")
        {
            await NotifyAssignedAdminsAsync(session, customerMsg.Message);
            return Ok(new { customerMsg.Id, customerMsg.SenderRole, customerMsg.Message, customerMsg.CreatedAt, aiReply = (object?)null });
        }

        var customer = await _context.Users.FindAsync(userId);
        var history = await _context.SupportMessages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        var aiResult = await _ai.GetReplyAsync(session, history, customer!);

        var aiMsg = new SupportMessage
        {
            SessionId = sessionId,
            SenderRole = "AI",
            Message = aiResult.ReplyText,
            CreatedAt = DateTime.UtcNow
        };
        _context.SupportMessages.Add(aiMsg);
        session.LastMessageAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            customerMsg.Id,
            aiReply = new { aiMsg.Id, aiMsg.SenderRole, aiMsg.Message, aiMsg.CreatedAt },
            escalated = aiResult.Escalated,
            complaintId = aiResult.CreatedComplaintId
        });
    }

    private async Task NotifyAssignedAdminsAsync(SupportSession session, string message)
    {
        var customer = await _context.Users.FindAsync(session.CustomerId);
        await _dispatcher.NotifyAdminsAsync(
            $"رسالة جديدة من {customer?.FullName} 💬",
            message,
            "SupportMessage",
            null,
            $"supportchat/{session.Id}");
    }

    // ── Admin ────────────────────────────────────────────────────────────

    // GET api/supportchat/admin?status=Escalated — كل شاتات الدعم (فلترة بالحالة)
    [HttpGet("admin")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllAdmin([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var query = _context.SupportSessions.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(s => s.Status == status);

        var total = await query.CountAsync();
        var list = await query
            .OrderByDescending(s => s.LastMessageAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.CustomerId,
                CustomerName = s.Customer != null ? s.Customer.FullName : null,
                s.Status,
                s.CreatedAt,
                s.LastMessageAt,
                LastMessage = _context.SupportMessages.Where(m => m.SessionId == s.Id).OrderByDescending(m => m.CreatedAt).Select(m => m.Message).FirstOrDefault()
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, items = list });
    }

    // GET api/supportchat/admin/{id} — تفاصيل + رسائل شات معين (شاشة الأدمن اللي بتفتح من الإشعار)
    [HttpGet("admin/{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetSessionAdmin(int id)
    {
        var session = await _context.SupportSessions.Include(s => s.Customer).FirstOrDefaultAsync(s => s.Id == id);
        if (session == null) return NotFound();

        var messages = await _context.SupportMessages
            .Where(m => m.SessionId == id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.SenderRole, m.SenderId, m.Message, m.CreatedAt })
            .ToListAsync();

        return Ok(new
        {
            session.Id,
            session.CustomerId,
            CustomerName = session.Customer != null ? session.Customer.FullName : null,
            CustomerPhone = session.Customer != null ? session.Customer.Phone : null,
            session.Status,
            session.CreatedAt,
            session.LastMessageAt,
            messages
        });
    }

    // POST api/supportchat/admin/{id}/messages — الأدمن يرد على العميل مباشرة
    [HttpPost("admin/{id}/messages")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AdminReply(int id, [FromBody] SendSupportMessageDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Message)) return BadRequest(new { message = "اكتب رسالة" });

        var session = await _context.SupportSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null) return NotFound();

        var adminId = GetUserId();
        var msg = new SupportMessage
        {
            SessionId = id,
            SenderRole = "Admin",
            SenderId = adminId,
            Message = dto.Message.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _context.SupportMessages.Add(msg);
        session.Status = "Escalated"; // أي رد أدمن بيثبت إن الشات بقى تحت المتابعة البشرية
        session.LastMessageAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await _hub.NotifyUserDirectly(session.CustomerId, "SupportMessageReceived",
            new { session.Id, msg.SenderRole, msg.Message, msg.CreatedAt });
        await _fcm.SendToUserAsync(session.CustomerId, "رد من فريق الدعم 💬", msg.Message,
            new Dictionary<string, string> { ["type"] = "SupportMessage", ["actionUrl"] = $"chat/support/{session.Id}" });

        return Ok(new { msg.Id, msg.SenderRole, msg.Message, msg.CreatedAt });
    }

    // PUT api/supportchat/admin/{id}/close — الأدمن يقفل الشات بعد ما يحل المشكلة
    [HttpPut("admin/{id}/close")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Close(int id)
    {
        var session = await _context.SupportSessions.FindAsync(id);
        if (session == null) return NotFound();
        session.Status = "Closed";
        await _context.SaveChangesAsync();
        return Ok(new { session.Id, session.Status });
    }
}

public class SendSupportMessageDto
{
    public string Message { get; set; } = "";
}
