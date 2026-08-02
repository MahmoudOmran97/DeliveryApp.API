using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

// ─────────────────────────────────────────────────────────────────────────────
// طلب روشتة قبل ما يتحول لأوردر فعلي: العميل بيرفع صورة الروشتة، وبعدين بيتفتح
// شات بينه وبين صاحب الصيدلية عشان يتفقوا على تمن الفاتورة. لما صاحب الصيدلية
// يحدد السعر (AgreedPrice) والعميل يوافق، الطلب يبقى "Confirmed" ويقدر
// يتحول لأوردر حقيقي بنفس السعر ده.
// ─────────────────────────────────────────────────────────────────────────────
public class PrescriptionRequest
{
    [Key]
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public int RestaurantId { get; set; } // لازم يكون StoreType == "Pharmacy"

    [StringLength(500)]
    public string ImageUrl { get; set; } = null!;

    [StringLength(1000)]
    public string? Notes { get; set; }

    // Pending → لسه مفيش سعر متحدد
    // Priced  → صاحب الصيدلية حدد السعر وبينتظر موافقة العميل
    // Confirmed → العميل وافق، جاهز يتحول لأوردر
    // Ordered → اتحول لأوردر فعلاً
    // Cancelled → اتلغى
    [StringLength(20)]
    public string Status { get; set; } = "Pending";

    [Column(TypeName = "decimal(10,2)")]
    public decimal? AgreedPrice { get; set; }

    public int? OrderId { get; set; } // بيتحدد لما يتحول لأوردر

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PricedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }

    [ForeignKey("CustomerId")]
    public virtual User? Customer { get; set; }

    [ForeignKey("RestaurantId")]
    public virtual Restaurant? Restaurant { get; set; }
}
