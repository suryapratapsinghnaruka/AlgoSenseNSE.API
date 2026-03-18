using AlgoSenseNSE.API.Models;
using AlgoSenseNSE.API.Services;

namespace AlgoSenseNSE.API.Services
{
    public class ScoringEngine
    {
        // Weights — must add up to 1.0
        private const double TechWeight = 0.40;
        private const double FundWeight = 0.30;
        private const double NewsWeight = 0.30;

        public CompositeScore Compute(
            string symbol,
            double techScore,
            double fundScore,
            double newsSentiment)
        {
            // Convert news sentiment (-1 to +1) to 0-100 score
            double newsScore = 50 + (newsSentiment * 40);
            newsScore = Math.Max(0, Math.Min(100, newsScore));

            double finalScore =
                (techScore * TechWeight) +
                (fundScore * FundWeight) +
                (newsScore * NewsWeight);

            return new CompositeScore
            {
                Symbol = symbol,
                TechnicalScore = Math.Round(techScore, 1),
                FundamentalScore = Math.Round(fundScore, 1),
                NewsScore = Math.Round(newsScore, 1),
                FinalScore = Math.Round(finalScore, 1),
                CalculatedAt = DateTime.Now
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
 