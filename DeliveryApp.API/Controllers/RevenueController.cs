using DeliveryApp.API.DTOs.Revenue;
using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DeliveryApp.API.Controllers
{
    // ─────────────────────────────────────────────────────────────
    // أرباحنا (Revenue): اشتراكات المحلات وعمولة السواقين اللي بتيجي
    // للمنصة نفسها - عكس /api/orders/admin/settlements اللي بيحسب
    // المستحق للمحل/السواق منّنا.
    // ─────────────────────────────────────────────────────────────
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    public class RevenueController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        public RevenueController(ApplicationDbContext context) => _context = context;

        // ══════════════════ Subscription Plans ══════════════════

        // GET /api/revenue/plans?entityType=Store
        [HttpGet("plans")]
        public async Task<IActionResult> GetPlans([FromQuery] RevenueEntityType? entityType)
        {
            var query = _context.SubscriptionPlans
                .Include(p => p.Restaurant)
                .Include(p => p.Driver).ThenInclude(d => d!.User)
                .Where(p => p.IsActive)
                .AsQueryable();

            if (entityType.HasValue)
                query = query.Where(p => p.EntityType == entityType);

            var plans = await query
                .OrderBy(p => p.EntityType)
                .Select(p => new SubscriptionPlanDto
                {
                    Id = p.Id,
                    EntityType = p.EntityType,
                    RestaurantId = p.RestaurantId,
                    RestaurantName = p.Restaurant != null ? p.Restaurant.Name : null,
                    DriverId = p.DriverId,
                    DriverName = p.Driver != null ? p.Driver.User.FullName : null,
                    Type = p.Type,
                    Value = p.Value,
                    IsActive = p.IsActive,
                    UpdatedAt = p.UpdatedAt
                })
                .ToListAsync();

            return Ok(plans);
        }

        // POST /api/revenue/plans — إنشاء أو تعديل خطة اشتراك محل/سواق (upsert)
        [HttpPost("plans")]
        public async Task<IActionResult> UpsertPlan([FromBody] UpsertSubscriptionPlanDto dto)
        {
            if (dto.EntityType == RevenueEntityType.Store && dto.RestaurantId == null)
                return BadRequest(new { message = "RestaurantId is required for Store plans" });
            if (dto.EntityType == RevenueEntityType.Driver && dto.DriverId == null)
                return BadRequest(new { message = "DriverId is required for Driver plans" });
            if (dto.Type == SubscriptionType.Percentage && (dto.Value < 0 || dto.Value > 100))
                return BadRequest(new { message = "Percentage value must be between 0 and 100" });
            if (dto.Type == SubscriptionType.Fixed && dto.Value < 0)
                return BadRequest(new { message = "Fixed value must be >= 0" });

            var plan = await _context.SubscriptionPlans.FirstOrDefaultAsync(p =>
                p.EntityType == dto.EntityType &&
                p.RestaurantId == dto.RestaurantId &&
                p.DriverId == dto.DriverId);

            if (plan == null)
            {
                plan = new SubscriptionPlan
                {
                    EntityType = dto.EntityType,
                    RestaurantId = dto.RestaurantId,
                    DriverId = dto.DriverId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.SubscriptionPlans.Add(plan);
            }

            plan.Type = dto.Type;
            plan.Value = dto.Value;
            plan.IsActive = dto.IsActive;
            plan.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new { message = "Subscription plan saved", plan.Id });
        }

        // ══════════════════ Settlements ══════════════════

        // POST /api/revenue/settlements/generate
        // بيولد سجلات استحقاق شهرية لكل المحلات والسواقين اللي عندهم خطة نشطة،
        // بناءً على الطلبات Delivered في الفترة المحددة. لو سجل الفترة دي موجود
        // بالفعل لمحل/سواق معين، بيتخطاه (منعًا للتكرار).
        [HttpPost("settlements/generate")]
        public async Task<IActionResult> GenerateSettlements([FromBody] GenerateSettlementsDto dto)
        {
            var periodStart = dto.PeriodStart.Date;
            var periodEnd = dto.PeriodEnd.Date.AddDays(1).AddTicks(-1);

            var plans = await _context.SubscriptionPlans.Where(p => p.IsActive).ToListAsync();
            if (!plans.Any())
                return Ok(new { message = "No active subscription plans", generated = 0 });

            var existing = await _context.RevenueSettlements
                .Where(s => s.PeriodStart == periodStart && s.PeriodEnd == periodEnd)
                .Select(s => new { s.EntityType, s.RestaurantId, s.DriverId })
                .ToListAsync();

            var ordersInPeriod = await _context.Orders
                .Where(o => o.Status == "Delivered")
                .Where(o => (o.DeliveredAt ?? o.CreatedAt) >= periodStart && (o.DeliveredAt ?? o.CreatedAt) <= periodEnd)
                .Select(o => new { o.RestaurantId, o.DriverId, o.SubTotal, o.DeliveryFee })
                .ToListAsync();

            var generated = new List<RevenueSettlement>();

            foreach (var plan in plans)
            {
                bool alreadyExists = existing.Any(e =>
                    e.EntityType == plan.EntityType &&
                    e.RestaurantId == plan.RestaurantId &&
                    e.DriverId == plan.DriverId);
                if (alreadyExists) continue;

                decimal ordersTotal;
                int ordersCount;

                if (plan.EntityType == RevenueEntityType.Store)
                {
                    // المحل: النسبة بتتحسب على قيمة الأوردر بس (SubTotal)، من غير تمن التوصيل
                    var storeOrders = ordersInPeriod.Where(o => o.RestaurantId == plan.RestaurantId).ToList();
                    ordersTotal = storeOrders.Sum(o => o.SubTotal);
                    ordersCount = storeOrders.Count;
                }
                else
                {
                    // السواق: النسبة بتتحسب على تمن التوصيل بس (DeliveryFee)، مش إجمالي الأوردر
                    var driverOrders = ordersInPeriod.Where(o => o.DriverId == plan.DriverId).ToList();
                    ordersTotal = driverOrders.Sum(o => o.DeliveryFee);
                    ordersCount = driverOrders.Count;
                }

                // اشتراك ثابت بيستحق كل شهر بغض النظر عن عدد الطلبات؛ نسبة بتتحسب على أساس الفترة (SubTotal للمحل / DeliveryFee للسواق)
                decimal amountDue = plan.Type == SubscriptionType.Fixed
                    ? plan.Value
                    : Math.Round(ordersTotal * plan.Value / 100m, 2);

                if (plan.Type == SubscriptionType.Percentage && ordersCount == 0)
                    continue; // مفيش مبيعات = مفيش نسبة تتحصل، مش محتاجين نولد سجل صفر

                generated.Add(new RevenueSettlement
                {
                    EntityType = plan.EntityType,
                    RestaurantId = plan.RestaurantId,
                    DriverId = plan.DriverId,
                    PeriodStart = periodStart,
                    PeriodEnd = periodEnd,
                    OrdersTotal = ordersTotal,
                    OrdersCount = ordersCount,
                    PlanType = plan.Type,
                    PlanValue = plan.Value,
                    AmountDue = amountDue,
                    AmountPaid = 0,
                    Status = SettlementStatus.Unpaid,
                    CreatedAt = DateTime.UtcNow
                });
            }

            if (generated.Any())
            {
                _context.RevenueSettlements.AddRange(generated);
                await _context.SaveChangesAsync();
            }

            return Ok(new { message = "Settlements generated", generated = generated.Count });
        }

        // GET /api/revenue/settlements?entityType=&status=&from=&to=
        [HttpGet("settlements")]
        public async Task<IActionResult> GetSettlements(
            [FromQuery] RevenueEntityType? entityType,
            [FromQuery] SettlementStatus? status,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            var query = _context.RevenueSettlements
                .Include(s => s.Restaurant)
                .Include(s => s.Driver).ThenInclude(d => d!.User)
                .AsQueryable();

            if (entityType.HasValue) query = query.Where(s => s.EntityType == entityType);
            if (status.HasValue) query = query.Where(s => s.Status == status);
            if (from.HasValue) query = query.Where(s => s.PeriodEnd >= from.Value.Date);
            if (to.HasValue) query = query.Where(s => s.PeriodStart <= to.Value.Date);

            var settlements = await query
                .OrderByDescending(s => s.PeriodStart)
                .Select(s => new RevenueSettlementDto
                {
                    Id = s.Id,
                    EntityType = s.EntityType,
                    RestaurantId = s.RestaurantId,
                    RestaurantName = s.Restaurant != null ? s.Restaurant.Name : null,
                    DriverId = s.DriverId,
                    DriverName = s.Driver != null ? s.Driver.User.FullName : null,
                    PeriodStart = s.PeriodStart,
                    PeriodEnd = s.PeriodEnd,
                    OrdersTotal = s.OrdersTotal,
                    OrdersCount = s.OrdersCount,
                    PlanType = s.PlanType,
                    PlanValue = s.PlanValue,
                    AmountDue = s.AmountDue,
                    AmountPaid = s.AmountPaid,
                    Status = s.Status,
                    PaidAt = s.PaidAt,
                    Notes = s.Notes
                })
                .ToListAsync();

            return Ok(settlements);
        }

        // GET /api/revenue/summary — أرقام كروت الداشبورد
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary()
        {
            var settlements = await _context.RevenueSettlements.ToListAsync();

            var summary = new RevenueSummaryDto
            {
                StoresCollected = settlements.Where(s => s.EntityType == RevenueEntityType.Store).Sum(s => s.AmountPaid),
                StoresPending = settlements.Where(s => s.EntityType == RevenueEntityType.Store).Sum(s => s.AmountDue - s.AmountPaid),
                DriversCollected = settlements.Where(s => s.EntityType == RevenueEntityType.Driver).Sum(s => s.AmountPaid),
                DriversPending = settlements.Where(s => s.EntityType == RevenueEntityType.Driver).Sum(s => s.AmountDue - s.AmountPaid),
                StoresOverdueCount = settlements.Count(s => s.EntityType == RevenueEntityType.Store && s.Status != SettlementStatus.Paid),
                DriversOverdueCount = settlements.Count(s => s.EntityType == RevenueEntityType.Driver && s.Status != SettlementStatus.Paid)
            };

            return Ok(summary);
        }

        // POST /api/revenue/settlements/{id}/mark-paid — تحصيل يدوي (كاش)
        [HttpPost("settlements/{id}/mark-paid")]
        public async Task<IActionResult> MarkPaid(int id, [FromBody] MarkSettlementPaidDto dto)
        {
            var settlement = await _context.RevenueSettlements.FindAsync(id);
            if (settlement == null) return NotFound(new { message = "Settlement not found" });

            var amount = dto.AmountPaid ?? (settlement.AmountDue - settlement.AmountPaid);
            if (amount < 0) return BadRequest(new { message = "Invalid amount" });

            settlement.AmountPaid += amount;
            settlement.Status = settlement.AmountPaid >= settlement.AmountDue
                ? SettlementStatus.Paid
                : SettlementStatus.PartiallyPaid;
            settlement.PaidAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(dto.Notes)) settlement.Notes = dto.Notes;

            var adminIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (int.TryParse(adminIdClaim, out var adminId)) settlement.CollectedByAdminId = adminId;

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Marked as collected",
                settlement.Id,
                settlement.AmountPaid,
                settlement.Status
            });
        }
    }
}
