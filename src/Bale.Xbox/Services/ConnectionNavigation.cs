using Bale.Core.Connection;

namespace Bale.Xbox.Services;

internal enum ConnectionScreen { Server, Login, Expired, Password, HttpWarning, Discovery }
internal sealed record ConnectionNavigation(ConnectionPreview Preview, ConnectionScreen Screen = ConnectionScreen.Server);
