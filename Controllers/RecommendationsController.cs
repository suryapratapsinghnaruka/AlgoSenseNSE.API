using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoSenseNSE.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RecommendationsController : ControllerBase
    {
        private readonly MarketScanService _scanner;

        public RecommendationsController(
            MarketScanService scanner)
        {
            _scanner = scanner;
        }

        // GET api/recommendations
        [HttpGet]
        public IActionResult Get()
        {
            var recs = _scanner.GetRecommendations();
            return Ok(new
            {
                generatedAt = DateTime.Now,
                count = recs.Count,
                recommendations = recs
            });
        }

        // POST api/recommendations/refresh
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            await _scanner.RefreshRecommendationsAsync();
            return Ok(_scanner.GetRecommendations());
        }
    }
}