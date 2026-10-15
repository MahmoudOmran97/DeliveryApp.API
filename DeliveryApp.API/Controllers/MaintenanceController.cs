using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers;

/// <summary>
/// أدوات تنظيف الداتابيز — سوبر أدمن فقط.
/// الهدف: تقليل حجم الجداول الكبيرة من غير ما نكسر الأوردرات النشطة أو السجلات المالية.
/// </summary>
[ApiController]
[Route("api/maintenance")]
[Authorize(Roles = "Admin")]
public class MaintenanceController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private static readonly string[] TerminalOrderStatuses = { "Delivered", "Cancelled", "Rejected" };

    public MaintenanceController(ApplicationDbContext context) => _context = context;

    private bool IsRequesterSuperAdmin()
    {
        var claim = User.Claims.FirstOrDefault(x => x.Type == "IsSuperAdmin");
        return claim != null && string.Equals(claim.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private IActionResult? RequireSuperAdmin() =>
        IsRequesterSuperAdmin() ? null : StatusCode(403, new { message = "SuperAdmin only" });

    private static DateTime CutoffUtc(int olderThanDays) =>
        DateTime.UtcNow.AddDays(-Math.Max(1, olderThanDays));

    // ─── نظرة عامة: كام صف هيتحذف لو نظّفت ───────────────────────────────
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] int olderThanDays = 30)
    {
        if (RequireSuperAdmin() is { } denied) return denied;
        var cutoff = CutoffUtc(olderThanDays);

        var terminalOrders = _context.Orders
            .Where(o => TerminalOrderStatuses.Contains(o.Status) && o.CreatedAt < cutoff);

        return Ok(new
        {
            olderThanDays,
            cutoffUtc = cutoff,
            notifications = await _context.Notifications.CountAsync(n => n.CreatedAt < cutoff),
            terminalOrders = await terminalOrders.CountAsync(),
            orderChatMessages = await _context.ChatMessages.CountAsync(m => m.Timestamp < cutoff),
            pharmacyMessages = await _context.PrescriptionMessages.CountAsync(m => m.CreatedAt < cutoff),
            pharmacyRequests = await _context.PrescriptionRequests.CountAsync(r =>
                r.CreatedAt < cutoff && (r.Status == "Cancelled" || r.Status == "Ordered")
                && (r.OrderId == null || !_context.Orders.Any(o => o.Id == r.OrderId))),
            supportMessages = await _context.SupportMessages.CountAsync(m => m.CreatedAt < cutoff),
            supportSessions = await _context.SupportSessions.CountAsync(s =>
                s.Status == "Closed" && s.LastMessageAt < cutoff),
            otpCodes = await _context.OtpCodes.CountAsync(o => o.IsUsed || o.ExpiresAt < DateTime.UtcNow),
            driverLocations = await _context.DriverLocations.CountAsync(l => l.Timestamp < cutoff),
            drivers = await _context.Drivers.CountAsync(),
            admins = await _context.Users.CountAsync(u => u.Role == "Admin")
        });
    }

    // ─── حذف إشعارات قديمة ────────────────────────────────────────────────
    [HttpPost("purge-notifications")]
    public async Task<IActionResult> PurgeNotifications([FromQuery] int olderThanDays = 30)
    {
        if (RequireSuperAdmin() is { } denied) return denied;
        var cutoff = CutoffUtc(olderThanDays);

        var rows = await _context.Notifications.Where(n => n.CreatedAt < cutoff).ToListAsync();
        _context.Notifications.RemoveRange(rows);
        await _context.SaveChangesAsync();

        return Ok(new { deleted = rows.Count, olderThanDays, cutoffUtc = cutoff });
    }

    // ─── حذف شات الطلبات (عميل ↔ دليفري) ─────────────────────────────────
    [HttpPost("purge-order-chats")]
    public async Task<IActionResult> PurgeOrderChats([FromQuery] int olderThanDays = 30)
    {
        if (RequireSuperAdmin() is { } denied) return denied;
        var cutoff = CutoffUtc(olderThanDays);

        var rows = await _context.ChatMessages.Where(m => m.Timestamp < cutoff).ToListAsync();
        _context.ChatMessages.RemoveRange(rows);
        await _context.SaveChangesAsync();

        return Ok(new { deleted = rows.Count, olderThanDays, cutoffUtc = cutoff });
    }

    // ─── حذف شات الصيدلية (رسائل الروشتة + الطلبات المنتهية) ───────────────
    [HttpPost("purge-pharmacy-chats")]
    public async Task<IActionResult> PurgePharmacyChats([FromQuery] int olderThanDays = 30)
    {
        if (RequireSuperAdmin() is { } denied) return denied;
        var cutoff = CutoffUtc(olderThanDays);

        var messages = await _context.PrescriptionMessages
            .Where(m => m.CreatedAt < cutoff)
            .ToListAsync();
        _context.PrescriptionMessages.RemoveRange(messages);

        // طلبات ملغية/قديمة ومفيش أوردر شغال مربوط بيها
        var orphanRequests = await _context.PrescriptionRequests
            .Where(r => r.CreatedAt < cutoff
                        && (r.Status == "Cancelled" || r.Status == "Ordered")
                        && (r.OrderId == null || !_context.Orders.Any(o => o.Id == r.OrderId)))
            .ToListAsync();

        var reqIds = orphanRequests.Select(r => r.Id).ToList();
        if (reqIds.Count > 0)
        {
            var leftover = await _context.PrescriptionMessages
                .Where(m => reqIds.Contains(m.PrescriptionRequestId))
                .ToListAsync();
            _context.PrescriptionMessages.RemoveRange(leftover);

            var linkedOrders = await _context.Orders
                .Where(o => o.PrescriptionRequestId != null && reqIds.Contains(o.PrescriptionRequestId.Value))
                .ToListAsync();
            foreach (var o in linkedOrders)
                o.PrescriptionRequestId = null;

            _context.PrescriptionRequests.RemoveRange(orphanRequests);
        }

        await _context.SaveChangesAsync();

        return Ok(new
        {
            messagesDeleted = messages.Count,
            requestsDeleted = orphanRequests.Count,
            olderThanDays,
            cutoffUtc = cutoff
        });
    }

    // ─── حذف شاتات الدعم (العميل / AI / أدمن) ─────────────────────────────
    [HttpPost("purge-support-chats")]
    public async Task<IActionResult> PurgeSupportChats([FromQuery] int olderThanDays = 30)
    {
        if (RequireSuperAdmin() is { } denied) return denied;
        var cutoff = CutoffUtc(olderThanDays);

        // رسائل قديمة في أي جلسة
        var oldMessages = await _context.SupportMessages
            .Where(m => m.CreatedAt < cutoff)
            .ToListAsync();
        _context.SupportMessages.RemoveRange(oldMessages);

        // جلسات مقفولة قديمة + كل رسائلها المتبقية
        var closedSessions = await _context.SupportSessions
            .Where(s => s.Status == "Closed" && s.LastMessageAt < cutoff)
            .ToListAsync();
        var sessionIds = closedSessions.Select(s => s.Id).ToList();
        if (sessionIds.Count > 0)
        {
            var sessionMsgs = await _context.SupportMessages
                .Where(m => sessionIds.Contains(m.SessionId))
                .ToListAsync();
            _context.SupportMessages.RemoveRange(sessionMsgs);
            _context.SupportSessions.RemoveRange(closedSessions);
        }

        await _context.SaveChangesAsync();

        return Ok(new
        {
            messagesDeleted = oldMessages.Count,
            sessionsDeleted = closedSessions.Count,
            olderThanDays,
            cutoffUtc = cutoff
        });
    }

    // ─── حذف طلبات منتهية قديمة (+ التوابع) ───────────────────────────────
    [HttpPost("purge-orders")]
    public async Task<IActionResult> PurgeOrders([FromQuery] int olderThanDays = 30)
    {
        if (RequireSuperAdmin() is { } denied) return denied;
        var cutoff = CutoffUtc(olderThanDays);

        var orderIds = await _context.Orders
            .Where(o => TerminalOrderStatuses.Contains(o.Status) && o.CreatedAt < cutoff)
            .Select(o => o.Id)
            .ToListAsync();

        if (orderIds.Count == 0)
            return Ok(new { deleted = 0, olderThanDays, cutoffUtc = cutoff });

        // توابع قبل مسح الأوردر
        _context.ChatMessages.RemoveRange(
            await _context.ChatMessages.Where(m => orderIds.Contains(m.OrderId)).ToListAsync());

        _context.DriverLocations.RemoveRange(
            await _context.DriverLocations.Where(l => l.OrderId != null && orderIds.Contains(l.OrderId.Value)).ToListAsync());

        _context.Complaints.RemoveRange(
            await _context.Complaints.Where(c => c.OrderId != null && orderIds.Contains(c.OrderId.Value)).ToListAsync());

        _context.Notifications.RemoveRange(
            await _context.Notifications.Where(n => n.OrderId != null && orderIds.Contains(n.OrderId.Value)).ToListAsync());

        _context.PointTransactions.RemoveRange(
            await _context.PointTransactions.Where(t => t.OrderId != null && orderIds.Contains(t.OrderId.Value)).ToListAsync());

        _context.Ratings.RemoveRange(
            await _context.Ratings.Where(r => orderIds.Contains(r.OrderId)).ToListAsync());

        _context.Payments.RemoveRange(
            await _context.Payments.Where(p => orderIds.Contains(p.OrderId)).ToListAsync());

        _context.OrderItems.RemoveRange(
            await _context.OrderItems.Where(i => orderIds.Contains(i.OrderId)).ToListAsync());

        // فك ربط الروشتات
        var rx = await _context.PrescriptionRequests
            .Where(r => r.OrderId != null && orderIds.Contains(r.OrderId.Value))
            .ToListAsync();
        foreach (var r in rx) r.OrderId = null;

        var orders = await _context.Orders.Where(o => orderIds.Contains(o.Id)).ToListAsync();
        foreach (var o in orders) o.PrescriptionRequestId = null;

        await _context.SaveChangesAsync();

        _context.Orders.RemoveRange(orders);
        await _context.SaveChangesAsync();

        return Ok(new { deleted = orders.Count, olderThanDays, cutoffUtc = cutoff });
    }

    // ─── تنظيف OTP منتهية/مستخدمة ─────────────────────────────────────────
    [HttpPost("purge-otps")]
    public async Task<IActionResult> PurgeOtps()
    {
        if (RequireSuperAdmin() is { } denied) return denied;

        var rows = await _context.OtpCodes
            .Where(o => o.IsUsed || o.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        _context.OtpCodes.RemoveRange(rows);
        await _context.SaveChangesAsync();

        return Ok(new { deleted = rows.Count });
    }

    // ─── تنظيف مواقع السائقين القديمة ─────────────────────────────────────
    [HttpPost("purge-driver-locations")]
    public async Task<IActionResult> PurgeDriverLocations([FromQuery] int olderThanDays = 14)
    {
        if (RequireSuperAdmin() is { } denied) return denied;
        var cutoff = CutoffUtc(olderThanDays);

        var rows = await _context.DriverLocations.Where(l => l.Timestamp < cutoff).ToListAsync();
        _context.DriverLocations.RemoveRange(rows);
        await _context.SaveChangesAsync();

        return Ok(new { deleted = rows.Count, olderThanDays, cutoffUtc = cutoff });
    }

    // ─── حذف سائق (مع حسابه) ──────────────────────────────────────────────
    [HttpDelete("drivers/{driverId:int}")]
    public async Task<IActionResult> DeleteDriver(int driverId)
    {
        if (RequireSuperAdmin() is { } denied) return denied;

        var driver = await _context.Drivers
            .Include(d => d.User)
            .FirstOrDefaultAsync(d => d.Id == driverId);

        if (driver == null)
            return NotFound(new { message = "Driver not found" });

        var hasActiveOrders = await _context.Orders.AnyAsync(o =>
            o.DriverId == driverId && !TerminalOrderStatuses.Contains(o.Status));
        if (hasActiveOrders)
            return BadRequest(new { message = "السائق عنده طلبات شغالة — خلّصها أو غيّر حالتها الأول" });

        // فك ربط الأوردرات المنتهية
        var linkedOrders = await _context.Orders.Where(o => o.DriverId == driverId).ToListAsync();
        foreach (var o in linkedOrders) o.DriverId = null;

        _context.DriverLocations.RemoveRange(
            await _context.DriverLocations.Where(l => l.DriverId == driverId).ToListAsync());

        // تقييمات فيها DriverId — امسحها أو فك الربط
        var ratings = await _context.Ratings.Where(r => r.DriverId == driverId).ToListAsync();
        foreach (var r in ratings) r.DriverId = null;

        var settlements = await _context.RevenueSettlements.Where(s => s.DriverId == driverId).ToListAsync();
        foreach (var s in settlements) s.DriverId = null;

        var user = driver.User;
        _context.Drivers.Remove(driver);

        if (user != null)
        {
            _context.Notifications.RemoveRange(
                await _context.Notifications.Where(n => n.UserId == user.Id).ToListAsync());
            _context.OtpCodes.RemoveRange(
                await _context.OtpCodes.Where(o => o.Email == user.Email).ToListAsync());

            user.FullName = "سائق محذوف";
            user.Email = $"deleted_driver_{user.Id}_{Guid.NewGuid():N}@deleted.local";
            user.Phone = $"deleted_{user.Id}";
            user.Fcmtoken = null;
            user.ProfileImageUrl = null;
            user.IsActive = false;
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());
            user.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return Ok(new { message = "Driver deleted", driverId });
    }

    // ─── حذف أدمن ─────────────────────────────────────────────────────────
    [HttpDelete("admins/{userId:int}")]
    public async Task<IActionResult> DeleteAdmin(int userId)
    {
        if (RequireSuperAdmin() is { } denied) return denied;

        var meClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                      ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var me = int.TryParse(meClaim, out var mid) ? mid : 0;
        if (userId == me)
            return BadRequest(new { message = "مينفعش تحذف حسابك أنت" });

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.Role == "Admin");
        if (user == null)
            return NotFound(new { message = "Admin not found" });

        if (user.IsSuperAdmin)
        {
            var otherSuper = await _context.Users.CountAsync(u =>
                u.Role == "Admin" && u.IsSuperAdmin && u.Id != userId && u.IsActive);
            if (otherSuper == 0)
                return BadRequest(new { message = "مينفعش تحذف آخر سوبر أدمن في النظام" });
        }

        _context.Notifications.RemoveRange(
            await _context.Notifications.Where(n => n.UserId == userId).ToListAsync());

        // رسائل الدعم اللي بعتها الأدمن — نسيبها لكن نفك SenderId
        var supportMsgs = await _context.SupportMessages.Where(m => m.SenderId == userId).ToListAsync();
        foreach (var m in supportMsgs) m.SenderId = null;

        _context.OtpCodes.RemoveRange(
            await _context.OtpCodes.Where(o => o.Email == user.Email).ToListAsync());

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Admin deleted", userId });
    }

    // ─── قوائم مساعدة للواجهة ─────────────────────────────────────────────
    [HttpGet("drivers")]
    public async Task<IActionResult> ListDrivers()
    {
        if (RequireSuperAdmin() is { } denied) return denied;

        var list = await _context.Drivers
            .Include(d => d.User)
            .OrderByDescending(d => d.Id)
            .Select(d => new
            {
                d.Id,
                d.UserId,
                Name = d.User.FullName,
                Email = d.User.Email,
                Phone = d.User.Phone,
                d.IsVerified,
                d.IsOnline,
                UserActive = d.User.IsActive,
                OrdersCount = d.Orders.Count
            })
            .ToListAsync();

        return Ok(list);
    }

    [HttpGet("admins")]
    public async Task<IActionResult> ListAdmins()
    {
        if (RequireSuperAdmin() is { } denied) return denied;

        var list = await _context.Users
            .Where(u => u.Role == "Admin")
            .OrderByDescending(u => u.IsSuperAdmin)
            .ThenBy(u => u.FullName)
            .Select(u => new
            {
                u.Id,
                u.FullName,
                u.Email,
                u.Phone,
                u.IsSuperAdmin,
                u.IsActive,
                u.CreatedAt
            })
            .ToListAsync();

        return Ok(list);
    }
}
