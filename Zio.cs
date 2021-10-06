using System;
using Unit = System.ValueTuple;
using LaYumba.Functional;
using static LaYumba.Functional.F;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

namespace Zio
{
    static class ZIOWorkflow
    {
        public static ZIO<B> Select<A, B>
           (this ZIO<A> self, Func<A, B> f)
           => self.Map(f);

        public static ZIO<B> SelectMany<A, B>
           (this ZIO<A> self, Func<A, ZIO<B>> f)
           => self.FlatMap(f);

        public static ZIO<BB> SelectMany<A, B, BB>
           (this ZIO<A> self, Func<A, ZIO<B>> f, Func<A, B, BB> project)
           => self.FlatMap(a => f(a).Map(b => project(a, b)));
    }

    interface Fiber<A>
    {
        // early completion
        // late completion
        ZIO<A> Join();
        Unit Start();
    }

    class FiberImpl<A> : Fiber<A>
    {
        private ZIO<A> zio;
        private Option<A> maybeResult;

        private List<Func<A, Unit>> callbacks = new List<Func<A, Unit>>();

        public FiberImpl(ZIO<A> zio)
        {
            this.zio = zio;
        }

        public ZIO<A> Join() =>
            this.maybeResult.Match(
                ()  => ZIO.Async<A>(complete => 
                {
                    this.callbacks.Add(complete);
                    return Unit();
                }),
                (a) => ZIO.SucceedNow<A>(a)
            );

        public Unit Start()
        {
            Task.Run(() => this.zio.Run((a) => {
                this.maybeResult = Some(a);
                this.callbacks.ForEach(callback => callback(a));
                return Unit();
            }));
            return Unit();
        }
            
    }

    // Stack Safety
    // Execution Context
    // Concurrency Safety
    // Interruption
    // Error Handling
    // Environment
    // Declarative Encoding
    // ZIO<R, E, A>
    // Async
    interface ZIO<A> 
    {
        // continuation
        // callback
        // method A => Unit
        Unit RunUnsafe(Func<A, Unit> callback);
        sealed Unit Run(Func<A, Unit> callback) 
        {
            var stack = new Stack<dynamic>();
            dynamic currentZIO = this;
            var loop = true;

            Unit Complete(dynamic value)
            {
                if (stack.Count == 0)
                {
                    loop = false;
                    callback(value);
                }
                else
                {
                    var cont = stack.Pop();
                    Console.WriteLine("Pop:" + stack.Count);
                    currentZIO = cont(value);
                }
                return Unit();
            };

            Unit Resume(dynamic nextZIO)
            {
                currentZIO = nextZIO;
                loop = true;
                return DoLoop();
            };

            Unit DoLoop()
            {
                while (loop)
                {
                    switch (currentZIO.Label)
                    {
                        case "Succeed":
                            Complete(currentZIO.Value);
                            break;
                        
                        case "Effect":
                            Complete(currentZIO.Thunk());
                            break;

                        case "FlatMap":
                            stack.Push(currentZIO.Cont);
                            Console.WriteLine("Push:" + stack.Count);
                            currentZIO = currentZIO.Zio;
                            break;

                        case "Async":
                            if (stack.Count == 0)
                            {
                                loop = false;
                                currentZIO.Register(callback);
                            }
                            else
                            {
                                loop = false;
                                Func<dynamic, Unit> resume = (dyn) => Resume(dyn);
                                currentZIO.Resume(resume);
                            }
                            break;

                        case "Fork":
                            var fiber = currentZIO.CreateFiber();
                            fiber.Start();
                            Complete(fiber);
                            break;

                        default: 
                            throw new Exception("Zio case does not match");
                    };
                }
                return Unit();
            };

            return DoLoop();
        }

        ZIO<B> As<B>(B b) => 
            this.Map((_) => b);

        ZIO<B> FlatMap<B>(Func<A, ZIO<B>> f) => 
            new FlatMap<A, B>(this, f);

        // correct by construction
        ZIO<Fiber<A>> Fork() =>
            new Fork<A>(this);

        ZIO<B> Map<B>(Func<A, B> f) => 
            this.FlatMap((a) => ZIO.SucceedNow(f(a)));

        ZIO<Unit> Repeat(int n)
        {
            var count = n;
            var result = ZIO.SucceedNow(Unit());
            while (count > 0)
            {
                result = this.ZipRight<Unit>(result);
                count -= 1;
            }
            return result;
        }

        ZIO<Unit> RepeatUnsafe(int n)
        {
            if (n <= 0) return ZIO.SucceedNow(Unit());
            else return this.ZipRight(RepeatUnsafe(n - 1));
        }

        ZIO<(A, B)> Zip<B>(ZIO<B> that) => 
            ZipWith(that, (a, b) => (a, b));

        ZIO<(A, B)> ZipPar<B>(ZIO<B> that) => 
            from f in this.Fork()
            from b in that
            from a in f.Join()
            select (a, b);
        
        ZIO<B> ZipRight<B>(ZIO<B> that) => 
            ZipWith(that, (a, b) => b);

        ZIO<C> ZipWith<B, C>(ZIO<B> that, Func<A, B, C> f) => 
            from a in this
            from b in that
            select f(a, b);
    }

    class Succeed<A> : ZIO<A>
    {
        public string Label
        {
            get => "Succeed";
        }

        public A Value { get; }

        public Succeed(A value)
        {
            this.Value = value;
        }

        public Unit RunUnsafe(Func<A, Unit> callback)
            => callback(this.Value);
    }

    class Effect<A> : ZIO<A>
    {
        public string Label
        {
            get => "Effect";
        }
        public Func<A> Thunk { get; }
        public Effect(Func<A> thunk)
        {
            this.Thunk = thunk;
        }

        public Unit RunUnsafe(Func<A, Unit> callback)
            => callback(this.Thunk());
    }

    class FlatMap<A, B> : ZIO<B>
    {
        public string Label
        {
            get => "FlatMap";
        }
        public ZIO<A> Zio { get; }
        public Func<A, ZIO<B>> Cont { get; }
        public FlatMap(ZIO<A> zio, Func<A, ZIO<B>> cont)
        {
            this.Zio = zio;
            this.Cont = cont;
        }

        public Unit RunUnsafe(Func<B, Unit> callback)
        {
            return this.Zio.RunUnsafe(a => 
            {
                return this.Cont(a).RunUnsafe(callback);
            });
        }
    }

    class Async<A> : ZIO<A>
    {
        public string Label
        {
            get => "Async";
        }
        public Func<Func<A, Unit>, Unit> Register { get; }
        public Async(Func<Func<A, Unit>, Unit> register)
        {
            this.Register = register;
        }
        public Unit Resume(Func<dynamic, Unit> resume) =>
            this.Register(a =>
            {
                return resume(ZIO.SucceedNow(a));
            });

        public Unit RunUnsafe(Func<A, Unit> callback) =>
            this.Register(callback);
    }

    class Fork<A> : ZIO<Fiber<A>>
    {
        public string Label
        {
            get => "Fork";
        }
        public ZIO<A> Zio { get; }
        public Fork(ZIO<A> zio)
        {
            this.Zio = zio;
        }

        public FiberImpl<A> CreateFiber() =>
            new FiberImpl<A>(Zio);

        public Unit RunUnsafe(Func<Fiber<A>, Unit> callback)
        {
            var fiber = CreateFiber();
            fiber.Start();
            return callback(fiber);
        }
    }

    static class ZIO
    {
        public static ZIO<A> Async<A>(Func<Func<A, Unit>, Unit> register) =>
            new Async<A>(register);
        public static ZIO<A> Succeed<A>(Func<A> value) => 
            new Effect<A>(value);
        internal static ZIO<A> SucceedNow<A>(A value) => 
            new Succeed<A>(value);

    }
}