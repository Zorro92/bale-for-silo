using Bale.Core.Connection;

namespace Bale.Xbox.Services;

internal enum ConnectionScreen { Server, Login, Expired, Password, HttpWarning, Discovery }
internal sealed record ConnectionNavigation(ConnectionPreview Preview, ConnectionScreen Screen = ConnectionScreen.Server);

/// <summary>Change Account's opener: a disposable preview, and/or the server whose "Add an account" takes focus.</summary>
internal sealed record AccountsNavigation(ConnectionPreview? Preview = null, string? AddAccountOn = null);
