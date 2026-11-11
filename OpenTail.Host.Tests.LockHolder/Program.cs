// Holds the skill store's lock file exclusively, as a second Host process would, and says so on standard output.
// The hold lasts until standard input is closed, which is how the test ends it. Argument: the path of the lock file.
using var held = new FileStream(args[0], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
Console.WriteLine("held");
Console.In.ReadToEnd();
