using System.Diagnostics;
using System.Text;

namespace Meitou.Launcher;

/// <summary>
/// Builds direct process arguments without passing user input through a shell.
/// </summary>
static class GameArguments
{
    /// <summary>
    /// Options incompatible with the dedicated Connect action, matching the game's Vulkan online entry contract.
    /// </summary>
    static readonly HashSet<string> LocalOptions = ["--connect", "--load", "--continue", "--new-game", "--no-population", "--duel",
        "--sandbox", "--battle", "--terrain-sandbox", "--ui-showcase", "--title-preview", "--play-bench", "--connection-check"];

    /// <summary>
    /// Adds user options followed by explicit renderer, connection and save selections.
    /// </summary>
    public static void Add(ProcessStartInfo start, string arguments, string? renderer, string? save, string? server)
    {
        if (arguments.Count(c => c == '"') % 2 != 0) throw new ArgumentException("Launch arguments contain an unclosed double quote.");

        var parsed = Split(arguments).ToArray();
        if (server is not null)
        {
            string? conflict = parsed.FirstOrDefault(LocalOptions.Contains);
            if (conflict is not null) throw new ArgumentException($"Remove {conflict} from Launch arguments before using Connect.");
            if (renderer == "metal") throw new ArgumentException("Online play requires Vulkan. Select Vulkan in Settings.");
        }

        foreach (string argument in parsed) start.ArgumentList.Add(argument);
        if (renderer is not null || server is not null)
        {
            start.ArgumentList.Add("--renderer");
            start.ArgumentList.Add(server is null ? renderer! : "vulkan");
        }
        if (server is not null)
        {
            start.ArgumentList.Add("--connect");
            start.ArgumentList.Add(server);
        }
        if (save is not null)
        {
            start.ArgumentList.Add("--load");
            start.ArgumentList.Add(save);
        }
    }

    /// <summary>
    /// Splits space-separated arguments with double-quoted values, including unfinished input while editing settings.
    /// </summary>
    public static IEnumerable<string> Split(string text)
    {
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) yield return current.ToString();

                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any) yield return current.ToString();
    }
}
