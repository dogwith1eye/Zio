using System;
using Unit = System.ValueTuple;

namespace Zio
{
    class Program
    {
        static void Main(string[] args)
        {
            ZIOApp<string> app = new ForkedMain();
            app.Main(args);
        }
    }
}
