using System.Windows.Forms;

namespace Crystarium.Capture;

/// <summary>
/// The harness's hidden host window. A plain <c>Form.Show()</c> ACTIVATES —
/// it steals foreground focus from whatever the user is doing every time a
/// capture or behavior suite runs, which with per-state processes is dozens
/// of thefts per verification pass. This one never can:
/// <see cref="ShowWithoutActivation"/> covers the <c>Show()</c> path and
/// <c>WS_EX_NOACTIVATE</c> covers everything else the shell might try.
/// </summary>
internal sealed class CaptureForm : Form
{
    private const int WsExNoActivate = 0x08000000;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams p = base.CreateParams;
            p.ExStyle |= WsExNoActivate;
            return p;
        }
    }
}
