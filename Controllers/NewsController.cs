using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoSenseNSE.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class NewsController : ControllerBase
    {
        private readonly NewsService _news;

        public NewsController(NewsService news)
        {
            _news = news;
        }

        // GET api/news
        [HttpGet]
        public IActionResult GetAll()
        {
            return Ok(_news.GetCachedNews());
        }

        // GET api/news/{symbol}
        [HttpGet("{symbol}")]
        public IActionResult GetForSymbol(string symbol)
        {
            return Ok(_news.GetNewsForSymbol(symbol.ToUpper()));
        }

        // POST api/news/refresh
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            var news = await _news.FetchAllNewsAsync();
            return Ok(new
            {
                count = news.Count,
                refreshedAt = DateTime.Now,
                news = news.Take(20)
            });
        }
    }
}