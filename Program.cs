using System;
using Unit = System.ValueTuple;

namespace Zio
{
    class Program
    {
        static void Main(string[] args)
        {
            var app = ZIOApp.Upcast(new Environment());
            app.Main(args);
        }
    }
}
