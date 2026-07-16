namespace DeliveryApp.API.DTOs.Auth
{
    // بيتبعت لطلب إرسال كود OTP على الإيميل
    // Purpose: "Register" أو "ResetPassword"
    public class SendOtpDto
    {
        public string Email { get; set; } = null!;
        public string Purpose { get; set; } = null!;
    }

    // بيتبعت للتحقق من الكود قبل ما اليوزر يكمل (بس تحقق، من غير ما يستهلك الكود)
    public class VerifyOtpDto
    {
        public string Email { get; set; } = null!;
        public string Code { get; set; } = null!;
        public string Purpose { get; set; } = null!;
    }

    // بيتبعت لتغيير كلمة المرور بعد التحقق من الكود
    public class ResetPasswordDto
    {
        public string Email { get; set; } = null!;
        public string Code { get; set; } = null!;
        public string NewPassword { get; set; } = null!;
    }
}