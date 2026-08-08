using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

// ─────────────────────────────────────────────────────────────────────────
// شات الدعم بتاع العميل: بيبدأ الـ AI بيرد لوحده، ولو الموضوع محتاج تدخل
// بشري الـ AI بيعمل Escalate فالحالة تتغير لـ "Escalated" ويظهر للأدمن في
// شاشة "شات الدعم" وياخد شات مفتوح مع العميل ده بالظبط (زي طلب اليوزر).
// ─────────────────────────────────────────────────────────────────────────
public class SupportSession
{
    [Key]
    public int Id { get; set; }

    public int CustomerId { get; set; }

    // AI → الـ AI هو اللي بيرد. Escalated → محتاج/بيتابعه أدمن. Closed → اتقفل.
    [StringLength(20)]
    public string Status { get; set; } = "AI";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("CustomerId")]
    public virtual User? Customer { get; set; }
}
