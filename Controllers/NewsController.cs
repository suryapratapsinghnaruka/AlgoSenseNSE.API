using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoSenseNSE.API.Controllers
{
    [ApiController]
    [Route("api/news")]
    public class NewsController : ControllerBase
    {
        private readonly NewsService _news;

        public NewsController(NewsService news)
            => _news = news;

        // ── GET /api/news ────────────────────────────
        [HttpGet]
        public IActionResult GetNews(
            [FromQuery] int limit = 50)
        {
            var items = _news.GetCachedNews()
                .Take(limit)
                .ToList();
            return Ok(items);
        }

        // ── GET /api/news/{symbol} ───────────────────
        [HttpGet("{symbol}")]
        public IActionResult GetNewsForSymbol(string symbol)
        {
            var items = _news.GetNewsForSymbol(
                symbol.ToUpper());
            return Ok(items);
        }
    }
}