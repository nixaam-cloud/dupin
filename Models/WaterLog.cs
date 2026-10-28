using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsNotch.Models;

public partial class WaterHourStat : ObservableObject
{
    [ObservableProperty]
    private string _hourLabel = "12";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarHeightPercentage))]
    [NotifyPropertyChangedFor(nameof(BarHeight))]
    private int _amountMl;

    public double BarHeightPercentage => Math.Min(1.0, AmountMl / 500.0);
    public double BarHeight => Math.Max(4, Math.Min(48.0, BarHeightPercentage * 48.0));
}
