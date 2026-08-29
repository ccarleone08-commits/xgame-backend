namespace BlogApp.Api.Hubs.Services
{
    namespace BlogApp.Api.Hubs.Services
    {
        /// <summary>
        /// Bot büdcə sistemi - In-memory, DB-dən asılı deyil.
        /// Büdcə limitsizdir, botlar heç vaxt balans səbəbilə dayanmır.
        /// </summary>
        public class BotBudgetService
        {
            private static decimal _botBudget = 10000m; // Statistika üçün görünən balans
            private static bool UnlimitedBudget => true;
            private static readonly object _lock = new object();

            public Task InitializeBotBudgetAccount()
            {
                Console.WriteLine($"💰 Bot budget initialized: {_botBudget}₼");
                return Task.CompletedTask;
            }

            public Task<bool> DeductBotExpense(decimal amount, string reason)
            {
                lock (_lock)
                {
                    if (!UnlimitedBudget && _botBudget < amount)
                    {
                        Console.WriteLine($"❌ Insufficient bot budget: {_botBudget}₼ < {amount}₼");
                        return Task.FromResult(false);
                    }

                    if (!UnlimitedBudget)
                    {
                        _botBudget -= amount;
                    }

                    Console.WriteLine($"💸 Bot expense: -{amount}₼ ({reason}) | Remaining: {(UnlimitedBudget ? "Unlimited" : $"{_botBudget}₼")}");
                    return Task.FromResult(true);
                }
            }

            public Task AddBotWinnings(decimal amount, string reason)
            {
                lock (_lock)
                {
                    _botBudget += amount;
                    Console.WriteLine($"💰 Bot winnings: +{amount}₼ ({reason}) | Balance: {_botBudget}₼");
                }
                return Task.CompletedTask;
            }

            public Task<decimal> GetCurrentBudget()
            {
                lock (_lock)
                {
                    return Task.FromResult(_botBudget);
                }
            }

            public Task<bool> CanAffordGame(decimal totalCost)
            {
                lock (_lock)
                {
                    if (UnlimitedBudget)
                    {
                        return Task.FromResult(true);
                    }

                    bool canAfford = _botBudget >= totalCost;
                    if (!canAfford)
                    {
                        Console.WriteLine($"⚠️ Bot budget too low: {_botBudget}₼ < {totalCost}₼ needed");
                    }
                    return Task.FromResult(canAfford);
                }
            }

            // Əlavə: Büdcəni yenilə (admin üçün)
            public Task SetBudget(decimal newBudget)
            {
                lock (_lock)
                {
                    decimal old = _botBudget;
                    _botBudget = newBudget;
                    Console.WriteLine($"⚙️ Bot budget updated: {old}₼ → {_botBudget}₼");
                }
                return Task.CompletedTask;
            }

            // Əlavə: Statistika
            public Task<BotBudgetStats> GetStats()
            {
                lock (_lock)
                {
                    return Task.FromResult(new BotBudgetStats
                    {
                        CurrentBudget = _botBudget,
                        IsHealthy = UnlimitedBudget || _botBudget > 1000m,
                        IsUnlimited = UnlimitedBudget,
                        LastChecked = DateTime.UtcNow
                    });
                }
            }
        }

        public class BotBudgetStats
        {
            public decimal CurrentBudget { get; set; }
            public bool IsHealthy { get; set; }
            public bool IsUnlimited { get; set; }
            public DateTime LastChecked { get; set; }
        }
    }
}
