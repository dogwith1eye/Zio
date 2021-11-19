using System;
using Unit = System.ValueTuple;
using static LaYumba.Functional.F;
using System.Threading;

namespace Zio
{
    interface ZIOApp<R, T> 
    {
        ZIO<R, T> Run();

        void Main(string[] args)
        {
            var result = this.Run().UnsafeRunSync();
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} The result was ${result}");
            Thread.Sleep(5000);
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Main after sleep");
        }
    }

    static class ZIOApp
    {
        public static ZIOApp<R, A> Upcast<R, A>(ZIOApp<R, A> app) => app;
    }

    record Person(string name, int age)
    {
        public static Person Peter = new Person("Peter", 88);
    }

    class SucceedNow : ZIOApp<Unit, Person>
    {
        ZIO<Unit, Person> PeterZIO = ZIO.SucceedNow(Person.Peter);
        public ZIO<Unit, Person> Run() => PeterZIO;
    }

    class SucceedNowUhOh : ZIOApp<Unit, int>
    {
        static Func<Unit> temp = () => 
        {
            Console.WriteLine("Howdy");
            return Unit();
        };
        // we get eagerly evaluated
        ZIO<Unit, Unit> HowdyZIO = ZIO.SucceedNow(temp());
        public ZIO<Unit, int> Run() => ZIO.SucceedNow(1);
    }

    class Succeed : ZIOApp<Unit, int>
    {
        // we not longer get eagerly evaluated
        // as we have trapped the computation in
        // a function
        ZIO<Unit, Unit> HowdyZIO = ZIO.Succeed(() => 
        {
            Console.WriteLine("Howdy");
            return Unit();
        });
        public ZIO<Unit, int> Run() => ZIO.SucceedNow(1);
    }

    class SucceedAgain : ZIOApp<Unit, Unit>
    {
        ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        public ZIO<Unit, Unit> Run() => WriteLine("fancy");
    }

    class Zip : ZIOApp<Unit, (int, string)>
    {
        ZIO<Unit, (int, string)> ZippedZIO = 
            ZIO.Succeed(() => 8).Zip(ZIO.Succeed(() => "LO"));

        public ZIO<Unit, (int, string)> Run() => ZippedZIO;
    }

    class Map : ZIOApp<Unit, Person>
    {
        static ZIO<Unit, (int, string)> ZippedZIO = 
            ZIO.Succeed(() => 8).Zip(ZIO.Succeed(() => "LO"));
        
        static ZIO<Unit, (string, int)> MappedZIO = 
            ZippedZIO.Map<(string, int)>((z) => (z.Item2, z.Item1));

        static ZIO<Unit, Person> PersonZIO = 
            ZippedZIO.Map<Person>((z) => new Person(z.Item2, z.Item1));

        public ZIO<Unit, Person> Run() => PersonZIO;
    }

    class FlatMap : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, (int, string)> ZippedZIO = 
            ZIO.Succeed(() => 8).Zip(ZIO.Succeed(() => "LO"));
        
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });
        
        static ZIO<Unit, Unit> MappedZIO = 
            ZippedZIO.FlatMap((z) => WriteLine($"My beautiful tuple {z}"));

        public ZIO<Unit, Unit> Run() => MappedZIO;
    }

    class LinqComprehension : ZIOApp<Unit, string>
    {
        static ZIO<Unit, (int, string)> ZippedZIO = 
            ZIO.Succeed(() => 8).Zip(ZIO.Succeed(() => "LO"));
        
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });
        
        static ZIO<Unit, string> MappedZIO = 
            from z in ZippedZIO
            from _ in WriteLine($"My beautiful tuple {z}")
            select "Nice";

        static ZIO<Unit, string> MappedZIORaw = 
            ZippedZIO.FlatMap(z => 
                WriteLine($"My beautiful tuple {z}")
                    .Map((_) => "Nice"));

        static ZIO<Unit, string> MappedZIORawAs = 
            ZippedZIO.FlatMap(z => 
                WriteLine($"My beautiful tuple {z}")
                    .As("Nice"));

        public ZIO<Unit, string> Run() => MappedZIORawAs;
    }

    class Async : ZIOApp<Unit, int>
    {
        // spill our guts
        static ZIO<Unit, int> AsyncZIO = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine("Async Start");
                Thread.Sleep(1000);
                complete(new Random().Next(999));
                Console.WriteLine("Async End");
                return Unit();
            });

        public ZIO<Unit, int> Run() => AsyncZIO;
    }

    class Async2 : ZIOApp<Unit, int>
    {
        // spill our guts
        static ZIO<Unit, int> AsyncZIO = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine("Async Start");
                Thread.Sleep(1000);
                complete(new Random().Next(999));
                Console.WriteLine("Async End");
                return Unit();
            });

        public ZIO<Unit, int> Run() => 
            from a in AsyncZIO
            from b in AsyncZIO
            select b;
    }

    class Forked : ZIOApp<Unit, string>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<Unit, int> AsyncZIO = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client Start");
                Thread.Sleep(2000);
                complete(new Random().Next(999));
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client End");
                return Unit();
            });

        static ZIO<Unit, int> AsyncZIO2 = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client Start");
                Thread.Sleep(3000);
                complete(new Random().Next(999));
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client End");
                return Unit();
            });

        static ZIO<Unit, string> ForkedZIO =
            from fiber1 in AsyncZIO.Fork()
            from fiber2 in AsyncZIO.Fork()
            from _ in  WriteLine($"{Thread.CurrentThread.ManagedThreadId} Nice")
            from i1 in fiber1.Join()
            from i2 in fiber2.Join()
            select $"My beautiful ints {i1} {i2}";

        public ZIO<Unit, string> Run() => ForkedZIO;
    }

    class ForkedSync : ZIOApp<Unit, string>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<Unit, int> SyncZIO = 
            ZIO.Succeed<int>(() => 
            {
                Console.WriteLine("Howdy!");
                Thread.Sleep(5000);
                Console.WriteLine("Partner!");
                return new Random().Next(999);
            });

        static ZIO<Unit, string> ForkedZIO =
            from fiber1 in SyncZIO.Fork()
            from fiber2 in SyncZIO.Fork()
            from _ in  WriteLine("Nice")
            from i1 in fiber1.Join()
            from i2 in fiber2.Join()
            select $"My beautiful ints {i1} {i2}";

        public ZIO<Unit, string> Run() => ForkedZIO;
    }

    class ForkedMain : ZIOApp<Unit, string>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<Unit, int> AsyncZIO1 = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client Start");
                Thread.Sleep(2000);
                complete(new Random().Next(999));
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client End");
                return Unit();
            });

        static ZIO<Unit, int> AsyncZIO2 = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client Start");
                Thread.Sleep(5000);
                complete(new Random().Next(999));
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Client End");
                return Unit();
            });

        static ZIO<Unit, string> ForkedZIO =
            from fiber1 in AsyncZIO2.Fork()
            from _ in  WriteLine($"{Thread.CurrentThread.ManagedThreadId} Nice")
            from i2 in AsyncZIO1
            from i1 in fiber1.Join()
            select $"My beautiful ints {i1} {i2}";

        public ZIO<Unit, string> Run() => ForkedZIO;
    }

    class ZipPar : ZIOApp<Unit, (int, int)>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<Unit, int> AsyncZIO = 
            ZIO.Async<int>((complete) => 
            {
                Console.WriteLine("Async Beginneth!");
                Thread.Sleep(1000);
                return complete(new Random().Next(999));
            });

        public ZIO<Unit, (int, int)> Run() => AsyncZIO.ZipPar(AsyncZIO);
    }

    class ZipParSucceed : ZIOApp<Unit, (int, int)>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        // spill our guts
        static ZIO<Unit, int> SyncZIO = 
            ZIO.Succeed<int>(() => 
            {
                Console.WriteLine("Sync Beginneth!");
                Thread.Sleep(200);
                return new Random().Next(999);
            });

        public ZIO<Unit, (int, int)> Run() => SyncZIO.ZipPar(SyncZIO);
    }

    class StackSafety : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> MyProgram = 
            ZIO.Succeed(() => 
            {
                Console.WriteLine("Howdy!");
                return Unit();
            }).Repeat(10000);

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    class AsyncStackSafety : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> MyProgram = 
            ZIO.Async<Unit>(complete => 
            {
                Console.WriteLine("Howdy!");
                return complete(Unit());
            }).Repeat(10000);

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    class AsyncStackSafetyFork : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> MyProgram = 
            ZIO.Async<Unit>(complete => 
            {
                Console.WriteLine("Howdy!");
                return complete(Unit());
            }).Fork().Repeat(1000);

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    class ConcurrencyUhOh : ZIOApp<Unit, int>
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

        static ZIO<Unit, Unit> Increment = 
            ZIO.Succeed<Unit>(() =>
            {
                i+=1;
                Console.WriteLine(i);
                return Unit();
            });

        // static ZIO<Unit, int> ForkedZIO =
        //     from _ in Increment.Fork().Repeat(1000)
        //     from value in ZIO.Succeed(() => i)
        //     select value;
        static ZIO<Unit, int> ForkedZIO =
            from fiber1 in Increment.Fork()
            from _ in fiber1.Join()
            from value in ZIO.Succeed(() => i)
            select value;

        public ZIO<Unit, int> Run() => ForkedZIO;
    }

    class ErrorHandling : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        static ZIO<Unit, Unit> MyProgram = 
            ZIO.Fail<Unit>(() => new Exception("Failed!"))
                .FlatMap(_ => WriteLine("Here"))
                .CatchAll(ex => WriteLine("Recovered from Error"));

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    class ErrorHandlingThrow : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        static ZIO<Unit, Unit> MyProgram = 
            ZIO.Succeed<Unit>(() => throw new Exception("Failed!"));

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    class ErrorHandlingThrowCatch : ZIOApp<Unit, int>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        static ZIO<Unit, int> MyProgram = 
            ZIO
                .Succeed<Unit>(() => throw new Exception("Failed!"))
                .CatchAll(_ => WriteLine("This should never be shown"))
                .FoldCauseZIO(
                    c => WriteLine("Recovered from a cause $c").ZipRight(ZIO.Succeed(() => 1)),
                    _ => ZIO.Succeed(() => 0)
                );

        public ZIO<Unit, int> Run() => MyProgram;
    }

    class ZipRight : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        static ZIO<Unit, Unit> MyProgram = 
            from _ in WriteLine("Howdy!")
                .ZipRight(WriteLine("From"))
                .ZipRight(WriteLine("AssociativeBoth"))
            select Unit();

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    class ZipRightRepeat : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        static ZIO<Unit, Unit> MyProgram = 
            from _ in WriteLine("Howdy!")
                .Repeat(10000)
            select Unit();

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    class Forever : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine(message);
            return Unit();
        });

        static ZIO<Unit, Unit> MyProgram = 
            from _ in WriteLine("Howdy!")
                .Forever()
            select Unit();

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    class Interruption : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} {message}");
            return Unit();
        });

        static ZIO<Unit, Unit> MyProgram = 
            from fiber in WriteLine("Howdy!")
                .Forever()
                .Ensuring(WriteLine("Goodbye"))
                .Fork()
            // from sleep in ZIO.Succeed(() => 
            // {
            //     Thread.Sleep(1000);
            //     return Unit();
            // })
            from _ in fiber.Interrupt()
            select Unit();

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    class Uninterruptible : ZIOApp<Unit, Unit>
    {
        static ZIO<Unit, Unit> WriteLine(string message) => ZIO.Succeed(() => 
        {
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} {message}");
            return Unit();
        });

        static ZIO<Unit, Unit> MyProgram = 
            from fiber in WriteLine("Howdy!")
                .Repeat(10000)
                .Uninterruptible()
                .ZipRight(WriteLine("Howdy! Howdy!").Forever())
                .Ensuring(WriteLine("Goodbye"))
                .Fork()
            // from sleep in ZIO.Succeed(() => 
            // {
            //     Thread.Sleep(100);
            //     return Unit();
            // })
            from _ in fiber.Interrupt()
            select Unit();

        public ZIO<Unit, Unit> Run() => MyProgram;
    }

    public interface IFoo
    {
        int foo() => 1;
    }
    public interface IBar
    {
        string bar() => "bar";
    }
    public class Env : IFoo, IBar {}

    class Environment : ZIOApp<int, int>
    {
        static ZIO<R, Unit> WriteLine<R>(string message) => ZIO.Succeed<R, Unit>(() => 
        {
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} {message}");
            return Unit();
        });

        static ZIO<Env, Unit> zio = 
            ZIO.AccessZIO<Env, Unit>(n => WriteLine<Env>($"{(n as IFoo).foo()}"));

        static ZIO<Env, Unit> zio2 = 
            zio.Provide(new Env());

        static ZIO<Env, Unit> zio3 = 
            ZIO.AccessZIO<Env, Unit>(n => WriteLine<Env>($"{(n as IBar).bar()}"));

        static ZIO<Env, (Unit, Unit)> zio4 = 
            zio2.Zip(zio3);

        static ZIO<int, Unit> zio5 = 
            ZIO.AccessZIO<int, Unit>(n => WriteLine<int>($"{n}"));

        static ZIO<int, int> potentiallyScary = 
            from x in ZIO.Environment<int>()
            from _ in zio5.Provide(42)
            from y in ZIO.Environment<int>()
            select x + y;

        static ZIO<int, int> potentiallyScarier = 
            from x in ZIO.Environment<int>()
            from _ in (zio5.ZipRight(ZIO.Fail<int, Unit>(() => new Exception("OH NO")))
                .Provide(42)
                .CatchAll(_ => ZIO.Succeed<int, Unit>(() => Unit())))
            from y in ZIO.Environment<int>()
            select x + y;

        public ZIO<int, int> Run() => 
            potentiallyScarier.Provide(100);
    }
}