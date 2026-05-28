using Discord;
using Discord.WebSocket;
using System.Diagnostics;
using System.Text.Json;
using Lavalink4NET;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Extensions;
using Lavalink4NET.Rest.Entities.Tracks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;


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

    private static readonly string[] _commands = [
        "!ping", "!ban", "!kick", "!help", "!8ball", "!coinflip",
        "!urban", "!serverinfo", "!userinfo", "!avatar", "!afk",
        "!daily", "!leaderboard", "!announcement", "!maintenance",
        "!join", "!leave", "!bye", "!volume", "!play",
        "!pause", "!resume", "!queue", "!next"
    ];

    private IAudioService? _audioService;
    private static readonly Dictionary<ulong, int> _volumes = new(); // guildId -> volume (0-100)

    static async Task Main() => await new Program().RunAsync();

    static string BuildVolumeBar(int volume)
    {
        const int bars = 10;
        int filled = (int)Math.Round(volume / 100.0 * bars);
        return string.Concat(Enumerable.Repeat("█", filled)) +
               string.Concat(Enumerable.Repeat("░", bars - filled));
    }

    static string ExtractYouTubeId(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url,
            @"(?:youtube\.com\/watch\?v=|youtu\.be\/)([^&\s]+)");
        return match.Success ? match.Groups[1].Value : "";
    }


    async Task RunAsync()
    {
        DailyStore.Load();

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        var lavalinkPassword = Environment.GetEnvironmentVariable("LAVALINK_PASSWORD") ?? "";

        if (token == null)
        {
            var json = File.ReadAllText("config.json");
            var configFile = JsonDocument.Parse(json);
            token = configFile.RootElement.GetProperty("token").GetString();
            lavalinkPassword = configFile.RootElement.GetProperty("lavalink_password").GetString() ?? "";
        }

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
                        | GatewayIntents.GuildMessages
                        | GatewayIntents.GuildVoiceStates
                        | GatewayIntents.MessageContent,
            DefaultRetryMode = RetryMode.AlwaysRetry,
            MessageCacheSize = 100
        };

        _client = new DiscordSocketClient(config);

        // Set up Lavalink BEFORE starting the client
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_client);
                services.AddLavalink();
                services.ConfigureLavalink(x =>
                {
                    x.BaseAddress = new Uri("http://127.0.0.1:2333");
                    x.Passphrase  = lavalinkPassword;
                });
            })
            .Build();

        _audioService = host.Services.GetRequiredService<IAudioService>();

        _ = host.StartAsync();

        _client.Log += msg => { Console.WriteLine(msg); return Task.CompletedTask; };
        _client.MessageReceived += msg =>
        {
            _ = Task.Run(() => OnMessageReceived(msg));
            return Task.CompletedTask;
        };

        _client.Ready += () =>
        {
            Console.WriteLine("\nBot is ready!\n");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _audioService!.WaitForReadyAsync(CancellationToken.None);
                    Console.WriteLine("Lavalink connected!");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Lavalink connection failed: {ex.Message}");
                }
            });
            return Task.CompletedTask;
        };

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(Timeout.Infinite); // this goes LAST, keeping the process alive
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

            // Voice stats
            int activeVoiceConnections = 0;
            int tracksInQueues = 0;
            bool lavalinkConnected = false;

            if (_audioService != null)
            {
                try
                {
                    var players = _audioService.Players.Players.OfType<QueuedLavalinkPlayer>();
                    activeVoiceConnections = players.Count();
                    tracksInQueues = players.Sum(p => p.Queue.Count + (p.CurrentTrack != null ? 1 : 0));
                    lavalinkConnected = true;
                }
                catch { lavalinkConnected = false; }
            }

            // Daily system stats
            int totalDailyUsers = DailyStore.Data.Count;
            int totalPoints = DailyStore.Data.Values.Sum(d => d.Points);
            int topStreak = DailyStore.Data.Values.Any()
                ? DailyStore.Data.Values.Max(d => d.Streak)
                : 0;

            var embed = new EmbedBuilder()
                .WithTitle($"{Emojis.coolguy} Pong!")
                .WithColor(gatewayLatency switch
                {
                    < 100 => Color.Green,
                    < 250 => Color.Gold,
                    _     => Color.Red
                })
                .WithCurrentTimestamp()
                // Latency
                .AddField("Gateway Latency", $"`{gatewayLatency}ms`",  inline: true)
                .AddField("Message Latency", $"`{messageLatency}ms`",  inline: true)
                .AddField("Uptime",          $"`{uptimeStr}`",          inline: true)
                // System
                .AddField("RAM Usage",    $"`{ramUsedMB} MB / {totalRamMB} MB`", inline: true)
                .AddField("Commands",     $"`{_commands.Length}`",               inline: true)
                .AddField("AFK Users",    $"`{_afkUsers.Count}`",                inline: true)
                // Versions
                .AddField("Discord.Net",  $"`v{discordNetVersion}`",  inline: true)
                .AddField(".NET Runtime", $"`v{dotnetVersion}`",      inline: true)
                .AddField("Lavalink",     lavalinkConnected ? "`Connected ✅`" : "`Disconnected ❌`", inline: true)
                // Voice
                .AddField("Voice Connections", $"`{activeVoiceConnections}`", inline: true)
                .AddField("Tracks Playing",    $"`{tracksInQueues}`",         inline: true)
                .AddField("Volume Overrides",  $"`{_volumes.Count}`",         inline: true)
                // Economy
                .AddField("Daily Users",  $"`{totalDailyUsers}`",  inline: true)
                .AddField("Points Given", $"`{totalPoints}`",       inline: true)
                .AddField("Top Streak",   $"`{topStreak} days`",   inline: true)
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

        if (msg.Content.StartsWith("!announcement "))
        {
            if (!caller.GuildPermissions.ManageGuild)
            {
                await msg.Channel.SendMessageAsync("You don't have permission to send announcements.");
                return;
            }

            // Expect: !announcement #channel-mention-or-id <message>
            var args = msg.Content[14..].Trim();

            ulong targetChannelId = 0;
            string announcementText = "";

            // Try parsing a channel mention: <#1234567890>
            var mentionedChannel = msg.MentionedChannels.FirstOrDefault();
            if (mentionedChannel != null)
            {
                targetChannelId = mentionedChannel.Id;
                // Strip the mention from the front to get the message
                var mentionStr = $"<#{mentionedChannel.Id}>";
                var afterMention = args[(args.IndexOf(mentionStr) + mentionStr.Length)..].Trim();
                announcementText = afterMention;
            }
            else
            {
                // Try a raw channel ID as the first token
                var spaceIdx = args.IndexOf(' ');
                if (spaceIdx > 0 && ulong.TryParse(args[..spaceIdx], out targetChannelId))
                {
                    announcementText = args[(spaceIdx + 1)..].Trim();
                }
            }

            if (targetChannelId == 0 || string.IsNullOrWhiteSpace(announcementText))
            {
                await msg.Channel.SendMessageAsync(
                    "Usage: `!announcement #channel <message>` or `!announcement <channel-id> <message>`");
                return;
            }

            var targetChannel = guildChannel.Guild.GetTextChannel(targetChannelId);
            if (targetChannel == null)
            {
                await msg.Channel.SendMessageAsync("Couldn't find that channel in this server.");
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle($"{Emojis.jawonthefloor} Announcement")
                .WithDescription(announcementText)
                .WithColor(Color.Orange)
                .WithFooter($"Sent by {caller.DisplayName}", caller.GetAvatarUrl() ?? caller.GetDefaultAvatarUrl())
                .WithCurrentTimestamp()
                .Build();

            await targetChannel.SendMessageAsync(embed: embed);

            // Confirm back in the command channel (if different)
            if (targetChannel.Id != msg.Channel.Id)
                await msg.Channel.SendMessageAsync($"Announcement sent to {targetChannel.Mention}!");
        }

        else if (msg.Content.StartsWith("!maintenance "))
        {
            if (!caller.GuildPermissions.ManageGuild)
            {
                await msg.Channel.SendMessageAsync("You don't have permission to schedule maintenance.");
                return;
            }

            var args = msg.Content[13..].Trim();

            // Extract target channel
            var mentionedChannel = msg.MentionedChannels.FirstOrDefault();
            ulong targetChannelId = 0;

            if (mentionedChannel != null)
            {
                targetChannelId = mentionedChannel.Id;
                var mentionStr = $"<#{mentionedChannel.Id}>";
                args = args[(args.IndexOf(mentionStr) + mentionStr.Length)..].Trim();
            }
            else
            {
                // Try raw channel ID as first token
                var spaceIdx = args.IndexOf(' ');
                if (spaceIdx > 0 && ulong.TryParse(args[..spaceIdx], out targetChannelId))
                    args = args[(spaceIdx + 1)..].Trim();
            }

            if (targetChannelId == 0)
            {
                await msg.Channel.SendMessageAsync(
                    "Please specify a channel. Usage: `!maintenance #channel From:dd/mm/yyyy hh:mm To:dd/mm/yyyy hh:mm`");
                return;
            }

            var targetChannel = guildChannel.Guild.GetTextChannel(targetChannelId);
            if (targetChannel == null)
            {
                await msg.Channel.SendMessageAsync("Couldn't find that channel in this server.");
                return;
            }

            // Parse timestamps from whatever remains after stripping the channel
            var fromMatch = System.Text.RegularExpressions.Regex.Match(args,
                @"From:(\d{2}/\d{2}/\d{4})[\s]+(\d{2}[:.]\d{2})");
            var toMatch = System.Text.RegularExpressions.Regex.Match(args,
                @"To:(\d{2}/\d{2}/\d{4})[\s]+(\d{2}[:.]\d{2})");

            if (!fromMatch.Success || !toMatch.Success)
            {
                await msg.Channel.SendMessageAsync(
                    "Usage: `!maintenance #channel From:dd/mm/yyyy hh:mm To:dd/mm/yyyy hh:mm`");
                return;
            }

            var fromStr = $"{fromMatch.Groups[1].Value} {fromMatch.Groups[2].Value.Replace('.', ':')}";
            var toStr   = $"{toMatch.Groups[1].Value} {toMatch.Groups[2].Value.Replace('.', ':')}";

            if (!DateTime.TryParseExact(fromStr, "dd/MM/yyyy HH:mm",
                    null, System.Globalization.DateTimeStyles.None, out var fromTime) ||
                !DateTime.TryParseExact(toStr,   "dd/MM/yyyy HH:mm",
                    null, System.Globalization.DateTimeStyles.None, out var toTime))
            {
                await msg.Channel.SendMessageAsync(
                    "Invalid date format. Use: `From:dd/mm/yyyy hh:mm To:dd/mm/yyyy hh:mm`");
                return;
            }

            if (toTime <= fromTime)
            {
                await msg.Channel.SendMessageAsync("The end time must be after the start time.");
                return;
            }

            var duration = toTime - fromTime;
            string durationStr = duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                : $"{duration.Minutes}m";

            var fromUnix = ((DateTimeOffset)DateTime.SpecifyKind(fromTime, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var toUnix   = ((DateTimeOffset)DateTime.SpecifyKind(toTime,   DateTimeKind.Utc)).ToUnixTimeSeconds();

            var embed = new EmbedBuilder()
                .WithTitle("🔧 Scheduled Maintenance")
                .WithColor(new Color(0xFF6600))
                .WithDescription(
                    "@everyone\n\n" +
                    "The bot will be undergoing scheduled maintenance.\n" +
                    "During this window, all commands will be unavailable.")
                .AddField("Start",    $"<t:{fromUnix}:F>", inline: true)
                .AddField("End",      $"<t:{toUnix}:F>",   inline: true)
                .AddField("Duration", $"`{durationStr}`",   inline: true)
                .WithFooter($"Scheduled by {caller.DisplayName}", caller.GetAvatarUrl() ?? caller.GetDefaultAvatarUrl())
                .WithCurrentTimestamp()
                .Build();

            await targetChannel.SendMessageAsync("@everyone", embed: embed);

            if (targetChannel.Id != msg.Channel.Id)
                await msg.Channel.SendMessageAsync($"Maintenance notice sent to {targetChannel.Mention}!");
        }

        // ── Voice: !join ──────────────────────────────────────────────────────────────
        else if (msg.Content == "!join")
        {
            if (_audioService == null)
            {
                await msg.Channel.SendMessageAsync("Audio service is not ready yet.");
                return;
            }

            var voiceChannel = (caller as IVoiceState)?.VoiceChannel;
            if (voiceChannel == null)
            {
                await msg.Channel.SendMessageAsync("You need to be in a voice channel first.");
                return;
            }

            try
            {
                var result = await _audioService.Players.RetrieveAsync(
                guildChannel.Guild.Id,
                voiceChannel.Id,
                PlayerFactory.Queued,
                Options.Create(new QueuedLavalinkPlayerOptions()),
                new PlayerRetrieveOptions(ChannelBehavior: PlayerChannelBehavior.Join));

                if (!result.IsSuccess)
                {
                    await msg.Channel.SendMessageAsync($"Couldn't connect to voice: {result.Status}");
                    return;
                }

                await msg.Channel.SendMessageAsync($"Joined **{voiceChannel.Name}**!");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"Failed to join: {ex.Message}");
            }
        }

        // ── Voice: !leave / !bye ──────────────────────────────────────────────────────
        else if (msg.Content == "!leave" || msg.Content == "!bye")
        {
            var player = await _audioService!.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildChannel.Guild.Id);
            if (player == null)
            {
                await msg.Channel.SendMessageAsync("I'm not in a voice channel.");
                return;
            }

            var channelName = (guildChannel.Guild as SocketGuild)?.GetVoiceChannel(player.VoiceChannelId)?.Name ?? "voice channel";

            await player.DisconnectAsync();
            _volumes.Remove(guildChannel.Guild.Id);
            await msg.Channel.SendMessageAsync($"Left **{channelName}**. Bye! {Emojis.bnuyinlove}");
        }

        // ── Voice: !volume ────────────────────────────────────────────────────────────
        else if (msg.Content.StartsWith("!volume"))
        {
            var player = await _audioService!.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildChannel.Guild.Id);
            if (player == null)
            {
                await msg.Channel.SendMessageAsync("I'm not in a voice channel.");
                return;
            }

            if (msg.Content.Trim() == "!volume")
            {
                var current = _volumes.TryGetValue(guildChannel.Guild.Id, out var v) ? v : 100;
                await msg.Channel.SendMessageAsync($"Current volume: **{current}%**");
                return;
            }

            var rawVol = msg.Content[8..].Trim();
            if (!int.TryParse(rawVol, out var volume) || volume < 0 || volume > 100)
            {
                await msg.Channel.SendMessageAsync("Volume must be a number between 0 and 100.");
                return;
            }

            await player.SetVolumeAsync(volume / 100f);
            _volumes[guildChannel.Guild.Id] = volume;

            var bar = BuildVolumeBar(volume);
            await msg.Channel.SendMessageAsync($"Volume set to **{volume}%**\n{bar}");
        }

        // ── Voice: !play ──────────────────────────────────────────────────────────────
        else if (msg.Content.StartsWith("!play "))
        {
            var query = msg.Content[6..].Trim();

            if (string.IsNullOrWhiteSpace(query))
            {
                await msg.Channel.SendMessageAsync("Usage: `!play <YouTube/Spotify/SoundCloud URL>`");
                return;
            }

            bool isAllowedSource =
                query.Contains("youtube.com") || query.Contains("youtu.be") ||
                query.Contains("spotify.com")                               ||
                query.Contains("soundcloud.com");

            if (!isAllowedSource)
            {
                await msg.Channel.SendMessageAsync(
                    $"Only YouTube, Spotify, and SoundCloud links are supported. {Emojis.nahnahnah}");
                return;
            }

            if (_audioService == null)
            {
                await msg.Channel.SendMessageAsync("Audio service is not ready yet.");
                return;
            }

            var voiceChannel = (caller as IVoiceState)?.VoiceChannel;
            if (voiceChannel == null)
            {
                await msg.Channel.SendMessageAsync("Join a voice channel first.");
                return;
            }

            var joinResult = await _audioService.Players.RetrieveAsync(
                guildChannel.Guild.Id,
                voiceChannel.Id,
                PlayerFactory.Queued,
                Options.Create(new QueuedLavalinkPlayerOptions()),
                new PlayerRetrieveOptions(ChannelBehavior: PlayerChannelBehavior.Join));

            if (!joinResult.IsSuccess)
            {
                await msg.Channel.SendMessageAsync($"Couldn't connect to voice: {joinResult.Status}");
                return;
            }

            var player = joinResult.Player;
            if (_volumes.TryGetValue(guildChannel.Guild.Id, out var savedVol))
                await player.SetVolumeAsync(savedVol / 100f);

            // Load as a collection to support playlists
            var tracks = await _audioService.Tracks.LoadTracksAsync(query, TrackSearchMode.None);

            if (tracks == null || !tracks.Tracks.Any())
            {
                await msg.Channel.SendMessageAsync($"Couldn't load that track or playlist. Double-check the link. {Emojis.I_DUNNO}");
                return;
            }

            var trackList = tracks.Tracks.ToList();

            // If it's a playlist
            if (trackList.Count > 1)
            {
                var firstTrack = trackList[0];
                bool wasEmpty = player.CurrentTrack == null;

                // Play the first track if nothing is playing, queue the rest
                if (wasEmpty)
                {
                    await player.PlayAsync(firstTrack);
                    foreach (var t in trackList.Skip(1))
                        await player.Queue.AddAsync(new TrackQueueItem(t));
                }
                else
                {
                    foreach (var t in trackList)
                        await player.Queue.AddAsync(new TrackQueueItem(t));
                }

                var playlistName = tracks.Playlist?.Name ?? "Playlist";

                var embed = new EmbedBuilder()
                    .WithTitle(wasEmpty ? "Now Playing Playlist" : "Playlist Added to Queue")
                    .WithColor(wasEmpty ? Color.Green : Color.Blue)
                    .AddField("Playlist", $"**{playlistName}**", inline: true)
                    .AddField("Tracks",   $"`{trackList.Count}`", inline: true)
                    .AddField("First Track", $"[{firstTrack.Title}]({firstTrack.Uri})", inline: false)
                    .WithCurrentTimestamp()
                    .Build();

                await msg.Channel.SendMessageAsync(embed: embed);
                return;
            }

            // Single track
            var track = trackList[0];

            if (player.CurrentTrack != null)
            {
                await player.Queue.AddAsync(new TrackQueueItem(track));

                var queueEmbed = new EmbedBuilder()
                    .WithTitle("Added to Queue")
                    .WithColor(Color.Blue)
                    .AddField("Track",    $"[{track.Title}]({track.Uri})", inline: true)
                    .AddField("Duration", track.IsLiveStream ? "`LIVE`" : $"`{track.Duration:mm\\:ss}`", inline: true)
                    .AddField("Position", $"`#{player.Queue.Count}`", inline: true)
                    .WithThumbnailUrl($"https://img.youtube.com/vi/{ExtractYouTubeId(track.Uri?.ToString() ?? "")}/hqdefault.jpg")
                    .WithCurrentTimestamp()
                    .Build();

                await msg.Channel.SendMessageAsync(embed: queueEmbed);
                return;
            }

            await player.PlayAsync(track);

            var nowEmbed = new EmbedBuilder()
                .WithTitle("Now Playing")
                .WithColor(Color.Green)
                .AddField("Track",    $"[{track.Title}]({track.Uri})", inline: true)
                .AddField("Duration", track.IsLiveStream ? "`LIVE`" : $"`{track.Duration:mm\\:ss}`", inline: true)
                .AddField("Volume",   $"`{(_volumes.TryGetValue(guildChannel.Guild.Id, out var vol) ? vol : 100)}%`", inline: true)
                .WithThumbnailUrl($"https://img.youtube.com/vi/{ExtractYouTubeId(track.Uri?.ToString() ?? "")}/hqdefault.jpg")
                .WithCurrentTimestamp()
                .Build();

            await msg.Channel.SendMessageAsync(embed: nowEmbed);
        }
        // ── Voice: !pause ─────────────────────────────────────────────────────────────
        else if (msg.Content == "!pause")
        {
            var player = await _audioService!.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildChannel.Guild.Id);
            if (player == null)
            {
                await msg.Channel.SendMessageAsync("I'm not in a voice channel.");
                return;
            }

            if (player.State != PlayerState.Playing)
            {
                await msg.Channel.SendMessageAsync("Nothing is playing right now.");
                return;
            }

            await player.PauseAsync();
            await msg.Channel.SendMessageAsync("Paused.");
        }

        // ── Voice: !resume ────────────────────────────────────────────────────────────
        else if (msg.Content == "!resume")
        {
            var player = await _audioService!.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildChannel.Guild.Id);
            if (player == null)
            {
                await msg.Channel.SendMessageAsync("I'm not in a voice channel.");
                return;
            }

            if (player.State != PlayerState.Paused)
            {
                await msg.Channel.SendMessageAsync("The player isn't paused.");
                return;
            }

            await player.ResumeAsync();
            await msg.Channel.SendMessageAsync("Resumed.");
        }

        // ── Voice: !queue ─────────────────────────────────────────────────────────────
        else if (msg.Content == "!queue")
        {
            var player = await _audioService!.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildChannel.Guild.Id);
            if (player == null)
            {
                await msg.Channel.SendMessageAsync("I'm not in a voice channel.");
                return;
            }

            if (player.CurrentTrack == null && player.Queue.Count == 0)
            {
                await msg.Channel.SendMessageAsync("The queue is empty.");
                return;
            }

            var desc = "";

            if (player.CurrentTrack != null)
            {
                var duration = player.CurrentTrack.IsLiveStream
                    ? "`LIVE`"
                    : $"`{player.CurrentTrack.Duration:mm\\:ss}`";
                desc += $"▶️ **Now Playing**\n[{player.CurrentTrack.Title}]({player.CurrentTrack.Uri}) {duration}\n\n";
            }

            if (player.Queue.Count > 0)
            {
                desc += "**Up Next**\n";
                var tracks = player.Queue.ToArray();
                int shown = Math.Min(tracks.Length, 10);

                for (int i = 0; i < shown; i++)
                {
                    var t = tracks[i].Track;
                    if (t == null) continue;
                    var duration = t.IsLiveStream ? "`LIVE`" : $"`{t.Duration:mm\\:ss}`";
                    desc += $"`#{i + 1}` [{t.Title}]({t.Uri}) {duration}\n";
                }

                if (tracks.Length > 10)
                    desc += $"\n*...and {tracks.Length - 10} more*";
            }

            var embed = new EmbedBuilder()
                .WithTitle("Queue")
                .WithColor(Color.Purple)
                .WithDescription(desc)
                .WithFooter($"{player.Queue.Count} track(s) in queue")
                .WithCurrentTimestamp()
                .Build();

            await msg.Channel.SendMessageAsync(embed: embed);
        }

        // ── Voice: !next ──────────────────────────────────────────────────────────────
        else if (msg.Content == "!next")
        {
            var player = await _audioService!.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildChannel.Guild.Id);
            if (player == null)
            {
                await msg.Channel.SendMessageAsync("I'm not in a voice channel.");
                return;
            }

            if (player.CurrentTrack == null)
            {
                await msg.Channel.SendMessageAsync("Nothing is playing right now.");
                return;
            }

            if (player.Queue.Count == 0)
            {
                await msg.Channel.SendMessageAsync("Nothing in the queue to skip to.");
                return;
            }

            await player.SkipAsync();

            if (player.CurrentTrack != null)
            {
                var duration = player.CurrentTrack.IsLiveStream
                    ? "`LIVE`"
                    : $"`{player.CurrentTrack.Duration:mm\\:ss}`";

                var embed = new EmbedBuilder()
                    .WithTitle("Skipped — Now Playing")
                    .WithColor(Color.Green)
                    .AddField("Track",    $"[{player.CurrentTrack.Title}]({player.CurrentTrack.Uri})", inline: true)
                    .AddField("Duration", duration, inline: true)
                    .WithThumbnailUrl($"https://img.youtube.com/vi/{ExtractYouTubeId(player.CurrentTrack.Uri?.ToString() ?? "")}/hqdefault.jpg")
                    .WithCurrentTimestamp()
                    .Build();

                await msg.Channel.SendMessageAsync(embed: embed);
            }
            else
            {
                await msg.Channel.SendMessageAsync("Skipped. Queue is now empty.");
            }
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
