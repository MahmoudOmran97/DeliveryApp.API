using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

public class ChatMessage
{
    [Key]
    public int Id { get; set; }

    public int OrderId { get; set; }

    public int SenderId { get; set; }

    [Required]
    [StringLength(1000)]
    public string Message { get; set; } = null!;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [ForeignKey("OrderId")]
    public virtual Order Order { get; set; } = null!;

    [ForeignKey("SenderId")]
    public virtual User Sender { get; set; } = null!;
}
