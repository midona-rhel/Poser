namespace Poser.UI;

/// <summary>
/// The control families the theme builds a sheet for. Values are indices into
/// the theme's sheet table, so a family is a SHORT on the element record
/// rather than a reference — and a variant is a whole sheet built with a
/// <c>with</c>-expression at theme construction, never a byte a painter
/// switches on.
/// </summary>
public enum SheetFamily : short
{
    None = 0,

    // Containers.
    Row,
    Column,
    Stack,

    // Text roles.
    Text,
    Caption,
    Readout,
    FormLabel,
    FormValue,
    Hint,
    PageHint,

    // Buttons: comfortable and workspace density, three tones each.
    Button,
    ButtonPrimary,
    ButtonDanger,
    ButtonDense,
    ButtonDensePrimary,
    ButtonDenseDanger,

    // Form leaves.
    Switch,
    Slider,
    ProgressTrack,
    ColorWell,

    // Page structure.
    PageOuter,
    PageColumn,
    PageEmptyBand,
    PageStatusBand,
    RowOverlay,
    ActionGroup,
    ActionGroupFill,
    ControlCell,
    ValueCell,
    ColorWellTrack,
    FormRow,
    SectionHeader,
    SectionRule,

    // Floating surfaces.
    DropdownTrigger,
    DropdownRow,
    PickerRow,
    PickerRule,
    PickerEmptyRow,
    PickerCheckSlot,
    PickerCheckBox,

    // Window chassis: the action bar, its title, the 1px rule it wears, and
    // the icon-sized action beside it.
    ActionBarBox,
    ActionBarRow,
    ActionBarTitle,
    BarRule,
    IconAction,

    // Settings navigation rail.
    NavRail,
    NavRow,
    NavIconSlot,
    NavLabel,

    // Settings form controls.
    SegmentPill,
    SegmentTab,
    SwatchPalette,
    SwatchBox,

    Count,
}

/// <summary>
/// A small index into the theme's sheet table. A default ref names no sheet,
/// which is what makes <c>default</c> mean "renderer defaults only".
/// </summary>
public readonly record struct SheetRef(short Index)
{
    public static SheetRef None => default;

    public static implicit operator SheetRef(SheetFamily family) =>
        new((short)family);

    internal bool IsNone => Index <= 0;
}
