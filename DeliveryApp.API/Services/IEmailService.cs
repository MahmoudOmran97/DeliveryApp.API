using System.Threading.Tasks;

namespace DeliveryApp.API.Services
{
    public interface IEmailService
    {
        Task SendOtpEmailAsync(string toEmail, string code, string purpose);
    }
}