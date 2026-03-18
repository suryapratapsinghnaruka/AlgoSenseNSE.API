using Microsoft.AspNetCore.SignalR;

namespace AlgoSenseNSE.API.Hubs
{
    public class MarketHub : Hub
    {
        public async Task JoinStock(string symbol)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId, $"stock-{symbol}");
        }

        public async Task LeaveStock(string symbol)
        {
            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId, $"stock-{symbol}");
        }

        public override async Task OnConnectedAsync()
        {
            await Clients.Caller.SendAsync(
                "Connected", "Connected to AlgoSense NSE live feed");
            await base.OnConnectedAsync();
        }
    }
}