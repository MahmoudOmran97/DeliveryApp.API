using DeliveryApp.API.Models;

namespace DeliveryApp.API.DTOs.Revenue
{
    public class SubscriptionPlanDto
    {
        public int Id { get; set; }
        public RevenueEntityType EntityType { get; set; }
        public int? RestaurantId { get; set; }
        public string? RestaurantName { get; set; }
        public int? DriverId { get; set; }
        public string? DriverName { get; set; }
        public SubscriptionType Type { get; set; }
        public decimal Value { get; set; }
        public bool IsActive { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class UpsertSubscriptionPlanDto
    {
        public RevenueEntityType EntityType { get; set; }
        public int? RestaurantId { get; set; }
        public int? DriverId { get; set; }
        public SubscriptionType Type { get; set; }
        public decimal Value { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class RevenueSettlementDto
    {
        public int Id { get; set; }
        public RevenueEntityType EntityType { get; set; }
        public int? RestaurantId { get; set; }
        public string? RestaurantName { get; set; }
        public int? DriverId { get; set; }
        public string? DriverName { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public decimal OrdersTotal { get; set; }
        public int OrdersCount { get; set; }
        public SubscriptionType PlanType { get; set; }
        public decimal PlanValue { get; set; }
        public decimal AmountDue { get; set; }
        public decimal AmountPaid { get; set; }
        public SettlementStatus Status { get; set; }
        public DateTime? PaidAt { get; set; }
        public string? Notes { get; set; }
    }

    public class MarkSettlementPaidDto
    {
        /// <summary>لو null، بيتحسب المبلغ المتبقي كامل (AmountDue) كـ Paid</summary>
        public decimal? AmountPaid { get; set; }
        public string? Notes { get; set; }
    }

    public class GenerateSettlementsDto
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
    }

    public class RevenueSummaryDto
    {
        public decimal StoresCollected { get; set; }
        public decimal StoresPending { get; set; }
        public decimal DriversCollected { get; set; }
        public decimal DriversPending { get; set; }
        public int StoresOverdueCount { get; set; }
        public int DriversOverdueCount { get; set; }
    }
}
