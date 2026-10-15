using System.Text;
using Heh8080.Devices;

namespace Heh8080.Tests;

/// <summary>Drives a CP-500 machine running the real ROM the way a user does: time passes, keys go down and up, the screen is read.</summary>
internal sealed class RealRomSession
{
    private const int ShiftRow = 7;
    private const int ShiftColumn = 0;

    public RealRomSession(IModelIIIDiskImageProvider? images = null, Cp500MachineProfile? profile = null)
    {
        Machine = new Cp500Machine(profile ?? Cp500MachineProfile.Original, File.ReadAllBytes(Cp500RomFile.Path!), images);
    }

    public Cp500Machine Machine { get; }

    public void Run(double seconds)
    {
        long targetCycles = (long)(seconds * Machine.Profile.ClockHz);
        for (long cycles = 0; cycles < targetCycles;)
            cycles += Machine.Step();
    }

    /// <summary>Runs until the screen contains the text and returns whether it did within the time limit.</summary>
    public bool RunUntilScreenContains(string text, double maxSeconds)
    {
        const double slice = 0.25;
        for (double elapsed = 0; elapsed < maxSeconds; elapsed += slice)
        {
            if (Screen().Contains(text))
                return true;

            Run(slice);
        }

        return Screen().Contains(text);
    }

    /// <summary>Runs until the CPU is about to execute the address and returns whether it did within the time limit.</summary>
    public bool RunUntilProgramCounter(ushort address, double maxSeconds)
    {
        long limitCycles = (long)(maxSeconds * Machine.Profile.ClockHz);
        for (long cycles = 0; cycles < limitCycles; cycles += Machine.Step())
        {
            if (Machine.Cpu.PC == address)
                return true;
        }

        return false;
    }

    public void Press(int row, int column, bool shift = false, double heldSeconds = 0.1)
    {
        Machine.Keyboard.SetKeyState(ShiftRow, ShiftColumn, shift);
        Machine.Keyboard.SetKeyState(row, column, pressed: true);
        Run(heldSeconds);
        Machine.Keyboard.SetKeyState(row, column, pressed: false);
        Machine.Keyboard.SetKeyState(ShiftRow, ShiftColumn, pressed: false);
        Run(seconds: 0.5);
    }

    public void Type(string keys)
    {
        foreach (char key in keys)
        {
            Cp500KeyStroke stroke = Stroke(key);
            Press(stroke.Row, stroke.Column, stroke.Shift == Cp500ShiftMode.Shifted);
        }
    }

    public string Screen()
    {
        var rows = new List<string>();
        for (int row = 0; row < Cp500VideoDevice.Rows; row++)
        {
            var text = new StringBuilder(Cp500VideoDevice.Columns);
            for (int column = 0; column < Cp500VideoDevice.Columns; column++)
            {
                byte value = Machine.Video.ReadCharacter(column, row);
                text.Append(value is >= 0x20 and < 0x7F ? (char)value : ' ');
            }

            rows.Add(text.ToString().TrimEnd());
        }

        return string.Join('\n', rows);
    }

    private static Cp500KeyStroke Stroke(char key)
    {
        if (key == '\r')
            return Cp500KeyMap.Map(Cp500HostKey.Enter);

        if (!Cp500KeyMap.TryMapCharacter(key, out Cp500KeyStroke stroke))
            throw new ArgumentOutOfRangeException(nameof(key), key, "No CP-500 matrix position.");

        return stroke;
    }
}
