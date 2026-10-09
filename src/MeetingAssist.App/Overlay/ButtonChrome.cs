using System.Windows;

namespace MeetingAssist.App.Overlay;

/// <summary>
/// The overlay's buttons share one template; this is what lets two of them join into a
/// segmented pair (Ask | Wide) by squaring off the corners where they meet.
/// </summary>
public static class ButtonChrome
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(ButtonChrome), new PropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject element) =>
        (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) =>
        element.SetValue(CornerRadiusProperty, value);
}
