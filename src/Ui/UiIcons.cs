namespace Frd;

/// <summary>Icons FRD adds to Glasspane's built-in set. Registered once at start-up so both the controller and the host window can use them.</summary>
static class UiIcons
{
    public static void Register()
    {
        UiPaint.RegisterIcon("cursor", "M6 3 L19 12 L12.5 13.5 L9.5 20 Z");
        UiPaint.RegisterIcon("rect", "M5 6 H19 V18 H5 Z");
        UiPaint.RegisterIcon("text", "M6 7 V5 H18 V7 M12 5 V19 M9 19 H15");
        UiPaint.RegisterIcon("camera", "M4 8 H8 L10 5.5 H14 L16 8 H20 V19 H4 Z M10 11.5 H14 V15.5 H10 Z");
        UiPaint.RegisterIcon("pen", "M4 20 L5 15.5 L16.5 4 L20 7.5 L8.5 19 Z M14 6.5 L17.5 10");
    }
}
