using System.Text;
using Heh8080.Cp500Runner;

// Screen rows hold Unicode glyphs and are joined by '\n', so every output line uses UTF-8 and '\n' on all hosts.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.Out.NewLine = "\n";
Console.Error.NewLine = "\n";
return (int)RunnerCommand.Execute(args, Console.Out, Console.Error);
