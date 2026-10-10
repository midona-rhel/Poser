using Poser.Application.Transforms;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

public static class IntegrationWrites
{
    /// <summary>An appearance write as a value write. A Glamourer hold (another
    /// plugin's lock, or Poser's own imported character file) is released by
    /// the user, so that refusal is transient; every other refusal is permanent.</summary>
    public static ValueWriteResult ToValueWrite(this IntegrationResult result) =>
        new(result.Success, result.Detail)
        {
            Transient = !result.Success
                && result.AppearanceRefusal is GlamourerAccessKind.ForeignHeld or GlamourerAccessKind.PoserHeld,
        };
}
