using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

public sealed class Dust2Trainer : BasePlugin
{
    public override string ModuleName => "Dust2Trainer";
    public override string ModuleVersion => "0.1.1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Marks = ["bluecar-player", "bluecar-bot", "peek-player", "peek-start", "peek-end"];
    private string PointsFile => Path.Combine(ModuleDirectory, "points.json");
    private Attempt? attempt;

    public override void Load(bool hotReload)
    {
        AddCommand("css_bluecar", "Start the blue car drill", (player, _) => Start(player, false));
        AddCommand("css_peek", "Start the wide peek drill", (player, _) => Start(player, true));
        AddCommand("css_mark", "Save a drill point at your feet", Mark);
        AddCommand("css_stopdrill", "Stop the drill", (player, _) =>
        {
            if (OwnedBy(player)) Finish("stopped");
        });
        RegisterListener<Listeners.OnTick>(Tick);
        RegisterListener<Listeners.OnMapEnd>(() => Finish("map_ended"));
        RegisterListener<Listeners.OnMapStart>(map =>
        {
            if (map == "de_dust2") Server.ExecuteCommand("exec dust2_trainer.cfg");
        });
        RegisterEventHandler<EventWeaponFire>(Fire);
        RegisterEventHandler<EventPlayerHurt>(Hurt);
        RegisterEventHandler<EventPlayerDeath>(Death);
    }

    public override void Unload(bool hotReload) => Finish("plugin_unloaded");

    private static bool IsDust2 => Server.MapName == "de_dust2";
    private bool OwnedBy(CCSPlayerController? player) =>
        attempt is { } run && player is { IsValid: true } && player.Handle == run.Player.Handle;

    private static bool IsSolo(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false } &&
        Utilities.GetPlayers().Count(p => p.IsValid && !p.IsBot && !p.IsHLTV) == 1;

    private Dictionary<string, Point> ReadPoints() =>
        File.Exists(PointsFile)
            ? JsonSerializer.Deserialize<Dictionary<string, Point>>(File.ReadAllText(PointsFile), Json) ?? []
            : [];

    private void Mark(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsSolo(player) || !IsDust2 || player!.PlayerPawn.Value is not { IsValid: true, AbsOrigin: { } origin } pawn ||
            !player.PawnIsAlive)
            return;

        string key = command.GetArg(1);
        if (!Marks.Contains(key))
        {
            player.PrintToChat("Usage: !mark " + string.Join(" | ", Marks));
            return;
        }

        try
        {
            var points = ReadPoints();
            points[key] = new Point(origin.X, origin.Y, origin.Z, pawn.EyeAngles.X, pawn.EyeAngles.Y);
            File.WriteAllText(PointsFile + ".tmp", JsonSerializer.Serialize(points, new JsonSerializerOptions(Json) { WriteIndented = true }));
            File.Move(PointsFile + ".tmp", PointsFile, true);
            player.PrintToChat($"[Drill] Saved {key}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.LogError(error, "Could not save drill points");
            player.PrintToChat("[Drill] Could not save points. Check the server log.");
        }
    }

    private void Start(CCSPlayerController? player, bool peek)
    {
        if (player is not { IsValid: true }) return;
        if (!IsSolo(player) || !IsDust2)
        {
            player.PrintToChat("[Drill] Requires one human on de_dust2.");
            return;
        }

        string mode = peek ? "peek" : "bluecar";
        string playerKey = mode + "-player";
        string botKey = peek ? "peek-start" : "bluecar-bot";
        Dictionary<string, Point> points;
        try
        {
            points = ReadPoints();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.LogError(error, "Could not read drill points");
            player.PrintToChat("[Drill] Could not read points.json.");
            return;
        }

        points.TryGetValue("peek-end", out var end);
        if (!points.TryGetValue(playerKey, out var start) || !points.TryGetValue(botKey, out var botStart) ||
            start is null || botStart is null || (peek && end is null))
        {
            player.PrintToChat($"[Drill] Mark {playerKey}, {botKey}" + (peek ? " and peek-end first." : " first."));
            return;
        }

        if (peek && (Distance(botStart, end!) < 32 || Math.Abs(botStart.Z - end!.Z) > 8))
        {
            player.PrintToChat("[Drill] Mark a straight peek at least 32 units long on level ground.");
            return;
        }

        Finish("restarted");
        attempt = new Attempt(player, mode, start, botStart, peek ? end : null, Server.CurrentTime);
        Server.ExecuteCommand("sv_cheats 1; mp_autoteambalance 0; mp_limitteams 0; " +
            "mp_ignore_round_win_conditions 1; mp_freezetime 0; mp_warmup_end; " +
            "bot_quota_mode normal; bot_join_after_player 0; bot_quota 0; bot_kick; bot_stop 1");
        if (player.Team != CsTeam.Terrorist) player.ChangeTeam(CsTeam.Terrorist);
        player.PrintToChat($"[Drill] Setting up {mode}...");
    }

    private void Tick()
    {
        if (attempt is not { } run) return;
        if (run.PendingOutcome is { } outcome)
        {
            Finish(outcome);
            return;
        }
        if (!run.Player.IsValid)
        {
            Finish("disconnected");
            return;
        }

        float now = Server.CurrentTime;
        if (run.ReadyAt is null)
        {
            Prepare(run, now);
            return;
        }

        if (!run.Player.PawnIsAlive || run.Player.PlayerPawn.Value is not { IsValid: true } playerPawn ||
            run.Bot is not { IsValid: true, PawnIsAlive: true } bot ||
            bot.PlayerPawn.Value is not { IsValid: true } botPawn)
        {
            Finish("target_or_player_lost");
            return;
        }

        if (now - run.ReadyAt.Value >= 45)
        {
            Finish("timeout");
            return;
        }

        if (run.BotEnd is { } end && !run.PeekFinished && now >= run.PeekAt)
        {
            run.PeekTick ??= Server.TickCount;
            const float speed = 230f;
            float length = Distance(run.BotStart, end);
            float fraction = Math.Clamp((now - run.PeekAt) * speed / length, 0f, 1f);
            var from = run.BotStart;
            var position = new Vector(
                from.X + (end.X - from.X) * fraction,
                from.Y + (end.Y - from.Y) * fraction,
                from.Z + (end.Z - from.Z) * fraction);
            botPawn.Teleport(position, null, new Vector(0, 0, 0));
            run.PeekFinished = fraction >= 1;
        }

        SampleVisibility(run, playerPawn, botPawn);
    }

    private void Prepare(Attempt run, float now)
    {
        if (now - run.RequestedAt > 8)
        {
            Finish($"setup_timeout ({run.SetupStatus})");
            return;
        }

        // Let queued bot commands and team changes settle before reading the pawns.
        if (now - run.RequestedAt < 0.25f) return;
        if (!run.BotRequested)
        {
            Server.ExecuteCommand("bot_add_ct");
            run.BotRequested = true;
            run.SetupStatus = "waiting for CT bot";
            return;
        }

        var bot = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.IsBot && p.Team == CsTeam.CounterTerrorist);
        if (bot is null) return;
        if (run.Player.Team != CsTeam.Terrorist)
        {
            run.SetupStatus = "player has not joined T";
            return;
        }

        var botPawn = bot.PlayerPawn.Value;
        var playerPawn = run.Player.PlayerPawn.Value;
        if (!bot.PawnIsAlive || botPawn is not { IsValid: true } ||
            !run.Player.PawnIsAlive || playerPawn is not { IsValid: true })
        {
            run.SetupStatus = !bot.PawnIsAlive || botPawn is not { IsValid: true }
                ? "CT bot has not spawned" : "T player has not spawned";
            if (now >= run.NextRespawnAt)
            {
                if (!bot.PawnIsAlive || botPawn is not { IsValid: true }) bot.Respawn();
                if (!run.Player.PawnIsAlive || playerPawn is not { IsValid: true }) run.Player.Respawn();
                run.NextRespawnAt = now + 1f;
            }
            return;
        }

        if (run.BotEnd is { } end)
        {
            Vector from = run.BotStart.Position();
            Vector to = end.Position();
            from.Z += 3;
            to.Z += 3;
            var sweep = Trace.TraceHullShape(from, to, new Vector(-16, -16, 0), new Vector(16, 16, 70), botPawn);
            if (sweep.DidHit() || sweep.IsAllSolid)
            {
                Finish("peek_path_blocked");
                return;
            }
        }

        var zero = new Vector(0, 0, 0);
        run.Player.RemoveWeapons();
        run.Player.GiveNamedItem("weapon_ak47");
        bot.RemoveWeapons();
        playerPawn.Teleport(run.PlayerStart.Position(), run.PlayerStart.Angles(), zero);
        botPawn.Teleport(run.BotStart.Position(), run.BotStart.Angles(), zero);
        run.Bot = bot;
        run.ReadyAt = now;
        run.StartTick = Server.TickCount;
        run.PeekAt = now + 0.8f + Random.Shared.NextSingle() * 1.6f;
        SampleVisibility(run, playerPawn, botPawn);
        run.Player.PrintToChat("[Drill] Ready.");
    }

    private static void SampleVisibility(Attempt run, CCSPlayerPawn player, CCSPlayerPawn bot)
    {
        if (run.VisibleTick.HasValue || player.AbsOrigin is not { } p || bot.AbsOrigin is not { } b) return;
        var eye = new Vector(p.X, p.Y, p.Z + player.ViewOffset.Z);
        var angles = player.EyeAngles;
        const float radians = MathF.PI / 180f;
        float pitch = angles.X * radians;
        float yaw = angles.Y * radians;
        float forwardX = MathF.Cos(pitch) * MathF.Cos(yaw);
        float forwardY = MathF.Cos(pitch) * MathF.Sin(yaw);
        float forwardZ = -MathF.Sin(pitch);
        // Approximate a 90-degree cone. This is a server estimate, not a client-frame sample.
        foreach (float height in new[] { bot.ViewOffset.Z, bot.ViewOffset.Z * 0.72f })
        {
            var target = new Vector(b.X, b.Y, b.Z + height);
            float x = target.X - eye.X, y = target.Y - eye.Y, z = target.Z - eye.Z;
            float projection = x * forwardX + y * forwardY + z * forwardZ;
            if (projection <= 0 || 2 * projection * projection < x * x + y * y + z * z) continue;
            var trace = Trace.TraceEndShape(eye, target, player);
            if (!trace.IsAllSolid && (!trace.DidHit() || trace.HitEntity()?.Handle == bot.Handle))
            {
                run.VisibleTick = Server.TickCount;
                return;
            }
        }
    }

    private HookResult Fire(EventWeaponFire e, GameEventInfo info)
    {
        if (attempt is { ReadyAt: not null } run && OwnedBy(e.Userid) && e.Weapon is "ak47" or "weapon_ak47")
        {
            run.FireTick ??= Server.TickCount;
            run.Shots++;
        }
        return HookResult.Continue;
    }

    private HookResult Hurt(EventPlayerHurt e, GameEventInfo info)
    {
        if (attempt is { ReadyAt: not null, Bot: { } bot } run && OwnedBy(e.Attacker) &&
            e.Userid?.Handle == bot.Handle && e.DmgHealth > 0 && e.Weapon is "ak47" or "weapon_ak47")
        {
            run.HitTick ??= Server.TickCount;
            run.FirstHitgroup ??= e.Hitgroup;
        }
        return HookResult.Continue;
    }

    private HookResult Death(EventPlayerDeath e, GameEventInfo info)
    {
        if (attempt is not { ReadyAt: not null, Bot: { } bot } run) return HookResult.Continue;
        if (e.Userid?.Handle == bot.Handle)
        {
            bool killed = OwnedBy(e.Attacker) && e.Weapon is "ak47" or "weapon_ak47";
            if (killed) run.KillTick = Server.TickCount;
            // Let the engine deliver remaining weapon and damage events from this tick.
            run.PendingOutcome = killed ? "kill" : "bot_died";
        }
        else if (OwnedBy(e.Userid))
        {
            run.PendingOutcome = "player_died";
        }
        return HookResult.Continue;
    }

    private void Finish(string outcome)
    {
        if (attempt is not { } run) return;
        attempt = null;
        outcome = run.PendingOutcome ?? outcome;
        double tickMs = Server.TickInterval * 1000.0;
        double? Elapsed(int? first, int? last) => (last - first) * tickMs;
        string Format(double? ms) => ms is { } value ? $"{value:0} ms" : "--";

        if (run.Player.IsValid)
        {
            run.Player.PrintToChat($"[Drill] {run.Mode}: {outcome}");
            if (run.ReadyAt.HasValue)
            {
                run.Player.PrintToChat($"Visible -> fire {Format(Elapsed(run.VisibleTick, run.FireTick))} | " +
                    $"fire -> hit {Format(Elapsed(run.FireTick, run.HitTick))} | " +
                    $"hit -> kill {Format(Elapsed(run.HitTick, run.KillTick))} | " +
                    $"visible -> kill {Format(Elapsed(run.VisibleTick, run.KillTick))}");
            }
        }

        if (!run.ReadyAt.HasValue) return;
        var record = new
        {
            schemaVersion = 1, endedAtUtc = DateTimeOffset.UtcNow, map = "de_dust2", run.Mode, outcome,
            tickMs, run.StartTick, run.PeekTick, run.VisibleTick, run.FireTick, run.HitTick, run.KillTick,
            weapon = "ak47", run.Shots, run.FirstHitgroup, run.PlayerStart, run.BotStart, run.BotEnd
        };
        try
        {
            File.AppendAllText(Path.Combine(ModuleDirectory, "attempts.jsonl"), JsonSerializer.Serialize(record, Json) + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.LogError(error, "Could not save drill attempt");
            if (run.Player.IsValid) run.Player.PrintToChat("[Drill] Could not save attempt.");
        }
    }

    private static float Distance(Point a, Point b) =>
        MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));

    public sealed record Point(float X, float Y, float Z, float Pitch, float Yaw)
    {
        public Vector Position() => new(X, Y, Z);
        public QAngle Angles() => new(Pitch, Yaw, 0);
    }

    private sealed class Attempt(CCSPlayerController player, string mode, Point playerStart, Point botStart,
        Point? botEnd, float requestedAt)
    {
        public CCSPlayerController Player { get; } = player;
        public string Mode { get; } = mode;
        public Point PlayerStart { get; } = playerStart;
        public Point BotStart { get; } = botStart;
        public Point? BotEnd { get; } = botEnd;
        public float RequestedAt { get; } = requestedAt;
        public bool BotRequested { get; set; }
        public float NextRespawnAt { get; set; }
        public string SetupStatus { get; set; } = "waiting for setup commands";
        public CCSPlayerController? Bot { get; set; }
        public float? ReadyAt { get; set; }
        public float PeekAt { get; set; }
        public bool PeekFinished { get; set; }
        public string? PendingOutcome { get; set; }
        public int? StartTick { get; set; }
        public int? PeekTick { get; set; }
        public int? VisibleTick { get; set; }
        public int? FireTick { get; set; }
        public int? HitTick { get; set; }
        public int? KillTick { get; set; }
        public int? FirstHitgroup { get; set; }
        public int Shots { get; set; }
    }
}
