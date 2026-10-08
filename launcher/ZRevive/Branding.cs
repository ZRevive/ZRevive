using System.Linq;
using System.Reflection;

namespace ZRevive;

/// <summary>
/// Build-time values that are deployment configuration rather than source.
///
/// <para>They are injected by MSBuild as <see cref="AssemblyMetadataAttribute"/> entries, so the launcher we
/// publish carries them while the values themselves stay out of the public repository. Build with
/// <c>-p:DiscordAppId=...</c>, or put the value in <c>launcher\ZRevive\Local.props</c>, which the project
/// imports when it exists and <c>.gitignore</c> keeps out of git.</para>
///
/// <para>A missing value is normal, not an error: a build from a clean clone simply runs without the feature
/// (<see cref="DiscordPresence"/> never starts its loop on a blank id). Nothing here is a secret - a Discord
/// <i>application id</i> is public by design, broadcast by every client that sets a presence. The Discord
/// <i>bot token</i> is the secret, it lives in the server's own config, and it never reaches the launcher.</para>
/// </summary>
internal static class Branding
{
    /// <summary>The Discord application id presence is published under; "" = no presence.</summary>
    public static readonly string DiscordAppId = Value("DiscordAppId");

    private static string Value(string key)
        => typeof(Branding).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? "";
}
