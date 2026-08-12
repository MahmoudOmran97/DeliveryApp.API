using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/site-links")]
public class SiteLinksController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public SiteLinksController(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Public endpoint used by the customer app. Only active links are returned.
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<SiteLinkDto>>> GetActiveLinks(CancellationToken cancellationToken)
    {
        var links = await _db.SiteLinks
            .AsNoTracking()
            .Where(x => x.IsActive && x.Url != "")
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Key)
            .Select(x => new SiteLinkDto
            {
                Key = x.Key,
                Title = x.Title,
                Url = x.Url,
                Icon = x.Icon,
                SortOrder = x.SortOrder
            })
            .ToListAsync(cancellationToken);

        return Ok(links);
    }

    /// <summary>
    /// Admin upsert endpoint. Example key values: website, facebook, instagram, tiktok, x.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPut("{key}")]
    public async Task<ActionResult<SiteLinkDto>> Upsert(
        string key,
        [FromBody] SiteLinkUpdateDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { message = "Link key is required." });

        if (string.IsNullOrWhiteSpace(request.Url) ||
            !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new { message = "Url must be a valid http or https address." });
        }

        var normalizedKey = key.Trim().ToLowerInvariant();
        var link = await _db.SiteLinks
            .FirstOrDefaultAsync(x => x.Key == normalizedKey, cancellationToken);

        if (link == null)
        {
            link = new SiteLink { Key = normalizedKey };
            _db.SiteLinks.Add(link);
        }

        link.Title = string.IsNullOrWhiteSpace(request.Title) ? normalizedKey : request.Title.Trim();
        link.Url = request.Url.Trim();
        link.Icon = string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim();
        link.IsActive = request.IsActive;
        link.SortOrder = request.SortOrder;
        link.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new SiteLinkDto
        {
            Key = link.Key,
            Title = link.Title,
            Url = link.Url,
            Icon = link.Icon,
            SortOrder = link.SortOrder
        });
    }
}
