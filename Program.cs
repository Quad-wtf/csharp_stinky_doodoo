using Discord;
using Discord.WebSocket;
using Discord.Rest;
using System.Diagnostics;
using System.Text.Json;

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
}

class Program
{
    private DiscordSocketClient? _client;

    private static readonly DateTime _startTime = DateTime.UtcNow;

    private static readonly string[] _commands = ["!ping", "!ban", "!kick", "!8ball", "!coinflip", "!urban", "!serverinfo", "!userinfo", "!avatar"];

    static async Task Main() => await new Program().RunAsync();

    async Task RunAsync()
    {
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

    async Task OnMessageReceived(SocketMessage msg)
    {
        if (msg.Author.IsBot) return;
        if (msg.Channel is not SocketGuildChannel guildChannel) return;
        if (msg.Author is not SocketGuildUser caller) return;

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
                .AddField("Gateway Latency", $"`{gatewayLatency}ms`",              inline: true)
                .AddField("Message Latency", $"`{messageLatency}ms`",              inline: true)
                .AddField("RAM Usage",       $"`{ramUsedMB} MB / {totalRamMB} MB`",inline: true)
                .AddField("Uptime",          $"`{uptimeStr}`",                     inline: true)
                .AddField("Commands",        $"`{_commands.Length}`",              inline: true)
                .AddField("Discord.Net",     $"`v{discordNetVersion}`",            inline: true)
                .AddField(".NET Runtime",    $"`v{dotnetVersion}`",                inline: true)
                .Build();

            await sent.ModifyAsync(m =>
            {
                m.Content = string.Empty;
                m.Embed = embed;
            });
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
                .WithTitle($"🏠 {guild.Name}")
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

            // Get the user's top colored role if available
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
