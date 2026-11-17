using System.Text;
using NashmiKeys.Core.Diagnostics;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // Console encoding is cosmetic.
}

Console.WriteLine("NashmiKeys test runner (plain asserts, no test framework)");
Console.WriteLine();
var exitCode = SelfTestSuite.Run(Console.Out);
return exitCode;
