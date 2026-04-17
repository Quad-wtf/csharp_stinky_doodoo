using Discord;
using Discord.WebSocket;
using Discord.Rest;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Diagnostics.Contracts;

static class Emojis
{
    public const string BanHammer     = "<:BanHammer:1471061548444160073>";
    public const string coolguy       = "<:coolguy:1471062526224629783>";
    public const string HAHAHAHAHAH   = "<:HAHAHAHAHAH:1463527123825463326>";
    public const string I_DUNNO       = "<:I_DUNNO:1463527298245591091>";
    public const string MUTETHEPERSON = "<:MUTETHEPERSON:1471062688099598442>";
    public const string TIMEOUT       = "<:TIMEOUT:1471062490703204385>";
    public const string ban_thinking  = "<:ban_thinking:1471061607948882152>";
    public const string bnuyinlove    = "<:bnuyinlove:1471062825429504172>";
    public const string bomboclat     = "<:bomboclat:1463526529098055802>";
    public const string bonk          = "<:bonk:1463527007131537531>";
    public const string cornball      = "<:cornball:1471062777081892906>";
    public const string devious       = "<:devious:1463527169643909141>";
    public const string eh            = "<:eh:1471061029034397788>";
    public const string empty         = "<:empty:1471060785907236874>";
    public const string ew            = "<:ew:1463526878634573896>";
    public const string femboy        = "<:femboy:1463526371740614728>";
    public const string getmuted      = "<:getmuted:1471062600409288831>";
    public const string interesting   = "<:interesting:1463527484405448747>";
    public const string jawonthefloor = "<:jawonthefloor:1471062859952947336>";
    public const string moneyface     = "<:moneyface:1463527541225685233>";
    public const string muted         = "<:muted:1471061503464706170>";
    public const string nahnahnah     = "<:nahnahnah:1463526945693106408>";
    public const string reverse       = "<:reverse:1471062740100579462>";
    public const string saythatagain  = "<:saythatagain:1463527631667462214>";
    public const string stoopid       = "<:stoopid:1463526846464397467>";
}

record DailyData(int Points, int Streak, DateTime LastClaim);

static class DailyStore
{
    private static readonly string FilePath = "daily.json";
    public static Dictionary<ulong, DailyData> Data = new();

    public static void Load()
    {
        if (!File.Exists(FilePath)) return;
        var json = File.ReadAllText(FilePath);
        Data = JsonSerializer.Deserialize<Dictionary<ulong, DailyData>>(json) ?? new();
    }

    public static void Save()
    {
        var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}

// Holds AFK state for a user
record AfkEntry(string Reason, DateTimeOffset Since);

class Program
{
    private DiscordSocketClient? _client;

    private static readonly DateTime _startTime = DateTime.UtcNow;

    // userId -> AFK entry
    private readonly Dictionary<ulong, AfkEntry> _afkUsers = new();

    private static readonly string[] _commands = ["!ping", "!ban", "!kick", "!help", "!8ball", "!coinflip", "!urban", "!serverinfo", "!userinfo", "!avatar", "!afk", "!daily", "!leaderboard"];

    static async Task Main() => await new Program().RunAsync();

    async Task RunAsync()
    {
        DailyStore.Load();

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");

        if (token == null)
        {
            var json = File.ReadAllText("config.json");
            var configFile = JsonDocument.Parse(json);
            token = configFile.RootElement.GetProperty("token").GetString();
        }

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
        };

        _client = new DiscordSocketClient(config);

        _client.Log += msg => { Console.WriteLine(msg); return Task.CompletedTask; };
        _client.MessageReceived += OnMessageReceived;
        _client.Ready += () => { Console.WriteLine("\nBot is ready!\n"); return Task.CompletedTask; };

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(Timeout.Infinite);
    }

    // Resolves a guild member by ID — checks cache first, falls back to REST API
    async Task<IGuildUser?> ResolveGuildUserAsync(SocketGuild guild, ulong userId)
    {
        if (guild.GetUser(userId) is { } cached)
            return cached;

        try { return await _client!.Rest.GetGuildUserAsync(guild.Id, userId); }
        catch { return null; }
    }

    // Formats a TimeSpan into a compact "Xh Ym Zs" string
    static string FormatDuration(TimeSpan t)
    {
        if (t.TotalMinutes < 1)  return $"{t.Seconds}s";
        if (t.TotalHours   < 1)  return $"{t.Minutes}m {t.Seconds}s";
        return $"{(int)t.TotalHours}h {t.Minutes}m";
    }

    async Task OnMessageReceived(SocketMessage msg)
    {
        if (msg.Author.IsBot) return;
        if (msg.Channel is not SocketGuildChannel guildChannel) return;
        if (msg.Author is not SocketGuildUser caller) return;

        // Auto-log any recognised command
        var matched = _commands.FirstOrDefault(cmd => msg.Content == cmd || msg.Content.StartsWith(cmd + " "));
        if (matched != null)
        {
            var timestamp = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
            Console.WriteLine($"[{timestamp}] Received command \"{msg.Content}\" from @{msg.Author.Username} in #{msg.Channel.Name}");
        }

        // AFK: auto-remove when the AFK user sends any message
        if (_afkUsers.TryGetValue(caller.Id, out var callerAfk))
        {
            // Skip removal if this is their own !afk command (so the command below can respond first)
            if (!msg.Content.StartsWith("!afk"))
            {
                _afkUsers.Remove(caller.Id);
                var gone = FormatDuration(DateTimeOffset.UtcNow - callerAfk.Since);
                await msg.Channel.SendMessageAsync($"Welcome back, {caller.Mention}! You were AFK for **{gone}**.");
            }
        }

        // AFK: notify when someone pings an AFK user 
        foreach (var mentioned in msg.MentionedUsers)
        {
            if (mentioned.Id == caller.Id) continue; // ignore self-mentions
            if (!_afkUsers.TryGetValue(mentioned.Id, out var entry)) continue;

            var ago = FormatDuration(DateTimeOffset.UtcNow - entry.Since);
            var reasonPart = entry.Reason.Length > 0 ? $" — *{entry.Reason}*" : "";
            await msg.Channel.SendMessageAsync(
                $"**{mentioned.Username}** is currently AFK{reasonPart} *(since {ago} ago)*");
        }

        if (msg.Content == "!ping")
        {
            var sw = Stopwatch.StartNew();
            var sent = await msg.Channel.SendMessageAsync("Pinging...");
            sw.Stop();

            long gatewayLatency = _client!.Latency;
            long messageLatency = sw.ElapsedMilliseconds;

            using var proc = Process.GetCurrentProcess();
            long ramUsedMB = proc.WorkingSet64 / 1024 / 1024;
            long totalRamMB = GetTotalRamMB();

            var uptime = DateTime.UtcNow - _startTime;
            string uptimeStr = $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";

            string discordNetVersion = typeof(DiscordSocketClient).Assembly.GetName().Version?.ToString(3) ?? "?";
            string dotnetVersion = Environment.Version.ToString();

            var embed = new EmbedBuilder()
                .WithTitle($"{Emojis.coolguy} Pong!")
                .WithColor(gatewayLatency switch
                {
                    < 100 => Color.Green,
                    < 250 => Color.Gold,
                    _     => Color.Red
                })
                .WithCurrentTimestamp()
                .AddField("Gateway Latency", $"`{gatewayLatency}ms`",               inline: true)
                .AddField("Message Latency", $"`{messageLatency}ms`",               inline: true)
                .AddField("RAM Usage",       $"`{ramUsedMB} MB / {totalRamMB} MB`", inline: true)
                .AddField("Uptime",          $"`{uptimeStr}`",                      inline: true)
                .AddField("Commands",        $"`{_commands.Length}`",               inline: true)
                .AddField("Discord.Net",     $"`v{discordNetVersion}`",             inline: true)
                .AddField(".NET Runtime",    $"`v{dotnetVersion}`",                 inline: true)
                .Build();

            await sent.ModifyAsync(m =>
            {
                m.Content = string.Empty;
                m.Embed = embed;
            });
        }

        if (msg.Content.StartsWith("!afk"))
        {
            var reason = msg.Content.Length > 5 ? msg.Content[5..].Trim() : "";

            _afkUsers[caller.Id] = new AfkEntry(reason, DateTimeOffset.UtcNow);

            var reasonPart = reason.Length > 0 ? $": *{reason}*" : ".";
            await msg.Channel.SendMessageAsync($"{caller.Mention} is now AFK{reasonPart}");
        }

        if (msg.Content.StartsWith("!ban "))
        {
            if (!caller.GuildPermissions.BanMembers)
            {
                await msg.Channel.SendMessageAsync("You don't have permission to ban members.");
                return;
            }

            var mentionedUser = msg.MentionedUsers.FirstOrDefault();
            if (mentionedUser == null)
            {
                await msg.Channel.SendMessageAsync("Please mention a user. Usage: `!ban @user reason`");
                return;
            }

            var target = await ResolveGuildUserAsync(guildChannel.Guild, mentionedUser.Id);
            if (target == null)
            {
                await msg.Channel.SendMessageAsync("Couldn't resolve that user.");
                return;
            }

            var reason = msg.Content.Contains(">")
                ? msg.Content[(msg.Content.IndexOf('>') + 1)..].Trim()
                : "No reason provided";
            if (string.IsNullOrEmpty(reason)) reason = "No reason provided";

            try
            {
                await guildChannel.Guild.AddBanAsync(target.Id, 0, reason);
                await msg.Channel.SendMessageAsync($"**{target.Username}** has been banned. Reason: {reason}");
            }
            catch (Discord.Net.HttpException ex)
            {
                await msg.Channel.SendMessageAsync($"Failed to ban: {ex.Message}");
            }
        }

        if (msg.Content.StartsWith("!kick "))
        {
            if (!caller.GuildPermissions.KickMembers)
            {
                await msg.Channel.SendMessageAsync("You don't have permission to kick members.");
                return;
            }

            var mentionedUser = msg.MentionedUsers.FirstOrDefault();
            if (mentionedUser == null)
            {
                await msg.Channel.SendMessageAsync("Please mention a user. Usage: `!kick @user reason`");
                return;
            }

            var target = await ResolveGuildUserAsync(guildChannel.Guild, mentionedUser.Id);
            if (target == null)
            {
                await msg.Channel.SendMessageAsync("Couldn't resolve that user.");
                return;
            }

            var reason = msg.Content.Contains(">")
                ? msg.Content[(msg.Content.IndexOf('>') + 1)..].Trim()
                : "No reason provided";
            if (string.IsNullOrEmpty(reason)) reason = "No reason provided";

            try
            {
                await target.KickAsync(reason);
                await msg.Channel.SendMessageAsync($"**{target.Username}** has been kicked. Reason: {reason}");
            }
            catch (Discord.Net.HttpException ex)
            {
                await msg.Channel.SendMessageAsync($"Failed to kick: {ex.Message}");
            }
        }

        if (msg.Content.StartsWith("!8ball "))
        {
            string[] responses =
            [
                "It is certain.", "Without a doubt.", "You may rely on it.",
                "Yes, definitely.", "Most likely.", "Outlook good.",
                "Signs point to yes.", "Reply hazy, try again.", "Ask again later.",
                "Better not tell you now.", "Cannot predict now.", "Don't count on it.",
                "My reply is no.", "My sources say no.", "Very doubtful."
            ];

            var question = msg.Content[7..].Trim();
            if (string.IsNullOrEmpty(question))
            {
                await msg.Channel.SendMessageAsync("Ask a question! Usage: `!8ball <question>`");
                return;
            }

            await msg.Channel.SendMessageAsync($"{responses[Random.Shared.Next(responses.Length)]}");
        }

        if (msg.Content.StartsWith("!urban "))
        {
            var term = msg.Content[7..].Trim();
            if (string.IsNullOrEmpty(term))
            {
                await msg.Channel.SendMessageAsync("Usage: `!urban <term>`");
                return;
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "DiscordBot");

            var response = await http.GetStringAsync($"https://api.urbandictionary.com/v0/define?term={Uri.EscapeDataString(term)}");
            var json = JsonDocument.Parse(response);
            var list = json.RootElement.GetProperty("list");

            if (list.GetArrayLength() == 0)
            {
                await msg.Channel.SendMessageAsync($"No results found for **{term}**.");
                return;
            }

            var entry      = list[0];
            var definition = entry.GetProperty("definition").GetString() ?? "N/A";
            var example    = entry.GetProperty("example").GetString() ?? "N/A";
            var thumbsUp   = entry.GetProperty("thumbs_up").GetInt32();
            var thumbsDown = entry.GetProperty("thumbs_down").GetInt32();
            var author     = entry.GetProperty("author").GetString() ?? "Unknown";
            var permalink  = entry.GetProperty("permalink").GetString() ?? "";

            definition = System.Text.RegularExpressions.Regex.Replace(definition, @"\[|\]", "");
            example    = System.Text.RegularExpressions.Regex.Replace(example,    @"\[|\]", "");

            if (definition.Length > 1024) definition = definition[..1021] + "...";
            if (example.Length    > 1024) example    = example[..1021]    + "...";

            var embed = new EmbedBuilder()
                .WithTitle($"{term}")
                .WithUrl(permalink)
                .WithColor(new Color(0xEFFF00))
                .AddField("Definition", definition)
                .AddField("Example", string.IsNullOrWhiteSpace(example) ? "*None*" : example)
                .WithFooter($"👍 {thumbsUp}  👎 {thumbsDown}  •  by {author}")
                .WithCurrentTimestamp()
                .Build();

            await msg.Channel.SendMessageAsync(embed: embed);
        }

        if (msg.Content == "!serverinfo")
        {
            var guild = guildChannel.Guild;

            var embed = new EmbedBuilder()
                .WithTitle($"{guild.Name}")
                .WithThumbnailUrl(guild.IconUrl)
                .WithColor(Color.Blue)
                .AddField("Owner",       $"<@{guild.OwnerId}>",                         inline: true)
                .AddField("Members",     $"`{guild.MemberCount}`",                       inline: true)
                .AddField("Channels",    $"`{guild.Channels.Count}`",                    inline: true)
                .AddField("Roles",       $"`{guild.Roles.Count}`",                       inline: true)
                .AddField("Boost Level", $"`{guild.PremiumTier}`",                       inline: true)
                .AddField("Boosts",      $"`{guild.PremiumSubscriptionCount}`",          inline: true)
                .AddField("Created",     $"<t:{guild.CreatedAt.ToUnixTimeSeconds()}:D>", inline: true)
                .WithCurrentTimestamp()
                .Build();

            await msg.Channel.SendMessageAsync(embed: embed);
        }

        if (msg.Content.StartsWith("!userinfo"))
        {
            var mentionedUser = msg.MentionedUsers.FirstOrDefault();
            IGuildUser target = mentionedUser != null
                ? await ResolveGuildUserAsync(guildChannel.Guild, mentionedUser.Id) ?? caller
                : caller;

            var roles = target is SocketGuildUser socketUser
                ? socketUser.Roles
                    .Where(r => !r.IsEveryone)
                    .OrderByDescending(r => r.Position)
                    .Select(r => r.Mention)
                : [];

            var rolesStr = roles.Any() ? string.Join(", ", roles) : "*None*";
            if (rolesStr.Length > 1024) rolesStr = rolesStr[..1021] + "...";

            var topColor = target is SocketGuildUser su
                ? su.Roles.OrderByDescending(r => r.Position).FirstOrDefault(r => r.Colors.PrimaryColor.RawValue != 0)?.Colors.PrimaryColor ?? Color.Default
                : Color.Default;

            var embed = new EmbedBuilder()
                .WithTitle(target.Username)
                .WithThumbnailUrl(target.GetAvatarUrl() ?? target.GetDefaultAvatarUrl())
                .WithColor(topColor)
                .AddField("Display Name",    target.DisplayName,                                    inline: true)
                .AddField("Account Created", $"<t:{target.CreatedAt.ToUnixTimeSeconds()}:D>",       inline: true)
                .AddField("Joined Server",   $"<t:{target.JoinedAt?.ToUnixTimeSeconds() ?? 0}:D>", inline: true)
                .AddField("Roles",           rolesStr)
                .WithFooter($"ID: {target.Id}")
                .WithCurrentTimestamp()
                .Build();

            await msg.Channel.SendMessageAsync(embed: embed);
        }

        if (msg.Content.StartsWith("!avatar"))
        {
            var mentionedUser = msg.MentionedUsers.FirstOrDefault();
            IGuildUser target = mentionedUser != null
                ? await ResolveGuildUserAsync(guildChannel.Guild, mentionedUser.Id) ?? caller
                : caller;

            var avatarUrl = target.GetAvatarUrl(size: 512) ?? target.GetDefaultAvatarUrl();

            var embed = new EmbedBuilder()
                .WithTitle($"{target.Username}'s Avatar")
                .WithImageUrl(avatarUrl)
                .WithUrl(avatarUrl)
                .WithColor(Color.DarkerGrey)
                .WithCurrentTimestamp()
                .Build();

            await msg.Channel.SendMessageAsync(embed: embed);
        }

        if (msg.Content == "!help")
        {
            var embed = new EmbedBuilder()
                .WithTitle($"{Emojis.bomboclat} Commands")
                .WithColor(Color.Blue)
                .WithDescription(string.Join("\n", _commands.Select(c => $"`{c}`")))
                .WithFooter($"{_commands.Length} commands total")
                .WithCurrentTimestamp()
                .Build();

            await msg.Channel.SendMessageAsync(embed: embed);
        }

        if (msg.Content == "!daily")
            {
                var now = DateTime.UtcNow;

                if (!DailyStore.Data.TryGetValue(caller.Id, out var data))
                {
                    data = new DailyData(0, 0, DateTime.MinValue);
                }

                int reward = 20;

                if (data.LastClaim != DateTime.MinValue)
                {
                    var diff = (now - data.LastClaim).TotalDays;

                     if (diff < 1)
                    {
                        await msg.Channel.SendMessageAsync("You already claimed your daily reward today.");
                        return;
                    }

                    if (diff < 2)
                    {
                        data = data with { Streak = data.Streak + 1 };
                        reward += data.Streak * 5;
                    }
                    else
                    {
                        data = data with { Streak = 0 };
                    }
                }

                data = data with
                {
                    Points = data.Points + reward,
                    LastClaim = now
                };

                DailyStore.Data[caller.Id] = data;
                DailyStore.Save();

                var embed = new EmbedBuilder()
                    .WithTitle("Daily Reward")
                    .WithColor(Color.Gold)
                    .AddField("Reward", $"+{reward} Mypoints(r)", true)
                    .AddField("Total Points", $"{data.Points}", true)
                    .AddField("Streak", $"{data.Streak} days", true)
                    .WithCurrentTimestamp()
                    .Build();

                    await msg.Channel.SendMessageAsync(embed: embed);
                }

                if (msg.Content == "!leaderboard")
                {
                    var top = DailyStore.Data
                        .OrderByDescending(x => x.Value.Points)
                        .Take(10)
                        .ToList();

                    if (top.Count == 0)
                    {
                        await msg.Channel.SendMessageAsync("No data yet.");
                        return;
                    }

                    var desc = "";

                    for (int i = 0; i < top.Count; i++)
                    {
                        var userId = top[i].Key;
                        var points = top[i].Value.Points;

                        var user = await ResolveGuildUserAsync(guildChannel.Guild, userId);
                        var name = user?.Username ?? $"User {userId}";

                        desc += $"**#{i + 1}** {name} — `{points} pts`\n";
                    }

                    var embed = new EmbedBuilder()
                        .WithTitle($"{Emojis.interesting}Leaderboard")
                        .WithColor(Color.Blue)
                        .WithDescription(desc)
                        .WithCurrentTimestamp()
                        .Build();

                    await msg.Channel.SendMessageAsync(embed: embed);
                }
    }

    static long GetTotalRamMB()
    {
        if (File.Exists("/proc/meminfo"))
        {
            foreach (var line in File.ReadAllLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:"))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (long.TryParse(parts[1], out long kb))
                        return kb / 1024;
                }
            }
        }

        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
    }
}
