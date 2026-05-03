namespace Poser.UI;

public enum Display
{
    /// <summary>Default block layout (vertical flow for children).</summary>
    Block,
    /// <summary>Element doesn't render and doesn't reserve space (CSS display: none).</summary>
    None,
    /// <summary>Flex layout — pair with FlexDirection. Set automatically when FlexDirection != null.</summary>
    Flex,
    /// <summary>Grid layout — pair with GridTemplateColumns. Set automatically when GridTemplateColumns is set.</summary>
    Grid,
}
