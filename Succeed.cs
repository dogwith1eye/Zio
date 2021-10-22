using System;
using Unit = System.ValueTuple;
using static LaYumba.Functional.F;
using System.Threading;

namespace Zio
{
    interface ZIOApp<T> 
    {
        ZIO<T> Run();

        void Main(string[] args)
        {
            var result = this.Run().UnsafeRunSync();
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} The result was ${result}");
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Done with Main");
            Thread.Sleep(5000);
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Done with Main Sleep");
        }
    }

    record Person(string name, int age)
    {
        public static Person Peter = new Person("Peter", 88);
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
                Console.WriteLine("Async Start");
                Thread.Sleep(1000);
                complete(new Random().Next(999));
                Console.WriteLine("Async End");
                return Unit();
            });

        public ZIO<int> Run() => AsyncZIO;
    }

    class Async2 : ZIOApp<int>
    {
        // spill our guts
        static ZIO<int> AsyncZIO = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine("Async Start");
                Thread.Sleep(1000);
                complete(new Random().Next(999));
                Console.WriteLine("Async End");
                return Unit();
            });

        public ZIO<int> Run() => 
            from a in AsyncZIO
            from b in AsyncZIO
            select b;
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
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client Start");
                Thread.Sleep(2000);
                complete(new Random().Next(999));
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client End");
                return Unit();
            });

        static ZIO<int> AsyncZIO2 = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client Start");
                Thread.Sleep(3000);
                complete(new Random().Next(999));
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client End");
                return Unit();
            });

        static ZIO<string> ForkedZIO =
            from fiber1 in AsyncZIO.Fork()
            from fiber2 in AsyncZIO.Fork()
            from _ in  WriteLine($"{Thread.CurrentThread.ManagedThreadId} Nice")
            from i1 in fiber1.Join()
            from i2 in fiber2.Join()
            select $"My beautiful ints {i1} {i2}";

        public ZIO<string> Run() => ForkedZIO;
    }

    class ForkedSync : ZIOApp<string>
    {
        static ZIO<Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<int> SyncZIO = 
            ZIO.Succeed<int>(() => 
            {
                Console.WriteLine("Howdy!");
                Thread.Sleep(5000);
                Console.WriteLine("Partner!");
                return new Random().Next(999);
            });

        static ZIO<string> ForkedZIO =
            from fiber1 in SyncZIO.Fork()
            from fiber2 in SyncZIO.Fork()
            from _ in  WriteLine("Nice")
            from i1 in fiber1.Join()
            from i2 in fiber2.Join()
            select $"My beautiful ints {i1} {i2}";

        public ZIO<string> Run() => ForkedZIO;
    }

    class ForkedMain : ZIOApp<string>
    {
        static ZIO<Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<int> AsyncZIO1 = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client Start");
                Thread.Sleep(2000);
                complete(new Random().Next(999));
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client End");
                return Unit();
            });

        static ZIO<int> AsyncZIO2 = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client Start");
                Thread.Sleep(5000);
                complete(new Random().Next(999));
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client End");
                return Unit();
            });

        static ZIO<string> ForkedZIO =
            from fiber1 in AsyncZIO2.Fork()
            from _ in  WriteLine($"{Thread.CurrentThread.ManagedThreadId} Nice")
            from i2 in AsyncZIO1
            from i1 in fiber1.Join()
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
                Thread.Sleep(1000);
                return complete(new Random().Next(999));
            });

        public ZIO<(int, int)> Run() => AsyncZIO.ZipPar(AsyncZIO);
    }

    class ZipParSucceed : ZIOApp<(int, int)>
    {
        static ZIO<Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<int> SyncZIO = 
            ZIO.Succeed<int>(() => 
            {
                Console.WriteLine("Sync Beginneth!");
                Thread.Sleep(200);
                return new Random().Next(999);
            });

        public ZIO<(int, int)> Run() => SyncZIO.ZipPar(SyncZIO);
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

    class AsyncStackSafetyFork : ZIOApp<Unit>
    {
        static ZIO<Unit> MyProgram = 
            ZIO.Async<Unit>(complete => 
            {
                Console.WriteLine("Howdy!");
                return complete(Unit());
            }).Fork().Repeat(10000);

        public ZIO<Unit> Run() => MyProgram;
    }

    class ConcurrencyUhOh : ZIOApp<int>
    {
        // Expectations in synchronous world

        // Thead #1
        // get the old value of 0
        // do some operation on it (0 + 1)
        // set it to the new value (1)

        // Thead #2
        // get the old value of 1
        // do some operation on it (1 + 1)
        // set it to the new value (2)

        // One possible order of execution
        
        // T1: get the old value of 0
        // T1: do some operation of it (0 + 1)
        // T2: get the old value of 0
        // T1: set it to the new value (1)
        // T2: do some operation of it (0 + 1)
        // T1: set it to the new value (1)

        // Expectations in atomic world

        // Thead #1
        // get the old value of 0
        // do some operation on it (0 + 1)
        // set it to the new value only if equal to old value (1)
        // otherwise go back to first step

        // Thead #2
        // get the old value of 1
        // do some operation on it (1 + 1)
        // set it to the new value only if equal to the old value (2)
        // otherwise go back to first step

        // T1: get the old value of 0
        // T1: do some operation of it (0 + 1)
        // T2: get the old value of 0
        // T1: set it to the new value (1) if it is 0 (what we got above)
        // T2: do some operation of it (0 + 1)
        // T1: set it to the new value (1) if it is 0 (what we got above) NO!IT WAS 1! RETRY!


        static int i = 0;

        static ZIO<Unit> Increment = 
            ZIO.Succeed<Unit>(() =>
            {
                i+=1;
                Console.WriteLine(i);
                return Unit();
            });

        // static ZIO<int> ForkedZIO =
        //     from _ in Increment.Fork().Repeat(1000)
        //     from value in ZIO.Succeed(() => i)
        //     select value;
        static ZIO<int> ForkedZIO =
            from fiber1 in Increment.Fork()
            from _ in fiber1.Join()
            from value in ZIO.Succeed(() => i)
            select value;

        public ZIO<int> Run() => ForkedZIO;
    }
}