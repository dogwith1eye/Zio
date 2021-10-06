using System;
using Unit = System.ValueTuple;
using static LaYumba.Functional.F;
using System.Threading;

namespace Zio
{
    record Person(string name, int age)
    {
        public static Person Peter = new Person("Peter", 88);
    }

    interface ZIOApp<T> 
    {
        ZIO<T> Run();

        void Main(string[] args)
        {
            this.Run().Run(result => 
            {
                Console.WriteLine($"The result was ${result}");
                return Unit();
            });
            Thread.Sleep(300);
        }
    }

    class SucceedNow : ZIOApp<Person>
    {
        ZIO<Person> PeterZIO = ZIO.SucceedNow(Person.Peter);
        public ZIO<Person> Run() => PeterZIO;
    }

    class SucceedNowUhOh : ZIOApp<int>
    {
        static Func<Unit> temp = () => 
        {
            Console.WriteLine("Howdy");
            return Unit();
        };
        // we get eagerly evaluated
        ZIO<Unit> HowdyZIO = ZIO.SucceedNow(temp());
        public ZIO<int> Run() => ZIO.SucceedNow(1);
    }

    class Succeed : ZIOApp<int>
    {
        // we not longer get eagerly evaluated
        // as we have trapped the computation in
        // a function
        ZIO<Unit> HowdyZIO = ZIO.Succeed(() => 
        {
            Console.WriteLine("Howdy");
            return Unit();
        });
        public ZIO<int> Run() => ZIO.SucceedNow(1);
    }

    class SucceedAgain : ZIOApp<Unit>
    {
        ZIO<Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        public ZIO<Unit> Run() => WriteLine("fancy");
    }

    class Zip : ZIOApp<(int, string)>
    {
        ZIO<(int, string)> ZippedZIO = 
            ZIO.Succeed(() => 8).Zip(ZIO.Succeed(() => "LO"));

        public ZIO<(int, string)> Run() => ZippedZIO;
    }

    class Map : ZIOApp<Person>
    {
        static ZIO<(int, string)> ZippedZIO = 
            ZIO.Succeed(() => 8).Zip(ZIO.Succeed(() => "LO"));
        
        static ZIO<(string, int)> MappedZIO = 
            ZippedZIO.Map<(string, int)>((z) => (z.Item2, z.Item1));

        static ZIO<Person> PersonZIO = 
            ZippedZIO.Map<Person>((z) => new Person(z.Item2, z.Item1));

        public ZIO<Person> Run() => PersonZIO;
    }

    class FlatMap : ZIOApp<Unit>
    {
        static ZIO<(int, string)> ZippedZIO = 
            ZIO.Succeed(() => 8).Zip(ZIO.Succeed(() => "LO"));
        
        static ZIO<Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });
        
        static ZIO<Unit> MappedZIO = 
            ZippedZIO.FlatMap<Unit>((z) => WriteLine($"My beautiful tuple {z}"));

        public ZIO<Unit> Run() => MappedZIO;
    }

    class LinqComprehension : ZIOApp<string>
    {
        static ZIO<(int, string)> ZippedZIO = 
            ZIO.Succeed(() => 8).Zip(ZIO.Succeed(() => "LO"));
        
        static ZIO<Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });
        
        static ZIO<string> MappedZIO = 
            from z in ZippedZIO
            from _ in WriteLine($"My beautiful tuple {z}")
            select "Nice";

        static ZIO<string> MappedZIORaw = 
            ZippedZIO.FlatMap(z => 
                WriteLine($"My beautiful tuple {z}")
                    .Map((_) => "Nice"));

        static ZIO<string> MappedZIORawAs = 
            ZippedZIO.FlatMap(z => 
                WriteLine($"My beautiful tuple {z}")
                    .As("Nice"));

        public ZIO<string> Run() => MappedZIORawAs;
    }

    class Async : ZIOApp<int>
    {
        // spill our guts
        static ZIO<int> AsyncZIO = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine("Async Beginneth!");
                Thread.Sleep(200);
                return complete(new Random().Next(999));
            });

        public ZIO<int> Run() => AsyncZIO;
    }

    class Forked : ZIOApp<string>
    {
        static ZIO<Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<int> AsyncZIO = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine("Async Beginneth!");
                Thread.Sleep(200);
                return complete(new Random().Next(999));
            });

        static ZIO<string> ForkedZIO =
            from fiber1 in AsyncZIO.Fork()
            from fiber2 in AsyncZIO.Fork()
            from _ in  WriteLine("Nice")
            from i1 in fiber1.Join()
            from i2 in fiber2.Join()
            select $"My beautiful ints {i1} {i2}";

        public ZIO<string> Run() => ForkedZIO;
    }

    class ZipPar : ZIOApp<(int, int)>
    {
        static ZIO<Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<int> AsyncZIO = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine("Async Beginneth!");
                Thread.Sleep(200);
                return complete(new Random().Next(999));
            });

        public ZIO<(int, int)> Run() => AsyncZIO.ZipPar(AsyncZIO);
    }

    class StackSafety : ZIOApp<Unit>
    {
        static ZIO<Unit> MyProgram = 
            ZIO.Succeed(() => 
            {
                Console.WriteLine("Howdy!");
                return Unit();
            }).Repeat(10000);

        public ZIO<Unit> Run() => MyProgram;
    }

    class AsyncStackSafety : ZIOApp<Unit>
    {
        static ZIO<Unit> MyProgram = 
            ZIO.Async<Unit>(complete => 
            {
                Console.WriteLine("Howdy!");
                return complete(Unit());
            }).Repeat(10000);

        public ZIO<Unit> Run() => MyProgram;
    }
}