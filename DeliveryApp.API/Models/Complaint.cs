using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

// ─────────────────────────────────────────────────────────────────────────
// شكوى عميل: ممكن تتعمل يدوي من العميل نفسه من التطبيق، أو تتعمل تلقائي
// بمعرفة الـ AI جوا شات الدعم لما يفهم إن العميل بيشتكي من حاجة (Source = AI).
// ─────────────────────────────────────────────────────────────────────────
public class Complaint
{
    [Key]
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public int? OrderId { get; set; }

    public int? SupportSessionId { get; set; }

    [StringLength(200)]
    public string Subject { get; set; } = null!;

    [StringLength(2000)]
    public string Description { get; set; } = null!;

    // Open → جديدة. InProgress → الأدمن بيتابعها. Resolved → اتحلت. Closed → اتقفلت.
    [StringLength(20)]
    public string Status { get; set; } = "Open";

    // Customer → العميل كتبها بنفسه. AI → الـ AI عملها من كلام العميل في الشات.
    [StringLength(20)]
    public string Source { get; set; } = "Customer";

    [StringLength(1000)]
    public string? AdminNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    [ForeignKey("CustomerId")]
    public virtual User? Customer { get; set; }

    [ForeignKey("OrderId")]
    public virtual Order? Order { get; set; }
}
