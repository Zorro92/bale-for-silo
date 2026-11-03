using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Connection;

/// <summary>
/// shared/src/commonMain/kotlin/org/siloserver/silo/network/apiv2/PlaybackV2Api.kt:
/// singleAttempt mutations leave uncertain outcomes to their owner, including after a 401.
/// </summary>
internal sealed class SingleAttemptRequestOption : IRequestOption;
