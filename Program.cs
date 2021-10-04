using System;
using Unit = System.ValueTuple;

namespace Zio
{
    class Program
    {
        static void Main(string[] args)
        {
            ZIOApp<(int, int)> app = new ZipPar();
            app.Main(args);
        }
    }
}
