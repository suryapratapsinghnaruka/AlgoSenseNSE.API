using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoSenseNSE.API.Controllers
{
    [ApiController]
    [Route("api/market")]
    public class MarketController : ControllerBase
    {
        private readonly NseIndiaService _nse;
        private readonly AngelOneWebSocketService _ws;
        private readonly StockScreenerService _screener;

        public MarketController(
            NseIndiaService nse,
            AngelOneWebSocketService ws,
            StockScreenerService screener)
        {
            _nse = nse;
            _ws = ws;
            _screener = screener;
        }

        // ── GET /api/market/context ──────────────────
        // Used by index.html to show VIX, FII, sector data
        [HttpGet("context")]
        public async Task<IActionResult> GetContext()
        {
            var ctx = await _nse.GetMarketContextAsync();
            return Ok(new
            {
                niftyLtp = ctx.NiftyLtp,
                niftyChange = ctx.NiftyChange,
                niftyTrend = ctx.NiftyTrend,
                niftyDayPosition = ctx.NiftyDayPosition,
                indiaVix = ctx.IndiaVix,
                vixChange = ctx.VixChange,
                vixInterpretation = ctx.VixInterpretation,
                fiiNetCrore = ctx.FiiNetCrore,
                diiNetCrore = ctx.DiiNetCrore,
                fiiSentiment = ctx.FiiSentiment,
                diiSentiment = ctx.DiiSentiment,
                marketQualityScore = ctx.MarketQualityScore,
                marketQualityLabel = ctx.MarketQualityLabel,
                topSectors = ctx.TopSectors,
                weakSectors = ctx.WeakSectors,
                sectors = ctx.Sectors,
                fetchedAt = ctx.FetchedAt
            });
        }

        // ── GET /api/market/websocket ────────────────
        // WebSocket connection status + screener stats
        [HttpGet("websocket")]
        public IActionResult GetWebSocketStatus()
        {
            return Ok(new
            {
                connected = _ws.IsConnected,
                tickCount = _ws.TickCount,
                candidates = _screener.CandidateCount,
                topGainers = _screener.GetTopGainers(10)
                    .Select(s => new
                    {
                        s.Symbol,
                        s.Price,
                        s.ChangePercent,
                        s.Volume,
                        s.MomentumScore
                    }),
                volumeSpikes = _screener.GetVolumeSpikes(5)
                    .Select(s => new
                    {
                        s.Symbol,
                        s.Price,
                        s.ChangePercent,
                        s.Volume
                    })
            });
        }
    }
}