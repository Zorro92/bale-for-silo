using Bale.Core.Detail;

namespace Bale.Xbox;

/// <summary>A Detail selector handoff for the playable leaf, never a series/season container.</summary>
internal sealed record PlayerRequest(ItemDetail Detail, PlaybackSelection Selection, double? OverridePosition = null);
