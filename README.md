# csharp_stinky_doodoo

## Setup

1. Copy `config.example.json` → `config.json` and fill in your bot token and Lavalink password
2. Copy `application.example.yml` → `application.yml` and fill in your Lavalink password and Spotify credentials
3. Never commit either of these files — they are gitignored

## Environment Variables (for deployment e.g. Railway)

| Variable | Description |
|---|---|
| `DISCORD_TOKEN` | Your bot token from Discord Developer Portal |
| `LAVALINK_PASSWORD` | Password you set in application.yml |
