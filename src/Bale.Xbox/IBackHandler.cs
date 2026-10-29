namespace Bale.Xbox;

/// <summary>A page that wants first say on the controller's B button. Return true if it handled it.</summary>
internal interface IBackHandler
{
    bool OnBack();
}
