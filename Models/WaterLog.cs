using System;

namespace WindowsNotch.Models;

public class WaterHourStat
{
    public string HourLabel { get; set; } = "12";
    public int AmountMl { get; set; }
    public double BarHeightPercentage => Math.Min(1.0, AmountMl / 400.0);
    public double BarHeight => Math.Max(4, Math.Min(50.0, BarHeightPercentage * 50.0));
}
