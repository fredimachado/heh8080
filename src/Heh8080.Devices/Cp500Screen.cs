using System.Text;

namespace Heh8080.Devices;

/// <summary>
/// Text form of the 64 by 16 video plane.
/// Bytes 0x20 through 0x7E are kept. Every other byte becomes a space.
/// Each row is trimmed on the right. Rows are joined with '\n' and there is no extra trailing newline.
/// 32-column mode is ignored: VRAM is still 64 columns, and odd cells still count.
/// </summary>
public static class Cp500Screen
{
    public static string ToText(Cp500VideoDevice video)
    {
        ArgumentNullException.ThrowIfNull(video);

        var rows = new string[Cp500VideoDevice.Rows];
        for (int row = 0; row < rows.Length; row++)
        {
            var text = new StringBuilder(Cp500VideoDevice.Columns);
            for (int column = 0; column < Cp500VideoDevice.Columns; column++)
            {
                byte value = video.ReadCharacter(column, row);
                text.Append(value is >= 0x20 and <= 0x7E ? (char)value : ' ');
            }

            rows[row] = text.ToString().TrimEnd();
        }

        return string.Join('\n', rows);
    }
}
