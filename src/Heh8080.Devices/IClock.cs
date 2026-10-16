namespace Heh8080.Devices;

/// <summary>
/// One FreHD clock sample. Bytes are binary, in guest order: seconds, minutes, hours,
/// year since 2000, day, month. Year 12 is 2012. The value is not BCD.
/// </summary>
public readonly record struct HardDiskTime
{
    /// <summary>Ranges: seconds 0-59, minutes 0-59, hours 0-23, day 1-31, month 1-12. Year is any byte.</summary>
    public HardDiskTime(byte seconds, byte minutes, byte hours, byte yearSince2000, byte day, byte month)
    {
        if (seconds > 59)
            throw new ArgumentOutOfRangeException(nameof(seconds));

        if (minutes > 59)
            throw new ArgumentOutOfRangeException(nameof(minutes));

        if (hours > 23)
            throw new ArgumentOutOfRangeException(nameof(hours));

        if (day is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(day));

        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month));

        Seconds = seconds;
        Minutes = minutes;
        Hours = hours;
        YearSince2000 = yearSince2000;
        Day = day;
        Month = month;
    }

    /// <summary>Seconds, 0 through 59.</summary>
    public byte Seconds { get; }

    /// <summary>Minutes, 0 through 59.</summary>
    public byte Minutes { get; }

    /// <summary>Hours, 0 through 23.</summary>
    public byte Hours { get; }

    /// <summary>Years after 2000. 12 is 2012.</summary>
    public byte YearSince2000 { get; }

    /// <summary>Day of the month, 1 through 31. A day the month does not have is still in range.</summary>
    public byte Day { get; }

    /// <summary>Month, 1 through 12.</summary>
    public byte Month { get; }

    /// <summary>Local wall time. Throws when the year does not fit in a byte.</summary>
    public static HardDiskTime FromLocal(DateTime local)
    {
        int year = local.Year - 2000;
        if (year is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(local));

        return new HardDiskTime(
            (byte)local.Second,
            (byte)local.Minute,
            (byte)local.Hour,
            (byte)year,
            (byte)local.Day,
            (byte)local.Month);
    }

    /// <summary>False for a range-legal sample that is not a real day, such as 31 February.</summary>
    public bool TryToLocalDateTime(out DateTime local)
    {
        local = default;
        if (Month is < 1 or > 12 || Day < 1 || Hours > 23 || Minutes > 59 || Seconds > 59)
            return false;

        if (Day > DateTime.DaysInMonth(2000 + YearSince2000, Month))
            return false;

        local = new DateTime(2000 + YearSince2000, Month, Day, Hours, Minutes, Seconds, DateTimeKind.Local);
        return true;
    }
}
