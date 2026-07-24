namespace Poser.UI;

/// <summary>
/// Typed class-name constants. Names match entries in <see cref="DefaultStylesheet"/>.
/// Use these to compose class sets without stringly-typed errors:
///   <c>Crystarium.Button("Save", Cls.Btn + Cls.Primary, Save);</c>
///
/// User-defined classes can still be passed as raw strings; <see cref="StyleClassSet"/>
/// has an implicit string conversion.
/// </summary>
public static class Cls
{
    // Layout
    public static readonly StyleClass Row       = new("row");
    public static readonly StyleClass TightRow  = new("tight-row");

    // Text
    public static readonly StyleClass Heading       = new("heading");
    public static readonly StyleClass DisabledText  = new("disabled-text");
    public static readonly StyleClass Label         = new("label");

    // Buttons
    public static readonly StyleClass Btn      = new("btn");
    public static readonly StyleClass Icon     = new("icon");
    public static readonly StyleClass Primary  = new("primary");
    public static readonly StyleClass Danger   = new("danger");
    public static readonly StyleClass Compact  = new("compact");

    // Inputs
    public static readonly StyleClass Checkbox    = new("checkbox");
    public static readonly StyleClass Toggle      = new("toggle");
    public static readonly StyleClass IconToggle  = new("icon-toggle");
    public static readonly StyleClass TextInput   = new("text-input");
    public static readonly StyleClass Scrubber    = new("scrubber");
    public static readonly StyleClass Slider      = new("slider");
    public static readonly StyleClass Dropdown    = new("dropdown");

    // Misc
    public static readonly StyleClass Separator   = new("separator");
    public static readonly StyleClass Tight       = new("tight");

    // Composites
    public static readonly StyleClass Card        = new("card");
    public static readonly StyleClass Panel       = new("panel");
    public static readonly StyleClass Badge       = new("badge");
    public static readonly StyleClass Tooltip     = new("tooltip");
}
