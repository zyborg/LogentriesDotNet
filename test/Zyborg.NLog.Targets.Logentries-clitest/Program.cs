using System;
using System.Diagnostics;

namespace ConsoleApplication
{
    public class Program
    {
        private static readonly NLog.Logger LOG = NLog.LogManager.GetCurrentClassLogger();

        public static void Main(string[] args)
        {
            LOG.Info("Hello World!");
            LOG.Error("Testing errors");

            while (true) {
                Console.WriteLine("Sending...");
                LOG.Warn($"Writing at {DateTime.Now}");
                Console.WriteLine("Ready.");
                Console.ReadKey();

                System.Diagnostics.Trace.WriteLine("HellO");
            }
        }
    }
}
