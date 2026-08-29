using BlogApp.Core.Entities;

namespace BlogApp.Api.Hubs
{
    public static class BackgammonBoardSetup
    {
        public static void InitializeBoard(BackgammonRoom room)
        {
            // Türk Tavlası başlanğıc pozisiyası
            room.Board = new Dictionary<int, List<string>>();

            // Bütün nöqtələri boş siyahı ilə başlat
            for (int i = 1; i <= 24; i++)
            {
                room.Board[i] = new List<string>();
            }

            // AĞ daşlar (white) - 24-dən 1-ə hərəkət edir
            // Point 24: 2 ağ daş
            room.Board[24].Add("white");
            room.Board[24].Add("white");

            // Point 13: 5 ağ daş
            for (int i = 0; i < 5; i++)
                room.Board[13].Add("white");

            // Point 8: 3 ağ daş
            for (int i = 0; i < 3; i++)
                room.Board[8].Add("white");

            // Point 6: 5 ağ daş
            for (int i = 0; i < 5; i++)
                room.Board[6].Add("white");

            // QARA daşlar (black) - 1-dən 24-ə hərəkət edir
            // Point 1: 2 qara daş
            room.Board[1].Add("black");
            room.Board[1].Add("black");

            // Point 12: 5 qara daş
            for (int i = 0; i < 5; i++)
                room.Board[12].Add("black");

            // Point 17: 3 qara daş
            for (int i = 0; i < 3; i++)
                room.Board[17].Add("black");

            // Point 19: 5 qara daş
            for (int i = 0; i < 5; i++)
                room.Board[19].Add("black");

            System.Console.WriteLine("🎲 Board initialized:");
            foreach (var kvp in room.Board)
            {
                if (kvp.Value.Count > 0)
                {
                    System.Console.WriteLine($"   Point {kvp.Key}: {kvp.Value.Count} x {kvp.Value[0]}");
                }
            }
        }
    }

    public static class BackgammonRules
    {
        public static bool IsValidMove(BackgammonRoom room, int from, int to, string color)
        {
            // PRIORITY: BAR-da daş varsa, önce onu oyuna salmalısan
            if (room.Bar[color] > 0 && from != 0)
            {
                return false; // BAR-dan başqa hərəkət edə bilməz
            }

            // BAR-dan hərəkət
            if (from == 0)
            {
                if (room.Bar[color] == 0)
                    return false; // BAR-da daş yoxdur

                // ✅ White: 19-24 arası (home quadrant)
                // ✅ Black: 1-6 arası (home quadrant)
                int expectedPoint = to;
                int distance = to;

                if (color == "white")
                {
                    // White BAR-dan 19-24 arasına daxil olmalıdır
                    if (to < 19 || to > 24)
                        return false;

                    distance = 25 - to; // 19→6, 20→5, ..., 24→1
                }
                else
                {
                    // Black BAR-dan 1-6 arasına daxil olmalıdır
                    if (to < 1 || to > 6)
                        return false;

                    distance = to; // 1→1, 2→2, ..., 6→6
                }

                // Zərdə bu rəqəm olmalıdır
                if (!room.RemainingMoves.Contains(distance))
                    return false;

                // Hədəf nöqtə açıq olmalıdır
                return IsPointOpen(room, to, color);
            }

            // Normal hərəkət
            if (!room.Board.ContainsKey(from) || room.Board[from].Count == 0)
                return false;

            if (!room.Board[from].Contains(color))
                return false;

            // BEARING OFF yoxlaması
            if ((color == "white" && to < 1) || (color == "black" && to > 24))
            {
                if (!CanBearOff(room, color))
                    return false;

                int distance = color == "white" ? from : 25 - from;
                return IsBearOffValid(room, from, distance, color);
            }

            // Normal hərəkət üçün distance
            int moveDistance = color == "white" ? from - to : to - from;

            if (moveDistance <= 0)
                return false;

            if (room.RemainingMoves.Contains(moveDistance))
                return IsPointOpen(room, to, color);

            if (IsValidDoubleStepMove(room, from, to, moveDistance, color))
                return IsPointOpen(room, to, color);

            if (!TryGetCompositeMoveDice(room, from, to, moveDistance, color, out _))
                return false;

            return IsPointOpen(room, to, color);
        }

        public static bool HasLegalMove(BackgammonRoom room, string color)
        {
            if (room.RemainingMoves.Count == 0)
                return false;

            if (room.Bar[color] > 0)
            {
                foreach (var dice in room.RemainingMoves.Distinct())
                {
                    var entryPoint = color == "white" ? 25 - dice : dice;
                    if (IsValidMove(room, 0, entryPoint, color))
                        return true;
                }

                return false;
            }

            foreach (var point in room.Board.Keys.OrderBy(p => p))
            {
                if (!room.Board[point].Any(p => p == color))
                    continue;

                foreach (var dice in room.RemainingMoves.Distinct())
                {
                    var to = color == "white" ? point - dice : point + dice;
                    if (IsValidMove(room, point, to, color))
                        return true;
                }

                foreach (var moveDistance in GetDoubleStepDistances(room))
                {
                    var to = color == "white" ? point - moveDistance : point + moveDistance;
                    if (to >= 1 && to <= 24 && IsValidMove(room, point, to, color))
                        return true;
                }

                foreach (var moveDistance in GetCompositeMoveDistances(room))
                {
                    var to = color == "white" ? point - moveDistance : point + moveDistance;
                    if (to >= 1 && to <= 24 && IsValidMove(room, point, to, color))
                        return true;
                }

                var homePoint = color == "white" ? 0 : 25;
                if (IsValidMove(room, point, homePoint, color))
                    return true;
            }

            return false;
        }

        public static void ExecuteMove(BackgammonRoom room, int from, int to, string color)
        {
            int distance = 0;
            string opponent = color == "white" ? "black" : "white";

            // BAR-dan hərəkət
            if (from == 0)
            {
                room.Bar[color]--;

                if (color == "white")
                {
                    distance = 25 - to; // 19→6, 20→5, ..., 24→1
                }
                else
                {
                    distance = to; // 1→1, 2→2, ..., 6→6
                }

                // Hədəfdə rəqib tək daşı varsa, vur
                if (room.Board.ContainsKey(to) && room.Board[to].Count == 1 && room.Board[to][0] == opponent)
                {
                    room.Board[to].Clear();
                    room.Bar[opponent]++;
                    System.Console.WriteLine($"💥 {opponent} daşı BAR-a vuruldu! BAR[{opponent}]={room.Bar[opponent]}");
                }

                if (!room.Board.ContainsKey(to))
                    room.Board[to] = new List<string>();

                room.Board[to].Add(color);
                System.Console.WriteLine($"✅ {color} BAR-dan {to}-a köçdü. BAR[{color}]={room.Bar[color]}");
            }
            // BEARING OFF
            else if ((color == "white" && to < 1) || (color == "black" && to > 24))
            {
                if (room.Board[from].Count > 0 && room.Board[from].Contains(color))
                {
                    room.Board[from].Remove(color);
                    room.Home[color]++;

                    // Distance hesabla
                    int exactDistance = color == "white" ? from : 25 - from;

                    // Tam uyğun zər varsa onu istifadə et
                    if (room.RemainingMoves.Contains(exactDistance))
                    {
                        distance = exactDistance;
                    }
                    else
                    {
                        // Böyük zər istifadə et
                        var biggerDice = room.RemainingMoves.Where(d => d > exactDistance).ToList();
                        if (biggerDice.Count > 0)
                        {
                            distance = biggerDice.Min();
                        }
                        else
                        {
                            distance = exactDistance; // Fallback
                        }
                    }

                    System.Console.WriteLine($"🏠 {color} daşı HOME-a çıxarıldı! HOME[{color}]={room.Home[color]}, exactDistance={exactDistance}, usedDice={distance}");
                }
            }
            // Normal hərəkət
            else
            {
                if (!room.Board.ContainsKey(from) || room.Board[from].Count == 0)
                {
                    System.Console.WriteLine($"⚠️ ERROR: {from} nöqtəsində daş yoxdur!");
                    return;
                }

                // Yalnız öz rəngimizi götür
                if (!room.Board[from].Contains(color))
                {
                    System.Console.WriteLine($"⚠️ ERROR: {from} nöqtəsində {color} daş yoxdur!");
                    return;
                }

                distance = color == "white" ? from - to : to - from;

                room.Board[from].Remove(color);

                if (TryGetDoubleStepDice(room, distance, out var die, out var steps))
                {
                    var current = from;
                    for (int i = 0; i < steps; i++)
                    {
                        current = color == "white" ? current - die : current + die;

                        if (room.Board.ContainsKey(current) && room.Board[current].Count == 1 && room.Board[current][0] == opponent)
                        {
                            room.Board[current].Clear();
                            room.Bar[opponent]++;
                            System.Console.WriteLine($"💥 {opponent} daşı {current} nöqtəsindən qoşa zər addımında vuruldu! BAR[{opponent}]={room.Bar[opponent]}");
                        }
                    }

                    if (!room.Board.ContainsKey(to))
                        room.Board[to] = new List<string>();

                    room.Board[to].Add(color);
                    System.Console.WriteLine($"♟️ {color} daşı qoşa zərlə {from} → {to} ({steps} x {die}). BAR durumu: white={room.Bar["white"]}, black={room.Bar["black"]}");
                }
                else if (!room.RemainingMoves.Contains(distance) && TryGetCompositeMoveDice(room, from, to, distance, color, out var diceOrder))
                {
                    var current = from;
                    foreach (var moveDie in diceOrder)
                    {
                        current = color == "white" ? current - moveDie : current + moveDie;

                        if (room.Board.ContainsKey(current) && room.Board[current].Count == 1 && room.Board[current][0] == opponent)
                        {
                            room.Board[current].Clear();
                            room.Bar[opponent]++;
                            System.Console.WriteLine($"💥 {opponent} daşı {current} nöqtəsindən kompozit zər addımında vuruldu! BAR[{opponent}]={room.Bar[opponent]}");
                        }
                    }

                    if (!room.Board.ContainsKey(to))
                        room.Board[to] = new List<string>();

                    room.Board[to].Add(color);
                    System.Console.WriteLine($"♟️ {color} daşı kompozit zərlə {from} → {to} ({string.Join("+", diceOrder)}). BAR durumu: white={room.Bar["white"]}, black={room.Bar["black"]}");
                }
                else
                {
                    // Hədəfdə rəqib tək daşı varsa, vur
                    if (room.Board.ContainsKey(to) && room.Board[to].Count == 1 && room.Board[to][0] == opponent)
                    {
                        room.Board[to].Clear();
                        room.Bar[opponent]++;
                        System.Console.WriteLine($"💥 {opponent} daşı {to} nöqtəsindən vuruldu! BAR-a göndərildi. BAR[{opponent}]={room.Bar[opponent]}");
                    }

                    if (!room.Board.ContainsKey(to))
                        room.Board[to] = new List<string>();

                    room.Board[to].Add(color);
                    System.Console.WriteLine($"♟️ {color} daşı {from} → {to}. BAR durumu: white={room.Bar["white"]}, black={room.Bar["black"]}");
                }
            }

            // İstifadə olunan zəri sil
            if (distance > 0 && room.RemainingMoves.Contains(distance))
            {
                room.RemainingMoves.Remove(distance);
                System.Console.WriteLine($"🎲 Zər istifadə edildi: {distance}. Qalan: [{string.Join(", ", room.RemainingMoves)}]");
            }
            else if (distance > 0 && TryGetDoubleStepDice(room, distance, out var die, out var steps))
            {
                for (int i = 0; i < steps; i++)
                {
                    room.RemainingMoves.Remove(die);
                }

                System.Console.WriteLine($"🎲 Qoşa zər addımları istifadə edildi: {steps} x {die}. Qalan: [{string.Join(", ", room.RemainingMoves)}]");
            }
            else if (distance > 0 && TryGetCompositeMoveDice(room, from, to, distance, color, out var diceOrder))
            {
                foreach (var moveDie in diceOrder)
                {
                    room.RemainingMoves.Remove(moveDie);
                }

                System.Console.WriteLine($"🎲 Kompozit zərlər istifadə edildi: {string.Join("+", diceOrder)}. Qalan: [{string.Join(", ", room.RemainingMoves)}]");
            }
        }

        private static IEnumerable<int> GetDoubleStepDistances(BackgammonRoom room)
        {
            if (room.RemainingMoves.Count < 2)
                yield break;

            var die = room.RemainingMoves[0];
            if (room.RemainingMoves.Any(d => d != die))
                yield break;

            for (int steps = 2; steps <= room.RemainingMoves.Count; steps++)
            {
                yield return die * steps;
            }
        }

        private static IEnumerable<int> GetCompositeMoveDistances(BackgammonRoom room)
        {
            if (room.RemainingMoves.Count < 2 || room.RemainingMoves.Distinct().Count() == 1)
                yield break;

            var sums = new HashSet<int>();
            var moveCount = room.RemainingMoves.Count;
            for (int mask = 1; mask < (1 << moveCount); mask++)
            {
                var dice = new List<int>();
                for (int i = 0; i < moveCount; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        dice.Add(room.RemainingMoves[i]);
                }

                if (dice.Count >= 2)
                    sums.Add(dice.Sum());
            }

            foreach (var sum in sums)
                yield return sum;
        }

        private static bool TryGetDoubleStepDice(BackgammonRoom room, int distance, out int die, out int steps)
        {
            die = 0;
            steps = 0;

            if (room.RemainingMoves.Count < 2)
                return false;

            var candidateDie = room.RemainingMoves[0];
            if (candidateDie <= 0 || room.RemainingMoves.Any(d => d != candidateDie))
                return false;

            if (distance % candidateDie != 0)
                return false;

            die = candidateDie;
            steps = distance / candidateDie;
            return steps >= 2 && steps <= room.RemainingMoves.Count;
        }

        private static bool IsValidDoubleStepMove(BackgammonRoom room, int from, int to, int distance, string color)
        {
            if (!TryGetDoubleStepDice(room, distance, out var die, out var steps))
                return false;

            var current = from;
            for (int i = 0; i < steps; i++)
            {
                current = color == "white" ? current - die : current + die;

                if (current < 1 || current > 24)
                    return false;

                if (!IsPointOpen(room, current, color))
                    return false;
            }

            return current == to;
        }

        private static bool TryGetCompositeMoveDice(BackgammonRoom room, int from, int to, int distance, string color, out List<int> diceOrder)
        {
            diceOrder = new List<int>();

            if (room.RemainingMoves.Count < 2 || room.RemainingMoves.Distinct().Count() == 1)
                return false;

            var moveCount = room.RemainingMoves.Count;
            for (int mask = 1; mask < (1 << moveCount); mask++)
            {
                var dice = new List<int>();
                for (int i = 0; i < moveCount; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        dice.Add(room.RemainingMoves[i]);
                }

                if (dice.Count < 2 || dice.Sum() != distance)
                    continue;

                foreach (var permutation in GetDistinctPermutations(dice))
                {
                    if (IsCompositePathOpen(room, from, to, permutation, color))
                    {
                        diceOrder = permutation;
                        return true;
                    }
                }
            }

            return false;
        }

        private static IEnumerable<List<int>> GetDistinctPermutations(List<int> dice)
        {
            if (dice.Count == 0)
            {
                yield return new List<int>();
                yield break;
            }

            foreach (var die in dice.Distinct())
            {
                var remaining = new List<int>(dice);
                remaining.Remove(die);

                foreach (var permutation in GetDistinctPermutations(remaining))
                {
                    permutation.Insert(0, die);
                    yield return permutation;
                }
            }
        }

        private static bool IsCompositePathOpen(BackgammonRoom room, int from, int to, List<int> diceOrder, string color)
        {
            var current = from;
            foreach (var die in diceOrder)
            {
                current = color == "white" ? current - die : current + die;

                if (current < 1 || current > 24)
                    return false;

                if (!IsPointOpen(room, current, color))
                    return false;
            }

            return current == to;
        }

        private static bool IsPointOpen(BackgammonRoom room, int point, string color)
        {
            if (!room.Board.ContainsKey(point))
                return true;

            if (room.Board[point].Count == 0)
                return true;

            // Öz rəngimizdirsə açıqdır
            if (room.Board[point][0] == color)
                return true;

            // Rəqib 1 daşdırsa, vura bilərik
            if (room.Board[point].Count == 1)
                return true;

            // Rəqib 2+ daşdırsa, bloklanıb
            return false;
        }

        private static bool CanBearOff(BackgammonRoom room, string color)
        {
            // BAR-da daş varsa, çıxara bilməz
            if (room.Bar[color] > 0)
                return false;

            // Bütün daşlar home quadrant-da olmalıdır
            int homeStart = color == "white" ? 1 : 19;
            int homeEnd = color == "white" ? 6 : 24;

            for (int i = 1; i <= 24; i++)
            {
                if (i >= homeStart && i <= homeEnd)
                    continue;

                if (room.Board.ContainsKey(i) && room.Board[i].Any(p => p == color))
                {
                    System.Console.WriteLine($"❌ {color} bearing off MUMKUN DEYIL - Point {i}-də daş var (home: {homeStart}-{homeEnd})");
                    return false;
                }
            }

            System.Console.WriteLine($"✅ {color} bearing off MUMKUNDUR (home: {homeStart}-{homeEnd})");
            return true;
        }

        private static bool IsBearOffValid(BackgammonRoom room, int from, int distance, string color)
        {
            // Tam uyğun zər varsa
            if (room.RemainingMoves.Contains(distance))
                return true;

            // Daha böyük zər varsa və daha uzaq xanada daş yoxdursa, onu istifadə edə bilər
            var biggerDice = room.RemainingMoves.Where(d => d > distance).ToList();
            if (biggerDice.Count == 0)
                return false;

            // Daha uzaq xanada daş varmı yoxla
            if (color == "white")
            {
                // White üçün 6 daha uzaqdır; 4/5/6 boşdursa, məsələn 3-dən böyük zərlə çıxa bilər.
                for (int i = from + 1; i <= 6; i++)
                {
                    if (room.Board.ContainsKey(i) && room.Board[i].Any(p => p == color))
                    {
                        System.Console.WriteLine($"❌ White bearing off: Point {i}-də daha uzaq daş var");
                        return false;
                    }
                }
            }
            else
            {
                // Black üçün 19 daha uzaqdır; 19/20/21 boşdursa, məsələn 22-dən böyük zərlə çıxa bilər.
                for (int i = from - 1; i >= 19; i--)
                {
                    if (room.Board.ContainsKey(i) && room.Board[i].Any(p => p == color))
                    {
                        System.Console.WriteLine($"❌ Black bearing off: Point {i}-də daha uzaq daş var");
                        return false;
                    }
                }
            }

            System.Console.WriteLine($"✅ Bearing off valid: böyük zər ({biggerDice.Min()}) istifadə oluna bilər");
            return true;
        }

        public static bool CheckWin(BackgammonRoom room, string color)
        {
            // HOME-da 15 daş varsa qalib
            bool hasWon = room.Home[color] >= 15;

            if (hasWon)
            {
                System.Console.WriteLine($"🏆 {color} QALIB OLDU! HOME-da {room.Home[color]} daş");
            }

            return hasWon;
        }
    }
}
