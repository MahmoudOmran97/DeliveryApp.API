using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class PaymentsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        public PaymentsController(ApplicationDbContext context) => _context = context;

        private int GetUserId() =>
            int.Parse(User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier
                                          || c.Type == "sub").Value);

        // ─────────────────────────────────────────────
        // GET api/payments/order/{orderId}
        // تفاصيل الدفع لطلب معين
        // ─────────────────────────────────────────────
        [HttpGet("order/{orderId}")]
        public async Task<IActionResult> GetByOrder(int orderId)
        {
            var userId = GetUserId();

            var order = await _context.Orders
                .AnyAsync(o => o.Id == orderId && o.CustomerId == userId);
            if (!order) return NotFound(new { message = "Order not found" });

            var payment = await _context.Payments
                .Where(p => p.OrderId == orderId)
                .Select(p => new
                {
                    p.Id,
                    p.Provider,
                    p.TransactionId,
                    p.Amount,
                    p.Currency,
                    p.Status,
                    p.PaidAt,
                    p.CreatedAt
                })
                .FirstOrDefaultAsync();

            if (payment == null) return NotFound(new { message = "Payment not found" });
            return Ok(payment);
        }

        // ─────────────────────────────────────────────
        // POST api/payments/confirm  — تأكيد الدفع بعد الـ Paymob callback
        // ─────────────────────────────────────────────
        [HttpPost("confirm")]
        public async Task<IActionResult> ConfirmPayment([FromBody] ConfirmPaymentDto dto)
        {
            var payment = await _context.Payments
                .Include(p => p.Order)
                .FirstOrDefaultAsync(p => p.OrderId == dto.OrderId);

            if (payment == null) return NotFound(new { message = "Payment not found" });

            if (payment.Status == "Completed")
                return BadRequest(new { message = "Payment already completed" });

            payment.TransactionId = dto.TransactionId;
            payment.Status = "Completed";
            payment.PaidAt = DateTime.UtcNow;

            // تحديث حالة الدفع في الطلب
            payment.Order.PaymentStatus = "Paid";

            // نوتيفيكيشن للعميل
            _context.Notifications.Add(new Notification
            {
                UserId = payment.Order.CustomerId,
                Title = "Payment Successful",
                Body = $"Payment of {payment.Amount} EGP confirmed.",
                Type = "PaymentSuccess",
                OrderId = payment.OrderId,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
            return Ok(new { message = "Payment confirmed", payment.Status });
        }

        // ─────────────────────────────────────────────
        // POST api/payments/refund/{orderId}  [Admin]
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Admin")]
        [HttpPost("refund/{orderId}")]
        public async Task<IActionResult> Refund(int orderId, [FromBody] RefundDto dto)
        {
            var payment = await _context.Payments
                .Include(p => p.Order)
                .FirstOrDefaultAsync(p => p.OrderId == orderId);

            if (payment == null) return NotFound();

            if (payment.Status != "Completed")
                return BadRequest(new { message = "Only completed payments can be refunded" });

            payment.Status = "Refunded";
            payment.RefundReason = dto.Reason;
            payment.Order.PaymentStatus = "Refunded";

            _context.Notifications.Add(new Notification
            {
                UserId = payment.Order.CustomerId,
                Title = "Refund Processed",
                Body = $"Your refund of {payment.Amount} EGP has been processed.",
                Type = "General",
                OrderId = orderId,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
            return Ok(new { message = "Refund processed successfully" });
        }

        // ─────────────────────────────────────────────
        // GET api/payments/admin  — كل المدفوعات (لوحة صاحب المنصة)
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Admin")]
        [HttpGet("admin")]
        public async Task<IActionResult> GetAllAdmin(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _context.Payments.AsQueryable();

            if (from.HasValue)
                query = query.Where(p => (p.PaidAt ?? p.CreatedAt) >= from.Value.Date);

            if (to.HasValue)
            {
                var toEnd = to.Value.Date.AddDays(1).AddTicks(-1);
                query = query.Where(p => (p.PaidAt ?? p.CreatedAt) <= toEnd);
            }

            var total = await query.CountAsync();
            var payments = await query
                .OrderByDescending(p => p.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new
                {
                    p.Id,
                    p.OrderId,
                    p.Provider,
                    p.Amount,
                    p.Currency,
                    p.Status,
                    p.TransactionId,
                    p.PaidAt,
                    p.CreatedAt,
                    RestaurantName = p.Order.Restaurant.Name
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = payments });
        }

        // ─────────────────────────────────────────────
        // GET api/payments/history  — سجل مدفوعات العميل
        // ─────────────────────────────────────────────
        [HttpGet("history")]
        public async Task<IActionResult> GetHistory(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            var userId = GetUserId();

            var total = await _context.Payments
                .CountAsync(p => p.Order.CustomerId == userId);

            var payments = await _context.Payments
                .Where(p => p.Order.CustomerId == userId)
                .OrderByDescending(p => p.CreatedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new
                {
                    p.Id,
                    p.Amount,
                    p.Currency,
                    p.Provider,
                    p.Status,
                    p.PaidAt,
                    p.CreatedAt,
                    OrderId = p.OrderId,
                    RestaurantName = p.Order.Restaurant.Name
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = payments });
        }
    }

    public class ConfirmPaymentDto
    {
        public int OrderId { get; set; }
        public string TransactionId { get; set; } = string.Empty;
    }

    public class RefundDto { public string? Reason { get; set; } }
}