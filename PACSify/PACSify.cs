using System;
using FellowOakDicom.Network;

namespace PACSify;

static class Program
{
    public static string Exe;
    static void Main(string[] args)
    {
        Exe = args[0];

        using var server = DicomServerFactory.Create<StoreScp>(1104);
        Console.WriteLine("Awaiting data...");
        Console.ReadLine();
    }
}