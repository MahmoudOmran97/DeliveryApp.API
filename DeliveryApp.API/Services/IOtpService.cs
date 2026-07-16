using System.Threading.Tasks;

namespace DeliveryApp.API.Services
{
    public interface IOtpService
    {
        /// <summary>ولّد كود جديد وابعته على الإيميل. بيلغي أي أكواد قديمة لنفس الإيميل والغرض.</summary>
        Task GenerateAndSendAsync(string email, string purpose);

        /// <summary>تحقق فقط (من غير ما يستهلك الكود) — بيستخدم في خطوة "أدخل الكود" في الواجهة.</summary>
        Task<bool> ValidateAsync(string email, string code, string purpose);

        /// <summary>تحقق واستهلك الكود (IsUsed = true) — بيستخدم عند إتمام العملية فعليًا (تسجيل / تغيير باسورد).</summary>
        Task<bool> ValidateAndConsumeAsync(string email, string code, string purpose);
    }
}