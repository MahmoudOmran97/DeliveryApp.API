using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ChatMessagesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public ChatMessagesController(ApplicationDbContext context)
        {
            _context = context;
        }

        private int GetUserId() =>
            int.Parse(User.Claims.First(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier
                                          || c.Type == "sub").Value);

        [HttpGet("{orderId}")]
        public async Task<IActionResult> GetMessages(int orderId)
        {
            var userId = GetUserId();

            // التحقق من أن المستخدم هو العميل أو المندوب الخاص بالطلب
            var order = await _context.Orders
                .Include(o => o.Driver)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null) return NotFound();

            bool isCustomer = order.CustomerId == userId;
            bool isDriver = order.Driver?.UserId == userId;

            if (!isCustomer && !isDriver) return Forbid();

            var messages = await _context.ChatMessages
                .Where(m => m.OrderId == orderId)
                .OrderBy(m => m.Timestamp)
                .Select(m => new
                {
                    m.Id,
                    m.OrderId,
                    m.SenderId,
                    m.Message,
                    m.Timestamp,
                    IsFromMe = m.SenderId == userId
                })
                .ToListAsync();

            return Ok(messages);
        }
    }
}
