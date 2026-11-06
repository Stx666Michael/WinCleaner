using System.Globalization;

namespace WinCleaner.Core;

public static class Format
{
    public static string Size(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        var fmt = i == 0 ? "0" : v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00";
        return v.ToString(fmt, CultureInfo.CurrentCulture) + " " + units[i];
    }

    public static string Age(TimeSpan t)
    {
        if (t.TotalDays >= 730) return $"{(int)(t.TotalDays / 365)} years";
        if (t.TotalDays >= 60) return $"{(int)(t.TotalDays / 30)} months";
        if (t.TotalDays >= 2) return $"{(int)t.TotalDays} days";
        if (t.TotalHours >= 2) return $"{(int)t.TotalHours} hours";
        return "less than 2 hours";
    }
}
