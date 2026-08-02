using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

public class PrescriptionMessage
{
    [Key]
    public int Id { get; set; }

    public int PrescriptionRequestId { get; set; }

    public int SenderId { get; set; }

    // "Customer" أو "Pharmacy" — بيتحدد سيرفر-سايد حسب الـ role مش المرسل نفسه
    [StringLength(20)]
    public string SenderRole { get; set; } = "Customer";

    [StringLength(1000)]
    public string Message { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("PrescriptionRequestId")]
    public virtual PrescriptionRequest? PrescriptionRequest { get; set; }
}
