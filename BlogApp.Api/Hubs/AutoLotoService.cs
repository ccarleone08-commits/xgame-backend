//using Microsoft.AspNetCore.SignalR;

//namespace BlogApp.Api.Hubs
//{
//    public class AutoLotoService
//    {
//        private readonly IHubContext<LotoHub> _hubContext;
//        private CancellationTokenSource? _cts;
//        private Task? _runningTask;
//        private readonly object _lock = new();

//        // ✅ ƏSAS FİX: Static state - bütün instance-lar üçün ortaq
//        private static Queue<int>? _numbersQueue;
//        private static readonly List<int> _drawnNumbers = new();
//        private static bool _isRunning = false;

//        public AutoLotoService(IHubContext<LotoHub> hubContext)
//        {
//            _hubContext = hubContext;
//        }

//        public void Start(int intervalMs = 1000)
//        {
//            lock (_lock)
//            {
//                if (_isRunning)
//                {
//                    Console.WriteLine("⚠️ AutoLotoService artıq işləyir");
//                    return;
//                }

//                // ✅ İlk dəfə başladıqda nömrələri qarışdır
//                if (_numbersQueue == null || _numbersQueue.Count == 0)
//                {
//                    _numbersQueue = new Queue<int>(
//                        Enumerable.Range(1, 90).OrderBy(x => Guid.NewGuid())
//                    );
//                    _drawnNumbers.Clear();
//                    Console.WriteLine($"🎲 Yeni oyun: {_numbersQueue.Count} nömrə hazırlandı");
//                }

//                _cts = new CancellationTokenSource();
//                _isRunning = true;
//                _runningTask = Task.Run(() => AutoDrawLoop(intervalMs, _cts.Token));

//                Console.WriteLine("✅ AutoLotoService başladı");
//            }
//        }

//        public void Stop()
//        {
//            lock (_lock)
//            {
//                if (!_isRunning) return;

//                _cts?.Cancel();
//                _isRunning = false;
//                Console.WriteLine("🛑 AutoLotoService dayandırıldı");
//            }
//        }

//        public void Reset()
//        {
//            lock (_lock)
//            {
//                Stop();
//                _numbersQueue = null;
//                _drawnNumbers.Clear();
//                Console.WriteLine("🔄 AutoLotoService reset edildi");
//            }
//        }

//        private async Task AutoDrawLoop(int intervalMs, CancellationToken token)
//        {
//            try
//            {
//                while (!token.IsCancellationRequested)
//                {
//                    int? number = null;

//                    lock (_lock)
//                    {
//                        if (_numbersQueue == null || _numbersQueue.Count == 0)
//                        {
//                            Console.WriteLine("⚠️ Nömrələr qurtardı - oyun bitir");
//                            _isRunning = false;
//                            break;
//                        }

//                        number = _numbersQueue.Dequeue();
//                        _drawnNumbers.Add(number.Value);
//                    }

//                    if (number.HasValue)
//                    {
//                        Console.WriteLine($"🎱 Auto-drawn: {number.Value} (qalan: {_numbersQueue?.Count ?? 0})");
//                        await _hubContext.Clients.All.SendAsync("NumberDrawn", number.Value);
//                    }

//                    await Task.Delay(intervalMs, token);
//                }

//                // ✅ Oyun bitdikdə bildiriş göndər
//                if (_numbersQueue?.Count == 0)
//                {
//                    await _hubContext.Clients.All.SendAsync("GameOver", "Bütün nömrələr çəkildi - oyun bitdi");
//                }
//            }
//            catch (TaskCanceledException)
//            {
//                Console.WriteLine("🛑 AutoDrawLoop ləğv edildi");
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"❌ AutoDrawLoop ERROR: {ex}");
//            }
//            finally
//            {
//                lock (_lock)
//                {
//                    _isRunning = false;
//                }
//            }
//        }

//        // ✅ Helper metodlar
//        public static int GetDrawnCount() => _drawnNumbers.Count;
//        public static int GetRemainingCount() => _numbersQueue?.Count ?? 0;
//        public static List<int> GetDrawnNumbers() => new List<int>(_drawnNumbers);
//    }
//}