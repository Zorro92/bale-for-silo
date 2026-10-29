// Developer tool: exercises Bale.Core against a real Silo server from Linux, no Xbox needed.
//
//   bale-cli capability <server>
//   bale-cli pair-test <server>    open a sign-in code, then cancel it
//   bale-cli login <server>        sign in with a code and keep the session (~/.config/bale-cli)
//   bale-cli profiles              list the signed-in account's profiles
//   bale-cli home [--json]         print the home rows Bale would show
//   bale-cli detail <content-id>   print a title's detail page as Bale shows it
//   bale-cli search <query>        print search results and their artwork
//   bale-cli logout
//
// Run with: dotnet run --project tools/Bale.Cli -- <command> ...

using System.Text.Json;
using Bale.Core;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Bale.Core.Detail;
using Bale.Core.Browse;

var identity = new ClientIdentity(
    DeviceId: "bale-cli-" + Environment.MachineName.ToLowerInvariant(),
    DeviceName: $"Bale CLI ({Environment.MachineName})",
    Platform: "linux",
    ClientName: "Bale CLI",
    ClientVersion: "0.1.0",
    ClientChannel: "dev",
    ClientFamily: "desktop");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var command = args.FirstOrDefault() ?? "";
try
{
switch (command)
{
    case "capability" or "pair-test" or "login":
    {
        if (args.Length < 2 || !ServerAddress.TryParse(args[1], out var server))
        {
            return Usage();
        }

        using var client = new SiloClient(server, identity);
        var flow = client.DeviceLogin;

        if (command == "capability")
        {
            var capability = await flow.GetCapabilityAsync(cts.Token);
            Console.WriteLine($"device sign-in: {capability?.State}, cancel: {capability?.Cancel}, opened signal: {capability?.OpenedSignal}");
            return 0;
        }

        var ticket = await flow.StartAsync(identity.DeviceName, identity.Platform, cts.Token);
        if (command == "pair-test")
        {
            Console.WriteLine($"server issued code {ticket.DisplayCode} for {ticket.ActivateText}, expires {ticket.ExpiresAt:u}");
            await flow.CancelAsync(ticket);
            Console.WriteLine("canceled it again");
            return 0;
        }

        Console.WriteLine($"Go to {ticket.ActivateText} and enter {ticket.DisplayCode}");
        Console.WriteLine($"(or open {ticket.VerificationUriComplete})");
        try
        {
            var result = await flow.WaitForApprovalAsync(ticket, () => Console.WriteLine("Link opened, waiting for approval..."), cts.Token);
            if (result.Outcome != DeviceLoginOutcome.Approved)
            {
                Console.WriteLine($"Not signed in: {result.Outcome}");
                return 1;
            }

            client.SignIn(result.Tokens!, result.ProfileId, result.ProfileToken);
            CliSession.Save(new CliSession(ServerAddress.ToBaseUrl(server), client.RefreshToken!, client.ProfileId));
            Console.WriteLine($"Signed in as {client.Account?.Username}, profile {client.ProfileId ?? "(server default)"}. Session saved to {CliSession.Path}.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await flow.CancelAsync(ticket);
            Console.WriteLine("Canceled.");
            return 1;
        }
    }

    case "profiles" or "home" or "logout" or "detail" or "search":
    {
        using var client = await CliSession.ResumeAsync(identity, cts.Token);
        if (client is null)
        {
            Console.Error.WriteLine("Not signed in. Run: bale-cli login <server>");
            return 1;
        }

        if (command == "logout")
        {
            await client.SignOutAsync(cts.Token);
            CliSession.Delete();
            Console.WriteLine("Signed out.");
            return 0;
        }

        if (client.ProfileId is null
            && ProfileChoice.Automatic(await client.GetProfilesAsync(cts.Token), null) is { } automatic)
        {
            client.UseProfile(automatic.Id!);
        }

        if (command == "profiles")
        {
            foreach (var profile in await client.GetProfilesAsync(cts.Token))
            {
                var marks = string.Join(", ", new[] { profile.IsPrimary == true ? "primary" : null, profile.HasPin == true ? "PIN" : null, profile.IsChild == true ? "child" : null }.Where(m => m is not null));
                Console.WriteLine($"{(profile.Id == client.ProfileId ? "*" : " ")} {profile.Name} [{profile.Id}] {marks}");
            }

            return 0;
        }

        if (command == "search")
        {
            if (args.Length < 2)
            {
                return Usage();
            }

            foreach (var card in await client.Browse.SearchAsync(string.Join(' ', args.Skip(1)), ArtworkWidths.Tv1080, cancellationToken: cts.Token))
            {
                Console.WriteLine($"{card.Title} [{card.Type} {card.ContentId}]");
                Console.WriteLine($"  image: {card.ImageUrl?.ToString() ?? "(none)"}");
            }

            return 0;
        }

        if (command == "detail")
        {
            if (args.Length < 2)
            {
                return Usage();
            }

            var detail = await client.Detail.LoadAsync(args[1], cts.Token);
            Console.WriteLine($"{detail.Title}{(detail.Subtitle is null ? "" : " — " + detail.Subtitle)}  [{detail.Type}]");
            Console.WriteLine($"  {string.Join(" · ", detail.SourceTokens)}  {detail.ContentRating}");
            Console.WriteLine($"  {string.Join(" · ", detail.Facts)}  {string.Join(" ", detail.Ratings.Select(r => r.Name + " " + r.Display))}  {string.Join(" ", detail.QualityBadges)}");
            Console.WriteLine($"  {detail.Tagline}");
            Console.WriteLine($"  {detail.CreditLine}");
            Console.WriteLine($"  flags: {detail.Flags}  play: {detail.PlayTargetId} (season {detail.PlaySeasonNumber})");
            Console.WriteLine($"  cast: {string.Join(", ", detail.Cast.Take(5).Select(c => $"{c.Name} ({c.Role})"))}");
            if (detail.IsSeries)
            {
                var seasons = await client.Detail.SeasonsAsync(detail.ContentId, cts.Token);
                Console.WriteLine($"  seasons: {string.Join(", ", seasons.Select(s => $"{s.Title} ({s.EpisodeCount})"))}");
                var season = detail.PlaySeasonNumber ?? seasons.FirstOrDefault()?.Number ?? 1;
                var episodes = await client.Detail.EpisodesAsync(detail.ContentId, season, cts.Token);
                var nextUp = episodes.FirstOrDefault(e => e.ContentId == detail.PlayTargetId);
                Console.WriteLine($"  play button: {PlayLabel.For(detail, nextUp)}");
                foreach (var episode in episodes.Take(3))
                {
                    Console.WriteLine($"    {episode.Caption}  ({episode.CaptionMeta}){(episode.Played ? " (watched)" : "")}");
                }
            }
            else
            {
                Console.WriteLine($"  play button: {PlayLabel.For(detail, null)}");
            }

            return 0;
        }

        var rows = await client.Browse.LoadHomeAsync(ArtworkWidths.Tv1080, cts.Token);
        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        foreach (var row in rows)
        {
            Console.WriteLine($"## {row.Title}  ({row.SectionType}, {row.Shape}{(row.Featured ? ", featured" : "")}, {row.Cards.Count} cards)");
            foreach (var card in row.Cards.Take(5))
            {
                var progress = card.Progress is { } p ? $" {p:P0}" : "";
                Console.WriteLine($"   {card.Title}{(card.Subtitle is null ? "" : " — " + card.Subtitle)}{progress}");
            }
        }

        return 0;
    }

    default:
        return Usage();
}
}
catch (Bale.Core.Api.Models.Problem problem)
{
    Console.Error.WriteLine($"Server said {problem.Status}: {problem.Title}. {problem.Detail}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: bale-cli <capability|pair-test|login> <server> | <profiles|home [--json]|detail <id>|search <query>|logout>");
    return 2;
}

/// <summary>The CLI's saved session. Only for development: the refresh token sits in a 0600 file.</summary>
internal sealed record CliSession(string Server, string RefreshToken, string? ProfileId)
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "bale-cli", "session.json");

    public static void Save(CliSession session)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(session));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static void Delete() => File.Delete(Path);

    public static async Task<SiloClient?> ResumeAsync(ClientIdentity identity, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path) || JsonSerializer.Deserialize<CliSession>(File.ReadAllText(Path)) is not { } saved
            || !ServerAddress.TryParse(saved.Server, out var server))
        {
            return null;
        }

        var client = new SiloClient(server, identity);
        if (!await client.ResumeAsync(saved.RefreshToken, cancellationToken))
        {
            client.Dispose();
            Delete();
            return null;
        }

        if (saved.ProfileId is not null)
        {
            client.UseProfile(saved.ProfileId);
        }

        Save(saved with { RefreshToken = client.RefreshToken! });
        return client;
    }
}
