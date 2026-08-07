using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Services;

// ─────────────────────────────────────────────────────────────────────────
// بيعمل 3 حاجات مرة واحدة لأي تنبيه: يحفظه في جدول Notifications (عشان يفضل
// موجود في تاريخ التنبيهات وجرس الأدمن/صاحب المحل)، يبعته لحظي عن طريق
// الـ Hub (user_{id} group) عشان الجرس يتحدث فورًا من غير ريفريش، وبعدين
// FCM push للموبايل لو التطبيق مقفول.
// ─────────────────────────────────────────────────────────────────────────
public interface INotificationDispatcher
{
    Task<Notification> NotifyUserAsync(int userId, string title, string body, string type, int? orderId = null, string? actionUrl = null);
    Task NotifyAdminsAsync(string title, string body, string type, int? orderId = null, string? actionUrl = null);
}

public class NotificationDispatcher : INotificationDispatcher
{
    private readonly ApplicationDbContext _context;
    private readonly IHubService _hubService;
    private readonly IFcmService _fcm;

    public NotificationDispatcher(ApplicationDbContext context, IHubService hubService, IFcmService fcm)
    {
        _context = context;
        _hubService = hubService;
        _fcm = fcm;
    }

    public async Task<Notification> NotifyUserAsync(int userId, string title, string body, string type, int? orderId = null, string? actionUrl = null)
    {
        var notif = new Notification
        {
            UserId = userId,
            Title = title,
            Body = body,
            Type = type,
            OrderId = orderId,
            ActionUrl = string.IsNullOrWhiteSpace(actionUrl) ? null : actionUrl.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Notifications.Add(notif);
        await _context.SaveChangesAsync();

        // الجرس بتاع الأدمن بورتال / صاحب المحل هيستقبل الحدث ده لحظي
        await _hubService.NotifyUserDirectly(userId, "NewNotification", new
        {
            notif.Id,
            notif.Title,
            notif.Body,
            notif.Type,
            notif.OrderId,
            notif.ActionUrl,
            notif.IsRead,
            notif.CreatedAt
        });

        // actionUrl بيتبعت جوه data payload الـ FCM عشان تطبيق الكستمر/الدرايفر
        // يقدر يوجّه المستخدم مكان محدد لما يدوس على الإشعار (نفس نظام البانرات)
        var data = new Dictionary<string, string> { ["type"] = type, ["orderId"] = orderId?.ToString() ?? "" };
        if (!string.IsNullOrWhiteSpace(notif.ActionUrl))
            data["actionUrl"] = notif.ActionUrl;

        await _fcm.SendToUserAsync(userId, title, body, data, _context);

        return notif;
    }

    public async Task NotifyAdminsAsync(string title, string body, string type, int? orderId = null, string? actionUrl = null)
    {
        var adminIds = await _context.Users
            .Where(u => u.Role == "Admin" && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync();

        foreach (var adminId in adminIds)
            await NotifyUserAsync(adminId, title, body, type, orderId, actionUrl);
    }
}
