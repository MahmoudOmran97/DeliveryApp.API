using System;
using System.Linq;
using System.Threading.Tasks;
using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Services
{
    public class OtpService : IOtpService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private static readonly Random _rng = new();

        public OtpService(ApplicationDbContext context, IEmailService emailService)
        {
            _context = context;
            _emailService = emailService;
        }

        public async Task GenerateAndSendAsync(string email, string purpose)
        {
            // ألغي أي أكواد سابقة لسه صالحة لنفس الإيميل والغرض
            var oldCodes = await _context.OtpCodes
                .Where(o => o.Email == email && o.Purpose == purpose && !o.IsUsed)
                .ToListAsync();

            foreach (var old in oldCodes)
                old.IsUsed = true;

            var code = _rng.Next(100000, 999999).ToString();

            var otp = new OtpCode
            {
                Email = email,
                Code = code,
                Purpose = purpose,
                IsUsed = false,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                CreatedAt = DateTime.UtcNow
            };

            _context.OtpCodes.Add(otp);
            await _context.SaveChangesAsync();

            await _emailService.SendOtpEmailAsync(email, code, purpose);
        }

        public async Task<bool> ValidateAsync(string email, string code, string purpose)
        {
            var otp = await _context.OtpCodes
                .Where(o => o.Email == email && o.Purpose == purpose && !o.IsUsed)
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync();

            if (otp == null) return false;
            if (otp.ExpiresAt < DateTime.UtcNow) return false;
            if (otp.Code != code) return false;

            return true;
        }

        public async Task<bool> ValidateAndConsumeAsync(string email, string code, string purpose)
        {
            var otp = await _context.OtpCodes
                .Where(o => o.Email == email && o.Purpose == purpose && !o.IsUsed)
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync();

            if (otp == null) return false;
            if (otp.ExpiresAt < DateTime.UtcNow) return false;
            if (otp.Code != code) return false;

            otp.IsUsed = true;
            await _context.SaveChangesAsync();
            return true;
        }
    }
}