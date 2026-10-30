namespace AliceVoice.Core.Chat;

/// <summary>
/// Resolves the API_SERVER_KEY for the Hermes API server. Checks the process
/// environment first, then the hermes .env file (where the key actually lives),
/// so the app works when launched fresh without the key in its environment.
/// </summary>
public static class ApiKeyResolver
{
    public static string Resolve()
    {
        // 1. Process environment.
        var env = Environment.GetEnvironmentVariable("API_SERVER_KEY");
        if (!string.IsNullOrEmpty(env)) return env;

        // 2. Hermes .env files (default profile, then named profiles).
        // Never log the key value itself.
        var hermes = new Config.AliceVoiceOptions().HermesHome;
        var candidates = new[]
        {
            Path.Combine(hermes, ".env"),
            Path.Combine(hermes, "profiles", "alice_voice", ".env"),
            Path.Combine(hermes, "profiles", "default", ".env")
        };

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("API_SERVER_KEY=", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = line.Substring("API_SERVER_KEY=".Length).Trim().Trim('"', '\'');
                        if (!string.IsNullOrEmpty(val)) return val;
                    }
                }
            }
            catch { /* ignore unreadable files */ }
        }

        return "";
    }
}
