using Bale.Core.Connection;

namespace Bale.Xbox.Services;

#if DEBUG_SERVER
/// <summary>The debug server's sign-in previews (disposable fixtures, no network or storage): which screen to show.</summary>
internal enum ConnectionScreen { Server, Login, Expired, Password, HttpWarning, Discovery }
internal sealed record ConnectionNavigation(ConnectionPreview Preview, ConnectionScreen Screen = ConnectionScreen.Server);
#endif

/// <summary>Change Account's opener: the server whose "Add an account" takes focus, and in debug-server builds a disposable
/// preview.</summary>
internal sealed record AccountsNavigation(
#if DEBUG_SERVER
    ConnectionPreview? Preview = null,
#endif
    string? AddAccountOn = null);
