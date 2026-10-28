using System.Text;

namespace FFMoGuang.Core;

public sealed record TeamMember(int Slot, string Name, bool IsSelf);
public sealed record TeamRoster(string Session, IReadOnlyList<TeamMember> Members);
public enum RankingMetric { Speed, Characters }
public sealed record TypingPlayerRow(int Slot, string Name, bool IsSelf, long? Messages, long? Characters,
    double? CharactersPerMinute, float Progress);
public sealed record TypingLeaderboard(IReadOnlyList<TypingPlayerRow> Players, string Status);

/// <summary>Only counts observed sends. Missing data is nullable, never presented as zero.</summary>
public sealed class TypingStatistics
{
    private sealed class Player(TeamMember member)
    {
        public TeamMember Member = member;
        public bool Observed = member.IsSelf;
        public long Messages;
        public long Characters;
        public Queue<(long Time, int Characters)> Recent = new();
    }
    private readonly Dictionary<int, Player> players = [];
    private string session = "";
    private long? endedAt;
    public void SetRoster(TeamRoster roster)
    {
        if (roster.Session != session)
        {
            players.Clear();
            session = roster.Session;
        }
        endedAt = null;
        foreach (var member in roster.Members.Where(m => m.Slot is >= 0 and < 4))
        {
            if (!players.TryGetValue(member.Slot, out var player) || player.Member.Name != member.Name)
                players[member.Slot] = new(member);
            else
            {
                player.Member = member;
                player.Observed |= member.IsSelf;
            }
        }
    }
    public void End(long now) { if (session.Length > 0) endedAt ??= now; }
    public void Reset()
    {
        foreach (var player in players.Values)
        {
            player.Messages = player.Characters = 0;
            player.Recent.Clear();
        }
    }
    public bool RecordSelf(string text, long now)
    {
        var self = players.Values.Where(p => p.Member.IsSelf).ToArray();
        return self.Length == 1 && Record(self[0].Member.Slot, text, now);
    }
    public bool Record(int slot, string text, long now)
    {
        if (endedAt.HasValue || string.IsNullOrWhiteSpace(text) || !players.TryGetValue(slot, out var player)) return false;
        var count = text.EnumerateRunes().Count();
        player.Observed = true;
        player.Messages++;
        player.Characters += count;
        player.Recent.Enqueue((now, count));
        return true;
    }
    public TypingLeaderboard Snapshot(long now, RankingMetric metric)
    {
        var at = endedAt ?? now;
        var rows = new List<TypingPlayerRow>();
        for (var slot = 0; slot < 4; slot++)
        {
            if (!players.TryGetValue(slot, out var player))
            {
                rows.Add(new(slot, $"队员 {slot + 1}（等待姓名）", false, null, null, null, 0));
                continue;
            }
            while (player.Recent.TryPeek(out var item) && at - item.Time >= 10000) player.Recent.Dequeue();
            double? speed = player.Observed ? player.Recent.Sum(s => s.Characters) * 6d : null;
            rows.Add(new(slot, player.Member.Name, player.Member.IsSelf,
                player.Observed ? player.Messages : null, player.Observed ? player.Characters : null, speed, 0));
        }
        double Value(TypingPlayerRow row) => metric == RankingMetric.Speed ? row.CharactersPerMinute ?? -1 : row.Characters ?? -1;
        var sorted = rows.OrderByDescending(Value).ThenByDescending(p => p.Characters ?? -1).ThenBy(p => p.Slot).ToArray();
        var max = Math.Max(0, sorted.Max(Value));
        sorted = sorted.Select(p => p with { Progress = max > 0 ? (float)Math.Max(0, Value(p) / max) : 0 }).ToArray();
        return new(sorted, session.Length == 0 ? "等待魔光键影队伍" :
            "自己：直接提交调用、模拟或手动 Enter 后输入清空观察，非服务器命中确认；队友数据待采样。速度按最近 10 秒折算字/分钟。"
            + (endedAt.HasValue ? " 已保留上局统计。" : ""));
    }
}
