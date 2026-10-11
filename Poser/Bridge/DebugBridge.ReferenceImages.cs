#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private string ReferenceImages(Dictionary<string, string> query)
    {
        var session = (global::Poser.UI.ReferenceImageSession)_services.GetService(
            typeof(global::Poser.UI.ReferenceImageSession))!;
        object? command = null;
        if (query.TryGetValue("add", out var path))
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                return Json(new { accepted = false,
                    detail = "add must name an existing absolute image path." });
            string extension = Path.GetExtension(path);
            if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                return Json(new { accepted = false, detail = "Only PNG and JPEG reference images are supported." });
            var added = session.Add(path);
            command = new { accepted = true, applied = true, id = added.Id };
        }
        else if (query.TryGetValue("remove", out var rawId))
        {
            if (!int.TryParse(rawId, out int id))
                return Json(new { accepted = false, detail = "remove must be an integer reference-image id." });
            var image = session.Instances.SingleOrDefault(item => item.Id == id);
            if (image is null)
                return Json(new { accepted = false, detail = "No current reference image has that exact id." });
            session.Close(image);
            command = new { accepted = true, applied = true, id };
        }

        var windows = (global::Poser.UI.Composition.UiWindowSet)_services.GetService(
            typeof(global::Poser.UI.Composition.UiWindowSet))!;
        var rows = session.Instances.Select(image =>
        {
            string windowName = global::Poser.UI.ReferenceImageWindow.WindowNameFor(image);
            var window = windows.System.Windows.SingleOrDefault(item => item.WindowName == windowName);
            var native = ImGuiP.FindWindowByName(windowName);
            return new
            {
                id = image.Id,
                filePath = image.Entry.FilePath,
                hidden = global::Poser.UI.ReferenceImageSession.IsHidden(image),
                loaded = image.Handle != nint.Zero,
                loading = image.Loading,
                image.Failure,
                pixels = new { x = image.Pixels.X, y = image.Pixels.Y },
                logicalPlacement = new
                {
                    x = image.Entry.X, y = image.Entry.Y,
                    width = image.Entry.Width, height = image.Entry.Height,
                },
                windowName,
                windowOpen = window?.IsOpen == true,
                windowPublished = !native.IsNull,
                windowPositionPixels = native.IsNull ? null : new { x = native.Pos.X, y = native.Pos.Y },
                windowSizePixels = native.IsNull ? null : new { x = native.Size.X, y = native.Size.Y },
                globalScale = ImGuiHelpers.GlobalScale,
            };
        }).ToArray();
        return Json(new { command, rows });
    }
}
#endif
