using System;
using Unit = System.ValueTuple;

namespace Zio
{
    class Program
    {
        static void Main(string[] args)
        {
            ZIOApp<Unit> app = new AsyncStackSafetyFork();
            app.Main(args);
        }
    }
}
