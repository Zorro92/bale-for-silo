// Developer tool: exercises Bale.Core against a real Silo server from Linux, no Xbox needed.
//
//   dotnet run --project tools/Bale.Cli -- capability <server>
//   dotnet run --project tools/Bale.Cli -- login <server>
//   dotnet run --project tools/Bale.Cli -- pair-test <server>   (opens a code, then cancels it)

using Bale.Core;
using Bale.Core.Auth;
using Bale.Core.Connection;

if (args.Length < 2 || !ServerAddress.TryParse(args[1], out var server))
{
    Console.Error.WriteLine("usage: bale-cli <capability|login|pair-test> <server>");
    return 2;
}

var identity = new ClientIdentity(
    DeviceId: "bale-cli-" + Environment.MachineName.ToLowerInvariant(),
    DeviceName: $"Bale CLI ({Environment.MachineName})",
    Platform: "linux",
    ClientName: "Bale CLI",
    ClientVersion: "0.1.0",
    ClientChannel: "dev",
    ClientFamily: "desktop");

using var client = new SiloClient(server, identity);
var flow = client.DeviceLogin;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (args[0])
{
    case "capability":
        var capability = await flow.GetCapabilityAsync(cts.Token);
        Console.WriteLine($"device sign-in: {capability?.State}, cancel: {capability?.Cancel}, opened signal: {capability?.OpenedSignal}");
        return 0;

    case "pair-test":
        var probe = await flow.StartAsync(identity.DeviceName, identity.Platform, cts.Token);
        Console.WriteLine($"server issued code {probe.DisplayCode} for {probe.ActivateText}, expires {probe.ExpiresAt:u}");
        await flow.CancelAsync(probe);
        Console.WriteLine("canceled it again");
        return 0;

    case "login":
        var ticket = await flow.StartAsync(identity.DeviceName, identity.Platform, cts.Token);
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

            client.SignIn(result.Tokens!);
            var me = await client.Api.Api.V2.Account.Me.GetAsync(cancellationToken: cts.Token);
            Console.WriteLine($"Signed in as {me?.Username} ({me?.Role})");
            await client.SignOutAsync();
            Console.WriteLine("Signed out again (the CLI doesn't keep sessions).");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await flow.CancelAsync(ticket);
            Console.WriteLine("Canceled.");
            return 1;
        }

    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        return 2;
}
