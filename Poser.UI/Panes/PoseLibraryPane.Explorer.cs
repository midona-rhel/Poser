using System;
using System.Diagnostics;
using Poser.Library;

namespace Poser.UI;

public sealed partial class PoseLibraryPane
{
    public void OpenLibraryInExplorer()
    {
        try
        {
            var root = _config.Config.Library.ResolveRoot();
            if (!LibraryConfiguration.TryEnsureDirectory(root, out var detail))
            {
                _notices.Failed(detail);
                _library.RequestScan();
                return;
            }
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
            _library.RequestScan();
        }
        catch (Exception ex)
        {
            _notices.Failed("Open in Explorer: " + ex.Message);
        }
    }
}
