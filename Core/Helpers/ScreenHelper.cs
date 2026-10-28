using System.Windows;

namespace WindowsNotch.Core.Helpers;

public static class ScreenHelper
{
    public static double PrimaryScreenWidth => SystemParameters.PrimaryScreenWidth;
    public static double PrimaryScreenHeight => SystemParameters.PrimaryScreenHeight;

    public static Point GetTopCenterPosition(double windowWidth, double yOffset = 0)
    {
        double screenWidth = PrimaryScreenWidth;
        double x = (screenWidth - windowWidth) / 2.0;
        return new Point(x, yOffset);
    }
}
