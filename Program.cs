using System;
using Unit = System.ValueTuple;

namespace Zio
{
    class Program
    {
        static void Main(string[] args)
        {
            ZIOApp<int> app = new ConcurrencyUhOh();
            app.Main(args);
        }
    }
}
