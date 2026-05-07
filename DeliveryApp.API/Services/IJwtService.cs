using DeliveryApp.API.Models;

namespace DeliveryApp.API.Services
{
    public interface IJwtService
    {
        string GenerateToken(User user);
    }
}