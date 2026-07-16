namespace DeliveryApp.API.DTOs.Auth
{
    public class RegisterDto
    {
        public string FullName { get; set; }

        public string Email { get; set; }

        public string Password { get; set; }

        public string Phone { get; set; }

        public string Role { get; set; }

        // ✅ الجديد: كود الـ OTP اللي وصل على الإيميل، لازم يتبعت مع بيانات التسجيل
        public string Otp { get; set; }
    }
}