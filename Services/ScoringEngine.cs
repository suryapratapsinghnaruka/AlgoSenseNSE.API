using AlgoSenseNSE.API.Models;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// ScoringEngine v3
    ///
    /// Weight change (per ChatGPT recommendation):
    ///   v2: Technical 70% + Fundamental 15% + News 15%
    ///   v3: Technical 75% + Fundamental 10% + News 15%
    ///
    /// Rationale:
    ///   Fundamentals don't move price in 5–30 min intraday windows.
    ///   They prevent trading garbage stocks (stock selection), not timing.
    ///   Since we use a curated 53-stock universe already screened for quality,
    ///   fundamental weight can be reduced without losing that protection.
    ///   Technical indicators + time-of-day now carry more weight.
    /// </summary>
    public class ScoringEngine
    {
        // v3 weights — must add to 1.0
        private const double TechWeight = 0.75; // was 0.70
        private const double FundWeight = 0.10; // was 0.15
        private const double NewsWeight = 0.15; // unchanged

        public CompositeScore Compute(
            string symbol,
            double techScore,
            double fundScore,
            double newsSentiment)
        {
            // Convert news sentiment (-1 to +1) → 0-100
            double newsScore = 50 + (newsSentiment * 40);
            newsScore = Math.Max(0, Math.Min(100, newsScore));

            double finalScore =
                (techScore  * TechWeight) +
                (fundScore  * FundWeight) +
                (newsScore  * NewsWeight);

            return new CompositeScore
            {
                Symbol           = symbol,
                TechnicalScore   = Math.Round(techScore,  1),
                FundamentalScore = Math.Round(fundScore,  1),
                NewsScore        = Math.Round(newsScore,  1),
                FinalScore       = Math.Round(finalScore, 1),
                CalculatedAt     = DateTime.Now
            };
        }

        public string GetRecommendation(double score)
        {
            if (score >= 65) return "BUY";
            if (score >= 45) return "HOLD";
            return "SELL";
        }

        public string GetConfidenceLabel(double score)
        {
            if (score >= 80) return "Very High";
            if (score >= 65) return "High";
            if (score >= 50) return "Medium";
            return "Low";
        }
    }
}
