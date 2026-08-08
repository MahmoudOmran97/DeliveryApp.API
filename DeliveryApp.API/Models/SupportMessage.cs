using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

public class SupportMessage
{
    [Key]
    public int Id { get; set; }

    public int SessionId { get; set; }

    // "Customer" أو "AI" أو "Admin"
    [StringLength(20)]
    public string SenderRole { get; set; } = "Customer";

    // متبعتة بس لما SenderRole = Admin (مين من الأدمنز رد)
    public int? SenderId { get; set; }

    [StringLength(2000)]
    public string Message { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("SessionId")]
    public virtual SupportSession? Session { get; set; }
}
